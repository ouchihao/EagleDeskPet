using DuckDeskPet.Core;

namespace DuckDeskPet.SelfTest;

internal static class YawnPacingTests
{
    private const double Step = 1.0 / 60.0;

    public static IEnumerable<(string Name, Action Body)> Cases => new (string, Action)[]
    {
        ("Automatic Yawn midpoint is a fresh randomized delay", RandomMidpoint),
        ("Automatic suspension does not accumulate yawns", Suspension),
        ("Pause does not accumulate yawns and resumes a fresh idle interval", Pause),
        ("Work and loafing do not accumulate idle yawns", WorkAndLoafing),
        ("Manual Yawn bypasses the automatic wait", ManualYawn),
        ("Queued interactions retain their exact two-second idle beat", InteractionBeat),
        ("Stalled frames cannot instantly consume the Yawn interval", StalledFrames),
    };

    private static void RandomMidpoint()
    {
        var timeline = new ClipTimeline(new FixedRandom(uint.MaxValue / 2));
        Idle(timeline, 60 * 67 + 29);
        Equal(ClipKind.Yawn, timeline.Advance(Step).Kind);
        Equal(0.0, timeline.CurrentSample.Progress);
    }

    private static void Suspension()
    {
        var timeline = NewTimeline();
        Idle(timeline, 60 * 20);
        timeline.SuspendAutomatic(true);
        Idle(timeline, 60 * 1800);
        timeline.SuspendAutomatic(false);
        Idle(timeline, 60 * 25 - 1);
        Equal(ClipKind.Yawn, timeline.Advance(Step).Kind);
    }

    private static void Pause()
    {
        var behavior = new PetBehaviorController(NewTimeline());
        Idle(behavior, 60 * 44);
        behavior.SetPaused(true);
        for (int i = 0; i < 60 * 1800; i++)
            Equal(ClipPlaybackPhase.Paused, behavior.Advance(Step).Phase);
        behavior.SetPaused(false);
        Idle(behavior, 60 * 45 - 1);
        Equal(ClipKind.Yawn, behavior.Advance(Step).Kind);
    }

    private static void WorkAndLoafing()
    {
        var behavior = new PetBehaviorController(NewTimeline());
        Idle(behavior, 60 * 44);
        behavior.SetWorkState(true, true);
        for (int i = 0; i < 60 * 120; i++) behavior.Advance(Step);
        Equal(ClipKind.BusyLoop, behavior.CurrentSample.Kind);
        behavior.SetPaused(true, preserveAction: true);
        var held = behavior.CurrentSample;
        for (int i = 0; i < 60 * 1800; i++) Equal(held, behavior.Advance(Step));
        behavior.SetPaused(false);
        behavior.SetWorkState(false, false);
        for (int i = 0; i < 60 * 10 && behavior.CurrentSample.Kind != ClipKind.Idle; i++)
            behavior.Advance(Step);
        Equal(ClipKind.Idle, behavior.CurrentSample.Kind);
        Idle(behavior, 60 * 45 - 1);
        Equal(ClipKind.Yawn, behavior.Advance(Step).Kind);
    }

    private static void ManualYawn()
    {
        var timeline = NewTimeline();
        Equal(ClipRequestResult.Started, timeline.RequestClip(ClipKind.Yawn));
        Equal(ClipKind.Yawn, timeline.CurrentSample.Kind);
        Equal(0.0, timeline.CurrentSample.Progress);
        var behavior = new PetBehaviorController(NewTimeline());
        Idle(behavior, 120);
        Equal(PetBehaviorRequestResult.Queued, behavior.RequestAction(ClipKind.Yawn));
        Equal(ClipKind.Yawn, behavior.Advance(Step).Kind);
    }

    private static void InteractionBeat()
    {
        var behavior = new PetBehaviorController(NewTimeline());
        behavior.QueueReaction(PetBehaviorKind.Petted);
        Idle(behavior, 119);
        Equal(ClipKind.Shy, behavior.Advance(Step).Kind);
        behavior.QueueReaction(PetBehaviorKind.Fed);
        for (int i = 0; i < 120; i++) Equal(ClipKind.Shy, behavior.Advance(Step).Kind);
        Equal(ClipPlaybackPhase.Terminal, behavior.CurrentSample.Phase);
        Equal(ClipKind.Idle, behavior.Advance(Step).Kind);
        Idle(behavior, 119);
        Equal(ClipKind.Eat, behavior.Advance(Step).Kind);
        Equal(0.0, behavior.CurrentSample.Progress);
    }

    private static void StalledFrames()
    {
        var timeline = NewTimeline();
        for (int i = 0; i < 899; i++) Equal(ClipKind.Idle, timeline.Advance(3600).Kind);
        Equal(ClipKind.Yawn, timeline.Advance(3600).Kind);
    }

    private static ClipTimeline NewTimeline() => new(new FixedRandom(0));
    private static void Idle(ClipTimeline timeline, int frames)
    {
        for (int i = 0; i < frames; i++) Equal(ClipKind.Idle, timeline.Advance(Step).Kind);
    }
    private static void Idle(PetBehaviorController behavior, int frames)
    {
        for (int i = 0; i < frames; i++) Equal(ClipKind.Idle, behavior.Advance(Step).Kind);
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, actual {actual}");
    }
    private sealed class FixedRandom(uint value) : IClipRandomSource
    {
        public uint NextUInt32() => value;
    }
}
