namespace DuckDeskPet.Core;

/// <summary>
/// A persistent work scene with authored entry, loop, bridge and exit phases.
/// State changes are honored only at a phase's complete terminal frame. No
/// neutral-standing frames or image crossfades are injected between work loops.
/// Wall-clock work accounting belongs to PetCareService, not this render clock.
/// </summary>
public sealed class WorkSceneTimeline
{
    private bool _workDesired;
    private bool _busyDesired;
    private double _elapsedSeconds;
    private long _sequence;

    public ClipSample CurrentSample { get; private set; } = ClipSample.Idle;
    public bool IsActive => ClipCatalog.IsWorkScene(CurrentSample.Kind);

    public void Start(bool busy, long minimumSequence = 0)
    {
        if (IsActive) return;
        _sequence = Math.Max(_sequence, minimumSequence);
        _workDesired = true;
        _busyDesired = busy;
        Begin(ClipKind.WorkEnter);
    }

    public void SetDesiredState(bool working, bool busy)
    {
        _workDesired = working;
        _busyDesired = working && busy;
    }

    public ClipSample Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (!IsActive) return CurrentSample;

        if (CurrentSample.Phase == ClipPlaybackPhase.Terminal)
        {
            ResolveTerminal();
            return CurrentSample;
        }

        double duration = CurrentSample.DurationSeconds;
        _elapsedSeconds = Math.Min(duration,
            _elapsedSeconds + Math.Min(deltaSeconds, ClipTimeline.MaximumDeltaSeconds));
        double progress = Math.Clamp(_elapsedSeconds / duration, CurrentSample.Progress, 1.0);
        bool terminal = progress >= 1.0 - 1e-12;
        CurrentSample = new(CurrentSample.Kind,
            terminal ? ClipPlaybackPhase.Terminal : ClipPlaybackPhase.Playing,
            terminal ? 1.0 : progress, _sequence);
        return CurrentSample;
    }

    private void ResolveTerminal()
    {
        ClipKind current = CurrentSample.Kind;
        if (current is ClipKind.WorkExit or ClipKind.BusyExit)
        {
            CurrentSample = new(ClipKind.Idle, ClipPlaybackPhase.Idle, 0.0, _sequence);
            return;
        }

        bool atBusyPose = current is ClipKind.WorkToBusy or ClipKind.BusyLoop;
        if (!_workDesired)
        {
            Begin(atBusyPose ? ClipKind.BusyExit : ClipKind.WorkExit);
        }
        else if (atBusyPose)
        {
            Begin(ClipKind.BusyLoop);
        }
        else
        {
            Begin(_busyDesired ? ClipKind.WorkToBusy : ClipKind.WorkLoop);
        }
    }

    private void Begin(ClipKind kind)
    {
        _sequence++;
        _elapsedSeconds = 0.0;
        CurrentSample = new(kind, ClipPlaybackPhase.Playing, 0.0, _sequence);
    }
}
