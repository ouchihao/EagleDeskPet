using System;

namespace DuckDeskPet.Core;

/// <summary>
/// Deterministic, allocation-free scheduler for complete action clips. Every
/// automatic action plays from zero to one, followed by an exact two-second
/// idle interval before the next idle-group action. Yawn is currently the only
/// automatic action; interaction and work clips never enter this pool.
/// </summary>
public sealed class ClipTimeline
{
    public const double MaximumDeltaSeconds = 0.050;
    public const double AutomaticIdleDurationSeconds = 2.0;

    private const double ProceduralFadeFraction = 0.15;
    private const double Tau = Math.PI * 2.0;

    private readonly IClipRandomSource _randomSource;
    private double _motionTimeSeconds;
    private double _secondsUntilAutomaticClip;
    private double _naturalElapsedSeconds;
    private long _sequence;
    private ClipKind? _pendingClip;
    private ClipKind _lastClickClip;
    private Pose _clipStartProceduralPose;

    public ClipTimeline(uint randomSeed = 0xD0C0_2026u)
        : this(new XorShiftClipRandomSource(randomSeed))
    {
    }

    public ClipTimeline(IClipRandomSource randomSource)
    {
        _randomSource = randomSource ?? throw new ArgumentNullException(nameof(randomSource));
        CurrentSample = ClipSample.Idle;
        CurrentProceduralPose = Pose.Neutral;
        _clipStartProceduralPose = Pose.Neutral;
        _lastClickClip = ClipKind.Shy;
        ScheduleNextAutomaticClip();
    }

    public ClipSample CurrentSample { get; private set; }

    /// <summary>
    /// Procedural breathing only. Compose this with the authored raster frame;
    /// it fades to neutral near the beginning and end of an action.
    /// </summary>
    public Pose CurrentProceduralPose { get; private set; }

    public ClipKind? PendingClip => _pendingClip;

    public bool IsPaused => CurrentSample.Phase == ClipPlaybackPhase.Paused;

    public ClipRequestResult RequestClip(
        ClipKind kind,
        ClipRequestMode mode = ClipRequestMode.Queue)
    {
        ValidatePlayableKind(kind);
        if ((uint)mode > (uint)ClipRequestMode.SmoothInterrupt)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (IsPaused)
        {
            return ClipRequestResult.RejectedPaused;
        }

        if (CurrentSample.Kind == ClipKind.Idle)
        {
            _pendingClip = null;
            BeginClip(kind);
            return ClipRequestResult.Started;
        }

        if (CurrentSample.Kind == kind || _pendingClip == kind)
        {
            return ClipRequestResult.IgnoredDuplicate;
        }

        bool replaced = _pendingClip.HasValue;
        _pendingClip = kind;
        return replaced ? ClipRequestResult.ReplacedQueued : ClipRequestResult.Queued;
    }

    public ClipRequestResult TriggerClick()
    {
        ClipKind requested = _lastClickClip == ClipKind.Bomb
            ? ClipKind.Shy
            : ClipKind.Bomb;
        ClipRequestResult result = RequestClip(requested, ClipRequestMode.SmoothInterrupt);
        if (result != ClipRequestResult.RejectedPaused &&
            result != ClipRequestResult.IgnoredDuplicate)
        {
            _lastClickClip = requested;
        }

        return result;
    }

    public ClipRequestResult TriggerShy() =>
        RequestClip(ClipKind.Shy, ClipRequestMode.SmoothInterrupt);

