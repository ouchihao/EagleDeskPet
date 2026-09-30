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
            sample.Kind == ClipKind.Idle && !_behavior.IsPaused && !_nativeDragInProgress &&
            !PetMenu.IsOpen && !InteractionsUnavailable && !_isClosing && IsVisible;
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
}
