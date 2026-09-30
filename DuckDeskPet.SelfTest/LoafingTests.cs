using System.Text.Json;
using DuckDeskPet.Core;

namespace DuckDeskPet.SelfTest;

internal static class LoafingTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
    public static IEnumerable<(string Name, Action Body)> Cases => new (string, Action)[]
    {
        ("Loafing first lottery is exactly one productive hour", FirstBoundary),
        ("Failed loafing lottery retries only after five productive minutes", RetryBoundary),
        ("Loafing freezes money and all XP but still accumulates food", UnpaidBreak),
        ("Restart preserves the chosen break and its remaining half-hour", Restart),
        ("Cancel clears the break without resetting its random stream", Cancel),
        ("Capped offline catch-up expires old breaks without more lotteries", Offline),
        ("Loafing is deterministic across ticks and JSON restarts", Cadence),
        ("Clock rollback cannot replay a lottery or break income", Rollback),
        ("Deferred startup performs no lottery until equipment is known", Deferred),
        ("Optional v4 break fields normalize without clearing the wallet", Normalize),
        ("Grain completed during a break awards no delayed experience", GrainBoundary),
    };

    private static PetState Initial() => new()
    {
        LastUpdatedUtc = Start, WageLastUpdatedUtc = Start, Food = 0, LoafingRandomState = 1,
    };
    private static PetCareService Working(Func<ulong, ulong>? step = null)
    {
        var care = new PetCareService(Initial(), Start, loafingRandomStep: step ?? (_ => 100UL));
        care.StartWork(Start);
        return care;
    }
    private static PetState RoundTrip(PetState state) => JsonSerializer.Deserialize<PetState>(JsonSerializer.Serialize(state))!;

    private static void FirstBoundary()
    {
        int draws = 0;
        var care = Working(_ => { draws++; return draws == 1 ? 119UL : 102UL; });
        long revision = care.LoafingScheduleRevision;
        care.Advance(Start.AddTicks(TimeSpan.TicksPerHour - 1));
        Equal(0, draws); Equal(revision, care.LoafingScheduleRevision); Check(!care.State.IsLoafing);
        care.Advance(Start.AddHours(1));
        Equal(2, draws); Equal(LoafingKind.NosePicking, care.State.Loafing);
        Equal(1800m, care.State.LoafingRemainingSeconds); Check(care.State.IsWorking && !care.State.IsBusy);
        Equal(revision + 1, care.LoafingScheduleRevision);
        for (int i = 0; i < 10; i++) care.Advance(Start.AddHours(1));
        Equal(2, draws);
    }

    private static void RetryBoundary()
    {
        static ulong Step(ulong state) => state switch { 1 => 120, 120 => 101, _ => 103 };
        var care = Working(Step);
        care.Advance(Start.AddHours(1));
        Check(!care.State.IsLoafing); Equal(300m, care.State.WorkUntilLoafingCheckSeconds);
        care.Advance(Start.AddMinutes(65).AddTicks(-1));
        Equal(120UL, care.State.LoafingRandomState); Check(!care.State.IsLoafing);
        care.Advance(Start.AddMinutes(65));
        Equal(LoafingKind.WoodenFish, care.State.Loafing); Equal(1800m, care.State.LoafingRemainingSeconds);
    }

    private static void UnpaidBreak()
    {
        var care = Working(); care.Advance(Start.AddHours(1));
        Equal(49.50m, care.State.Coins); Equal(72, care.State.Experience); Equal(12, care.State.Food);
        Equal(0m, care.MoneyPerWorkMinute); Equal(0m, care.WorkExperiencePerMinute);
        var before = care.State.CreateSnapshot();
        care.Advance(Start.AddMinutes(90));
        Equal(before.Coins, care.State.Coins); Equal(before.Experience, care.State.Experience);
        Equal(before.WageRemainderScaledUnits, care.State.WageRemainderScaledUnits);
        Equal(before.WorkExperienceRemainderUnits, care.State.WorkExperienceRemainderUnits);
        Equal(18, care.State.Food); Near(59.5, care.State.Mood); Near(3600, care.State.TotalWorkSeconds);
        Near(3600, care.State.WorkSessionSeconds); Check(care.State.IsBusy && !care.State.IsLoafing);
        Equal(3600m, care.State.WorkUntilLoafingCheckSeconds);
        care.Advance(Start.AddMinutes(91));
        Equal(50.29m, care.State.Coins); Equal(73, care.State.Experience); Near(3660, care.State.TotalWorkSeconds);
    }

    private static void Restart()
    {
        var care = Working(); care.Advance(Start.AddHours(1));
        var reopened = new PetCareService(RoundTrip(care.State), Start.AddMinutes(70), loafingRandomStep: _ => throw new Exception("Unexpected reroll"));
        Check(!reopened.MigrationWasApplied); Equal(49.5m, reopened.State.Coins); Equal(72, reopened.State.Experience);
        Equal(1200m, reopened.State.LoafingRemainingSeconds); Equal(care.State.Loafing, reopened.State.Loafing);
        Equal(care.State.LoafingRandomState, reopened.State.LoafingRandomState);
        reopened.Advance(Start.AddMinutes(90)); Check(!reopened.State.IsLoafing);
        reopened.Advance(Start.AddMinutes(149)); Check(!reopened.State.IsLoafing); Equal(60m, reopened.State.WorkUntilLoafingCheckSeconds);
    }

    private static void Cancel()
    {
        var care = Working(); care.Advance(Start.AddHours(1)); ulong seed = care.State.LoafingRandomState;
        care.StopWork(Start.AddMinutes(70));
        Check(!care.State.IsWorking && !care.State.IsLoafing); Equal(0m, care.State.LoafingRemainingSeconds);
        Equal(3600m, care.State.WorkUntilLoafingCheckSeconds); Equal(seed, care.State.LoafingRandomState);
        care.StartWork(Start.AddMinutes(70)); care.StartWork(Start.AddMinutes(71));
        Equal(3540m, care.State.WorkUntilLoafingCheckSeconds); Near(60, care.State.WorkSessionSeconds);
        care.Advance(Start.AddMinutes(129)); Check(!care.State.IsLoafing);
        care.Advance(Start.AddMinutes(130)); Check(care.State.IsLoafing);
    }

    private static void Offline()
    {
        int draws = 0;
        var state = Initial(); state.IsWorking = true; state.WorkUntilLoafingCheckSeconds = 600m;
        var care = new PetCareService(state, Start.AddDays(3), loafingRandomStep: _ => { draws++; return 100UL; });
        Equal(4, draws); Check(!care.State.IsLoafing && care.State.IsWorking);
        Near(4200, care.State.TotalWorkSeconds); Equal(84, care.State.Experience); Equal(24, care.State.Food);
        Equal(3600m, care.State.WorkUntilLoafingCheckSeconds); Equal(Start.AddDays(3), care.State.LastUpdatedUtc);
        var same = new PetCareService(RoundTrip(care.State), Start.AddDays(3));
        Equal(care.State.Coins, same.State.Coins); Equal(care.State.Experience, same.State.Experience);
    }

    private static void Cadence()
    {
        var initial = Initial(); initial.LoafingRandomState = 0xA153112358UL;
        var slow = new PetCareService(initial.CreateSnapshot(), Start); slow.StartWork(Start);
        var fast = new PetCareService(initial.CreateSnapshot(), Start); fast.StartWork(Start);
        for (int hour = 2; hour <= 6; hour += 2) slow.Advance(Start.AddHours(hour));
        long elapsed = 0, end = 6 * TimeSpan.TicksPerHour; int count = 0; var random = new Random(471);
        while (elapsed < end)
        {
            elapsed = Math.Min(end, elapsed + random.NextInt64(1, 70_000_000));
            fast.Advance(Start.AddTicks(elapsed));
            if (++count % 47 == 0) fast = new PetCareService(RoundTrip(fast.State), Start.AddTicks(elapsed));
        }
        Equal(slow.State.Loafing, fast.State.Loafing); Equal(slow.State.LoafingRandomState, fast.State.LoafingRandomState);
        Equal(slow.State.WorkUntilLoafingCheckSeconds, fast.State.WorkUntilLoafingCheckSeconds);
        Equal(slow.State.LoafingRemainingSeconds, fast.State.LoafingRemainingSeconds);
        Equal(slow.State.Coins, fast.State.Coins); Equal(slow.State.Experience, fast.State.Experience);
        Equal(slow.State.Food, fast.State.Food); Equal(slow.State.MoodUnits, fast.State.MoodUnits);
        Equal(slow.State.WageRemainderScaledUnits, fast.State.WageRemainderScaledUnits);
        Equal(slow.State.WorkExperienceRemainderUnits, fast.State.WorkExperienceRemainderUnits);
        Near(slow.State.TotalWorkSeconds, fast.State.TotalWorkSeconds, 1e-6);
        Check(slow.State.TotalWorkSeconds < 6 * 3600);
    }

    private static void Rollback()
    {
        var care = Working(); care.Advance(Start.AddMinutes(70));
        var old = care.State.CreateSnapshot();
        var reopened = new PetCareService(RoundTrip(old), Start.AddMinutes(30), loafingRandomStep: _ => throw new Exception("Rollback rerolled"));
        reopened.Advance(Start.AddMinutes(70));
        Equal(old.Coins, reopened.State.Coins); Equal(old.Experience, reopened.State.Experience);
        Equal(old.LoafingRemainingSeconds, reopened.State.LoafingRemainingSeconds); Equal(old.MoodUnits, reopened.State.MoodUnits);
        reopened.Advance(Start.AddMinutes(90));
        Equal(old.Coins, reopened.State.Coins); Check(!reopened.State.IsLoafing);
    }

    private static void Deferred()
    {
        int draws = 0; var initial = Initial(); initial.IsWorking = true;
        var care = new PetCareService(initial, Start.AddHours(1), deferInitialAdvance: true,
            loafingRandomStep: _ => { draws++; return 100UL; });
        care.Advance(Start.AddHours(1)); Equal(0, draws); Equal(0m, care.State.Coins);
        care.BeginCatchUp(Start.AddHours(1), new Dictionary<ContentSlot, string>());
        Equal(2, draws); Check(care.State.IsLoafing); Equal(49.5m, care.State.Coins);
        Check(!care.BeginCatchUp(Start.AddMinutes(70), new Dictionary<ContentSlot, string>())); Equal(2, draws);
    }

    private static void Normalize()
    {
        var initial = Initial(); initial.Coins = 123.45m; initial.IsWorking = true;
        initial.Loafing = (LoafingKind)999; initial.LoafingRemainingSeconds = decimal.MaxValue;
        initial.WorkUntilLoafingCheckSeconds = -1; initial.LoafingRandomState = 0;
        var care = new PetCareService(initial, Start);
        Equal(4, care.State.Version); Check(!care.MigrationWasApplied); Equal(123.45m, care.State.Coins);
        Equal(LoafingKind.None, care.State.Loafing); Equal(0m, care.State.LoafingRemainingSeconds);
        Equal(3600m, care.State.WorkUntilLoafingCheckSeconds); Check(care.State.LoafingRandomState != 0);
        var oldV4 = JsonSerializer.Deserialize<PetState>("{\"Version\":4,\"Coins\":4.25}")!;
        Equal(4.25m, new PetCareService(oldV4, Start).State.Coins);
    }

    private static void GrainBoundary()
    {
        var initial = Initial(); initial.IsWorking = true; initial.Loafing = LoafingKind.WoodenFish;
        initial.LoafingRemainingSeconds = 2m; initial.FoodProgressSeconds = 299;
        var care = new PetCareService(initial, Start);
        care.Advance(Start.AddSeconds(1)); Equal(1, care.State.Food); Equal(0, care.State.Experience); Equal(0m, care.State.Coins);
        care.Advance(Start.AddSeconds(2)); Check(!care.State.IsLoafing); Equal(0, care.State.Experience);
        care.Advance(Start.AddSeconds(3)); Equal(0, care.State.Experience); Near(1, care.State.TotalWorkSeconds);
    }

    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Loafing assertion failed"); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}");
    }
    private static void Near(double expected, double actual, double tolerance = 1e-8)
    {
        if (Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"Expected {expected}; actual {actual}");
    }
}
