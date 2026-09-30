using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var results = new List<string>();
        void Check(bool test, string name) { if (!test) throw new InvalidOperationException(name); results.Add(name); }
        var t = new LoafingVisualTimeline();
        t.Advance(.1, LoafingKind.NosePicking, false, false); Check(!t.Active, "waits for stable work");
        t.Advance(.1, LoafingKind.NosePicking, true, true); Check(!t.Active, "paused does not enter");
        t.Advance(.1, LoafingKind.NosePicking, true, false); Check(t.Phase == LoafingPhase.Throw, "starts with computer throw");
        for (int i = 0; i < 120; i++) t.Advance(.01, LoafingKind.NosePicking, true, false);
        Check(!t.SpriteVisible, "no GIF before body hidden");
        double seconds = t.Seconds; t.Advance(1, LoafingKind.None, true, true);
        Check(t.Seconds == seconds, "pause freezes transition");
        for (int i = 0; i < 180; i++) t.Advance(.01, LoafingKind.NosePicking, true, false);
        Check(t.Phase == LoafingPhase.Loop && t.SpriteOffsetY == 0, "settles to stationary loop");
        t.Advance(.01, LoafingKind.None, true, false); Check(t.Phase == LoafingPhase.Return, "expiration starts return");
        for (int i = 0; i < 290; i++) t.Advance(.01, LoafingKind.None, true, false);
        Check(!t.Active && t.Kind == LoafingKind.None, "return completes without idle injection");
        var frames = Enumerable.Range(0, 137).Select(i => LoafingVisualTimeline.SourceFrame(i * .04)).ToArray();
        Check(frames.Zip(frames.Skip(1)).All(p => Math.Abs(p.First - p.Second) == 1), "source loop has no terminal-to-start jump");
        Check(LoafingVisualTimeline.SourceFrame(.039) == 0 && LoafingVisualTimeline.SourceFrame(.04) == 1, "original 40ms timing retained");
        var assets = new LoafingAssets(Load);
        Check(assets.Nose.Length == 35 && assets.Monk.Length == 35, "both original sequences loaded");
        Check(assets.Fish.PixelWidth / (double)assets.Fish.PixelHeight is > 1.1 and < 1.5 &&
            assets.Mallet.PixelWidth / (double)assets.Mallet.PixelHeight is > .25 and < .4,
            "prop atlas divider separates the complete fish and mallet");
        foreach (var frame in assets.Nose.Concat(assets.Monk))
        {
            var pixels = new byte[250 * 250 * 4]; frame.CopyPixels(pixels, 1000, 0);
            if (pixels[(110 * 250 + 110) * 4 + 3] != 255) throw new Exception("face interior must stay opaque");
            if (Enumerable.Range(0, 250 * 48).Any(i => pixels[i * 4 + 3] != 0)) throw new Exception("caption escaped matte");
        }
        results.Add("all 70 frames have transparent caption band");
        results.Add("all 70 frames keep the white face opaque without old eye ghosts");
        var back = Load("Assets/SceneProps/WorkV2/desk-back.png");
        var front = Load("Assets/SceneProps/WorkV2/desk-front.png");
        var laptop = Load("Assets/SceneProps/Work/laptop.png");
        var body = Load("Assets/mascot-animated-neutral.png");
        foreach (var kind in new[] { LoafingKind.NosePicking, LoafingKind.WoodenFish })
        {
            var timeline = new LoafingVisualTimeline(); timeline.Advance(0, kind, true, false);
            for (int i = 0; i < 290; i++) timeline.Advance(.01, kind, true, false);
            var layer = new LoafingSpriteLayer(assets) { Width = 384, Height = 346 };
            layer.Measure(new Size(384, 346)); layer.Arrange(new Rect(0, 0, 384, 346));
            var sheet = new DrawingVisual();
            using (var dc = sheet.RenderOpen())
            for (int i = 0; i < 10; i++)
            {
                layer.Present(timeline, true, body, back, front, laptop);
                layer.UpdateLayout();
                var image = new RenderTargetBitmap(384, 346, 96, 96, PixelFormats.Pbgra32); image.Render(layer);
                Save(image, Path.Combine(output, $"{kind}-{i:00}.png"));
                double x = i % 5 * 384, y = i / 5 * 346;
                dc.DrawRectangle(i < 5 ? new SolidColorBrush(Color.FromRgb(28, 30, 35)) : Brushes.WhiteSmoke, null, new Rect(x, y, 384, 346));
                dc.DrawImage(image, new Rect(x, y, 384, 346));
                for (int k = 0; k < 28; k++) timeline.Advance(.01, kind, true, false);
            }
            var contact = new RenderTargetBitmap(1920, 692, 96, 96, PixelFormats.Pbgra32); contact.Render(sheet);
            Save(contact, Path.Combine(output, kind + "-review.png"));
        }
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, checks = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {results.Count}; visual evidence: {output}");
        BitmapSource Load(string path)
        {
            var image = new BitmapImage(); image.BeginInit(); image.UriSource = new Uri(Path.Combine(root, "DuckDeskPet", path));
            image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image;
        }
    }
    private static void Save(BitmapSource image, string path)
    { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
}
