using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
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
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        int exit = 1;
        _ = Run();
        Dispatcher.Run();
        return exit;
        async Task Run()
        {
            try
            {
                if (Option("--resources-root") is { } root && Option("--output") is { } output)
                    await Real(root, output, Option("--bindings-root"), args.Contains("--samples-only"), args.Contains("--export-thumbnails"));
                else await Fixtures();
                Console.WriteLine($"LayeredOutfitSelfTest: {_checks} passed."); exit = 0;
            }
            catch (Exception ex) { Console.WriteLine(ex); }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
        }
        string? Option(string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }

    private static async Task Fixtures()
    {
        var f = new Fixture(); var catalog = OutfitCatalog.Load(f);
        var available = await catalog.GetAvailabilityAsync("outfit.layered");
        Console.WriteLine($"Fixture availability: {available.IsAvailable}, clips={available.SupportedClips.Count}, {available.Warning}");
        Check(available.IsAvailable && available.SupportedClips.Count == 20, "compact costume validates all 19 actions plus neutral");
        var preview = (await RasterFramePlayer.LoadOutfitPreviewFramesAsync(catalog, "outfit.layered"))[0];
        Check(preview.IsFrozen, "preload returns a frozen cross-thread image");
        Check(Pixel(preview, 191, 267).B > Pixel(preview, 191, 267).R + 80, "torso UV uses clothing texture");
        var hand = Pixel(preview, 165, 228);
        Check(hand.R > hand.B + 60, "raised hand restores original pixels above head accessory");
        Check(Pixel(preview, 194, 140).R > 220, "unlabelled white face remains intact");
        Check(!f.Files.ContainsKey("Assets/Outfits/Layered/neutral.png"), "no duplicate baked neutral PNG needed");
        var target = new Image();
        using (var player = new RasterFramePlayer(target, catalog))
        {
            Check((await player.SelectOutfitAsync("outfit.layered")).Success, "complete candidate atomically applies at idle");
            Check(player.DecodedFrameCount == 363, "prewarms the same three common actions only");
            int reads = f.Reads;
            player.Apply(new ClipSample(ClipKind.Shy, ClipPlaybackPhase.Playing, .5, 1));
            Check(f.Reads == reads, "60 Hz Apply does no I/O or per-frame composition");
            var displayed = target.Source;
            Check((await player.SelectOutfitAsync(OutfitCatalog.DefaultId)).Status == OutfitSelectionStatus.UnsafeBoundary && ReferenceEquals(target.Source, displayed), "active animation cannot undress or jump");
            await player.WarmClipAsync(ClipKind.WorkLoop);
            player.Apply(new ClipSample(ClipKind.WorkLoop, ClipPlaybackPhase.Playing, .5, 1));
            Check(!ReferenceEquals(OutfitCompositeMetadata.Anatomy((BitmapSource)target.Source), target.Source), "work frames retain colour-independent occlusion geometry");
            player.Apply(ClipSample.Idle);
            Check((await player.SelectOutfitAsync(OutfitCatalog.DefaultId)).Success, "standing returns atomically to the native pack");
        }
        await Reject("source hash mismatch", f => f.CorruptHash = true);
        await Reject("missing final map", f => f.MissingLastMap = true);
        await Reject("map escapes source alpha", f => f.EscapeMask = true);
        await Reject("unknown semantic label", f => f.InvalidLabel = true);
        await Reject("singular head transform", f => f.SingularHead = true);
        await Reject("missing boot in full-body recipe", f => f.MissingBoot = true);
        await Reject("cross-outfit artwork path", f => f.ForeignArtwork = true);
        await Reject("stale base manifest binding", f => f.WrongBase = true);
        await Reject("incomplete action suite", f => f.IncompleteSuite = true);
        var shared = new Fixture(); var sharedCatalog = OutfitCatalog.Load(shared);
        await sharedCatalog.GetAvailabilityAsync("outfit.layered");
        int firstReads = shared.BaseReads;
        await sharedCatalog.GetAvailabilityAsync("outfit.alternative");
        Check(shared.BaseReads == firstReads, "multiple costumes validate their common anatomical atlas only once");
    }

    private static async Task Reject(string name, Action<Fixture> change)
    {
        var f = new Fixture(build: false); change(f); f.Build();
        var result = await OutfitCatalog.Load(f).GetAvailabilityAsync("outfit.layered");
        Check(!result.IsAvailable && result.Warning is not null, "rejects " + name);
    }

    private static async Task Real(string root, string output, string? bindingsRoot, bool samplesOnly, bool exportThumbnails)
    {
        var resources = new FileResources(root, bindingsRoot);
        var catalog = OutfitCatalog.Load(resources);
        Directory.CreateDirectory(output);
        var observations = new List<object>();
        foreach (var definition in catalog.Outfits)
        {
            string id = definition.Id;
            if (AnimationAssets.Load(resources, definition.AnimationManifest).Appearance is null) continue;
            var validationWatch = System.Diagnostics.Stopwatch.StartNew();
            var validated = await catalog.GetValidatedAsync(id);
            validationWatch.Stop();
            Check(validated.Availability.IsAvailable, id + " complete production resources validate");
            var paths = LayeredOutfitPack.FramePaths(validated.Assets!);
            var contacts = new List<(string, BitmapSource)>();
            long allocatedBefore = GC.GetTotalAllocatedBytes();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var (key, path) in paths)
            {
                bool contact = key == "neutral" || int.Parse(key.Split('/')[1]) is 0 or 30 or 60 or 90 or 120 or 144 or 168 or 210;
                if (samplesOnly && !contact) continue;
                // Every frame is really composed. Retain only the small contact sample set.
                var frame = RasterFramePlayer.LoadFrame(resources, path, validated.Layers);
                if (contact) contacts.Add((key, frame));
                if (key == "neutral" && exportThumbnails)
                {
                    string thumbnail = Path.Combine(root, validated.Assets!.Neutral[..validated.Assets.Neutral.LastIndexOf('/')], "thumbnail.png");
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
                    using var file = File.Create(thumbnail); encoder.Save(file);
                    Console.WriteLine("Exported " + thumbnail);
                }
                Check(frame.IsFrozen && frame.PixelWidth == 320, id + "/" + key + " complete frozen frame", quiet: true);
            }
            watch.Stop();
            for (int page = 0; page * 20 < contacts.Count; page++)
                SaveContact(contacts.Skip(page * 20).Take(20).ToArray(), Path.Combine(output, id + $"-{page + 1:00}.png"));
            observations.Add(new { outfit = id, validatedFrames = paths.Count, validationSeconds = validationWatch.Elapsed.TotalSeconds,
                composedFrames = samplesOnly ? contacts.Count : paths.Count, composeSeconds = watch.Elapsed.TotalSeconds,
                totalAllocatedMiB = (GC.GetTotalAllocatedBytes() - allocatedBefore) / 1048576.0 });
            Console.WriteLine($"REAL {id}: {paths.Count} validated frames in {validationWatch.Elapsed.TotalSeconds:F1}s; composed {(samplesOnly ? contacts.Count : paths.Count)} in {watch.Elapsed.TotalSeconds:F1}s");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SaveContact((string Name, BitmapSource Frame)[] frames, string path)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(26, 31, 42)), null, new System.Windows.Rect(0, 0, 1280, 1016));
            for (int i = 0; i < frames.Length; i++)
            {
                int x = i % 5 * 256, y = i / 5 * 254;
                dc.DrawText(new FormattedText(frames[i].Name, System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, 1), new System.Windows.Point(x + 8, y + 3));
                dc.DrawImage(frames[i].Frame, new System.Windows.Rect(x, y + 23, 256, 231));
            }
        }
        var bitmap = new RenderTargetBitmap(1280, 1016, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static (int R, int G, int B, int A) Pixel(BitmapSource source, int nativeX, int nativeY)
    {
        var readable = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int x = (int)Math.Round(nativeX * source.PixelWidth / 384.0), y = (int)Math.Round(nativeY * source.PixelHeight / 346.0);
        byte[] bytes = new byte[4]; readable.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return (bytes[2], bytes[1], bytes[0], bytes[3]);
    }
    private static void Check(bool condition, string name, bool quiet = false)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        _checks++; if (!quiet) Console.WriteLine("PASS " + name);
    }

    private sealed class FileResources(string root, string? bindingsRoot) : IAnimationResourceProvider
    {
        public Stream? Open(string path)
        {
            string file = bindingsRoot is not null && path.StartsWith("Assets/OutfitBindings/", StringComparison.Ordinal)
                ? Path.Combine(bindingsRoot, Path.GetFileName(path)) : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) ? File.OpenRead(file) : null;
        }
    }

    private sealed class Fixture : IAnimationResourceProvider
    {
        internal readonly ConcurrentDictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        internal bool CorruptHash, MissingLastMap, EscapeMask, InvalidLabel, SingularHead, MissingBoot, ForeignArtwork, WrongBase, IncompleteSuite;
        internal int Reads, BaseReads;
        internal Fixture(bool build = true) { if (build) Build(); }
        public Stream? Open(string path)
        {
            Interlocked.Increment(ref Reads);
            if (path.StartsWith("Assets/Animations/", StringComparison.Ordinal)) Interlocked.Increment(ref BaseReads);
            return Files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes, writable: false) : null;
        }
        internal void Build()
        {
            var neutral = new byte[384 * 346 * 4];
            Rect(neutral, 120, 70, 144, 162, 245, 245, 245); Rect(neutral, 150, 226, 84, 80, 192, 116, 68);
            Rect(neutral, 154, 218, 24, 28, 190, 112, 65); Rect(neutral, 207, 218, 24, 28, 190, 112, 65);
            var native = Png(neutral); string sha = Convert.ToHexString(SHA256.HashData(native));
            var maps = new byte[neutral.Length];
            Rect(maps, 178, 248, 29, 43, 1, 128, 128); Rect(maps, 154, 218, 24, 28, 6, 0, 0); Rect(maps, 207, 218, 24, 28, 7, 0, 0);
            if (EscapeMask) Rect(maps, 3, 3, 2, 2, 1, 0, 0);
            if (InvalidLabel) Rect(maps, 180, 260, 2, 2, 8, 0, 0);
            var mapPng = Png(maps);
            AnimationAssets baseAssets = Manifest("Assets/", isDefault: true);
            PutJson("Assets/actions.json", baseAssets);
            var paths = LayeredOutfitPack.FramePaths(baseAssets);
            var bindings = new Dictionary<string, object>();
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (key, path) in paths)
                    {
                        Files[path] = native;
                        string name = "maps/" + key + ".png";
                        if (!MissingLastMap || key != paths.Keys.Last()) Write(zip, name, mapPng);
                        bindings[key] = new { source = path, sha256 = CorruptHash ? new string('0', 64) : sha, map = name,
                            head = SingularHead ? new double[6] : new double[] { 120, 70, 144, 0, 0, 162 }, body = new double[] { 150, 226, 84, 0, 0, 80 } };
                    }
                    if (IncompleteSuite) bindings.Remove(bindings.Keys.Last());
                    Write(zip, "bindings.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 1, width = 384, height = 346,
                        baseManifest = WrongBase ? "Assets/Outfits/Other/actions.json" : "Assets/actions.json", frames = bindings })));
                }
                Files["Assets/OutfitBindings/Default-v1.zip"] = stream.ToArray();
            }
            foreach (string folder in new[] { "Layered", "Alternative" })
            {
                string root = $"Assets/Outfits/{folder}/";
                var assets = Manifest(root); assets.Appearance = root + "appearance.json"; PutJson(root + "actions.json", assets);
                var layers = new List<object>();
                foreach (var slot in new[] { "torso", "leftSleeve", "rightSleeve", "leftBoot", "rightBoot", "head" })
                {
                    if (MissingBoot && slot == "leftBoot") continue;
                    string image = root + "Layers/" + slot + ".png";
                    Files[image] = Png(Color(slot == "head" ? (byte)30 : (byte)35, slot == "head" ? (byte)220 : (byte)60, slot == "head" ? (byte)50 : (byte)235));
                    layers.Add(new { slot, image = ForeignArtwork ? "Assets/Outfits/Foreign/Layers/torso.png" : image,
                        rect = slot == "head" ? new[] { 0.0, .75, 1.0, .25 } : new[] { 0.0, 0.0, 1.0, 1.0 } });
                }
                PutJson(root + "appearance.json", new { version = 1, baseManifest = "Assets/actions.json", bindings = "Assets/OutfitBindings/Default-v1.zip", layers });
            }
            PutJson("Assets/outfits.json", new { version = 1, defaultOutfitId = "outfit.default", outfits = new[] {
                new { id = "outfit.default", name = "Default", animationManifest = "Assets/actions.json" },
                new { id = "outfit.layered", name = "Layered", animationManifest = "Assets/Outfits/Layered/actions.json" },
                new { id = "outfit.alternative", name = "Alternative", animationManifest = "Assets/Outfits/Alternative/actions.json" } } });
        }
        private static AnimationAssets Manifest(string root, bool isDefault = false)
        {
            ClipKind[] supported = [ClipKind.Yawn, ClipKind.Shy, ClipKind.Eat, ClipKind.WorkEnter, ClipKind.WorkLoop, ClipKind.WorkToBusy,
                ClipKind.BusyLoop, ClipKind.WorkExit, ClipKind.BusyExit, ClipKind.HungryEnter, ClipKind.HungryLoop, ClipKind.HungryExit,
                ClipKind.Tea, ClipKind.RpsRock, ClipKind.RpsPaper, ClipKind.RpsScissors, ClipKind.RpsWin, ClipKind.RpsLose, ClipKind.Annoyed];
            var actions = supported.Select(kind =>
            {
                var definition = ClipCatalog.GetDefinition(kind);
                return new ActionAsset { Id = kind.ToString(), Clip = kind.ToString(), Name = kind.ToString(), Group = ClipCatalog.GetGroup(kind).ToString(),
                    Directory = root + "Animations/" + kind, FrameCount = definition.FrameCount, DurationSeconds = definition.DurationSeconds,
                    Phase = kind switch { ClipKind.WorkEnter or ClipKind.HungryEnter => "enter", ClipKind.WorkLoop or ClipKind.BusyLoop or ClipKind.HungryLoop => "loop",
                        ClipKind.WorkToBusy => "transition", ClipKind.WorkExit or ClipKind.BusyExit or ClipKind.HungryExit => "exit", _ => "once" } };
            }).ToList();
            return new AnimationAssets { Version = 1, Neutral = root + (isDefault ? "mascot-animated-neutral.png" : "neutral.png"), Actions = actions };
        }
        private void PutJson(string path, object value) => Files[path] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        private static void Write(ZipArchive zip, string name, byte[] bytes) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); }
        private static byte[] Color(byte r, byte g, byte b) { var data = new byte[384 * 346 * 4]; Rect(data, 0, 0, 384, 346, r, g, b); return data; }
        private static void Rect(byte[] data, int x, int y, int width, int height, byte r, byte g, byte b)
        {
            for (int yy = y; yy < y + height; yy++) for (int xx = x; xx < x + width; xx++)
            { int p = (yy * 384 + xx) * 4; data[p] = b; data[p + 1] = g; data[p + 2] = r; data[p + 3] = 255; }
        }
        private static byte[] Png(byte[] data)
        {
            var source = BitmapSource.Create(384, 346, 96, 96, PixelFormats.Bgra32, null, data, 384 * 4); source.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
        }
    }
}
