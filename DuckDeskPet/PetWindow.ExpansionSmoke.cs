using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DuckDeskPet.Core;
using DuckDeskPet.Integration;
using BridgeNotice = DuckDeskPet.Integration.PetNotification;

namespace DuckDeskPet;

public partial class PetWindow
{
    // Run only through the opt-in launcher. This is the REAL host, renderer,
    // controls, timers and store; no fake animation clock or fake care service.
    private async Task RunExpansionSmokeAsync(string output)
    {
        var results = new List<ExpansionCase>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(280));
        var elapsed = Stopwatch.StartNew();
        bool isolated = false;
        string? fatal = null;
        string reportPath = Path.Combine(output, "expansion-smoke.json");
        void Report() => File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            passed = fatal is null && results.All(x => x.Status is "passed" or "skipped") && results.Count >= 8,
            fullAcceptance = fatal is null && results.Count >= 8 && results.All(x => x.Status == "passed"),
            completed = fatal is not null || results.Count >= 8,
            mode = "expansion", elapsedSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 2),
            cases = results, error = fatal, dataDirectory = AppPaths.DataDirectory,
            realHost = true, realRenderClock = true, ownWindowRenderingOnly = true,
            limitations = new[]
            {
                "Shop purchase uses the real transaction coordinator; the human confirmation MessageBox is not automated.",
                "Runtime state is seeded only in the launcher's new disposable data directory; elapsed care/work simulation is allowed.",
                "No real GitHub/AI accounts, client configuration, task producer delivery, mouse dragging or physical 60 Hz certification.",
                "A skipped outfit case is not outfit acceptance. Screenshots alone do not certify animation aesthetics.",
                "The outfit case validates installed completeness and plays its Yawn; it does not visually inspect every outfit action.",
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
        async Task Case(string name, Func<Task> action)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            try { await action(); results.Add(new(name, "passed", watch.Elapsed.TotalSeconds, null)); }
            catch (Exception ex)
            {
                results.Add(new(name, "failed", watch.Elapsed.TotalSeconds, ex.ToString()));
                Report();
                throw;
            }
            Report();
        }
        try
        {
            ValidateExpansionIsolation(output);
            isolated = true;
            await Case("isolation, fresh seed and persistent opt-in", async () =>
            {
                ExpansionCheck(!_companion.AutoEmotionScenesEnabled && !_emotions.Enabled, "Automatic emotions must default to off in a fresh profile.");
                _settings.IsPaused = false; _behavior.SetPaused(false); _behavior.SuspendAutomatic(true);
                SetActiveBanter(false); SetNotifications(true); SetTaskNotificationsMuted(true);
                CareState.Coins = 500; CareState.Food = 12; CareState.FoodProgressSeconds = 0;
                CareState.Fullness = 75; CareState.Mood = 60;
                ExpansionCheck(_store.Save(CareState), "Cannot seed the isolated real store.");
                EmotionMenuItem.IsChecked = true; EmotionMenu_OnClick(EmotionMenuItem, new RoutedEventArgs(MenuItem.ClickEvent));
                await ExpansionWaitAsync(() => !_preparingEmotionAssets, timeout.Token, "emotion preloading");
                ExpansionCheck(CompanionPreferences.Load().AutoEmotionScenesEnabled, "Opt-in did not persist.");
                EmotionMenuItem.IsChecked = false; EmotionMenu_OnClick(EmotionMenuItem, new RoutedEventArgs(MenuItem.ClickEvent));
                ExpansionCheck(!CompanionPreferences.Load().AutoEmotionScenesEnabled, "Opt-out did not persist.");
            });

            await Case("menu, real shop preview, ownership and duplicate purchase", async () =>
            {
                PetMenu.PlacementTarget = PetVisual; PetMenu.IsOpen = true;
                await Task.Delay(100, timeout.Token);
                ExpansionCheck(PetMenu.Items.Contains(GameMenuItem) && PetMenu.Items.Contains(TaskInboxMenuItem), "New menu entries are missing.");
                RenderOwnVisual(PetMenu, Path.Combine(output, "expansion-menu.png")); PetMenu.IsOpen = false;
                var shopMenu = PetMenu.Items.OfType<MenuItem>().Single(x => x.Header?.ToString()?.Contains("小卖部", StringComparison.Ordinal) == true);
                shopMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                ExpansionCheck(_shopWindow is not null, "Shop menu did not open the actual window.");
                _shopWindow!.UpdateLayout();
                RenderOwnVisual(_shopWindow, Path.Combine(output, "expansion-shop-before.png"));
                string before = ExpansionContentFingerprint();
                var preview = ExpansionVisuals<Button>(_shopWindow).Single(x => x.Tag as string == ContentCatalog.MintDeskId && x.Content as string == "预览");
                ExpansionCheck(preview.IsEnabled, "Mint desk preview unavailable.");
                preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await _shopWindow.PendingOperation.WaitAsync(timeout.Token);
                ExpansionCheck(_contentPreviewWindow?.IsVisible == true, "Preview did not open its independent stage.");
                await Task.Delay(350, timeout.Token);
                ExpansionCheck(ExpansionContentFingerprint() == before, "Preview mutated currency, ownership, equipment or active outfit/scene.");
                RenderOwnVisual(_contentPreviewWindow!, Path.Combine(output, "expansion-preview.png"));
                _contentPreviewWindow!.Close();
                int coins = CareState.Coins;
                var purchase = _contentTransactions!.Purchase(ContentCatalog.MintDeskId);
                ExpansionCheck(purchase.Success && purchase.Changed && CareState.Coins == coins - 20, "First purchase did not atomically charge 20.");
                var duplicate = _contentTransactions.Purchase(ContentCatalog.MintDeskId);
                ExpansionCheck(!duplicate.Changed && CareState.Coins == coins - 20, "Duplicate purchase charged again.");
                ExpansionCheck(ContentOwnershipService.Owns(CareState.Content, ContentCatalog.MintDeskId), "Purchased desk was not owned.");
                var computer = _contentTransactions.Purchase(ContentCatalog.MidnightComputerId);
                ExpansionCheck(computer.Success && CareState.Coins == coins - 50, "Computer purchase failed.");
                _shopWindow.Refresh(); RenderOwnVisual(_shopWindow, Path.Combine(output, "expansion-shop-owned.png"));
                ExpansionVisuals<Button>(_shopWindow).Single(x => x.Content as string == "先逛到这").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ExpansionCheck(_shopWindow is null, "Shop did not detach on close.");
            });

            await Case("task inbox per-source ordering, terminal guard and privacy", async () =>
            {
                string secret = "isolated-task-body-" + Guid.NewGuid().ToString("N");
                BridgeNotice Notice(string source, string status, long revision) => new(source, $"{source}-{status}-{revision}", null,
                    secret, PetBridgeProtocol.TaskEventType(status)!, "smoke-task", revision, status, DateTimeOffset.UtcNow);
                ExpansionCheck(ReceiveNotice(Notice("Smoke AI", "running", 1)), "Running update rejected.");
                ExpansionCheck(ReceiveNotice(Notice("Smoke AI", "succeeded", 3)), "Success update rejected.");
                ReceiveNotice(Notice("Smoke AI", "running", 2)); ReceiveNotice(Notice("Smoke AI", "running", 4));
                ReceiveNotice(Notice("Other AI", "waiting", 1));
                ExpansionCheck(TaskNotificationItems.Count == 2 && TaskNotificationItems.Single(x => x.Source == "Smoke AI") is { Status: PetTaskStatus.Succeeded, Revision: 3 },
                    "Out-of-order or terminal update rolled back, or sources collided.");
                TaskInboxMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Task.Delay(100, timeout.Token);
                ExpansionCheck(_taskInboxWindow?.IsVisible == true, "Task inbox menu not wired.");
                ExpansionCheck(((ListBox)_taskInboxWindow!.FindName("TaskList")).Items.Count == 2, "Task inbox did not show both sources.");
                RenderOwnVisual(_taskInboxWindow!, Path.Combine(output, "expansion-task-inbox.png"));
                ((Button)_taskInboxWindow.FindName("ReadAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ExpansionCheck(TaskUnreadCount == 0, "Mark read failed.");
                ((Button)_taskInboxWindow.FindName("ClearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ExpansionCheck(TaskNotificationItems.Count == 0, "Clear did not hide task bodies.");
                ExpansionCheck(_store.Save(CareState), "Isolated save failed after task notices.");
                ExpansionCheck(!Directory.EnumerateFiles(AppPaths.DataDirectory, "*.json").Any(file => File.ReadAllText(file).Contains(secret, StringComparison.Ordinal)),
                    "Task body leaked into persistent JSON.");
                _taskInboxWindow!.Close();
            });

            await Case("third touch plays Annoyed without another care reward", async () =>
            {
                await ExpansionIdleAsync(timeout.Token);
                await _framePlayer.WarmClipAsync(ClipKind.Annoyed, timeout.Token);
                int pets = CareState.TotalPets, xp = CareState.Experience;
                long sequence = _behavior.CurrentSample.Sequence;
                PetHead(); PetHead(); PetHead();
                await ExpansionObserveClipAsync(ClipKind.Annoyed, sequence, output, "expansion-annoyed.png", timeout.Token);
                ExpansionCheck(CareState.TotalPets == pets + 1 && CareState.Experience == xp + 1,
                    "Repeated touches gave extra care count or XP beyond the first allowed touch.");
            });

            await Case("hungry scene exits before one meal and complete Eat", async () =>
            {
                await ExpansionIdleAsync(timeout.Token);
                CareState.Fullness = 10;
                EmotionMenuItem.IsChecked = true; EmotionMenu_OnClick(EmotionMenuItem, new RoutedEventArgs(MenuItem.ClickEvent));
                await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.HungryLoop, timeout.Token, "HungryLoop");
                RenderOwnVisual(Root, Path.Combine(output, "expansion-hungry.png"));
                int food = CareState.Food, meals = CareState.TotalMeals;
                long sequence = _behavior.CurrentSample.Sequence;
                FeedPet(); FeedPet(); FeedPet();
                ExpansionCheck(CareState.Food == food && _pendingFeed, "Food was consumed before the hungry stage left.");
                await ExpansionObserveClipAsync(ClipKind.Eat, sequence, output, "expansion-eating.png", timeout.Token);
                ExpansionCheck(CareState.Food == food - 1 && CareState.TotalMeals == meals + 1 && !_pendingFeed,
                    "Repeated feed requests consumed more than one meal.");
                EmotionMenuItem.IsChecked = false; EmotionMenu_OnClick(EmotionMenuItem, new RoutedEventArgs(MenuItem.ClickEvent));
            });

            await Case("work holds desk and computer until full exit, then applies pending", async () =>
            {
                await ExpansionIdleAsync(timeout.Token);
                CareState.Fullness = 80;
                await StartWorkingAsync().WaitAsync(timeout.Token);
                await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.WorkLoop, timeout.Token, "WorkLoop");
                SceneSelection oldScene = _workStage.ActiveSelection;
                int coins = CareState.Coins;
                var desk = await EquipContentAsync(ContentCatalog.MintDeskId).WaitAsync(timeout.Token);
                var computer = await EquipContentAsync(ContentCatalog.MidnightComputerId).WaitAsync(timeout.Token);
                ExpansionCheck(desk.Success && computer.Success && CareState.Content.PendingEquipment.Count == 2 && _workStage.ActiveSelection == oldScene,
                    "Working equipment did not remain pending.");
                RenderOwnVisual(Root, Path.Combine(output, "expansion-working-before-swap.png"));
                StopWorking();
                await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.WorkExit, timeout.Token, "WorkExit");
                ExpansionCheck(_workStage.ActiveSelection == oldScene, "Desk/computer changed in the middle of the exit.");
                await ExpansionWaitAsync(() => !WorkInProgress && CareState.Content.PendingEquipment.Count == 0 && !IsContentEquipmentApplying &&
                    _workStage.ActiveSelection.DeskId == ContentCatalog.MintDeskId && _workStage.ActiveSelection.ComputerId == ContentCatalog.MidnightComputerId,
                    timeout.Token, "pending equipment commit");
                ExpansionCheck(CareState.Coins >= coins, "Equipping already-owned props charged again; legitimate elapsed wages are allowed.");
                await StartWorkingAsync().WaitAsync(timeout.Token);
                await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.WorkLoop, timeout.Token, "new workplace WorkLoop");
                RenderOwnVisual(Root, Path.Combine(output, "expansion-working-new-props.png"));
                StopWorking(); await ExpansionIdleAsync(timeout.Token);
            });

            await Case("real guessing game reveals after throw and saves one cooldown reward", async () =>
            {
                await ExpansionIdleAsync(timeout.Token);
                CareState.Mood = 50; CareState.LastGameRewardUtc = null;
                ExpansionCheck(_store.Save(CareState), "Could not seed isolated game reward state.");
                GameMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await _gameOpening.WaitAsync(timeout.Token);
                ExpansionCheck(_gameWindow?.IsVisible == true && IsGameActive, "Game menu did not open a real exclusive game.");
                await ExpansionRpsRoundAsync(output, "first", timeout.Token);
                var firstReward = CareState.LastGameRewardUtc;
                ExpansionCheck(firstReward is not null && CareState.Mood > 51.9 && CareState.Mood <= 52.1, "Completed game did not add exactly its small mood reward.");
                var saved = new PetStore(AppPaths.DataDirectory).Load();
                ExpansionCheck(saved is not null && saved.LastGameRewardUtc == firstReward && saved.Mood > 51.9, "Cooldown and mood were not durable together.");
                ((Button)_gameWindow!.FindName("ReplayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await ExpansionRpsRoundAsync(output, "cooldown", timeout.Token);
                ExpansionCheck(CareState.LastGameRewardUtc == firstReward && CareState.Mood <= 52.1, "Immediate replay bypassed the persisted cooldown.");
                _gameWindow!.Close();
                await ExpansionWaitAsync(() => !IsGameActive, timeout.Token, "game complete close");
            });

            if (Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_SKIP_OUTFIT") == "1")
            {
                results.Add(new("complete office outfit acquisition, preview, switch and return", "skipped", 0,
                    "Explicit EAGLE_PET_SMOKE_SKIP_OUTFIT=1; no outfit acceptance claimed.")); Report();
            }
            else await Case("complete office outfit acquisition, preview, switch and return", async () =>
            {
                await ExpansionIdleAsync(timeout.Token);
                ExpansionCheck(IsContentResourceAvailable(ContentCatalog.Get(ContentCatalog.OfficeOutfitId)), "Complete outfit assets are not available; use explicit skip only for an interim run.");
                string before = ExpansionContentFingerprint();
                await PreviewContentAsync(ContentCatalog.OfficeOutfitId).WaitAsync(timeout.Token);
                await Task.Delay(250, timeout.Token);
                ExpansionCheck(before == ExpansionContentFingerprint(), "Outfit preview changed real ownership or appearance.");
                RenderOwnVisual(_contentPreviewWindow!, Path.Combine(output, "expansion-outfit-preview.png")); _contentPreviewWindow!.Close();
                var purchase = _contentTransactions!.Purchase(ContentCatalog.OfficeOutfitId);
                ExpansionCheck(purchase.Success, "Office outfit purchase failed.");
                var equip = await EquipContentAsync(ContentCatalog.OfficeOutfitId).WaitAsync(timeout.Token);
                ExpansionCheck(equip.Success, "Office outfit equip failed.");
                await ExpansionWaitAsync(() => _framePlayer.CurrentOutfitId == ContentCatalog.OfficeOutfitId, timeout.Token, "office outfit switch");
                RenderOwnVisual(Root, Path.Combine(output, "expansion-outfit-idle.png"));
                await _framePlayer.WarmClipAsync(ClipKind.Yawn, timeout.Token);
                long sequence = _behavior.CurrentSample.Sequence;
                _behavior.RequestAction(ClipKind.Yawn);
                await ExpansionObserveClipAsync(ClipKind.Yawn, sequence, output, "expansion-outfit-yawn.png", timeout.Token);
                await EquipContentAsync(ContentCatalog.DefaultOutfitId).WaitAsync(timeout.Token);
                await ExpansionWaitAsync(() => _framePlayer.CurrentOutfitId == ContentCatalog.DefaultOutfitId, timeout.Token, "default outfit restore");
            });
        }
        catch (Exception ex) { fatal = ex.ToString(); }
        finally
        {
            Report();
            if (isolated)
            {
                // Normal authored teardown, with a separate bounded budget. The
                // launcher terminates only this child PID at its 300 s hard cap.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    PetMenu.IsOpen = false; _contentPreviewWindow?.Close(); _shopWindow?.Close(); _taskInboxWindow?.Close();
                    _emotions.Enabled = false; _pendingFeed = false; _behavior.CancelAutomaticEmotions();
                    if (CareState.IsWorking) StopWorking();
                    await CancelGameForPauseAsync().WaitAsync(cleanup.Token);
                    _gameWindow?.Close();
                    await ExpansionIdleAsync(cleanup.Token);
                }
                catch (Exception ex)
                {
                    fatal ??= "Safe teardown did not complete: " + ex.Message;
                    Report();
                }
            }
        }
    }

    private static void ValidateExpansionIsolation(string output)
    {
        string runRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("EAGLE_PET_SMOKE_RUN_ROOT") ?? throw new InvalidOperationException("Missing disposable run root."));
        string channel = Environment.GetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL") ?? "";
        string parent = Path.GetDirectoryName(runRoot)!;
        ExpansionCheck(string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(runRoot).StartsWith("EagleExpansionSmoke-", StringComparison.Ordinal) &&
            string.Equals(Path.GetFullPath(AppPaths.DataDirectory), Path.Combine(runRoot, "data"), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(output), Path.Combine(runRoot, "evidence"), StringComparison.OrdinalIgnoreCase), "Smoke state/output must be in its new launcher-created temp root.");
        ExpansionCheck(channel.Length > 0 && File.ReadAllText(Path.Combine(runRoot, "expansion-smoke.sentinel")) == channel,
            "Missing/mismatched isolation sentinel.");
        foreach (string path in new[] { runRoot, AppPaths.DataDirectory, output })
            ExpansionCheck((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Smoke directories cannot be links.");
    }

    private static void ExpansionCheck(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private async Task ExpansionWaitAsync(Func<bool> condition, CancellationToken token, string label, double seconds = 35)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            if (_isClosing || clock.Elapsed.TotalSeconds > seconds)
                throw new TimeoutException($"Waiting for {label}; current {_behavior.CurrentSample.Kind}/{_behavior.CurrentSample.Sequence}, pending {_behavior.PendingCount}, status {CareStatus}.");
            await Task.Delay(20, token);
        }
    }
    private Task ExpansionIdleAsync(CancellationToken token) => ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.Idle &&
        _behavior.PendingCount == 0 && !WorkInProgress && !_behavior.IsHungrySceneActive && !_behavior.IsHungryRequested && !IsContentEquipmentApplying,
        token, "complete idle boundary");

    private async Task ExpansionObserveClipAsync(ClipKind kind, long afterSequence, string output, string imageName, CancellationToken token)
    {
        await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == kind && _behavior.CurrentSample.Sequence > afterSequence, token, kind + " start");
        long sequence = _behavior.CurrentSample.Sequence;
        await ExpansionWaitAsync(() => _behavior.CurrentSample.Sequence == sequence && _behavior.CurrentSample.Progress >= .45, token, kind + " midpoint");
        RenderOwnVisual(Root, Path.Combine(output, imageName));
        await ExpansionWaitAsync(() => _behavior.CurrentSample.Sequence == sequence && _behavior.CurrentSample.Progress >= .90, token, kind + " ending");
        await ExpansionWaitAsync(() => _behavior.CurrentSample.Kind == ClipKind.Idle && _behavior.CurrentSample.Sequence == sequence, token, kind + " full completion");
    }

    private async Task ExpansionRpsRoundAsync(string output, string suffix, CancellationToken token)
    {
        var window = _gameWindow ?? throw new InvalidOperationException("Game window missing.");
        long before = _behavior.CurrentSample.Sequence;
        ((Button)window.FindName("RockButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ExpansionWaitAsync(() => _behavior.CurrentSample.Sequence > before && _behavior.CurrentSample.Kind is ClipKind.RpsRock or ClipKind.RpsPaper or ClipKind.RpsScissors,
            token, "actual RPS throw");
        ExpansionCheck(((TextBlock)window.FindName("StageText")).Text == "出拳！" && !_gameThrown, "Result was disclosed before the throw animation finished.");
        int food = CareState.Food, pets = CareState.TotalPets;
        FeedPet(); PetHead(); await StartWorkingAsync();
        ExpansionCheck(CareState.Food == food && CareState.TotalPets == pets && !CareState.IsWorking, "Game exclusivity let another interaction mutate care/work.");
        long throwingSequence = _behavior.CurrentSample.Sequence;
        await ExpansionWaitAsync(() => _behavior.CurrentSample.Sequence == throwingSequence && _behavior.CurrentSample.Progress >= .45,
            token, "RPS throw midpoint");
        ExpansionCheck(((TextBlock)window.FindName("StageText")).Text == "出拳！" && !_gameThrown, "Mid-throw result was revealed prematurely.");
        RenderOwnVisual(Root, Path.Combine(output, $"expansion-rps-{suffix}-throw.png"));
        RenderOwnVisual(window, Path.Combine(output, $"expansion-rps-{suffix}-waiting.png"));
        await ExpansionWaitAsync(() => ((Button)window.FindName("ReplayButton")).IsEnabled, token, "RPS complete result", 25);
        ExpansionCheck(_gameReactionComplete && _behavior.CurrentSample.Kind == ClipKind.Idle, "RPS enabled replay before complete reaction.");
        RenderOwnVisual(window, Path.Combine(output, $"expansion-rps-{suffix}-result.png"));
    }

    private string ExpansionContentFingerprint() => JsonSerializer.Serialize(new
    {
        CareState.Coins, CareState.Content, CareState.AppliedWalletDebits, CareState.LastGameRewardUtc,
        outfit = _framePlayer.CurrentOutfitId, scene = _workStage.ActiveSelection,
    });

    private static IEnumerable<T> ExpansionVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in ExpansionVisuals<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private sealed record ExpansionCase(string Name, string Status, double Seconds, string? Detail);
}
