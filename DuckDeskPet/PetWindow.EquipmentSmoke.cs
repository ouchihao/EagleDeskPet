using System.IO;
using System.Windows;
using System.Windows.Controls;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    // Only called after ValidateExpansionIsolation: never seed the real pet.
    private async Task RunEquipmentSmokeAsync(string output, CancellationToken token)
    {
        string[][] sets =
        [
            [ContentCatalog.NoodleDeskId, ContentCatalog.ServerComputerId, ContentCatalog.OxOutfitId],
            [ContentCatalog.CloudDeskId, ContentCatalog.GoldComputerId, ContentCatalog.HeroOutfitId],
            [ContentCatalog.BoardroomDeskId, ContentCatalog.UltrabookComputerId, ContentCatalog.AstronautOutfitId],
        ];
        foreach (var set in sets)
        {
            await ExpansionIdleAsync(token);
            foreach (string id in set)
            {
                ExpansionCheck(IsContentResourceAvailable(ContentCatalog.Get(id)), id + " resource validation failed.");
                var purchase = _contentTransactions!.Purchase(id);
                ExpansionCheck(purchase.Success, id + " purchase failed.");
                ExpansionCheck((await EquipContentAsync(id).WaitAsync(token)).Success, id + " equipment failed.");
            }
            await ExpansionWaitAsync(() => _framePlayer.CurrentOutfitId == set[2] &&
                _workStage.ActiveSelection.DeskId == set[0] && _workStage.ActiveSelection.ComputerId == set[1], token, "actual V2 wardrobe/scene");
            SynchronizeEquipmentBonuses();
            var expected = EquipmentPolicy.Resolve(CareState.Content, new Dictionary<ContentSlot, string>
                { [ContentSlot.Desk] = set[0], [ContentSlot.Computer] = set[1], [ContentSlot.Outfit] = set[2] });
            ExpansionCheck(!_care.IsCatchUpPending && CurrentEquipmentBonuses == expected &&
                MoneyPerWorkMinute == 1m + expected.WorkCoinBonus, "Actual equipment bonuses disagree with rendered equipment.");
            RenderOwnVisual(Root, Path.Combine(output, "equipment-" + set[2] + "-idle.png"));
            CareState.Fullness = 90;
            await StartWorkingAsync().WaitAsync(token);
            await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.WorkLoop, token, "V2 equipped workplace");
            await Task.Delay(200, token);
            RenderOwnVisual(Root, Path.Combine(output, "equipment-" + set[2] + "-work.png"));
            StopWorking();
            await ExpansionIdleAsync(token);
        }
        OpenShopWindow();
        _shopWindow!.SelectItem(ContentCatalog.OfficeOutfitId);
        await Task.Delay(200, token);
        RenderOwnVisual(_shopWindow, Path.Combine(output, "equipment-shop-v111.png"));
        _shopWindow.Close();
        var saved = new PetStore(AppPaths.DataDirectory).Load();
        ExpansionCheck(saved is not null && sets.SelectMany(x => x).All(x => ContentOwnershipService.Owns(saved.Content, x)), "V2 ownership was not durable.");
    }

    private async Task RunReminderSmokeAsync(string output, CancellationToken token)
    {
        _reminderTimer.Stop();
        try
        {
            var menu = PetMenu.Items.OfType<MenuItem>().Single(x => x.Header?.ToString()?.Contains("定时提醒", StringComparison.Ordinal) == true);
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ExpansionCheck(_reminderWindow?.IsVisible == true, "Reminder menu did not open its actual window.");
            ((TextBox)_reminderWindow!.FindName("MinutesBox")).Text = "1";
            ((TextBox)_reminderWindow.FindName("MessageBox")).Text = "隔离验收：喝口水";
            ((CheckBox)_reminderWindow.FindName("RepeatCheck")).IsChecked = false;
            ((Button)_reminderWindow.FindName("AddButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ExpansionCheck(ReminderItems.Count == 1 && new ReminderStore(AppPaths.DataDirectory).Load()!.Items.Count == 1,
                "Actual reminder form did not persist the new item.");
            string id = ReminderItems.Single().Id;
            ExpansionCheck(ToggleReminder(id) is null && ReminderItems.Single().IsPaused, "Pause failed.");
            ExpansionCheck(ToggleReminder(id) is null && !ReminderItems.Single().IsPaused, "Resume failed.");
            // Seed only a deadline, then invoke the same timer handler as production.
            var now = DateTimeOffset.UtcNow;
            var due = new ReminderScheduler(new ReminderSnapshot { Items = [ReminderItems.Single() with { DueAtUtc = now.AddSeconds(-1) }] });
            ExpansionCheck(CommitReminders(due, now), "Could not seed isolated deadline.");
            DismissBubble();
            ReceiveNotice(new DuckDeskPet.Integration.PetNotification("Smoke AI", Guid.NewGuid().ToString("N"), "demo", "优先消息", "reply_ready"));
            var notice = _activeNotice;
            TickReminders(now);
            ExpansionCheck(notice is not null && ReferenceEquals(_activeNotice, notice) && ReminderItems.Single().IsPending,
                "A reminder displaced an unread AI message.");
            DismissBubble();
            var last = _lastNotice;
            var action = _behavior.CurrentSample.Kind;
            TickReminders(now.AddSeconds(1));
            ExpansionCheck(_bubble?.IsVisible == true && ((TextBlock)_bubble.FindName("MessageText")).Text.Contains("喝口水") &&
                ReferenceEquals(last, _lastNotice) && _activeNotice is null && _behavior.CurrentSample.Kind == action &&
                ReminderItems.Single().IsCompleted && !ReminderItems.Single().IsPending, "Reminder delivery changed inbox/animation state or failed.");
            await Task.Delay(120, token);
            RenderOwnVisual(_bubble!, Path.Combine(output, "reminder-bubble-v111.png"));
            RenderOwnVisual(_reminderWindow, Path.Combine(output, "reminder-window-v111.png"));
            ExpansionCheck(DeleteReminder(id) is null && ReminderItems.Count == 0, "Reminder deletion failed.");
            _reminderWindow.Close();
            DismissBubble();
        }
        finally { if (!_isClosing) _reminderTimer.Start(); }
    }
}
