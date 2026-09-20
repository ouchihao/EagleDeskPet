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
    internal string? ReminderWarning => _reminderStore.Warning;

    private void InitializeReminders()
    {
        _reminderStore = new ReminderStore(AppPaths.DataDirectory);
        _reminders = new ReminderScheduler(_reminderStore.Load());
        _reminderTimer.Tick += (_, _) => TickReminders(DateTimeOffset.UtcNow);
    }

    private void StartReminders() => _reminderTimer.Start();

    private void StopReminders()
    {
        _reminderTimer.Stop();
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
