using System.Text.Json;
using DuckDeskPet.Core;

var tests = new (string Name, Action Body)[]
{
    ("Nine immutable definitions with unique stable IDs", () =>
    {
        Require(HonorCatalog.Definitions.Count == 9, "Exactly nine demo honors.");
        Require(HonorCatalog.Definitions.Select(x => x.Id).Distinct().Count() == 9, "IDs are unique.");
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
    ("New pet starts with nine visible locked previews", () =>
    {
        var all = HonorCatalog.Evaluate(new PetState());
        Require(all.Count == 9 && all.All(x => !x.IsEarned), "All locked.");
        Require(all.All(x => x.Fraction >= 0 && x.Fraction < 1), "Finite bounded preview progress.");
    }),
    ("Meal tiers unlock exactly at one ten thirty", () =>
    {
        foreach (var (meals, earned) in new[] { (0, 0), (1, 1), (9, 1), (10, 2), (29, 2), (30, 3) })
            Require(Count(new PetState { TotalMeals = meals }, HonorSeries.Meals) == earned, $"Meals {meals}.");
    }),
    ("Affection tiers unlock exactly at ten fifty hundred", () =>
    {
        foreach (var (pets, earned) in new[] { (0, 0), (9, 0), (10, 1), (49, 1), (50, 2), (99, 2), (100, 3) })
            Require(Count(new PetState { TotalPets = pets }, HonorSeries.Affection) == earned, $"Pets {pets}.");
    }),
    ("Growth tiers use derived level two five ten", () =>
    {
        foreach (var (xp, earned) in new[] { (0, 0), (99, 0), (100, 1), (399, 1), (400, 2), (899, 2), (900, 3) })
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
    ("Full progress earns all nine with clamped bars", () =>
    {
        var all = HonorCatalog.Evaluate(new PetState { TotalMeals = int.MaxValue, TotalPets = int.MaxValue, Experience = int.MaxValue });
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
