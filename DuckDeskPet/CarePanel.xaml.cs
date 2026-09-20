using System.Windows;
using System.Windows.Input;
using DuckDeskPet.Core;
using Microsoft.Win32;

namespace DuckDeskPet;

public partial class CarePanel : Window
{
    private readonly PetWindow _pet;
    private Point _foodStart;
    private bool _foodWasDragged;
    internal CarePanel(PetWindow pet)
    {
        _pet = pet;
        InitializeComponent();
        NotificationsCheck.IsChecked = _pet.NotificationsEnabled;
        Refresh();
    }

    internal void Refresh(string? message = null)
    {
        PetState state = _pet.CareState;
        LevelText.Text = state.IsMaximumLevel ? $"Lv.{state.Level} · 已满级" :
            $"Lv.{state.Level}  ·  经验 {state.ExperienceIntoLevel:N0}/{state.NextLevelRequirement:N0}";
        FullnessBar.Value = state.Fullness;
        MoodBar.Value = state.Mood;
        FullnessText.Text = $"{state.Fullness:0} / 100";
        MoodText.Text = $"{state.Mood:0} / 100";
        FoodText.Text = $"粮袋 {state.Food} / 99";
        var remaining = TimeSpan.FromSeconds(Math.Max(0, 300 - state.FoodProgressSeconds));
        IncomeText.Text = state.Food >= 99 ? "粮袋满啦，先吃一点再攒。" : $"下一份粮食 {remaining:mm\\:ss} · 离线最多积累 2 小时";
        StatusText.Text = message ?? _pet.CareStatus;
        WalletText.Text = $"{state.Coins:N2} 鹰币";
        RunWageText.Text = $"本次启动已赚 {_pet.EarnedCoinsThisRun:N2} 鹰币（含本次离线补算，不扣除购物支出）";
        WageText.Text = state.Coins >= EconomyPolicy.MaximumCoins
            ? "钱包已满；满额期间不积压可补领工资。"
            : $"有效工作每分钟 +{_pet.MoneyPerWorkMinute:N2} 鹰币 / +{_pet.WorkExperiencePerMinute:0.##} 经验\n" +
              $"当前装备：饱食衰减 −{_pet.CurrentEquipmentBonuses.FullnessDecayReduction:P0} · 心情衰减 −{_pet.CurrentEquipmentBonuses.MoodDecayReduction:P0}";
        var honors = HonorCatalog.Evaluate(state);
        AchievementsText.Text = $"点亮 {honors.Count(x => x.IsEarned)} / {honors.Count} 枚";
        GitHubStatusText.Text = _pet.GitHub.IsConnected ? $"@{_pet.GitHub.Login} · {_pet.GitHub.UnreadCount} 条本地未读" : "可选连接，不需要 GitHub 密码。";
        FoodButton.IsEnabled = !_pet.InteractionsUnavailable;
        PetButton.IsEnabled = !_pet.InteractionsUnavailable;
        FoodButton.ToolTip = PetButton.ToolTip = _pet.WorkInProgress ? "请先取消工作，收好工位后再互动。" : null;
        WorkText.Text = _pet.WorkStatus;
        WorkButton.Content = state.IsWorking ? "取消工作" : _pet.WorkInProgress ? "收工中…" : "开始工作";
        WorkButton.IsEnabled = state.IsWorking || _pet.CanStartWork;
        BanterCheck.IsChecked = _pet.ActiveBanterEnabled;
    }

    private void Feed_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_foodWasDragged) _pet.FeedPet();
        _foodWasDragged = false;
    }
    internal void SelectSource(string source)
    {
        CareTabs.SelectedItem = MessagesTab;
        AiSettingsExpander.IsExpanded = true;
        SourceBox.Text = source;
    }
    private async void Work_OnClick(object sender, RoutedEventArgs e) => await _pet.ToggleWorkAsync();
    private void Banter_OnClick(object sender, RoutedEventArgs e) => _pet.SetActiveBanter(BanterCheck.IsChecked == true);
    private void Pet_OnClick(object sender, RoutedEventArgs e) => _pet.PetHead();
    private void HonorWall_OnClick(object sender, RoutedEventArgs e) => _pet.OpenHonorWall();
    private void GitHub_OnClick(object sender, RoutedEventArgs e) => _pet.OpenGitHubWindow();
    private void ClientSetup_OnClick(object sender, RoutedEventArgs e) => _pet.OpenClientSetup();
    private void Shop_OnClick(object sender, RoutedEventArgs e) => _pet.OpenShopWindow();
    private async void Game_OnClick(object sender, RoutedEventArgs e) => await _pet.OpenGameAsync();
    private void TaskInbox_OnClick(object sender, RoutedEventArgs e) => _pet.OpenTaskInbox();
    private void Window_OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
    private void Food_OnDown(object sender, MouseButtonEventArgs e)
    {
        _foodStart = e.GetPosition(FoodButton);
        _foodWasDragged = false;
    }
    private void Food_OnMove(object sender, MouseEventArgs e)
    {
        if (_foodWasDragged || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(FoodButton);
        if (Math.Abs(point.X - _foodStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _foodStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _foodWasDragged = true;
        DragDrop.DoDragDrop(FoodButton, new DataObject(PetWindow.FoodFormat, _pet.FoodDragToken), DragDropEffects.Copy);
        e.Handled = true;
    }
    private void Notifications_OnClick(object sender, RoutedEventArgs e) => _pet.SetNotifications(NotificationsCheck.IsChecked == true);
    private string SourceName => SourceBox.Text.Trim();
    private void ChooseApp_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Integration.PetBridgeProtocol.IsSafeText(SourceName, 32)) { StatusText.Text = "来源名请填 1–32 个可显示字符。"; return; }
        var dialog = new OpenFileDialog { Title = "选择提醒后要打开的 AI 应用", Filter = "应用程序 (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        AppPathText.Text = _pet.ConfigureApplication(SourceName, dialog.FileName)
            ? $"{SourceName} → {dialog.FileName}" : "配置未能保存，请检查本地目录权限。";
    }
    private void TestNotification_OnClick(object sender, RoutedEventArgs e) => _pet.ShowDemoNotification(SourceName);
}
