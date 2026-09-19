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

internal static class Program
{
    private static readonly List<string> Assertions = [];
    private static readonly List<string> Failures = [];
    private static string _output = "";
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: ExpansionIntegrationSelfTest <new QA output directory>"); return 2; }
        _output = Path.GetFullPath(args[0]); Directory.CreateDirectory(_output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        try
        {
            Test("Hungry segments use complete 60 Hz resource sequences with planted feet", Frames);
            Test("Hungry layers preserve loop anchors and complete entry/exit travel", Motion);
            Test("Desk front occludes the lower body but does not swallow the held bowl", Occlusion);
            Test("Pausing a hunger vignette preserves its exact actor and prop frame", Pause);
            Test("Work and manual reactions wait for the complete hungry exit", Priority);
            Test("New workplace selections remain pending for the entire hungry scene", PendingSelection);
            Test("Missing optional prop disables only that choice and preserves the default renderer", MissingOptionalResources);
            Test("Strict build parsing rejects missing optional assets; runtime still rejects missing defaults", MissingDefaultResources);
            Test("Runtime catalog continues rejecting unsafe optional paths and unknown versions", InvalidRuntimeMetadata);
            Test("Dark/light 1x and 1.28x native WPF composites render", Screenshots);
            Test("Isolated preview plays entry, two complete loops, exit and two-second replay rest", FullPreview);
            Test("Preview pause/continue and authored finish affect only its private timeline", PreviewControls);
            Test("Unavailable requested outfit/workplace warns and previews real defaults without a save", PreviewFallback);
            Test("Closing during preview preload cancels work and releases every retained frame", ClosePreviewDuringLoad);
            File.WriteAllText(Path.Combine(_output, "report.json"), JsonSerializer.Serialize(new
            { passed = Failures.Count == 0, assertions = Assertions, failures = Failures,
              ownVisualTreeOnly = true, desktopCapture = false, userStateAccess = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Expansion WPF integration: {Assertions.Count} passed, {Failures.Count} failed. {_output}");
            return Failures.Count == 0 ? 0 : 1;
        }
        finally { application.Shutdown(); }
    }
    private static void Test(string name, Action body)
    {
        try { body(); Assertions.Add(name); Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { Failures.Add(name + ": " + ex.Message); Console.Error.WriteLine("FAIL  " + name + "\n" + ex); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static BitmapSource Frame(ClipKind clip, int index) => RasterFramePlayer.LoadBitmap($"Assets/Animations/{clip}/frame-{index:0000}.png");
    private static ClipSample Sample(ClipKind kind, double progress) => new(kind,
        progress == 1 ? ClipPlaybackPhase.Terminal : ClipPlaybackPhase.Playing, progress, (long)kind);

    private static void Frames()
    {
        foreach (var kind in new[] { ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit })
        {
            var definition = ClipCatalog.GetDefinition(kind); double minimum = double.MaxValue, maximum = double.MinValue;
            Check(definition.FrameCount == definition.DurationSeconds * 60 + 1, "The inclusive 60 Hz contract is broken.");
            for (int frame = 0; frame < definition.FrameCount; frame++)
            {
                var bitmap = Frame(kind, frame); byte[] pixels = Pixels(bitmap);
                int footY = -1;
                for (int y = (int)(bitmap.PixelHeight * .87); y < bitmap.PixelHeight; y++)
                    for (int x = (int)(bitmap.PixelWidth * .35); x < bitmap.PixelWidth * .65; x++)
                    {
                        int p = (y * bitmap.PixelWidth + x) * 4;
                        if (pixels[p + 3] > 180 && pixels[p + 2] > 175 && pixels[p + 1] > 100 && pixels[p] < 125) footY = y;
                    }
                Check(footY >= 0, $"Foot contact missing: {kind}/{frame}");
                double nativeY = (footY + 1) * 346.0 / bitmap.PixelHeight;
                minimum = Math.Min(minimum, nativeY); maximum = Math.Max(maximum, nativeY);
            }
            Check(maximum - minimum <= 2.5 && Math.Abs((maximum + minimum) / 2 - 336) <= 2.5,
                $"{kind} feet drift: {minimum:F2}..{maximum:F2}");
        }
        Check(Equal(Frame(ClipKind.HungryEnter, 90), Frame(ClipKind.HungryLoop, 0)), "Entry does not meet the loop's first frame.");
        Check(Equal(Frame(ClipKind.HungryLoop, 120), Frame(ClipKind.HungryExit, 0)), "Loop does not meet the exit's first frame.");
        Check(Equal(Frame(ClipKind.HungryExit, 90), RasterFramePlayer.LoadBitmap("Assets/mascot-animated-neutral.png")), "Exit does not finish on neutral.");
    }
    private static void Motion()
    {
        foreach (double scale in new[] { 1.0, 1.28 })
        {
            var stage = new Stage(scale, Brushes.Transparent); Point? anchor = null;
            foreach (var kind in new[] { ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit })
            {
                double previousX = kind == ClipKind.HungryExit ? 1 : double.MinValue;
                for (int f = 0; f < ClipCatalog.GetDefinition(kind).FrameCount; f++)
                {
                    stage.Show(kind, f); var sample = Sample(kind, ClipCatalog.GetProgressAtFrame(kind, f));
                    double x = ((TranslateTransform)stage.Front.RenderTransform).X;
                    Check(stage.Back.Visibility == Visibility.Visible && stage.Front.Visibility == Visibility.Visible, "Hungry desk disappeared inside scene.");
                    Check(stage.Laptop.Visibility == Visibility.Collapsed && stage.Fire.Visibility == Visibility.Collapsed, "Hungry scene leaked work-only props.");
                    Check(Math.Abs(x - ((TranslateTransform)stage.Back.RenderTransform).X) < 1e-9, "Desk pair split apart.");
                    if (kind == ClipKind.HungryEnter) Check(x >= previousX, "Desk reversed during entry.");
                    if (kind == ClipKind.HungryLoop) Check(Math.Abs(x) < 1e-9, "Desk drifted during loop.");
                    if (kind == ClipKind.HungryExit) Check(x <= previousX, "Desk reversed during exit.");
                    previousX = x;
                    Point actual = stage.Pet.TransformToAncestor(stage.Root).Transform(new Point(191.5 * 160 / 384, 336.0 * 160 / 384));
                    anchor ??= actual; Check((actual - anchor.Value).Length < .001, "Renderer moved the whole eagle while moving props.");
                }
            }
            Check(((TranslateTransform)stage.Front.RenderTransform).X < -160, "Exit leaves table pixels inside the stage.");
            stage.Renderer.Apply(ClipSample.Idle);
            Check(new[] { stage.Back, stage.Front, stage.Laptop, stage.Fire }.All(x => x.Visibility == Visibility.Collapsed), "Scene props survive the completed exit.");
        }
    }
    private static void Occlusion()
    {
        var stage = new Stage(1, Brushes.Transparent); stage.Show(ClipKind.HungryLoop, 48);
        byte[] composed = Pixels(stage.Render());
        stage.Renderer.ForegroundLayer!.Visibility = Visibility.Hidden;
        stage.Back.Visibility = stage.Front.Visibility = Visibility.Hidden; byte[] actor = Pixels(stage.Render());
        stage.Pet.Visibility = Visibility.Hidden; stage.Front.Visibility = Visibility.Visible; byte[] front = Pixels(stage.Render());
        int overlap = 0, visibleBowl = 0;
        for (int y = 0; y < 174; y++) for (int x = 0; x < 160; x++)
        {
            int p = (y * 160 + x) * 4;
            if (front[p + 3] > 253 && actor[p + 3] > 230)
            {
                overlap++; Check(Enumerable.Range(0, 3).All(c => Math.Abs(composed[p + c] - front[p + c]) <= 2), "The body was painted on top of the desk apron.");
            }
            double nativeX = x * 384.0 / 160, nativeY = (y - (174 - 346.0 * 160 / 384) / 2) * 384 / 160;
            if (nativeX is > 150 and < 235 && nativeY is > 220 and < 270 && actor[p + 3] > 240 && actor[p + 2] > 160 && actor[p + 1] > 140 && actor[p] > 100 && front[p + 3] < 5)
            { visibleBowl++; Check(Enumerable.Range(0, 3).All(c => Math.Abs(composed[p + c] - actor[p + c]) <= 2),
                $"Desk hid the held bowl at {nativeX:F1},{nativeY:F1}: BGRA actor {actor[p]}/{actor[p + 1]}/{actor[p + 2]}/{actor[p + 3]}, composed {composed[p]}/{composed[p + 1]}/{composed[p + 2]}/{composed[p + 3]}."); }
        }
        Check(overlap >= 20, $"Occlusion test lacks actual lower-body/front overlap ({overlap}).");
        Check(visibleBowl >= 10, $"Held bowl is not visible above the table ({visibleBowl}).");
    }
    private static void Pause()
    {
        var behavior = new PetBehaviorController(); behavior.SuspendAutomatic(true); behavior.RequestHungryScene();
        AdvanceUntil(behavior, x => x.Kind == ClipKind.HungryLoop && x.Progress > .3);
        var before = behavior.CurrentSample; behavior.SetPaused(true);
        for (int i = 0; i < 300; i++) Check(behavior.Advance(1.0 / 60) == before, "Pause advanced the hungry sequence.");
        behavior.CancelAutomaticEmotions(); Check(behavior.Advance(.1) == before, "Disabling emotions cut a paused scene.");
        behavior.SetPaused(false); AdvanceUntil(behavior, x => x.Kind == ClipKind.Idle);
        Check(!behavior.IsHungrySceneActive && !behavior.IsHungryRequested, "Resume did not finish cancellation cleanup.");
    }
    private static void Priority()
    {
        foreach (bool work in new[] { false, true })
        {
            var behavior = new PetBehaviorController(); behavior.SuspendAutomatic(true); behavior.RequestHungryScene();
            AdvanceUntil(behavior, x => x.Kind == ClipKind.HungryLoop && x.Progress > .2);
            if (work) behavior.SetWorkState(true, false);
            else { behavior.CancelAutomaticEmotions(); behavior.QueueReaction(PetBehaviorKind.Fed); }
            bool exit = false, terminal = false;
            for (int i = 0; i < 900; i++)
            {
                var sample = behavior.Advance(1.0 / 60);
                if (sample.Kind == ClipKind.HungryExit) { exit = true; terminal |= sample.Phase == ClipPlaybackPhase.Terminal; }
                if (sample.Kind == (work ? ClipKind.WorkEnter : ClipKind.Eat)) { Check(exit && terminal, "Interaction replaced hunger without full exit."); break; }
                Check(i != 899, "Requested next interaction never started.");
            }
        }
    }
    private static void PendingSelection()
    {
        var stage = new Stage(1, Brushes.Transparent); stage.Show(ClipKind.HungryLoop, 30);
        var original = stage.Renderer.ActiveSelection;
        // Only real registered alternative assets are exercised; missing registration is reported, not replaced by a fake bitmap.
        var next = original with { DeskId = "desk.mint", ComputerId = "computer.midnight" };
        Check(stage.Renderer.Catalog.TryResolve(next, out _, out var warning), "New scene props are not registered: " + warning);
        WaitUi(stage.Renderer.RequestSelectionAsync(next));
        stage.Show(ClipKind.HungryLoop, 90); Check(stage.Renderer.ActiveSelection == original, "Loop hot-swapped its table.");
        stage.Show(ClipKind.HungryExit, 90); Check(stage.Renderer.ActiveSelection == original, "Exit hot-swapped its table.");
        stage.Renderer.Apply(ClipSample.Idle); Check(stage.Renderer.ActiveSelection == next, "Idle did not apply complete prop pair.");
    }
    private static SceneCatalog ParseCatalog(bool runtime, Func<string, bool> exists, Action<System.Text.Json.Nodes.JsonObject>? change = null)
    {
        using var source = Application.GetResourceStream(new Uri($"pack://application:,,,/{SceneCatalog.ResourcePath}"))!.Stream;
        if (change is null) return runtime ? SceneCatalog.ParseRuntime(source, exists) : SceneCatalog.Parse(source, exists);
        var json = System.Text.Json.Nodes.JsonNode.Parse(source)!.AsObject(); change(json);
        using var modified = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        return runtime ? SceneCatalog.ParseRuntime(modified, exists) : SceneCatalog.Parse(modified, exists);
    }
    private static void MissingOptionalResources()
    {
        foreach (var (absent, selection) in new[]
        {
            ("Assets/SceneProps/Shop/desk-mint-back.png", new SceneSelection("work.default", "desk.mint", "computer.default")),
            ("Assets/SceneProps/Shop/desk-mint-front.png", new SceneSelection("work.default", "desk.mint", "computer.default")),
            ("Assets/SceneProps/Shop/computer-midnight.png", new SceneSelection("work.default", "desk.default", "computer.midnight")),
        })
        {
            int probes = 0;
            var catalog = ParseCatalog(true, path => { probes++; return path != absent; });
            Check(catalog.TryResolve(catalog.DefaultSelection, out _, out _), "An optional missing asset removed the valid fallback.");
            Check(!catalog.TryResolve(selection, out _, out var warning) && warning!.Contains(absent), "Missing choice was accepted or its failure was hidden.");
            int probesAfterLoad = probes;
            for (int i = 0; i < 200; i++) { catalog.TryResolve(selection, out _, out _); catalog.Resolve(catalog.DefaultSelection); }
            Check(probes == probesAfterLoad, "Menu/renderer resolution re-probed immutable pack resources.");
            var back = new Image { Width = 160, Height = 174 }; var front = new Image { Width = 160, Height = 174 };
            var computer = new Image { Width = 160, Height = 174 }; var fire = new Image { Width = 160, Height = 174 };
            var renderer = new WorkStageRenderer(back, front, computer, fire, catalog);
            var originals = new[] { back.Source, front.Source, computer.Source };
            bool rejected = false; try { WaitUi(renderer.RequestSelectionAsync(selection)); } catch (InvalidDataException) { rejected = true; }
            renderer.Apply(ClipSample.Idle);
            Check(rejected && renderer.ActiveSelection == catalog.DefaultSelection && renderer.PendingSelection is null &&
                originals.SequenceEqual(new[] { back.Source, front.Source, computer.Source }), "Unavailable request mutated or fabricated part of the active scene.");
        }
    }
    private static void MissingDefaultResources()
    {
        foreach (string absent in new[] { "Assets/SceneProps/WorkV2/desk-back.png", "Assets/SceneProps/WorkV2/desk-front.png",
                     "Assets/SceneProps/Work/laptop.png", "Assets/SceneProps/WorkV2/Fire/frame-0060.png" })
        {
            bool rejected = false; try { ParseCatalog(true, path => path != absent); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Missing mandatory fallback asset was silently accepted: " + absent);
        }
        bool strictRejected = false;
        try { ParseCatalog(false, path => path != "Assets/SceneProps/Shop/desk-mint-front.png"); } catch (InvalidDataException) { strictRejected = true; }
        Check(strictRejected, "Build/audit parser stopped checking optional shipped resources.");
    }
    private static void InvalidRuntimeMetadata()
    {
        foreach (var change in new Action<System.Text.Json.Nodes.JsonObject>[]
        {
            json => json["version"] = 999,
            json => json["desks"]![1]!["back"] = "Assets/SceneProps/../secret.png",
            json => json["desks"]![1]!["compatibilityId"] = "unknown-slot",
        })
        {
            bool rejected = false; try { ParseCatalog(true, _ => true, change); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Optional-resource fallback weakened catalog schema/path validation.");
        }
    }
    private static void Screenshots()
    {
        foreach (var (theme, background) in new[] { ("dark", Brushes.Black), ("light", Brushes.FloralWhite) })
            foreach (double scale in new[] { 1.0, 1.28 })
            {
                var stage = new Stage(scale, background); var frames = new List<(string, BitmapSource)>();
                foreach (var kind in new[] { ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit })
                    foreach (double p in new[] { 0.0, .25, .5, .75, 1.0 })
                    {
                        int frame = (int)Math.Round((ClipCatalog.GetDefinition(kind).FrameCount - 1) * p);
                        stage.Show(kind, frame); frames.Add(($"{kind} {p:P0}", stage.Render()));
                    }
                SaveContact(frames, background, $"hungry-{theme}-{scale:0.00}x.png");
            }
    }
    private static void AdvanceUntil(PetBehaviorController behavior, Func<ClipSample, bool> condition)
    { for (int i = 0; i < 1800; i++) if (condition(behavior.Advance(1.0 / 60))) return; throw new TimeoutException("Behavior did not reach expected boundary."); }
    private static bool Equal(BitmapSource a, BitmapSource b) => a.PixelWidth == b.PixelWidth && a.PixelHeight == b.PixelHeight && Pixels(a).SequenceEqual(Pixels(b));

    private static void FullPreview()
    {
        var chosen = new SceneSelection("work.default", "desk.mint", "computer.midnight");
        var window = new HungryScenePreviewWindow(OutfitCatalog.DefaultId, chosen);
        try
        {
            var preparation = window.PrepareAsync(); Check(ReferenceEquals(preparation, window.PrepareAsync()), "Repeated preparation started competing loaders.");
            WaitUi(preparation);
            Check(window.IsPrepared && window.EffectiveSelection == chosen && window.EffectiveOutfitId == OutfitCatalog.DefaultId, "Preview ignored selected props/outfit.");
            Check(window.RetainedFrameCount == 303, "Default preview unnecessarily decoded unrelated actions.");
            Check(!window.IsRenderLoopAttached, "Hidden preview subscribed to global rendering.");
            var phases = new List<ClipKind>(); var terminals = new List<ClipKind>(); long sequence = -1;
            int ticks = 0, idleStart = -1;
            while (!window.CanReplay && ticks < 620)
            {
                var sample = window.CurrentSample;
                if (sample.Sequence != sequence) { sequence = sample.Sequence; phases.Add(sample.Kind); }
                if (sample.Phase == ClipPlaybackPhase.Terminal) terminals.Add(sample.Kind);
                if (sample.Kind == ClipKind.Idle && idleStart < 0) idleStart = ticks;
                StepPreview(window); ticks++;
            }
            Check(phases.SequenceEqual(new[] { ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryLoop, ClipKind.HungryExit }), "Preview skipped or interleaved an authored scene segment.");
            Check(terminals.SequenceEqual(phases), "Preview failed to present each authored terminal frame.");
            Check(window.CanReplay && idleStart > 0 && ticks - idleStart >= 120, "Replay bypassed its two-second standing rest.");
            ClickPreview(window, "再看一遍"); Check(window.CurrentSample.Kind == ClipKind.HungryEnter && !window.CanReplay, "Replay did not start a new full scene.");
            for (int i = 0; i < 145; i++) StepPreview(window);
            SavePreview(window, "hungry-preview-window.png");
            Check(!typeof(HungryScenePreviewWindow).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Any(x => x.FieldType.Name is "PetStore" or "PetState" or "PetCareService" or "ContentOwnershipService"), "Preview acquired mutable gameplay services.");
        }
        finally { window.Close(); }
        Check(!window.IsRenderLoopAttached && window.RetainedFrameCount == 0 && window.EffectiveOutfitId is null &&
            PreviewDescendants<Image>((DependencyObject)window.Content).All(x => x.Source is null), "Closed preview retained frames or image references.");
    }
    private static void PreviewControls()
    {
        var window = new HungryScenePreviewWindow(OutfitCatalog.DefaultId, new("work.default", "desk.default", "computer.default"));
        try
        {
            WaitUi(window.PrepareAsync()); for (int i = 0; i < 45; i++) StepPreview(window);
            var paused = window.CurrentSample; ClickPreview(window, "暂停");
            for (int i = 0; i < 90; i++) StepPreview(window);
            Check(window.CurrentSample == paused && !window.IsRenderLoopAttached, "Pause advanced an actor or kept a render clock.");
            ClickPreview(window, "继续"); StepPreview(window); Check(window.CurrentSample.Progress > paused.Progress, "Continue did not resume the same segment.");
            var beforeFinish = window.CurrentSample; ClickPreview(window, "收场");
            Check(window.CurrentSample == beforeFinish, "Finish button replaced the current authored frame.");
            var kinds = new HashSet<ClipKind>(); bool sawEntryEnd = false, sawExitEnd = false;
            for (int i = 0; i < 360 && window.CurrentSample.Kind != ClipKind.Idle; i++)
            {
                var sample = window.CurrentSample; kinds.Add(sample.Kind);
                if (sample.Phase == ClipPlaybackPhase.Terminal) { sawEntryEnd |= sample.Kind == ClipKind.HungryEnter; sawExitEnd |= sample.Kind == ClipKind.HungryExit; }
                StepPreview(window);
            }
            Check(sawEntryEnd && sawExitEnd && !kinds.Contains(ClipKind.HungryLoop) && window.CurrentSample.Kind == ClipKind.Idle,
                "Finish did not complete entry then exit naturally.");
        }
        finally { window.Close(); }
    }
    private static void PreviewFallback()
    {
        var window = new HungryScenePreviewWindow("outfit.missing", new("work.default", "desk.missing", "computer.default"));
        try
        {
            WaitUi(window.PrepareAsync());
            Check(window.IsPrepared && window.EffectiveOutfitId == OutfitCatalog.DefaultId && window.EffectiveSelection!.DeskId == "desk.default" &&
                window.WarningText.Contains("原味大头鹰") && window.WarningText.Contains("经典木桌") && window.WarningText.Contains("装备不会改变"),
                "Fallback was silent, incomplete, or presented as an equipment change.");
        }
        finally { window.Close(); }
    }
    private static void ClosePreviewDuringLoad()
    {
        var window = new HungryScenePreviewWindow(OutfitCatalog.DefaultId, new("work.default", "desk.default", "computer.default"));
        var preparation = window.PrepareAsync(); window.Close(); WaitUi(preparation); WaitUi(window.PrepareAsync());
        Check(!window.IsPrepared && !window.IsRenderLoopAttached && window.RetainedFrameCount == 0 &&
            PreviewDescendants<Image>((DependencyObject)window.Content).All(x => x.Source is null), "A completed stale preload resurrected a closed preview.");
    }
    private static void StepPreview(HungryScenePreviewWindow window) => typeof(HungryScenePreviewWindow)
        .GetMethod("AdvanceTimeline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, new object[] { 1.0 / 60 });
    private static void ClickPreview(HungryScenePreviewWindow window, string text)
    {
        LayoutPreview(window);
        var button = PreviewDescendants<Button>((DependencyObject)window.Content).Single(x => (string?)x.Content == text);
        Check(button.IsEnabled, "Expected enabled preview control: " + text); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void LayoutPreview(Window window)
    {
        var surface = (FrameworkElement)window.Content; surface.Measure(new Size(390, 425));
        surface.Arrange(new Rect(0, 0, 390, 425)); surface.UpdateLayout();
    }
    private static IEnumerable<T> PreviewDescendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i); if (child is T found) yield return found;
            foreach (var descendant in PreviewDescendants<T>(child)) yield return descendant;
        }
    }
    private static void SavePreview(Window window, string name)
    {
        LayoutPreview(window); var surface = (FrameworkElement)window.Content; var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(_output, name)); encoder.Save(file);
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; converted.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
    private static void WaitUi(Task task)
    {
        while (!task.IsCompleted) { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
        task.GetAwaiter().GetResult();
    }
    private static void SaveContact(IReadOnlyList<(string Label, BitmapSource Frame)> frames, Brush background, string name)
    {
        const int cellWidth = 230, cellHeight = 258, columns = 5; var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(background, null, new Rect(0, 0, columns * cellWidth, 3 * cellHeight));
            for (int i = 0; i < frames.Count; i++)
            {
                int x = i % columns * cellWidth, y = i / columns * cellHeight;
                context.DrawText(new FormattedText(frames[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12, Brushes.Gray, 1), new Point(x + 8, y + 8));
                // Keep exact 1x/1.28x native UI dimensions; no enlargement disguising small-scale seams.
                context.DrawImage(frames[i].Frame, new Rect(x + (cellWidth - frames[i].Frame.PixelWidth) / 2.0,
                    y + 28, frames[i].Frame.PixelWidth, frames[i].Frame.PixelHeight));
            }
        }
        var bitmap = new RenderTargetBitmap(columns * cellWidth, 3 * cellHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(_output, name)); encoder.Save(file);
    }
    private sealed class Stage
    {
        internal readonly Grid Root;
        internal readonly Image Fire = Layer(), Back = Layer(), Pet = Layer(), Front = Layer(), Laptop = Layer();
        internal readonly WorkStageRenderer Renderer;
        internal Stage(double scale, Brush background)
        {
            Root = new() { Width = Math.Ceiling(160 * scale), Height = Math.Ceiling(174 * scale), Background = background, ClipToBounds = true };
            var layers = new Grid { Width = 160, Height = 174, LayoutTransform = new ScaleTransform(scale, scale) };
            foreach (var image in new[] { Fire, Back, Pet, Front, Laptop }) layers.Children.Add(image);
            Root.Children.Add(layers); Renderer = new(Back, Front, Laptop, Fire, character: Pet);
        }
        internal void Show(ClipKind kind, int frame)
        { Pet.Source = Frame(kind, frame); Renderer.Apply(Sample(kind, ClipCatalog.GetProgressAtFrame(kind, frame)), 1.0 / 60); Layout(); }
        private void Layout() { Root.Measure(new Size(Root.Width, Root.Height)); Root.Arrange(new Rect(0, 0, Root.Width, Root.Height)); Root.UpdateLayout(); }
        internal BitmapSource Render()
        { Layout(); var bitmap = new RenderTargetBitmap((int)Root.Width, (int)Root.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(Root); bitmap.Freeze(); return bitmap; }
        private static Image Layer() => new() { Width = 160, Height = 174, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    }
}
