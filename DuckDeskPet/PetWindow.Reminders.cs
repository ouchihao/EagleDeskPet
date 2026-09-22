using System.Windows;
using System.Windows.Threading;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private readonly DispatcherTimer _reminderTimer = new(DispatcherPriority.ContextIdle) { Interval = TimeSpan.FromSeconds(1) };
    private ReminderStore _reminderStore = null!;
    private ReminderScheduler _reminders = new();
    private ReminderWindow? _reminderWindow;
    private DateTimeOffset _nextReminderBubbleAt;
    private DateTimeOffset _reminderRetryAt;
    internal IReadOnlyList<LocalReminder> ReminderItems => _reminders.Items;
    private string? _reminderNotificationFeedback;
    internal string? ReminderWarning => _reminderStore.Warning ?? _reminderNotificationFeedback;
    internal string? ReminderStorageWarning => _reminderStore.Warning;
    internal bool NativeReminderEnabled => _reminders.NativeNotificationsEnabled;
    internal bool ReminderBubbleEnabled => _reminders.BubbleNotificationsEnabled;

    private void InitializeReminders()
    {
        _reminderStore = new ReminderStore(AppPaths.DataDirectory);
        _reminders = new ReminderScheduler(_reminderStore.Load());
        var recovery = new ReminderScheduler(_reminders.Snapshot());
        if (recovery.RecoverInterruptedDeliveries()) CommitReminders(recovery, DateTimeOffset.UtcNow);
        ReminderNotificationServices.DeliveryFailed += OnNativeReminderFailed;
        _reminderTimer.Tick += (_, _) => TickReminders(DateTimeOffset.UtcNow);
    }

    private void StartReminders() => _reminderTimer.Start();

    private void StopReminders()
    {
        _reminderTimer.Stop();
        ReminderNotificationServices.DeliveryFailed -= OnNativeReminderFailed;
        _reminderWindow?.Close();
        // Every mutation was already persisted. Do not overwrite a concurrent
        // instance on exit, or move a running UTC deadline forward on shutdown.
    }

    private void TickReminders(DateTimeOffset now)
    {
        if (_isClosing) return;
        _reminderWindow?.Refresh(now);
        if (now < _reminderRetryAt) return;
        var candidate = new ReminderScheduler(_reminders.Snapshot());
        if (candidate.Advance(now) && !CommitReminders(candidate, now)) return;
        // Native submission is deliberately before every animation, bubble,
        // drag and inbox guard; there is still only one business timer
        if (_reminders.HasPendingNative)
        {
            candidate = new ReminderScheduler(_reminders.Snapshot());
            var batch = candidate.ReserveNativeBatch();
            if (batch.Count > 0)
            {
                if (!CommitReminders(candidate, now)) return;
                var result = ReminderNotificationServices.Current.Send(batch);
                candidate = new ReminderScheduler(_reminders.Snapshot());
                candidate.CompleteNativeBatch(batch, result.Accepted, result.Detail, result.Uncertain);
                _reminderNotificationFeedback = result.Detail;
                if (!CommitReminders(candidate, now)) return;
            }
        }
        if (!_reminders.HasPending || !_assetsReady || _nativeDragInProgress || now < _nextReminderBubbleAt) return;

        // The existing AI/GitHub inboxes retain priority and unread ownership.
        // This module never writes _activeNotice or _lastNotice, and never asks
        // the behavior controller to change an animation or ongoing work.
        TryShowGitHubNotice();
        TickTaskNotifications();
        if (_activeNotice is not null || _notices.Count > 0 || _bubble?.IsVisible == true) return;
        candidate = new ReminderScheduler(_reminders.Snapshot());
        var pending = candidate.AcknowledgePending(now);
        if (pending.Count == 0 || !CommitReminders(candidate, now)) return;
        _nextReminderBubbleAt = now.AddSeconds(30);
        ShowBubble("", ReminderScheduler.BubbleMessage(pending), "", canOpen: false, seconds: 10);
    }

    private bool CommitReminders(ReminderScheduler candidate, DateTimeOffset now)
    {
        if (!_reminderStore.Save(candidate.Snapshot()))
        {
            _reminderRetryAt = now.AddSeconds(30);
            _reminderWindow?.Refresh(now);
            return false;
        }
        _reminders = candidate;
        _reminderRetryAt = DateTimeOffset.MinValue;
        _reminderWindow?.Refresh(now);
        return true;
    }

    private void OnNativeReminderFailed(IReadOnlyList<LocalReminder> batch, string detail)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing) return;
            var candidate = new ReminderScheduler(_reminders.Snapshot());
            if (candidate.RecordNativeFailure(batch, detail)) CommitReminders(candidate, DateTimeOffset.UtcNow);
        });
    }

    internal string? AddReminder(string message, int minutes, bool repeat)
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = new ReminderScheduler(_reminders.Snapshot());
        try { candidate.Add(message, minutes, repeat, now); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ex.Message; }
        return CommitReminders(candidate, now) ? null : ReminderWarning;
    }

    internal string? ToggleReminder(string id)
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = new ReminderScheduler(_reminders.Snapshot());
        if (!candidate.TogglePause(id, now)) return "这条提醒已经不在了。";
        return CommitReminders(candidate, now) ? null : ReminderWarning;
    }

    internal string? DeleteReminder(string id)
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = new ReminderScheduler(_reminders.Snapshot());
        if (!candidate.Remove(id)) return "这条提醒已经不在了。";
        return CommitReminders(candidate, now) ? null : ReminderWarning;
    }

    internal string? SetReminderChannels(bool native, bool bubble)
    {
        if (native && !_reminders.NativeNotificationsEnabled)
        {
            var readiness = ReminderNotificationServices.Current.Prepare();
            if (!readiness.Accepted) return _reminderNotificationFeedback = readiness.Detail;
        }
        var candidate = new ReminderScheduler(_reminders.Snapshot());
        candidate.SetChannels(native, bubble);
        if (!CommitReminders(candidate, DateTimeOffset.UtcNow)) return ReminderWarning;
        _reminderNotificationFeedback = native ? "Windows 原生提醒已启用。仅提醒今后的到期事件；系统可能因勿扰或权限隐藏横幅。"
            : "Windows 原生提醒已关闭；现有通知仍可打开列表，不会更改提醒。";
        return null;
    }

    internal string TestReminderNotification()
    {
        var result = ReminderNotificationServices.Current.SendTest();
        return _reminderNotificationFeedback = result.Detail;
    }

    internal void HandleReminderActivation(ReminderNativeActivation activation)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => HandleReminderActivation(activation)); return; }
        if (_isClosing) return;
        bool current = !activation.IsTest && _reminders.IsCurrentActivation(activation.ReminderId, activation.CycleId);
        _reminderNotificationFeedback = activation.IsTest ? "测试通知点击已收到；没有修改任何提醒。" : current
            ? "已定位此轮提醒。查看不等于暂停，重复提醒会按原间隔继续。"
            : "这条通知已过期、提醒已暂停或删除；这里只打开列表，不恢复旧提醒。";
        OpenReminderWindow();
        _reminderWindow!.FocusReminder(current ? activation.ReminderId : null);
    }

    internal void OpenReminderWindow()
    {
        if (_reminderWindow is null)
        {
            var window = new ReminderWindow(this) { Owner = this, Topmost = Topmost };
            _reminderWindow = window;
            window.Closed += (_, _) => _reminderWindow = null;
            window.Show();
            PlaceCompanion(window, above: false);
        }
        else { _reminderWindow.Refresh(DateTimeOffset.UtcNow); _reminderWindow.Show(); _reminderWindow.Activate(); }
    }

    private void ReminderMenu_OnClick(object sender, RoutedEventArgs e) => OpenReminderWindow();
}
