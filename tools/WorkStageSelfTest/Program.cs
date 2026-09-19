using DuckDeskPet;
using DuckDeskPet.Core;

var cases = new (string Name, Action Body)[]
{
    ("Entry begins wholly outside the scene and ends at identity", EntryEndpoints),
    ("Desk arrival precedes the laptop landing", EntryChoreography),
    ("Work, busy bridge and busy loops keep both props anchored", AnchoredLoops),
    ("Both exit phases remove props fully before standing", ExitEndpoints),
    ("All sampled prop motion stays finite, bounded and continuous", MotionBounds),
    ("Non-work clips have no visible workplace props", NonWorkInvisible),
    ("Busy fire grows from zero to one without fading props", BusyGrowth),
    ("Busy exit shrinks fire from one to zero", ExitGrowth),
    ("Every authored phase boundary has matching prop and fire state", PhaseSeams),
    ("The motion contract cannot translate the pet or fade props", PropOnlyContract),
};
int failures = 0;
foreach (var (name, body) in cases)
{
    try { body(); Console.WriteLine("PASS  " + name); }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine("FAIL  " + name + "\n      " + exception.Message);
    }
}
Console.WriteLine(failures == 0
    ? $"All {cases.Length} WorkStage production-motion self-tests passed."
    : $"{failures} of {cases.Length} WorkStage tests failed.");
return failures == 0 ? 0 : 1;

static void EntryEndpoints()
{
    var first = Sample(At(ClipKind.WorkEnter, 0));
    var last = Sample(At(ClipKind.WorkEnter, 1));
    Check(first.Visible && last.Visible, "Entry lost its scene layer.");
    Check(first.DeskXPixels <= -384, "Desk is not fully to the left at frame zero.");
    Check(first.LaptopYPixels <= -346, "Laptop is not fully above the canvas at frame zero.");
    Near(-440, first.DeskXPixels);
    Near(-440, first.LaptopYPixels);
    Near(0, last.DeskXPixels);
    Near(0, last.LaptopYPixels);
}

static void EntryChoreography()
{
    var atZero = Sample(AtSeconds(ClipKind.WorkEnter, 0));
    var duringSlide = Sample(AtSeconds(ClipKind.WorkEnter, 0.6));
    var atDeskArrival = Sample(AtSeconds(ClipKind.WorkEnter, 1.0));
    var atLanding = Sample(AtSeconds(ClipKind.WorkEnter, 1.8));
    Check(duringSlide.DeskXPixels > atZero.DeskXPixels && duringSlide.DeskXPixels < 0,
        "Desk did not slide into place.");
    Near(-440, duringSlide.LaptopYPixels);
    Near(0, atDeskArrival.DeskXPixels);
    Check(atDeskArrival.LaptopYPixels < -300, "Laptop landed before the desk arrived.");
    Near(0, atLanding.LaptopYPixels);
    double previousDesk = -440;
    for (int i = 0; i <= 210; i++)
    {
        var motion = Sample(At(ClipKind.WorkEnter, i / 210.0));
        Check(motion.DeskXPixels >= previousDesk - 1e-9, "Entry desk reversed direction.");
        previousDesk = motion.DeskXPixels;
    }
}

static void AnchoredLoops()
{
    foreach (var kind in new[] { ClipKind.WorkLoop, ClipKind.WorkToBusy, ClipKind.BusyLoop })
    {
        for (int i = 0; i <= 960; i++)
        {
            var motion = Sample(At(kind, i / 960.0));
            Check(motion.Visible, kind + " removed the table.");
            Near(0, motion.DeskXPixels);
            Near(0, motion.LaptopYPixels);
        }
    }
}

static void ExitEndpoints()
{
    foreach (var kind in new[] { ClipKind.WorkExit, ClipKind.BusyExit })
    {
        var first = Sample(At(kind, 0));
        var last = Sample(At(kind, 1));
        Check(first.Visible && last.Visible, "Exit hid props before moving them away.");
        Near(0, first.DeskXPixels);
        Near(0, first.LaptopYPixels);
        Near(-440, last.DeskXPixels);
        Near(-440, last.LaptopYPixels);
        Check(last.DeskXPixels + 384 < 0 && last.LaptopYPixels + 346 < 0,
            "An exit prop is still inside the canvas when standing returns.");
        var previous = first;
        for (int i = 1; i <= 120; i++)
        {
            var motion = Sample(At(kind, i / 120.0));
            Check(motion.DeskXPixels <= previous.DeskXPixels + 1e-9, "Exit desk reversed direction.");
            Check(motion.LaptopYPixels <= previous.LaptopYPixels + 1e-9, "Exit laptop reversed direction.");
            previous = motion;
        }
    }
}

static void MotionBounds()
{
    foreach (var kind in Enum.GetValues<ClipKind>().Where(ClipCatalog.IsWorkScene))
    {
        int steps = (int)Math.Round(ClipCatalog.GetDefinition(kind).DurationSeconds * 240);
        var previous = Sample(At(kind, 0));
        for (int i = 0; i <= steps; i++)
        {
            var clip = At(kind, i / (double)steps);
            var motion = Sample(clip);
            Check(double.IsFinite(motion.DeskXPixels) && double.IsFinite(motion.LaptopYPixels),
                "A prop position is not finite.");
            Check(motion.DeskXPixels is >= -440.000001 and <= 0.000001, "Desk left expected scene bounds.");
            Check(motion.LaptopYPixels is >= -440.000001 and <= 0.000001, "Laptop left expected drop bounds.");
            Check(Math.Abs(motion.DeskXPixels - previous.DeskXPixels) <= 7,
                kind + " desk has a discontinuity.");
            Check(Math.Abs(motion.LaptopYPixels - previous.LaptopYPixels) <= 7,
                kind + " laptop has a discontinuity.");
            double growth = FireGrowth(clip);
            Check(double.IsFinite(growth) && growth is >= 0 and <= 1, "Fire growth left unit bounds.");
            previous = motion;
        }
    }
}

