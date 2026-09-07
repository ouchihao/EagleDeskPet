using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>Only props move in scene space; the eagle's authored body is never translated as a transition.</summary>
internal readonly record struct WorkStageMotion(bool Visible, double DeskXPixels, double LaptopYPixels)
{
    internal static WorkStageMotion Sample(ClipSample clip)
    {
        if (!ClipCatalog.IsWorkScene(clip.Kind)) return new(false, 0, 0);
        double seconds = clip.ElapsedSeconds;
        if (clip.Kind == ClipKind.WorkEnter)
        {
            double table = Unit((seconds - 0.15) / 0.85);
            double drop = Unit((seconds - 0.9) / 0.9);
            return new(true, -440 * Math.Pow(1 - table, 3), DropOffset(drop));
        }
        if (clip.Kind is ClipKind.WorkExit or ClipKind.BusyExit)
        {
            double lift = Unit((seconds - 0.35) / 0.8);
            double table = Unit((seconds - 0.9) / 1.05);
            return new(true, -440 * table * table * table, -440 * lift * lift * lift);
        }
        return new(true, 0, 0);
    }

    private static double DropOffset(double t)
    {
        if (t < 0.65) return -440 * (1 - Math.Pow(t / 0.65, 2));
        if (t < 0.84) return -14 * Math.Sin(Math.PI * (t - 0.65) / 0.19);
        return -4 * Math.Sin(Math.PI * (t - 0.84) / 0.16);
    }
    private static double Unit(double value) => Math.Clamp(value, 0, 1);

    internal static double FireGrowth(ClipSample clip)
    {
        if (clip.Kind == ClipKind.WorkToBusy) return Smooth(Unit((clip.ElapsedSeconds - 0.25) / 1.05));
        if (clip.Kind == ClipKind.BusyLoop) return 1;
        if (clip.Kind == ClipKind.BusyExit) return 1 - Smooth(Unit(clip.ElapsedSeconds / 0.85));
        return 0;
    }
    private static double Smooth(double value) => value * value * (3 - 2 * value);
}
