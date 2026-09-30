using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>Own visual tree, decoded frames and clock. Never receives care, wallet or ownership services.</summary>
internal sealed class ContentPreviewWindow : Window
{
    private readonly ContentDefinition _item;
    private readonly SceneSelection _selection;
    private readonly Func<ContentDefinition, bool> _available;
    private readonly Image _pet;
    private readonly WorkStageRenderer _stage;
    private readonly TextBlock _status;
    private readonly Button _pause;
    private readonly PreviewPresentationFactory? _preparePresentation;
    private readonly Grid _presentationSurface = new() { Width = 256, Height = 232, IsHitTestVisible = false };
    private readonly Image[] _rasterLayers;
    private IPetPresentationBackend? _presentation;
    private string? _presentationWarning;
    private readonly CancellationTokenSource _loading = new();
    private readonly Stopwatch _clock = new();
    private IReadOnlyList<BitmapSource>? _frames;
    private BitmapSource? _neutral;
    private ClipKind _clip = ClipKind.Idle;
    private double _duration;
    private double _previousClockSeconds, _elapsed;
    private ClipSample _sample = ClipSample.Idle;
    private bool _scene, _ready, _closed, _subscribed, _paused;

    internal ContentPreviewWindow(ContentDefinition item, SceneSelection selection, Func<ContentDefinition, bool> available,
        PreviewPresentationFactory? preparePresentation = null)
    {
        _item = item; _selection = selection; _available = available;
        _preparePresentation = preparePresentation;
        Title = "大头鹰 · " + item.Name + "预览";
        Width = 400; Height = 485; MinWidth = 350; MinHeight = 390;
        Background = PreviewStageChrome.Paper; Foreground = PreviewStageChrome.Ink;
        FontFamily = new FontFamily("Microsoft YaHei UI"); ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Themes/PetTheme.xaml") });
        var root = new Grid { Margin = new Thickness(17), Background = Background };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(PreviewStageChrome.Ticket(item.Name, "私享试映 · 不扣款、不领奖、不换装", "PREVIEW"));
        var scene = new Grid { Width = 290, Height = 265, ClipToBounds = true, Background = Brushes.Transparent };
        var fire = Layer(); var back = Layer(); _pet = Layer(); var front = Layer(); var computer = Layer();
        foreach (var image in new[] { fire, back, _pet, front, computer }) scene.Children.Add(image);
        // A short/narrow window scales this self-contained stage instead of cropping feet or controls.
        var viewport = PreviewStageChrome.Stage(scene);
        Grid.SetRow(viewport, 1); root.Children.Add(viewport);
        _stage = new WorkStageRenderer(back, front, computer, fire, character: _pet);
        _rasterLayers = [fire, back, _pet, front, computer, _stage.ForegroundLayer!];
        Panel.SetZIndex(_presentationSurface, 10); scene.Children.Add(_presentationSurface);
        var footer = new StackPanel { Margin = new(0, 12, 0, 0) };
        _status = new TextBlock { Text = "正在准备预览…", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = PreviewStageChrome.Muted, Margin = new(0, 0, 0, 10) };
        footer.Children.Add(_status);
        var controls = new DockPanel();
        var close = new Button { Content = "看完啦", Style = (Style)FindResource("PetPrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); controls.Children.Add(close);
        _pause = new Button { Content = "暂停预览", Style = (Style)FindResource("PetButton"), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
        _pause.Click += (_, _) => { _paused = !_paused; _pause.Content = _paused ? "继续预览" : "暂停预览"; UpdateClock(); };
        controls.Children.Add(_pause); footer.Children.Add(controls); Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;
        Activated += (_, _) => UpdateClock(); Deactivated += (_, _) => UpdateClock(); IsVisibleChanged += (_, _) => UpdateClock();
        StateChanged += (_, _) => UpdateClock();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Closed += (_, _) =>
        {
            _closed = true; _loading.Cancel(); UpdateClock(); _frames = null; _neutral = null; _pet.Source = null;
            _stage.ReleaseFireFrames(); _loading.Dispose();
            _presentation?.Dispose();
        };
    }

    internal async Task PrepareAsync()
    {
        if (_ready || _closed) return;
        try
        {
            if (!_available(_item)) throw new InvalidDataException("Content resources are not available.");
            CancellationToken token = _loading.Token;
            if (_item.Type == ContentType.Outfit)
            {
                _clip = ClipKind.Yawn;
                var catalog = OutfitCatalog.Load();
                _frames = await RasterFramePlayer.LoadOutfitPreviewFramesAsync(catalog, _item.OutfitId!, _clip, token);
                _neutral = (await RasterFramePlayer.LoadOutfitPreviewFramesAsync(catalog, _item.OutfitId!, ClipKind.Idle, token))[0];
                _duration = ClipCatalog.GetDefinition(_clip).DurationSeconds;
            }
            else
            {
                var assets = AnimationAssets.Load();
                _scene = _item.Type is ContentType.Desk or ContentType.Computer;
                var action = assets.Actions.FirstOrDefault(x => _scene ? x.Clip == nameof(ClipKind.WorkLoop) : x.Id == _item.AnimationId)
                    ?? throw new InvalidDataException("Missing content preview animation.");
                _clip = Enum.Parse<ClipKind>(action.Clip); _duration = action.DurationSeconds;
                var frames = await Task.Run(() => Enumerable.Range(0, action.FrameCount).Select(i =>
                {
                    token.ThrowIfCancellationRequested();
                    return RasterFramePlayer.LoadBitmap($"{action.Directory}/frame-{i:0000}.png");
                }).ToArray(), token);
                _frames = frames;
                _neutral = RasterFramePlayer.LoadBitmap(assets.Neutral);
                if (_scene)
                {
                    var selected = _item.Type == ContentType.Desk ? _selection with { DeskId = _item.ScenePropId! }
                        : _selection with { ComputerId = _item.ScenePropId! };
                    await _stage.RequestSelectionAsync(selected);
                    _stage.Apply(ClipSample.Idle);
                }
            }
            if (_closed) { _frames = null; _neutral = null; return; }
            _sample = new(_clip, ClipPlaybackPhase.Playing, 0, 1);
            if (_preparePresentation is not null)
            {
                try
                {
                    _presentation = await _preparePresentation(_presentationSurface,
                        new(_stage.ActiveSelection.DeskId, _stage.ActiveSelection.ComputerId,
                            _item.Type == ContentType.Outfit ? _item.OutfitId! : OutfitCatalog.DefaultId), _sample, token);
                    if (_closed) { _presentation?.Dispose(); return; }
                    if (_presentation is not null) _presentation.PresentationChanged += UpdatePresentation;
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException)) { _presentationWarning = "Live2D 预览暂不可用，使用相同装备的逐帧预览。"; }
            }
            _ready = true; _pause.IsEnabled = true;
            _status.Text = _scene ? "持续办公的实际图层预览；关闭后桌宠保持原样。" : "完整动作后站立 2 秒，再从头预览。";
            UpdatePresentation();
            RenderFrame(); UpdateClock();
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closed) _status.Text = "素材暂未完整加载。未扣款，也没有改变桌宠或收藏。";
            throw;
        }
    }

    private void UpdateClock()
    {
        bool run = _ready && !_closed && !_paused && IsVisible && IsActive && WindowState != WindowState.Minimized;
        bool changed = run != _subscribed;
        if (run && !_subscribed) { _clock.Start(); CompositionTarget.Rendering += OnRendering; _subscribed = true; }
        else if (!run && _subscribed) { _clock.Stop(); CompositionTarget.Rendering -= OnRendering; _subscribed = false; }
        if (changed) _presentation?.Present(_sample, paused: !run, force: true);
    }
    internal bool IsLive2D => _presentation?.IsActive == true;
    private void UpdatePresentation()
    {
        foreach (var image in _rasterLayers) image.Opacity = IsLive2D ? 0 : 1;
        if ((_presentation?.Warning ?? _presentationWarning) is { } warning) _status.Text = warning;
    }
    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double delta = Math.Min(ClipTimeline.MaximumDeltaSeconds, Math.Max(0, now - _previousClockSeconds));
        _previousClockSeconds = now;
        if (_presentation?.CanAdvance(_sample) != false)
        {
            if (_sample.Phase == ClipPlaybackPhase.Terminal)
            {
                _elapsed = 0;
                _sample = _scene ? new(_clip, ClipPlaybackPhase.Playing, 0, _sample.Sequence + 1)
                    : new(ClipKind.Idle, ClipPlaybackPhase.Idle, 0, _sample.Sequence);
            }
            else if (_sample.Kind == ClipKind.Idle)
            {
                _elapsed += delta;
                if (_elapsed >= 2) { _elapsed = 0; _sample = new(_clip, ClipPlaybackPhase.Playing, 0, _sample.Sequence + 1); }
            }
            else
            {
                _elapsed = Math.Min(_duration, _elapsed + delta);
                double progress = _elapsed / _duration;
                _sample = new(_clip, progress >= 1 ? ClipPlaybackPhase.Terminal : ClipPlaybackPhase.Playing, progress, _sample.Sequence);
            }
        }
        RenderFrame();
    }
    private void RenderFrame()
    {
        if (_frames is not { Count: > 0 }) return;
        _pet.Source = _sample.Kind == ClipKind.Idle ? _neutral :
            _frames[Math.Clamp((int)Math.Round(_sample.Progress * (_frames.Count - 1)), 0, _frames.Count - 1)];
        _stage.Apply(_scene ? _sample : ClipSample.Idle);
        _presentation?.Present(_sample, paused: !_subscribed);
    }
    private static Image Layer() => new() { Width = 256, Height = 232, Stretch = Stretch.Uniform, IsHitTestVisible = false };
}
