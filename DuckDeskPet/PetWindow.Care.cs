using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DuckDeskPet.Core;
using DuckDeskPet.Integration;
using PetNotification = DuckDeskPet.Integration.PetNotification;

namespace DuckDeskPet;

public partial class PetWindow
{
    internal const string FoodFormat = "EagleDeskPet.Food.v1";
    internal string FoodDragToken { get; } = Guid.NewGuid().ToString("N");
    private readonly PetStore _store = new();
    private readonly CompanionPreferences _companion = CompanionPreferences.Load();
    private readonly OfficeBanter _banter = new();
    private PetCareService _care = null!;
    private readonly DispatcherTimer _careTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Queue<PetNotice> _notices = new();
    private PetBridgeServer? _bridge;
    private CarePanel? _carePanel;
    private SpeechBubble? _bubble;
    private PetNotice? _activeNotice;
    private PetNotice? _lastNotice;
    private DateTimeOffset _bubbleUntil;
    private DateTimeOffset _lastSaved;
    private DateTimeOffset _nextHungryHint;
    private DateTimeOffset _lastBanterTick;
    private bool _bubbleIsBanter;
    private bool _preparingWork;
    private bool _assetsReady;
    internal PetState CareState => _care.State;
    internal decimal EarnedCoinsThisRun => _care.EarnedCoinsThisRun;
    internal decimal MoneyPerWorkMinute => _care.MoneyPerWorkMinute;
    internal decimal WorkExperiencePerMinute => _care.WorkExperiencePerMinute;
    internal EquipmentBonuses CurrentEquipmentBonuses => _care.CurrentBonuses;
    internal bool NotificationsEnabled => _companion.NotificationsEnabled;
    internal bool ActiveBanterEnabled => _companion.ActiveBanterEnabled;
    internal bool WorkInProgress => CareState.IsWorking || _behavior.IsWorkSceneActive || _behavior.IsWorkRequested || _preparingWork;
    internal bool InteractionsUnavailable => !_assetsReady || _behavior.IsPaused || WorkInProgress || IsGameActive || IsFeeding || _preparingReaction || _preparingOwnedAction || IsContentEquipmentApplying;
    internal bool CanStartWork => !InteractionsUnavailable && CareState.Fullness > 0;
    internal string WorkStatus => _preparingWork ? "搬工位中…" : CareState.IsWorking
        ? $"{(CareState.IsBusy ? "忙碌中" : "办公中")} · {(int)(CareState.WorkSessionSeconds / 60)} 分 {((int)CareState.WorkSessionSeconds % 60):00} 秒"
        : _behavior.IsWorkSceneActive ? "正在收工，等我收好电脑。" : "工位空着，随时开工。";
    internal string CareStatus { get; private set; } = "负责吃饭，顺便陪你上班。";

