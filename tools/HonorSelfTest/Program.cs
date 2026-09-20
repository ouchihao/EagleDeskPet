using System.Text.Json;
using DuckDeskPet.Core;
using DuckDeskPet;

var tests = new (string Name, Action Body)[]
{
    ("Eighteen immutable definitions with unique stable IDs", () =>
    {
        Require(HonorCatalog.Definitions.Count == 18, "Exactly eighteen demo honors.");
        Require(HonorCatalog.Definitions.Select(x => x.Id).Distinct().Count() == 18, "IDs are unique.");
        Require(HonorCatalog.Series.Count == 6 && HonorCatalog.Series.Select(x => x.BadgeKey).Distinct().Count() == 6, "Six distinct art series.");
        Require(HonorCatalog.Definitions.All(x => x.Target > 0 && x.Name.Length > 0), "Usable targets and names.");
        Require(((IList<HonorDefinition>)HonorCatalog.Definitions).IsReadOnly, "Definitions cannot be modified.");
    }),
    ("Each series has ascending bronze silver gold targets", () =>
    {
        foreach (var series in Enum.GetValues<HonorSeries>())
        {
            var honors = HonorCatalog.Definitions.Where(x => x.Series == series).ToArray();
            Require(honors.Select(x => x.Tier).SequenceEqual(Enum.GetValues<HonorTier>()), "All three tiers in order.");
            Require(honors.Select(x => x.Target).SequenceEqual(honors.Select(x => x.Target).Order()), "Ascending targets.");
        }
    }),
    ("New pet starts with eighteen visible locked previews", () =>
    {
        var all = HonorCatalog.Evaluate(new PetState());
        Require(all.Count == 18 && all.All(x => !x.IsEarned), "All locked.");
        Require(all.All(x => x.Fraction >= 0 && x.Fraction < 1), "Finite bounded preview progress.");
    }),
    ("Meal tiers unlock exactly at one thirty one-eighty", () =>
    {
        foreach (var (meals, earned) in new[] { (0, 0), (1, 1), (29, 1), (30, 2), (179, 2), (180, 3) })
            Require(Count(new PetState { TotalMeals = meals }, HonorSeries.Meals) == earned, $"Meals {meals}.");
    }),
    ("Affection tiers unlock exactly at ten one-fifty twelve-hundred", () =>
    {
        foreach (var (pets, earned) in new[] { (0, 0), (9, 0), (10, 1), (149, 1), (150, 2), (1199, 2), (1200, 3) })
            Require(Count(new PetState { TotalPets = pets }, HonorSeries.Affection) == earned, $"Pets {pets}.");
    }),
    ("Growth tiers use nonlinear derived level two ten twenty-five", () =>
    {
        foreach (var (xp, earned) in new[] { (0, 0), (99, 0), (100, 1), (2699, 1), (2700, 2), (16199, 2), (16200, 3) })
            Require(Count(new PetState { Experience = xp }, HonorSeries.Growth) == earned, $"XP {xp}.");
    }),
    ("Legacy IDs retain bronze without silently granting silver", () =>
    {
        var state = new PetState { Achievements = new() { "first-meal", "gentle-hands", "level-2", "unknown" } };
        var all = HonorCatalog.Evaluate(state);
        Require(all.Count(x => x.IsEarned) == 3, "Exactly the three existing awards remain.");
        Require(all.Where(x => x.IsEarned).All(x => x.Definition.Tier == HonorTier.Bronze), "Legacy grants bronze only.");
    }),
    ("Each legacy ID affects only its own series", () =>
    {
        foreach (var (id, series) in new[] { ("first-meal", HonorSeries.Meals), ("gentle-hands", HonorSeries.Affection), ("level-2", HonorSeries.Growth) })
        {
            var all = HonorCatalog.Evaluate(new PetState { Achievements = new() { id } });
            Require(all.Count(x => x.IsEarned) == 1, "One legacy ID, one award.");
            Require(all.Single(x => x.IsEarned).Definition.Series == series, "Correct series.");
        }
    }),
    ("Full progress earns all eighteen with clamped bars", () =>
    {
        var state = new PetState { TotalMeals = int.MaxValue, TotalPets = int.MaxValue, Experience = int.MaxValue, TotalWorkSeconds = 1800000 };
        state.Content.OwnedContentIds.UnionWith(ContentCatalog.Definitions.Select(x => x.Id));
        var all = HonorCatalog.Evaluate(state);
        Require(all.All(x => x.IsEarned && x.Fraction == 1), "All complete without numeric overflow.");
    }),
    ("Negative counters and missing legacy list are safe", () =>
    {
        var all = HonorCatalog.Evaluate(new PetState { TotalMeals = -2, TotalPets = -3, Experience = -1, Achievements = null! });
        Require(all.All(x => !x.IsEarned && x.Current >= 0 && double.IsFinite(x.Fraction)), "Malformed old data produces no phantom award.");
    }),
    ("Exhibition does not mutate saved counters rewards flags or version", () =>
    {
        var state = new PetState { TotalMeals = 5, TotalPets = 20, Experience = 200, Achievements = new() { "unknown", "first-meal" } };
        var before = JsonSerializer.Serialize(state);
        for (int i = 0; i < 20; i++) HonorCatalog.Evaluate(state);
        Require(JsonSerializer.Serialize(state) == before, "No save mutations after repeated reads.");
    }),
    ("Snapshot equality ignores hunger mood and working ticks", () =>
    {
        var state = new PetState { TotalMeals = 1, TotalPets = 10, Experience = 100 };
        var before = HonorCatalog.Evaluate(state);
        state.Fullness = 12; state.Mood = 3; state.IsWorking = true; state.WorkSessionSeconds = 23;
        Require(HonorCatalog.Evaluate(state).SequenceEqual(before), "UI does not rebuild every care tick.");
        state.TotalMeals++;
        Require(!HonorCatalog.Evaluate(state).SequenceEqual(before), "Actual honor progress triggers refresh.");
    }),
    ("Work tiers count effective minutes not wage balance", () =>
    {
        foreach (var (minutes, earned) in new[] { (0d, 0), (29.99, 0), (30d, 1), (1799.99, 1), (1800d, 2), (17999.99, 2), (18000d, 3) })
            Require(Count(new PetState { TotalWorkSeconds = minutes * 60, Coins = 999999 }, HonorSeries.Work) == earned, $"Minutes {minutes}.");
        foreach (double seconds in new[] { -1d, double.NaN, double.PositiveInfinity })
            Require(Count(new PetState { TotalWorkSeconds = seconds }, HonorSeries.Work) == 0, "Invalid duration grants nothing.");
    }),
    ("Collection tiers count two nine eighteen known non-default entitlements", () =>
    {
        var ids = ContentCatalog.Definitions.Where(x => !x.IsDefault).Select(x => x.Id).ToArray();
        foreach (var (count, earned) in new[] { (0, 0), (1, 0), (2, 1), (8, 1), (9, 2), (17, 2), (18, 3) })
        {
            var state = new PetState(); state.Content.OwnedContentIds.UnionWith(ids.Take(count));
            state.Content.OwnedContentIds.Add("future.missing");
            Require(Count(state, HonorSeries.Collection) == earned, $"Collection {count}; defaults and unknown IDs excluded.");
        }
    }),
    ("Bond tiers sum successful meals and pets without overflow", () =>
    {
        foreach (var (count, earned) in new[] { (0, 0), (24, 0), (25, 1), (299, 1), (300, 2), (1499, 2), (1500, 3) })
            Require(Count(new PetState { TotalMeals = count / 2, TotalPets = count - count / 2 }, HonorSeries.Bond) == earned, $"Bond {count}.");
        Require(HonorCatalog.Evaluate(new PetState { TotalMeals = int.MaxValue, TotalPets = int.MaxValue })
            .First(x => x.Definition.Series == HonorSeries.Bond).Current == int.MaxValue, "Sum saturates safely.");
    }),
    ("Previously recorded honors survive missing counts and assets", () =>
    {
        var state = new PetState { Achievements = new() { "work-gold", "collection-silver", "bond-bronze" } };
        var care = new PetCareService(state, DateTimeOffset.UtcNow);
        Require(Count(care.State, HonorSeries.Work) == 3 && Count(care.State, HonorSeries.Collection) == 2 && Count(care.State, HonorSeries.Bond) == 1, "Recorded evidence is retained by normalization.");
        Require(!care.State.Achievements.Contains("bond-silver"), "No speculative higher tier.");
        Require(care.State.TotalWorkSeconds == 0 && care.State.TotalMeals == 0, "Recorded evidence does not invent counters.");
    }),
    ("Milestone recording is idempotent and grants no currency", () =>
    {
        var state = new PetState { TotalWorkSeconds = 1800, TotalPets = 25, Coins = 19 };
        Require(HonorCatalog.RecordEarned(state), "First record changes evidence.");
        var before = JsonSerializer.Serialize(state);
        Require(!HonorCatalog.RecordEarned(state) && JsonSerializer.Serialize(state) == before, "Second record has no effect.");
        Require(state.Coins == 19 && state.Content.OwnedContentIds.Count == 3, "No coins or items from recording alone.");
    }),
    ("Success care records bond and cooldown does not advance it", () =>
    {
        var now = DateTimeOffset.UtcNow;
        var care = new PetCareService(new PetState { TotalMeals = 12, TotalPets = 12, LastUpdatedUtc = now }, now);
        Require(care.Pet(now).Success && care.State.Achievements.Contains("bond-bronze"), "25th successful interaction recorded.");
        Require(care.Pet(now).Status == PetCareStatus.Cooldown && care.State.TotalPets == 13, "Rapid repeat not counted.");
    }),
    ("Hunger cutoff prevents unworked time counting", () =>
    {
        var now = DateTimeOffset.UtcNow;
        var care = new PetCareService(new PetState { TotalWorkSeconds = 1790, IsWorking = true, Fullness = 0.02, LastUpdatedUtc = now }, now);
        care.Advance(now.AddMinutes(10));
        Require(!care.State.IsWorking && care.State.TotalWorkSeconds < 1800 && Count(care.State, HonorSeries.Work) == 0, "Six effective seconds, not ten wall-clock minutes.");
    }),
    ("Free rewards can cross collection milestone in the same candidate", () =>
    {
        var state = new PetState { TotalMeals = 30, Experience = 100 };
        ContentOwnershipService.GrantEligibleRewards(state.Content, state);
        Require(state.Achievements.Contains("collection-bronze"), "Two actual free reward entitlements recorded immediately.");
    }),
    ("Separate ownership candidates do not mutate progress evidence", () =>
    {
        var state = new PetState(); state.Content.OwnedContentIds.Add(ContentCatalog.WalnutDeskId);
        var candidate = state.Content.CreateSnapshot(); var before = JsonSerializer.Serialize(state);
        ContentOwnershipService.GrantPurchase(candidate, ContentCatalog.MidnightComputerId, state, _ => true);
        Require(JsonSerializer.Serialize(state) == before, "A separate staged ownership object never leaks honors into live state.");
    }),
    ("Purchase honor is atomic with debit and disk persistence", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "EagleHonorAtomic-" + Guid.NewGuid().ToString("N"));
        var store = new PetStore(directory);
        var state = new PetState { Coins = 1000m };
        state.Content.OwnedContentIds.Add(ContentCatalog.WalnutDeskId);
        Require(store.Save(state), "Isolated baseline saved.");
        var rival = new PetStore(directory); var changed = rival.Load()!; changed.Mood++; Require(rival.Save(changed), "Create an actual stale-writer conflict.");
        var before = JsonSerializer.Serialize(state);
        var result = store.TryTransaction(state, ContentCatalog.PurchaseTransactionId(ContentCatalog.MidnightComputerId), 240m,
            candidate => ContentOwnershipService.GrantPurchase(candidate.Content, ContentCatalog.MidnightComputerId, candidate, _ => true).Changed);
        Require(result.Status == WalletTransactionStatus.SaveFailed && JsonSerializer.Serialize(state) == before, "Failed save leaks neither ownership nor coins nor honors.");
        var retry = new PetStore(directory); state = retry.Load()!;
        result = retry.TryTransaction(state, ContentCatalog.PurchaseTransactionId(ContentCatalog.MidnightComputerId), 240m,
            candidate => ContentOwnershipService.GrantPurchase(candidate.Content, ContentCatalog.MidnightComputerId, candidate, _ => true).Changed);
        Require(result.Changed && state.Coins == 760m && state.Achievements.Contains("collection-bronze"), "Successful transaction includes honor.");
        Require(new PetStore(directory).Load()!.Achievements.Contains("collection-bronze"), "Honor survives disk round trip.");
        Console.WriteLine("Isolated atomic evidence: " + directory);
    }),
};

int failed = 0;
foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
Console.WriteLine($"Honor self-test: {tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

static int Count(PetState state, HonorSeries series) =>
    HonorCatalog.Evaluate(state).Count(x => x.Definition.Series == series && x.IsEarned);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
