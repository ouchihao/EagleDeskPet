using DuckDeskPet.Core;
using System.Windows.Controls;

namespace DuckDeskPet;

public partial class PetWindow
{
    private Live2DFramePresenter? _live2D;
    private string? _presentationWarning;
    internal string? PresentationWarning => _live2D?.Warning ?? _presentationWarning;

    private PresentationEquipment RasterEquipment => new(_workStage.ActiveSelection.DeskId,
        _workStage.ActiveSelection.ComputerId, _framePlayer.CurrentOutfitId);

    private async Task InitializeLive2DAsync()
    {
        try
        {
            var runtime = Live2DLocalRuntime.Find();
            if (runtime is null)
            {
                _presentationWarning = "未找到本机 Live2D 配置，或 EAGLE_PET_RENDERER=png 明确选择了兼容渲染";
                return;
            }
            _live2D = new(PetVisual);
            _live2D.PresentationChanged += OnPresentationChanged;
            await _live2D.InitializeAsync(runtime, RasterEquipment, _behavior.CurrentSample, _behavior.IsPaused);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _live2D?.Dispose();
            _presentationWarning = "Live2D 本机资源未准备好，继续使用原装备的逐帧动画。" + ex.Message;
            CareStatus = _presentationWarning;
        }
    }

    private void ApplyPresentation(ClipSample sample, double deltaSeconds, bool force = false)
    {
        // Keep the complete matching raster set warm for a synchronous failure handoff
        _framePlayer.Apply(sample);
        _workStage.Apply(sample, deltaSeconds);
        ApplyPose(_live2D?.IsActive == true ? Pose.Neutral : _behavior.CurrentProceduralPose);
        _live2D?.Present(sample, _behavior.IsPaused, force);
    }

    private void OnPresentationChanged()
    {
        bool live = _live2D?.IsActive == true;
        double opacity = live ? 0 : 1;
        foreach (var image in new[] { PetImage, BusyFireImage, DeskBackImage, DeskFrontImage, LaptopImage, _workStage.ForegroundLayer })
            if (image is not null) image.Opacity = opacity;
        ApplyPose(live ? Pose.Neutral : _behavior.CurrentProceduralPose);
        RendererStatusMenuItem.Header = live ? "动画 · Live2D 全动作" : "动画 · 逐帧兼容模式";
        if (!live && _live2D?.Warning is { } warning)
        {
            CareStatus = warning;
            RendererStatusMenuItem.ToolTip = warning;
            if (_assetsReady) Say(warning);
        }
        SynchronizeEquipmentBonuses();
    }

    private async Task ApplyLive2DEquipmentAsync()
    {
        if (_live2D?.IsActive == true)
            await _live2D.SelectEquipmentAsync(RasterEquipment, _behavior.CurrentSample, _behavior.IsPaused);
    }

    private static async Task<IPetPresentationBackend?> CreatePreviewPresentationAsync(Grid surface,
        PresentationEquipment equipment, ClipSample sample, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = Live2DLocalRuntime.Find();
        if (runtime is null) return null;
        var presenter = new Live2DFramePresenter(surface);
        using var registration = cancellationToken.Register(() =>
        {
            if (surface.Dispatcher.CheckAccess()) presenter.Dispose();
            else surface.Dispatcher.BeginInvoke(new Action(presenter.Dispose));
        });
        try
        {
            await presenter.InitializeAsync(runtime, equipment, sample, paused: true);
            cancellationToken.ThrowIfCancellationRequested();
            return presenter;
        }
        catch { presenter.Dispose(); throw; }
    }
}
