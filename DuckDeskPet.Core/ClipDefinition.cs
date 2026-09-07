using System;

namespace DuckDeskPet.Core;

/// <summary>
/// Immutable metadata for one complete Idle-to-action-to-Idle clip.
/// FrameCount is endpoint-inclusive.
/// </summary>
public readonly record struct ClipDefinition
{
    public ClipDefinition(
        ClipKind kind,
        int frameCount,
        double durationSeconds,
        double peakProgress)
    {
        if (!ClipCatalog.IsKnown(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (frameCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        if (!double.IsFinite(durationSeconds) || durationSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        if (!double.IsFinite(peakProgress) || peakProgress < 0.0 || peakProgress > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(peakProgress));
        }

        if (kind == ClipKind.Idle && (frameCount != 1 || durationSeconds != 0.0 || peakProgress != 0.0))
        {
            throw new ArgumentException("Idle must be a single, zero-duration neutral sample.");
        }

        if (kind != ClipKind.Idle && (frameCount < 2 || durationSeconds <= 0.0))
        {
            throw new ArgumentException("A playable clip needs at least two frames and a positive duration.");
        }

        Kind = kind;
        FrameCount = frameCount;
        DurationSeconds = durationSeconds;
        PeakProgress = peakProgress;
    }

    public ClipKind Kind { get; }

    public int FrameCount { get; }

    public double DurationSeconds { get; }

    public double PeakProgress { get; }
}
