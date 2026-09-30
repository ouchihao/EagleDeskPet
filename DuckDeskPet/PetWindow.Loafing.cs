using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private readonly LoafingVisualTimeline _loafingVisual = new();
    private LoafingSpriteLayer? _loafingLayer;
    private bool _loafingWasVisible;
    private long _loafingLastTick;

    private async Task InitializeLoafingAsync()
    {
        var assets = await Task.Run(() => new LoafingAssets());
        if (_isClosing) return;
        _loafingLayer = new(assets) { Width = 160, Height = 174, IsHitTestVisible = false,
            Visibility = Visibility.Collapsed, ClipToBounds = true };
        Panel.SetZIndex(_loafingLayer, 11);
        PetVisual.Children.Add(_loafingLayer);
    }

    private bool TickLoafingVisual()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = _loafingLastTick == 0 ? 0 : Stopwatch.GetElapsedTime(_loafingLastTick, now).TotalSeconds;
        _loafingLastTick = now;
        if (_loafingLayer is null) return false;
        var previous = _loafingVisual.Phase;
        _loafingVisual.Advance(elapsed, CareState.Loafing,
            _behavior.CurrentSample.Kind is ClipKind.WorkLoop or ClipKind.BusyLoop, _behavior.IsPaused);
        if (previous == LoafingPhase.None && _loafingVisual.Active)
            Say(_loafingVisual.Kind == LoafingKind.WoodenFish ? "暂停打工，先给 KPI 超度半小时。" : "电脑先歇着，我研究一下鼻生。半小时后回来。");
        if (previous == LoafingPhase.Loop && _loafingVisual.Phase == LoafingPhase.Return && CareState.IsWorking)
            Say("摸鱼结束，电脑归位，鹰继续打工。");
        return _loafingVisual.Active;
    }

    private void ApplyLoafingVisual()
    {
        bool active = _loafingVisual.Active;
        _live2D?.SendLoafing(_loafingVisual.ProtocolPhase, _loafingVisual.Seconds,
            _behavior.IsPaused, _loafingVisual.Kind.ToString());
        if (_loafingLayer is not null)
            _loafingLayer.Present(_loafingVisual, _live2D?.IsActive != true, PetImage.Source as BitmapSource,
                DeskBackImage.Source as BitmapSource, DeskFrontImage.Source as BitmapSource, LaptopImage.Source as BitmapSource);
        if (active)
        {
            ApplyPose(Pose.Neutral);
            foreach (var layer in new[] { PetImage, BusyFireImage, DeskBackImage, DeskFrontImage, LaptopImage, _workStage.ForegroundLayer })
                if (layer is not null) layer.Opacity = 0;
        }
        else if (_loafingWasVisible) OnPresentationChanged();
        _loafingWasVisible = active;
    }
}
