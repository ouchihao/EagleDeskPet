using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class TaskInboxWindow : Window
{
    private readonly PetWindow _pet;
    public TaskInboxWindow(PetWindow pet) { InitializeComponent(); _pet = pet; Refresh(); }

    internal void Refresh()
    {
        var all = _pet.TaskNotificationItems;
        SummaryText.Text = $"{all.Count} 个任务 · {all.Count(item => item.IsUnread)} 条未读 · 来源报告，不代替验收";
        EnabledCheck.IsChecked = _pet.TaskNotificationsEnabled;
        MutedCheck.IsChecked = _pet.TaskNotificationsMuted;
        ModeText.Text = !_pet.NotificationsEnabled ? "AI 通知总开关已关闭；此处不会接收新的任务事件。"
            : !_pet.TaskNotificationsEnabled ? "任务接收已关闭；现有消息仍按保留时间清理，旧式回复提醒不受这个开关影响。"
            : _pet.TaskNotificationsMuted ? "静音中：继续收件，不弹任务气泡；恢复后不补播积压。"
            : "普通进度安静收件，重要状态才提醒；任务气泡最少间隔 10 秒，每分钟最多 6 次。";
        var visible = UnreadOnlyCheck.IsChecked == true ? all.Where(item => item.IsUnread).ToArray() : all.ToArray();
        // Avoid replacing bound cards every timer tick when nothing changed.
        var old = TaskList.ItemsSource as TaskNotificationItem[];
        if (old is null || !old.SequenceEqual(visible)) TaskList.ItemsSource = visible;
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReadAllButton.IsEnabled = all.Any(item => item.IsUnread);
        ClearButton.IsEnabled = all.Count > 0;
    }

    private void Enabled_OnClick(object sender, RoutedEventArgs e) => _pet.SetTaskNotificationsEnabled(EnabledCheck.IsChecked == true);
    private void Muted_OnClick(object sender, RoutedEventArgs e) => _pet.SetTaskNotificationsMuted(MutedCheck.IsChecked == true);
    private void Filter_OnClick(object sender, RoutedEventArgs e) => Refresh();
    private void ReadAll_OnClick(object sender, RoutedEventArgs e) => _pet.MarkAllTasksRead();
    private void Clear_OnClick(object sender, RoutedEventArgs e) => _pet.ClearTaskInbox();
    private void Read_OnClick(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is TaskNotificationItem item) _pet.MarkTaskRead(item); }
    private void Open_OnClick(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is TaskNotificationItem item) _pet.OpenTaskApplication(item); }
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); }
}
