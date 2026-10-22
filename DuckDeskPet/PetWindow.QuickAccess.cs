using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    private BitmapSource? _quickAccessAnchorSource;
    private double _quickAccessArtTop;
    private double _quickAccessArtBottom;

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

    private CustomPopupPlacement[] QuickAccessPlacements(Size popup, Size target, Point offset)
    {
        BitmapSource art = _framePlayer.NeutralFrame;
        double scale = Math.Min(target.Width / art.PixelWidth, target.Height / art.PixelHeight);
        double letterbox = (target.Height - art.PixelHeight * scale) / 2;
        double x = (target.Width - popup.Width) / 2;
        return
        [
            new(new Point(x, letterbox + _quickAccessArtTop * scale - popup.Height + 2), PopupPrimaryAxis.Horizontal),
            new(new Point(x, letterbox + _quickAccessArtBottom * scale - 2), PopupPrimaryAxis.Horizontal)
        ];
    }

    private void UpdateQuickAccessAnchor()
    {
        BitmapSource art = _framePlayer.NeutralFrame;
        if (ReferenceEquals(art, _quickAccessAnchorSource)) return;
        (_quickAccessArtTop, _quickAccessArtBottom) = MeasureQuickAccessArt(art);
        _quickAccessAnchorSource = art;
    }

    private static (double Top, double Bottom) MeasureQuickAccessArt(BitmapSource art)
    {
        BitmapSource pixels = art.Format == PixelFormats.Bgra32 ? art :
            new FormatConvertedBitmap(art, PixelFormats.Bgra32, null, 0);
        int stride = pixels.PixelWidth * 4;
        var data = new byte[stride * pixels.PixelHeight];
        pixels.CopyPixels(data, stride, 0);
        int top = pixels.PixelHeight, bottom = 0;
        for (int y = 0; y < pixels.PixelHeight; y++)
            for (int x = 0; x < pixels.PixelWidth; x++)
                if (data[y * stride + x * 4 + 3] > 40)
                {
                    top = Math.Min(top, y);
                    bottom = y + 1;
                    break;
                }
        // Cache the composed neutral outfit once, including horns/helmets, so animation cannot jitter the toolbar
        return bottom > top ? (top, bottom) : (0, pixels.PixelHeight);
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
            UpdateQuickAccessAnchor();
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
