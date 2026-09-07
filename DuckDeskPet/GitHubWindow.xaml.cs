using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuckDeskPet.GitHub;

namespace DuckDeskPet;

public partial class GitHubWindow : Window
{
    private readonly PetWindow _pet;
    private bool _busy;
    private bool _closed;
    private string _inboxFingerprint = "";

    internal GitHubWindow(PetWindow pet)
    {
        _pet = pet;
        InitializeComponent();
        MaxHeight = Math.Max(440, SystemParameters.WorkArea.Height);
        MaxWidth = Math.Max(440, SystemParameters.WorkArea.Width);
        Closed += (_, _) => { _closed = true; TokenBox.Clear(); };
        Refresh();
        if (_pet.GitHub.IsConnected) ConnectionExpander.IsExpanded = false;
    }

    internal void Refresh()
    {
        if (_closed) return;
        var github = _pet.GitHub;
        AccountText.Text = github.IsConnected ? $"已连接 @{github.Login} · 只读通知" : "可选连接 · 只在本机运行";
        UnreadText.Text = github.UnreadCount.ToString();
        StatusText.Text = github.StatusText;
        bool connecting = _busy || github.IsConnecting;
        TokenBox.IsEnabled = ConnectButton.IsEnabled = !connecting;
        ConnectButton.Content = connecting ? "正在验证…" : "验证并连接";
        // Also allow cleanup after an expired token / unreadable local credential.
        // Disconnect is idempotent; an empty inbox does not prove no token is saved.
        DisconnectButton.IsEnabled = true;
        PollButton.IsEnabled = github.IsConnected && !connecting;
        SeenButton.IsEnabled = github.UnreadCount > 0;
        AllNotificationsCheck.IsChecked = github.IncludeAllNotifications;
        var inbox = github.Inbox;
        string fingerprint = string.Join('|', inbox.Select(item => $"{item.VersionKey}:{item.IsSeen}"));
        if (fingerprint != _inboxFingerprint)
        {
            _inboxFingerprint = fingerprint;
            InboxList.ItemsSource = inbox.Select(item => new InboxRow(item)).ToArray();
        }
        EmptyState.Visibility = inbox.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = github.IsConnected ? "小鹰继续帮你盯着，你先安心忙。" : "连接 GitHub 后，与你有关的通知会出现在这里。";
    }

    private async void Connect_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string token = TokenBox.Password;
        TokenBox.Clear();
        _busy = true;
        Refresh();
        try
        {
            var result = await _pet.GitHub.ConnectAsync(token);
            if (!_closed)
            {
                Refresh();
                StatusText.Text = result.Message;
                if (result.Success) ConnectionExpander.IsExpanded = false;
            }
        }
        catch (Exception)
        {
            // Never put credentials, raw HTTP bodies or exception text into the UI/logs.
            if (!_closed) StatusText.Text = "连接暂未完成，请检查网络或凭据后重试。";
        }
        finally { _busy = false; if (!_closed) { TokenBox.IsEnabled = ConnectButton.IsEnabled = true; ConnectButton.Content = "验证并连接"; } }
    }

    private void Disconnect_OnClick(object sender, RoutedEventArgs e)
    {
        _pet.DisconnectGitHub();
        TokenBox.Clear();
        ConnectionExpander.IsExpanded = true;
        Refresh();
    }

    private void CreateToken_OnClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/settings/tokens/new") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { StatusText.Text = "请在浏览器打开 GitHub → Settings → Developer settings → Personal access tokens → Tokens (classic)。"; }
    }

    private async void Poll_OnClick(object sender, RoutedEventArgs e)
    {
        await _pet.GitHub.PollNowAsync();
        if (_closed) return;
        Refresh();
        if (_pet.GitHub.NextPollAt is { } next && next > DateTimeOffset.UtcNow)
            StatusText.Text = _pet.GitHub.StatusText + $" · 下次允许检查 {next.ToLocalTime():HH:mm:ss}";
    }
    private void Filter_OnClick(object sender, RoutedEventArgs e) => _pet.GitHub.SetIncludeAllNotifications(AllNotificationsCheck.IsChecked == true);
    private void Seen_OnClick(object sender, RoutedEventArgs e) => _pet.GitHub.MarkAllSeen();
    private void OpenItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InboxRow row } && !_pet.OpenGitHubItem(row.Item))
            StatusText.Text = "这个链接暂时打不开，请从 GitHub 通知中心查看。";
    }
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
    private sealed record InboxRow(GitHubInboxItem Item)
    {
        public string Repository => Item.Repository;
        public string Title => Item.Title;
        public string ReasonLabel => Item.ReasonLabel;
        public string VersionKey => Item.VersionKey;
        public bool IsSeen => Item.IsSeen;
        public string UpdatedLabel => Item.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    }
}
