using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal sealed partial class Live2DFramePresenter
{
    private enum HandoffPhase { None, Entering, Raster, Leaving, Complete }
    private sealed record LegacyActions(string Outfit, IReadOnlyDictionary<ClipKind, IReadOnlyList<BitmapSource>> Clips);
    private readonly Image _nativeRaster = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false, Opacity = 0 };
    private LegacyActions? _legacyActions;
    private HandoffPhase _hybridPhase;
    private ClipSample _hybridSample;
    private long _neutralRequestId, _neutralRevision, _renderSerial, _terminalFrame, _nativeFrameChanges;
    private bool _neutralAcknowledged, _hybridPaused;
    private double _neutralStarted;
    private TimeSpan _lastHybridRender;
    private bool _recordingVisibleSurface;
    private int _nativeFrameIndex;
    internal bool IsNativeRasterVisible => _nativeRaster.Opacity == 1;
    internal string VisibleSurface => IsNativeRasterVisible ? "native-raster" : "live2d-core";
    internal long NativeRasterTerminalCount { get; private set; }

    internal static bool IsLegacyAction(ClipKind kind) => kind is ClipKind.Yawn or ClipKind.Eat;
    internal bool UsesNativeAction(ClipKind kind) => IsLegacyAction(kind) && _legacyActions?.Clips.ContainsKey(kind) == true;

    private void InitializeHybridLayer()
    {
        _layer.Children.Add(_nativeRaster);
        CompositionTarget.Rendering += OnHybridRendering;
    }

    private async Task<LegacyActions> PrepareLegacyActionsAsync(string outfit)
    {
        if (_legacyActions?.Outfit == outfit) return _legacyActions;
        return new(outfit, await HybridActionPack.LoadAsync(outfit));
    }

    private bool CanAdvanceHybrid(ClipSample sample)
    {
        if (_hybridPhase == HandoffPhase.None || _hybridSample.Sequence != sample.Sequence || _hybridSample.Kind != sample.Kind)
        {
            BeginHybrid(sample, _lastPaused);
            return false;
        }
        if (_hybridPhase is HandoffPhase.Entering or HandoffPhase.Leaving)
        {
            if (!_hybridPaused && _clock.Elapsed.TotalSeconds - _neutralStarted > 5)
                Fail("原帧动作与 Live2D 的中立姿态未完成交接，保留同装备逐帧画面");
            return false;
        }
        if (_hybridPhase == HandoffPhase.Complete) return true;
        if (sample.Phase != ClipPlaybackPhase.Terminal) return true;
        if (_terminalFrame > 0 && _renderSerial >= _terminalFrame + 2 && !_hybridPaused)
        {
            _hybridPhase = HandoffPhase.Leaving;
            RequestNeutral(sample, false);
        }
        return false;
    }

    private bool PresentHybrid(ClipSample sample, bool paused)
    {
        if (!UsesNativeAction(sample.Kind))
        {
            if (_hybridPhase is HandoffPhase.Entering or HandoffPhase.Leaving && paused)
            {
                UpdateNeutralPause(true);
                return true;
            }
            _hybridPhase = HandoffPhase.None;
            _nativeRaster.Opacity = 0;
            _coreSurface.Opacity = 1;
            return false;
        }
        if (_hybridPhase == HandoffPhase.None || _hybridSample.Sequence != sample.Sequence || _hybridSample.Kind != sample.Kind)
            BeginHybrid(sample, paused);
        _hybridSample = sample;
        if (_hybridPhase is HandoffPhase.Entering or HandoffPhase.Leaving) UpdateNeutralPause(paused);
        else _hybridPaused = paused;
        if (_hybridPhase == HandoffPhase.Raster)
        {
            ApplyNativeFrame(sample);
            if (sample.Phase == ClipPlaybackPhase.Terminal && _terminalFrame == 0) _terminalFrame = _renderSerial;
        }
        return true;
    }

    private void BeginHybrid(ClipSample sample, bool paused)
    {
        if (_legacyActions is null || _legacyActions.Outfit != ActiveEquipment?.Outfit || !_legacyActions.Clips.ContainsKey(sample.Kind))
        {
            Fail("原帧动作没有完成同服装预载");
            return;
        }
        _hybridSample = sample;
        _hybridPhase = HandoffPhase.Entering;
        _terminalFrame = 0;
        ApplyNativeFrame(new(sample.Kind, ClipPlaybackPhase.Playing, 0, sample.Sequence));
        RequestNeutral(sample, paused);
    }

    private void RequestNeutral(ClipSample sample, bool paused)
    {
        _neutralRequestId++;
        _neutralRevision = ++_revision;
        _neutralAcknowledged = false;
        _hybridPaused = paused;
        _neutralStarted = _clock.Elapsed.TotalSeconds;
        PostNeutral(sample);
    }

    private void UpdateNeutralPause(bool paused)
    {
        if (_hybridPaused == paused) return;
        _hybridPaused = paused;
        _neutralStarted = _clock.Elapsed.TotalSeconds;
        PostNeutral(_hybridSample);
    }

    private void PostNeutral(ClipSample sample) => Post(new
    {
        type = "neutralize", protocol = 1, generation = _acknowledgements.Generation,
        requestId = _neutralRequestId, revision = _neutralRevision, sequence = sample.Sequence,
        kind = sample.Kind.ToString(), equipment = ActiveEquipment, paused = _hybridPaused,
    });

    private void ReceiveNeutralized(JsonElement message)
    {
        if (_hybridPhase is not (HandoffPhase.Entering or HandoffPhase.Leaving) ||
            ActiveEquipment is not { } equipment ||
            !NeutralHandoffReceipt.Matches(message, _acknowledgements.Generation, _neutralRequestId, _neutralRevision,
                _hybridSample, equipment.Desk, equipment.Computer, equipment.Outfit)) return;
        _neutralAcknowledged = true;
    }

    private void OnHybridRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || frame.RenderingTime == _lastHybridRender) return;
        _lastHybridRender = frame.RenderingTime;
        _renderSerial++;
        if (_disposed || !IsActive || !_neutralAcknowledged || _hybridPaused) return;
        if (_hybridPhase == HandoffPhase.Entering)
        {
            _nativeRaster.Opacity = 1;
            _coreSurface.Opacity = 0;
            _hybridPhase = HandoffPhase.Raster;
            SendRecordingSurface();
        }
        else if (_hybridPhase == HandoffPhase.Leaving)
        {
            _nativeRaster.Opacity = 0;
            _coreSurface.Opacity = 1;
            _hybridPhase = HandoffPhase.Complete;
            NativeRasterTerminalCount++;
            PresentedTerminalCount++;
            SendRecordingSurface();
        }
    }

    private void ApplyNativeFrame(ClipSample sample)
    {
        var frames = _legacyActions!.Clips[sample.Kind];
        int index = Math.Clamp((int)Math.Round(sample.Progress * (frames.Count - 1)), 0, frames.Count - 1);
        if (ReferenceEquals(_nativeRaster.Source, frames[index])) return;
        _nativeRaster.Source = frames[index];
        _nativeFrameIndex = index;
        _nativeFrameChanges++;
        if (IsNativeRasterVisible) SendRecordingSurface();
    }

    private void SendRecordingSurface()
    {
        if (!_recordingVisibleSurface) return;
        Post(new { type = "record-raster-frame", protocol = 1, generation = _acknowledgements.Generation,
            key = IsNativeRasterVisible ? _hybridSample.Kind.ToString() : null, index = _nativeFrameIndex });
    }

    private async Task PrepareVisibleRecordingAsync()
    {
        var sources = _legacyActions!.Clips.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.Select(frame =>
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
            using var stream = new MemoryStream(); encoder.Save(stream);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }).ToArray());
        await _view.CoreWebView2.ExecuteScriptAsync("window.__eagleRasterReady=null;window.eagleStage.prepareRasterRecording(" +
            JsonSerializer.Serialize(sources) + ").then(()=>{window.__eagleRasterReady={ready:true}}).catch(error=>{window.__eagleRasterReady={error:String(error)}})");
        for (int attempt = 0; attempt < 150; attempt++)
        {
            await Task.Delay(100);
            using var result = JsonDocument.Parse(await _view.CoreWebView2.ExecuteScriptAsync("window.__eagleRasterReady"));
            if (result.RootElement.ValueKind == JsonValueKind.Null) continue;
            if (result.RootElement.TryGetProperty("error", out var error)) throw new InvalidDataException(error.GetString());
            _recordingVisibleSurface = true;
            SendRecordingSurface();
            return;
        }
        throw new TimeoutException("Visible raster recording source preparation did not finish");
    }

    private Task CaptureNativeRasterAsync(string path)
    {
        var source = (BitmapSource)_nativeRaster.Source;
        double width = 768, height = 692, scale = Math.Min(width / source.PixelWidth, height / source.PixelHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(source, new Rect((width - source.PixelWidth * scale) / 2, (height - source.PixelHeight * scale) / 2,
                source.PixelWidth * scale, source.PixelHeight * scale));
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return File.WriteAllBytesAsync(path, stream.ToArray());
    }

    private void DisposeHybridLayer()
    {
        CompositionTarget.Rendering -= OnHybridRendering;
        _nativeRaster.Source = null;
        _legacyActions = null;
    }
}
