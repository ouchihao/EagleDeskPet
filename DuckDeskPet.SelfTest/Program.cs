using System;
using System.Collections.Generic;
using DuckDeskPet.Core;

namespace DuckDeskPet.SelfTest;

internal static class Program
{
    private const double Step60 = 1.0 / 60.0;
    private const double Step240 = 1.0 / 240.0;

    private static readonly ClipKind[] AutomaticClips =
    {
        ClipKind.Yawn,
    };

    private static readonly ClipKind[] RequestableClips =
    {
        ClipKind.SideEye,
        ClipKind.Bomb,
        ClipKind.Yawn,
        ClipKind.Shy,
        ClipKind.Wiggle,
        ClipKind.Hop,
        ClipKind.Nod,
        ClipKind.Shimmy,
        ClipKind.Stretch,
        ClipKind.Eat,
    };

    private static int Main()
    {
        (string Name, Action Body)[] tests =
        {
            ("Frame gate cadence", FrameGateCadence),
            ("Frame gate stalls", FrameGateStalls),
            ("Catalog separates idle and interaction actions", CatalogContract),
            ("Every requested clip has complete endpoints", CompleteRequestedClips),
            ("Automatic action and exact 2-second idle", AutomaticActionAndExactIdle),
            ("Automatic scheduling selects only idle-group Yawn", AutomaticCoverageWithoutRepeats),
            ("Active requests never shorten the clip", ActiveRequestsNeverShortenClip),
            ("Pending waits through complete idle", PendingWaitsThroughCompleteIdle),
            ("Click, Shy, and pause", ClickShyAndPause),
            ("Procedural breathing continuity", ProceduralBreathingContinuity),
            ("Legacy body actions remain bounded", LegacyBodyActionsRemainBounded),
            ("Input invariants", InputInvariants),
        };

        tests = tests.Concat(CareTests.Cases).Concat(WorkTests.Cases).ToArray();
        int failures = 0;
        foreach ((string name, Action body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {name}");
                Console.Error.WriteLine($"      {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            failures == 0
                ? $"All {tests.Length} deterministic self-tests passed."
                : $"{failures} of {tests.Length} deterministic self-tests failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void FrameGateCadence()
    {
        foreach (double sourceRate in new[] { 30.0, 59.94, 60.0, 120.0, 144.0 })
        {
            const double durationSeconds = 20.0;
            var gate = new FrameRateGate();
            int lastSample = (int)Math.Floor(durationSeconds * sourceRate);
            int accepted = 0;

            for (int sample = 0; sample <= lastSample; sample++)
            {
                if (!gate.TryAdvance(sample / sourceRate, out double deltaSeconds))
                {
                    continue;
                }

                accepted++;
                AssertInRange(
                    deltaSeconds,
                    0.0,
                    FrameRateGate.DefaultMaximumDeltaSeconds,
                    $"{sourceRate} Hz gate delta");
            }

            double sampledDuration = lastSample / sourceRate;
            double measuredRate = (accepted - 1) / sampledDuration;
            AssertNear(
                Math.Min(sourceRate, ClipCatalog.SampleRate),
                measuredRate,
                0.30,
                $"Unexpected gated rate for {sourceRate} Hz input.");
        }
    }

    private static void FrameGateStalls()
    {
        var gate = new FrameRateGate();
        Assert(gate.TryAdvance(0.0, out double first), "First frame was rejected.");
        AssertNear(0.0, first, 0.0, "First frame delta was not zero.");
        Assert(gate.TryAdvance(Step60, out _), "Second frame was rejected.");
        Assert(gate.TryAdvance(0.276, out double stalled), "Stall was rejected.");
        AssertNear(
            FrameRateGate.DefaultMaximumDeltaSeconds,
            stalled,
            1e-12,
            "Stalled delta was not clamped.");
        Assert(
            !gate.TryAdvance(0.276 + (1.0 / 144.0), out _),
            "A stall generated an early catch-up frame.");
        Assert(gate.TryAdvance(0.276 + Step60, out _), "Gate did not resume at 60 Hz.");
        Assert(!gate.TryAdvance(0.276 + Step60, out _), "Duplicate time was accepted.");
        Assert(gate.TryAdvance(0.1, out double reset), "Reversed clock was rejected.");
        AssertNear(0.0, reset, 0.0, "Reversed clock did not reset the delta.");
    }

    private static void CatalogContract()
    {
        AssertNear(
            2.0,
            ClipTimeline.AutomaticIdleDurationSeconds,
            0.0,
            "Automatic idle duration changed.");
        AssertEqual(1, ClipCatalog.PlayableClipCount, "Automatic pool must contain only Yawn.");

        foreach (ClipKind kind in AutomaticClips)
        {
            Assert(ClipCatalog.IsAutomaticAction(kind), $"{kind} is not marked automatic.");
            ClipDefinition definition = ClipCatalog.GetDefinition(kind);
            AssertEqual(121, definition.FrameCount, $"{kind} is not a 121-frame clip.");
            AssertNear(2.0, definition.DurationSeconds, 0.0, $"{kind} is not two seconds.");
            AssertNear(
                120.0,
                ClipCatalog.GetProgressAtFrame(kind, 120) * 120.0,
                1e-12,
                $"{kind} terminal frame mapping changed.");
        }

        Assert(!ClipCatalog.IsKnown((ClipKind)10), "Removed transition enum value is still known.");
        Assert(!ClipCatalog.IsAutomaticAction(ClipKind.Wiggle), "Legacy Wiggle entered the automatic pool.");
    }

    private static void CompleteRequestedClips()
    {
        foreach (ClipKind kind in RequestableClips)
        {
            var timeline = new ClipTimeline((uint)(1000 + (int)kind));
            AssertEqual(ClipRequestResult.Started, timeline.RequestClip(kind), $"{kind} did not start.");
            AssertSample(timeline.CurrentSample, kind, ClipPlaybackPhase.Playing, 0.0, $"{kind} start");

            double previousProgress = 0.0;
            long sequence = timeline.CurrentSample.Sequence;
            for (int frame = 0; frame < 300 && timeline.CurrentSample.Phase != ClipPlaybackPhase.Terminal; frame++)
            {
                ClipSample sample = timeline.Advance(Step60);
                AssertEqual(kind, sample.Kind, $"{kind} switched before completion.");
                AssertEqual(sequence, sample.Sequence, $"{kind} sequence changed.");
                Assert(sample.Progress + 1e-12 >= previousProgress, $"{kind} moved backwards.");
                AssertInRange(sample.Progress, 0.0, 1.0, $"{kind} progress");
                previousProgress = sample.Progress;
            }

            AssertSample(
                timeline.CurrentSample,
                kind,
                ClipPlaybackPhase.Terminal,
                1.0,
                $"{kind} terminal");
            AssertSample(
                timeline.Advance(Step60),
                ClipKind.Idle,
                ClipPlaybackPhase.Idle,
                0.0,
                $"{kind} post-terminal idle");
        }
    }

    private static void AutomaticActionAndExactIdle()
    {
        var random = new ScriptedRandomSource(0u, 0u, 0u);
        var timeline = new ClipTimeline(random);

        AssertIdleForFrames(timeline, 119, "initial idle");
        ClipSample first = timeline.Advance(Step60);
        AssertSample(first, ClipKind.Yawn, ClipPlaybackPhase.Playing, 0.0, "first automatic start");
        AssertEqual(1, random.CallCount, "Random source was consumed during fixed idle.");
        AssertAutomaticRunsEveryFrameToTerminal(timeline, ClipKind.Yawn);

        ClipSample idle = timeline.Advance(Step60);
        AssertSample(idle, ClipKind.Idle, ClipPlaybackPhase.Idle, 0.0, "first post-action idle");
        AssertIdleForFrames(timeline, 119, "post-action idle");
        ClipSample second = timeline.Advance(Step60);
        AssertSample(second, ClipKind.Yawn, ClipPlaybackPhase.Playing, 0.0, "second automatic start");
        AssertEqual(2, random.CallCount, "Random source consumption changed.");
        AssertEqual(first.Kind, second.Kind, "The single idle-group action changed.");
    }

    private static void AutomaticCoverageWithoutRepeats()
    {
        var timeline = new ClipTimeline(0xCAFE_BABEu);
        var observed = new HashSet<ClipKind>();
        long sequence = 0;
        int starts = 0;

        for (int frame = 0; frame < 60 * 600 && starts < 60; frame++)
        {
            ClipSample sample = timeline.Advance(Step60);
            if (sample.Kind == ClipKind.Idle || sample.Sequence == sequence)
            {
                continue;
            }

            Assert(ClipCatalog.IsAutomaticAction(sample.Kind), $"Unexpected automatic {sample.Kind}.");
            AssertNear(0.0, sample.Progress, 0.0, "Automatic action skipped frame zero.");
            AssertEqual(ClipKind.Yawn, sample.Kind, "An interaction entered the idle pool.");
            observed.Add(sample.Kind);
            sequence = sample.Sequence;
            starts++;
        }

        AssertEqual(60, starts, "Too few automatic actions were observed.");
        foreach (ClipKind kind in AutomaticClips)
        {
            Assert(observed.Contains(kind), $"Automatic scheduling never selected {kind}.");
        }
    }

    private static void ActiveRequestsNeverShortenClip()
    {
        foreach (ClipRequestMode mode in new[]
                 {
                     ClipRequestMode.Queue,
                     ClipRequestMode.SmoothInterrupt,
                 })
        {
            var timeline = new ClipTimeline(0x1234u);
            timeline.RequestClip(ClipKind.Yawn);
            for (int frame = 1; frame <= 24; frame++)
            {
                ClipSample sample = timeline.Advance(Step60);
                AssertNear(frame, sample.FrameCoordinate, 1e-10, $"{mode} setup frame");
            }

            ClipSample before = timeline.CurrentSample;
            AssertEqual(
                ClipRequestResult.Queued,
                timeline.RequestClip(ClipKind.Shy, mode),
                $"{mode} was not queued.");
            AssertEqual(before, timeline.CurrentSample, $"{mode} changed the active sample.");
            AssertEqual(ClipKind.Shy, timeline.PendingClip, $"{mode} did not retain pending Shy.");

            for (int frame = 25; frame <= 120; frame++)
            {
                ClipSample sample = timeline.Advance(Step60);
                AssertEqual(ClipKind.Yawn, sample.Kind, $"{mode} swapped the active clip.");
                AssertNear(frame, sample.FrameCoordinate, 1e-10, $"{mode} changed natural timing.");
                Assert(
                    sample.Phase != ClipPlaybackPhase.ReturningToIdle,
                    $"{mode} exposed the retired smooth-return phase.");
                AssertEqual(
                    frame == 120 ? ClipPlaybackPhase.Terminal : ClipPlaybackPhase.Playing,
                    sample.Phase,
                    $"{mode} changed phase at authored frame {frame}.");
            }

            AssertSample(
                timeline.CurrentSample,
                ClipKind.Yawn,
                ClipPlaybackPhase.Terminal,
                1.0,
                $"{mode} natural terminal");
        }
    }

    private static void PendingWaitsThroughCompleteIdle()
    {
        var random = new ScriptedRandomSource(3u);
        var timeline = new ClipTimeline(random);
        timeline.RequestClip(ClipKind.SideEye);
        for (int frame = 0; frame < 30; frame++)
        {
            timeline.Advance(Step60);
        }

        AssertEqual(
            ClipRequestResult.Queued,
            timeline.RequestClip(ClipKind.Bomb, ClipRequestMode.Queue),
            "Bomb was not queued.");
        AssertEqual(
            ClipRequestResult.ReplacedQueued,
            timeline.RequestClip(ClipKind.Shy, ClipRequestMode.Queue),
            "Shy did not replace Bomb.");

        AdvanceToTerminal(timeline, ClipKind.SideEye, 240);
        AssertNear(1.0, timeline.CurrentSample.Progress, 0.0, "Queued source did not end at one.");
        AssertEqual(ClipKind.Shy, timeline.PendingClip, "Replacement pending clip was lost.");
        AssertSample(
            timeline.Advance(Step60),
            ClipKind.Idle,
            ClipPlaybackPhase.Idle,
            0.0,
            "post-terminal idle");
        AssertEqual(ClipKind.Shy, timeline.PendingClip, "Pending clip was consumed before idle.");
        AssertIdleForFrames(timeline, 119, "pending idle");
        AssertEqual(ClipKind.Shy, timeline.PendingClip, "Pending clip was lost during idle.");
        AssertEqual(0, random.CallCount, "Pending idle consumed the random source.");

        AssertSample(
            timeline.Advance(Step60),
            ClipKind.Shy,
            ClipPlaybackPhase.Playing,
            0.0,
            "pending Shy start after two seconds");
        AssertEqual(null, timeline.PendingClip, "Pending clip was not consumed at start.");
        AssertEqual(0, random.CallCount, "Random action won priority over pending clip.");

        var manualOverride = new ClipTimeline(92u);
        manualOverride.RequestClip(ClipKind.SideEye);
        manualOverride.RequestClip(ClipKind.Shy, ClipRequestMode.SmoothInterrupt);
        AdvanceToTerminal(manualOverride, ClipKind.SideEye, 240);
        manualOverride.Advance(Step60);
        AssertEqual(
            ClipRequestResult.Started,
            manualOverride.RequestClip(ClipKind.Bomb),
            "Manual request while Idle did not start immediately.");
        AssertSample(
            manualOverride.CurrentSample,
            ClipKind.Bomb,
            ClipPlaybackPhase.Playing,
            0.0,
            "manual Idle override");
        AssertEqual(null, manualOverride.PendingClip, "Manual Idle start left stale pending work.");
    }

    private static void ClickShyAndPause()
    {
        var timeline = new ClipTimeline(77u);
        AssertEqual(ClipRequestResult.Started, timeline.TriggerClick(), "Click was rejected.");
        AssertEqual(ClipKind.Bomb, timeline.CurrentSample.Kind, "First click did not choose Bomb.");
        AdvanceToTerminal(timeline, ClipKind.Bomb, 240);
        timeline.Advance(Step60);
        AssertEqual(ClipRequestResult.Started, timeline.TriggerClick(), "Second click was rejected.");
        AssertEqual(ClipKind.Shy, timeline.CurrentSample.Kind, "Second click did not choose Shy.");

        timeline.Advance(0.1);
        timeline.SetPaused(true);
        Assert(timeline.IsPaused, "Timeline did not pause.");
        AssertSample(
            timeline.CurrentSample,
            ClipKind.Idle,
            ClipPlaybackPhase.Paused,
            0.0,
            "paused sample");
        AssertEqual(Pose.Neutral, timeline.CurrentProceduralPose, "Pause did not neutralize pose.");
        AssertEqual(ClipRequestResult.RejectedPaused, timeline.TriggerShy(), "Paused Shy was accepted.");
        timeline.SetPaused(false);
        AssertSample(
            timeline.CurrentSample,
            ClipKind.Idle,
            ClipPlaybackPhase.Idle,
            0.0,
            "resumed sample");
    }

    private static void ProceduralBreathingContinuity()
    {
        var timeline = new ClipTimeline(0xBEEFu);
        Pose initial = timeline.CurrentProceduralPose;
        Pose previous = initial;
        bool moved = false;
        for (int frame = 0; frame < 60; frame++)
        {
            timeline.Advance(Step60);
            Pose pose = timeline.CurrentProceduralPose;
            Assert(pose.IsFinite, $"Idle pose {frame} was non-finite.");
            AssertPoseStep(previous, pose, 0.004, 0.05, 0.05, $"idle frame {frame}");
            moved |= PoseDistance(initial, pose) > 1e-5;
            previous = pose;
        }

        Assert(moved, "Idle breathing did not move.");
        Pose before = timeline.CurrentProceduralPose;
        timeline.RequestClip(ClipKind.Yawn);
        AssertPoseNear(before, timeline.CurrentProceduralPose, 0.0, "Clip start popped breathing.");

        previous = timeline.CurrentProceduralPose;
        for (int frame = 0; frame < 180 && timeline.CurrentSample.Phase != ClipPlaybackPhase.Terminal; frame++)
        {
            timeline.Advance(Step60);
            Pose pose = timeline.CurrentProceduralPose;
            Assert(pose.IsFinite, $"Yawn pose {frame} was non-finite.");
            AssertPoseStep(previous, pose, 0.006, 0.09, 0.09, $"Yawn pose {frame}");
            previous = pose;
        }
    }

    private static void LegacyBodyActionsRemainBounded()
    {
        foreach (AnimationAction action in new[]
                 {
                     AnimationAction.Wiggle,
                     AnimationAction.Hop,
                     AnimationAction.Nod,
                     AnimationAction.Shimmy,
                     AnimationAction.Stretch,
                     AnimationAction.Shy,
                 })
        {
            var animator = new PetAnimator((uint)(400 + (int)action));
            Assert(animator.TriggerAction(action), $"Legacy {action} was rejected.");
            Pose previous = animator.CurrentPose;
            for (int frame = 0; frame < 720 && animator.State != AnimationState.Idle; frame++)
            {
                Pose pose = animator.Advance(Step240);
                Assert(pose.IsFinite, $"Legacy {action} produced a non-finite pose.");
                AssertInRange(pose.ScaleX, PetAnimator.MinimumScale, PetAnimator.MaximumScale, $"{action} ScaleX");
                AssertInRange(pose.ScaleY, PetAnimator.MinimumScale, PetAnimator.MaximumScale, $"{action} ScaleY");
                AssertPoseStep(previous, pose, 0.04, 2.0, 1.5, $"{action} frame {frame}");
                previous = pose;
            }

            AssertEqual(AnimationState.Idle, animator.State, $"Legacy {action} did not finish.");
        }
    }

    private static void InputInvariants()
    {
        AssertEqual(ClipSample.Idle, default(ClipSample), "Default ClipSample is not Idle.");
        AssertThrows<ArgumentOutOfRangeException>(
            () => ClipCatalog.GetDefinition((ClipKind)10),
            "Removed transition kind was accepted.");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new ClipTimeline().RequestClip(ClipKind.Idle),
            "Idle was accepted as a requested clip.");
        AssertThrows<ArgumentOutOfRangeException>(
            () => new ClipTimeline().Advance(double.NaN),
            "NaN delta was accepted.");
        AssertThrows<ArgumentNullException>(
            () => new ClipTimeline((IClipRandomSource)null!),
            "Null random source was accepted.");
    }

    private static void AssertAutomaticRunsEveryFrameToTerminal(
        ClipTimeline timeline,
        ClipKind kind)
    {
        double previousFrame = 0.0;
        for (int frame = 1; frame <= 120; frame++)
        {
            ClipSample sample = timeline.Advance(Step60);
            AssertEqual(kind, sample.Kind, $"{kind} did not play continuously.");
            AssertNear(frame, sample.FrameCoordinate, 1e-10, $"{kind} skipped a 60 Hz frame.");
            AssertNear(1.0, sample.FrameCoordinate - previousFrame, 1e-10, $"{kind} frame step changed.");
            previousFrame = sample.FrameCoordinate;
        }

        AssertSample(timeline.CurrentSample, kind, ClipPlaybackPhase.Terminal, 1.0, $"{kind} terminal");
    }

    private static void AssertIdleForFrames(ClipTimeline timeline, int frameCount, string context)
    {
        for (int frame = 0; frame < frameCount; frame++)
        {
            ClipSample sample = timeline.Advance(Step60);
            AssertEqual(ClipKind.Idle, sample.Kind, $"{context} ended at frame {frame}.");
        }
    }

    private static void AdvanceToTerminal(ClipTimeline timeline, ClipKind kind, int maximumFrames)
    {
        for (int frame = 0; frame < maximumFrames && timeline.CurrentSample.Phase != ClipPlaybackPhase.Terminal; frame++)
        {
            ClipSample sample = timeline.Advance(Step60);
            AssertEqual(kind, sample.Kind, $"{kind} switched before Terminal.");
        }

        AssertEqual(ClipPlaybackPhase.Terminal, timeline.CurrentSample.Phase, $"{kind} did not finish.");
    }

    private static void AssertSample(
        ClipSample sample,
        ClipKind kind,
        ClipPlaybackPhase phase,
        double progress,
        string context)
    {
        AssertEqual(kind, sample.Kind, $"{context}: wrong kind.");
        AssertEqual(phase, sample.Phase, $"{context}: wrong phase.");
        AssertNear(progress, sample.Progress, 1e-12, $"{context}: wrong progress.");
    }

    private static void AssertPoseStep(
        Pose from,
        Pose to,
        double maximumScaleStep,
        double maximumRotationStep,
        double maximumOffsetStep,
        string context)
    {
        Assert(
            Math.Abs(to.ScaleX - from.ScaleX) <= maximumScaleStep &&
            Math.Abs(to.ScaleY - from.ScaleY) <= maximumScaleStep,
            $"{context}: scale step was too large.");
        Assert(
            Math.Abs(to.RotationDegrees - from.RotationDegrees) <= maximumRotationStep,
            $"{context}: rotation step was too large.");
        Assert(
            Math.Abs(to.OffsetXDip - from.OffsetXDip) <= maximumOffsetStep &&
            Math.Abs(to.OffsetYDip - from.OffsetYDip) <= maximumOffsetStep,
            $"{context}: offset step was too large.");
    }

    private static void AssertPoseNear(Pose expected, Pose actual, double tolerance, string message)
    {
        if (PoseDistance(expected, actual) > tolerance)
        {
            throw new InvalidOperationException($"{message} Expected {expected}, actual {actual}.");
        }
    }

    private static double PoseDistance(Pose left, Pose right) => Math.Max(
        Math.Max(Math.Abs(left.ScaleX - right.ScaleX), Math.Abs(left.ScaleY - right.ScaleY)),
        Math.Max(
            Math.Abs(left.RotationDegrees - right.RotationDegrees),
            Math.Max(
                Math.Abs(left.OffsetXDip - right.OffsetXDip),
                Math.Abs(left.OffsetYDip - right.OffsetYDip))));

    private static void AssertInRange(double value, double minimum, double maximum, string context)
    {
        if (!double.IsFinite(value) || value < minimum - 1e-12 || value > maximum + 1e-12)
        {
            throw new InvalidOperationException(
                $"{context}: expected [{minimum}, {maximum}], actual {value}.");
        }
    }

    private static void AssertNear(double expected, double actual, double tolerance, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message} Expected {expected}, actual {actual}.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected {expected}, actual {actual}.");
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ScriptedRandomSource : IClipRandomSource
    {
        private readonly uint[] _values;
        private int _index;

        public ScriptedRandomSource(params uint[] values)
        {
            _values = values;
        }

        public int CallCount => _index;

        public uint NextUInt32()
        {
            uint value = _index < _values.Length ? _values[_index] : 0u;
            _index++;
            return value;
        }
    }
}
