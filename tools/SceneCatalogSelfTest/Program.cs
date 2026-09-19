using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    private static string _json = "";
    private static string _applicationRoot = "";
    private static readonly SceneSelection Alternate = new("work.default", "desk.test", "computer.test");

    [STAThread]
    private static int Main(string[] args)
    {
        string repository = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRepository();
        _applicationRoot = Path.Combine(repository, "DuckDeskPet");
        _json = File.ReadAllText(Path.Combine(_applicationRoot, SceneCatalog.ResourcePath));
        var cases = new (string Name, Action Body)[]
        {
            ("Production catalog resolves its original artwork and every fire frame", ProductionCatalog),
            ("Unknown schema versions fail closed", () => Reject(d => d["version"] = 99)),
            ("Missing required properties fail closed", () => Reject(d => Scene(d).Remove("motion"))),
            ("Unknown properties cannot silently change a contract", () => Reject(d => Scene(d)["typo"] = 1)),
            ("Null scene lists fail with a useful validation error", () => Reject(d => d["scenes"] = null)),
            ("Duplicate stable IDs are rejected", () => Reject(d => ((JsonArray)d["desks"]!).Add(d["desks"]![0]!.DeepClone()))),
            ("Unknown default selection is rejected", () => Reject(d => d["defaultSelection"]!["deskId"] = "desk.missing")),
            ("Traversal, URL and absolute resource paths are rejected", UnsafePaths),
            ("A missing prop or fire frame is rejected", MissingResources),
            ("Desk back and front must be a complete distinct pair", () => Reject(d => d["desks"]![0]!["front"] = d["desks"]![0]!["back"]!.GetValue<string>())),
            ("Canvas, anchor and layer order cannot drift from the character", SceneContract),
            ("Character frame counts and phase durations stay authoritative", TimelineContract),
            ("Motion intervals, distances and bounce fractions are validated", InvalidMotion),
            ("Fire endpoint, rate and growth timing are validated", InvalidFire),
            ("Incompatible prop slots cannot resolve together", Compatibility),
            ("Unknown saved selections can be reported and safely defaulted", UnknownSelection),
            ("Configured motion is actually sampled", ConfiguredMotion),
            ("Active entry, loops and exits keep one indivisible prop set", SafeSwitch),
            ("New work entry can consume a fully prepared idle selection", EntrySwitch),
            ("Returning to active selection cancels a queued change", CancelPending),
            ("A failed preload preserves the complete active and pending sets", FailedPreload),
            ("Latest asynchronous selection wins", LatestSelectionWins),
            ("Apply never loads images and release clears fire sources", RenderPathAndRelease),
            ("Fire anchor follows image scale without adding letterboxing", FireAnchor),
        };
        int failures = 0;
        foreach (var (name, body) in cases)
        {
            try { body(); Console.WriteLine("PASS  " + name); }
            catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL  " + name + "\n" + exception); }
        }
        Console.WriteLine($"Scene catalog and renderer: {cases.Length - failures}/{cases.Length} passed; own in-memory WPF tree only.");
        return failures == 0 ? 0 : 1;
    }

    private static JsonObject Document() => JsonNode.Parse(_json)!.AsObject();
    private static JsonObject Scene(JsonObject document) => document["scenes"]![0]!.AsObject();
    private static SceneCatalog Parse(JsonObject document, Func<string, bool>? exists = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        return SceneCatalog.Parse(stream, exists ?? (_ => true));
    }
    private static void Reject(Action<JsonObject> edit)
    {
        var document = Document(); edit(document);
        Throws(() => Parse(document));
    }
    private static void Throws(Action body)
    {
        try { body(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected InvalidDataException.");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static void Near(double expected, double actual) => Check(double.IsFinite(actual) &&
        Math.Abs(expected - actual) < 1e-8, $"Expected {expected}, actual {actual}.");

    private static void ProductionCatalog()
    {
        var paths = new List<string>();
        var catalog = Parse(Document(), path => { paths.Add(path); return File.Exists(Path.Combine(_applicationRoot, path)); });
        var scene = catalog.Resolve(catalog.DefaultSelection);
        Check(scene.Desk.Back == "Assets/SceneProps/WorkV2/desk-back.png" &&
            scene.Desk.Front == "Assets/SceneProps/WorkV2/desk-front.png" &&
            scene.Computer.Image == "Assets/SceneProps/Work/laptop.png", "Default art changed.");
        Check(paths.Count >= 124 && paths.Contains("Assets/SceneProps/WorkV2/Fire/frame-0120.png"), "Catalog did not check all required frames.");
    }
    private static void UnsafePaths()
    {
        foreach (string path in new[] { "Assets/SceneProps/../private.png", "https://host/image.png", "C:/file.png",
                     "Assets\\SceneProps\\a.png", "Assets/SceneProps//a.png", "Assets/SceneProps/a.png?token=1" })
            Reject(d => d["computers"]![0]!["image"] = path);
    }
    private static void MissingResources()
    {
        foreach (string absent in new[] { "Assets/SceneProps/WorkV2/desk-front.png", "Assets/SceneProps/WorkV2/Fire/frame-0060.png" })
            Throws(() => Parse(Document(), path => path != absent));
    }
    private static void SceneContract()
    {
        Reject(d => Scene(d)["canvas"]!["width"] = 0);
        Reject(d => Scene(d)["canvas"]!["height"] = 347);
        Reject(d => Scene(d)["characterFootAnchor"]!["y"] = 330);
        Reject(d => Scene(d)["deskSurfaceAnchor"]!["y"] = 250);
        Reject(d => Scene(d)["layerOrder"]![0] = "character");
    }
    private static void TimelineContract()
    {
        Reject(d => Scene(d)["phases"]![0]!["durationSeconds"] = 2.0);
        Reject(d => Scene(d)["phases"]![0]!["frameCount"] = 210);
        Reject(d => Scene(d)["phases"]![0]!["clip"] = "Yawn");
        Reject(d => Scene(d)["phases"]![0]!["clip"] = "WorkLoop");
    }
    private static void InvalidMotion()
    {
        Reject(d => Scene(d)["motion"]!["deskTravelPixels"] = 100);
        Reject(d => Scene(d)["motion"]!["computerTravelPixels"] = "NaN");
        Reject(d => Scene(d)["motion"]!["deskEnter"]!["durationSeconds"] = 0);
        Reject(d => Scene(d)["motion"]!["computerExit"]!["startSeconds"] = 3);
        Reject(d => Scene(d)["motion"]!["dropFirstBounceEndFraction"] = 0.5);
        Reject(d => Scene(d)["motion"]!["dropLandingFraction"] = 0.99);
        Reject(d => Scene(d)["motion"]!["dropFirstBouncePixels"] = -1);
        Reject(d => Scene(d)["motion"]!["deskEnter"]!["durationSeconds"] = 3);
    }
    private static void InvalidFire()
    {
        Reject(d => Scene(d)["fire"]!["frameCount"] = 0);
        Reject(d => Scene(d)["fire"]!["loopFrameCount"] = 121);
        Reject(d => Scene(d)["fire"]!["sampleRate"] = 30);
        Reject(d => Scene(d)["fire"]!["anchor"]!["y"] = 999);
        Reject(d => Scene(d)["fire"]!["grow"]!["durationSeconds"] = 3);
    }
    private static void Compatibility()
    {
        Reject(d => d["desks"]![0]!["compatibilityId"] = "unknown-slot");
        var document = Document();
        var other = Scene(document).DeepClone(); other["id"] = "work.test"; other["compatibilityId"] = "other-slot";
        ((JsonArray)document["scenes"]!).Add(other);
        var computer = document["computers"]![0]!.DeepClone(); computer["id"] = "computer.other"; computer["compatibilityId"] = "other-slot";
        ((JsonArray)document["computers"]!).Add(computer);
        var catalog = Parse(document);
        Throws(() => catalog.Resolve(catalog.DefaultSelection with { ComputerId = "computer.other" }));
    }
    private static void UnknownSelection()
    {
        var catalog = Parse(Document());
        Check(!catalog.TryResolve(catalog.DefaultSelection with { DeskId = "desk.removed" }, out var scene, out var error) &&
            scene is null && !string.IsNullOrEmpty(error), "Unknown saved item was silently accepted.");
        Check(catalog.Resolve(catalog.DefaultSelection).Desk.Id == "desk.default", "Default unavailable.");
    }
    private static void ConfiguredMotion()
    {
        var document = Document(); Scene(document)["motion"]!["deskTravelPixels"] = 600;
        var catalog = Parse(document); var scene = catalog.Resolve(catalog.DefaultSelection).Scene;
        Near(-600, WorkStageMotion.Sample(At(ClipKind.WorkEnter, 0), scene).DeskXPixels);
        Near(0, WorkStageMotion.Sample(At(ClipKind.WorkLoop, 0.5), scene).DeskXPixels);
        Near(-600, WorkStageMotion.Sample(At(ClipKind.WorkExit, 1), scene).DeskXPixels);
    }

    private static SceneCatalog AlternateCatalog()
    {
        var document = Document();
        var desk = document["desks"]![0]!.DeepClone();
        desk["id"] = Alternate.DeskId; desk["back"] = "Assets/SceneProps/Test/back.png"; desk["front"] = "Assets/SceneProps/Test/front.png";
        ((JsonArray)document["desks"]!).Add(desk);
        var computer = document["computers"]![0]!.DeepClone(); computer["id"] = Alternate.ComputerId;
        computer["image"] = "Assets/SceneProps/Test/computer.png";
        ((JsonArray)document["computers"]!).Add(computer);
        return Parse(document);
    }
    private static Image Layer() => new() { Width = 160, Height = 174, Stretch = Stretch.Uniform };
    private static BitmapSource Pixel()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 0 }, 4);
        bitmap.Freeze(); return bitmap;
    }
    private static ClipSample At(ClipKind kind, double progress) => new(kind,
        progress == 1 ? ClipPlaybackPhase.Terminal : ClipPlaybackPhase.Playing, progress, 1);
    private static void SafeSwitch()
    {
        var back = Layer(); var front = Layer(); var computer = Layer(); var fire = Layer();
        var bitmaps = new ConcurrentDictionary<string, BitmapSource>();
        var renderer = new WorkStageRenderer(back, front, computer, fire, AlternateCatalog(), path => bitmaps.GetOrAdd(path, _ => Pixel()));
        var original = new[] { back.Source, front.Source, computer.Source };
        renderer.Apply(At(ClipKind.WorkEnter, 0));
        renderer.RequestSelectionAsync(Alternate).GetAwaiter().GetResult();
        foreach (var kind in new[] { ClipKind.WorkEnter, ClipKind.WorkLoop, ClipKind.WorkToBusy, ClipKind.BusyLoop, ClipKind.WorkExit, ClipKind.BusyExit })
            foreach (double progress in new[] { 0.0, 0.5, 1.0 })
            {
                renderer.Apply(At(kind, progress));
                Check(renderer.ActiveSelection == renderer.Catalog.DefaultSelection &&
                    original.SequenceEqual(new[] { back.Source, front.Source, computer.Source }), "Live scene hot-swapped or mixed a prop layer.");
            }
        renderer.Apply(ClipSample.Idle);
        Check(renderer.ActiveSelection == Alternate && renderer.PendingSelection is null, "Prepared selection did not commit after exit.");
        Check(ReferenceEquals(back.Source, bitmaps["Assets/SceneProps/Test/back.png"]) &&
            ReferenceEquals(front.Source, bitmaps["Assets/SceneProps/Test/front.png"]) &&
            ReferenceEquals(computer.Source, bitmaps["Assets/SceneProps/Test/computer.png"]), "Pair and computer were not committed together.");
    }
    private static void EntrySwitch()
    {
        var renderer = new WorkStageRenderer(Layer(), Layer(), Layer(), Layer(), AlternateCatalog(), _ => Pixel());
        renderer.RequestSelectionAsync(Alternate).GetAwaiter().GetResult();
        renderer.Apply(At(ClipKind.WorkEnter, 0));
        Check(renderer.ActiveSelection == Alternate, "Entry did not consume prepared selection.");
    }
    private static void CancelPending()
    {
        var renderer = new WorkStageRenderer(Layer(), Layer(), Layer(), Layer(), AlternateCatalog(), _ => Pixel());
        renderer.Apply(At(ClipKind.WorkLoop, 0.5));
        renderer.RequestSelectionAsync(Alternate).GetAwaiter().GetResult();
        renderer.RequestSelectionAsync(renderer.ActiveSelection).GetAwaiter().GetResult();
        Check(renderer.PendingSelection is null, "Reselecting equipped set did not cancel pending set.");
    }
    private static void FailedPreload()
    {
        bool fail = false;
        var renderer = new WorkStageRenderer(Layer(), Layer(), Layer(), Layer(), AlternateCatalog(), path =>
            fail && path.EndsWith("Test/front.png", StringComparison.Ordinal) ? throw new InvalidDataException("Synthetic decode failure") : Pixel());
        renderer.Apply(At(ClipKind.WorkLoop, 0.5));
        renderer.RequestSelectionAsync(Alternate with { ComputerId = "computer.default" }).GetAwaiter().GetResult();
        var pending = renderer.PendingSelection; fail = true;
        Throws(() => renderer.RequestSelectionAsync(Alternate).GetAwaiter().GetResult());
        Check(renderer.ActiveSelection == renderer.Catalog.DefaultSelection && renderer.PendingSelection == pending,
            "Decode failure partially replaced a prepared or active set.");
        Throws(() => renderer.RequestSelectionAsync(Alternate with { DeskId = "desk.missing" }).GetAwaiter().GetResult());
        Check(renderer.PendingSelection == pending, "Invalid request discarded the valid pending selection.");
    }
    private static void LatestSelectionWins()
    {
        using var started = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        int blockOnce = 0;
        var renderer = new WorkStageRenderer(Layer(), Layer(), Layer(), Layer(), AlternateCatalog(), path =>
        {
            if (path.EndsWith("Test/back.png", StringComparison.Ordinal) && Interlocked.Increment(ref blockOnce) == 1)
            { started.Set(); if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test preload was not resumed."); }
            return Pixel();
        });
        var first = renderer.RequestSelectionAsync(Alternate);
        Check(started.Wait(TimeSpan.FromSeconds(5)), "Test preload did not start.");
        var latest = Alternate with { ComputerId = "computer.default" };
        var second = renderer.RequestSelectionAsync(latest); resume.Set();
        Task.WhenAll(first, second).GetAwaiter().GetResult(); renderer.Apply(ClipSample.Idle);
        Check(renderer.ActiveSelection == latest, "Older asynchronous completion overwrote the latest request.");
    }
    private static void RenderPathAndRelease()
    {
        int loads = 0; var fire = Layer();
        var renderer = new WorkStageRenderer(Layer(), Layer(), Layer(), fire, AlternateCatalog(), _ => { Interlocked.Increment(ref loads); return Pixel(); });
        renderer.WarmFireAsync().GetAwaiter().GetResult();
        renderer.RequestSelectionAsync(Alternate).GetAwaiter().GetResult();
        renderer.Apply(ClipSample.Idle); int preparedLoads = loads;
        foreach (var kind in new[] { ClipKind.WorkEnter, ClipKind.WorkLoop, ClipKind.WorkToBusy, ClipKind.BusyLoop, ClipKind.BusyExit })
            for (int i = 0; i <= 120; i++) renderer.Apply(At(kind, i / 120.0), 1.0 / 60);
        Check(loads == preparedLoads, "A render frame loaded a bitmap.");
        renderer.ReleaseFireFrames(); Check(fire.Source is null, "Release retained effect image.");
        renderer.Apply(At(ClipKind.BusyLoop, 0.5)); Check(fire.Visibility == Visibility.Collapsed, "Released fire reappeared without preload.");
        renderer.WarmFireAsync().GetAwaiter().GetResult(); renderer.Apply(At(ClipKind.BusyLoop, 0.6));
        Check(fire.Visibility == Visibility.Visible && fire.Source is not null, "Fire could not reload for another work session.");
    }
    private static void FireAnchor()
    {
        var fire = Layer(); var renderer = new WorkStageRenderer(Layer(), Layer(), Layer(), fire, AlternateCatalog(), _ => Pixel());
        renderer.Apply(At(ClipKind.WorkToBusy, 0.5));
        var transform = (ScaleTransform)fire.RenderTransform;
        Near(80, transform.CenterX); Near(334 * 160.0 / 384, transform.CenterY);
        fire.Width = 120; renderer.Apply(At(ClipKind.WorkToBusy, 0.6));
        Near(60, transform.CenterX); Near(334 * 120.0 / 384, transform.CenterY);
    }
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DuckDeskPet", SceneCatalog.ResourcePath))) return directory.FullName;
        throw new DirectoryNotFoundException("Pass the EagleDeskPet repository root as the first argument.");
    }
}
