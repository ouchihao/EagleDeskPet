using System.Text.Json;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    private static readonly Func<ContentDefinition, bool> Available = _ => true;
    private static readonly Func<ContentDefinition, bool> OnlyDefaults = x => x.IsDefault;
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);
    private static string _directory = "";

    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) ||
            !File.Exists(Path.Combine(args[0], "DuckDeskPet", "PetStore.cs")))
        {
            Console.Error.WriteLine("Usage: ContentSelfTest <absolute project root>");
            return 2;
        }
        string root = Path.Combine(Path.GetFullPath(args[0]), ".codex-build", "content-test-" + Guid.NewGuid().ToString("N"));
        Console.WriteLine("Isolated output: " + root);
        Directory.CreateDirectory(root);
        var cases = new (string Name, Action Test)[]
        {
            ("stable-twenty-one-entry-catalog", Catalog),
            ("three-free-defaults-forever-owned", Defaults),
            ("resource-links-match-content-types", Links),
            ("no-content-is-automatically-played", ManualActions),
            ("reward-conditions-reference-honor-thresholds", RewardContract),
            ("meal-reward-at-thirty-not-twenty-nine", MealReward),
            ("growth-reward-at-level-two", GrowthReward),
            ("legacy-growth-honor-still-grants", LegacyGrowth),
            ("reward-grants-are-idempotent", RepeatReward),
            ("reward-does-not-require-art", RewardWithoutArt),
            ("purchase-quote-is-read-only", Quote),
            ("missing-art-not-for-sale", MissingArt),
            ("unknown-id-rejected", UnknownId),
            ("candidate-grant-does-not-charge-wallet", Grant),
            ("different-transaction-cannot-buy-owned-again", DuplicatePurchase),
            ("purchase-after-reward-never-charges", RewardThenPurchase),
            ("reward-after-purchase-does-not-refund", PurchaseThenReward),
            ("eligible-unclaimed-reward-blocks-paid-purchase", EligibleBlocksPurchase),
            ("grant-rechecks-resource-availability", GrantRechecksArt),
            ("cannot-equip-unowned-content", EquipUnowned),
            ("equip-immediately-when-safe", EquipNow),
            ("busy-equipment-waits-for-safe-boundary", DeferredEquip),
            ("pending-selection-replaces-not-stacks", ReplacePending),
            ("select-current-cancels-pending", CancelPending),
            ("missing-pending-resource-retains-request", MissingPending),
            ("unowned-pending-never-applies", InvalidPending),
            ("fallback-is-read-only-and-preserves-ownership", MissingFallback),
            ("unknown-future-id-survives-fallback", FutureContent),
            ("wrong-slot-falls-back", WrongSlot),
            ("missing-default-is-explicit", MissingDefault),
            ("action-is-play-only-not-equipment", ActionPlay),
            ("unowned-action-not-playable", UnownedAction),
            ("missing-owned-action-not-playable", MissingAction),
            ("snapshot-collections-detached", Snapshot),
            ("json-round-trip-owned-current-pending", RoundTrip),
            ("normalize-null-and-malformed-selections", Normalize),
            ("normalization-preserves-valid-unknown-owned-ids", NormalizeUnknown),
            ("ownership-cap-does-not-drop-old-content", Capacity),
            ("v2-full-collection-survives-expanded-catalog", LegacyFullCollection),
            ("expanded-shop-reserves-space-for-old-free-reward", ReservedRewardSlot),
            ("reward-cap-failure-has-no-partial-grant", RewardCapacity),
            ("purchase-id-stable-and-bounded", TransactionIds),
            ("pet-state-content-deep-copy", StateSnapshot),
            ("legacy-normalize-grants-no-extra-coins", LegacyState),
            ("current-normalize-grants-no-extra-coins", CurrentState),
            ("game-reward-clock-normalized-and-persisted", GameRewardClock),
            ("atomic-purchase-debits-and-grants-together", AtomicPurchase),
            ("same-item-other-id-still-not-charged", DuplicateItemTransaction),
            ("reward-eligibility-race-aborts-debit", RewardRace),
            ("failed-purchase-grants-nothing-and-retries", FailedPurchase),
            ("future-owned-choice-survives-disk", UnknownDisk),
            ("invalid-content-save-retains-original", InvalidContentSave),
            ("pending-equipment-survives-restart", PendingDisk),
            ("feeding-thirtieth-meal-grants-in-core", CoreMealReward),
            ("petting-level-two-grants-in-core", CoreLevelReward),
            ("offline-level-up-grants-in-core", CoreOfflineReward),
        };
        int failed = 0;
        var results = new List<object>();
        foreach (var item in cases)
        {
            _directory = Path.Combine(root, item.Name);
            Directory.CreateDirectory(_directory);
            try { item.Test(); Console.WriteLine("PASS " + item.Name); results.Add(new { name = item.Name, passed = true }); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + item.Name + ": " + ex); results.Add(new { name = item.Name, passed = false, error = ex.ToString() }); }
        }
        File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { testCount = cases.Length, failures = failed, results },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{cases.Length - failed}/{cases.Length} content checks passed. Local logic + isolated saves; no asset-readiness claim or normal user-data access.");
        return failed == 0 ? 0 : 1;
    }

    private static PetState Progress(int meals = 0, int experience = 0) => new() { TotalMeals = meals, Experience = experience, Coins = 1000 };
    private static ContentOwnershipState Owned(string id)
    {
        var state = new ContentOwnershipState(); state.OwnedContentIds.Add(id); return state;
    }
    private static void Catalog()
    {
        Equal(21, ContentCatalog.Definitions.Count); Equal(21, ContentCatalog.Definitions.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        Equal(7, ContentCatalog.Definitions.Count(x => x.Type == ContentType.Desk));
        Equal(7, ContentCatalog.Definitions.Count(x => x.Type == ContentType.Computer));
        Equal(6, ContentCatalog.Definitions.Count(x => x.Type == ContentType.Outfit));
        Equal(600m, ContentCatalog.Get(ContentCatalog.HoodieOutfitId).Price);
        Equal(150m, ContentCatalog.Get("desk.mint").Price); Equal(240m, ContentCatalog.Get("computer.midnight").Price);
        Equal(120m, ContentCatalog.Get("action.tea").Price); Equal(10000m, ContentCatalog.Get("outfit.office").Price);
        foreach (var entry in ContentCatalog.Definitions) Check(entry.Price >= 0 && entry.Price <= EconomyPolicy.MaximumCoins);
    }
    private static void Defaults()
    {
        var state = new ContentOwnershipState(); Equal(3, state.OwnedContentIds.Count); state.OwnedContentIds.Clear();
        foreach (string id in ContentCatalog.DefaultContentIds)
        {
            Check(ContentOwnershipService.Owns(state, id)); Equal(0, ContentCatalog.Get(id).Price);
            Equal(ContentOperationStatus.AlreadyOwned, ContentOwnershipService.QuotePurchase(state, id, Progress(), Available).Status);
        }
        Equal(3, ContentOwnershipService.Normalize(state).OwnedContentIds.Count);
    }
    private static void Links()
    {
        foreach (var x in ContentCatalog.Definitions)
        {
            if (x.Type is ContentType.Desk or ContentType.Computer) { Equal(x.Id, x.ScenePropId); Check(x.AnimationId is null && x.OutfitId is null); }
            if (x.Type == ContentType.Action) { Equal("tea", x.AnimationId); Check(x.ScenePropId is null && x.OutfitId is null); }
            if (x.Type == ContentType.Outfit) { Equal(x.Id, x.OutfitId); Check(x.AnimationId is null && x.ScenePropId is null); }
        }
    }
    private static void ManualActions()
    {
        Equal(1, ContentCatalog.Definitions.Count(x => x.PlaybackMode == ContentPlaybackMode.ManualOnly));
        Check(ContentCatalog.Get(ContentCatalog.TeaActionId).Slot is null);
        foreach (var x in ContentCatalog.Definitions.Where(x => x.Type != ContentType.Action)) Equal(ContentPlaybackMode.None, x.PlaybackMode);
    }
    private static void RewardContract()
    {
        foreach (var entry in ContentCatalog.Definitions.Where(x => x.Reward is not null))
            Equal(1, HonorCatalog.Definitions.Count(x => x.Id == entry.Reward!.HonorId));
        Equal(30, HonorCatalog.Definitions.Single(x => x.Id == ContentCatalog.Get("desk.mint").Reward!.HonorId).Target);
        Equal(2, HonorCatalog.Definitions.Single(x => x.Id == ContentCatalog.Get("action.tea").Reward!.HonorId).Target);
    }
    private static void MealReward()
    {
        var c = new ContentOwnershipState(); Equal(0, ContentOwnershipService.GrantEligibleRewards(c, Progress(29)).Count);
        Check(ContentOwnershipService.GrantEligibleRewards(c, Progress(30)).SequenceEqual(new[] { "desk.mint" }));
    }
    private static void GrowthReward()
    {
        var c = new ContentOwnershipState(); Equal(0, ContentOwnershipService.GrantEligibleRewards(c, Progress(experience: 99)).Count);
        Check(ContentOwnershipService.GrantEligibleRewards(c, Progress(experience: 100)).SequenceEqual(new[] { "action.tea" }));
    }
    private static void LegacyGrowth()
    {
        var c = new ContentOwnershipState(); var p = Progress(); p.Achievements.Add("level-2");
        ContentOwnershipService.GrantEligibleRewards(c, p); Check(c.OwnedContentIds.Contains("action.tea")); Equal(1, p.Level);
    }
    private static void RepeatReward()
    {
        var c = new ContentOwnershipState(); var p = Progress(30, 100);
        Equal(2, ContentOwnershipService.GrantEligibleRewards(c, p).Count); Equal(0, ContentOwnershipService.GrantEligibleRewards(c, p).Count); Equal(5, c.OwnedContentIds.Count);
        Equal(1000m, p.Coins); Equal(0, p.AppliedWalletDebits.Count);
    }
    private static void RewardWithoutArt()
    {
        var c = new ContentOwnershipState(); ContentOwnershipService.GrantEligibleRewards(c, Progress(30));
        Check(c.OwnedContentIds.Contains("desk.mint")); Equal(ContentOperationStatus.Unavailable, ContentOwnershipService.RequestEquip(c, "desk.mint", false, OnlyDefaults).Status);
    }
    private static void Quote()
    {
        var c = new ContentOwnershipState(); var p = Progress(); string before = JsonSerializer.Serialize(c);
        var result = ContentOwnershipService.QuotePurchase(c, "desk.mint", p, Available); Check(result.CanPurchase); Equal(150m, result.Definition!.Price);
        Equal(before, JsonSerializer.Serialize(c)); Equal(1000m, p.Coins);
    }
    private static void MissingArt()
    {
        var c = new ContentOwnershipState(); Equal(ContentOperationStatus.Unavailable, ContentOwnershipService.QuotePurchase(c, "outfit.office", Progress(), OnlyDefaults).Status);
        Check(!c.OwnedContentIds.Contains("outfit.office"));
    }
    private static void UnknownId()
    {
        var c = new ContentOwnershipState(); Equal(ContentOperationStatus.UnknownContent, ContentOwnershipService.QuotePurchase(c, "https://not-a-content", Progress(), Available).Status);
        Equal(ContentOperationStatus.UnknownContent, ContentOwnershipService.RequestEquip(c, "desk.unknown", false, Available).Status);
        Check(!ContentCatalog.TryGet(null, out _)); Throws<ArgumentException>(() => ContentCatalog.Get("unknown"));
    }
    private static void Grant()
    {
        var c = new ContentOwnershipState(); var p = Progress(); var result = ContentOwnershipService.GrantPurchase(c, "desk.mint", p, Available);
        Check(result.Changed); Check(c.OwnedContentIds.Contains("desk.mint")); Equal(1000m, p.Coins); Equal(0, p.AppliedWalletDebits.Count);
        Equal("desk.default", c.Equipped[ContentSlot.Desk]);
    }
    private static void DuplicatePurchase()
    {
        var c = new ContentOwnershipState(); ContentOwnershipService.GrantPurchase(c, "computer.midnight", Progress(), Available);
        var result = ContentOwnershipService.GrantPurchase(c, "computer.midnight", Progress(), Available);
        Equal(ContentOperationStatus.AlreadyOwned, result.Status); Check(!result.Changed); Equal(4, c.OwnedContentIds.Count);
    }
    private static void RewardThenPurchase()
    {
        var c = new ContentOwnershipState(); var p = Progress(30); ContentOwnershipService.GrantEligibleRewards(c, p);
        Equal(ContentOperationStatus.AlreadyOwned, ContentOwnershipService.GrantPurchase(c, "desk.mint", p, Available).Status); Equal(1000m, p.Coins);
    }
    private static void PurchaseThenReward()
    {
        var c = new ContentOwnershipState(); var p = Progress(); ContentOwnershipService.GrantPurchase(c, "desk.mint", p, Available);
        p.Coins = 80; p.TotalMeals = 30; Equal(0, ContentOwnershipService.GrantEligibleRewards(c, p).Count); Equal(80, p.Coins);
    }
    private static void EligibleBlocksPurchase()
    {
        var c = new ContentOwnershipState(); var p = Progress(30);
        Equal(ContentOperationStatus.RewardAvailable, ContentOwnershipService.QuotePurchase(c, "desk.mint", p, Available).Status);
        Check(!ContentOwnershipService.GrantPurchase(c, "desk.mint", p, Available).Changed); Check(!c.OwnedContentIds.Contains("desk.mint"));
    }
    private static void GrantRechecksArt()
    {
        var c = new ContentOwnershipState(); Check(ContentOwnershipService.QuotePurchase(c, "computer.midnight", Progress(), Available).CanPurchase);
        Check(!ContentOwnershipService.GrantPurchase(c, "computer.midnight", Progress(), OnlyDefaults).Changed); Check(!c.OwnedContentIds.Contains("computer.midnight"));
    }
    private static void EquipUnowned()
    {
        var c = new ContentOwnershipState(); Equal(ContentOperationStatus.NotOwned, ContentOwnershipService.RequestEquip(c, "desk.mint", false, Available).Status);
        Equal(0, c.PendingEquipment.Count); Equal("desk.default", c.Equipped[ContentSlot.Desk]);
    }
    private static void EquipNow()
    {
        var c = Owned("desk.mint"); Equal(ContentOperationStatus.Equipped, ContentOwnershipService.RequestEquip(c, "desk.mint", false, Available).Status);
        Equal("desk.mint", c.Equipped[ContentSlot.Desk]); Equal(0, c.PendingEquipment.Count);
        Equal(ContentOperationStatus.NoChange, ContentOwnershipService.RequestEquip(c, "desk.mint", false, Available).Status);
    }
    private static void DeferredEquip()
    {
        var c = Owned("desk.mint"); Equal(ContentOperationStatus.Pending, ContentOwnershipService.RequestEquip(c, "desk.mint", true, Available).Status);
        Equal("desk.default", c.Equipped[ContentSlot.Desk]); Equal("desk.mint", c.PendingEquipment[ContentSlot.Desk]);
        Equal("desk.default", ContentOwnershipService.ResolveEquipped(c, ContentSlot.Desk, Available).EffectiveId);
        Equal(ContentOperationStatus.Equipped, ContentOwnershipService.ApplyPendingAtSafeBoundary(c, Available).Single().Status);
        Equal("desk.mint", c.Equipped[ContentSlot.Desk]); Equal(0, c.PendingEquipment.Count);
    }
    private static void ReplacePending()
    {
        var c = Owned("desk.mint"); c.PendingEquipment[ContentSlot.Desk] = "desk.future";
        ContentOwnershipService.RequestEquip(c, "desk.mint", true, Available); Equal(1, c.PendingEquipment.Count); Equal("desk.mint", c.PendingEquipment[ContentSlot.Desk]);
        Equal(ContentOperationStatus.NoChange, ContentOwnershipService.RequestEquip(c, "desk.mint", true, Available).Status);
    }
    private static void CancelPending()
    {
        var c = Owned("desk.mint"); ContentOwnershipService.RequestEquip(c, "desk.mint", true, Available);
        Check(ContentOwnershipService.RequestEquip(c, "desk.default", true, Available).Changed); Equal(0, c.PendingEquipment.Count); Equal("desk.default", c.Equipped[ContentSlot.Desk]);
    }
    private static void MissingPending()
    {
        var c = Owned("desk.mint"); ContentOwnershipService.RequestEquip(c, "desk.mint", true, Available);
        Equal(ContentOperationStatus.Unavailable, ContentOwnershipService.ApplyPendingAtSafeBoundary(c, OnlyDefaults).Single().Status);
        Equal("desk.mint", c.PendingEquipment[ContentSlot.Desk]); Equal("desk.default", c.Equipped[ContentSlot.Desk]); Check(c.OwnedContentIds.Contains("desk.mint"));
        ContentOwnershipService.ApplyPendingAtSafeBoundary(c, Available); Equal("desk.mint", c.Equipped[ContentSlot.Desk]);
    }
    private static void InvalidPending()
    {
        var c = new ContentOwnershipState(); c.PendingEquipment[ContentSlot.Desk] = "desk.mint";
        Equal(ContentOperationStatus.NotOwned, ContentOwnershipService.ApplyPendingAtSafeBoundary(c, Available).Single().Status); Equal("desk.default", c.Equipped[ContentSlot.Desk]);
    }
    private static void MissingFallback()
    {
        var c = Owned("desk.mint"); c.Equipped[ContentSlot.Desk] = "desk.mint"; string before = JsonSerializer.Serialize(c);
        var result = ContentOwnershipService.ResolveEquipped(c, ContentSlot.Desk, OnlyDefaults); Check(result.IsFallback); Equal("desk.default", result.EffectiveId);
        Equal(before, JsonSerializer.Serialize(c)); Check(!string.IsNullOrWhiteSpace(result.Warning));
        Equal("desk.mint", ContentOwnershipService.ResolveEquipped(c, ContentSlot.Desk, Available).EffectiveId);
    }
    private static void FutureContent()
    {
        var c = Owned("desk.future-pack"); c.Equipped[ContentSlot.Desk] = "desk.future-pack";
        var normalized = ContentOwnershipService.Normalize(c); var result = ContentOwnershipService.ResolveEquipped(normalized, ContentSlot.Desk, Available);
        Equal("desk.default", result.EffectiveId); Check(normalized.OwnedContentIds.Contains("desk.future-pack")); Equal("desk.future-pack", normalized.Equipped[ContentSlot.Desk]);
    }
    private static void WrongSlot()
    {
        var c = Owned("computer.midnight"); c.Equipped[ContentSlot.Desk] = "computer.midnight";
        Equal("desk.default", ContentOwnershipService.ResolveEquipped(c, ContentSlot.Desk, Available).EffectiveId);
    }
    private static void MissingDefault()
    {
        var c = new ContentOwnershipState(); var result = ContentOwnershipService.ResolveEquipped(c, ContentSlot.Desk, _ => false);
        Check(result.IsFallback && result.EffectiveId is null && result.Warning is not null); Equal("desk.default", c.Equipped[ContentSlot.Desk]);
    }
    private static void ActionPlay()
    {
        var c = Owned("action.tea"); Check(ContentOwnershipService.CanPlayAction(c, "action.tea", Available).CanPlay);
        Equal(ContentOperationStatus.NotEquippable, ContentOwnershipService.RequestEquip(c, "action.tea", false, Available).Status); Equal(0, c.PendingEquipment.Count);
        Check(!ContentOwnershipService.CanPlayAction(c, "desk.default", Available).CanPlay);
    }
    private static void UnownedAction() => Check(!ContentOwnershipService.CanPlayAction(new(), "action.tea", Available).CanPlay);
    private static void MissingAction() => Check(!ContentOwnershipService.CanPlayAction(Owned("action.tea"), "action.tea", OnlyDefaults).CanPlay);
    private static void Snapshot()
    {
        var c = Owned("desk.mint"); c.PendingEquipment[ContentSlot.Desk] = "desk.mint"; var clone = c.CreateSnapshot();
        clone.OwnedContentIds.Clear(); clone.Equipped.Clear(); clone.PendingEquipment.Clear(); Equal(4, c.OwnedContentIds.Count); Equal(3, c.Equipped.Count); Equal(1, c.PendingEquipment.Count);
    }
    private static void RoundTrip()
    {
        var c = Owned("desk.mint"); c.Equipped[ContentSlot.Desk] = "desk.mint"; c.OwnedContentIds.Add("outfit.office"); c.PendingEquipment[ContentSlot.Outfit] = "outfit.office";
        var roundtrip = JsonSerializer.Deserialize<ContentOwnershipState>(JsonSerializer.Serialize(c))!;
        Equal(5, roundtrip.OwnedContentIds.Count); Equal("desk.mint", roundtrip.Equipped[ContentSlot.Desk]); Equal("outfit.office", roundtrip.PendingEquipment[ContentSlot.Outfit]);
    }
    private static void Normalize()
    {
        Equal(3, ContentOwnershipService.Normalize(null).OwnedContentIds.Count);
        var c = new ContentOwnershipState { OwnedContentIds = null!, Equipped = null!, PendingEquipment = null! };
        var normalized = ContentOwnershipService.Normalize(c); Equal(3, normalized.OwnedContentIds.Count); Equal(3, normalized.Equipped.Count); Equal(0, normalized.PendingEquipment.Count);
        c = new(); c.Equipped[(ContentSlot)99] = "desk.mint"; c.PendingEquipment[ContentSlot.Desk] = "../bad"; c.OwnedContentIds.Add("bad id");
        normalized = ContentOwnershipService.Normalize(c); Equal(3, normalized.Equipped.Count); Equal(0, normalized.PendingEquipment.Count); Check(!normalized.OwnedContentIds.Contains("bad id"));
    }
    private static void NormalizeUnknown()
    {
        var c = Owned("outfit.future-blue"); var n = ContentOwnershipService.Normalize(c); c.OwnedContentIds.Clear();
        Check(ContentOwnershipService.Owns(n, "outfit.future-blue")); Equal(4, n.OwnedContentIds.Count);
    }
    private static void Capacity()
    {
        var c = new ContentOwnershipState(); for (int i = 3; i < ContentOwnershipService.MaximumOwnedContents; i++) c.OwnedContentIds.Add("future." + i);
        Equal(ContentOperationStatus.Unavailable, ContentOwnershipService.QuotePurchase(c, "computer.midnight", Progress(), Available).Status);
        Throws<InvalidDataException>(() => ContentOwnershipService.Normalize(c)); Equal(ContentOwnershipService.MaximumOwnedContents, c.OwnedContentIds.Count);
        c = new();
        int unknownLimit = ContentOwnershipService.MaximumOwnedContents - ContentCatalog.Definitions.Count;
        for (int i = 0; i < unknownLimit; i++) c.OwnedContentIds.Add("future." + i);
        c = ContentOwnershipService.Normalize(c);
        ContentOwnershipService.GrantEligibleRewards(c, Progress(30, 100));
        foreach (var item in ContentCatalog.Definitions.Where(x => !x.IsDefault && x.Reward is null))
            ContentOwnershipService.GrantPurchase(c, item.Id, Progress(), Available);
        Equal(ContentOwnershipService.MaximumOwnedContents, c.OwnedContentIds.Count); ContentOwnershipService.ValidateForSave(c);
        c.OwnedContentIds.Add("future.overflow"); Throws<InvalidDataException>(() => ContentOwnershipService.Normalize(c)); Check(c.OwnedContentIds.Contains("future.overflow"));
    }
    private static void RewardCapacity()
    {
        var c = new ContentOwnershipState(); for (int i = 3; i < ContentOwnershipService.MaximumOwnedContents - 1; i++) c.OwnedContentIds.Add("future." + i);
        int count = c.OwnedContentIds.Count; Throws<InvalidDataException>(() => ContentOwnershipService.GrantEligibleRewards(c, Progress(30, 100)));
        Equal(count, c.OwnedContentIds.Count); Check(!c.OwnedContentIds.Contains("desk.mint") && !c.OwnedContentIds.Contains("action.tea"));
    }
    private static void LegacyFullCollection()
    {
        var old = Progress(); old.Version = 2; old.LastUpdatedUtc = Start;
        string[] oldIds = { "desk.default", "computer.default", "outfit.default", "desk.mint", "computer.midnight", "action.tea",
            "outfit.office", "desk.walnut", "desk.arcade", "computer.retro", "computer.arcade", "outfit.hoodie" };
        old.Content.OwnedContentIds.UnionWith(oldIds);
        for (int i = 0; i < ContentOwnershipService.MaximumOwnedContents - oldIds.Length; i++) old.Content.OwnedContentIds.Add("future." + i);
        string before = JsonSerializer.Serialize(old);
        File.WriteAllText(SavePath, before); var store = new PetStore(_directory);
        var care = new PetCareService(store.Load(), Start);
        Equal(ContentOwnershipService.MaximumOwnedContents, care.State.Content.OwnedContentIds.Count);
        Check(old.Content.OwnedContentIds.SetEquals(care.State.Content.OwnedContentIds));
        Check(store.Save(care.State)); Equal(before, File.ReadAllText(SavePath + ".bak"));
        Equal(ContentOperationStatus.Unavailable, ContentOwnershipService.QuotePurchase(care.State.Content,
            ContentCatalog.GoldComputerId, care.State, Available).Status);
    }
    private static void ReservedRewardSlot()
    {
        var state = Progress();
        string[] oldIds = { "desk.default", "computer.default", "outfit.default", "desk.mint", "computer.midnight",
            "outfit.office", "desk.walnut", "desk.arcade", "computer.retro", "computer.arcade", "outfit.hoodie" };
        state.Content.OwnedContentIds.UnionWith(oldIds);
        for (int i = 0; i < ContentOwnershipService.MaximumOwnedContents - 12; i++) state.Content.OwnedContentIds.Add("future." + i);
        Equal(4095, state.Content.OwnedContentIds.Count);
        ContentOwnershipService.ValidateForSave(state.Content);
        Check(!ContentOwnershipService.QuotePurchase(state.Content, ContentCatalog.GoldComputerId, state, Available).CanPurchase);
        state.Experience = 100;
        Check(ContentOwnershipService.GrantEligibleRewards(state.Content, state).SequenceEqual(new[] { ContentCatalog.TeaActionId }));
        Equal(4096, state.Content.OwnedContentIds.Count); ContentOwnershipService.ValidateForSave(state.Content);
    }
    private static void TransactionIds()
    {
        foreach (var definition in ContentCatalog.Definitions)
        {
            string id = ContentCatalog.PurchaseTransactionId(definition.Id); Check(id.Length <= EconomyPolicy.MaximumTransactionIdLength); Equal("purchase:" + definition.Id, id);
        }
    }
    private static void StateSnapshot()
    {
        var state = Progress(); state.LastGameRewardUtc = Start; state.Content.OwnedContentIds.Add("desk.mint");
        state.Content.PendingEquipment[ContentSlot.Desk] = "desk.mint";
        var clone = state.CreateSnapshot(); clone.Content.OwnedContentIds.Clear(); clone.Content.PendingEquipment.Clear();
        Check(state.Content.OwnedContentIds.Contains("desk.mint")); Equal(1, state.Content.PendingEquipment.Count);
        var applied = new PetState(); applied.ApplySnapshot(state); state.Content.OwnedContentIds.Clear(); state.Content.PendingEquipment.Clear();
        Check(applied.Content.OwnedContentIds.Contains("desk.mint")); Equal(1, applied.Content.PendingEquipment.Count); Equal<DateTimeOffset?>(Start, applied.LastGameRewardUtc);
    }
    private static void LegacyState()
    {
        var saved = Progress(30, 100); saved.Version = 1; saved.LastUpdatedUtc = Start;
        var care = new PetCareService(saved, Start); Check(care.State.Content.OwnedContentIds.Contains("desk.mint")); Check(care.State.Content.OwnedContentIds.Contains("action.tea"));
        Equal(0, care.State.Coins); Equal(0, care.State.AppliedWalletDebits.Count); Equal(3, saved.Content.OwnedContentIds.Count);
    }
    private static void CurrentState()
    {
        var saved = Progress(30, 100); saved.LastUpdatedUtc = Start; saved.Content.OwnedContentIds.Add("desk.future"); saved.Content.Equipped[ContentSlot.Desk] = "desk.future";
        var care = new PetCareService(saved, Start); Equal(1000m, care.State.Coins); Equal(6, care.State.Content.OwnedContentIds.Count); Equal("desk.future", care.State.Content.Equipped[ContentSlot.Desk]);
        Check(care.State.Content.OwnedContentIds.Contains("desk.mint")); Equal(0, care.State.AppliedWalletDebits.Count);
    }
    private static void GameRewardClock()
    {
        var state = Progress(); state.LastUpdatedUtc = Start; state.LastGameRewardUtc = Start.AddMinutes(-2);
        var care = new PetCareService(state, Start); Equal(state.LastGameRewardUtc, care.State.LastGameRewardUtc);
        state.LastGameRewardUtc = Start.AddHours(1); var future = new PetCareService(state, Start); Equal<DateTimeOffset?>(Start.AddHours(1), future.State.LastGameRewardUtc);
        Check(new PetStore(_directory).Save(future.State)); Equal<DateTimeOffset?>(Start.AddHours(1), new PetStore(_directory).Load()!.LastGameRewardUtc);
        Check(new PetCareService(null, Start).State.LastGameRewardUtc is null);
    }
    private static WalletTransactionResult Purchase(PetStore store, PetState state, string id, string? transactionId = null) =>
        store.TryTransaction(state, transactionId ?? ContentCatalog.PurchaseTransactionId(id), ContentCatalog.Get(id).Price,
            candidate => ContentOwnershipService.GrantPurchase(candidate.Content, id, candidate, Available).Changed);
    private static string SavePath => Path.Combine(_directory, "pet-state.json");
    private static void AtomicPurchase()
    {
        var store = new PetStore(_directory); var state = Progress(); Check(store.Save(state)); Check(Purchase(store, state, "desk.mint").Changed);
        Equal(850m, state.Coins); Check(state.Content.OwnedContentIds.Contains("desk.mint"));
        var loadedStore = new PetStore(_directory); var loaded = loadedStore.Load()!; Equal(850m, loaded.Coins); Check(loaded.Content.OwnedContentIds.Contains("desk.mint"));
        Equal(WalletTransactionStatus.AlreadyApplied, Purchase(loadedStore, loaded, "desk.mint").Status); Equal(850m, loaded.Coins);
        var before = JsonSerializer.Deserialize<PetState>(File.ReadAllText(SavePath + ".bak"))!; Equal(1000m, before.Coins); Check(!before.Content.OwnedContentIds.Contains("desk.mint"));
    }
    private static void DuplicateItemTransaction()
    {
        var store = new PetStore(_directory); var state = Progress(); Check(store.Save(state)); Check(Purchase(store, state, "desk.mint").Changed);
        Equal(WalletTransactionStatus.InvalidRequest, Purchase(store, state, "desk.mint", "another-id").Status);
        Equal(850m, state.Coins); Equal(1, state.AppliedWalletDebits.Count); Equal(850m, new PetStore(_directory).Load()!.Coins);
    }
    private static void RewardRace()
    {
        var store = new PetStore(_directory); var state = Progress(29); Check(store.Save(state));
        Check(ContentOwnershipService.QuotePurchase(state.Content, "desk.mint", state, Available).CanPurchase);
        state.TotalMeals = 30; Equal(WalletTransactionStatus.InvalidRequest, Purchase(store, state, "desk.mint").Status);
        Equal(1000m, state.Coins); Equal(0, state.AppliedWalletDebits.Count);
        ContentOwnershipService.GrantEligibleRewards(state.Content, state); Check(store.Save(state)); Check(state.Content.OwnedContentIds.Contains("desk.mint")); Equal(1000m, state.Coins);
    }
    private static void FailedPurchase()
    {
        var store = new PetStore(_directory); var state = Progress(); Check(store.Save(state)); byte[] before = File.ReadAllBytes(SavePath);
        using (var held = new FileStream(SavePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Equal(WalletTransactionStatus.SaveFailed, Purchase(store, state, "computer.midnight").Status);
            Equal(1000m, state.Coins); Check(!state.Content.OwnedContentIds.Contains("computer.midnight")); Equal(0, state.AppliedWalletDebits.Count);
            Check(File.ReadAllBytes(SavePath).SequenceEqual(before));
        }
        Check(Purchase(store, state, "computer.midnight").Changed); Equal(760m, state.Coins); Check(state.Content.OwnedContentIds.Contains("computer.midnight"));
    }
    private static void UnknownDisk()
    {
        var state = Progress(); state.LastUpdatedUtc = Start; state.Content.OwnedContentIds.Add("desk.future-pack"); state.Content.Equipped[ContentSlot.Desk] = "desk.future-pack";
        Check(new PetStore(_directory).Save(state)); var store = new PetStore(_directory); var care = new PetCareService(store.Load(), Start);
        Equal("desk.default", ContentOwnershipService.ResolveEquipped(care.State.Content, ContentSlot.Desk, OnlyDefaults).EffectiveId);
        Check(store.Save(care.State)); var loaded = new PetStore(_directory).Load()!; Check(loaded.Content.OwnedContentIds.Contains("desk.future-pack")); Equal("desk.future-pack", loaded.Content.Equipped[ContentSlot.Desk]);
    }
    private static void InvalidContentSave()
    {
        var state = Progress(); var store = new PetStore(_directory); Check(store.Save(state)); string before = File.ReadAllText(SavePath);
        state.Content.OwnedContentIds.Add("../invalid"); Check(!store.Save(state)); Equal(before, File.ReadAllText(SavePath));
        state.Content.OwnedContentIds.Remove("../invalid"); Check(store.Save(state));
    }
    private static void PendingDisk()
    {
        var state = Progress(); state.LastUpdatedUtc = Start; state.Content.OwnedContentIds.Add("desk.mint");
        ContentOwnershipService.RequestEquip(state.Content, "desk.mint", true, Available); Check(new PetStore(_directory).Save(state));
        var care = new PetCareService(new PetStore(_directory).Load(), Start); Equal("desk.default", care.State.Content.Equipped[ContentSlot.Desk]); Equal("desk.mint", care.State.Content.PendingEquipment[ContentSlot.Desk]);
        ContentOwnershipService.ApplyPendingAtSafeBoundary(care.State.Content, Available); Equal("desk.mint", care.State.Content.Equipped[ContentSlot.Desk]);
    }
    private static void CoreMealReward()
    {
        var state = Progress(29); state.LastUpdatedUtc = Start; state.Fullness = 50;
        var care = new PetCareService(state, Start); Check(care.Feed(Start).Success);
        Check(care.State.Content.OwnedContentIds.Contains("desk.mint")); Equal(1000m, care.State.Coins);
    }
    private static void CoreLevelReward()
    {
        var state = Progress(experience: 99); state.LastUpdatedUtc = Start;
        var care = new PetCareService(state, Start); Check(care.Pet(Start).Success);
        Check(care.State.Content.OwnedContentIds.Contains("action.tea")); Equal(1000m, care.State.Coins);
    }
    private static void CoreOfflineReward()
    {
        var state = Progress(experience: 99); state.LastUpdatedUtc = Start;
        var care = new PetCareService(state, Start.AddMinutes(5)); Equal(2, care.State.Level);
        Check(care.State.Content.OwnedContentIds.Contains("action.tea")); Equal(1000m, care.State.Coins);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
    private static void Throws<T>(Action body) where T : Exception { try { body(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
