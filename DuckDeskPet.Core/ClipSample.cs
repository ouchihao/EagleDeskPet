using System;

namespace DuckDeskPet.Core;

/// <summary>
/// Allocation-free timeline output. Progress is always forward-only for a
/// given Sequence. A renderer can interpolate authored frames with
/// LowerFrameIndex, UpperFrameIndex, and FrameBlend.
/// </summary>
public readonly record struct ClipSample
{
    public ClipSample(
        ClipKind kind,
        ClipPlaybackPhase phase,
        double progress,
        long sequence)
    {
        if (!ClipCatalog.IsKnown(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if ((uint)phase > (uint)ClipPlaybackPhase.Paused)
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (!double.IsFinite(progress) || progress < 0.0 || progress > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(progress));
        }

        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (kind == ClipKind.Idle && progress != 0.0)
        {
            throw new ArgumentException("Idle samples must have zero progress.");
        }

        if (kind != ClipKind.Idle &&
            (phase == ClipPlaybackPhase.Idle || phase == ClipPlaybackPhase.Paused))
        {
            throw new ArgumentException("Playable clips cannot use an idle or paused phase.");
        }

        if (phase == ClipPlaybackPhase.Terminal && progress != 1.0)
        {
            throw new ArgumentException("Terminal samples must have progress one.");
        }

        Kind = kind;
        Phase = phase;
        Progress = progress;
        Sequence = sequence;
    }

    public ClipKind Kind { get; }

    public ClipPlaybackPhase Phase { get; }

    public double Progress { get; }

    public long Sequence { get; }

    public double DurationSeconds => ClipCatalog.GetDefinition(Kind).DurationSeconds;

    public double ElapsedSeconds => Progress * DurationSeconds;

    public double FrameCoordinate =>
        Progress * (ClipCatalog.GetDefinition(Kind).FrameCount - 1);

    public int LowerFrameIndex => (int)Math.Floor(FrameCoordinate);

    public int UpperFrameIndex
    {
        get
        {
            int last = ClipCatalog.GetDefinition(Kind).FrameCount - 1;
            return Math.Min(LowerFrameIndex + 1, last);
        }
    }

    public double FrameBlend => FrameCoordinate - LowerFrameIndex;

    public static ClipSample Idle { get; } =
        new(ClipKind.Idle, ClipPlaybackPhase.Idle, 0.0, 0);
}
