using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
            ("UI preserves both decimal places through purchase and refresh", UiDecimalBalance),
            ("UI purchase confirmation cancellation changes no progress", UiCancelPurchase),
            ("UI preview dispatch is read-only and does not buy or equip", UiPreview),
            ("Every real catalog entry has a frozen, transparent-cropped art icon", UiIcons),
            ("Shelf pagination exposes every item without a long vertical scroll", UiPages),
            ("Category and owned filters compose without modifying progress", UiFilters),
            ("Narrow and short windows retain reachable product details and paging", UiResponsive),
            ("440-wide shelf keeps long Chinese names, four equipment stats and maximum wallet reachable", UiEquipmentResponsive),
            ("Real modal purchase dialog shows installed art, price and balances without spending", PurchaseDialogContent),
            ("Premium equipment confirmation keeps long names, four stats and cent balances visible", PurchaseDialogEquipment),
            ("Real modal confirmation accepts only confirm; cancel, Escape and close return false", PurchaseDialogDecisions),
            ("Purchase dialog supports Tab focus and Enter while preserving safe initial cancel", PurchaseDialogKeyboard),
            ("Default shop modal rechecks resource availability after its message pump", UiModalRevalidation),
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
        Check(result.Success && result.Changed && f.State.Coins == 850m && f.State.Content.OwnedContentIds.Contains(ContentCatalog.MintDeskId), "Purchase not applied together.");
        Check(f.State.Content.Equipped[ContentSlot.Desk] == before, "Purchase auto-equipped the desk.");
        Check(f.State.AppliedWalletDebits[ContentCatalog.PurchaseTransactionId(ContentCatalog.MintDeskId)] == 150m, "Stable receipt missing.");
        var saved = new PetStore(f.Directory).Load()!;
        Check(saved.Coins == 850m && saved.Content.OwnedContentIds.Contains(ContentCatalog.MintDeskId), "Durable state differs from live state.");
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
        f.Available = false; Check(!f.Shop.Purchase(ContentCatalog.MintDeskId).Success && f.State.Coins == 1000m, "Stale quote authorized unavailable content.");
    }
    private static void FreeReward()
    {
        using var f = new Fixture(); f.State.TotalMeals = 30;
        Check(!f.Shop.Purchase(ContentCatalog.MintDeskId).Changed, "Eligible reward was sold.");
        Check(f.Shop.ClaimReward(ContentCatalog.MintDeskId).Changed && f.State.Coins == 1000m, "Reward cost coins or did not grant.");
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
        Check(!otherShop.Purchase(ContentCatalog.MidnightComputerId).Success && otherState.Coins == 1000m, "Stale writer overwrote another purchase.");
        Check(new PetStore(f.Directory).Load()!.Coins == 850m, "Stale writer changed durable balance.");
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
        Check(f.State.Coins == 850m && f.State.AppliedWalletDebits.Count == 1, "Re-equipping charged more coins.");
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
            Check(window.FilteredCount == ContentCatalog.Definitions.Count && items.Items.Count == 8 && window.PageCount == 3 && ((TextBlock)window.FindName("BalanceText")).Text == 1000m.ToString("N2"), "Shop did not expose its paged catalog and balance.");
            object tea = Card(items, ContentCatalog.TeaActionId);
            Check(Property<string>(tea, "Acquisition").Contains("2 级") && Property<string>(tea, "PriceLabel").Contains("120.00"), "Reward condition or price absent.");
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
    private static void UiDecimalBalance()
    {
        using var f = new Fixture(1000.05m); var window = Window(f);
        try
        {
            Layout(window);
            Check(((TextBlock)window.FindName("BalanceText")).Text == 1000.05m.ToString("N2"), "Initial cents were hidden.");
            Check(f.Shop.Purchase(ContentCatalog.MintDeskId).Changed, "Decimal wallet could not purchase.");
            window.Refresh(); Layout(window);
            Check(f.State.Coins == 850.05m && ((TextBlock)window.FindName("BalanceText")).Text == 850.05m.ToString("N2"),
                "Purchase or refresh lost cents.");
            Check(new PetStore(f.Directory).Load()!.Coins == 850.05m, "Serialized wallet lost cents.");
        }
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
    private static void UiIcons()
    {
        foreach (var item in ContentCatalog.Definitions)
        {
            var image = ShopThumbnails.Get(item) as BitmapSource;
            Check(image is { IsFrozen: true } && image.PixelWidth > 20 && image.PixelHeight > 20,
                "Missing or tiny real thumbnail: " + item.Id);
            Check(ReferenceEquals(image, ShopThumbnails.Get(item)), "Repeated refresh redecoded icon: " + item.Id);
        }
    }
    private static void UiPages()
    {
        using var f = new Fixture(); var window = Window(f); string before = Snapshot(f.State);
        try
        {
            Layout(window); var items = (ListBox)window.FindName("CardsItems");
            var ids = items.Items.Cast<object>().Select(x => Property<string>(x, "Id")).ToHashSet();
            for (int page = 1; page < window.PageCount; page++)
            {
                ((Button)window.FindName("NextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window);
                foreach (var item in items.Items.Cast<object>()) Check(ids.Add(Property<string>(item, "Id")), "Pages duplicate an item.");
            }
            Check(ids.SetEquals(ContentCatalog.Definitions.Select(x => x.Id)), "Pages hide a catalog item.");
            Check(!((Button)window.FindName("NextButton")).IsEnabled && ((Button)window.FindName("PreviousButton")).IsEnabled, "Page boundary controls incorrect.");
            Check(ScrollViewer.GetVerticalScrollBarVisibility(items) == ScrollBarVisibility.Disabled, "Shop reverted to a long-scroll list.");
            Check(window.SelectItem(ContentCatalog.HoodieOutfitId) && (string?)((Button)window.FindName("PreviewButton")).Tag == ContentCatalog.HoodieOutfitId, "Off-page item does not select into details.");
            Layout(window);
            Save(window, "shop-page-2.png"); Check(Snapshot(f.State) == before, "Browsing pages mutated progress.");
        }
        finally { window.Close(); }
    }
    private static void UiFilters()
    {
        using var f = new Fixture(); var window = Window(f); string before = Snapshot(f.State);
        try
        {
            Layout(window); ((RadioButton)window.FindName("DeskCategory")).IsChecked = true; Layout(window);
            Check(window.FilteredCount == 7 && ((ListBox)window.FindName("CardsItems")).Items.Cast<object>().All(x => Property<ContentType>(x, "Type") == ContentType.Desk), "Desk category filter incorrect.");
            Save(window, "shop-desks.png");
            window.SelectOwned(true); Layout(window);
            Check(window.FilteredCount == 1, "Owned and category filters do not compose.");
            ((RadioButton)window.FindName("ActionCategory")).IsChecked = true; Layout(window);
            Check(window.FilteredCount == 0 && ((TextBlock)window.FindName("EmptyText")).Visibility == Visibility.Visible &&
                ((Border)window.FindName("DetailsPanel")).Visibility == Visibility.Collapsed, "Empty collection retains a stale buy button.");
            Save(window, "shop-empty-collection.png"); Check(Snapshot(f.State) == before, "Filters mutated progress.");
        }
        finally { window.Close(); }
    }
    private static void UiResponsive()
    {
        using var f = new Fixture(); var window = Window(f);
        try
        {
            Layout(window, 424, 620);
            Check(window.GridColumns == 2 && window.PageCount >= 3, "Narrow window did not increase pages and reduce columns.");
            var surface = (FrameworkElement)window.Content;
            foreach (string name in new[] { "CardsItems", "DetailsPanel", "NextButton", "ActionButton", "PreviewButton" })
            {
                var element = (FrameworkElement)window.FindName(name);
                var rect = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
                Check(rect.X >= 0 && rect.Y >= 0 && rect.Right <= surface.ActualWidth + 1 && rect.Bottom <= surface.ActualHeight + 1,
                    "Responsive element clipped: " + name);
            }
            Save(window, "shop-narrow.png");
            Layout(window, 944, 560); Check(window.GridRows == 1, "Short work area did not reduce shelf rows.");
            Save(window, "shop-short.png");
            Layout(window); Check(window.GridColumns == 4 && window.GridRows == 2, "Restored size kept compact pagination.");
        }
        finally { window.Close(); }
    }
    private static void PurchaseDialogContent()
    {
        using var f = new Fixture(); string before = Snapshot(f.State), disk = File.ReadAllText(f.Path);
        bool? result = InPurchaseDialog(dialog =>
        {
            Check(((Image)dialog.FindName("ProductImage")).Source is BitmapSource, "Confirmation has no real product portrait.");
            Check(((TextBlock)dialog.FindName("ProductName")).Text == "薄荷小工位" &&
                ((TextBlock)dialog.FindName("ProductPrice")).Text.Contains("150.00 鹰币") &&
                ((TextBlock)dialog.FindName("CurrentBalance")).Text == $"{1000m:N2} 鹰币" &&
                ((TextBlock)dialog.FindName("RemainingBalance")).Text == $"{850m:N2} 鹰币", "Confirmation omitted exact item, price or balances.");
            Save(dialog, "shop-purchase-confirmation.png");
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Check(result != true && Snapshot(f.State) == before && File.ReadAllText(f.Path) == disk, "Merely asking the question mutated progress.");
        InPurchaseDialog(dialog =>
        {
            Check(!((Button)dialog.FindName("ConfirmButton")).IsEnabled &&
                ((TextBlock)dialog.FindName("RemainingBalance")).Text == "鹰币不足", "Insufficient balance remains confirmable.");
        }, balance: 0);
    }
    private static void UiEquipmentResponsive()
    {
        using var f = new Fixture(EconomyPolicy.MaximumCoins - 0.01m); var window = Window(f);
        string before = Snapshot(f.State), disk = File.ReadAllText(f.Path);
        try
        {
            foreach (string id in new[] { "computer.ultrabook", ContentCatalog.OfficeOutfitId, "desk.boardroom" })
            {
                // A 440-DIP decorated window has approximately 424 DIPs of client width.
                Layout(window, 424, 620); Check(window.SelectItem(id), "Premium item is not selectable: " + id);
                Layout(window, 424, 620);
                var item = ContentCatalog.Get(id); var surface = (FrameworkElement)window.Content;
                var details = (Border)window.FindName("DetailsPanel");
                var title = Descendants<TextBlock>(details).Single(x => x.Text == item.Name);
                Check(title.TextTrimming == TextTrimming.None, "Long item title was truncated in details: " + id);
                var effects = Descendants<TextBlock>(details).Single(x => x.Text == EquipmentPresentation.Describe(item.Bonuses));
                Check(effects.Text.Contains("赚钱") && effects.Text.Contains("工作经验"), "Work properties are missing: " + id);
                if (item.Type != ContentType.Computer)
                    Check(effects.Text.Contains("饱食衰减") && effects.Text.Contains("心情衰减"), "Four-stat details are incomplete: " + id);
                foreach (string name in new[] { "CardsItems", "DetailsPanel", "NextButton", "ActionButton", "PreviewButton", "BalanceText" })
                    AssertInside((FrameworkElement)window.FindName(name), surface, id + ":" + name);
                foreach (var text in Descendants<TextBlock>(details))
                    AssertInside(text, surface, id + ":" + text.Text);
                Check((string?)((Button)window.FindName("ActionButton")).Tag == id && ((Button)window.FindName("ActionButton")).IsEnabled,
                    "Premium equipment lost its reachable purchase action: " + id);
                Check(((TextBlock)window.FindName("BalanceText")).Text == f.State.Coins.ToString("N2"), "Large balance lost cents.");
                Save(window, "shop-440-" + id.Replace('.', '-') + ".png");
            }
            Check(window.SelectItem(ContentCatalog.OfficeOutfitId), "Office could not be restored for wide screenshot.");
            Layout(window); Save(window, "shop-premium-wide.png");
            Check(Snapshot(f.State) == before && File.ReadAllText(f.Path) == disk, "Responsive browsing changed wallet or ownership.");
        }
        finally { window.Close(); }
    }
    private static void PurchaseDialogEquipment()
    {
        using var f = new Fixture(EconomyPolicy.MaximumCoins - 0.01m);
        string before = Snapshot(f.State), disk = File.ReadAllText(f.Path);
        foreach (string id in new[] { "computer.ultrabook", ContentCatalog.OfficeOutfitId })
        {
            var item = ContentCatalog.Get(id);
            bool? result = InPurchaseDialog(dialog =>
            {
                var surface = (FrameworkElement)dialog.Content;
                Check(((Image)dialog.FindName("ProductImage")).Source is BitmapSource && ((Button)dialog.FindName("ConfirmButton")).IsEnabled,
                    "Premium confirmation lacks real art or explicit confirm: " + id);
                Check(((TextBlock)dialog.FindName("ProductName")).Text == item.Name &&
                    ((TextBlock)dialog.FindName("ProductPrice")).Text.Contains(item.Price.ToString("N2")) &&
                    ((TextBlock)dialog.FindName("CurrentBalance")).Text == $"{f.State.Coins:N2} 鹰币" &&
                    ((TextBlock)dialog.FindName("RemainingBalance")).Text == $"{f.State.Coins - item.Price:N2} 鹰币",
                    "Premium confirmation changed name, price or decimal balances: " + id);
                string note = ((TextBlock)dialog.FindName("PurchaseNote")).Text;
                Check(note.StartsWith(EquipmentPresentation.Describe(item.Bonuses), StringComparison.Ordinal), "Premium properties missing from confirmation: " + id);
                foreach (string name in new[] { "ProductImage", "ProductName", "ProductPrice", "CurrentBalance", "RemainingBalance", "PurchaseNote", "CancelButton", "ConfirmButton" })
                    AssertInside((FrameworkElement)dialog.FindName(name), surface, id + ":" + name);
                Save(dialog, "shop-confirm-" + id.Replace('.', '-') + ".png");
                ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, balance: f.State.Coins, item: item);
            Check(result != true, "Premium confirmation authorized purchase without consent.");
        }
        Check(Snapshot(f.State) == before && File.ReadAllText(f.Path) == disk, "Premium confirmation mutated saved progress.");
    }
    private static void AssertInside(FrameworkElement element, FrameworkElement surface, string context)
    {
        var rect = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
        Check(element.ActualWidth > 0 && element.ActualHeight > 0 && rect.X >= -1 && rect.Y >= -1 &&
            rect.Right <= surface.ActualWidth + 1 && rect.Bottom <= surface.ActualHeight + 1, "Layout element clipped: " + context);
    }
    private static void PurchaseDialogDecisions()
    {
        Check(InPurchaseDialog(dialog => ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))) == true, "Explicit confirm did not return true.");
        Check(InPurchaseDialog(dialog => ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))) != true, "Cancel authorized spending.");
        Check(InPurchaseDialog(dialog => dialog.Close()) != true, "Window close authorized spending.");
        Check(InPurchaseDialog(dialog => ((Button)dialog.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))) != true, "Title close authorized spending.");
        Check(InPurchaseDialog(dialog => SendDialogKey(dialog, Key.Escape)) != true, "Escape authorized spending.");
    }
    private static void PurchaseDialogKeyboard()
    {
        Check(InPurchaseDialog(dialog =>
        {
            var cancel = (Button)dialog.FindName("CancelButton"); var confirm = (Button)dialog.FindName("ConfirmButton");
            Check(cancel.IsKeyboardFocused, "Initial keyboard focus was not the safe cancel choice.");
            Check(cancel.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) && confirm.IsKeyboardFocused, "Tab traversal did not reach confirm.");
            SendDialogKey(dialog, Key.Enter);
        }) == true, "Enter on the explicit confirm choice failed.");
        Check(InPurchaseDialog(dialog => SendDialogKey(dialog, Key.Enter)) != true, "Enter on the initial cancel choice unexpectedly purchased.");
    }
    private static bool? InPurchaseDialog(Action<PurchaseConfirmationWindow> inspect, decimal balance = 1000m, ContentDefinition? item = null)
    {
        var owner = HiddenOwner(); owner.Show();
        var dialog = new PurchaseConfirmationWindow(item ?? ContentCatalog.Get(ContentCatalog.MintDeskId), balance) { Owner = owner };
        Exception? failure = null; bool callback = false;
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            try
            {
                callback = true;
                Check(ReferenceEquals(dialog.Owner, owner) && !IsWindowEnabled(new WindowInteropHelper(owner).Handle), "ShowDialog did not disable the correct owner.");
                dialog.UpdateLayout(); inspect(dialog);
            }
            catch (Exception exception) { failure = exception; }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }));
        try
        {
            bool? result = dialog.ShowDialog();
            Check(callback && IsWindowEnabled(new WindowInteropHelper(owner).Handle), "Modal callback did not run or owner stayed disabled.");
            if (failure is not null) throw failure;
            return result;
        }
        finally { if (dialog.IsVisible) dialog.Close(); owner.Close(); }
    }
    private static Window HiddenOwner() => new()
    {
        Width = 420, Height = 520, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual,
        WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
    };
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
    private static void SendDialogKey(PurchaseConfirmationWindow dialog, Key key) => dialog.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static void UiModalRevalidation()
    {
        using var f = new Fixture(); string before = Snapshot(f.State);
        var owner = HiddenOwner(); owner.Show();
        var window = new ShopWindow(f.Shop, id => Task.FromResult(f.Shop.Equip(id, false)), _ => Task.CompletedTask,
            _ => Task.CompletedTask, () => false) { Owner = owner, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        // Give the shop an HWND for ownership without displaying it on the user's desktop.
        new WindowInteropHelper(window).EnsureHandle();
        bool answered = false;
        try
        {
            Layout(window);
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<PurchaseConfirmationWindow>().Single();
                f.Available = false; answered = true;
                ((Button)dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            ClickCard(window, ContentCatalog.MintDeskId, preview: false);
            WaitUi(window.PendingOperation);
            Check(answered && Snapshot(f.State) == before && !f.State.Content.OwnedContentIds.Contains(ContentCatalog.MintDeskId),
                "Modal confirmation bypassed live resource revalidation.");
        }
        finally { window.Close(); owner.Close(); }
    }

    private static void HonorRewards()
    {
        using var f = new Fixture(); f.State.TotalMeals = 30; int opens = 0;
        var window = new HonorWallWindow(() => f.State, _ => opens++, id => f.Shop.ClaimReward(id).Message, f.Shop.IsAvailable);
        try
        {
            Layout(window, 840, 1200); var items = (ItemsControl)window.FindName("CardsItems");
            object card = items.Items.Cast<object>().Single(x => Property<string?>(x, "RewardContentId") == ContentCatalog.MintDeskId);
            Check(Property<string>(card, "RewardLabel").Contains("免费领取") && Property<bool>(card, "CanUseReward"), "Eligible honor does not offer its real reward.");
            var button = Descendants<Button>((DependencyObject)window.Content).Single(x => ReferenceEquals(x.DataContext, card) && (string?)x.Content == "领取奖励");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window, 840, 1200);
            card = items.Items.Cast<object>().Single(x => Property<string?>(x, "RewardContentId") == ContentCatalog.MintDeskId);
            Check(Property<string>(card, "RewardLabel").Contains("已拥有") && f.State.Coins == 1000m, "Honor failed to refresh ownership or charged coins.");
            var rewardIds = new HashSet<string>(StringComparer.Ordinal);
            for (int page = 0; page < 32; page++)
            {
                foreach (var visible in items.Items.Cast<object>())
                    if (Property<string?>(visible, "RewardContentId") is { } rewardId) rewardIds.Add(rewardId);
                var next = (Button)window.FindName("NextPageButton");
                if (!next.IsEnabled) break;
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window, 840, 1200);
            }
            Check(rewardIds.SetEquals(ContentCatalog.Definitions.Where(x => x.Reward is not null).Select(x => x.Id)), "Gallery omitted or invented reward mappings across pages.");
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
        Check(window.SelectItem(id), "Requested item is not present in the selected category.");
        Layout(window);
        var button = (Button)window.FindName(preview ? "PreviewButton" : "ActionButton");
        Check((string?)button.Tag == id && button.IsEnabled, "Selected item did not enable the expected detail action.");
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
    private static void Layout(Window window, double width = 944, double height = 681)
    {
        var surface = (FrameworkElement)window.Content;
        surface.Width = width; surface.Height = height;
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
        internal Fixture(decimal coins = 1000m)
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
