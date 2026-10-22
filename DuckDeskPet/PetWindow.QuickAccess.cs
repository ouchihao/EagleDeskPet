using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DuckDeskPet.Controls;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private readonly QuickAccessPolicy _quickAccessPolicy = new();
    private readonly TranslateTransform _quickAccessOffset = new();
    private DispatcherTimer? _quickAccessTimer;
    private Popup? _quickAccessPopup;
    private QuickAccessBar? _quickAccessBar;
    private bool _quickAccessRevealed;

    private void StartQuickAccess()
    {
        _quickAccessBar = new QuickAccessBar { RenderTransform = _quickAccessOffset };
        _quickAccessBar.ActionRequested += OpenQuickAccessDestination;
        _quickAccessBar.PreviewKeyDown += QuickAccess_OnKeyDown;
        _quickAccessPopup = new Popup
        {
            Child = _quickAccessBar,
            PlacementTarget = PetVisual,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = QuickAccessPlacements,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false
        };
        _quickAccessTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _quickAccessTimer.Tick += QuickAccess_OnTick;
        LocationChanged += QuickAccess_OnOwnerMoved;
        SizeChanged += QuickAccess_OnOwnerMoved;
        _quickAccessTimer.Start();
    }

    private static CustomPopupPlacement[] QuickAccessPlacements(Size popup, Size target, Point offset)
    {
        // Both renderers use the 384 x 346 scene; ignore its transparent head/foot padding
        double scale = Math.Min(target.Width / 384, target.Height / 346);
        double letterbox = (target.Height - 346 * scale) / 2;
        double x = (target.Width - popup.Width) / 2;
        return
        [
            new(new Point(x, letterbox + 84 * scale - popup.Height + 2), PopupPrimaryAxis.Horizontal),
            new(new Point(x, letterbox + 336 * scale - 2), PopupPrimaryAxis.Horizontal)
        ];
    }

    private void QuickAccess_OnTick(object? sender, EventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            HideQuickAccess();
            return;
        }
        UpdateQuickAccess(new Point(cursor.X, cursor.Y), Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    private void UpdateQuickAccess(Point screenPoint, double seconds)
    {
        bool blocked = _isClosing || !IsVisible || Opacity == 0 || !_assetsReady ||
            _nativeDragInProgress || PetMenu.IsOpen ||
            (Mouse.LeftButton == MouseButtonState.Pressed &&
             !(_quickAccessBar?.IsMouseOver == true || _quickAccessBar?.IsMouseCaptureWithin == true));
        bool nearBar = !blocked && _quickAccessPopup?.IsOpen == true &&
            (IsNearQuickAccessElement(_quickAccessBar!, screenPoint, 8) || _quickAccessBar!.IsKeyboardFocusWithin);
        if (!blocked && !nearBar)
        {
            // Probe the opaque middle of the eagle, not its transparent approach halo
            Point center = PetVisual.PointToScreen(new Point(PetVisual.ActualWidth / 2, PetVisual.ActualHeight / 2));
            nint coveringWindow = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = (int)center.X, Y = (int)center.Y });
            blocked = NativeMethods.GetAncestor(coveringWindow, 2 /* GA_ROOT */) != _windowHandle;
        }
        bool nearPet = !blocked && IsNearQuickAccessElement(PetVisual, screenPoint, 12);
        // The fading popup remains a real target, so returning can reverse its exit
        SetQuickAccessVisible(_quickAccessPolicy.Update(seconds, nearPet, nearBar, blocked,
                barStillVisible: _quickAccessPopup?.IsOpen == true),
            animate: !blocked && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast);
    }

    private static bool IsNearQuickAccessElement(FrameworkElement element, Point screenPoint, double margin)
    {
        if (PresentationSource.FromVisual(element) is null) return false;
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        bounds.Inflate(margin, margin);
        return bounds.Contains(element.PointFromScreen(screenPoint));
    }

    private void SetQuickAccessVisible(bool visible, bool animate)
    {
        if (_quickAccessBar is null || _quickAccessPopup is null) return;
        if (_quickAccessRevealed == visible && (animate || visible || !_quickAccessPopup.IsOpen)) return;
        _quickAccessRevealed = visible;
        _quickAccessBar.IsHitTestVisible = visible || (animate && _quickAccessPopup.IsOpen);
        if (visible && !_quickAccessPopup.IsOpen)
        {
            _quickAccessBar.Opacity = 0;
            _quickAccessOffset.Y = 4;
            _quickAccessPopup.IsOpen = true;
        }
        if (!animate)
        {
            _quickAccessBar.BeginAnimation(OpacityProperty, null);
            _quickAccessOffset.BeginAnimation(TranslateTransform.YProperty, null);
            _quickAccessBar.Opacity = visible ? 1 : 0;
            _quickAccessOffset.Y = 0;
            _quickAccessPopup.IsOpen = visible;
            return;
        }
        var duration = TimeSpan.FromMilliseconds(visible ? 180 : 140);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacity = new DoubleAnimation(_quickAccessBar.Opacity, visible ? 1 : 0, duration) { EasingFunction = easing };
        if (!visible)
            opacity.Completed += (_, _) =>
            {
                if (_quickAccessRevealed) return;
                _quickAccessBar.IsHitTestVisible = false;
                _quickAccessPopup.IsOpen = false;
            };
        _quickAccessBar.BeginAnimation(OpacityProperty, opacity, HandoffBehavior.SnapshotAndReplace);
        _quickAccessOffset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(_quickAccessOffset.Y, visible ? 0 : 3, duration) { EasingFunction = easing },
            HandoffBehavior.SnapshotAndReplace);
    }

    private void HideQuickAccess(bool dismissUntilLeave = false)
    {
        _quickAccessPolicy.Reset(dismissUntilLeave);
        SetQuickAccessVisible(false, animate: false);
    }

    private void QuickAccess_OnOwnerMoved(object? sender, EventArgs e) => HideQuickAccess();

    private void QuickAccess_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        HideQuickAccess(dismissUntilLeave: true);
        e.Handled = true;
    }

    private void OpenQuickAccessDestination(QuickAccessDestination destination)
    {
        HideQuickAccess(dismissUntilLeave: true);
        switch (destination)
        {
            case QuickAccessDestination.Shop: OpenShopWindow(); break;
            case QuickAccessDestination.Notebook: OpenNotebookWindow(); break;
            case QuickAccessDestination.Timer: OpenReminderWindow(); break;
            case QuickAccessDestination.Home: OpenCarePanel(); break;
            case QuickAccessDestination.Honors: OpenHonorWall(); break;
        }
    }

    private void StopQuickAccess()
    {
        _quickAccessTimer?.Stop();
        if (_quickAccessTimer is not null) _quickAccessTimer.Tick -= QuickAccess_OnTick;
        LocationChanged -= QuickAccess_OnOwnerMoved;
        SizeChanged -= QuickAccess_OnOwnerMoved;
        HideQuickAccess();
        if (_quickAccessBar is not null)
        {
            _quickAccessBar.ActionRequested -= OpenQuickAccessDestination;
            _quickAccessBar.PreviewKeyDown -= QuickAccess_OnKeyDown;
        }
    }
}
