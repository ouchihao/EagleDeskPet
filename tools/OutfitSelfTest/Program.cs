using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DuckDeskPet;
using DuckDeskPet.Core;

internal static class Program
{
    private static int _passed;
    private static int _failed;
    private const string Office = "outfit.office";
    private const string Other = "outfit.other";
    private const string OfficeRoot = "Assets/Outfits/Office/";

    [STAThread]
    private static int Main(string[] args)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        _ = RunAndStopAsync(args, dispatcher);
        Dispatcher.Run();
        Console.WriteLine($"OutfitSelfTest: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunAndStopAsync(string[] args, Dispatcher dispatcher)
    {
        try
        {
            await RunAsync();
            Console.WriteLine($"FIXTURE OutfitSelfTest: {_passed} passed, {_failed} failed.");
            if (args is ["--resources-root", var root])
                await TestRealResourcesAsync(root);
        }
        catch (Exception ex) { _failed++; Console.WriteLine("UNEXPECTED: " + ex); }
        finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
    }

    private static async Task TestRealResourcesAsync(string root)
    {
        var resources = new FileResources(root);
        var catalog = OutfitCatalog.Load(resources);
        var expected = new HashSet<string>([OutfitCatalog.DefaultId, Office, "outfit.hoodie"], StringComparer.Ordinal);
        Check(catalog.Warning is null && expected.SetEquals(catalog.Outfits.Select(x => x.Id)),
            "real registry contains default, office and hoodie without fallback");
        foreach (var outfit in catalog.Outfits)
        {
            // This exercises the production full-PNG validator, not WarmClipsAsync.
            // It retains only metadata, and each preceding pack's temporary WIC
            // decoders are reclaimed before the next pack begins.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int before = resources.PngReads;
            var result = await catalog.GetAvailabilityAsync(outfit.Id);
            var validated = await catalog.GetValidatedAsync(outfit.Id);
            int pngReads = resources.PngReads - before;
            Check(result.IsAvailable && result.Warning is null,
                $"real {outfit.Id} resources fully decode without warnings");
            Check(result.SupportedClips.Count == 20 && result.SupportedClips.Contains(ClipKind.Idle),
                $"real {outfit.Id} supports all 19 actions plus neutral");
            Check(validated.Assets is { Actions.Count: 19 } assets && assets.Actions.Sum(x => x.FrameCount) == 2299,
                $"real {outfit.Id} manifest declares exactly 19 clips and 2299 frames");
            Check(pngReads == 2300,
                $"real {outfit.Id} actually reads all 2299 frames and its neutral PNG");
            Console.WriteLine($"REAL {outfit.Id}: available={result.IsAvailable}; pngReads={pngReads}; warning={result.Warning ?? "none"}");
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Console.WriteLine($"REAL sequential validation peak working set: {process.PeakWorkingSet64 / (1024.0 * 1024):F1} MiB (includes preceding fixture tests).");
    }

    private static async Task RunAsync()
    {
        var fixture = new Fixture();
        var catalog = OutfitCatalog.Load(fixture);
        Check(catalog.Outfits.Count == 3 && catalog.DefaultOutfitId == OutfitCatalog.DefaultId, "registry identifies three packs");
        var defaultReady = await catalog.GetAvailabilityAsync(OutfitCatalog.DefaultId);
        Check(defaultReady.IsAvailable && defaultReady.SupportedClips.Count == 20, "default supports all 19 actions plus neutral");
        var officeReady = await catalog.GetAvailabilityAsync(Office);
        Check(officeReady.IsAvailable && officeReady.SupportedClips.Count == 20, "complete office validates actual PNGs");
        Check((await catalog.GetAvailabilityAsync(Other)).IsAvailable, "second complete replacement pack validates");
        int reads = fixture.TotalReads;
        await catalog.GetAvailabilityAsync(Office);
        Check(fixture.TotalReads == reads, "availability cached without repeated frame scanning");
        Check(!(await catalog.GetAvailabilityAsync("outfit.unknown")).IsAvailable, "unknown outfit unavailable");

        var legacy = new Fixture();
        legacy.Files.TryRemove("Assets/outfits.json", out _);
        Check(OutfitCatalog.Load(legacy).Outfits.Count == 1, "optional registry preserves old default release");
        var invalidRegistry = new Fixture();
        invalidRegistry.Put("Assets/outfits.json", "{broken");
        var fallbackCatalog = OutfitCatalog.Load(invalidRegistry);
        Check(fallbackCatalog.Outfits.Count == 1 && fallbackCatalog.Warning is not null, "bad registry defaults with explicit warning");
        var external = new Fixture();
        external.PutRegistry("https://example.com/actions.json");
        Check(OutfitCatalog.Load(external).Warning is not null, "remote registry path rejected");

        await Unavailable("missing manifest", f => f.Files.TryRemove(OfficeRoot + "actions.json", out _));
        await Unavailable("missing neutral", f => f.Files.TryRemove(OfficeRoot + "neutral.png", out _));
        await Unavailable("missing final frame", f => f.Files.TryRemove(OfficeRoot + "Animations/Annoyed/frame-0120.png", out _));
        await Unavailable("corrupted PNG", f => f.Files[OfficeRoot + "Animations/Yawn/frame-0004.png"] = [1, 2, 3]);
        await Unavailable("valid header but truncated pixel stream", f => f.Files[OfficeRoot + "Animations/Yawn/frame-0004.png"] = Fixture.Png(384, 346, Colors.Red)[..40]);
        await Unavailable("wrong canvas dimensions", f => f.Files[OfficeRoot + "neutral.png"] = Fixture.Png(383, 346, Colors.Red));
        await Unavailable("wrong frame count", f => f.ChangeOffice(a => a.Actions[0].FrameCount--));
        await Unavailable("wrong duration", f => f.ChangeOffice(a => a.Actions[0].DurationSeconds = 1.99));
        await Unavailable("wrong phase", f => f.ChangeOffice(a => a.Actions[0].Phase = "loop"));
        await Unavailable("wrong group", f => f.ChangeOffice(a => a.Actions[0].Group = "Interaction"));
        await Unavailable("missing expansion clip", f => f.ChangeOffice(a => a.Actions.RemoveAll(x => x.Clip == "Tea")));
        await Unavailable("numeric enum name", f => f.ChangeOffice(a => a.Actions[0].Clip = "3"));
        await Unavailable("duplicate clip", f => f.ChangeOffice(a => a.Actions.Add(a.Actions[0])));
        await Unavailable("cross-outfit neutral", f => f.ChangeOffice(a => a.Neutral = "Assets/mascot-animated-neutral.png"));
        await Unavailable("cross-outfit action", f => f.ChangeOffice(a => a.Actions[0].Directory = "Assets/Animations/Yawn"));
        await Unavailable("traversal directory", f => f.ChangeOffice(a => a.Actions[0].Directory = OfficeRoot + "Animations/../Yawn"));
        await Unavailable("action identifier mismatch", f => f.ChangeOffice(a => a.Actions[0].Id = "not-yawn"));

        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            Check(player.DecodedFrameCount == 0 && player.IsClipReady(ClipKind.Idle), "constructor only loads neutral");
            await player.WarmAsync();
            Check(player.DecodedFrameCount == 363 && player.IsClipReady(ClipKind.Shy) && player.IsClipReady(ClipKind.Eat), "legacy warm caches only common three");
            Check(!player.IsClipReady(ClipKind.Tea) && !player.IsClipReady(ClipKind.HungryLoop), "expansions stay lazy");
            int before = fixture.TotalReads;
            await Task.WhenAll(player.WarmClipAsync(ClipKind.Tea), player.WarmClipAsync(ClipKind.Tea));
            Check(fixture.TotalReads - before == 121 && player.IsClipReady(ClipKind.Tea), "concurrent same-clip warm deduplicates");
            await player.WarmWorkAsync();
            Check(player.IsClipReady(ClipKind.BusyExit), "legacy work warm includes exit phases");
            player.ReleaseWorkFrames();
            Check(!player.IsClipReady(ClipKind.WorkLoop) && player.IsClipReady(ClipKind.Tea) && player.DecodedFrameCount == 484,
                "work release preserves common and expansion caches");
            var selected = await player.SelectOutfitAsync(Office);
            Check(selected.Success && player.CurrentOutfitId == Office && player.DecodedFrameCount == 363,
                "switch atomically replaces cache and warms common clips");
            Check(!player.IsClipReady(ClipKind.Tea), "old outfit tea cache cannot leak into new outfit");
            var failed = await player.SelectOutfitAsync("outfit.missing");
            Check(!failed.Success && player.CurrentOutfitId == Office && player.DecodedFrameCount == 363 && player.Warning is not null,
                "failed selection preserves existing suite and cache");
            var restore = await player.RestoreOutfitAsync("outfit.missing");
            Check(!restore.UsedFallback && player.CurrentOutfitId == Office, "restore cannot silently downgrade an active nondefault suite");
            player.Apply(Sample(ClipKind.Yawn));
            Check((await player.SelectOutfitAsync(OutfitCatalog.DefaultId)).Status == OutfitSelectionStatus.UnsafeBoundary,
                "active action rejects outfit change");
            player.Apply(ClipSample.Idle);
            Check((await player.SelectOutfitAsync(OutfitCatalog.DefaultId)).Success, "standing permits default restoration");
            var fallback = await player.RestoreOutfitAsync("outfit.missing");
            Check(fallback.UsedFallback && fallback.Success && fallback.Warning!.Contains("所有权"), "startup fallback explicitly preserves saved ownership");
        }

        var image = new Image();
        using (var player = new RasterFramePlayer(image, catalog))
        {
            await player.SelectOutfitAsync(Office);
            player.Apply(Sample(ClipKind.Shy));
            var previous = image.Source;
            int before = fixture.TotalReads;
            player.Apply(Sample(ClipKind.Tea));
            Check(ReferenceEquals(previous, image.Source) && fixture.TotalReads == before && player.Warning is not null,
                "missing warm holds same-suite previous frame with zero render-path IO");
            int cached = player.DecodedFrameCount;
            var previews = await RasterFramePlayer.LoadOutfitPreviewFramesAsync(catalog, OutfitCatalog.DefaultId, ClipKind.Yawn);
            Check(previews.Count == 121 && previews.All(x => x.IsFrozen) && player.CurrentOutfitId == Office && player.DecodedFrameCount == cached,
                "independent preview freezes frames without changing live player");
            Check(Pixel((BitmapSource)image.Source) == Colors.IndianRed && Pixel(previews[0]) == Colors.SteelBlue,
                "preview and live character pixels come from their respective outfit suites");
            var neutral = await RasterFramePlayer.LoadOutfitPreviewFramesAsync(catalog, Office);
            Check(neutral.Count == 1 && neutral[0].PixelWidth == 320, "neutral preview returns one decoded frame");
            await ThrowsAsync<InvalidDataException>(() => RasterFramePlayer.LoadOutfitPreviewFramesAsync(catalog, "outfit.missing"), "unavailable preview fails explicitly");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => RasterFramePlayer.LoadOutfitPreviewFramesAsync(catalog, Office, ClipKind.Yawn, cancellation.Token),
                "cancelled preview never starts a decoder sequence");
            Check(player.CurrentOutfitId == Office && player.DecodedFrameCount == cached, "preview cancellation leaves live state intact");
        }

        await TestAsyncSequencing(fixture, catalog);
        await TestLoadingFailures();
        await TestTransientRelease(fixture, catalog);
        await TestReleasedLoadRaces(fixture, catalog);
    }