static void NonWorkInvisible()
{
    foreach (var kind in Enum.GetValues<ClipKind>().Where(kind => !ClipCatalog.IsWorkScene(kind)))
    {
        var clip = kind == ClipKind.Idle ? ClipSample.Idle : At(kind, 0.5);
        var motion = Sample(clip);
        Check(!motion.Visible, kind + " showed office props.");
        Near(0, motion.DeskXPixels);
        Near(0, motion.LaptopYPixels);
        Near(0, FireGrowth(clip));
    }
}

static void BusyGrowth()
{
    Near(0, FireGrowth(At(ClipKind.WorkToBusy, 0)));
    Near(1, FireGrowth(At(ClipKind.WorkToBusy, 1)));
    Near(0, FireGrowth(AtSeconds(ClipKind.WorkToBusy, 0.25)));
    Near(1, FireGrowth(AtSeconds(ClipKind.WorkToBusy, 1.3)));
    double previous = 0;
    for (int i = 0; i <= 360; i++)
    {
        double growth = FireGrowth(At(ClipKind.WorkToBusy, i / 360.0));
        Check(growth >= previous - 1e-10, "Fire growth reversed during the busy bridge.");
        Check(growth - previous < 0.01, "Fire popped into view.");
        previous = growth;
    }
    for (int i = 0; i <= 120; i++) Near(1, FireGrowth(At(ClipKind.BusyLoop, i / 120.0)));
}

static void ExitGrowth()
{
    Near(1, FireGrowth(At(ClipKind.BusyExit, 0)));
    Near(0, FireGrowth(At(ClipKind.BusyExit, 1)));
    Near(0, FireGrowth(AtSeconds(ClipKind.BusyExit, 0.85)));
    double previous = 1;
    for (int i = 0; i <= 480; i++)
    {
        double growth = FireGrowth(At(ClipKind.BusyExit, i / 480.0));
        Check(growth <= previous + 1e-10, "Exit fire grew again.");
        Check(previous - growth < 0.01, "Exit fire disappeared abruptly.");
        previous = growth;
    }
}

static void PhaseSeams()
{
    foreach (var (left, right) in new[]
    {
        (ClipKind.WorkEnter, ClipKind.WorkLoop), (ClipKind.WorkEnter, ClipKind.WorkToBusy),
        (ClipKind.WorkEnter, ClipKind.WorkExit), (ClipKind.WorkLoop, ClipKind.WorkLoop),
        (ClipKind.WorkLoop, ClipKind.WorkToBusy), (ClipKind.WorkLoop, ClipKind.WorkExit),
        (ClipKind.WorkToBusy, ClipKind.BusyLoop), (ClipKind.WorkToBusy, ClipKind.BusyExit),
        (ClipKind.BusyLoop, ClipKind.BusyLoop), (ClipKind.BusyLoop, ClipKind.BusyExit),
    })
    {
        var end = At(left, 1);
        var start = At(right, 0);
        var endMotion = Sample(end);
        var startMotion = Sample(start);
        Check(endMotion.Visible == startMotion.Visible, "Prop visibility changes at " + left + " -> " + right);
        Near(endMotion.DeskXPixels, startMotion.DeskXPixels);
        Near(endMotion.LaptopYPixels, startMotion.LaptopYPixels);
        Near(FireGrowth(end), FireGrowth(start));
    }
}

static void PropOnlyContract()
{
    var properties = typeof(WorkStageMotion).GetProperties().Select(property => property.Name).Order().ToArray();
    Check(properties.SequenceEqual(new[] { "DeskXPixels", "LaptopYPixels", "Visible" }),
        "Motion contract gained body translation, prop opacity, or another untested transform.");
}

static ClipSample At(ClipKind kind, double progress) => new(kind,
    progress == 1 ? ClipPlaybackPhase.Terminal : ClipPlaybackPhase.Playing, progress, 1);

static WorkStageMotion Sample(ClipSample clip) => WorkStageMotion.Sample(clip, Fixture.Scene);
static double FireGrowth(ClipSample clip) => WorkStageMotion.FireGrowth(clip, Fixture.Scene);

static ClipSample AtSeconds(ClipKind kind, double seconds) =>
    At(kind, seconds / ClipCatalog.GetDefinition(kind).DurationSeconds);

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Near(double expected, double actual, double tolerance = 1e-8)
{
    if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
        throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
}

static class Fixture
{
    internal static readonly SceneDefinition Scene = Load();
    private static SceneDefinition Load()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "work-scenes.json"));
        // Full resource existence is covered by SceneCatalogSelfTest; this suite checks production motion.
        var catalog = SceneCatalog.Parse(stream, _ => true);
        return catalog.Resolve(catalog.DefaultSelection).Scene;
    }
}
