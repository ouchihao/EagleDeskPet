using DuckDeskPet.Core;

namespace DuckDeskPet;

// UI-only adapter: renders the real XAML and care values without a live pet, accounts or saves
internal sealed class PetWindow
{
    private readonly PetCareService _care = new(null, DateTimeOffset.UtcNow);
    internal PetState CareState => _care.State;
    internal decimal EarnedCoinsThisRun => _care.EarnedCoinsThisRun;
    internal decimal MoneyPerWorkMinute => _care.MoneyPerWorkMinute;
    internal decimal WorkExperiencePerMinute => _care.WorkExperiencePerMinute;
    internal EquipmentBonuses CurrentEquipmentBonuses => _care.CurrentBonuses;
    internal bool EconomyMigrationApplied { get; set; }
    internal string? EconomyMigrationBackupPath { get; set; }
    internal bool NotificationsEnabled => false;
    internal bool ActiveBanterEnabled => false;
    internal bool InteractionsUnavailable => WorkInProgress;
    internal bool WorkInProgress => CareState.IsWorking;
    internal bool CanStartWork => !WorkInProgress;
    internal string WorkStatus => WorkInProgress ? "办公中 · 0 分 00 秒" : "工位空着，随时开工。";
    internal string CareStatus => "负责吃饭，顺便陪你上班。";
    internal (bool IsConnected, string Login, int UnreadCount) GitHub => (false, "", 0);
    internal const string FoodFormat = "EagleDeskPet.Food.v1";
    internal string FoodDragToken => "ui-only-fixture";
    internal void FeedPet() { }
    internal Task ToggleWorkAsync() => Task.CompletedTask;
    internal void SetActiveBanter(bool enabled) { }
    internal void PetHead() { }
    internal void OpenHonorWall() { }
    internal void OpenGitHubWindow() { }
    internal void OpenClientSetup() { }
    internal void OpenShopWindow() { }
    internal void OpenTaskInbox() { }
    internal void SetNotifications(bool enabled) { }
    internal bool ConfigureApplication(string source, string path) => false;
    internal void ShowDemoNotification(string source) { }
}
