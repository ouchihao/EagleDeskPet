using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow : Window
{
    private const double BaseWindowWidth = 200.0;
    private const double BaseWindowHeight = 222.0;
    private const double BottomRightMarginDip = 16.0;
    private const double MaximumClickDurationMilliseconds = 500.0;

    private readonly PetBehaviorController _behavior = new();
    private readonly FrameRateGate _frameRateGate = new();
    private readonly PetSettings _settings;
    private readonly RasterFramePlayer _framePlayer;
    private readonly WorkStageRenderer _workStage;

    private HwndSource? _windowSource;
    private nint _windowHandle;
    private bool _isRenderingSubscribed;
    private bool _nativeDragInProgress;
    private bool _nativeDragObservedMovement;
    private bool _isClosing;
    private bool _pauseChanging;
    private nint _pendingBottomRightMonitor;
    private NativeMethods.Rect _nativeDragWindowOrigin;
    private double _fpsWindowStartSeconds;
    private int _fpsFrameCount;

    public PetWindow()
    {
        InitializeComponent();
        Closing += OnClosing;

        _framePlayer = new RasterFramePlayer(PetImage);
        _workStage = new WorkStageRenderer(DeskBackImage, DeskFrontImage, LaptopImage, BusyFireImage, character: PetImage);
        _settings = PetSettings.Load();
        ApplySize(_settings.SizeScale, persist: false);
        Topmost = _settings.IsTopmost;
        FpsBadge.Visibility = _settings.ShowFps ? Visibility.Visible : Visibility.Collapsed;
        _behavior.SetPaused(_settings.IsPaused);
        InitializeCare();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowMessageHook);

        nint extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLongPtr(
            _windowHandle,
            NativeMethods.GwlExStyle,
            extendedStyle | (nint)NativeMethods.WsExToolWindow);

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceWindowFromSettings);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartCare();
        try
        {
            await _framePlayer.WarmAsync();
            await InitializeContentResourcesAsync();
            await InitializeContent();
            if (CareState.IsWorking)
            {
                await _framePlayer.WarmWorkAsync();
                await _workStage.WarmFireAsync();
                if (!CareState.IsWorking)
                {
                    _framePlayer.ReleaseWorkFrames();
                    _workStage.ReleaseFireFrames();
                }
            }
            if (_isClosing) return;
            _assetsReady = true;
            _behavior.SetWorkState(CareState.IsWorking, CareState.IsBusy);
            SubscribeRendering();
            _ = RestoreGitHubAsync();
            await RunSmokeTestIfRequestedAsync();
        }
        catch (Exception exception)
        {
            if (_isClosing) return;
            CareStatus = "动画资源未能加载：" + exception.Message;
            Say(CareStatus);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_clientSetupWindow?.IsWriting == true)
        {
            e.Cancel = true;
            CareStatus = "AI 配置正在备份和保存，请完成后再退出。";
            _clientSetupWindow.Activate();
            return;
        }
        if (!TryCloseGameForPetExit()) e.Cancel = true;
        if (!TryCloseFeedingForPetExit()) e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        bool shouldShutdown = !_isClosing;
        _isClosing = true;
        UnsubscribeRendering();
        StopCare();
        _framePlayer.Dispose();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        SaveCurrentSettings();

        if (shouldShutdown)
        {
            _isClosing = true;
            Application.Current.Shutdown();
        }
    }

    private void SubscribeRendering()
    {
        if (_isRenderingSubscribed)
        {
            return;
        }

        _frameRateGate.Reset();
        CompositionTarget.Rendering += OnRendering;
        _isRenderingSubscribed = true;
    }

    private void UnsubscribeRendering()
    {
        if (!_isRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _isRenderingSubscribed = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs renderingEventArgs)
        {
            return;
        }

        double compositorSeconds = renderingEventArgs.RenderingTime.TotalSeconds;
        if (!_frameRateGate.TryAdvance(compositorSeconds, out double deltaSeconds))
        {
            return;
        }

        bool wasWorking = _behavior.IsWorkSceneActive;
        ClipSample sample = _behavior.Advance(deltaSeconds);
        ApplyPose(_behavior.CurrentProceduralPose);
        _framePlayer.Apply(sample);
        _workStage.Apply(sample, deltaSeconds);
        TickFeeding();
        RefreshContentAtSafeBoundary();
        ReleaseUnusedTransientResources();
        if (wasWorking && !_behavior.IsWorkSceneActive)
        {
            _framePlayer.ReleaseWorkFrames();
            _workStage.ReleaseFireFrames();
            _carePanel?.Refresh();
        }
        UpdateFps(compositorSeconds);
    }

    private void ApplyPose(Pose pose)
    {
        PetScale.ScaleX = pose.ScaleX;
        PetScale.ScaleY = pose.ScaleY;
        PetRotation.Angle = pose.RotationDegrees;
        PetTranslation.X = pose.OffsetXDip;
        PetTranslation.Y = pose.OffsetYDip;
    }

    private void UpdateFps(double compositorSeconds)
    {
        if (!_settings.ShowFps)
        {
            _fpsWindowStartSeconds = compositorSeconds;
            _fpsFrameCount = 0;
            return;
        }

        if (_fpsWindowStartSeconds <= 0.0)
        {
            _fpsWindowStartSeconds = compositorSeconds;
        }

        _fpsFrameCount++;
        double elapsed = compositorSeconds - _fpsWindowStartSeconds;
        if (elapsed < 1.0)
        {
            return;
        }

        double fps = _fpsFrameCount / elapsed;
        FpsText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{fps:0} FPS");
        _fpsWindowStartSeconds = compositorSeconds;
        _fpsFrameCount = 0;
    }

    private void PetVisual_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (_windowHandle == nint.Zero ||
            !NativeMethods.GetCursorPos(out NativeMethods.Point pointerOrigin) ||
            !NativeMethods.GetWindowRect(_windowHandle, out _nativeDragWindowOrigin))
        {
            RegisterPetClick();
            return;
        }

        // Cancel a pending two-phase bottom-right placement before the system
        // starts moving the HWND, otherwise its deferred re-anchor could undo
        // the user's drag on a mixed-DPI desktop.
        _pendingBottomRightMonitor = nint.Zero;
        _nativeDragInProgress = true;
        _nativeDragObservedMovement = false;
        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(
                _windowHandle,
                NativeMethods.WmNcLButtonDown,
                (nint)NativeMethods.HtCaption,
                nint.Zero);
        }
        finally
        {
            _nativeDragInProgress = false;
        }

        bool haveFinalWindowRect = NativeMethods.GetWindowRect(
            _windowHandle,
            out NativeMethods.Rect finalWindowRect);
        bool windowMoved = haveFinalWindowRect &&
            (finalWindowRect.Left != _nativeDragWindowOrigin.Left ||
             finalWindowRect.Top != _nativeDragWindowOrigin.Top);

        bool pointerMoved = false;
        if (NativeMethods.GetCursorPos(out NativeMethods.Point pointerFinal))
        {
            double dpiScale = Math.Max(1.0, NativeMethods.GetDpiForWindow(_windowHandle) / 96.0);
            double thresholdX = SystemParameters.MinimumHorizontalDragDistance * dpiScale;
            double thresholdY = SystemParameters.MinimumVerticalDragDistance * dpiScale;
            pointerMoved =
                Math.Abs(pointerFinal.X - pointerOrigin.X) >= thresholdX ||
                Math.Abs(pointerFinal.Y - pointerOrigin.Y) >= thresholdY;
        }

        if (_nativeDragObservedMovement || windowMoved || pointerMoved)
        {
            ClampToVisibleWorkArea();
            SaveCurrentSettings();
        }
        else if (Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds <=
                 MaximumClickDurationMilliseconds)
        {
            RegisterPetClick();
        }
    }

    private void ContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        TouchMenuItem.IsEnabled = FeedMenuItem.IsEnabled = YawnMenuItem.IsEnabled = !InteractionsUnavailable;
        StartWorkMenuItem.IsEnabled = CanStartWork;
        StopWorkMenuItem.IsEnabled = CareState.IsWorking;
        BanterMenuItem.IsChecked = ActiveBanterEnabled;
        EmotionMenuItem.IsChecked = _emotions.Enabled;
        GameMenuItem.IsEnabled = IsGameActive || !InteractionsUnavailable;
        PauseMenuItem.IsEnabled = !WorkInProgress && !IsFeeding && !_pauseChanging && !IsContentEquipmentApplying && !_preparingOwnedAction;
        PauseMenuItem.IsChecked = _settings.IsPaused;
        TopmostMenuItem.IsChecked = _settings.IsTopmost;
        FpsMenuItem.IsChecked = _settings.ShowFps;
        SmallMenuItem.IsChecked = Math.Abs(_settings.SizeScale - 0.80) < 0.01;
        MediumMenuItem.IsChecked = Math.Abs(_settings.SizeScale - 1.00) < 0.01;
        LargeMenuItem.IsChecked = Math.Abs(_settings.SizeScale - 1.28) < 0.01;
    }

    private async void PauseMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_pauseChanging || IsContentEquipmentApplying || _preparingOwnedAction || IsFeeding) return;
        if (WorkInProgress) { Say("先取消工作，再暂停待机动作。 "); return; }
        bool targetPaused = !_settings.IsPaused;
        _pauseChanging = true;
        try
        {
            if (targetPaused) await CancelGameForPauseAsync();
            if (_isClosing) return;
            _settings.IsPaused = targetPaused;
            _behavior.SetPaused(targetPaused);
            ApplyPose(_behavior.CurrentProceduralPose);
            _framePlayer.Apply(_behavior.CurrentSample);
            SaveCurrentSettings();
            if (!targetPaused) ResumeGameAfterPause();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Say("动作还没收好，本次没有切换暂停状态。 "); }
        finally { _pauseChanging = false; }
    }

    private void TopmostMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.IsTopmost = !_settings.IsTopmost;
        Topmost = _settings.IsTopmost;
        foreach (Window child in Application.Current.Windows)
            if (child.Owner == this) child.Topmost = Topmost;
        if (_windowHandle != nint.Zero)
        {
            NativeMethods.SetWindowPos(
                _windowHandle,
                _settings.IsTopmost ? NativeMethods.HwndTopmost : NativeMethods.HwndNoTopmost,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }

        SaveCurrentSettings();
    }

    private void FpsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.ShowFps = !_settings.ShowFps;
        FpsBadge.Visibility = _settings.ShowFps ? Visibility.Visible : Visibility.Collapsed;
        _fpsWindowStartSeconds = 0.0;
        _fpsFrameCount = 0;
        SaveCurrentSettings();
    }

    private void ClipMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (InteractionsUnavailable) return;
        if (sender is MenuItem { Tag: string tag } &&
            Enum.TryParse(tag, ignoreCase: false, out ClipKind kind) &&
            kind != ClipKind.Idle)
        {
            _behavior.RequestAction(kind);
        }
    }

    private void SizeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } ||
            !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale))
        {
            return;
        }

        ApplySize(scale, persist: true);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ClampToVisibleWorkArea);
    }

    private void ApplySize(double scale, bool persist)
    {
        if (!PetSettings.IsSupportedScale(scale))
        {
            scale = 1.0;
        }

        _settings.SizeScale = scale;
        Width = BaseWindowWidth * scale;
        Height = BaseWindowHeight * scale;
        PetVisual.LayoutTransform = new ScaleTransform(scale, scale);

        if (persist)
        {
            SaveCurrentSettings();
        }
    }

    private void ResetPositionMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        PlaceAtBottomRight();
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        // Let Closing protect an in-flight multi-file setup transaction. An
        // explicit Application.Shutdown here would bypass window cancellation.
        Close();
    }

    private void PlaceWindowFromSettings()
    {
        if (_settings.LeftPixels is int left && _settings.TopPixels is int top)
        {
            MoveAndClamp(left, top);
        }
        else
        {
            PlaceAtBottomRight();
        }
    }

    private void PlaceAtBottomRight()
    {
        NativeMethods.Point targetPoint = default;
        nint monitor;
        if (NativeMethods.GetCursorPos(out NativeMethods.Point cursor))
        {
            targetPoint = cursor;
            monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        }
        else
        {
            monitor = NativeMethods.MonitorFromPoint(targetPoint, NativeMethods.MonitorDefaultToPrimary);
        }

        if (!TryGetMonitorWorkArea(monitor, out NativeMethods.Rect workArea) ||
            !NativeMethods.GetWindowRect(_windowHandle, out NativeMethods.Rect windowRect))
        {
            return;
        }

        _pendingBottomRightMonitor = monitor;
        double dpiScale = Math.Max(1.0, NativeMethods.GetDpiForWindow(_windowHandle) / 96.0);
        int marginPixels = (int)Math.Round(BottomRightMarginDip * dpiScale);
        int left = workArea.Right - windowRect.Width - marginPixels;
        int top = workArea.Bottom - windowRect.Height - marginPixels;
        MoveAndClamp(left, top);

        // Moving to a monitor with a different scale causes WM_DPICHANGED to
        // resize the HWND. Re-anchor after that synchronous message and WPF's
        // layout work have both completed, using the new physical size/DPI.
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () => CompleteBottomRightPlacement(monitor));
    }

    private void CompleteBottomRightPlacement(nint monitor)
    {
        if (_pendingBottomRightMonitor != monitor ||
            !TryGetMonitorWorkArea(monitor, out NativeMethods.Rect workArea) ||
            !NativeMethods.GetWindowRect(_windowHandle, out NativeMethods.Rect windowRect))
        {
            return;
        }

        double dpiScale = Math.Max(1.0, NativeMethods.GetDpiForWindow(_windowHandle) / 96.0);
        int marginPixels = (int)Math.Round(BottomRightMarginDip * dpiScale);
        int left = workArea.Right - windowRect.Width - marginPixels;
        int top = workArea.Bottom - windowRect.Height - marginPixels;
        _pendingBottomRightMonitor = nint.Zero;
        MoveAndClamp(left, top);
        SaveCurrentSettings();
    }

    private void ClampToVisibleWorkArea()
    {
        if (_windowHandle == nint.Zero ||
            !NativeMethods.GetWindowRect(_windowHandle, out NativeMethods.Rect windowRect))
        {
            return;
        }

        MoveAndClamp(windowRect.Left, windowRect.Top);
    }

    private void MoveAndClamp(int desiredLeft, int desiredTop)
    {
        if (_windowHandle == nint.Zero ||
            !NativeMethods.GetWindowRect(_windowHandle, out NativeMethods.Rect windowRect))
        {
            return;
        }

        var center = new NativeMethods.Point
        {
            X = desiredLeft + (windowRect.Width / 2),
            Y = desiredTop + (windowRect.Height / 2),
        };
        nint monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MonitorDefaultToNearest);
        if (!TryGetMonitorWorkArea(monitor, out NativeMethods.Rect workArea))
        {
            return;
        }

        int maximumLeft = Math.Max(workArea.Left, workArea.Right - windowRect.Width);
        int maximumTop = Math.Max(workArea.Top, workArea.Bottom - windowRect.Height);
        int left = Math.Clamp(desiredLeft, workArea.Left, maximumLeft);
        int top = Math.Clamp(desiredTop, workArea.Top, maximumTop);

        NativeMethods.SetWindowPos(
            _windowHandle,
            nint.Zero,
            left,
            top,
            0,
            0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    private static bool TryGetMonitorWorkArea(nint monitor, out NativeMethods.Rect workArea)
    {
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (monitor == nint.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            workArea = default;
            return false;
        }

        workArea = monitorInfo.Work;
        return true;
    }

    private void SaveCurrentSettings()
    {
        if (_windowHandle != nint.Zero &&
            NativeMethods.GetWindowRect(_windowHandle, out NativeMethods.Rect rect))
        {
            _settings.LeftPixels = rect.Left;
            _settings.TopPixels = rect.Top;
        }

        _settings.Save();
    }

    private nint WindowMessageHook(
        nint window,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (_nativeDragInProgress &&
            message is NativeMethods.WmMove or NativeMethods.WmMoving)
        {
            if (message == NativeMethods.WmMove)
            {
                _nativeDragObservedMovement = true;
            }
            else if (lParam != nint.Zero)
            {
                NativeMethods.Rect proposedRect =
                    System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.Rect>(lParam);
                _nativeDragObservedMovement |=
                    proposedRect.Left != _nativeDragWindowOrigin.Left ||
                    proposedRect.Top != _nativeDragWindowOrigin.Top;
            }
        }

        if (message == NativeMethods.WmMouseActivate)
        {
            handled = true;
            return NativeMethods.MaNoActivate;
        }

        if (message is NativeMethods.WmDpiChanged or
            NativeMethods.WmDisplayChange or
            NativeMethods.WmSettingChange)
        {
            if (_nativeDragInProgress)
            {
                // The native move loop owns placement until button-up. Its
                // completion path below performs one final clamp/save using
                // the destination monitor's physical work area and DPI.
                return nint.Zero;
            }

            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                () =>
                {
                    if (_pendingBottomRightMonitor != nint.Zero)
                    {
                        CompleteBottomRightPlacement(_pendingBottomRightMonitor);
                    }
                    else
                    {
                        ClampToVisibleWorkArea();
                    }
                });
        }

        return nint.Zero;
    }
}
