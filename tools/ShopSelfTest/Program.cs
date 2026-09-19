using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    private static string? _output;
    [STAThread]
    private static int Main(string[] args)
    {
        _output = args.Length == 0 ? null : Path.GetFullPath(args[0]);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var cases = new (string Name, Action Body)[]
        {
            ("Purchase atomically saves debit, stable receipt and ownership, without equipping", Purchase),
            ("Repeated purchase never debits an already-owned permanent item", DuplicatePurchase),
            ("Insufficient coins leave both memory and disk unchanged", InsufficientCoins),
            ("Missing content resources are never sold", MissingResources),
            ("Resources are rechecked after a displayed quote", ChangedResources),
            ("Reward-eligible content is free, not sold again", FreeReward),
            ("Unavailable reward art still preserves its earned entitlement", MissingRewardArt),
            ("Failed purchase persistence does not publish ownership or debit", FailedPurchaseSave),
            ("Failed equipment persistence does not publish a new choice", FailedEquipSave),
            ("A second stale writer cannot double-spend or replace the first save", StaleWriter),
            ("Work-time equipment queues, and latest per-slot selection wins", DeferredEquipment),
            ("Returning to the current item cancels a queued choice", CancelPending),
            ("Invalid slot combinations are rejected before saving", IncompatibleEquipment),
            ("Owned equipment can be swapped repeatedly without a second payment", FreeReequip),
            ("UI shows balance, prices, conditions, owned and unavailable states", UiStates),
            ("UI purchase confirmation cancellation changes no progress", UiCancelPurchase),
            ("UI preview dispatch is read-only and does not buy or equip", UiPreview),
            ("Honor wall maps real rewards and refreshes ownership without progress changes", HonorRewards),
            ("Opening the honor wall never claims a reward automatically", HonorReadOnly),
            ("Actual isolated preview loads the production workplace without save changes", RealPreview),
            ("Unavailable preview fails explicitly without producing a fake playable image", UnavailablePreview),
        };
        int failures = 0;
        try
        {
            foreach (var (name, body) in cases)
            {
                try { body(); Console.WriteLine("PASS  " + name); }
                catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL  " + name + "\n" + exception); }
            }
        }
        finally { app.Shutdown(); }
        Console.WriteLine($"Shop transactions and WPF: {cases.Length - failures}/{cases.Length} passed; all save tests used newly created isolated temp directories.");
        return failures == 0 ? 0 : 1;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Snapshot(PetState state) => JsonSerializer.Serialize(state);
    private static void Purchase()
    {
        using var f = new Fixture(); var before = f.State.Content.Equipped[ContentSlot.Desk];
        var result = f.Shop.Purchase(ContentCatalog.MintDeskId);
        Check(result.Success && result.Changed && f.State.Coins == 80 && f.State.Content.OwnedContentIds.Contains(ContentCatalog.MintDeskId), "Purchase not applied together.");
        Check(f.State.Content.Equipped[ContentSlot.Desk] == before, "Purchase auto-equipped the desk.");
        Check(f.State.AppliedWalletDebits[ContentCatalog.PurchaseTransactionId(ContentCatalog.MintDeskId)] == 20, "Stable receipt missing.");
        var saved = new PetStore(f.Directory).Load()!;
        Check(saved.Coins == 80 && saved.Content.OwnedContentIds.Contains(ContentCatalog.MintDeskId), "Durable state differs from live state.");
    }
    private static void DuplicatePurchase()
    {
        using var f = new Fixture(); f.Shop.Purchase(ContentCatalog.MintDeskId); string before = Snapshot(f.State);
        f.Shop.Purchase(ContentCatalog.MintDeskId); Check(before == Snapshot(f.State), "Permanent purchase debited twice.");
    }
    private static void InsufficientCoins()
    {
        using var f = new Fixture(0); string before = Snapshot(f.State), disk = File.ReadAllText(f.Path);
        Check(!f.Shop.Purchase(ContentCatalog.MintDeskId).Success && Snapshot(f.State) == before && File.ReadAllText(f.Path) == disk, "Insufficient funds changed state.");
    }
    private static void MissingResources()
    {
        using var f = new Fixture(); f.Available = false; string before = Snapshot(f.State);
        Check(!f.Shop.Purchase(ContentCatalog.MidnightComputerId).Success && Snapshot(f.State) == before, "Unavailable resources were sold.");
    }
    private static void ChangedResources()
    {
        using var f = new Fixture(); Check(f.Shop.Quote(ContentCatalog.MintDeskId).CanPurchase, "Expected a quote.");
        f.Available = false; Check(!f.Shop.Purchase(ContentCatalog.MintDeskId).Success && f.State.Coins == 100, "Stale quote authorized unavailable content.");
    }
    private static void FreeReward()
    {
        using var f = new Fixture(); f.State.TotalMeals = 10;
        Check(!f.Shop.Purchase(ContentCatalog.MintDeskId).Changed, "Eligible reward was sold.");
        Check(f.Shop.ClaimReward(ContentCatalog.MintDeskId).Changed && f.State.Coins == 100, "Reward cost coins or did not grant.");
        Check(!f.Shop.ClaimReward(ContentCatalog.MintDeskId).Changed && f.State.AppliedWalletDebits.Count == 0, "Repeat reward created a receipt.");
    }
    private static void MissingRewardArt()
    {
        using var f = new Fixture(); f.State.Experience = 100; f.Available = false;
        Check(f.Shop.ClaimReward(ContentCatalog.TeaActionId).Changed && f.State.Content.OwnedContentIds.Contains(ContentCatalog.TeaActionId), "Missing art erased earned entitlement.");
        Check(!ContentOwnershipService.CanPlayAction(f.State.Content, ContentCatalog.TeaActionId, f.Shop.IsAvailable).CanPlay, "Unavailable action was playable.");
    }
    private static void FailedPurchaseSave()
    {
        using var f = new Fixture(); string before = Snapshot(f.State);
        using var locked = new FileStream(f.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(!f.Shop.Purchase(ContentCatalog.MintDeskId).Success && Snapshot(f.State) == before, "Failed save published purchase.");
    }
    private static void FailedEquipSave()
    {
        using var f = new Fixture(); f.Shop.Purchase(ContentCatalog.MintDeskId); string before = Snapshot(f.State);
        using var locked = new FileStream(f.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(!f.Shop.Equip(ContentCatalog.MintDeskId, false).Success && Snapshot(f.State) == before, "Failed save published equipment.");
    }
    private static void StaleWriter()
    {
        using var f = new Fixture(); var otherStore = new PetStore(f.Directory); var otherState = otherStore.Load()!;
        var otherShop = new ShopTransactions(otherStore, () => otherState, _ => true);
        Check(f.Shop.Purchase(ContentCatalog.MintDeskId).Changed, "First writer failed.");
        Check(!otherShop.Purchase(ContentCatalog.MidnightComputerId).Success && otherState.Coins == 100, "Stale writer overwrote another purchase.");
        Check(new PetStore(f.Directory).Load()!.Coins == 80, "Stale writer changed durable balance.");
    }
    private static void DeferredEquipment()
    {
        using var f = new Fixture(); f.Shop.Purchase(ContentCatalog.MintDeskId); f.Shop.Purchase(ContentCatalog.MidnightComputerId);
        Check(f.Shop.Equip(ContentCatalog.MintDeskId, true).Changed && f.Shop.Equip(ContentCatalog.MidnightComputerId, true).Changed, "Selections were not queued.");
        Check(f.State.Content.Equipped[ContentSlot.Desk] == ContentCatalog.DefaultDeskId && f.State.Content.PendingEquipment.Count == 2, "Working scene was hot-swapped.");
        Check(f.Shop.ApplyPending().Changed && f.State.Content.Equipped[ContentSlot.Desk] == ContentCatalog.MintDeskId &&
            f.State.Content.Equipped[ContentSlot.Computer] == ContentCatalog.MidnightComputerId && f.State.Content.PendingEquipment.Count == 0, "Boundary commit was incomplete.");
    }
    private static void CancelPending()
    {
        using var f = new Fixture(); f.Shop.Purchase(ContentCatalog.MintDeskId); f.Shop.Equip(ContentCatalog.MintDeskId, true);
        Check(f.Shop.Equip(ContentCatalog.DefaultDeskId, true).Changed && f.State.Content.PendingEquipment.Count == 0, "Current selection did not cancel future swap.");
    }
    private static void IncompatibleEquipment()
    {
        using var f = new Fixture(); f.Shop.Purchase(ContentCatalog.MintDeskId); f.Compatible = false; string before = Snapshot(f.State);
        Check(!f.Shop.Equip(ContentCatalog.MintDeskId, false).Success && Snapshot(f.State) == before, "Incompatible set was committed.");
    }
    private static void FreeReequip()
    {
        using var f = new Fixture(); f.Shop.Purchase(ContentCatalog.MintDeskId);
        for (int i = 0; i < 3; i++) { f.Shop.Equip(ContentCatalog.MintDeskId, false); f.Shop.Equip(ContentCatalog.DefaultDeskId, false); }
        Check(f.State.Coins == 80 && f.State.AppliedWalletDebits.Count == 1, "Re-equipping charged more coins.");
    }

    private static ShopWindow Window(Fixture f, bool confirm = true, Func<string, Task>? preview = null) => new(f.Shop,
        id => Task.FromResult(f.Shop.Equip(id, false)), preview ?? (_ => Task.CompletedTask), _ => Task.CompletedTask,
        () => false, confirmPurchase: _ => confirm);
    private static void UiStates()
    {
        using var f = new Fixture(); var window = Window(f);
        try
        {
            Layout(window); var items = (ItemsControl)window.FindName("CardsItems");
            Check(items.Items.Count == ContentCatalog.Definitions.Count && ((TextBlock)window.FindName("BalanceText")).Text == "100", "Shop did not expose its catalog and balance.");
            object tea = Card(items, ContentCatalog.TeaActionId);
            Check(Property<string>(tea, "Acquisition").Contains("2 级") && Property<string>(tea, "PriceLabel").Contains("15"), "Reward condition or price absent.");
            Save(window, "shop-all.png");
            f.Available = false; window.Refresh(); Layout(window);
            object computer = Card(items, ContentCatalog.MidnightComputerId);
            Check(!Property<bool>(computer, "CanAct") && !Property<bool>(computer, "CanPreview") && Property<string>(computer, "Status").Contains("暂不售卖"), "Unavailable item was actionable.");
            f.Available = true; window.SelectOwned(true); Layout(window);
            Check(items.Items.Count == 3, "Owned filter includes unowned content."); Save(window, "shop-owned.png");
        }
        finally { window.Close(); }
    }
    private static void UiCancelPurchase()
    {
        using var f = new Fixture(); var window = Window(f, confirm: false); string before = Snapshot(f.State);
        try { Layout(window); ClickCard(window, ContentCatalog.MintDeskId, preview: false); window.PendingOperation.GetAwaiter().GetResult(); Check(Snapshot(f.State) == before, "Cancelled confirmation bought content."); }
        finally { window.Close(); }
    }
    private static void UiPreview()
    {
        using var f = new Fixture(); int previews = 0; var window = Window(f, preview: _ => { previews++; return Task.CompletedTask; });
        string before = Snapshot(f.State), disk = File.ReadAllText(f.Path);
        try
        {
            Layout(window); ClickCard(window, ContentCatalog.MintDeskId, preview: true); window.PendingOperation.GetAwaiter().GetResult();
            Check(previews == 1 && Snapshot(f.State) == before && File.ReadAllText(f.Path) == disk, "Preview mutated care, ownership, equipment or balance.");
        }
        finally { window.Close(); }
    }
    private static void HonorRewards()
    {
        using var f = new Fixture(); f.State.TotalMeals = 10; int opens = 0;
        var window = new HonorWallWindow(() => f.State, _ => opens++, id => f.Shop.ClaimReward(id).Message, f.Shop.IsAvailable);
        try
        {
            Layout(window, 840, 1200); var items = (ItemsControl)window.FindName("CardsItems");
            object card = items.Items.Cast<object>().Single(x => Property<string?>(x, "RewardContentId") == ContentCatalog.MintDeskId);
            Check(Property<string>(card, "RewardLabel").Contains("免费领取") && Property<bool>(card, "CanUseReward"), "Eligible honor does not offer its real reward.");
            var button = Descendants<Button>((DependencyObject)window.Content).Single(x => ReferenceEquals(x.DataContext, card) && (string?)x.Content == "领取奖励");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window, 840, 1200);
            card = items.Items.Cast<object>().Single(x => Property<string?>(x, "RewardContentId") == ContentCatalog.MintDeskId);
            Check(Property<string>(card, "RewardLabel").Contains("已拥有") && f.State.Coins == 100, "Honor failed to refresh ownership or charged coins.");
            Check(items.Items.Cast<object>().Count(x => Property<string?>(x, "RewardContentId") is not null) == 2, "Gallery invented unrelated rewards.");
            ((Button)window.FindName("CollectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(opens == 1, "Collection shortcut did not dispatch.");
            Save(window, "honor-rewards.png");
        }
        finally { window.Close(); }
    }
    private static void HonorReadOnly()
    {
        using var f = new Fixture(); f.State.Experience = 100; string before = Snapshot(f.State); var window = new HonorWallWindow(() => f.State);
        try { window.Refresh(); Check(Snapshot(f.State) == before, "Opening gallery granted a reward."); }
        finally { window.Close(); }
    }
    private static void RealPreview()
    {
        using var f = new Fixture(); string before = Snapshot(f.State), disk = File.ReadAllText(f.Path);
        var window = new ContentPreviewWindow(ContentCatalog.Get(ContentCatalog.DefaultDeskId),
            new("work.default", "desk.default", "computer.default"), _ => true);
        try
        {
            WaitUi(window.PrepareAsync()); Layout(window, 360, 405);
            Check(Descendants<Image>((DependencyObject)window.Content).Count(x => x.Source is not null) >= 4, "Actual preview lacks the eagle or complete prop set.");
            Check(Snapshot(f.State) == before && File.ReadAllText(f.Path) == disk, "Actual preview changed persistent progress.");
            Check(!(bool)typeof(ContentPreviewWindow).GetField("_subscribed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!,
                "Hidden preview is consuming a rendering clock.");
            Check(Descendants<Viewbox>((DependencyObject)window.Content).Single().ActualHeight > 0, "Preview lost its responsive stage.");
            Save(window, "preview-default-workplace.png");
        }
        finally { window.Close(); }
    }
    private static void UnavailablePreview()
    {
        var window = new ContentPreviewWindow(ContentCatalog.Get(ContentCatalog.MintDeskId),
            new("work.default", "desk.default", "computer.default"), _ => false);
        try
        {
            bool rejected = false; try { WaitUi(window.PrepareAsync()); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && Descendants<TextBlock>((DependencyObject)window.Content).Any(x => x.Text.Contains("素材暂未完整加载")), "Unavailable preview silently reported success.");
        }
        finally { window.Close(); }
    }

    private static T Property<T>(object item, string name) => (T)item.GetType().GetProperty(name)!.GetValue(item)!;
    private static object Card(ItemsControl items, string id) => items.Items.Cast<object>().Single(x => Property<string>(x, "Id") == id);
    private static void ClickCard(ShopWindow window, string id, bool preview)
    {
        var button = Descendants<Button>((DependencyObject)window.Content).Single(x => (string?)x.Tag == id && ((string?)x.Content == "预览") == preview);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Layout(Window window, double width = 740, double height = 900)
    {
        var surface = (FrameworkElement)window.Content;
        surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
    }
    private static void Save(Window window, string name)
    {
        if (_output is null) return;
        System.IO.Directory.CreateDirectory(_output); var surface = (FrameworkElement)window.Content;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(_output, name)); encoder.Save(stream);
    }
    private static void WaitUi(Task task)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (deadline.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Preview did not finish preparing.");
        }
        task.GetAwaiter().GetResult();
    }
    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EagleDeskPet-ShopSelfTest-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "pet-state.json");
        internal PetState State { get; }
        internal PetStore Store { get; }
        internal ShopTransactions Shop { get; }
        internal bool Available = true, Compatible = true;
        internal Fixture(int coins = 100)
        {
            State = new() { Coins = coins, LastUpdatedUtc = DateTimeOffset.UtcNow, WageLastUpdatedUtc = DateTimeOffset.UtcNow };
            Store = new(Directory); Check(Store.Load() is null && Store.Save(State), "Could not create isolated save.");
            Shop = new(Store, () => State, item => item.IsDefault || Available, _ => Compatible);
        }
        public void Dispose()
        {
            string full = System.IO.Path.GetFullPath(Directory);
            string expectedParent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (System.IO.Path.GetDirectoryName(full) != expectedParent || !System.IO.Path.GetFileName(full).StartsWith("EagleDeskPet-ShopSelfTest-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a path outside this test's generated temp directory.");
            if (System.IO.Directory.Exists(full)) System.IO.Directory.Delete(full, recursive: true);
        }
    }
}
