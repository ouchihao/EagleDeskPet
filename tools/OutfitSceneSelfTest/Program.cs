using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static partial class Program
{
    private static readonly ClipKind[] WorkClips = [ClipKind.WorkEnter, ClipKind.WorkLoop, ClipKind.WorkToBusy, ClipKind.BusyLoop, ClipKind.WorkExit, ClipKind.BusyExit];
    private static readonly ClipKind[] HungryClips = [ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit];
    private static readonly double[] Progresses = [0, .25, .5, .75, 1];
    private static readonly List<string> Failures = [], Sheets = [];
    private static readonly Dictionary<string, BitmapSource> CharacterSamples = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LayeredOutfitPack> CostumeLayers = new(StringComparer.Ordinal);
    private static readonly List<object> OcclusionMeasurements = [], ComputerMeasurements = [], FootMeasurements = [];
    private static FileResources _resources = null!;
    private static SceneCatalog _catalog = null!;
    private static string _output = "";
    private static int _renders;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] is not ("--motion" or "--motion-first" or "--motion-last" or "--occlusion-only")))
        { Console.Error.WriteLine("Usage: OutfitSceneSelfTest <repository root> <QA output under .codex-build> [--motion|--motion-first|--motion-last|--occlusion-only]"); return 2; }
        bool occlusionOnly = args.Length == 3 && args[2] == "--occlusion-only";
        string repository = Path.GetFullPath(args[0]); _output = Path.GetFullPath(args[1]);
        string qaRoot = Path.Combine(repository, ".codex-build") + Path.DirectorySeparatorChar;
        if (!_output.StartsWith(qaRoot, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("QA output must be inside the repository's .codex-build directory."); return 2; }
        Directory.CreateDirectory(_output); _resources = new(Path.Combine(repository, "DuckDeskPet"));
        using (var stream = _resources.Open(SceneCatalog.ResourcePath)!) _catalog = SceneCatalog.Parse(stream, _resources.Exists);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var outfits = OutfitCatalog.Load(_resources).Outfits.Select(item =>
            (Id: item.Id.Replace("outfit.", "", StringComparison.Ordinal), Assets: AnimationAssets.Load(_resources, item.AnimationManifest))).ToArray();
        foreach (var outfit in outfits)
            if (outfit.Assets.Appearance is { } recipe) CostumeLayers.Add(recipe, LayeredOutfitPack.Load(_resources, outfit.Assets));
        int workCombinations = outfits.Length * _catalog.Desks.Count * _catalog.Computers.Count;
        var overview = new List<(string Label, BitmapSource Image)>();
        try
        {
            AuditSourceOverOracle();
            if (args.Length == 3 && !occlusionOnly) return AuditAllMotion(args[2] == "--motion-last" ? outfits.TakeLast(1).ToArray() : outfits,
                args[2] is "--motion-first" or "--motion-last");
            foreach (var outfit in outfits)
                foreach (string desk in _catalog.Desks.Select(x => x.Id))
                    foreach (string computer in _catalog.Computers.Select(x => x.Id))
                    {
                        string combination = $"{outfit.Id}-{desk}-{computer}";
                        AuditWork(outfit.Assets, new("work.default", desk, computer), combination, overview, occlusionOnly);
                    }
            foreach (string desk in _catalog.Desks.Select(x => x.Id))
                AuditHungry(outfits[^1].Assets, new("work.default", desk, "computer.midnight"), outfits[^1].Id + "-" + desk + "-hungry");
            int groupSize = WorkClips.Length * _catalog.Computers.Count;
            for (int i = 0; i < overview.Count; i += groupSize)
                Contact(overview.Skip(i).Take(groupSize).ToArray(), 6, Brushes.FloralWhite,
                    $"work-combination-grid-{i / groupSize + 1:00}.png", "One outfit/desk with every computer / six scene phases", rowLabels: true);
            for (int start = 0; start < overview.Count; start += 72)
                Contact(overview.Skip(start).Take(72).ToArray(), 6, Brushes.FloralWhite, $"all-combinations-light-1.00x-overview-{start / 72 + 1:00}.png",
                    $"{workCombinations} combinations / six actual work phases at 50% / page {start / 72 + 1}", rowLabels: true);
            File.WriteAllText(Path.Combine(_output, "report.json"), JsonSerializer.Serialize(new
            {
                passed = Failures.Count == 0, failures = Failures, occlusionOnly, workCombinations, hungryCombinations = _catalog.Desks.Count,
                workPhases = WorkClips.Select(x => x.ToString()), hungryPhases = HungryClips.Select(x => x.ToString()),
                sampledProgress = Progresses, themes = new[] { "dark", "light" }, scales = new[] { 1.0, 1.28 },
                renderedComposites = _renders, distinctCharacterSamples = CharacterSamples.Count, sheets = Sheets,
                footMeasurements = FootMeasurements, occlusionMeasurements = OcclusionMeasurements, computerMeasurements = ComputerMeasurements,
                source = "Real production WorkStageRenderer/SceneCatalog and OutfitBitmap decoder; actual repository PNGs and manifests.",
                cachePolicy = "Only requested character key samples retained; production fire preload is released after each combination.",
                boundary = "Automated invariants and sampled renders only, not a claim that every animation frame is visually perfect.",
                ownWpfTreesOnly = true, desktopCapture = false, userSaveAccess = false,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Outfit/scene QA: {workCombinations} work + {_catalog.Desks.Count} hungry combinations; {_renders} composites; {CharacterSamples.Count} distinct character samples; {Sheets.Count} sheets; {Failures.Count} failures.");
            Console.WriteLine(_output); return Failures.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { CharacterSamples.Clear(); app.Shutdown(); }
    }

    private static void AuditWork(AnimationAssets assets, SceneSelection selection, string label, List<(string, BitmapSource)> overview, bool occlusionOnly = false)
    {
        var stage = new Stage(); WaitUi(stage.Renderer.RequestSelectionAsync(selection)); stage.Renderer.Apply(ClipSample.Idle);
        WaitUi(stage.Renderer.WarmFireAsync());
        try
        {
            AuditComputer(stage, label);
            if (occlusionOnly)
            {
                foreach (var kind in new[] { ClipKind.WorkLoop, ClipKind.BusyLoop })
                {
                    stage.Show(Character(assets, kind, .5), kind, .5);
                    Guard(label + "/occlusion/" + kind, () => AuditOcclusion(stage, label + "/" + kind));
                }
                return;
            }
            foreach (var (theme, brush) in Themes()) foreach (double scale in new[] { 1.0, 1.28 })
            {
                stage.SetEnvironment(scale, brush); Point? foot = null; var frames = new List<(string, BitmapSource)>();
                foreach (var kind in WorkClips) foreach (double progress in Progresses)
                {
                    stage.Show(Character(assets, kind, progress), kind, progress);
                    string sampleLabel = $"{kind} {progress:P0}";
                    Guard(label + "/" + sampleLabel, () => AssertAnchors(stage, kind, progress, ref foot));
                    BitmapSource composite = stage.Render(); _renders++; frames.Add((sampleLabel, composite));
                    if (theme == "light" && scale == 1 && progress == .5) overview.Add(($"{label}\n{kind}", composite));
                    if (theme == "dark" && scale == 1 && progress == .5 && kind is ClipKind.WorkLoop or ClipKind.BusyLoop)
                        Guard(label + "/occlusion/" + kind, () => AuditOcclusion(stage, label + "/" + kind));
                    if (kind is ClipKind.WorkToBusy or ClipKind.BusyLoop or ClipKind.BusyExit)
                        Guard(label + "/fire-occlusion/" + kind, () => AuditFireOcclusion(stage));
                }
                stage.Renderer.Apply(ClipSample.Idle);
                Guard(label + "/idle", () => Require(stage.Props.All(x => x.Visibility == Visibility.Collapsed), "Scene props remained visible after idle."));
                Contact(frames, 5, brush, $"work-{label}-{theme}-{scale:0.00}x.png", $"{label} / {theme} / native {scale:0.00}x");
            }
            Console.WriteLine("AUDITED " + label);
        }
        finally { stage.Release(); }
    }
    private static void AuditHungry(AnimationAssets assets, SceneSelection selection, string label)
    {
        var stage = new Stage(); WaitUi(stage.Renderer.RequestSelectionAsync(selection)); stage.Renderer.Apply(ClipSample.Idle);
        try
        {
            foreach (var (theme, brush) in Themes()) foreach (double scale in new[] { 1.0, 1.28 })
            {
                stage.SetEnvironment(scale, brush); Point? foot = null; var frames = new List<(string, BitmapSource)>();
                foreach (var kind in HungryClips) foreach (double progress in Progresses)
                {
                    stage.Show(Character(assets, kind, progress), kind, progress);
                    Guard(label + "/" + kind, () =>
                    {
                        Require(stage.Computer.Visibility == Visibility.Collapsed && stage.Fire.Visibility == Visibility.Collapsed, "Hungry scene retained a computer or fire.");
                        Point anchor = stage.FootAnchor; foot ??= anchor; Require((anchor - foot.Value).Length < .001, "Office hungry scene moved its root.");
                        Require(Math.Abs(((TranslateTransform)stage.Back.RenderTransform).X - ((TranslateTransform)stage.Front.RenderTransform).X) < 1e-9, "Hungry desk pair separated.");
                    });
                    var composite = stage.Render(); _renders++; frames.Add(($"{kind} {progress:P0}", composite));
                    if (theme == "dark" && scale == 1 && kind == ClipKind.HungryLoop && progress == .5)
                        Guard(label + "/occlusion", () => AuditOcclusion(stage, label));
                }
                Contact(frames, 5, brush, $"hungry-{label}-{theme}-{scale:0.00}x.png", $"{label} / {theme} / native {scale:0.00}x / NO COMPUTER");
            }
            Console.WriteLine("AUDITED " + label);
        }
        finally { stage.Release(); }
    }

    private static BitmapSource Character(AnimationAssets assets, ClipKind kind, double progress)
    {
        var action = assets.Actions.Single(x => x.Clip == kind.ToString());
        int index = (int)Math.Round(progress * (action.FrameCount - 1)); string path = $"{action.Directory}/frame-{index:0000}.png";
        if (CharacterSamples.TryGetValue(path, out var cached)) return cached;
        var layers = assets.Appearance is null ? null : CostumeLayers[assets.Appearance];
        var bitmap = RasterFramePlayer.LoadFrame(_resources, path, layers); CharacterSamples[path] = bitmap;
        Guard(path + "/foot", () =>
        {
            // Contact is the visible foot OUTLINE, not the last bright-yellow interior row.
            // This matches prepare_care_animation.measure_eat_root's alpha >= 32 policy;
            // measure original pixels so the production 320px decode cannot shift the test threshold.
            using var originalStream = _resources.Open(layers?.AnatomicalSourcePath(path) ?? path)!;
            var original = new PngBitmapDecoder(originalStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            byte[] pixels = Pixels(original); int bottom = -1, yellowFootPixels = 0;
            for (int y = (int)(original.PixelHeight * .87); y < original.PixelHeight; y++)
                for (int x = (int)(original.PixelWidth * .35); x < original.PixelWidth * .65; x++)
                {
                    int p = (y * original.PixelWidth + x) * 4;
                    if (pixels[p + 3] >= 32) bottom = y;
                    if (pixels[p + 3] > 180 && pixels[p + 2] > 175 && pixels[p + 1] > 100 && pixels[p] < 125) yellowFootPixels++;
                }
            FootMeasurements.Add(new { path, nativeFootContactY = bottom, yellowFootPixels });
            Require(yellowFootPixels >= 80 && Math.Abs(bottom - 336) <= 1.5, $"Authored foot contact drifted: y={bottom}, foot pixels={yellowFootPixels}.");
        });
        return bitmap;
    }
    private static void AssertAnchors(Stage stage, ClipKind kind, double progress, ref Point? foot)
    {
        Point anchor = stage.FootAnchor; foot ??= anchor; Require((anchor - foot.Value).Length < .001, "Actor root drifted across phases.");
        var motion = WorkStageMotion.Sample(Sample(kind, progress), stage.Renderer.CurrentScene);
        Require(Math.Abs(((TranslateTransform)stage.Back.RenderTransform).X - motion.DeskXPixels * 160 / 384) < .001, "Desk anchor does not match authored motion.");
        Require(Math.Abs(((TranslateTransform)stage.Front.RenderTransform).X - ((TranslateTransform)stage.Back.RenderTransform).X) < .001, "Desk front/back pair separated.");
        Require(Math.Abs(((TranslateTransform)stage.Computer.RenderTransform).Y - motion.LaptopYPixels * 160 / 384) < .001, "Computer landing does not use the same scene space.");
        Require(stage.Renderer.CurrentScene.CharacterFootAnchor is { X: 191.5, Y: 336 }, "Scene contract changed the character root.");
        double growth = WorkStageMotion.FireGrowth(Sample(kind, progress), stage.Renderer.CurrentScene);
        if (growth > 0)
        {
            var transform = (ScaleTransform)stage.Fire.RenderTransform;
            Require(Math.Abs(transform.CenterY - 334.0 * 160 / 384) < .001 && Math.Abs(transform.ScaleY - growth) < .001, "Fire growth moved its fixed base.");
            if (stage.Fire.Clip.Bounds.Height > 0)
                Require(Math.Abs(transform.Transform(new Point(0, stage.Fire.Clip.Bounds.Bottom)).Y - 276.0 * 160 / 384) < .001,
                    "Fire clipping line moved with the growth transform.");
        }
    }
    private static void AuditComputer(Stage stage, string label)
    {
        var bitmap = (BitmapSource)stage.Computer.Source; var bounds = AlphaBounds(bitmap);
        double width = bounds.Width * 384.0 / bitmap.PixelWidth, bottom = bounds.Bottom * 346.0 / bitmap.PixelHeight;
        ComputerMeasurements.Add(new { label, nativeVisibleWidth = width, nativeBottom = bottom });
        Guard(label + "/computer-bounds", () => Require(width is >= 85 and <= 120 && bottom is >= 255 and <= 282,
            $"Visible laptop bounds changed: width={width:F2}, bottom={bottom:F2}; inspect sparse-alpha noise/landing."));
    }
    private static void AuditFireOcclusion(Stage stage)
    {
        var layers = new[] { stage.Back, stage.Pet, stage.Front, stage.Computer, stage.Renderer.ForegroundLayer! };
        var visibility = layers.Select(x => x.Visibility).ToArray();
        Brush background = stage.Root.Background;
        try
        {
            stage.Root.Background = Brushes.Transparent;
            foreach (var layer in layers) layer.Visibility = Visibility.Hidden;
            var fire = stage.Render(); byte[] pixels = Pixels(fire);
            double cutoff = stage.Back.TransformToAncestor(stage.Root).Transform(new Point(0, 276 * 160.0 / 384)).Y;
            for (int y = (int)Math.Ceiling(cutoff + .5); y < fire.PixelHeight; y++)
            for (int x = 0; x < fire.PixelWidth; x++)
                Require(pixels[(y * fire.PixelWidth + x) * 4 + 3] == 0, "Fire base leaked below the fixed tabletop line.");
        }
        finally
        {
            for (int i = 0; i < layers.Length; i++) layers[i].Visibility = visibility[i];
            stage.Root.Background = background;
        }
    }
    private static void AuditOcclusion(Stage stage, string label)
    {
        var foreground = stage.Renderer.ForegroundLayer!;
        var foregroundVisibility = foreground.Visibility;
        var old = new[] { stage.Fire, stage.Back, stage.Pet, stage.Front, stage.Computer }.Select(x => x.Visibility).ToArray();
        Brush background = stage.Root.Background; stage.Root.Background = Brushes.Transparent;
        try
        {
            byte[] composed = Pixels(stage.Render());
            foreground.Visibility = Visibility.Hidden;
            stage.Fire.Visibility = stage.Back.Visibility = stage.Front.Visibility = stage.Computer.Visibility = Visibility.Hidden;
            byte[] actor = Pixels(stage.Render()); stage.Pet.Visibility = Visibility.Hidden; stage.Front.Visibility = Visibility.Visible;
            byte[] front = Pixels(stage.Render()); stage.Back.Visibility = Visibility.Visible;
            byte[] completeDesk = Pixels(stage.Render()); stage.Back.Visibility = stage.Front.Visibility = Visibility.Hidden;
            stage.Back.Visibility = old[1]; byte[] back = Pixels(stage.Render()); stage.Back.Visibility = Visibility.Hidden;
            stage.Fire.Visibility = old[0]; byte[] fire = Pixels(stage.Render()); stage.Fire.Visibility = Visibility.Hidden;
            foreground.Visibility = foregroundVisibility; byte[] hands = Pixels(stage.Render()); foreground.Visibility = Visibility.Hidden;
            stage.Computer.Visibility = old[4];
            byte[] computer = Pixels(stage.Render()); int overlaps = 0, surfaceOverlaps = 0, translucentOverlaps = 0, mismatches = 0;
            byte[][] semanticStack = [fire, actor, back, hands, front, computer];
            for (int p = 0; p < composed.Length; p += 4)
                if (front[p + 3] > 253 && actor[p + 3] > 230 && computer[p + 3] < 3)
                {
                    overlaps++; if (Enumerable.Range(0, 3).Any(c => Math.Abs(composed[p + c] - front[p + c]) > 2)) mismatches++;
                }
                else if (completeDesk[p + 3] > 253 && actor[p + 3] > 230 && computer[p + 3] < 3 && hands[p + 3] < 3)
                {
                    surfaceOverlaps++;
                    if (Enumerable.Range(0, 3).Any(c => Math.Abs(composed[p + c] - completeDesk[p + c]) > 2)) mismatches++;
                }
                else if (completeDesk[p + 3] > 230 && actor[p + 3] > 230 && computer[p + 3] < 3)
                {
                    // Generated metal/wood can contain alpha=253 throughout its interior.
                    // Compare its ACTUAL source-over result rather than pretending it is opaque.
                    // This retains the same 2/255 colour tolerance and independently fixes depth.
                    translucentOverlaps++;
                    if (!MatchesSemanticStack(composed, p, semanticStack)) mismatches++;
                }
            OcclusionMeasurements.Add(new { label, frontBodyOverlappingPixels = overlaps, tabletopBodyOverlappingPixels = surfaceOverlaps,
                translucentDeskBodyOverlappingPixels = translucentOverlaps, mismatchedPixels = mismatches });
            Require(overlaps + surfaceOverlaps + translucentOverlaps >= 8 && mismatches == 0,
                $"Complete desk overlap check failed: {overlaps} apron + {surfaceOverlaps} tabletop + {translucentOverlaps} alpha-composited pixels, {mismatches} leaked actor pixels.");
        }
        finally
        {
            var layers = new[] { stage.Fire, stage.Back, stage.Pet, stage.Front, stage.Computer };
            for (int i = 0; i < layers.Length; i++) layers[i].Visibility = old[i]; stage.Root.Background = background;
            foreground.Visibility = foregroundVisibility;
        }
    }

    private static bool MatchesSemanticStack(byte[] composed, int pixel, byte[][] layers)
    {
        for (int channel = 0; channel < 4; channel++)
        {
            double expected = 0;
            foreach (byte[] layer in layers)
                expected = layer[pixel + channel] + expected * (1 - layer[pixel + 3] / 255.0);
            if (Math.Abs(composed[pixel + channel] - expected) > 2) return false;
        }
        return true;
    }

    private static void AuditSourceOverOracle()
    {
        byte[] actor = [0, 0, 255, 255], translucentDesk = [253, 0, 0, 253];
        byte[][] stack = [actor, translucentDesk];
        Require(MatchesSemanticStack([253, 0, 2, 255], 0, stack), "Alpha-aware desk oracle rejected exact source-over.");
        Require(!MatchesSemanticStack(actor, 0, stack), "Alpha-aware desk oracle failed to detect body above desk.");
        Require(!MatchesSemanticStack([253, 3, 2, 255], 0, stack), "Alpha-aware desk oracle weakened the two-level colour tolerance.");
        Require(!MatchesSemanticStack([253, 0, 2, 250], 0, stack), "Alpha-aware desk oracle failed to check alpha coverage.");
    }

    private static ClipSample Sample(ClipKind kind, double progress) => new(kind, progress == 1 ? ClipPlaybackPhase.Terminal : ClipPlaybackPhase.Playing, progress, (long)kind);
    private static IEnumerable<(string, Brush)> Themes() { yield return ("dark", Brushes.Black); yield return ("light", Brushes.FloralWhite); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Guard(string name, Action action)
    { try { action(); } catch (Exception ex) { string failure = name + ": " + ex.Message; Failures.Add(failure); Console.Error.WriteLine("FAIL " + failure); } }
    private static byte[] Pixels(BitmapSource bitmap)
    { var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0); var data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; converted.CopyPixels(data, bitmap.PixelWidth * 4, 0); return data; }
    private static Rect AlphaBounds(BitmapSource bitmap)
    {
        var pixels = Pixels(bitmap); int left = bitmap.PixelWidth, top = bitmap.PixelHeight, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.PixelHeight; y++) for (int x = 0; x < bitmap.PixelWidth; x++) if (pixels[(y * bitmap.PixelWidth + x) * 4 + 3] > 8)
        { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        return right < 0 ? Rect.Empty : new Rect(left, top, right - left + 1, bottom - top + 1);
    }
    private static void WaitUi(Task task)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
            if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Scene resource preload did not complete.");
        }
        task.GetAwaiter().GetResult();
    }
    private static void Contact(IReadOnlyList<(string Label, BitmapSource Image)> frames, int columns, Brush background, string name, string heading, bool rowLabels = false)
    {
        const int cellWidth = 236, cellHeight = 264, headerHeight = 46; int rows = (int)Math.Ceiling(frames.Count / (double)columns);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(background, null, new Rect(0, 0, columns * cellWidth, rows * cellHeight + headerHeight));
            drawing.DrawText(Text(heading, 15), new Point(12, 12));
            for (int i = 0; i < frames.Count; i++)
            {
                int x = i % columns * cellWidth, y = i / columns * cellHeight + headerHeight;
                drawing.DrawText(Text(frames[i].Label, rowLabels ? 10 : 12), new Point(x + 7, y + 4));
                var image = frames[i].Image;
                drawing.DrawImage(image, new Rect(x + (cellWidth - image.PixelWidth) / 2.0, y + (rowLabels ? 46 : 28), image.PixelWidth, image.PixelHeight));
            }
        }
        var bitmap = new RenderTargetBitmap(columns * cellWidth, rows * cellHeight + headerHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(_output, name)); encoder.Save(stream); Sheets.Add(name);
    }
    private static FormattedText Text(string value, double size) => new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Gray, 1);

    private sealed class FileResources(string appRoot) : IAnimationResourceProvider
    {
        private readonly string _root = Path.GetFullPath(appRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        private string Resolve(string relative)
        {
            string full = Path.GetFullPath(Path.Combine(_root, relative));
            if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Test asset path escaped its repository root.");
            return full;
        }
        public Stream? Open(string path) => File.Exists(Resolve(path)) ? File.OpenRead(Resolve(path)) : null;
        internal bool Exists(string path) => File.Exists(Resolve(path));
        internal BitmapSource Decode(string path)
        { using var stream = Open(path) ?? throw new InvalidDataException("Missing real scene asset: " + path); return OutfitBitmap.Decode(stream); }
    }
    private sealed class Stage
    {
        internal readonly Grid Root = new() { ClipToBounds = true };
        private readonly Grid _layers = new() { Width = 160, Height = 174 };
        internal readonly Image Fire = Layer(), Back = Layer(), Pet = Layer(), Front = Layer(), Computer = Layer();
        internal readonly WorkStageRenderer Renderer;
        internal Image[] Props => [Fire, Back, Front, Computer];
        internal Point FootAnchor => Pet.TransformToAncestor(Root).Transform(new Point(191.5 * 160 / 384, 336.0 * 160 / 384));
        internal Stage()
        {
            foreach (var image in new[] { Fire, Back, Pet, Front, Computer }) _layers.Children.Add(image);
            Root.Children.Add(_layers); Renderer = new(Back, Front, Computer, Fire, _catalog, _resources.Decode, Pet); SetEnvironment(1, Brushes.Black);
        }
        internal void SetEnvironment(double scale, Brush background)
        { Root.Width = Math.Ceiling(160 * scale); Root.Height = Math.Ceiling(174 * scale); Root.Background = background; _layers.LayoutTransform = new ScaleTransform(scale, scale); Layout(); }
        internal void Show(BitmapSource actor, ClipKind kind, double progress)
        {
            Pet.Source = actor;
            // Sample the production effect clock to the requested phase time without loading intermediate actor frames.
            // Reset between independent screenshots, so identical phases compare across all outfit/prop combinations.
            Renderer.Apply(ClipSample.Idle);
            Renderer.Apply(Sample(kind, 0), 0);
            int ticks = (int)Math.Round(ClipCatalog.GetDefinition(kind).DurationSeconds * progress * 60);
            for (int tick = 1; tick <= ticks; tick++) Renderer.Apply(Sample(kind, tick / (ClipCatalog.GetDefinition(kind).DurationSeconds * 60)), 1.0 / 60);
            Renderer.Apply(Sample(kind, progress), 0); Layout();
        }
        private void Layout() { Root.Measure(new Size(Root.Width, Root.Height)); Root.Arrange(new Rect(0, 0, Root.Width, Root.Height)); Root.UpdateLayout(); }
        internal BitmapSource Render()
        { Layout(); var bitmap = new RenderTargetBitmap((int)Root.Width, (int)Root.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(Root); bitmap.Freeze(); return bitmap; }
        internal void Release() { Renderer.ReleaseFireFrames(); foreach (var image in new[] { Fire, Back, Pet, Front, Computer }) image.Source = null; }
        private static Image Layer() => new() { Width = 160, Height = 174, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    }
}
