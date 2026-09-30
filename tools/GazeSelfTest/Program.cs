using DuckDeskPet.Core;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS " + name); passed++;
}
var center = GazePolicy.Resolve(193, 147, 384, 346);
Check(center.Enabled && center.X == 0 && center.Y == 0, "Authored head center and dead zone are neutral");
Check(GazePolicy.Resolve(283, 147, 384, 346).X > 0 && GazePolicy.Resolve(103, 147, 384, 346).X < 0, "Left and right track the pointer");
Check(GazePolicy.Resolve(193, 72, 384, 346).Y > 0 && GazePolicy.Resolve(193, 222, 384, 346).Y < 0, "WPF downward y is converted to upward model y");
Check(GazePolicy.Resolve(193 + 240, 147, 384, 346) == GazeTarget.Neutral, "Far pointer is disabled");
Check(Math.Abs(GazePolicy.Resolve(193 + 239.9, 147, 384, 346).X) < 0.00001, "Distance boundary fades continuously");
Check(GazePolicy.Resolve(195, 145, 384, 346) == center, "Two-DIP jitter does not move the head");
Check(GazePolicy.Resolve(223, 147, 384, 346).X > .7 && GazePolicy.Resolve(193, 122, 384, 346).Y > .7,
    "Near-face pointer movement produces a readable target at desktop size");
foreach (ClipKind kind in Enum.GetValues<ClipKind>())
    Check(GazePolicy.Supports(kind) == (kind is ClipKind.Idle or ClipKind.WorkLoop or ClipKind.BusyLoop),
        $"Gaze action gate: {kind}");
foreach (double scale in new[] { .5, .8, 1, 1.28, 1.5, 2 })
{
    var q = GazePolicy.Resolve(193 * scale + 50, 147 * scale - 30, 384 * scale, 346 * scale);
    var reference = GazePolicy.Resolve(243, 117, 384, 346);
    Check(q.Enabled == reference.Enabled && Math.Abs(q.X - reference.X) < 1e-12 && Math.Abs(q.Y - reference.Y) < 1e-12,
        $"DIP distance is invariant after image scale {scale}");
}
Check(GazePolicy.Resolve(193 + 50, 147 + 100 - 30, 384, 546) == GazePolicy.Resolve(243, 117, 384, 346), "Contain letterboxing adjusts the head anchor");
Check(GazePolicy.Resolve(-20, 147, 384, 346).X < 0, "Pointer outside image bounds may still be nearby");
foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    Check(GazePolicy.Resolve(bad, 0, 384, 346) == GazeTarget.Neutral, "Invalid cursor cannot propagate nonfinite model values");
Check(GazePolicy.Resolve(0, 0, 0, 0) == GazeTarget.Neutral, "Unmeasured image disables gaze");
Console.WriteLine($"{passed} gaze policy assertions passed; no cursor movement or user settings changed");
