namespace DuckDeskPet.Core;

public enum LoafingPhase { None, Throw, Loop, Return }

/// <summary>Visual handoff is independent of the persisted, wall-clock break</summary>
public sealed class LoafingVisualTimeline
{
    public const double TransitionSeconds = 2.8;
    public LoafingPhase Phase { get; private set; }
    public LoafingKind Kind { get; private set; }
    public double Seconds { get; private set; }
    public double LoopSeconds { get; private set; }
    public bool Active => Phase != LoafingPhase.None;
    public string ProtocolPhase => Phase.ToString().ToLowerInvariant();
    public double SpriteOffsetY => Phase switch
    {
        LoafingPhase.Throw => 240 * (1 - Ease((Seconds - 1.95) / .85)),
        LoafingPhase.Loop => 0,
        LoafingPhase.Return => 240 * Ease(Seconds / .8),
        _ => 240
    };
    public bool SpriteVisible => Phase == LoafingPhase.Loop ||
        Phase == LoafingPhase.Throw && Seconds > 1.95 || Phase == LoafingPhase.Return && Seconds < .8;

    public void Advance(double elapsed, LoafingKind desired, bool stableWork, bool paused)
    {
        if (paused) return;
        double delta = Math.Max(0, elapsed);
        if (!Active)
        {
            if (desired == LoafingKind.None || !stableWork) return;
            Kind = desired; Phase = LoafingPhase.Throw; Seconds = LoopSeconds = 0;
            return;
        }
        Seconds += delta;
        if (SpriteVisible) LoopSeconds += delta;
        if (Phase == LoafingPhase.Throw && Seconds >= TransitionSeconds)
        {
            Phase = desired == LoafingKind.None ? LoafingPhase.Return : LoafingPhase.Loop;
            Seconds -= TransitionSeconds;
        }
        else if (Phase == LoafingPhase.Loop && desired == LoafingKind.None)
        { Phase = LoafingPhase.Return; Seconds = 0; }
        else if (Phase == LoafingPhase.Return && Seconds >= TransitionSeconds)
        { Phase = LoafingPhase.None; Kind = LoafingKind.None; Seconds = LoopSeconds = 0; }
    }

    public static int SourceFrame(double seconds)
    {
        int frame = (int)(Math.Max(0, seconds) / .04 + 1e-8) % 68;
        return frame <= 34 ? frame : 68 - frame;
    }

    public static double Ease(double value)
    { double t = Math.Clamp(value, 0, 1); return t * t * (3 - 2 * t); }
}