    private static async Task TestTransientRelease(Fixture fixture, OutfitCatalog catalog)
    {
        ClipKind[] emotions = [ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit, ClipKind.Annoyed];
        ClipKind[] games = [ClipKind.Tea, ClipKind.RpsRock, ClipKind.RpsPaper, ClipKind.RpsScissors, ClipKind.RpsWin, ClipKind.RpsLose];
        var image = new Image();
        using var player = new RasterFramePlayer(image, catalog);
        await player.WarmAsync();
        await player.WarmClipsAsync(emotions);
        Check(player.DecodedFrameCount == 787, "common plus intentionally retained emotion cache has 787 frames");
        for (int round = 0; round < 3; round++)
        {
            await player.WarmClipsAsync(games);
            Check(player.DecodedFrameCount == 1513 && games.All(player.IsClipReady), $"game round {round + 1} lazily warms six transient clips");
            player.Apply(Sample(ClipKind.Tea));
            var displayed = image.Source;
            player.ReleaseClips(games.Concat(new[] { ClipKind.Yawn, ClipKind.Shy, ClipKind.Eat, ClipKind.Idle }));
            Check(player.DecodedFrameCount == 908 && player.IsClipReady(ClipKind.Tea) &&
                  !player.IsClipReady(ClipKind.RpsRock) && ReferenceEquals(displayed, image.Source),
                $"game round {round + 1} release protects the playing clip and common three");
            player.Apply(ClipSample.Idle);
            int reads = fixture.TotalReads;
            player.ReleaseClips(games);
            player.ReleaseClips(games); // Repeated boundary notifications do not trigger reloading.
            Check(player.DecodedFrameCount == 787 && games.All(kind => !player.IsClipReady(kind)) &&
                  emotions.All(player.IsClipReady) && fixture.TotalReads == reads,
                $"game round {round + 1} returns to bounded retained cache without IO or emotion eviction");
        }
        await player.WarmClipAsync(ClipKind.Tea);
        player.Apply(Sample(ClipKind.Tea));
        player.Apply(Sample(ClipKind.RpsRock)); // Unprepared action holds the preceding, still visible tea image.
        player.ReleaseClips([ClipKind.Tea, ClipKind.RpsRock]);
        Check(player.IsClipReady(ClipKind.Tea), "release protects the actually displayed clip even when an unready sample was requested");
        player.Apply(ClipSample.Idle);
        player.ReleaseClips(emotions.Concat(games));
        Check(player.DecodedFrameCount == 363, "explicit emotion shutdown can release its four sequences without touching common clips");
        await player.WarmClipAsync(ClipKind.WorkLoop);
        player.Apply(Sample(ClipKind.WorkLoop));
        player.ReleaseWorkFrames();
        Check(player.IsClipReady(ClipKind.WorkLoop), "work-release compatibility API now protects an actively displayed work clip");
        player.Apply(ClipSample.Idle);
        player.ReleaseWorkFrames();
        Check(player.DecodedFrameCount == 363, "work clip becomes releasable after returning to standing");
        player.ReleaseClips([(ClipKind)123456, ClipKind.Idle]);
        Check(player.DecodedFrameCount == 363, "irrelevant release ids cannot evict defaults or grow a frame cache");
    }

