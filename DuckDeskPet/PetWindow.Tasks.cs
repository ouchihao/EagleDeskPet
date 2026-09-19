using System.Diagnostics;
using System.IO;
using System.Windows;
using DuckDeskPet.Core;
using DuckDeskPet.Integration;
using PetNotification = DuckDeskPet.Integration.PetNotification;

namespace DuckDeskPet;

public partial class PetWindow
{
    private readonly TaskNotificationTracker _taskNotifications = new();
    private TaskInboxWindow? _taskInboxWindow;
    internal IReadOnlyList<TaskNotificationItem> TaskNotificationItems => _taskNotifications.GetItems(DateTimeOffset.UtcNow);
    internal int TaskUnreadCount => TaskNotificationItems.Count(item => item.IsUnread);
    internal bool TaskNotificationsEnabled => _taskNotifications.Enabled;
    internal bool TaskNotificationsMuted => _taskNotifications.IsMuted;

    // Null means the old event notification path must remain in charge. GitHub
    // enters ReceivePetNotice directly and is never interpreted as a CI result.
    private bool? ReceiveTaskNotice(PetNotification notice)
    {
        if (notice.TaskId is null && notice.Revision is null && notice.Status is null) return null;
        if (_isClosing || !NotificationsEnabled || PetBridgeProtocol.Validate(notice) is not null) return false;
        var result = _taskNotifications.Apply(new DuckDeskPet.Core.PetNotification(notice.Source, notice.Message,
            notice.EventId, notice.SessionId, notice.TaskId, notice.Revision, notice.Status, notice.OccurredAt, notice.IsReplay), DateTimeOffset.UtcNow);
        if (result.Changed) TickTaskNotifications();
        return result.Accepted;
    }

    private void TickTaskNotifications()
    {
        if (_isClosing) return;
        var items = TaskNotificationItems;
        if (FindName("TaskInboxMenuItem") is System.Windows.Controls.MenuItem menu)
            menu.Header = TaskUnreadCount > 0 ? $"任务小信使 · {TaskUnreadCount} 条未读" : "任务小信使…";
        _taskInboxWindow?.Refresh();
        if (!NotificationsEnabled || !TaskNotificationsEnabled || TaskNotificationsMuted)
        {
            _taskNotifications.DiscardAnnouncements();
            ClearTaskBubble();
            return;
        }
        if (_lastNotice?.Message.TaskId is { } lastTask)
        {
            var current = items.FirstOrDefault(item => item.Source == _lastNotice.Message.Source && item.TaskId == lastTask);
            _lastNotice = current is null || current.Status == PetTaskStatus.Unknown ? null : new(ToNotice(current));
        }
        if (_activeNotice?.Message.TaskId is { } activeTask)
        {
            var item = items.FirstOrDefault(candidate => candidate.Source == _activeNotice.Message.Source && candidate.TaskId == activeTask);
            if (item is null || item.Status is PetTaskStatus.Unknown or PetTaskStatus.Running || !item.IsUnread)
                DismissBubble();
            else if (_activeNotice.Message.Revision != item.Revision)
            {
                _activeNotice = _lastNotice = new(ToNotice(item));
                _taskNotifications.AcknowledgeAnnouncement(item.Source, item.TaskId);
                // Refresh the one active bubble without extending its lifetime.
                _bubble?.SetMessage(TaskHeadline(item), TaskBody(item), "只传来源明确报告的状态。", true);
                PositionBubble();
            }
        }
        if (!_assetsReady || _nativeDragInProgress || _behavior.IsPaused || _activeNotice is not null ||
            _notices.Count > 0 || _bubble?.IsVisible == true) return;
        var announcement = _taskNotifications.TakeAnnouncement(DateTimeOffset.UtcNow);
        if (announcement is null) return;
        _activeNotice = _lastNotice = new(ToNotice(announcement));
        ShowBubble(TaskHeadline(announcement), TaskBody(announcement), "进度在收件箱，干饭不被打断。", canOpen: true, seconds: 10);
    }

