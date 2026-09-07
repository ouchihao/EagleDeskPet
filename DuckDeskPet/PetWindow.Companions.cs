using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DuckDeskPet.GitHub;
using DuckDeskPet.Integration;

namespace DuckDeskPet;

public partial class PetWindow
{
    // A trusted in-process adapter can attach a validated GitHub destination.
    // The external MCP protocol still accepts only plain text, never launch URLs.
    private sealed record PetNotice(PetNotification Message, Uri? GitHubLink = null, string? GitHubThreadId = null, string? GitHubVersionKey = null);
    private GitHubNotificationService _github = new(AppPaths.DataDirectory);
    private GitHubWindow? _githubWindow;
    private HonorWallWindow? _honorWall;
    private ClientSetupWindow? _clientSetupWindow;
    private string? _displayedGitHubLogin;
    internal GitHubNotificationService GitHub => _github;

    private void InitializeCompanions() => _github.Changed += OnGitHubChanged;

    private async Task RestoreGitHubAsync()
    {
        try { await _github.RestoreAsync(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            if (!_isClosing) CareStatus = "GitHub 连接暂时无法恢复，请在消息面板重新连接。";
        }
    }

    private void OnGitHubChanged()
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_isClosing) return;
            if (_displayedGitHubLogin is not null && !string.Equals(_displayedGitHubLogin, _github.Login, StringComparison.OrdinalIgnoreCase))
                ClearGitHubNotices();
            _displayedGitHubLogin = _github.Login;
            UpdateGitHubBadge();
            _githubWindow?.Refresh();
            _carePanel?.Refresh();
            TryShowGitHubNotice();
        });
    }

    private void UpdateGitHubBadge()
    {
        int count = _github.UnreadCount;
        GitHubUnreadText.Text = count > 99 ? "99+" : count.ToString();
        GitHubUnreadBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GitHubMenuItem.Header = count > 0 ? $"GitHub 消息 · {count} 条未读" : "GitHub 消息…";
    }

    private void TryShowGitHubNotice()
    {
        if (_isClosing || !_assetsReady || !_github.IsConnected || !NotificationsEnabled || _nativeDragInProgress ||
            _behavior.IsPaused || _activeNotice is not null || _notices.Count > 0 || _bubble?.IsVisible == true) return;
        var item = _github.PeekAnnouncement();
        if (item is null) return;
        if (!GitHubLinks.TryGetWebUri(item.WebUrl, out Uri? uri) || uri is null) return;
        string summary = $"{item.Repository}：{item.Title}";
        if (summary.Length > 190) summary = summary[..187] + "…";
        var message = new PetNotification("GitHub", item.VersionKey, "github-inbox", summary, "needs_attention");
        if (ReceivePetNotice(new(message, uri, item.ThreadId, item.VersionKey)))
            _github.AcknowledgeAnnouncement(item.VersionKey);
    }

    internal void OpenGitHubWindow()
    {
        if (_githubWindow is null)
        {
            var window = new GitHubWindow(this) { Owner = this, Topmost = Topmost };
            _githubWindow = window;
            window.Closed += (_, _) => _githubWindow = null;
            window.Show();
            PlaceCompanion(window, above: false);
        }
        else { _githubWindow.Show(); _githubWindow.Activate(); }
    }

    internal void OpenHonorWall()
    {
        if (_honorWall is null)
        {
            var window = new HonorWallWindow(() => CareState) { Owner = this, Topmost = Topmost };
            _honorWall = window;
            window.Closed += (_, _) => _honorWall = null;
            window.Show();
            PlaceCompanion(window, above: false);
        }
        else { _honorWall.Refresh(); _honorWall.Show(); _honorWall.Activate(); }
    }

    internal void OpenClientSetup()
    {
        if (_clientSetupWindow is null)
        {
            var window = new ClientSetupWindow() { Owner = this, Topmost = Topmost };
            _clientSetupWindow = window;
            window.Closed += (_, _) => _clientSetupWindow = null;
            window.Show();
            PlaceCompanion(window, above: false);
        }
        else { _clientSetupWindow.Show(); _clientSetupWindow.Activate(); }
    }

    internal bool OpenGitHubItem(GitHubInboxItem item) => OpenGitHubLink(item.WebUrl, item.ThreadId, item.VersionKey);

    private bool OpenGitHubLink(string value, string? threadId, string? versionKey)
    {
        if (!GitHubLinks.TryGetWebUri(value, out Uri? uri) || uri is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            if (threadId is not null && versionKey is not null) _github.MarkSeen(threadId, versionKey);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            CareStatus = "浏览器没有打开，可以稍后从 GitHub 消息面板再试。";
            return false;
        }
    }

    internal void DisconnectGitHub()
    {
        _github.Disconnect();
        ClearGitHubNotices();
        UpdateGitHubBadge();
    }

    private void ClearGitHubNotices()
    {
        var remaining = _notices.Where(item => item.GitHubThreadId is null).ToArray();
        _notices.Clear();
        foreach (var item in remaining) _notices.Enqueue(item);
        if (_lastNotice?.GitHubThreadId is not null) _lastNotice = null;
        if (_activeNotice?.GitHubThreadId is not null) DismissBubble();
    }

    private void GitHubMenu_OnClick(object sender, RoutedEventArgs e) => OpenGitHubWindow();
    private void HonorMenu_OnClick(object sender, RoutedEventArgs e) => OpenHonorWall();
    private void ClientSetupMenu_OnClick(object sender, RoutedEventArgs e) => OpenClientSetup();
}
