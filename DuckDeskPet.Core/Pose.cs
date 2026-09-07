using System;

namespace DuckDeskPet.Core;

/// <summary>
/// An allocation-free snapshot of the transforms applied to the mascot image.
/// Offsets are expressed in WPF device-independent pixels.
/// </summary>
public readonly record struct Pose(
    double ScaleX,
    double ScaleY,
    double RotationDegrees,
    double OffsetXDip,
    double OffsetYDip)
{
    public static Pose Neutral { get; } = new(1.0, 1.0, 0.0, 0.0, 0.0);

    public bool IsFinite =>
        double.IsFinite(ScaleX) &&
        double.IsFinite(ScaleY) &&
        double.IsFinite(RotationDegrees) &&
        double.IsFinite(OffsetXDip) &&
        double.IsFinite(OffsetYDip);

    /// <summary>
    /// Applies a relative modifier to a base pose. Scales multiply; rotation
    /// and translation add.
    /// </summary>
    public static Pose Compose(Pose basePose, Pose modifier) => new(
        basePose.ScaleX * modifier.ScaleX,
        basePose.ScaleY * modifier.ScaleY,
        basePose.RotationDegrees + modifier.RotationDegrees,
        basePose.OffsetXDip + modifier.OffsetXDip,
        basePose.OffsetYDip + modifier.OffsetYDip);

    public static Pose Lerp(Pose from, Pose to, double progress)
    {
        double amount = Math.Clamp(progress, 0.0, 1.0);
        return new Pose(
            from.ScaleX + ((to.ScaleX - from.ScaleX) * amount),
            from.ScaleY + ((to.ScaleY - from.ScaleY) * amount),
            from.RotationDegrees + ((to.RotationDegrees - from.RotationDegrees) * amount),
            from.OffsetXDip + ((to.OffsetXDip - from.OffsetXDip) * amount),
            from.OffsetYDip + ((to.OffsetYDip - from.OffsetYDip) * amount));
    }
}
