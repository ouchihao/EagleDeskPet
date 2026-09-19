using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>Only props move in scene space; the eagle's authored body is never translated as a transition.</summary>
internal readonly record struct WorkStageMotion(bool Visible, double DeskXPixels, double LaptopYPixels)
{
    internal static WorkStageMotion Sample(ClipSample clip, SceneDefinition scene)
    {
        if (!ClipCatalog.IsWorkScene(clip.Kind)) return new(false, 0, 0);
        var motion = scene.Motion;
        double seconds = clip.ElapsedSeconds;
        if (clip.Kind == ClipKind.WorkEnter)
        {
            double table = SampleInterval(seconds, motion.DeskEnter);
            double drop = SampleInterval(seconds, motion.ComputerEnter);
            return new(true, -motion.DeskTravelPixels * Math.Pow(1 - table, 3), DropOffset(drop, motion));
        }
        if (clip.Kind is ClipKind.WorkExit or ClipKind.BusyExit)
        {
            double lift = SampleInterval(seconds, motion.ComputerExit);
            double table = SampleInterval(seconds, motion.DeskExit);
            return new(true, -motion.DeskTravelPixels * table * table * table,
                -motion.ComputerTravelPixels * lift * lift * lift);
        }
        return new(true, 0, 0);
    }

    private static double DropOffset(double t, SceneMotionDefinition motion)
    {
        double landing = motion.DropLandingFraction;
        double bounceEnd = motion.DropFirstBounceEndFraction;
        if (t < landing) return -motion.ComputerTravelPixels * (1 - Math.Pow(t / landing, 2));
        if (t < bounceEnd) return -motion.DropFirstBouncePixels * Math.Sin(Math.PI * (t - landing) / (bounceEnd - landing));
        return -motion.DropSecondBouncePixels * Math.Sin(Math.PI * (t - bounceEnd) / (1 - bounceEnd));
    }
    private static double Unit(double value) => Math.Clamp(value, 0, 1);
    private static double SampleInterval(double seconds, SceneInterval interval) =>
        Unit((seconds - interval.StartSeconds) / interval.DurationSeconds);

    internal static double FireGrowth(ClipSample clip, SceneDefinition scene)
    {
        if (clip.Kind == ClipKind.WorkToBusy) return Smooth(SampleInterval(clip.ElapsedSeconds, scene.Fire.Grow));
        if (clip.Kind == ClipKind.BusyLoop) return 1;
        if (clip.Kind == ClipKind.BusyExit) return 1 - Smooth(SampleInterval(clip.ElapsedSeconds, scene.Fire.Shrink));
        return 0;
    }
    private static double Smooth(double value) => value * value * (3 - 2 * value);
}
