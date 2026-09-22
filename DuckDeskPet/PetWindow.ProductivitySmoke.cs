using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    private async Task RunProductivitySmokeAsync(string output, CancellationToken token)
    {
        ValidateExpansionIsolation(output);
        await ExpansionIdleAsync(token);
        await StartWorkingAsync();
        await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.WorkLoop, token, "notebook while working");
        var channel = new ProductivitySmokeChannel();
        var previousChannel = ReminderNotificationServices.Current;
        ReminderNotificationServices.Current = channel;
        _reminderTimer.Stop();
        try
        {
            var menu = PetMenu.Items.OfType<MenuItem>().Single(x => x.Header?.ToString()?.Contains("图钉便签墙", StringComparison.Ordinal) == true);
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ExpansionCheck(_notebookWindow?.IsVisible == true && _notebookWindow.Owner == this, "Notebook menu/owner not connected.");
            var wall = _notebookWindow!;
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ExpansionCheck(ReferenceEquals(wall, _notebookWindow), "Notebook opened a duplicate window.");
            var input = (TextBox)wall.FindName("ComposerBox");
            var add = (Button)wall.FindName("AddButton");
            input.Text = "隔离验收：检查 PR 回复";
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            input.Text = "隔离验收：下班记得带饭盒";
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var notes = new NotebookStore(AppPaths.DataDirectory).Load();
            ExpansionCheck(notes.Notes.Count == 2 && notes.Notes.All(n => !n.IsCompleted), "Notebook UI did not persist two separate notes.");
            string firstId = notes.Notes.First(n => n.Text.Contains("PR")).Id;
            await Task.Delay(240, token);
            wall.UpdateLayout();
            RenderOwnVisual(wall, Path.Combine(output, "notebook-real-exe-two-notes.png"));
            var complete = ExpansionVisuals<Button>(wall).Single(b => (string?)b.Tag == firstId && b.Content?.ToString() == "✓ 完成");
            complete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ExpansionCheck(new NotebookStore(AppPaths.DataDirectory).Load().Notes.Single(n => n.Id == firstId).IsCompleted,
                "Notebook removal animation started before durable completion.");
            await wall.PendingAnimation.WaitAsync(token);
            ((RadioButton)wall.FindName("ArchiveTab")).IsChecked = true;
            wall.UpdateLayout();
            var restore = ExpansionVisuals<Button>(wall).Single(b => (string?)b.Tag == firstId && b.Content?.ToString() == "↩ 恢复到墙上");
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ExpansionCheck(new NotebookStore(AppPaths.DataDirectory).Load().Notes.Count(n => !n.IsCompleted) == 2, "Notebook restore lost a note.");
            wall.Close();
            ExpansionCheck(_notebookWindow is null, "Notebook lifetime did not clear the closed reference.");
            OpenNotebookWindow();
            await Task.Delay(220, token);
            ExpansionCheck(((ItemsControl)_notebookWindow!.FindName("NotesItems")).Items.Count == 2, "Reopened wall lost its saved notes.");
            _notebookWindow.Close();
            ExpansionCheck(CareState.IsWorking && _behavior.IsWorkSceneActive, "Notebook operation interrupted work.");

            ExpansionCheck(SetReminderChannels(native: true, bubble: false) is null, "Could not enable the isolated native channel.");
            ExpansionCheck(AddReminder("隔离验收：系统通道不等待 AI 气泡", 1, repeat: true) is null, "Reminder creation failed.");
            var now = DateTimeOffset.UtcNow;
            var snapshot = _reminders.Snapshot();
            string reminderId = snapshot.Items.Single().Id;
            snapshot = snapshot with { Items = [snapshot.Items.Single() with { DueAtUtc = now.AddSeconds(-1) }] };
            ExpansionCheck(CommitReminders(new ReminderScheduler(snapshot), now), "Could not seed an isolated deadline.");
            ReceiveNotice(new Integration.PetNotification("Smoke AI", Guid.NewGuid().ToString("N"), "demo", "这是一条仍在显示的 AI 消息", "reply_ready"));
            var activeNotice = _activeNotice;
            TickReminders(now);
            ExpansionCheck(channel.Submissions == 1 && ReferenceEquals(activeNotice, _activeNotice) && CareState.IsWorking &&
                ReminderItems.Single().NativeDelivery == NativeReminderDelivery.Accepted, "Native channel waited for or displaced work/AI.");
            TickReminders(now.AddSeconds(1));
            ExpansionCheck(channel.Submissions == 1, "Native cycle was submitted twice.");
            var delivered = ReminderItems.Single();
            string savedBeforeClick = JsonSerializer.Serialize(_reminders.Snapshot());
            HandleReminderActivation(new(reminderId, delivered.CycleId));
            ExpansionCheck(_reminderWindow?.IsVisible == true && savedBeforeClick == JsonSerializer.Serialize(_reminders.Snapshot()), "Click mutated the reminder.");
            await Task.Delay(220, token);
            RenderOwnVisual(_reminderWindow!, Path.Combine(output, "reminder-native-channel-real-exe.png"));
            ExpansionCheck(DeleteReminder(reminderId) is null, "Could not delete the isolated reminder.");
            HandleReminderActivation(new(reminderId, delivered.CycleId));
            ExpansionCheck(ReminderItems.Count == 0, "A stale activation resurrected a deleted reminder.");
        }
        finally
        {
            SetReminderChannels(native: false, bubble: true);
            ReminderNotificationServices.Current = previousChannel;
            _notebookWindow?.Close();
            _reminderWindow?.Close();
            DismissBubble();
            StopWorking();
            await ExpansionIdleAsync(token);
            if (!_isClosing) _reminderTimer.Start();
        }
    }

    // This tests the real host's due-event wiring, not a Windows banner or COM activation
    private sealed class ProductivitySmokeChannel : IReminderNativeChannel
    {
        internal int Submissions { get; private set; }
        public ReminderNativeResult Send(IReadOnlyList<LocalReminder> batch)
        { Submissions++; return new(true, "隔离宿主验收的假系统通道：没有调用 Windows，也没有注册通知身份。"); }
        public ReminderNativeResult SendTest() => throw new InvalidOperationException("The host smoke must not send a real test notification.");
    }
}