    private static string TaskHeadline(TaskNotificationItem item) => $"{item.Source} · {item.StatusLabel}";
    private static string TaskBody(TaskNotificationItem item)
    {
        string id = item.TaskId.Length > 28 ? item.TaskId[..28] + "…" : item.TaskId;
        return $"任务 {id}\n{(item.Message.Length == 0 ? "打开任务收件箱查看状态。" : item.Message)}";
    }

    private static PetNotification ToNotice(TaskNotificationItem item)
    {
        string status = item.Status switch
        {
            PetTaskStatus.Running => "running", PetTaskStatus.Waiting => "waiting", PetTaskStatus.ReplyReady => "reply_ready",
            PetTaskStatus.Succeeded => "succeeded", PetTaskStatus.Failed => "failed", _ => "reply_ready"
        };
        return new(item.Source, item.EventId, null, item.Message, PetBridgeProtocol.TaskEventType(status)!, item.TaskId, item.Revision, status, item.OccurredAt);
    }

    internal void OpenTaskInbox()
    {
        if (_taskInboxWindow is null)
        {
            var window = new TaskInboxWindow(this) { Owner = this, Topmost = Topmost };
            _taskInboxWindow = window;
            window.Closed += (_, _) => _taskInboxWindow = null;
            window.Show();
            PlaceCompanion(window, above: false);
        }
        else { _taskInboxWindow.Refresh(); _taskInboxWindow.Show(); _taskInboxWindow.Activate(); }
    }

    internal void SetTaskNotificationsEnabled(bool enabled)
    {
        _taskNotifications.SetEnabled(enabled);
        if (!enabled) ClearTaskBubble();
        TickTaskNotifications();
    }

    internal void SetTaskNotificationsMuted(bool muted)
    {
        _taskNotifications.SetMuted(muted);
        if (muted) ClearTaskBubble();
        TickTaskNotifications();
    }

    internal void MarkTaskRead(TaskNotificationItem item)
    {
        _taskNotifications.MarkRead(item.Source, item.TaskId);
        TickTaskNotifications();
    }

    internal void MarkAllTasksRead() { _taskNotifications.MarkAllRead(); TickTaskNotifications(); }
    internal void ClearTaskInbox() { _taskNotifications.Clear(); ClearTaskBubble(); TickTaskNotifications(); }

    internal void OpenTaskApplication(TaskNotificationItem item)
    {
        MarkTaskRead(item);
        // An incoming task can never select a URL, argument or executable.
        if (!_companion.Applications.TryGetValue(item.Source, out var path) || !File.Exists(path))
        {
            CareStatus = $"请在 AI 传话里为“{item.Source}”选择返回的应用。";
            OpenCarePanel();
            _carePanel!.SelectSource(item.Source);
            _carePanel.Refresh();
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { CareStatus = "应用没能打开，请重新选择可执行文件。"; OpenCarePanel(); _carePanel?.Refresh(); }
    }

    private bool TryOpenTaskNotice(PetNotice notice)
    {
        if (notice.Message.TaskId is not { } taskId) return false;
        var item = TaskNotificationItems.FirstOrDefault(candidate => candidate.Source == notice.Message.Source && candidate.TaskId == taskId);
        if (item is null || item.Status == PetTaskStatus.Unknown) OpenTaskInbox();
        else OpenTaskApplication(item);
        return true;
    }

    private void ClearTaskBubble()
    {
        if (_lastNotice?.Message.TaskId is not null) _lastNotice = null;
        if (_activeNotice?.Message.TaskId is not null) DismissBubble();
    }

    private void StopTaskNotifications()
    {
        _taskInboxWindow?.Close();
        _taskNotifications.Clear();
    }

    private void TaskInboxMenu_OnClick(object sender, RoutedEventArgs e) => OpenTaskInbox();
}