    private static async Task TestReleasedLoadRaces(Fixture fixture, OutfitCatalog catalog)
    {
        const string teaFrame = "Assets/Animations/Tea/frame-0000.png";
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var oldGate = fixture.BlockOnce(teaFrame);
            var old = player.WarmClipAsync(ClipKind.Tea);
            await oldGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.ReleaseClips([ClipKind.Tea]);
            var newGate = fixture.BlockOnce(teaFrame);
            var newer = player.WarmClipAsync(ClipKind.Tea);
            await newGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            oldGate.Release.Set();
            await old;
            Check(!player.IsClipReady(ClipKind.Tea), "released old decode cannot fill cache while replacement decode is pending");
            var duplicate = player.WarmClipAsync(ClipKind.Tea);
            Check(ReferenceEquals(newer, duplicate), "old completion cannot delete the newer load registration");
            newGate.Release.Set();
            await Task.WhenAll(newer, duplicate);
            Check(player.IsClipReady(ClipKind.Tea) && player.DecodedFrameCount == 121,
                "warm after in-flight release performs a real replacement load");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var oldGate = fixture.BlockOnce(teaFrame);
            var old = player.WarmClipAsync(ClipKind.Tea);
            await oldGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.ReleaseClips([ClipKind.Tea]);
            await player.WarmClipAsync(ClipKind.Tea);
            player.ReleaseClips([ClipKind.Tea]);
            oldGate.Release.Set();
            await old;
            Check(!player.IsClipReady(ClipKind.Tea) && player.DecodedFrameCount == 0,
                "twice-released clip cannot be resurrected by a very late first generation");
            await player.WarmClipAsync(ClipKind.Tea);
            Check(player.IsClipReady(ClipKind.Tea), "subsequent explicit warm retries after multiple releases");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var gate = fixture.BlockOnce(teaFrame);
            var warm = player.WarmClipAsync(ClipKind.Tea);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.Apply(Sample(ClipKind.Tea));
            player.ReleaseClips([ClipKind.Tea]);
            gate.Release.Set();
            await warm;
            Check(player.IsClipReady(ClipKind.Tea), "release does not invalidate a decode now needed by an active sample");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var gate = fixture.BlockOnce(teaFrame);
            var warm = player.WarmClipAsync(ClipKind.Tea);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.ReleaseClips([ClipKind.Tea]);
            await player.SelectOutfitAsync(Office);
            gate.Release.Set();
            await warm;
            Check(player.CurrentOutfitId == Office && !player.IsClipReady(ClipKind.Tea) && player.DecodedFrameCount == 363,
                "released decode cannot cross a successful outfit switch");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var gate = fixture.BlockOnce(teaFrame);
            var warm = player.WarmClipAsync(ClipKind.Tea);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.ReleaseClips([ClipKind.Tea]);
            player.Dispose();
            gate.Release.Set();
            await ThrowsAsync<OperationCanceledException>(() => warm, "dispose cancels a released in-flight decode without resurrecting it");
        }
        // Fault an old generation only after a newer load has entered its own gate.
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var oldGate = fixture.BlockOnce(teaFrame);
            oldGate.FailAfterRelease = true;
            var old = player.WarmClipAsync(ClipKind.Tea);
            await oldGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.ReleaseClips([ClipKind.Tea]);
            var newGate = fixture.BlockOnce(teaFrame);
            var newer = player.WarmClipAsync(ClipKind.Tea);
            await newGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            oldGate.Release.Set();
            await ThrowsAsync<InvalidDataException>(() => old, "released old load still reports its own failure to its caller");
            Check(player.Warning is null && ReferenceEquals(newer, player.WarmClipAsync(ClipKind.Tea)),
                "released old failure cannot overwrite warnings or remove a newer load");
            newGate.Release.Set();
            await newer;
            Check(player.IsClipReady(ClipKind.Tea), "new generation succeeds despite a late old-generation failure");
        }
    }

    private static async Task TestAsyncSequencing(Fixture fixture, OutfitCatalog catalog)
    {
        var target = new Image();
        using (var player = new RasterFramePlayer(target, catalog))
        {
            var gate = fixture.BlockOnce(OfficeRoot + "neutral.png");
            var old = player.SelectOutfitAsync(Office);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var latest = await player.SelectOutfitAsync(Other);
            gate.Release.Set();
            Check(latest.Success && (await old).Status == OutfitSelectionStatus.Superseded && player.CurrentOutfitId == Other,
                "two different requested outfits completing backwards keep only the newest selection");
            Check(Pixel((BitmapSource)target.Source) == Colors.SeaGreen && player.DecodedFrameCount == 363,
                "latest-selection neutral and cache belong to the same suite");
            player.Apply(Sample(ClipKind.Yawn));
            Check(Pixel((BitmapSource)target.Source) == Colors.SeaGreen, "new outfit action cannot use stale old-colored frames");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var gate = fixture.BlockOnce(OfficeRoot + "neutral.png");
            Task<OutfitSelectionResult> old = player.SelectOutfitAsync(Office);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var recent = await player.SelectOutfitAsync(OutfitCatalog.DefaultId);
            gate.Release.Set();
            Check((await old).Status == OutfitSelectionStatus.Superseded && recent.Success && player.CurrentOutfitId == OutfitCatalog.DefaultId,
                "latest already-selected request supersedes delayed old selection");
            Check(player.Warning is null, "superseded selection cannot overwrite newer warning");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var gate = fixture.BlockOnce(OfficeRoot + "neutral.png");
            var pending = player.SelectOutfitAsync(Office);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.Apply(Sample(ClipKind.WorkLoop));
            gate.Release.Set();
            Check((await pending).Status == OutfitSelectionStatus.UnsafeBoundary && player.CurrentOutfitId == OutfitCatalog.DefaultId,
                "safe boundary rechecked after async decoding");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var gate = fixture.BlockOnce("Assets/Animations/Tea/frame-0000.png");
            var oldWarm = player.WarmClipAsync(ClipKind.Tea);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await player.SelectOutfitAsync(Office);
            gate.Release.Set();
            await oldWarm;
            Check(!player.IsClipReady(ClipKind.Tea) && player.DecodedFrameCount == 363, "stale warm cannot refill a replaced outfit cache");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var gate = fixture.BlockOnce("Assets/Animations/WorkLoop/frame-0000.png");
            var warm = player.WarmClipAsync(ClipKind.WorkLoop);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.ReleaseWorkFrames();
            gate.Release.Set();
            await warm;
            Check(!player.IsClipReady(ClipKind.WorkLoop) && player.DecodedFrameCount == 0, "release invalidates in-flight work decode");
            await player.WarmClipAsync(ClipKind.WorkLoop);
            Check(player.IsClipReady(ClipKind.WorkLoop), "released work clip can be freshly loaded later");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        using (var cancellation = new CancellationTokenSource())
        {
            var gate = fixture.BlockOnce(OfficeRoot + "neutral.png");
            var selection = player.SelectOutfitAsync(Office, cancellation.Token);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            gate.Release.Set();
            Check((await selection).Status == OutfitSelectionStatus.Cancelled && player.CurrentOutfitId == OutfitCatalog.DefaultId,
                "cancelled selection leaves current suite intact");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        using (var cancellation = new CancellationTokenSource())
        {
            var gate = fixture.BlockOnce("Assets/Animations/Tea/frame-0000.png");
            var cancelledWaiter = player.WarmClipAsync(ClipKind.Tea, cancellation.Token);
            var retainedWaiter = player.WarmClipAsync(ClipKind.Tea);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            gate.Release.Set();
            await ThrowsAsync<OperationCanceledException>(() => cancelledWaiter, "cancelling one warm waiter is observable");
            await retainedWaiter;
            Check(player.IsClipReady(ClipKind.Tea) && player.DecodedFrameCount == 121,
                "one cancelled caller does not cancel a shared clip needed by another caller");
        }
        using (var player = new RasterFramePlayer(new Image(), catalog))
        {
            var gate = fixture.BlockOnce(OfficeRoot + "neutral.png");
            var selection = player.SelectOutfitAsync(Office);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            player.Dispose();
            gate.Release.Set();
            Check((await selection).Status == OutfitSelectionStatus.Superseded && player.CurrentOutfitId == OutfitCatalog.DefaultId,
                "disposed player never receives delayed outfit commit");
        }
    }

    private static async Task TestLoadingFailures()
    {
        var fixture = new Fixture();
        var catalog = OutfitCatalog.Load(fixture);
        await catalog.GetAvailabilityAsync(Office);
        var image = new Image();
        using var player = new RasterFramePlayer(image, catalog);
        await player.WarmAsync();
        var previous = image.Source;
        fixture.Files[OfficeRoot + "Animations/Shy/frame-0008.png"] = [1, 2];
        var result = await player.SelectOutfitAsync(Office);
        Check(!result.Success && ReferenceEquals(previous, image.Source) && player.DecodedFrameCount == 363,
            "post-validation decoding failure preserves complete old candidate");
        fixture.Files["Assets/Animations/Tea/frame-0002.png"] = [1, 2];
        await ThrowsAsync<InvalidDataException>(() => player.WarmClipAsync(ClipKind.Tea), "bad lazy frame raises load failure");
        Check(!player.IsClipReady(ClipKind.Tea) && player.DecodedFrameCount == 363, "partial lazy decode is never installed");
        fixture.Files["Assets/Animations/Tea/frame-0002.png"] = fixture.DefaultPng;
        await player.WarmClipAsync(ClipKind.Tea);
        Check(player.IsClipReady(ClipKind.Tea), "failed lazy load can retry after resources recover");
    }

    private static ClipSample Sample(ClipKind kind) => new(kind, ClipPlaybackPhase.Playing, 0.5, 1);

    private static Color Pixel(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static async Task Unavailable(string name, Action<Fixture> mutation)
    {
        var fixture = new Fixture();
        mutation(fixture);
        var result = await OutfitCatalog.Load(fixture).GetAvailabilityAsync(Office);
        Check(!result.IsAvailable && !string.IsNullOrWhiteSpace(result.Warning), name);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); Check(false, name); }
        catch (T) { Check(true, name); }
    }

    private static void Check(bool condition, string name)
    {
        if (condition) _passed++; else _failed++;
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    }

    private sealed class Fixture : IAnimationResourceProvider
    {
        public readonly ConcurrentDictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        public readonly byte[] DefaultPng = Png(384, 346, Colors.SteelBlue);
        private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);
        private int _reads;
        public int TotalReads => Volatile.Read(ref _reads);

        public Fixture()
        {
            AddPack("Assets/", DefaultPng);
            AddPack(OfficeRoot, Png(384, 346, Colors.IndianRed));
            AddPack("Assets/Outfits/Other/", Png(384, 346, Colors.SeaGreen));
            PutRegistry(OfficeRoot + "actions.json");
        }

        public void PutRegistry(string officeManifest) => Put("Assets/outfits.json", JsonSerializer.Serialize(new
        {
            version = 1, defaultOutfitId = OutfitCatalog.DefaultId,
            outfits = new[]
            {
                new OutfitDefinition(OutfitCatalog.DefaultId, "默认", "Assets/actions.json"),
                new OutfitDefinition(Office, "办公", officeManifest),
                new OutfitDefinition(Other, "其他", "Assets/Outfits/Other/actions.json")
            }
        }));

        public void Put(string path, string value) => Files[path] = Encoding.UTF8.GetBytes(value);

        public void ChangeOffice(Action<AnimationAssets> mutate)
        {
            var assets = JsonSerializer.Deserialize<AnimationAssets>(Files[OfficeRoot + "actions.json"])!;
            mutate(assets);
            Put(OfficeRoot + "actions.json", JsonSerializer.Serialize(assets));
        }

        private void AddPack(string root, byte[] png)
        {
            ClipKind[] kinds = [ClipKind.Yawn, ClipKind.Shy, ClipKind.Eat, ClipKind.WorkEnter, ClipKind.WorkLoop,
                ClipKind.WorkToBusy, ClipKind.BusyLoop, ClipKind.WorkExit, ClipKind.BusyExit,
                ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit, ClipKind.Tea,
                ClipKind.RpsRock, ClipKind.RpsPaper, ClipKind.RpsScissors, ClipKind.RpsWin, ClipKind.RpsLose, ClipKind.Annoyed];
            var assets = new AnimationAssets { Version = 1, Neutral = root == "Assets/" ? "Assets/mascot-animated-neutral.png" : root + "neutral.png" };
            Files[assets.Neutral] = png;
            foreach (var kind in kinds)
            {
                var definition = ClipCatalog.GetDefinition(kind);
                var action = new ActionAsset { Id = kind.ToString().ToLowerInvariant(), Clip = kind.ToString(),
                    Group = ClipCatalog.GetGroup(kind).ToString(), Directory = root + "Animations/" + kind,
                    FrameCount = definition.FrameCount, DurationSeconds = definition.DurationSeconds,
                    Phase = kind switch {
                        ClipKind.WorkEnter or ClipKind.HungryEnter => "enter",
                        ClipKind.WorkLoop or ClipKind.BusyLoop or ClipKind.HungryLoop => "loop",
                        ClipKind.WorkToBusy => "transition",
                        ClipKind.WorkExit or ClipKind.BusyExit or ClipKind.HungryExit => "exit", _ => "once" } };
                assets.Actions.Add(action);
                for (int i = 0; i < action.FrameCount; i++) Files[$"{action.Directory}/frame-{i:0000}.png"] = png;
            }
            Put(root + "actions.json", JsonSerializer.Serialize(assets));
        }

        public Stream? Open(string path)
        {
            Interlocked.Increment(ref _reads);
            if (_gates.TryRemove(path, out var gate))
            {
                gate.Entered.TrySetResult();
                if (!gate.Release.Wait(TimeSpan.FromSeconds(20))) throw new IOException("Test resource gate timed out.");
                if (gate.FailAfterRelease) throw new InvalidDataException("Injected stale-generation resource failure.");
            }
            return Files.TryGetValue(path, out var data) ? new MemoryStream(data, false) : null;
        }

        public Gate BlockOnce(string path)
        {
            var gate = new Gate();
            if (!_gates.TryAdd(path, gate)) throw new InvalidOperationException("Already blocked.");
            return gate;
        }

        public static byte[] Png(int width, int height, Color color)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    private sealed class Gate
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new(false);
        public bool FailAfterRelease;
    }

    private sealed class FileResources(string root) : IAnimationResourceProvider
    {
        private readonly string _root = Path.GetFullPath(root);
        private int _pngReads;
        public int PngReads => Volatile.Read(ref _pngReads);
        public Stream? Open(string path)
        {
            AnimationResourcePath.Validate(path);
            var absolute = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!absolute.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Resource escaped fixture root.");
            if (!File.Exists(absolute)) return null;
            var stream = File.OpenRead(absolute);
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _pngReads);
            return stream;
        }
    }
}
