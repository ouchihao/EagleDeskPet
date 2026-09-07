using System.Text.Json;
using DuckDeskPet.Core;

namespace DuckDeskPet.SelfTest;

internal static class WorkTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private const double Step = 1.0 / 60.0;

    public static IEnumerable<(string Name, Action Body)> Cases => new (string, Action)[]
    {
        ("Idle group contains only standing and Yawn", GroupContract),
        ("Work phases are authored at 60 Hz with complete endpoints", SceneFrames),
        ("Work loops continuously and switches to busy only at a boundary", SceneLoops),
        ("Cancellation completes entry or bridge before the matching exit", EarlyCancellation),
        ("Work waits for an ordinary action to finish and returns to standing", ControllerLifecycle),
        ("Work pause holds its scene and rejects unrelated reactions", WorkPause),
        ("Work economy uses wall time and becomes busy at thirty minutes", WorkEconomy),
        ("Work accounting is invariant to tick rate", WorkTickRate),
        ("Work hunger exhaustion splits catch-up at the exact stop time", HungerStop),
        ("Work cancellation and restart preserve partial XP without duplicate credit", CancelAccounting),
        ("Work saves resume with bounded catch-up and normalize invalid state", WorkPersistence),
        ("Work interactions reject without spending food or rewards", WorkInteractions),
    };

    private static void GroupContract()
    {
        foreach (ClipKind kind in Enum.GetValues<ClipKind>())
        {
            Equal(kind is ClipKind.Idle or ClipKind.Yawn ? PetActionGroup.Idle : PetActionGroup.Interaction,
                ClipCatalog.GetGroup(kind));
            Equal(kind == ClipKind.Yawn, ClipCatalog.IsAutomaticAction(kind));
        }

        Throws<ArgumentOutOfRangeException>(() => new ClipTimeline().RequestClip(ClipKind.WorkLoop));
        Throws<ArgumentOutOfRangeException>(() => new PetBehaviorController().RequestAction(ClipKind.BusyLoop));
    }

    private static void SceneFrames()
    {
        var expected = new[]
        {
            (ClipKind.WorkEnter, 211, 3.5), (ClipKind.WorkLoop, 121, 2.0),
            (ClipKind.WorkToBusy, 91, 1.5), (ClipKind.BusyLoop, 121, 2.0),
            (ClipKind.WorkExit, 121, 2.0), (ClipKind.BusyExit, 121, 2.0),
        };
        foreach (var (kind, frames, duration) in expected)
        {
            ClipDefinition definition = ClipCatalog.GetDefinition(kind);
            Equal(frames, definition.FrameCount);
            Near(duration, definition.DurationSeconds);
            Near(frames - 1, duration * 60.0);
        }

        var scene = new WorkSceneTimeline();
        scene.Start(false);
        PlayPhase(scene, ClipKind.WorkEnter);
        Equal(ClipKind.WorkLoop, scene.Advance(Step).Kind);
        PlayPhase(scene, ClipKind.WorkLoop);
        scene.SetDesiredState(true, true);
        Equal(ClipKind.WorkToBusy, scene.Advance(Step).Kind);
        PlayPhase(scene, ClipKind.WorkToBusy);
        Equal(ClipKind.BusyLoop, scene.Advance(Step).Kind);
        PlayPhase(scene, ClipKind.BusyLoop);
        scene.SetDesiredState(false, false);
        Equal(ClipKind.BusyExit, scene.Advance(Step).Kind);
        PlayPhase(scene, ClipKind.BusyExit);
        Equal(ClipKind.Idle, scene.Advance(Step).Kind);
        Check(!scene.IsActive, "Exit did not remove the work scene.");
    }

    private static void SceneLoops()
    {
        var scene = new WorkSceneTimeline();
        scene.Start(false);
        PlayPhase(scene, ClipKind.WorkEnter);
        scene.Advance(Step);
        for (int loop = 0; loop < 6; loop++)
        {
            PlayPhase(scene, ClipKind.WorkLoop);
            Equal(ClipKind.WorkLoop, scene.Advance(Step).Kind);
            Near(0.0, scene.CurrentSample.Progress);
        }

        for (int i = 0; i < 35; i++) scene.Advance(Step);
        ClipSample before = scene.CurrentSample;
        scene.SetDesiredState(true, true);
        Equal(before, scene.CurrentSample);
        FinishPhase(scene, ClipKind.WorkLoop);
        Equal(ClipKind.WorkToBusy, scene.Advance(Step).Kind);
        PlayPhase(scene, ClipKind.WorkToBusy);
        Equal(ClipKind.BusyLoop, scene.Advance(Step).Kind);
        for (int loop = 0; loop < 6; loop++)
        {
            PlayPhase(scene, ClipKind.BusyLoop);
            Equal(ClipKind.BusyLoop, scene.Advance(Step).Kind);
        }
    }

    private static void EarlyCancellation()
    {
        var entry = new WorkSceneTimeline();
        entry.Start(false);
        entry.Advance(Step);
        entry.SetDesiredState(false, false);
        FinishPhase(entry, ClipKind.WorkEnter);
        Equal(ClipKind.WorkExit, entry.Advance(Step).Kind);
        PlayPhase(entry, ClipKind.WorkExit);
        Equal(ClipKind.Idle, entry.Advance(Step).Kind);

        var bridge = new WorkSceneTimeline();
        bridge.Start(true);
        PlayPhase(bridge, ClipKind.WorkEnter);
        Equal(ClipKind.WorkToBusy, bridge.Advance(Step).Kind);
        bridge.Advance(Step);
        bridge.SetDesiredState(false, false);
        FinishPhase(bridge, ClipKind.WorkToBusy);
        Equal(ClipKind.BusyExit, bridge.Advance(Step).Kind);
    }

    private static void ControllerLifecycle()
    {
        var controller = new PetBehaviorController();
        controller.QueueReaction(PetBehaviorKind.Petted);
        for (int i = 0; i < 120; i++) controller.Advance(Step);
        Equal(ClipKind.Shy, controller.CurrentSample.Kind);
        for (int i = 0; i < 30; i++) controller.Advance(Step);
        controller.QueueReaction(PetBehaviorKind.Notification);
        controller.SetWorkState(true, false);
        Equal(0, controller.PendingCount);
        Equal(PetBehaviorRequestResult.RejectedWorking, controller.QueueReaction(PetBehaviorKind.Fed));
        for (int i = 31; i <= 120; i++)
        {
            Equal(ClipKind.Shy, controller.Advance(Step).Kind);
        }
        Equal(ClipPlaybackPhase.Terminal, controller.CurrentSample.Phase);
        Equal(ClipKind.Idle, controller.Advance(Step).Kind);
        Equal(ClipKind.WorkEnter, controller.Advance(Step).Kind);
        Check(controller.IsWorkSceneActive, "Work scene did not activate.");
        for (int i = 0; i < 211; i++) controller.Advance(Step);
        Equal(ClipKind.WorkLoop, controller.CurrentSample.Kind);
        for (int i = 0; i < 20; i++) controller.Advance(Step);
        controller.SetWorkState(false, false);
        for (int i = 20; i < 120; i++) Equal(ClipKind.WorkLoop, controller.Advance(Step).Kind);
        Equal(ClipKind.WorkExit, controller.Advance(Step).Kind);
        for (int i = 0; i < 120; i++) Equal(ClipKind.WorkExit, controller.Advance(Step).Kind);
        Equal(ClipKind.Idle, controller.Advance(Step).Kind);
        Check(!controller.IsWorkSceneActive, "Work exit still owns the renderer.");
        for (int i = 0; i < 119; i++) Equal(ClipKind.Idle, controller.Advance(Step).Kind);
        Equal(ClipKind.Yawn, controller.Advance(Step).Kind);
    }

    private static void WorkPause()
    {
        var controller = new PetBehaviorController();
        controller.SetWorkState(true, false);
        controller.Advance(Step);
        for (int i = 0; i < 75; i++) controller.Advance(Step);
        ClipSample before = controller.CurrentSample;
        controller.SetPaused(true);
        for (int i = 0; i < 120; i++) Equal(before, controller.Advance(Step));
        Equal(PetBehaviorRequestResult.RejectedPaused, controller.RequestAction(ClipKind.Bomb));
        controller.SetWorkState(false, false);
        Equal(before, controller.CurrentSample);
        controller.SetPaused(false);
        Equal(ClipKind.WorkEnter, controller.Advance(Step).Kind);
        Near(before.FrameCoordinate + 1, controller.CurrentSample.FrameCoordinate);
    }

    private static void WorkEconomy()
    {
        var care = new PetCareService(null, Start);
        Equal(PetCareStatus.WorkStarted, care.StartWork(Start).Status);
        care.Advance(Start.AddSeconds(1799));
        Check(!care.State.IsBusy, "Busy began before thirty minutes.");
        care.Advance(Start.AddSeconds(1800));
        Check(care.State.IsBusy, "Busy did not begin at thirty minutes.");
        Near(1800, care.State.WorkSessionSeconds);
        Near(1800, care.State.TotalWorkSeconds);
        Near(69, care.State.Fullness);
        Near(65, care.State.Mood);
        Equal(36, care.State.Experience); // 30 work XP + 6 ambient food XP.
        Equal(11, care.State.Food);
        care.Advance(Start.AddSeconds(1800));
        Equal(36, care.State.Experience);
        care.Advance(Start.AddHours(-1));
        Equal(36, care.State.Experience);
    }

    private static void WorkTickRate()
    {
        var slow = new PetCareService(null, Start);
        var fast = new PetCareService(null, Start);
        slow.StartWork(Start);
        fast.StartWork(Start);
        slow.Advance(Start.AddHours(1));
        for (int i = 1; i <= 36000; i++) fast.Advance(Start.AddMilliseconds(i * 100));
        Near(slow.State.Fullness, fast.State.Fullness, 1e-7);
        Near(slow.State.Mood, fast.State.Mood, 1e-7);
        Near(slow.State.WorkSessionSeconds, fast.State.WorkSessionSeconds, 1e-7);
        Equal(slow.State.Experience, fast.State.Experience);
        Equal(slow.State.Food, fast.State.Food);
        Check(fast.State.IsBusy, "Frequent ticks prevented the busy threshold.");
    }

    private static void HungerStop()
    {
        var care = new PetCareService(new PetState { Fullness = 0.5, LastUpdatedUtc = Start }, Start);
        care.StartWork(Start);
        care.Advance(Start.AddHours(1));
        Check(!care.State.IsWorking, "Empty fullness did not stop work.");
        Near(150, care.State.WorkSessionSeconds);
        Near(150, care.State.TotalWorkSeconds);
        Near(30, care.State.WorkExperienceProgressSeconds);
        Near(0, care.State.Fullness);
        Near(70 - 150 * 10.0 / 3600.0 - 3450.0 / 3600.0, care.State.Mood);
        Equal(14, care.State.Experience); // Two work minutes, twelve ambient food credits.
        Equal(PetCareStatus.TooHungryToWork, care.StartWork(Start.AddHours(1)).Status);
        care.Advance(Start.AddHours(2));
        Near(150, care.State.TotalWorkSeconds);
        Equal(26, care.State.Experience);
    }

    private static void CancelAccounting()
    {
        var care = new PetCareService(null, Start);
        care.StartWork(Start);
        Equal(PetCareStatus.AlreadyWorking, care.StartWork(Start.AddSeconds(35)).Status);
        Near(35, care.State.WorkSessionSeconds);
        Equal(PetCareStatus.WorkStopped, care.StopWork(Start.AddSeconds(35)).Status);
        Equal(PetCareStatus.NotWorking, care.StopWork(Start.AddSeconds(35)).Status);
        care.StartWork(Start.AddMinutes(1));
        Near(0, care.State.WorkSessionSeconds);
        care.Advance(Start.AddSeconds(85));
        Equal(1, care.State.Experience);
        Near(60, care.State.TotalWorkSeconds);
        Near(25, care.State.WorkSessionSeconds);
        Near(0, care.State.WorkExperienceProgressSeconds);
    }

    private static void WorkPersistence()
    {
        var care = new PetCareService(null, Start);
        care.StartWork(Start);
        care.Advance(Start.AddMinutes(10));
        var saved = JsonSerializer.Deserialize<PetState>(JsonSerializer.Serialize(care.State));
        DateTimeOffset reopenedAt = Start.AddDays(1);
        var reopened = new PetCareService(saved, reopenedAt);
        Check(reopened.State.IsWorking && reopened.State.IsBusy, "Persisted work did not resume.");
        Near(7800, reopened.State.WorkSessionSeconds);
        Near(49, reopened.State.Fullness);
        int experience = reopened.State.Experience;
        var twice = new PetCareService(reopened.State, reopenedAt);
        Equal(experience, twice.State.Experience);
        Near(7800, twice.State.TotalWorkSeconds);

        var malformed = new PetCareService(new PetState
        {
            IsWorking = true, Fullness = 0, WorkSessionSeconds = double.NaN,
            WorkExperienceProgressSeconds = double.PositiveInfinity,
            TotalWorkSeconds = -1, LastUpdatedUtc = Start,
        }, Start);
        Check(!malformed.State.IsWorking, "An empty saved pet resumed work.");
        Near(0, malformed.State.WorkSessionSeconds);
        Near(0, malformed.State.WorkExperienceProgressSeconds);
        Near(0, malformed.State.TotalWorkSeconds);
    }

    private static void WorkInteractions()
    {
        var care = new PetCareService(null, Start);
        care.StartWork(Start);
        Equal(PetCareStatus.Working, care.Feed(Start).Status);
        Equal(PetCareStatus.Working, care.Pet(Start).Status);
        Equal(5, care.State.Food);
        Equal(0, care.State.Experience);
        care.StopWork(Start);
        Equal(PetCareStatus.Fed, care.Feed(Start).Status);
        Equal(PetCareStatus.Petted, care.Pet(Start).Status);
    }

    private static void PlayPhase(WorkSceneTimeline scene, ClipKind kind)
    {
        Near(0, scene.CurrentSample.Progress);
        int frames = ClipCatalog.GetDefinition(kind).FrameCount;
        for (int i = 1; i < frames; i++)
        {
            ClipSample sample = scene.Advance(Step);
            Equal(kind, sample.Kind);
            Near(i, sample.FrameCoordinate, 1e-8);
        }
        Equal(ClipPlaybackPhase.Terminal, scene.CurrentSample.Phase);
        Near(1, scene.CurrentSample.Progress);
    }

    private static void FinishPhase(WorkSceneTimeline scene, ClipKind kind)
    {
        for (int i = 0; i < 300 && scene.CurrentSample.Phase != ClipPlaybackPhase.Terminal; i++)
            Equal(kind, scene.Advance(Step).Kind);
        Equal(ClipPlaybackPhase.Terminal, scene.CurrentSample.Phase);
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
