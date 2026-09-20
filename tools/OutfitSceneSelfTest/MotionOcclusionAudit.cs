using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static partial class Program
{
    private static readonly ClipKind[] MovingClips =
        [ClipKind.WorkEnter, ClipKind.WorkExit, ClipKind.BusyExit, ClipKind.HungryEnter, ClipKind.HungryExit];

    // Every authored frame is decoded once, rendered by the production WPF layers and then released.
    // Contact pages deliberately keep native-sized pets legible rather than shrinking 211 frames into one sheet.
    private static int AuditAllMotion((string Id, AnimationAssets Assets)[] outfits, bool firstOnly)
    {
        var measurements = new List<object>();
        var maskTimings = new List<double>();
        int frames = 0, overlapFrames = 0, hiddenPixels = 0, leakedPixels = 0, stableOverlapPixels = 0;
        foreach (var outfit in firstOnly ? outfits.Take(1) : outfits)
        foreach (string desk in firstOnly ? new[] { "desk.default" } : _catalog.Desks.Select(x => x.Id))
        {
            string label = $"{outfit.Id}-{desk}";
            var stage = new Stage();
            WaitUi(stage.Renderer.RequestSelectionAsync(new("work.default", desk, "computer.default")));
            stage.Renderer.Apply(ClipSample.Idle);
            try
            {
                foreach (double scale in firstOnly ? new[] { 1.28 } : new[] { 1.0, 1.28 })
                foreach (var (theme, brush) in firstOnly ? Themes().Take(1) : Themes())
                {
                    stage.SetEnvironment(scale, brush);
                    foreach (var kind in MovingClips)
                    {
                        var action = outfit.Assets.Actions.Single(x => x.Clip == kind.ToString());
                        var contacts = new List<(string, BitmapSource)>();
                        Point? root = null;
                        int phaseLeaks = 0, phaseOverlap = 0;
                        for (int i = 0; i < action.FrameCount; i++)
                        {
                            double progress = i / (double)(action.FrameCount - 1);
                            string path = $"{action.Directory}/frame-{i:0000}.png";
                            var layers = outfit.Assets.Appearance is null ? null : CostumeLayers[outfit.Assets.Appearance];
                            var actor = RasterFramePlayer.LoadFrame(_resources, path, layers);
                            stage.Pet.Source = actor;
                            long started = System.Diagnostics.Stopwatch.GetTimestamp();
                            stage.Renderer.Apply(Sample(kind, progress), 1.0 / 60);
                            maskTimings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                            var rendered = stage.Render(); frames++;
                            root ??= stage.FootAnchor;
                            Require((stage.FootAnchor - root.Value).Length < .001, "Actor root changed in full-frame audit.");
                            Require(Math.Abs(((TranslateTransform)stage.Back.RenderTransform).X - ((TranslateTransform)stage.Front.RenderTransform).X) < 1e-9, "Desk halves separated.");
                            Require(Panel.GetZIndex(stage.Back) == 1 && Panel.GetZIndex(stage.Pet) == 0 &&
                                Panel.GetZIndex(stage.Renderer.ForegroundLayer!) == 2, "Scene depth changed at a motion boundary.");
                            bool isMoving = Math.Abs(((TranslateTransform)stage.Back.RenderTransform).X) > 1e-7;
                            if (isMoving)
                            {
                                int overlap, leaks;
                                try { (overlap, leaks) = MovingTablePixels(stage, foreground: true, kind); }
                                catch (Exception ex) { throw new InvalidOperationException($"{label}/{theme}/{scale}/{kind}/frame-{i:0000}: {ex.Message}", ex); }
                                if (overlap > 0) overlapFrames++;
                                hiddenPixels += overlap; leakedPixels += leaks; phaseOverlap += overlap; phaseLeaks += leaks;
                            }
                            if (ClipCatalog.IsHungryScene(kind)) Require(stage.Computer.Visibility == Visibility.Collapsed, "Computer appeared during hunger scene.");
                            // The full sequence is retained as legible, twenty-frame contact pages in both backgrounds at 1.28x.
                            if (scale == 1.28)
                            {
                                contacts.Add(($"{kind} f{i:000} / {progress:P0}", rendered));
                                if (contacts.Count == 20 || i == action.FrameCount - 1)
                                {
                                    int page = i / 20 + 1;
                                    Contact(contacts, 5, brush, $"motion-{label}-{theme}-{kind}-{page:00}.png", $"{label} / {kind} / {theme} / full 60Hz sequence / page {page}");
                                    contacts.Clear();
                                }
                            }
                            stage.Pet.Source = null;
                        }
                        measurements.Add(new { outfit = outfit.Id, desk, kind = kind.ToString(), theme, scale, frameCount = action.FrameCount,
                            tabletopBodyOverlapPixels = phaseOverlap, leakedBodyPixels = phaseLeaks });
                        Guard($"{label}/{theme}/{scale}/{kind}", () => Require(phaseLeaks == 0,
                            $"Moving tabletop: {phaseOverlap} overlapped pixels, {phaseLeaks} leaked body pixels."));
                    }
                    // Seated hands/bowl must still render on top of the tabletop after it has stopped.
                    foreach (var kind in new[] { ClipKind.WorkLoop, ClipKind.HungryLoop })
                    {
                        stage.Show(Character(outfit.Assets, kind, .5), kind, .5);
                        var (overlap, leaks) = MovingTablePixels(stage, foreground: false, kind);
                        stableOverlapPixels += overlap;
                        Guard($"{label}/{theme}/{scale}/{kind}/seated", () => Require(overlap >= 2 && leaks == 0,
                            $"Seated hands/bowl no longer cover table: {overlap} overlap, {leaks} incorrect pixels."));
                    }
                }
                Console.WriteLine("FULL-FRAME AUDITED " + label);
            }
            finally { stage.Release(); }
        }
        Require(hiddenPixels > 0 && stableOverlapPixels > 0, "Regression audit did not exercise both body and foreground pixels.");
        maskTimings.Sort();
        File.WriteAllText(Path.Combine(_output, "motion-report.json"), JsonSerializer.Serialize(new
        {
            passed = Failures.Count == 0, failures = Failures, renderedAuthoredFrames = frames,
            overlappingFrames = overlapFrames, tabletopBodyOverlapPixels = hiddenPixels, leakedBodyPixels = leakedPixels,
            seatedHandsAndBowlOverlapPixels = stableOverlapPixels, phases = measurements, sheets = Sheets,
            coldFrameApplyMilliseconds = new { median = maskTimings[maskTimings.Count / 2], p95 = maskTimings[(int)(maskTimings.Count * .95)], maximum = maskTimings[^1] },
            source = "Actual frame PNGs and production WPF WorkStageRenderer; no mock composition or screenshots of user windows.",
            cachePolicy = "Only one moving character frame and twenty rendered contact samples are retained at a time.",
            boundary = "Full authored-frame tabletop occlusion audit; does not claim every aesthetic detail is perfect.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Motion QA: {frames} actual frames, {overlapFrames} with tabletop/body overlap, {leakedPixels} leaked pixels, {Sheets.Count} contact pages, {Failures.Count} failed phase checks.");
        Console.WriteLine(_output);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static (int Overlap, int Leaks) MovingTablePixels(Stage stage, bool foreground, ClipKind kind)
    {
        var layers = new[] { stage.Fire, stage.Back, stage.Pet, stage.Front, stage.Computer, stage.Renderer.ForegroundLayer! };
        var old = layers.Select(x => x.Visibility).ToArray();
        Brush background = stage.Root.Background; stage.Root.Background = Brushes.Transparent;
        try
        {
            // Isolate the seated-hand contract from the laptop, which can legitimately cover
            // all but one fully opaque paw pixel in small outfits at 1x. The ordinary contact
            // render still includes the real laptop; this diagnostic restores it in finally.
            if (!foreground) stage.Computer.Visibility = Visibility.Hidden;
            byte[] composed = Pixels(stage.Render());
            foreach (var layer in layers) layer.Visibility = Visibility.Hidden;
            stage.Pet.Visibility = Visibility.Visible; byte[] actor = Pixels(stage.Render());
            stage.Pet.Visibility = Visibility.Hidden; stage.Back.Visibility = Visibility.Visible; byte[] back = Pixels(stage.Render());
            stage.Back.Visibility = Visibility.Hidden; stage.Front.Visibility = Visibility.Visible; byte[] front = Pixels(stage.Render());
            stage.Front.Visibility = Visibility.Hidden; stage.Computer.Visibility = foreground ? old[4] : Visibility.Hidden; byte[] computer = Pixels(stage.Render());
            stage.Computer.Visibility = Visibility.Hidden; stage.Renderer.ForegroundLayer!.Visibility = old[5]; byte[] hands = Pixels(stage.Render());
            // Independent semantic probes do NOT use the implementation's hand/bowl mask as expected data.
            // A known central torso strip is always behind the desk in work, regardless of white shirt/cream logo.
            // Actual light bowl interior pixels must retain the original actor pixels in hunger scenes.
            var transform = stage.Pet.TransformToAncestor(stage.Root);
            const double imageScale = 160.0 / 384;
            Rect torso = transform.TransformBounds(new Rect(184 * imageScale, 250 * imageScale, 17 * imageScale, 24 * imageScale));
            Rect bowl = transform.TransformBounds(new Rect(154 * imageScale, 250 * imageScale, 77 * imageScale, 20 * imageScale));
            int renderWidth = (int)stage.Root.Width;
            for (int p = 0; p < composed.Length; p += 4)
            {
                if (back[p + 3] <= 253 || actor[p + 3] <= 253 || front[p + 3] >= 3 || computer[p + 3] >= 3) continue;
                var point = new Point(p / 4 % renderWidth + .5, p / 4 / renderWidth + .5);
                bool workBody = ClipCatalog.IsWorkScene(kind) && torso.Contains(point);
                bool heldBowl = ClipCatalog.IsHungryScene(kind) && bowl.Contains(point) && actor[p] > 155 && actor[p + 1] > 175 && actor[p + 2] > 185;
                if (!workBody && !heldBowl) continue;
                var expected = workBody ? back : actor;
                Require(Math.Abs(composed[p] - expected[p]) <= 2 && Math.Abs(composed[p + 1] - expected[p + 1]) <= 2 && Math.Abs(composed[p + 2] - expected[p + 2]) <= 2,
                    $"Independent {(workBody ? "central torso" : "held bowl")} pixel failed in {kind} at {point}; actor {actor[p]}/{actor[p + 1]}/{actor[p + 2]}, actual {composed[p]}/{composed[p + 1]}/{composed[p + 2]}.");
            }
            int overlap = 0, leaks = 0;
            for (int p = 0; p < composed.Length; p += 4)
            {
                // A fully opaque tabletop pixel inside the body, outside the apron and laptop,
                // is a direct regression detector for the old back-table-through-body layering bug.
                if (back[p + 3] <= 253 || actor[p + 3] <= 253 || front[p + 3] >= 3 || computer[p + 3] >= 3 ||
                    (foreground ? hands[p + 3] >= 3 : hands[p + 3] <= 253)) continue;
                overlap++;
                var expected = foreground ? back : actor;
                if (Math.Abs(composed[p] - expected[p]) > 2 || Math.Abs(composed[p + 1] - expected[p + 1]) > 2 || Math.Abs(composed[p + 2] - expected[p + 2]) > 2) leaks++;
            }
            return (overlap, leaks);
        }
        finally
        {
            for (int i = 0; i < layers.Length; i++) layers[i].Visibility = old[i];
            stage.Root.Background = background;
        }
    }
}
