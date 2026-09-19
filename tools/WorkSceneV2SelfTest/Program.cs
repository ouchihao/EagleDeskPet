using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : "work-scene-v2-output");
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var dark = new SolidColorBrush(Color.FromRgb(24, 24, 24));
        var stage = new Grid { Width = 160, Height = 174, Background = dark, ClipToBounds = true };
        var fire = Layer(); var back = Layer(); var eagle = Layer(); var front = Layer(); var laptop = Layer();
        foreach (var image in new[] { fire, back, eagle, front, laptop }) stage.Children.Add(image);
        var renderer = new WorkStageRenderer(back, front, laptop, fire);
        renderer.WarmFireAsync().GetAwaiter().GetResult();
        var assertions = new List<string>();
        var selected = new List<(string Label, BitmapSource Frame)>();
        var transitionFrames = new List<BitmapFrame>();
        try
        {
            int sequence = 0;
            foreach (var kind in new[] { ClipKind.WorkEnter, ClipKind.WorkLoop, ClipKind.WorkToBusy, ClipKind.BusyLoop, ClipKind.BusyExit })
            {
                sequence++;
                int frameCount = (int)Math.Round(ClipCatalog.GetDefinition(kind).DurationSeconds * 60) + 1;
                foreach (int frame in Enumerable.Range(0, frameCount))
                {
                    double progress = frame / (double)(frameCount - 1);
                    var sample = new ClipSample(kind, frame == frameCount - 1 ? ClipPlaybackPhase.Terminal : ClipPlaybackPhase.Playing, progress, sequence);
                    string directory = kind switch { ClipKind.WorkToBusy => "WorkToBusyV2", ClipKind.BusyExit => "BusyExitV2", _ => kind.ToString() };
                    eagle.Source = RasterFramePlayer.LoadBitmap($"Assets/Animations/{directory}/frame-{frame:0000}.png");
                    renderer.Apply(sample, 1.0 / 60);
                    Layout(stage);
                    if (WorkStageMotion.FireGrowth(sample, renderer.CurrentScene) > 0)
                    {
                        var authoredBase = new Point(80, 334 * (160.0 / 384));
                        double baseY = fire.TransformToAncestor(stage).Transform(authoredBase).Y;
                        double expectedY = back.TransformToAncestor(stage).Transform(authoredBase).Y;
                        Check(Math.Abs(baseY - expectedY) < 0.01,
                            "fire growth keeps the image-local baseline fixed (no double letterbox red rug)", once: true);
                    }
                    var rendered = Render(stage);
                    if (frame % 3 == 0 && frame < frameCount - 1)
                    {
                        var metadata = new BitmapMetadata("gif");
                        metadata.SetQuery("/grctlext/Delay", (ushort)5);
                        metadata.SetQuery("/grctlext/Disposal", (byte)2);
                        transitionFrames.Add(BitmapFrame.Create(rendered, null, metadata, null));
                    }
                    if (kind is ClipKind.WorkToBusy or ClipKind.BusyExit && (frame % 15 == 0 || frame == frameCount - 1))
                    {
                        string label = $"{kind} {frame / 60.0:0.00}s";
                        selected.Add((label, rendered));
                        Save(rendered, $"{kind}-{frame:0000}.png");
                    }
                    if (kind is ClipKind.WorkLoop or ClipKind.BusyLoop && frame == 30)
                        Save(rendered, kind + "-actual.png");
                    if (kind is ClipKind.WorkLoop or ClipKind.BusyLoop)
                    {
                        var desk = (TranslateTransform)front.RenderTransform;
                        Check(Math.Abs(desk.X) < 1e-9 && Math.Abs(((TranslateTransform)laptop.RenderTransform).Y) < 1e-9, "props remain anchored in persistent loops", once: true);
                    }
                }
            }
            eagle.Source = RasterFramePlayer.LoadBitmap("Assets/mascot-animated-neutral.png");
            renderer.Apply(ClipSample.Idle);
            Check(new[] { fire, back, front, laptop }.All(x => x.Visibility == Visibility.Collapsed), "all scene layers disappear after natural exit");
            renderer.ReleaseFireFrames();
            Check(fire.Source is null, "fire memory releases without retaining the last effect image");
            SaveContact(selected.Take(7).ToArray(), "WorkToBusyV2-runtime-contact.png");
            SaveContact(selected.Skip(7).ToArray(), "BusyExitV2-runtime-contact.png");
            var gif = new GifBitmapEncoder();
            foreach (var frame in transitionFrames) gif.Frames.Add(frame);
            using (var stream = File.Create(Path.Combine(output, "work-scene-v2-runtime.gif"))) gif.Save(stream);
            File.WriteAllText(Path.Combine(output, "runtime-report.json"), JsonSerializer.Serialize(new
            {
                passed = true, assertions, source = "Production WorkStageRenderer and LoadBitmap with versioned embedded art",
                snapshots = selected.Select(x => x.Label),
                ownVisualTreeOnly = true, desktopCapture = false,
                gifNote = "Raw WIC GIF may drop timing metadata. Run tools/encode_work_scene_preview.py for a verified 20 fps/50 ms preview; production sequences retain exact 60 Hz timing.",
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Work scene WPF QA PASS: {assertions.Count} assertions; {selected.Count} transition snapshots. {output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { application.Shutdown(); }

        void Check(bool condition, string name, bool once = false)
        {
            if (!condition) throw new InvalidOperationException(name);
            if (!once || !assertions.Contains(name)) assertions.Add(name);
        }
        void Save(BitmapSource bitmap, string name)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream);
        }
        void SaveContact((string Label, BitmapSource Frame)[] frames, string name)
        {
            var drawing = new DrawingVisual();
            int columns = 4, cellWidth = 320, cellHeight = 386;
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(dark, null, new Rect(0, 0, columns * cellWidth, Math.Ceiling(frames.Length / (double)columns) * cellHeight));
                for (int i = 0; i < frames.Length; i++)
                {
                    double x = i % columns * cellWidth, y = i / columns * cellHeight;
                    dc.DrawText(new FormattedText(frames[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 17, Brushes.Wheat, 1), new Point(x + 12, y + 8));
                    dc.DrawImage(frames[i].Frame, new Rect(x, y + 36, 320, 348));
                }
            }
            var bitmap = new RenderTargetBitmap(columns * cellWidth, (int)Math.Ceiling(frames.Length / (double)columns) * cellHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing); Save(bitmap, name);
        }
    }

    private static Image Layer() => new() { Width = 160, Height = 174, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private static void Layout(FrameworkElement item)
    {
        item.Measure(new Size(item.Width, item.Height)); item.Arrange(new Rect(0, 0, item.Width, item.Height)); item.UpdateLayout();
    }
    private static BitmapSource Render(FrameworkElement item)
    {
        var bitmap = new RenderTargetBitmap(320, 348, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(item); bitmap.Freeze(); return bitmap;
    }
}
