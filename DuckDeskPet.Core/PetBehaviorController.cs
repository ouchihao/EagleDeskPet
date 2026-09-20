namespace DuckDeskPet.Core;

public enum PetBehaviorKind
{
    Fed,
    Petted,
    Notification,
    ManualAction,
    Annoyed,
    AutomaticEmotion,
}

public enum PetBehaviorRequestResult
{
    Queued,
    Coalesced,
    RejectedPaused,
    RejectedFull,
    RejectedWorking,
}

/// <summary>
/// Semantic requests sit above the raster timeline. They never replace a clip in
/// flight, and normal reactions wait for the complete two-second idle beat.
/// The wrapped timeline is owned here; callers must not request clips on it.
/// </summary>
public sealed class PetBehaviorController
{
    public const int MaximumPendingBehaviors = 8;

    private readonly ClipTimeline _timeline;
    private readonly List<PendingBehavior> _pending = new();
    private readonly WorkSceneTimeline _workScene = new();
    private readonly HungrySceneTimeline _hungryScene = new();
    private double _idleElapsed;
    private bool _workDesired;
    private bool _busyDesired;
    private bool _hungryDesired;
    private bool _paused;
    private Pose _workEntryPose = Pose.Neutral;

    public PetBehaviorController(ClipTimeline? timeline = null)
    {
        _timeline = timeline ?? new ClipTimeline();
        _paused = _timeline.IsPaused;
    }

    public ClipSample CurrentSample => _workScene.IsActive ? _workScene.CurrentSample :
        _hungryScene.IsActive ? _hungryScene.CurrentSample : _timeline.CurrentSample;
    public Pose CurrentProceduralPose => _hungryScene.IsActive ? Pose.Neutral : _workScene.IsActive
        ? CurrentSample.Kind == ClipKind.WorkEnter
            ? Pose.Lerp(_workEntryPose, Pose.Neutral, Math.Clamp(CurrentSample.ElapsedSeconds / 0.3, 0.0, 1.0))
            : Pose.Neutral
        : _timeline.CurrentProceduralPose;
    public bool IsPaused => _paused;
    public int PendingCount => _pending.Count;
    public bool IsWorkSceneActive => _workScene.IsActive;
    public bool IsWorkRequested => _workDesired;
    public bool IsHungrySceneActive => _hungryScene.IsActive;
    public bool IsHungryRequested => _hungryDesired;
    public bool IsAutomaticSuspended => _timeline.IsAutomaticSuspended;

    public void SuspendAutomatic(bool suspended) => _timeline.SuspendAutomatic(suspended);

    public PetBehaviorRequestResult RequestHungryScene()
    {
        if (IsPaused) return PetBehaviorRequestResult.RejectedPaused;
        if (_workDesired || _workScene.IsActive) return PetBehaviorRequestResult.RejectedWorking;
        if (_hungryDesired || _hungryScene.IsActive) return PetBehaviorRequestResult.Coalesced;
        _hungryDesired = true;
        return PetBehaviorRequestResult.Queued;
    }

    public void CancelHungryScene()
    {
        _hungryDesired = false;
        _hungryScene.Cancel();
    }

    public PetBehaviorRequestResult QueueAutomaticEmotion(EmotionReaction reaction) => reaction switch
    {
        EmotionReaction.HungryScene => RequestHungryScene(),
        EmotionReaction.Annoyed => Enqueue(new(PetBehaviorKind.AutomaticEmotion, ClipKind.Annoyed)),
        _ => throw new ArgumentOutOfRangeException(nameof(reaction)),
    };

    /// <summary>Disable automatic emotion without discarding deliberate touch reactions.</summary>
    public void CancelAutomaticEmotions()
    {
        _pending.RemoveAll(x => x.Kind == PetBehaviorKind.AutomaticEmotion);
        CancelHungryScene();
    }

