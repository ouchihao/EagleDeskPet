namespace DuckDeskPet.Core;

/// <summary>
/// Finite seated hunger vignette. Cancellation never replaces an authored frame:
/// finish the current segment, then play the complete exit. No idle pose is
/// injected between its entry, loops, and exit.
/// </summary>
public sealed class HungrySceneTimeline
{
    public const int MaximumLoopCount = 2;
    private bool _cancelRequested;
    private double _elapsedSeconds;
    private long _sequence;
    public int CompletedLoopCount { get; private set; }
    public ClipSample CurrentSample { get; private set; } = ClipSample.Idle;
    public bool IsActive => ClipCatalog.IsHungryScene(CurrentSample.Kind);

    public void Start(long minimumSequence = 0)
    {
        if (IsActive) return;
        _sequence = Math.Max(_sequence, minimumSequence);
        _cancelRequested = false;
        CompletedLoopCount = 0;
        Begin(ClipKind.HungryEnter);
    }

    public void Cancel() => _cancelRequested = true;

    public ClipSample Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (!IsActive) return CurrentSample;
        if (CurrentSample.Phase == ClipPlaybackPhase.Terminal)
        {
            ResolveTerminal();
            return CurrentSample;
        }
        double duration = CurrentSample.DurationSeconds;
        _elapsedSeconds = Math.Min(duration, _elapsedSeconds + Math.Min(deltaSeconds, ClipTimeline.MaximumDeltaSeconds));
        double progress = Math.Clamp(_elapsedSeconds / duration, CurrentSample.Progress, 1);
        bool terminal = progress >= 1 - 1e-12;
        CurrentSample = new(CurrentSample.Kind, terminal ? ClipPlaybackPhase.Terminal : ClipPlaybackPhase.Playing,
            terminal ? 1 : progress, _sequence);
        return CurrentSample;
    }

    private void ResolveTerminal()
    {
        if (CurrentSample.Kind == ClipKind.HungryExit)
        {
            CurrentSample = new(ClipKind.Idle, ClipPlaybackPhase.Idle, 0, _sequence);
            return;
        }
        if (CurrentSample.Kind == ClipKind.HungryLoop) CompletedLoopCount++;
        Begin(_cancelRequested || CompletedLoopCount >= MaximumLoopCount ? ClipKind.HungryExit : ClipKind.HungryLoop);
    }

    private void Begin(ClipKind kind)
    {
        _sequence++;
        _elapsedSeconds = 0;
        CurrentSample = new(kind, ClipPlaybackPhase.Playing, 0, _sequence);
    }
}
