using System.Diagnostics;
using System.Windows;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private long _lastGazeTick;
    private bool _lastGazeAllowed;

    private void TickGaze(ClipSample sample)
    {
        if (_live2D?.IsActive != true) return;
        bool allowed = _settings.FollowCursor && SystemParameters.ClientAreaAnimation &&
            GazePolicy.Supports(sample.Kind) && !_behavior.IsPaused && !_nativeDragInProgress && !_loafingVisual.Active &&
            !PetMenu.IsOpen && _assetsReady && !IsFeeding && !IsContentEquipmentApplying &&
            !_preparingReaction && !_preparingOwnedAction && !_isClosing && IsVisible;
        long now = Stopwatch.GetTimestamp();
        if (allowed == _lastGazeAllowed && _lastGazeTick != 0 && Stopwatch.GetElapsedTime(_lastGazeTick, now).TotalMilliseconds < 50)
            return;
        _lastGazeTick = now;
        _lastGazeAllowed = allowed;
        GazeTarget target = GazeTarget.Neutral;
        if (allowed && NativeMethods.GetCursorPos(out var cursor))
        {
            // WPF owns the physical-screen to local-DIP conversion, including negative multi-monitor coordinates
            Point local = PetImage.PointFromScreen(new Point(cursor.X, cursor.Y));
            target = GazePolicy.Resolve(local.X, local.Y, PetImage.ActualWidth, PetImage.ActualHeight);
        }
        _live2D.SendGaze(target);
    }

    private void GazeMenu_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.FollowCursor = GazeMenuItem.IsChecked;
        SaveCurrentSettings();
        TickGaze(_behavior.CurrentSample);
    }

    private void RefreshGazeMenu()
    {
        GazeMenuItem.IsChecked = _settings.FollowCursor;
        GazeMenuItem.IsEnabled = _live2D?.IsActive == true;
        GazeMenuItem.ToolTip = _live2D?.IsActive != true
            ? "当前是逐帧兼容模式，需启用 Live2D 才能独立转动头眼"
            : !SystemParameters.ClientAreaAnimation ? "Windows 已关闭界面动画，跟随暂时停用"
            : "站立、办公、忙碌时跟随整个桌面的鼠标，不限制距离；进出场、互动、摸鱼、拖动和菜单展开时暂时让出控制";
    }
}
