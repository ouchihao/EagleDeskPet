namespace DuckDeskPet.Core;

public readonly record struct GazeTarget(bool Enabled, double X, double Y)
{
    public static GazeTarget Neutral => new(false, 0, 0);
}

/// <summary>Maps a pointer already converted to image-local DIPs; never moves a window or reads screen content</summary>
public static class GazePolicy
{
    public static bool Supports(ClipKind kind) => kind is ClipKind.Idle or ClipKind.WorkLoop or ClipKind.BusyLoop;

    public static GazeTarget Resolve(double pointerX, double pointerY, double imageWidth, double imageHeight)
    {
        if (!double.IsFinite(pointerX) || !double.IsFinite(pointerY) ||
            !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight) || imageWidth <= 0 || imageHeight <= 0)
            return GazeTarget.Neutral;
        double scale = Math.Min(imageWidth / 384, imageHeight / 346);
        double x = pointerX - ((imageWidth - 384 * scale) / 2 + 193 * scale);
        double y = pointerY - ((imageHeight - 346 * scale) / 2 + 147 * scale);
        return new(true, Direction(x, 38), -Direction(y, 32));
    }

    private static double Direction(double distance, double extent) =>
        Math.Sign(distance) * Math.Clamp((Math.Abs(distance) - 2) / extent, 0, 1);
}
