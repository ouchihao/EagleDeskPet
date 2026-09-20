using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DuckDeskPet.GitHub;
using DuckDeskPet.ClientSetup;
using DuckDeskPet.Core;

namespace DuckDeskPet;

public partial class PetWindow
{
    // Opt-in integration evidence from our own WPF trees, with a fake HTTP
    // handler and fresh data directory. No GitHub account or browser is touched.
    private async Task RunFeaturesSmokeAsync(string output)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL")))
            throw new InvalidOperationException("Feature smoke requires an isolated test channel.");
        _careTimer.Stop();
        SetActiveBanter(false);
        _notices.Clear();
        DismissBubble();
        var checks = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }

        PetMenu.PlacementTarget = PetVisual;
        PetMenu.IsOpen = true;
        await Task.Delay(100);
        Check(PetMenu.Items.Contains(TouchMenuItem) && PetMenu.Items.Contains(FeedMenuItem) &&
            PetMenu.Items.Contains(YawnMenuItem) && PetMenu.Items.Contains(StartWorkMenuItem), "Interaction options exposed at top level");
        Check(!PetMenu.Items.OfType<MenuItem>().Any(item => item.Header is "待机组" or "交互组"), "No old action-group submenu");
        RenderOwnVisual(PetMenu, Path.Combine(output, "context-menu.png"));
        SizeRootMenuItem.IsSubmenuOpen = true;
        await Task.Delay(100);
        var popup = SizeRootMenuItem.Template.FindName("PART_Popup", SizeRootMenuItem) as Popup;
        Check(popup?.IsOpen == true && popup.Child is FrameworkElement, "Size submenu still opens with custom template");
        RenderOwnVisual((FrameworkElement)popup!.Child, Path.Combine(output, "size-menu.png"));
        SizeRootMenuItem.IsSubmenuOpen = false;
        PetMenu.IsOpen = false;

        // Keep the real save model/API, but give this disposable test gallery a
        // mixture of earned bronze/silver and previewable locked gold medals.
        CareState.TotalMeals = 12;
        CareState.TotalPets = 55;
        CareState.Experience = 450;
        OpenCarePanel();
        ((Button)_carePanel!.FindName("HonorWallButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(100);
        Check(_honorWall is not null, "Care-panel honor card opens its separate exhibition");
        Check(HonorCatalog.Definitions.Count == 18 && ((ItemsControl)_honorWall!.FindName("CardsItems")).Items.Count is > 0 and <= 6,
            "All eighteen honors are available in a bounded paged gallery");
        RenderOwnVisual(_honorWall!, Path.Combine(output, "honor-wall.png"));
        RenderOwnVisual(_carePanel, Path.Combine(output, "care-panel.png"));

        // The setup window is injected with a disposable client home. Even the
        // real menu/button routing below must never inspect or edit real AI configs.
        string clientHome = Path.Combine(output, "client-home");
        Directory.CreateDirectory(Path.Combine(clientHome, ".codex"));
        string codexConfig = Path.Combine(clientHome, ".codex", "config.toml");
        const string existingConfig = "# Existing settings must remain\nmodel = \"demo-model\"\n\n[mcp_servers.keep-me]\ncommand = \"not-a-real-command\"\n";
        File.WriteAllText(codexConfig, existingConfig);
        var setupService = new ClientSetupService(clientHome, AppContext.BaseDirectory);
        var setupWindow = new ClientSetupWindow(setupService) { Owner = this, Topmost = Topmost };
        _clientSetupWindow = setupWindow;
        setupWindow.Closed += (_, _) => _clientSetupWindow = null;
        setupWindow.Show();
        await setupWindow.PendingOperation;
        ((TabControl)_carePanel.FindName("CareTabs")).SelectedIndex = 2;
        ((Expander)_carePanel.FindName("AiSettingsExpander")).IsExpanded = true;
        ((Button)_carePanel.FindName("ClientSetupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(ReferenceEquals(_clientSetupWindow, setupWindow), "Care-panel one-click setup opens/reuses its own window");
        Check(File.ReadAllText(codexConfig) == existingConfig, "Setup preview does not write even disposable client settings");
        RenderOwnVisual(setupWindow, Path.Combine(output, "client-setup-preview.png"));
        var install = (Button)setupWindow.FindName("InstallButton");
        Check(install.IsEnabled, "Empty test client has an actionable setup preview");
        install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(setupWindow.IsWriting, "Setup exposes its in-flight write before allowing another UI action");
        Close();
        Check(!_isClosing && IsVisible, "Pet close is cancelled while client configuration is being saved");
        await setupWindow.PendingOperation;
        var installed = setupService.Preview(ClientKind.Codex);
        Check(installed.CanApply && !installed.HasChanges, "One-click setup installs once and is idempotent");
        Check(File.ReadAllText(codexConfig).Contains("not-a-real-command", StringComparison.Ordinal), "One-click setup preserves the unrelated MCP server");
        Check(((TextBox)setupWindow.FindName("BackupsText")).Visibility == Visibility.Visible, "One-click setup displays backup locations");
        RenderOwnVisual(setupWindow, Path.Combine(output, "client-setup-installed.png"));
        var removed = setupService.Apply(setupService.Preview(ClientKind.Codex, SetupAction.Remove));
        Check(removed.Success && File.ReadAllText(codexConfig).Contains("not-a-real-command", StringComparison.Ordinal), "Removal only removes managed pet configuration");
        setupWindow.Close();

        _github.Changed -= OnGitHubChanged;
        _github.Dispose();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var handler = new FeaturesGitHubHandler(() => now);
        _github = new GitHubNotificationService(AppPaths.DataDirectory, new HttpClient(handler),
            new WindowsGitHubCredentialProtector(), () => now, automaticPolling: false);
        _github.Changed += OnGitHubChanged;
        OpenGitHubWindow();
        await Task.Delay(80);
        RenderOwnVisual(_githubWindow!, Path.Combine(output, "github-connect.png"));
        string fakeToken = "ghp_" + new string('x', 36);
        var connection = await _github.ConnectAsync(fakeToken);
        Check(connection.Success && _github.Login == "eagle-demo", "Account and notification permission validated through fake GET transport");
        await Task.Delay(100);
        Check(_github.Inbox.Count == 1 && _github.UnreadCount == 1 && _github.PeekAnnouncement() is null,
            "Initial unread inbox imported silently");
        var credential = File.ReadAllBytes(Path.Combine(AppPaths.DataDirectory, "github-credential.dpapi"));
        Check(!Encoding.UTF8.GetString(credential).Contains(fakeToken, StringComparison.Ordinal), "Saved token is not plaintext");
        ((Expander)_githubWindow!.FindName("ConnectionExpander")).IsExpanded = false;
        _githubWindow.Refresh();
        RenderOwnVisual(_githubWindow, Path.Combine(output, "github-inbox.png"));

        handler.IncludeNew = true;
        now = now.AddSeconds(61);
        await _github.PollNowAsync();
        await Task.Delay(120);
        TryShowGitHubNotice();
        Check(_github.UnreadCount == 2 && _activeNotice?.GitHubThreadId == "72", "New thread update reaches pet bubble and durable unread list");
        Check(_activeNotice!.GitHubLink?.AbsoluteUri == "https://github.com/eagle-demo/pet-demo/pull/42#issuecomment-4202", "GitHub notification carries validated PR comment destination");
        Check(GitHubUnreadBadge.Visibility == Visibility.Visible, "Desktop unread badge is visible");
        RenderOwnVisual(_bubble!, Path.Combine(output, "github-bubble.png"));
        RenderOwnVisual(Root, Path.Combine(output, "pet-unread.png"));
        RenderOwnVisual(_githubWindow, Path.Combine(output, "github-new-message.png"));
        int before = handler.RequestCount;
        await _github.PollNowAsync();
        Check(handler.RequestCount == before, "Manual refresh obeys minimum poll interval");
        DismissBubble();
        now = now.AddSeconds(61);
        await _github.PollNowAsync();
        await Task.Delay(100);
        Check(_github.UnreadCount == 2 && _github.PeekAnnouncement() is null && _activeNotice is null, "Duplicate update is not announced twice");
        _github.MarkAllSeen();
        await Task.Delay(80);
        Check(_github.UnreadCount == 0 && GitHubUnreadBadge.Visibility == Visibility.Collapsed && handler.WriteCount == 0,
            "Local seen state clears the badge without writing to GitHub");
        Check(!GitHubLinks.TryGetWebUri("https://github.com.evil.test/eagle-demo/pet-demo/pull/42", out _) &&
            !GitHubLinks.TryGetWebUri("file:///C:/Windows/notepad.exe", out _), "Untrusted launch destinations rejected");
        DisconnectGitHub();
        await Task.Delay(80);
        Check(!_github.IsConnected && _github.Inbox.Count == 0 && !File.Exists(Path.Combine(AppPaths.DataDirectory, "github-credential.dpapi")),
            "Disconnect removes this test credential and inbox");
        Check(_lastNotice?.GitHubThreadId is null, "Disconnect removes retained GitHub click destination");
        File.WriteAllText(Path.Combine(output, "gui-smoke.json"), JsonSerializer.Serialize(new
        {
            passed = true, featuresMode = true, checks, checkCount = checks.Count,
            dataDirectory = AppPaths.DataDirectory, fakeHttpOnly = true, networkWrites = handler.WriteCount, clientConfigHome = clientHome,
            limitations = "Real WPF and Windows DPAPI with fake HTTP; setup writes only an injected disposable client home. No actual AI clients configured, no real GitHub login/delivery, browser launch or manual display input tested."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class FeaturesGitHubHandler(Func<DateTimeOffset> now) : HttpMessageHandler
    {
        public bool IncludeNew { get; set; }
        public int RequestCount { get; private set; }
        public int WriteCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Method != HttpMethod.Get) WriteCount++;
            if (request.RequestUri?.Host != "api.github.com" || request.Method != HttpMethod.Get)
                throw new InvalidOperationException("Feature test forbids non-GitHub/non-GET transport.");
            string json;
            if (request.RequestUri.AbsolutePath == "/user") json = "{\"id\":9901,\"login\":\"eagle-demo\"}";
            else
            {
                object Item(string id, string number, string title, string reason) => new
                {
                    id, updated_at = "2026-09-06T08:00:00Z", reason,
                    repository = new { full_name = "eagle-demo/pet-demo" },
                    subject = new { title, type = "PullRequest", url = "https://api.github.com/repos/eagle-demo/pet-demo/pulls/" + number,
                        latest_comment_url = "https://api.github.com/repos/eagle-demo/pet-demo/issues/comments/" + number + "02" }
                };
                var items = new List<object> { Item("71", "41", "把小鹰的菜单做得更好看", "author") };
                if (IncludeNew) items.Insert(0, Item("72", "42", "有人在讨论里等你：看看这版通知气泡？", "mention"));
                json = JsonSerializer.Serialize(items);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("X-Poll-Interval", "60");
            response.Content.Headers.LastModified = now();
            return Task.FromResult(response);
        }
    }
}
