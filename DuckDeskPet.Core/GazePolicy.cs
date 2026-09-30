namespace DuckDeskPet.Core;

public readonly record struct GazeTarget(bool Enabled, double X, double Y)
{
    public static GazeTarget Neutral => new(false, 0, 0);
}

/// <summary>Maps a pointer already converted to image-local DIPs; never moves a window or reads screen content</summary>
public static class GazePolicy
{
    public const double FullAttentionRadiusDip = 140;
    public const double MaximumDistanceDip = 240;

    public static bool Supports(ClipKind kind) => kind is ClipKind.Idle or ClipKind.WorkLoop or ClipKind.BusyLoop;

    public static GazeTarget Resolve(double pointerX, double pointerY, double imageWidth, double imageHeight)
    {
        if (!double.IsFinite(pointerX) || !double.IsFinite(pointerY) ||
            !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight) || imageWidth <= 0 || imageHeight <= 0)
            return GazeTarget.Neutral;
        double scale = Math.Min(imageWidth / 384, imageHeight / 346);
        double x = pointerX - ((imageWidth - 384 * scale) / 2 + 193 * scale);
        double y = pointerY - ((imageHeight - 346 * scale) / 2 + 147 * scale);
        double distance = Math.Sqrt(x * x + y * y);
        if (distance >= MaximumDistanceDip) return GazeTarget.Neutral;
        double fade = Math.Clamp((MaximumDistanceDip - distance) / (MaximumDistanceDip - FullAttentionRadiusDip), 0, 1);
        fade = fade * fade * (3 - 2 * fade);
        return new(true, Direction(x, 38) * fade, -Direction(y, 32) * fade);
    }

    private static double Direction(double distance, double extent) =>
        Math.Sign(distance) * Math.Clamp((Math.Abs(distance) - 2) / extent, 0, 1);
}
