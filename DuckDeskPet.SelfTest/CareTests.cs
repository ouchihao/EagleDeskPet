using System.Text.Json;
using DuckDeskPet.Core;

namespace DuckDeskPet.SelfTest;

internal static class CareTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private const double Step = 1.0 / 60.0;

    public static IEnumerable<(string Name, Action Body)> Cases => new (string, Action)[]
    {
        ("Care starts with bounded local progress", InitialState),
        ("Care grain cadence is independent of frame rate", FoodCadence),
        ("Care catch-up is capped and not awarded twice", CatchUp),
        ("Care full bags do not bank refill rewards", FullBag),
        ("Feeding consumes food and rejects full or empty", Feeding),
        ("Petting cooldown prevents reward spam", Petting),
        ("Care clock rollback cannot create rewards", ClockRollback),
        ("Care save JSON round-trips and normalizes", Persistence),
        ("Care achievements unlock once", Achievements),
        ("Behavior reactions preserve endpoints and idle beats", SafeReaction),
        ("Behavior repeated actions are not discarded", RepeatedReaction),
        ("Behavior queue is bounded and prioritizes interaction", QueueBounds),
        ("Behavior pause rejects requests and clears stale work", Pause),
        ("Notification display data is bounded Unicode", Notifications),
    };

    private static void InitialState()
    {
        var care = new PetCareService(null, Start);
        Equal(5, care.State.Food);
        Near(75.0, care.State.Fullness);
        Equal(1, care.State.Level);
        Equal(Start, care.State.LastUpdatedUtc);
        Check(!care.State.IsHungry, "A new pet unexpectedly starts hungry.");
        Check(!care.Advance(Start), "An identical clock reading changed progress.");
    }

    private static void FoodCadence()
    {
        var slow = new PetCareService(null, Start);
        var fast = new PetCareService(null, Start);
        slow.Advance(Start.AddSeconds(900));
        for (int i = 1; i <= 9000; i++)
        {
            fast.Advance(Start.AddMilliseconds(i * 100));
        }

        Equal(8, slow.State.Food);
        Equal(slow.State.Food, fast.State.Food);
        Equal(3, fast.State.Experience);
        Near(slow.State.Fullness, fast.State.Fullness, 1e-8);
        Near(0.0, fast.State.FoodProgressSeconds, 1e-6);
        fast.Advance(Start.AddSeconds(1199));
        Equal(8, fast.State.Food);
        fast.Advance(Start.AddSeconds(1200));
        Equal(9, fast.State.Food);
    }

    private static void CatchUp()
    {
        var saved = new PetState { LastUpdatedUtc = Start };
        DateTimeOffset afterAbsence = Start.AddDays(7);
        var care = new PetCareService(saved, afterAbsence);
        Equal(29, care.State.Food);
        Near(67.0, care.State.Fullness);
        Equal(afterAbsence, care.State.LastUpdatedUtc);
        var reopened = new PetCareService(care.State, afterAbsence);
        Equal(29, reopened.State.Food);
        Equal(24, reopened.State.Experience);
        reopened.Advance(afterAbsence.AddMinutes(5));
        Equal(30, reopened.State.Food);
    }

    private static void FullBag()
    {
        var care = new PetCareService(new PetState
        {
            Food = 98, Fullness = 40.0, FoodProgressSeconds = 299.0, LastUpdatedUtc = Start,
        }, Start);
        care.Advance(Start.AddHours(2));
        Equal(99, care.State.Food);
        Equal(1, care.State.Experience);
        Near(0.0, care.State.FoodProgressSeconds);
        Check(care.Feed(Start.AddHours(2)).Success, "A hungry pet rejected food.");
        Equal(98, care.State.Food);
        care.Advance(Start.AddHours(2).AddSeconds(1));
        Equal(98, care.State.Food);
    }

    private static void Feeding()
    {
        var care = new PetCareService(null, Start);
        PetCareResult result = care.Feed(Start);
        Equal(PetCareStatus.Fed, result.Status);
        Equal(4, care.State.Food);
        Near(100.0, care.State.Fullness);
        Near(78.0, care.State.Mood);
        Equal(5, care.State.Experience);
        Equal(PetCareStatus.Cooldown, care.Feed(Start.AddSeconds(1)).Status);
        Equal(PetCareStatus.Full, care.Feed(Start.AddSeconds(3)).Status);
        Equal(4, care.State.Food);
        var empty = new PetCareService(new PetState { Food = 0, Fullness = 0, LastUpdatedUtc = Start }, Start);
        Equal(PetCareStatus.NoFood, empty.Feed(Start).Status);
        Equal(0, empty.State.Food);
        Near(0.0, empty.State.Fullness);
    }

    private static void Petting()
    {
        var care = new PetCareService(null, Start);
        Equal(PetCareStatus.Petted, care.Pet(Start).Status);
        for (int i = 0; i < 50; i++)
        {
            Equal(PetCareStatus.Cooldown, care.Pet(Start.AddSeconds(9)).Status);
        }

        Equal(1, care.State.Experience);
        Equal(1, care.State.TotalPets);
        Equal(PetCareStatus.Petted, care.Pet(Start.AddSeconds(10)).Status);
        Equal(2, care.State.Experience);
    }

    private static void ClockRollback()
    {
        var care = new PetCareService(null, Start);
        care.Advance(Start.AddMinutes(5));
        Equal(6, care.State.Food);
        double fullness = care.State.Fullness;
        care.Advance(Start.AddHours(-2));
        care.Advance(Start.AddMinutes(5));
        Equal(6, care.State.Food);
        Near(fullness, care.State.Fullness);
        care.Pet(Start.AddMinutes(5));
        Equal(PetCareStatus.Cooldown, care.Pet(Start.AddHours(-1)).Status);
        care.Advance(Start.AddMinutes(10));
        Equal(7, care.State.Food);
    }

    private static void Persistence()
    {
        var care = new PetCareService(null, Start);
        care.Feed(Start);
        string json = JsonSerializer.Serialize(care.State);
        PetState restored = JsonSerializer.Deserialize<PetState>(json)!;
        var reopened = new PetCareService(restored, Start);
        Equal(4, reopened.State.Food);
        Equal(5, reopened.State.Experience);
        Check(reopened.State.Achievements.Contains("first-meal"), "Achievement was not persisted.");
        Check(!json.Contains("\"Level\"", StringComparison.Ordinal), "Derived level leaked into save data.");
        restored.Achievements.Clear();
        Check(reopened.State.Achievements.Count == 1, "Save normalization retained the mutable source list.");

        var invalid = new PetCareService(new PetState
        {
            Food = int.MaxValue, Experience = int.MaxValue, Fullness = double.NaN, Mood = -100,
            FoodProgressSeconds = double.PositiveInfinity, LastUpdatedUtc = Start.AddYears(5),
            Achievements = new() { "first-meal", "first-meal", "arbitrary-data" },
        }, Start);
        Equal(99, invalid.State.Food);
        Equal(1000, invalid.State.Level);
        Near(75, invalid.State.Fullness);
        Near(0, invalid.State.Mood);
        Near(0, invalid.State.FoodProgressSeconds);
        Equal(Start, invalid.State.LastUpdatedUtc);
        // Normalization records every milestone proven by the bounded counters;
        // maximum XP proves the three growth tiers, but no arbitrary or duplicate flag.
        var expectedHonors = new[] { "first-meal", "level-2", "growth-silver", "growth-gold" };
        Equal(expectedHonors.Length, invalid.State.Achievements.Count);
        Check(expectedHonors.All(id => invalid.State.Achievements.Count(actual => actual == id) == 1),
            "Normalization failed to deduplicate known honors, discard unknown IDs, or record proven growth tiers.");
        Throws<NotSupportedException>(() => new PetCareService(new PetState { Version = 999 }, Start));
    }

    private static void Achievements()
    {
        var care = new PetCareService(new PetState { Experience = 99, LastUpdatedUtc = Start }, Start);
        for (int i = 0; i < 10; i++)
        {
            care.Pet(Start.AddSeconds(10 * i));
        }

        Equal(2, care.State.Level);
        Equal(1, care.State.Achievements.Count(id => id == "level-2"));
        Equal(1, care.State.Achievements.Count(id => id == "gentle-hands"));
    }

    private static void SafeReaction()
    {
        var controller = new PetBehaviorController();
        controller.RequestAction(ClipKind.Yawn);
        StartAfterIdle(controller, ClipKind.Yawn);
        for (int i = 0; i < 30; i++) controller.Advance(Step);
        ClipSample before = controller.CurrentSample;
        Equal(PetBehaviorRequestResult.Queued, controller.QueueReaction(PetBehaviorKind.Fed));
        Equal(before, controller.CurrentSample);
        ToTerminal(controller, ClipKind.Yawn);
        Equal(1, controller.PendingCount);
        Equal(ClipKind.Idle, controller.Advance(Step).Kind);
        StartAfterIdle(controller, ClipKind.Eat);
        Equal(0, controller.PendingCount);
        ToTerminal(controller, ClipKind.Eat);
        Near(1, controller.CurrentSample.Progress);
    }

    private static void RepeatedReaction()
    {
        var controller = new PetBehaviorController();
        controller.QueueReaction(PetBehaviorKind.Petted);
        StartAfterIdle(controller, ClipKind.Shy);
        controller.QueueReaction(PetBehaviorKind.Petted);
        Equal(PetBehaviorRequestResult.Coalesced, controller.QueueReaction(PetBehaviorKind.Petted));
        ToTerminal(controller, ClipKind.Shy);
        controller.Advance(Step);
        StartAfterIdle(controller, ClipKind.Shy);
        Equal(2L, controller.CurrentSample.Sequence);
    }

    private static void QueueBounds()
    {
        var controller = new PetBehaviorController();
        controller.QueueReaction(PetBehaviorKind.Notification);
        controller.QueueReaction(PetBehaviorKind.Fed);
        StartAfterIdle(controller, ClipKind.Eat);
        Equal(1, controller.PendingCount);

        var bounded = new PetBehaviorController();
        for (int i = 1; i <= PetBehaviorController.MaximumPendingBehaviors; i++)
        {
            Equal(PetBehaviorRequestResult.Queued, bounded.RequestAction((ClipKind)i));
        }

        Equal(8, bounded.PendingCount);
        Equal(PetBehaviorRequestResult.RejectedFull, bounded.RequestAction(ClipKind.Stretch));
        Equal(PetBehaviorRequestResult.RejectedFull, bounded.QueueReaction(PetBehaviorKind.Notification));
        Equal(PetBehaviorRequestResult.Coalesced, bounded.RequestAction(ClipKind.Bomb));
        Throws<ArgumentOutOfRangeException>(() => bounded.RequestAction(ClipKind.Idle));
        Throws<ArgumentOutOfRangeException>(() => bounded.Advance(double.NaN));
    }

    private static void Pause()
    {
        var controller = new PetBehaviorController();
        controller.QueueReaction(PetBehaviorKind.Fed);
        controller.SetPaused(true);
        Equal(0, controller.PendingCount);
        Equal(PetBehaviorRequestResult.RejectedPaused, controller.QueueReaction(PetBehaviorKind.Petted));
        Equal(ClipPlaybackPhase.Paused, controller.Advance(Step).Phase);
        controller.SetPaused(false);
        controller.QueueReaction(PetBehaviorKind.Petted);
        StartAfterIdle(controller, ClipKind.Shy);
    }

    private static void Notifications()
    {
        PetNotification note = PetNotification.Create("  Codex\r\n", new string('哈', 179) + "🦉后续", "\0", "session");
        Equal("Codex", note.Source);
        Equal(179, note.Message.Length);
        Equal<string?>(null, note.EventId);
        Equal("session", note.SessionId);
        Equal("AI", PetNotification.Create(null, "\0").Source);
        Check(PetNotification.Create(null, "\0").Message.Length > 0, "Empty notification has no fallback.");
    }

    private static void StartAfterIdle(PetBehaviorController controller, ClipKind expected)
    {
        for (int i = 0; i < 119; i++)
        {
            Equal(ClipKind.Idle, controller.Advance(Step).Kind);
        }

        ClipSample started = controller.Advance(Step);
        Equal(expected, started.Kind);
        Equal(ClipPlaybackPhase.Playing, started.Phase);
        Near(0, started.Progress);
    }

    private static void ToTerminal(PetBehaviorController controller, ClipKind expected)
    {
        double previous = controller.CurrentSample.Progress;
        for (int i = 0; i < 125 && controller.CurrentSample.Phase != ClipPlaybackPhase.Terminal; i++)
        {
            ClipSample sample = controller.Advance(Step);
            Equal(expected, sample.Kind);
            Check(sample.Progress >= previous, "Action moved backwards.");
            Check(sample.Progress - previous <= Step / 2.0 + 1e-10, "Action skipped an authored frame.");
            previous = sample.Progress;
        }

        Equal(ClipPlaybackPhase.Terminal, controller.CurrentSample.Phase);
        Near(1.0, controller.CurrentSample.Progress);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }

    private static void Near(double expected, double actual, double tolerance = 1e-9)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