    public void SetWorkState(bool working, bool busy)
    {
        if (working && !_workDesired) _pending.Clear();
        _workDesired = working;
        _busyDesired = working && busy;
        _workScene.SetDesiredState(working, busy);
        if (working) CancelHungryScene();
    }

    public PetBehaviorRequestResult RequestAction(ClipKind kind)
    {
        if (!ClipCatalog.IsKnown(kind) || kind == ClipKind.Idle || ClipCatalog.IsScene(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return Enqueue(new(PetBehaviorKind.ManualAction, kind));
    }

    /// <summary>
    /// A minigame already holding exclusive automatic-action ownership can start
    /// its next authored clip at the current idle boundary. This never interrupts,
    /// drains, replaces or skips an existing request; ordinary requests still
    /// use their two-second idle beat. The whitelist prevents use as a general
    /// shortcut around feeding, work or scene admission rules.
    /// </summary>
    public bool TryStartExclusiveGameAction(ClipKind kind)
    {
        if (kind is not (ClipKind.RpsRock or ClipKind.RpsPaper or ClipKind.RpsScissors or
            ClipKind.RpsWin or ClipKind.RpsLose or ClipKind.Shy))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!_timeline.IsAutomaticSuspended || IsPaused || _pending.Count != 0 ||
            _timeline.PendingClip.HasValue || CurrentSample.Kind != ClipKind.Idle ||
            _workDesired || _workScene.IsActive || _hungryDesired || _hungryScene.IsActive)
            return false;
        if (_timeline.RequestClip(kind) != ClipRequestResult.Started) return false;
        _idleElapsed = 0;
        return true;
    }

    public PetBehaviorRequestResult QueueReaction(PetBehaviorKind kind) => kind switch
    {
        PetBehaviorKind.Fed => Enqueue(new(kind, ClipKind.Eat)),
        PetBehaviorKind.Petted => Enqueue(new(kind, ClipKind.Shy)),
        PetBehaviorKind.Notification => Enqueue(new(kind, ClipKind.SideEye)),
        PetBehaviorKind.Annoyed => Enqueue(new(kind, ClipKind.Annoyed)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Read-only admission check for a deliberate reaction. A caller may perform
    /// a synchronous care transaction and enqueue on the same owner thread, with
    /// no await or message pump between these operations. Coalesced is not a new
    /// animation and must not authorize another consumable.
    /// </summary>
    public PetBehaviorRequestResult PreviewReaction(PetBehaviorKind kind)
    {
        var behavior = kind switch
        {
            PetBehaviorKind.Fed => new PendingBehavior(kind, ClipKind.Eat),
            PetBehaviorKind.Petted => new PendingBehavior(kind, ClipKind.Shy),
            PetBehaviorKind.Notification => new PendingBehavior(kind, ClipKind.SideEye),
            PetBehaviorKind.Annoyed => new PendingBehavior(kind, ClipKind.Annoyed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (IsPaused) return PetBehaviorRequestResult.RejectedPaused;
        if (_workDesired || _workScene.IsActive) return PetBehaviorRequestResult.RejectedWorking;
        if (_pending.Contains(behavior)) return PetBehaviorRequestResult.Coalesced;
        var effective = kind == PetBehaviorKind.Annoyed
            ? _pending.Where(x => x.Kind != PetBehaviorKind.Petted) : _pending;
        if (effective.Count() >= MaximumPendingBehaviors && !effective.Any(x => Priority(x.Kind) < Priority(kind)))
            return PetBehaviorRequestResult.RejectedFull;
        return PetBehaviorRequestResult.Queued;
    }

    public ClipSample Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }

        if (IsPaused)
        {
            return CurrentSample;
        }

        if (_workScene.IsActive)
        {
            ClipSample workSample = _workScene.Advance(deltaSeconds);
            if (!_workScene.IsActive)
            {
                _timeline.ResetToIdle(workSample.Sequence);
                _idleElapsed = 0.0;
            }

            return CurrentSample;
        }

        if (_hungryScene.IsActive)
        {
            ClipSample hungrySample = _hungryScene.Advance(deltaSeconds);
            if (!_hungryScene.IsActive)
            {
                _hungryDesired = false;
                _timeline.ResetToIdle(hungrySample.Sequence);
                _idleElapsed = 0;
            }
            return CurrentSample;
        }

        double boundedDelta = Math.Min(deltaSeconds, ClipTimeline.MaximumDeltaSeconds);
        ClipSample before = CurrentSample;
        if (before.Kind == ClipKind.Idle)
        {
            if (_workDesired)
            {
                _workEntryPose = _timeline.CurrentProceduralPose;
                _workScene.Start(_busyDesired, before.Sequence);
                _idleElapsed = 0.0;
                return CurrentSample;
            }

            _idleElapsed += boundedDelta;
            if (_hungryDesired && _pending.Count == 0 &&
                _idleElapsed >= ClipTimeline.AutomaticIdleDurationSeconds - 1e-12)
            {
                _hungryScene.Start(before.Sequence);
                _idleElapsed = 0;
                return CurrentSample;
            }

            if (_pending.Count > 0 && _idleElapsed >= ClipTimeline.AutomaticIdleDurationSeconds - 1e-12)
            {
                int index = SelectNextIndex();
                PendingBehavior next = _pending[index];
                _pending.RemoveAt(index);
                _timeline.RequestClip(next.Clip);
                _idleElapsed = 0.0;
                // Return the authored first frame exactly as the timeline's
                // automatic branch does; do not advance the clip on its start tick.
                return CurrentSample;
            }
        }

        ClipSample sample = _timeline.Advance(deltaSeconds);
        if (sample.Kind != ClipKind.Idle || before.Kind != ClipKind.Idle)
        {
            _idleElapsed = 0.0;
        }

        return sample;
    }

    public void SetPaused(bool paused)
    {
        if (paused == IsPaused)
        {
            return;
        }

        _paused = paused;
        if (!_workScene.IsActive && !_hungryScene.IsActive) _timeline.SetPaused(paused);
        _pending.Clear();
        _idleElapsed = 0.0;
    }

    private PetBehaviorRequestResult Enqueue(PendingBehavior behavior)
    {
        if (IsPaused)
        {
            return PetBehaviorRequestResult.RejectedPaused;
        }

        if (_workDesired || _workScene.IsActive)
        {
            return PetBehaviorRequestResult.RejectedWorking;
        }

        // A deliberate touch complaint supersedes queued shy reactions, not an
        // already-running frame sequence. It still observes the two-second beat.
        if (behavior.Kind == PetBehaviorKind.Annoyed)
            _pending.RemoveAll(x => x.Kind == PetBehaviorKind.Petted);

        if (_pending.Contains(behavior))
        {
            return PetBehaviorRequestResult.Coalesced;
        }

        if (_pending.Count >= MaximumPendingBehaviors)
        {
            int disposable = _pending.FindLastIndex(item => Priority(item.Kind) < Priority(behavior.Kind));
            if (disposable < 0)
            {
                return PetBehaviorRequestResult.RejectedFull;
            }

            _pending.RemoveAt(disposable);
        }

        _pending.Add(behavior);
        return PetBehaviorRequestResult.Queued;
    }

    private int SelectNextIndex()
    {
        int next = 0;
        for (int index = 1; index < _pending.Count; index++)
        {
            if (Priority(_pending[index].Kind) > Priority(_pending[next].Kind))
            {
                next = index;
            }
        }

        return next;
    }

    private static int Priority(PetBehaviorKind kind) => kind switch
    {
        PetBehaviorKind.Fed or PetBehaviorKind.ManualAction or PetBehaviorKind.Annoyed => 3,
        PetBehaviorKind.Petted => 2,
        PetBehaviorKind.AutomaticEmotion => 0,
        _ => 1,
    };

    private readonly record struct PendingBehavior(PetBehaviorKind Kind, ClipKind Clip);
}
