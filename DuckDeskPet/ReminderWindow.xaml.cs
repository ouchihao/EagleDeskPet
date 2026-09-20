using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class ReminderWindow : Window
{
    private readonly PetWindow _pet;
    private readonly ObservableCollection<ReminderRow> _rows = new();
    private string? _feedback;

    public ReminderWindow(PetWindow pet)
    {
        InitializeComponent();
        _pet = pet;
        ReminderList.ItemsSource = _rows;
        Refresh(DateTimeOffset.UtcNow);
        // PetUiMotion's application-level companion handler supplies the same
        // entrance/reduced-motion behavior as the other cream-and-mint panels.
    }

    internal void Refresh(DateTimeOffset now)
    {
        var items = _pet.ReminderItems;
        foreach (var row in _rows.Where(row => items.All(item => item.Id != row.Id)).ToArray()) _rows.Remove(row);
        foreach (var item in items)
        {
            var row = _rows.FirstOrDefault(row => row.Id == item.Id);
            if (row is null) { row = new ReminderRow(item.Id); _rows.Add(row); }
            row.Update(item, now);
        }
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = $"{items.Count} / {ReminderScheduler.MaximumReminders} 条提醒 · {items.Count(item => !item.IsCompleted && !item.IsPaused)} 条计时中";
        AddButton.IsEnabled = items.Count < ReminderScheduler.MaximumReminders;
        StatusText.Text = _pet.ReminderWarning ?? _feedback ?? "支持 1–10080 分钟；提醒正文最多 120 字。";
    }

    private void Preset_OnClick(object sender, RoutedEventArgs e)
    {
        string? preset = (sender as Button)?.Tag as string;
        MinutesBox.Text = preset == "stretch" ? "45" : "20";
        RepeatCheck.IsChecked = preset != "custom";
        MessageBox.Text = preset switch { "water" => "喝口水吧，别光给工作续杯。", "stretch" => "起来活动一下，椅子不会替你升职。", _ => "" };
        MessageBox.Focus();
        MessageBox.SelectAll();
    }

    private void Add_OnClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinutesBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
            _feedback = "请输入整数分钟，例如 20。";
        else
            _feedback = _pet.AddReminder(MessageBox.Text, minutes, RepeatCheck.IsChecked == true) ?? "记住啦，到点由小鹰提醒你。";
        Refresh(DateTimeOffset.UtcNow);
    }

    private void Toggle_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id) _feedback = _pet.ToggleReminder(id) ?? "提醒状态已保存。";
        Refresh(DateTimeOffset.UtcNow);
    }
    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id) _feedback = _pet.DeleteReminder(id) ?? "这条提醒已经删除。";
        Refresh(DateTimeOffset.UtcNow);
    }
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); }

    private sealed class ReminderRow(string id) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public string Message { get; private set; } = "";
        public string RemainingLabel { get; private set; } = "";
        public string StateLabel { get; private set; } = "";
        public string ToggleLabel { get; private set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(LocalReminder item, DateTimeOffset now)
        {
            Message = item.Message;
            var remaining = TimeSpan.FromSeconds(Math.Ceiling(item.Remaining(now).TotalSeconds));
            RemainingLabel = item.IsPending ? "等待气泡空闲" : item.IsCompleted ? "已提醒" :
                (remaining.Days > 0 ? $"{remaining.Days}天 " : "") + (remaining.TotalHours >= 1 ? remaining.ToString(@"hh\:mm\:ss") : remaining.ToString(@"mm\:ss"));
            StateLabel = item.IsPaused ? "已暂停 · 剩余时间冻结" : item.IsCompleted
                ? (item.LastNotifiedUtc is { } last ? "完成于 " + last.ToLocalTime().ToString("MM-dd HH:mm") : "已到期 · 等待提醒")
                : (item.Repeat ? $"每 {item.Minutes} 分钟重复" : $"{item.Minutes} 分钟 · 仅提醒一次");
            ToggleLabel = item.IsCompleted ? "再来一次" : item.IsPaused ? "继续" : "暂停";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
