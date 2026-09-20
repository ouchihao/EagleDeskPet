using System.Text.Json;
using DuckDeskPet.Core;

internal static class Program
{
    private const double Step = 1.0 / 60;
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

    private static int Main()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("automatic-emotion-default-off", DefaultOff),
            ("hunger-25-enter-35-exit", HungerThresholds),
            ("mood-25-enter-40-exit", MoodThresholds),
            ("hunger-priority-over-low-mood", Priority),
            ("automatic-cooldown-five-minutes", Cooldown),
            ("disallowed-reaction-does-not-consume-cooldown", Blocked),
            ("toggle-does-not-reset-cooldown", Toggle),
            ("rollback-does-not-bypass-cooldown", Rollback),
            ("disabled-policy-still-exposes-context", DisabledContext),
            ("invalid-stats-do-not-trigger", InvalidStats),
            ("third-touch-annoyed-even-automatic-off", ThirdTouch),
            ("three-touches-at-exact-ten-seconds", ExactWindow),
            ("sparse-touches-do-not-trigger", SparseTouches),
            ("one-annoyed-per-continuous-burst", Burst),
            ("quiet-gap-allows-new-burst", NewBurst),
            ("frequent-touch-does-not-grant-rewards", NoRewards),
            ("policy-never-mutates-care-state", PolicyReadOnly),
            ("new-clip-enum-and-frame-contract", Catalog),
            ("default-automatic-pool-remains-yawn", AutomaticPool),
            ("scene-phases-not-requestable-as-one-shots", RejectSceneClips),
            ("hungry-enter-two-loops-exit", HungryFull),
            ("cancel-during-entry-completes-before-exit", CancelEntry),
            ("cancel-loop-completes-before-exit", CancelLoop),
            ("cancel-exit-does-not-restart-it", CancelExit),
            ("scene-stall-delta-is-bounded", Stall),
            ("hungry-request-waits-two-second-idle", HungryIdle),
            ("hungry-waits-for-current-action-and-idle", HungryAfterAction),
            ("manual-queue-precedes-hungry", ManualPriority),
            ("duplicate-hungry-request-does-not-extend", DuplicateHungry),
            ("cancel-pending-hungry-never-starts", CancelPending),
            ("work-cancels-pending-hungry", WorkPending),
            ("work-exits-active-hungry-before-entry", WorkActive),
            ("work-rejects-new-hungry", WorkRejects),
            ("pause-freezes-hungry-frame-and-root", PauseHungry),
            ("cancel-while-paused-exits-on-resume", CancelPaused),
            ("hungry-root-is-neutral-throughout", HungryAnchored),
            ("disable-safely-cancels-active-scene", DisableScene),
            ("food-reaction-waits-after-complete-exit", FeedAfterHungry),
            ("annoyed-removes-pending-shy-not-active", AnnoyedQueue),
            ("suspend-automatic-keeps-idle", SuspendIdle),
            ("suspend-does-not-stop-active-action", SuspendActive),
            ("exclusive-manual-actions-retain-two-second-beat", ExclusiveActions),
            ("automatic-resume-only-yawn", ResumeAutomatic),
            ("all-new-ordinary-clips-complete", NewActionCompletion),
            ("scene-completion-sequences-monotonic", Sequences),
            ("invalid-delta-rejected", InvalidDelta),
            ("disable-removes-only-pending-automatic-emotions", DisablePendingEmotions),
            ("disable-finishes-running-automatic-annoyed", DisableRunningEmotion),
            ("manual-interaction-precedes-automatic-emotion", AutomaticEmotionPriority),
        };
        int failures = 0;
        foreach (var test in tests)
            try { test.Test(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex); }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} emotion/scheduler tests passed. No app, images, user saves, or AI configuration touched.");
        return failures == 0 ? 0 : 1;
    }

    private static PetState State(double fullness = 75, double mood = 70) => new() { Fullness = fullness, Mood = mood, LastUpdatedUtc = Start };
    private static EmotionPolicy Enabled() => new() { Enabled = true };
    private static void DefaultOff()
    {
        var p = new EmotionPolicy(); Check(!p.Enabled); var result = p.Evaluate(State(0, 0), Start);
        Equal(EmotionReaction.None, result.Reaction); Check(result.CancelHungryScene); Check(p.LastAutomaticReactionUtc is null);
    }
    private static void HungerThresholds()
    {
        var p = Enabled(); foreach (var pair in new[] { (26d, false), (25d, true), (34.9, true), (35d, false), (30d, false), (25d, true) })
            Equal(pair.Item2, p.Evaluate(State(pair.Item1), Start, false).IsHungry);
    }
    private static void MoodThresholds()
    {
        var p = Enabled(); foreach (var pair in new[] { (26d, false), (25d, true), (39.9, true), (40d, false), (30d, false), (25d, true) })
            Equal(pair.Item2, p.Evaluate(State(mood: pair.Item1), Start, false).IsLowMood);
        Equal(EmotionReaction.Annoyed, p.Evaluate(State(mood: 10), Start).Reaction);
    }
    private static void Priority() => Equal(EmotionReaction.HungryScene, Enabled().Evaluate(State(0, 0), Start).Reaction);
    private static void Cooldown()
    {
        var p = Enabled(); Equal(EmotionReaction.HungryScene, p.Evaluate(State(0), Start).Reaction);
        Equal(EmotionReaction.None, p.Evaluate(State(0), Start.AddSeconds(299.9)).Reaction);
        Equal(EmotionReaction.HungryScene, p.Evaluate(State(0), Start.AddMinutes(5)).Reaction);
    }
    private static void Blocked()
    {
        var p = Enabled(); Equal(EmotionReaction.None, p.Evaluate(State(0), Start, false).Reaction); Check(p.LastAutomaticReactionUtc is null);
        Equal(EmotionReaction.HungryScene, p.Evaluate(State(0), Start.AddSeconds(1)).Reaction);
    }
    private static void Toggle()
    {
        var p = Enabled(); p.Evaluate(State(0), Start); p.Enabled = false;
        Check(p.Evaluate(State(0), Start.AddSeconds(1)).CancelHungryScene); p.Enabled = true;
        Equal(EmotionReaction.None, p.Evaluate(State(0), Start.AddSeconds(2)).Reaction); Equal<DateTimeOffset?>(Start, p.LastAutomaticReactionUtc);
    }
    private static void Rollback()
    {
        var p = Enabled(); p.Evaluate(State(0), Start); Equal(EmotionReaction.None, p.Evaluate(State(0), Start.AddHours(-1)).Reaction);
        Equal(EmotionReaction.None, p.Evaluate(State(0), Start.AddSeconds(299)).Reaction); Equal(EmotionReaction.HungryScene, p.Evaluate(State(0), Start.AddSeconds(300)).Reaction);
    }
    private static void DisabledContext()
    {
        var p = new EmotionPolicy(); var r = p.Evaluate(State(20, 20), Start); Check(r.IsHungry && r.IsLowMood); Equal(EmotionReaction.None, r.Reaction);
        r = p.Evaluate(State(30, 30), Start); Check(r.IsHungry && r.IsLowMood);
        r = p.Evaluate(State(35, 40), Start); Check(!r.IsHungry && !r.IsLowMood);
    }
    private static void InvalidStats()
    {
        var p = Enabled(); var r = p.Evaluate(State(double.NaN, double.PositiveInfinity), Start); Check(!r.IsHungry && !r.IsLowMood); Equal(EmotionReaction.None, r.Reaction);
    }
    private static void ThirdTouch()
    {
        var p = new EmotionPolicy(); Check(p.RegisterPetAttempt(Start).AllowCareReward); Check(p.RegisterPetAttempt(Start.AddSeconds(1)).AllowCareReward);
        var third = p.RegisterPetAttempt(Start.AddSeconds(2)); Check(!third.AllowCareReward && third.IsFrequent); Equal(EmotionReaction.Annoyed, third.Reaction);
    }
    private static void ExactWindow()
    {
        var p = new EmotionPolicy(); p.RegisterPetAttempt(Start); p.RegisterPetAttempt(Start.AddSeconds(5));
        Equal(EmotionReaction.Annoyed, p.RegisterPetAttempt(Start.AddSeconds(10)).Reaction);
    }
    private static void SparseTouches()
    {
        var p = new EmotionPolicy(); foreach (int time in new[] { 0, 6, 12, 18, 24 })
        { var r = p.RegisterPetAttempt(Start.AddSeconds(time)); Check(r.AllowCareReward); Equal(EmotionReaction.None, r.Reaction); }
    }
    private static void Burst()
    {
        var p = new EmotionPolicy(); int reactions = 0;
        for (int i = 0; i < 100; i++) { var r = p.RegisterPetAttempt(Start.AddMilliseconds(i * 100)); if (r.Reaction == EmotionReaction.Annoyed) reactions++; if (i >= 2) Check(!r.AllowCareReward); }
        Equal(1, reactions);
    }
    private static void NewBurst()
    {
        var p = new EmotionPolicy(); for (int i = 0; i < 3; i++) p.RegisterPetAttempt(Start.AddSeconds(i));
        Check(p.RegisterPetAttempt(Start.AddSeconds(13)).AllowCareReward); p.RegisterPetAttempt(Start.AddSeconds(14));
        Equal(EmotionReaction.Annoyed, p.RegisterPetAttempt(Start.AddSeconds(15)).Reaction);
    }
    private static void NoRewards()
    {
        var p = new EmotionPolicy(); var care = new PetCareService(null, Start);
        for (int i = 0; i < 10; i++)
        {
            var now = Start.AddMilliseconds(i * 100); var decision = p.RegisterPetAttempt(now);
            if (decision.AllowCareReward) care.Pet(now);
        }
        Equal(1, care.State.TotalPets); Equal(1, care.State.Experience); Equal(0, care.State.Coins);
    }
    private static void PolicyReadOnly()
    {
        var state = State(0, 0); string before = JsonSerializer.Serialize(state); var p = Enabled();
        p.Evaluate(state, Start); for (int i = 0; i < 5; i++) p.RegisterPetAttempt(Start.AddSeconds(i));
        Equal(before, JsonSerializer.Serialize(state));
    }
    private static void Catalog()
    {
        Equal(40, (int)ClipKind.HungryEnter); Equal(41, (int)ClipKind.HungryLoop); Equal(42, (int)ClipKind.HungryExit);
        Equal(50, (int)ClipKind.Tea); Equal(56, (int)ClipKind.Annoyed);
        foreach (var kind in Enum.GetValues<ClipKind>().Where(x => ClipCatalog.IsHungryScene(x) || x >= ClipKind.Tea))
        {
            var d = ClipCatalog.GetDefinition(kind);
            var (seconds, frames) = kind switch
            {
                ClipKind.HungryEnter or ClipKind.HungryExit => (1.5, 91),
                ClipKind.RpsRock or ClipKind.RpsPaper or ClipKind.RpsScissors => (2.8, 169),
                ClipKind.RpsWin or ClipKind.RpsLose => (2.4, 145),
                ClipKind.HungryLoop or ClipKind.Tea or ClipKind.Annoyed => (2.0, 121),
                _ => throw new InvalidOperationException("Missing an independent frame/timing contract for " + kind),
            };
            Equal(seconds, d.DurationSeconds); Equal(frames, d.FrameCount); Equal(PetActionGroup.Interaction, ClipCatalog.GetGroup(kind));
        }
        Check(!ClipCatalog.IsKnown((ClipKind)43) && !ClipCatalog.IsKnown((ClipKind)49));
    }
    private static void AutomaticPool()
    {
        Equal(1, ClipCatalog.PlayableClipCount); Check(Enum.GetValues<ClipKind>().Where(ClipCatalog.IsAutomaticAction).SequenceEqual(new[] { ClipKind.Yawn }));
        var b = new PetBehaviorController(); for (int i = 0; i < 5000; i++) Check(b.Advance(Step).Kind is ClipKind.Idle or ClipKind.Yawn);
    }
    private static void RejectSceneClips()
    {
        var b = new PetBehaviorController(); var timeline = new ClipTimeline();
        foreach (var kind in Enum.GetValues<ClipKind>().Where(ClipCatalog.IsScene))
        { Throws<ArgumentOutOfRangeException>(() => b.RequestAction(kind)); Throws<ArgumentOutOfRangeException>(() => timeline.RequestClip(kind)); }
    }
    private static void HungryFull()
    {
        var h = new HungrySceneTimeline(); h.Start(); Equal(ClipKind.HungryEnter, h.CurrentSample.Kind); Near(0, h.CurrentSample.Progress);
        End(h); Equal(ClipKind.HungryLoop, h.Advance(Step).Kind); End(h); Equal(ClipKind.HungryLoop, h.Advance(Step).Kind);
        End(h); Equal(ClipKind.HungryExit, h.Advance(Step).Kind); Equal(2, h.CompletedLoopCount); End(h);
        Equal(ClipKind.Idle, h.Advance(Step).Kind); Check(!h.IsActive);
    }
    private static void CancelEntry()
    {
        var h = new HungrySceneTimeline(); h.Start(); h.Advance(Step); var before = h.CurrentSample; h.Cancel(); Equal(before, h.CurrentSample);
        End(h); Equal(ClipKind.HungryExit, h.Advance(Step).Kind); Equal(0, h.CompletedLoopCount); End(h); Equal(ClipKind.Idle, h.Advance(Step).Kind);
    }
    private static void CancelLoop()
    {
        var h = new HungrySceneTimeline(); h.Start(); End(h); h.Advance(Step); h.Advance(Step); var before = h.CurrentSample;
        h.Cancel(); Equal(before, h.CurrentSample); End(h); Equal(ClipKind.HungryExit, h.Advance(Step).Kind); Equal(1, h.CompletedLoopCount);
    }
    private static void CancelExit()
    {
        var h = new HungrySceneTimeline(); h.Start(); h.Cancel(); End(h); h.Advance(Step); h.Advance(Step); var before = h.CurrentSample;
        h.Cancel(); Equal(before, h.CurrentSample); End(h); Equal(ClipKind.Idle, h.Advance(Step).Kind);
    }
    private static void Stall()
    {
        var h = new HungrySceneTimeline(); h.Start(); h.Advance(99); Near(0.05 / 1.5, h.CurrentSample.Progress);
    }
    private static PetBehaviorController Hungry()
    {
        var b = new PetBehaviorController(); Equal(PetBehaviorRequestResult.Queued, b.RequestHungryScene());
        IdleBeat(b, ClipKind.HungryEnter); return b;
    }
    private static void HungryIdle() { var b = Hungry(); Check(b.IsHungrySceneActive && b.IsHungryRequested); Near(0, b.CurrentSample.Progress); }
    private static void HungryAfterAction()
    {
        var b = new PetBehaviorController(); b.RequestAction(ClipKind.Tea); IdleBeat(b, ClipKind.Tea); b.Advance(Step); var before = b.CurrentSample;
        b.RequestHungryScene(); Equal(before, b.CurrentSample); End(b); Equal(ClipKind.Idle, b.Advance(Step).Kind); IdleBeat(b, ClipKind.HungryEnter);
    }
    private static void ManualPriority()
    {
        var b = new PetBehaviorController(); b.RequestHungryScene(); b.RequestAction(ClipKind.Eat); IdleBeat(b, ClipKind.Eat);
        End(b); b.Advance(Step); IdleBeat(b, ClipKind.HungryEnter);
    }
    private static void DuplicateHungry()
    {
        var b = Hungry(); Equal(PetBehaviorRequestResult.Coalesced, b.RequestHungryScene()); int loops = 0;
        for (int i = 0; b.IsHungrySceneActive && i < 1000; i++)
        { var before = b.CurrentSample; b.RequestHungryScene(); var after = b.Advance(Step); if (after.Kind == ClipKind.HungryLoop && after.Sequence != before.Sequence) loops++; }
        Equal(2, loops); Check(!b.IsHungryRequested && !b.IsHungrySceneActive);
    }
    private static void CancelPending()
    {
        var b = new PetBehaviorController(); b.RequestHungryScene(); b.CancelHungryScene(); Check(!b.IsHungryRequested);
        for (int i = 0; i < 600; i++) Check(!ClipCatalog.IsHungryScene(b.Advance(Step).Kind));
    }
    private static void WorkPending()
    {
        var b = new PetBehaviorController(); b.RequestHungryScene(); b.SetWorkState(true, false); Check(!b.IsHungryRequested);
        Equal(ClipKind.WorkEnter, b.Advance(Step).Kind);
    }
    private static void WorkActive()
    {
        var b = Hungry(); b.Advance(Step); var before = b.CurrentSample; b.SetWorkState(true, false); Equal(before, b.CurrentSample);
        Check(!b.IsHungryRequested); End(b); Equal(ClipKind.HungryExit, b.Advance(Step).Kind); End(b);
        Equal(ClipKind.Idle, b.Advance(Step).Kind); Equal(ClipKind.WorkEnter, b.Advance(Step).Kind); Check(!b.IsHungrySceneActive);
    }
    private static void WorkRejects()
    {
        var b = new PetBehaviorController(); b.SetWorkState(true, false); Equal(PetBehaviorRequestResult.RejectedWorking, b.RequestHungryScene());
        b.Advance(Step); b.SetWorkState(false, false); Equal(PetBehaviorRequestResult.RejectedWorking, b.RequestHungryScene());
    }
    private static void PauseHungry()
    {
        var b = Hungry(); b.Advance(Step); var before = b.CurrentSample; b.SetPaused(true);
        for (int i = 0; i < 500; i++) Equal(before, b.Advance(Step)); Equal(Pose.Neutral, b.CurrentProceduralPose);
        Equal(PetBehaviorRequestResult.RejectedPaused, b.RequestHungryScene()); b.SetPaused(false); Equal(before, b.CurrentSample);
        Check(b.Advance(Step).Progress > before.Progress);
    }
    private static void CancelPaused()
    {
        var b = Hungry(); b.SetPaused(true); b.CancelHungryScene(); Check(b.IsHungrySceneActive); b.SetPaused(false);
        End(b); Equal(ClipKind.HungryExit, b.Advance(Step).Kind); End(b); Equal(ClipKind.Idle, b.Advance(Step).Kind);
    }
    private static void HungryAnchored()
    {
        var b = Hungry(); for (int i = 0; b.IsHungrySceneActive && i < 1000; i++) { Equal(Pose.Neutral, b.CurrentProceduralPose); b.Advance(Step); }
        Check(!b.IsHungrySceneActive);
    }
    private static void DisableScene()
    {
        var b = Hungry(); var p = Enabled(); p.Evaluate(State(0), Start); p.Enabled = false;
        if (p.Evaluate(State(0), Start.AddSeconds(1)).CancelHungryScene) b.CancelHungryScene();
        Check(!b.IsHungryRequested && b.IsHungrySceneActive); End(b); Equal(ClipKind.HungryExit, b.Advance(Step).Kind);
    }
    private static void FeedAfterHungry()
    {
        var b = Hungry(); b.CancelHungryScene(); b.QueueReaction(PetBehaviorKind.Fed); b.QueueReaction(PetBehaviorKind.Fed);
        End(b); Equal(ClipKind.HungryExit, b.Advance(Step).Kind); End(b); b.Advance(Step); IdleBeat(b, ClipKind.Eat);
        Equal(0, b.PendingCount); End(b); b.Advance(Step); IdleBeat(b, ClipKind.Yawn);
    }
    private static void AnnoyedQueue()
    {
        var b = new PetBehaviorController(); b.QueueReaction(PetBehaviorKind.Petted); IdleBeat(b, ClipKind.Shy);
        b.QueueReaction(PetBehaviorKind.Petted); var before = b.CurrentSample; b.QueueReaction(PetBehaviorKind.Annoyed); Equal(before, b.CurrentSample); Equal(1, b.PendingCount);
        End(b); b.Advance(Step); IdleBeat(b, ClipKind.Annoyed); Equal(0, b.PendingCount);
    }
    private static void SuspendIdle()
    {
        var b = new PetBehaviorController(); b.SuspendAutomatic(true); Check(b.IsAutomaticSuspended);
        for (int i = 0; i < 1000; i++) Equal(ClipKind.Idle, b.Advance(Step).Kind);
    }
    private static void SuspendActive()
    {
        var b = new PetBehaviorController(); IdleBeat(b, ClipKind.Yawn); b.Advance(Step); var before = b.CurrentSample; b.SuspendAutomatic(true); Equal(before, b.CurrentSample);
        End(b); b.Advance(Step); for (int i = 0; i < 500; i++) Equal(ClipKind.Idle, b.Advance(Step).Kind);
    }
    private static void ExclusiveActions()
    {
        var b = new PetBehaviorController(); b.SuspendAutomatic(true); b.RequestAction(ClipKind.RpsRock); IdleBeat(b, ClipKind.RpsRock);
        b.RequestAction(ClipKind.RpsWin); End(b); Equal(ClipKind.Idle, b.Advance(Step).Kind); IdleBeat(b, ClipKind.RpsWin);
        End(b); b.Advance(Step); for (int i = 0; i < 600; i++) Equal(ClipKind.Idle, b.Advance(Step).Kind);
    }
    private static void ResumeAutomatic()
    {
        var b = new PetBehaviorController(); b.SuspendAutomatic(true); for (int i = 0; i < 300; i++) b.Advance(Step);
        b.SuspendAutomatic(false); Equal(ClipKind.Yawn, b.Advance(Step).Kind);
    }
    private static void NewActionCompletion()
    {
        foreach (var kind in Enum.GetValues<ClipKind>().Where(x => x >= ClipKind.Tea))
        { var b = new PetBehaviorController(); b.RequestAction(kind); IdleBeat(b, kind); End(b); Equal(ClipKind.Idle, b.Advance(Step).Kind); }
    }
    private static void Sequences()
    {
        var b = Hungry(); long sequence = b.CurrentSample.Sequence;
        for (int i = 0; i < 600; i++) { var sample = b.Advance(Step); Check(sample.Sequence >= sequence); sequence = sample.Sequence; }
        b.RequestHungryScene(); for (int i = 0; i < 700; i++) { var sample = b.Advance(Step); Check(sample.Sequence >= sequence); sequence = sample.Sequence; }
    }
    private static void InvalidDelta()
    {
        foreach (double delta in new[] { -1, double.NaN, double.PositiveInfinity })
        { Throws<ArgumentOutOfRangeException>(() => new HungrySceneTimeline().Advance(delta)); Throws<ArgumentOutOfRangeException>(() => new PetBehaviorController().Advance(delta)); }
    }
    private static void DisablePendingEmotions()
    {
        var b = new PetBehaviorController(); b.SuspendAutomatic(true);
        b.QueueAutomaticEmotion(EmotionReaction.Annoyed); b.QueueReaction(PetBehaviorKind.Annoyed); b.RequestHungryScene();
        Equal(2, b.PendingCount); b.CancelAutomaticEmotions(); Equal(1, b.PendingCount); Check(!b.IsHungryRequested);
        IdleBeat(b, ClipKind.Annoyed); End(b); b.Advance(Step);
        for (int i = 0; i < 300; i++) Equal(ClipKind.Idle, b.Advance(Step).Kind);
    }
    private static void DisableRunningEmotion()
    {
        var b = new PetBehaviorController(); b.QueueAutomaticEmotion(EmotionReaction.Annoyed); IdleBeat(b, ClipKind.Annoyed);
        b.Advance(Step); var before = b.CurrentSample; b.CancelAutomaticEmotions(); Equal(before, b.CurrentSample); End(b);
    }
    private static void AutomaticEmotionPriority()
    {
        var b = new PetBehaviorController(); b.QueueAutomaticEmotion(EmotionReaction.Annoyed); b.QueueReaction(PetBehaviorKind.Petted);
        IdleBeat(b, ClipKind.Shy); End(b); b.Advance(Step); IdleBeat(b, ClipKind.Annoyed);
    }
    private static void IdleBeat(PetBehaviorController b, ClipKind expected)
    {
        for (int i = 0; i < 119; i++) Equal(ClipKind.Idle, b.Advance(Step).Kind);
        Equal(expected, b.Advance(Step).Kind); Near(0, b.CurrentSample.Progress);
    }
    private static void End(PetBehaviorController b)
    {
        ClipKind kind = b.CurrentSample.Kind; long sequence = b.CurrentSample.Sequence; double last = b.CurrentSample.Progress;
        for (int i = 0; i < 400 && b.CurrentSample.Phase != ClipPlaybackPhase.Terminal; i++)
        { var s = b.Advance(Step); Equal(kind, s.Kind); Equal(sequence, s.Sequence); Check(s.Progress >= last); last = s.Progress; }
        Equal(ClipPlaybackPhase.Terminal, b.CurrentSample.Phase); Near(1, b.CurrentSample.Progress);
    }
    private static void End(HungrySceneTimeline h)
    {
        ClipKind kind = h.CurrentSample.Kind; long sequence = h.CurrentSample.Sequence; double last = h.CurrentSample.Progress;
        for (int i = 0; i < 200 && h.CurrentSample.Phase != ClipPlaybackPhase.Terminal; i++)
        { var s = h.Advance(Step); Equal(kind, s.Kind); Equal(sequence, s.Sequence); Check(s.Progress >= last); last = s.Progress; }
        Equal(ClipPlaybackPhase.Terminal, h.CurrentSample.Phase); Near(1, h.CurrentSample.Progress);
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
    private static void Near(double expected, double actual) { if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 1e-9) throw new InvalidOperationException($"Expected {expected}, actual {actual}."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