    private void InitializeCare()
    {
        // Asset validation happens asynchronously. Never settle offline wages
        // against an unverified saved selection while the loading screen is up.
        _care = new PetCareService(_store.Load(), DateTimeOffset.UtcNow, deferInitialAdvance: true);
        _banter.Enabled = _companion.ActiveBanterEnabled;
        _emotions.Enabled = _companion.AutoEmotionScenesEnabled;
        _lastBanterTick = DateTimeOffset.UtcNow;
        if (CareState.IsWorking)
        {
            _settings.IsPaused = false;
            _behavior.SetPaused(false);
        }
        CareStatus = _store.Warning ?? CareStatus;
        _careTimer.Tick += (_, _) => TickCare();
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); if (!_nativeDragInProgress) PetHead(); };
        LocationChanged += (_, _) => PositionBubble();
        SizeChanged += (_, _) => PositionBubble();
        InitializeCompanions();
    }

    private void StartCare()
    {
        _careTimer.Start();
        _bridge = new PetBridgeServer(
            async (notice, token) => await Dispatcher.InvokeAsync(() => ReceiveNotice(notice), DispatcherPriority.Normal, token),
            async token => await Dispatcher.InvokeAsync<object>(() => new
            {
                name = "大头鹰", level = CareState.Level, fullness = Math.Round(CareState.Fullness),
                mood = Math.Round(CareState.Mood), food = CareState.Food, coins = CareState.Coins,
                paused = _behavior.IsPaused, notificationsEnabled = NotificationsEnabled,
                isWorking = CareState.IsWorking, isBusy = CareState.IsBusy,
                workSessionSeconds = Math.Round(CareState.WorkSessionSeconds, 1), activeBanterEnabled = ActiveBanterEnabled,
                githubConnected = _github.IsConnected, githubUnread = _github.UnreadCount,
                pendingNotifications = _notices.Count, taskUnread = TaskUnreadCount,
                trackedTasks = TaskNotificationItems.Count, currentAction = _behavior.CurrentSample.Kind.ToString(),
                smokeTest = Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_DIR") is not null,
                smokeProcessId = Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_DIR") is not null ? Environment.ProcessId : (int?)null,
                smokeDataDirectory = Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_DIR") is not null ? AppPaths.DataDirectory : null
            }, DispatcherPriority.Normal, token));
        _bridge.Start();
        if (_store.Warning != null) Say(_store.Warning);
    }

    private void StopCare()
    {
        _careTimer.Stop();
        _clickTimer.Stop();
        _bridge?.Dispose();
        _github.Changed -= OnGitHubChanged;
        _github.Dispose();
        _care.Advance(DateTimeOffset.UtcNow);
        _store.Save(CareState);
        _carePanel?.Close();
        _bubble?.Close();
        _githubWindow?.Close();
        _honorWall?.Close();
        _clientSetupWindow?.Close();
        StopTaskNotifications();
        StopFeeding();
        StopContent();
        _hungryScenePreview?.Close();
    }

    private void TickCare()
    {
        var now = DateTimeOffset.UtcNow;
        bool wasWorking = CareState.IsWorking;
        _care.Advance(now);
        _behavior.SetWorkState(CareState.IsWorking, CareState.IsBusy);
        if (wasWorking && !CareState.IsWorking)
        {
            CareStatus = "饿得敲不动键盘了，收工找饭。";
            Say(CareStatus);
        }
        if ((now - _lastSaved).TotalSeconds >= 30)
        {
            _store.Save(CareState);
            _lastSaved = now;
            if (_store.Warning is not null) CareStatus = _store.Warning;
        }
        _carePanel?.Refresh();
        _honorWall?.Refresh();
        RefreshContent();
        if (_bubble?.IsVisible == true && now >= _bubbleUntil) DismissBubble();
        TryShowGitHubNotice();
        TickTaskNotifications();
        TickEmotions(now);
        TickBanter((now - _lastBanterTick).TotalSeconds);
        _lastBanterTick = now;
        if (CareState.IsHungry && now >= _nextHungryHint && _activeNotice is null && _notices.Count == 0)
        {
            _nextHungryHint = now.AddMinutes(20);
            Say("饿得前胸贴后脑勺了……饭呢？");
        }
    }

    internal async void PetHead()
    {
        if (InteractionsUnavailable) { Say(WorkInProgress ? "先让我收工，再摸摸头。" : "先让我活动起来，再摸嘛。 "); return; }
        var decision = _emotions.RegisterPetAttempt(DateTimeOffset.UtcNow);
        if (!decision.AllowCareReward)
        {
            if (decision.Reaction == EmotionReaction.Annoyed) await PlayTouchComplaintAsync();
            Say("再摸就秃了！鹰也是有发际线的。");
            return;
        }
        var result = _care.Pet(DateTimeOffset.UtcNow);
        if (result.Success)
        {
            _behavior.QueueReaction(PetBehaviorKind.Petted);
            _store.Save(CareState);
        }
        CareStatus = _store.Warning ?? result.Message.Trim();
        _carePanel?.Refresh();
        Say(CareStatus);
    }

    private void RegisterPetClick()
    {
        if (_clickTimer.IsEnabled)
        {
            _clickTimer.Stop();
            if (_lastNotice is null) OpenCarePanel();
            else OpenNoticeApplication();
        }
        else _clickTimer.Start();
    }

    internal void OpenCarePanel()
    {
        if (_carePanel is null)
        {
            _carePanel = new CarePanel(this) { Owner = this, Topmost = Topmost };
            _carePanel.Closed += (_, _) => _carePanel = null;
            _carePanel.Show();
            PlaceCompanion(_carePanel, above: false);
        }
        else { _carePanel.Show(); _carePanel.Activate(); }
    }

    private void CareMenu_OnClick(object sender, RoutedEventArgs e) => OpenCarePanel();
    private void FeedMenu_OnClick(object sender, RoutedEventArgs e) => FeedPet();
    private void PetMenu_OnClick(object sender, RoutedEventArgs e) => PetHead();
    private async void WorkMenu_OnClick(object sender, RoutedEventArgs e) => await StartWorkingAsync();
    private void StopWorkMenu_OnClick(object sender, RoutedEventArgs e) => StopWorking();
    private void BanterMenu_OnClick(object sender, RoutedEventArgs e) => SetActiveBanter(BanterMenuItem.IsChecked);

    internal async Task ToggleWorkAsync()
    {
        if (CareState.IsWorking) StopWorking();
        else await StartWorkingAsync();
    }

    internal async Task StartWorkingAsync()
    {
        if (!CanStartWork) { Say(WorkInProgress ? "工位还没收好呢。" : "先恢复动作、吃点饭，再开工吧。 "); return; }
        _preparingWork = true;
        _pendingFeed = false;
        _carePanel?.Refresh();
        try
        {
            // Complete sequences are decoded before the scene can start; the render path never waits on I/O.
            await _framePlayer.WarmWorkAsync();
            await _workStage.WarmFireAsync();
            if (_isClosing) return;
            var result = _care.StartWork(DateTimeOffset.UtcNow);
            _behavior.SetWorkState(CareState.IsWorking, CareState.IsBusy);
            CareStatus = result.Message.Trim();
            _store.Save(CareState);
            Say(CareStatus);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            _framePlayer.ReleaseWorkFrames();
            _workStage.ReleaseFireFrames();
            if (!_isClosing) { CareStatus = "工位素材没有准备好，暂时没开工。"; Say(CareStatus); }
        }
        finally { _preparingWork = false; if (!_isClosing) _carePanel?.Refresh(); }
    }

    internal void StopWorking()
    {
        var result = _care.StopWork(DateTimeOffset.UtcNow);
        _behavior.SetWorkState(CareState.IsWorking, CareState.IsBusy);
        CareStatus = result.Message.Trim();
        _store.Save(CareState);
        _carePanel?.Refresh();
        Say(CareStatus);
    }

    internal void SetActiveBanter(bool enabled)
    {
        _companion.ActiveBanterEnabled = _banter.Enabled = enabled;
        if (!_companion.Save()) CareStatus = "碎碎念开关暂时没能保存。";
        if (!enabled && _bubbleIsBanter) DismissBubble();
        _carePanel?.Refresh();
    }

    private void TickBanter(double elapsedSeconds)
    {
        string? line = _banter.Tick(elapsedSeconds, _isClosing || !_assetsReady || _nativeDragInProgress ||
            _behavior.IsPaused || IsGameActive || _bubble?.IsVisible == true || _activeNotice is not null || _notices.Count > 0,
            CareState.IsWorking ? BanterContext.Working : _emotions.IsHungry ? BanterContext.Hungry :
            _emotions.IsLowMood ? BanterContext.LowMood : DateTime.Now.Hour >= 22 ? BanterContext.LateNight : BanterContext.Ordinary);
        if (line is not null) ShowBubble("", line, "", canOpen: false, seconds: 5, isBanter: true);
    }
    private void Food_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !InteractionsUnavailable && IsOwnFood(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Food_OnDrop(object sender, DragEventArgs e)
    {
        if (!InteractionsUnavailable && IsOwnFood(e)) { FeedPet(); e.Effects = DragDropEffects.Copy; }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }
    private bool IsOwnFood(DragEventArgs e) => e.Data.GetDataPresent(FoodFormat) && e.Data.GetData(FoodFormat) is string token && token == FoodDragToken;

    internal void SetNotifications(bool enabled)
    {
        _companion.NotificationsEnabled = enabled;
        if (!_companion.Save()) CareStatus = "提醒设置暂时没有保存成功。";
        if (!enabled) { _notices.Clear(); if (_activeNotice is not null) DismissBubble(); }
    }

    internal bool ConfigureApplication(string source, string executable)
    {
        if (!PetBridgeProtocol.IsSafeText(source, 32) ||
            !Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
        _companion.Applications[source] = Path.GetFullPath(executable);
        return _companion.Save();
    }

    internal void ShowDemoNotification(string source)
    {
        if (!PetBridgeProtocol.IsSafeText(source, 32)) source = "Codex";
        if (!ReceiveNotice(new(source, Guid.NewGuid().ToString("N"), "demo", "新回复到了，快去看看。", "reply_ready")))
            Say("提醒已关闭，或消息队列暂时满了。 ");
    }

    private bool ReceiveNotice(PetNotification notice) => ReceiveTaskNotice(notice) ?? ReceivePetNotice(new(notice));

    private bool ReceivePetNotice(PetNotice notice)
    {
        if (!_companion.NotificationsEnabled || _isClosing || _notices.Count >= 8) return false;
        _notices.Enqueue(notice);
        if (_activeNotice is null) ShowNextNotice();
        return true;
    }

    private void ShowNextNotice()
    {
        if (_isClosing || _notices.Count == 0) return;
        _activeNotice = _lastNotice = _notices.Dequeue();
        var notice = _activeNotice.Message;
        string headline = notice.EventType switch
        {
            "needs_attention" => $"{notice.Source} 在等你处理",
            "task_failed" => $"{notice.Source} 遇到问题了",
            _ => $"{notice.Source} 有新回复了"
        };
        string[] quips = _activeNotice.GitHubLink is not null
            ? new[] { "PR 有新动静，别让人等到下班。", "消息我叼来了，回复就靠你了。", "有人等你接球，我先接着干饭。" }
            : new[] { "我只是传话的，顺便路过你的粮袋。", "它负责动脑，我负责动嘴。", "快去看，我帮你盯着饭。" };
        ShowBubble(headline, notice.Message.Length == 0 ? "快去看看，别让它等太久。" : notice.Message, quips[Random.Shared.Next(quips.Length)], canOpen: true, seconds: 12);
        // A notification is speech, not an unsolicited change out of idle or ongoing work.
    }

    private void Say(string message)
    {
        // Care responses must not erase an unread AI notice.
        if (_isClosing || _activeNotice is not null) return;
        ShowBubble("大头鹰 · 碎碎念", message.Trim(), "", canOpen: false, seconds: 5);
    }

    private void ShowBubble(string source, string message, string quip, bool canOpen, double seconds, bool isBanter = false)
    {
        if (_isClosing) return;
        if (_bubble is null)
        {
            var created = new SpeechBubble(OpenNoticeApplication, DismissBubble) { Owner = this, Topmost = Topmost };
            _bubble = created;
            created.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_bubble, created)) return;
                _bubble = null;
                _activeNotice = null;
                if (!_isClosing) Dispatcher.BeginInvoke(DispatcherPriority.Background, ShowNextNotice);
            };
        }
        _bubble.SetMessage(source, message, quip, canOpen);
        _bubbleIsBanter = isBanter;
        _bubbleUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
        if (!_bubble.IsVisible) _bubble.Show();
        _bubble.UpdateLayout();
        PositionBubble();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PositionBubble);
    }

    private void DismissBubble()
    {
        _bubble?.Hide();
        _bubbleIsBanter = false;
        _activeNotice = null;
        ShowNextNotice();
    }

    private void OpenNoticeApplication()
    {
        var envelope = _activeNotice ?? _lastNotice;
        if (envelope is null) { OpenCarePanel(); return; }
        if (TryOpenTaskNotice(envelope)) return;
        if (envelope.GitHubLink is Uri githubLink)
        {
            if (OpenGitHubLink(githubLink.AbsoluteUri, envelope.GitHubThreadId, envelope.GitHubVersionKey)) DismissBubble();
            else OpenGitHubWindow();
            return;
        }
        var notice = envelope.Message;
        if (!_companion.Applications.TryGetValue(notice.Source, out var path) || !File.Exists(path))
        {
            CareStatus = $"请在 AI 传话里为“{notice.Source}”选择返回的应用。";
            OpenCarePanel();
            _carePanel!.SelectSource(notice.Source);
            _carePanel.Refresh();
            return;
        }
        try
        {
            // Only the local file selected by this user; never arguments or targets from a tool.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            DismissBubble();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            CareStatus = "应用没能打开，请重新选择可执行文件。";
            OpenCarePanel();
            _carePanel?.Refresh();
        }
    }

    private void PositionBubble()
    {
        if (_bubble?.IsVisible == true) PlaceCompanion(_bubble, above: true);
    }

    private void PlaceCompanion(Window child, bool above)
    {
        nint handle = new WindowInteropHelper(child).Handle;
        if (handle == 0 || !NativeMethods.GetWindowRect(_windowHandle, out var petRect) ||
            !NativeMethods.GetWindowRect(handle, out var childRect)) return;
        var point = new NativeMethods.Point { X = petRect.Left + petRect.Width / 2, Y = petRect.Top + petRect.Height / 2 };
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        if (!TryGetMonitorWorkArea(monitor, out var work)) return;
        int x = petRect.Left - childRect.Width - 10;
        int y = petRect.Bottom - childRect.Height;
        if (above)
        {
            Point head = GetHeadScreenAnchor();
            double dpi = Math.Max(1, NativeMethods.GetDpiForWindow(_windowHandle) / 96.0);
            int gap = (int)Math.Round(4 * dpi);
            x = (int)Math.Round(head.X) - childRect.Width / 2;
            y = (int)Math.Round(head.Y) - childRect.Height - gap;
            if (y < work.Top)
            {
                int beside = (int)Math.Round(40 * dpi * _settings.SizeScale);
                x = (int)Math.Round(head.X) - beside - childRect.Width;
                if (x < work.Left) x = (int)Math.Round(head.X) + beside;
                y = (int)Math.Round(head.Y);
            }
        }
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - childRect.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - childRect.Height));
        NativeMethods.SetWindowPos(handle, nint.Zero, x, y, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    private Point GetHeadScreenAnchor()
    {
        double scale = Math.Min(PetImage.ActualWidth / 384, PetImage.ActualHeight / 346);
        return PetImage.PointToScreen(new Point(PetImage.ActualWidth / 2,
            (PetImage.ActualHeight - 346 * scale) / 2 + 76 * scale));
    }
}