    public ClipSample Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaSeconds),
                "Delta time must be finite and non-negative.");
        }

        if (IsPaused)
        {
            CurrentProceduralPose = Pose.Neutral;
            return CurrentSample;
        }

        double boundedDeltaSeconds = Math.Min(deltaSeconds, MaximumDeltaSeconds);
        _motionTimeSeconds += boundedDeltaSeconds;

        if (CurrentSample.Phase == ClipPlaybackPhase.Terminal)
        {
            ResolveTerminal();
            UpdateProceduralPose();
            return CurrentSample;
        }

        if (CurrentSample.Kind == ClipKind.Idle)
        {
            _secondsUntilAutomaticClip -= boundedDeltaSeconds;
            if (_secondsUntilAutomaticClip <= 1e-12)
            {
                if (_pendingClip is ClipKind pending)
                {
                    _pendingClip = null;
                    BeginClip(pending);
                }
                else
                {
                    BeginAutomaticClip();
                }
            }

            UpdateProceduralPose();
            return CurrentSample;
        }

        AdvancePlayableClip(boundedDeltaSeconds);
        UpdateProceduralPose();
        return CurrentSample;
    }

    public void SetPaused(bool paused)
    {
        if (paused)
        {
            _pendingClip = null;
            _naturalElapsedSeconds = 0.0;
            CurrentSample = new ClipSample(
                ClipKind.Idle,
                ClipPlaybackPhase.Paused,
                0.0,
                _sequence);
            CurrentProceduralPose = Pose.Neutral;
            return;
        }

        if (!IsPaused)
        {
            return;
        }

        _motionTimeSeconds = 0.0;
        CurrentSample = new ClipSample(
            ClipKind.Idle,
            ClipPlaybackPhase.Idle,
            0.0,
            _sequence);
        CurrentProceduralPose = Pose.Neutral;
        ScheduleNextAutomaticClip();
    }

    private void AdvancePlayableClip(double deltaSeconds)
    {
        ClipDefinition definition = ClipCatalog.GetDefinition(CurrentSample.Kind);
        _naturalElapsedSeconds = Math.Min(
            _naturalElapsedSeconds + deltaSeconds,
            definition.DurationSeconds);
        double progress = _naturalElapsedSeconds / definition.DurationSeconds;
        progress = Math.Clamp(progress, CurrentSample.Progress, 1.0);
        if (progress >= 1.0 - 1e-12)
        {
            CurrentSample = new ClipSample(
                CurrentSample.Kind,
                ClipPlaybackPhase.Terminal,
                1.0,
                CurrentSample.Sequence);
            return;
        }

        CurrentSample = new ClipSample(
            CurrentSample.Kind,
            ClipPlaybackPhase.Playing,
            progress,
            CurrentSample.Sequence);
    }

    private void ResolveTerminal()
    {
        CurrentSample = new ClipSample(
            ClipKind.Idle,
            ClipPlaybackPhase.Idle,
            0.0,
            _sequence);
        ScheduleNextAutomaticClip();
    }

    private void BeginClip(ClipKind kind)
    {
        _sequence++;
        _naturalElapsedSeconds = 0.0;
        _clipStartProceduralPose = CurrentProceduralPose;
        CurrentSample = new ClipSample(
            kind,
            ClipPlaybackPhase.Playing,
            0.0,
            _sequence);
    }

    private void UpdateProceduralPose()
    {
        Pose idlePose = SampleIdlePose(_motionTimeSeconds);
        if (CurrentSample.Kind == ClipKind.Idle)
        {
            CurrentProceduralPose = idlePose;
            return;
        }

        double progress = CurrentSample.Progress;
        if (progress < ProceduralFadeFraction)
        {
            double amount = SmoothStep(progress / ProceduralFadeFraction);
            CurrentProceduralPose = Pose.Lerp(
                _clipStartProceduralPose,
                Pose.Neutral,
                amount);
            return;
        }

        double fadeOutStart = 1.0 - ProceduralFadeFraction;
        if (progress > fadeOutStart)
        {
            double amount = SmoothStep(
                (progress - fadeOutStart) / ProceduralFadeFraction);
            CurrentProceduralPose = Pose.Lerp(Pose.Neutral, idlePose, amount);
            return;
        }

        CurrentProceduralPose = Pose.Neutral;
    }

    private void BeginAutomaticClip()
    {
        ClipKind next = ChooseAutomaticClip();
        BeginClip(next);
    }

    private ClipKind ChooseAutomaticClip()
    {
        // Keep a stable RNG consumption contract for future idle-group additions,
        // without relying on contiguous enum values or interaction asset counts.
        _randomSource.NextUInt32();
        return ClipKind.Yawn;
    }

    internal void ResetToIdle(long minimumSequence)
    {
        _sequence = Math.Max(_sequence, minimumSequence);
        _motionTimeSeconds = 0.0;
        _naturalElapsedSeconds = 0.0;
        _pendingClip = null;
        CurrentSample = new(ClipKind.Idle, ClipPlaybackPhase.Idle, 0.0, _sequence);
        CurrentProceduralPose = Pose.Neutral;
        ScheduleNextAutomaticClip();
    }

    private void ScheduleNextAutomaticClip()
    {
        _secondsUntilAutomaticClip = AutomaticIdleDurationSeconds;
    }

    private static void ValidatePlayableKind(ClipKind kind)
    {
        if (!ClipCatalog.IsKnown(kind) || kind == ClipKind.Idle || ClipCatalog.IsWorkScene(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "Idle is procedural and cannot be requested as a clip.");
        }
    }

    private static Pose SampleIdlePose(double timeSeconds)
    {
        double breathPhase = timeSeconds * Tau / 3.2;
        double breath = Math.Sin(breathPhase);
        return new Pose(
            1.0 - (0.004 * breath),
            1.0 + (0.012 * breath),
            0.35 * Math.Sin(timeSeconds * Tau / 5.0),
            0.0,
            -0.35 * (1.0 - Math.Cos(breathPhase)));
    }

    private static double SmoothStep(double value)
    {
        double t = Math.Clamp(value, 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }
}
