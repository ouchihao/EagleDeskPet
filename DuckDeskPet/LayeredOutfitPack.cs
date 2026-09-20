using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DuckDeskPet;

/// <summary>
/// Immutable artwork + shared per-frame anatomical UV bindings. Composition happens only while
/// preloading; the 60 Hz player still receives one frozen BitmapSource per authored frame.
/// </summary>
internal sealed class LayeredOutfitPack
{
    private const int Width = OutfitBitmap.Width, Height = OutfitBitmap.Height;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConditionalWeakTable<IAnimationResourceProvider, ConcurrentDictionary<string, Lazy<BindingArchive>>> Archives = new();
    private readonly IAnimationResourceProvider _resources;
    private readonly BindingArchive _bindings;
    private readonly Dictionary<string, string> _keys;
    private readonly Dictionary<string, Layer> _layers;
    internal int FrameCount => _keys.Count;
    internal string AnatomicalSourcePath(string targetPath) => _bindings.Frames[_keys[targetPath]].Source;

    private LayeredOutfitPack(IAnimationResourceProvider resources, BindingArchive bindings,
        Dictionary<string, string> keys, Dictionary<string, Layer> layers)
        => (_resources, _bindings, _keys, _layers) = (resources, bindings, keys, layers);

    internal static LayeredOutfitPack Load(IAnimationResourceProvider resources, AnimationAssets assets)
    {
        string recipePath = assets.Appearance ?? throw new InvalidDataException("Missing outfit recipe.");
        using var input = AnimationResourcePath.ReadBounded(resources, recipePath, 1024 * 1024);
        var recipe = JsonSerializer.Deserialize<Recipe>(input, Json) ?? throw new InvalidDataException("Missing outfit recipe.");
        string root = recipePath[..(recipePath.LastIndexOf('/') + 1)];
        if (recipe.Version != 1 || recipe.Layers is not { Count: > 0 and <= 12 })
            throw new InvalidDataException("Unsupported layered outfit recipe.");
        AnimationResourcePath.Validate(recipe.BaseManifest);
        AnimationResourcePath.Validate(recipe.Bindings);
        if (!recipe.Bindings.StartsWith("Assets/OutfitBindings/", StringComparison.Ordinal) || !recipe.Bindings.EndsWith(".zip", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid anatomical binding archive.");
        var baseAssets = AnimationAssets.Load(resources, recipe.BaseManifest);
        var expected = FramePaths(baseAssets);
        var targetPaths = FramePaths(assets);
        if (!expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(targetPaths.Keys))
            throw new InvalidDataException("Outfit/base action suites differ.");
        var cache = Archives.GetValue(resources, _ => new(StringComparer.Ordinal));
        // Failed validation remains failed: application resources cannot change during a run.
        var bindings = cache.GetOrAdd(recipe.Bindings, _ => new Lazy<BindingArchive>(() =>
            BindingArchive.Load(resources, recipe.Bindings), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        bindings.ValidateSources(resources, recipe.BaseManifest, expected);
        var layers = new Dictionary<string, Layer>(StringComparer.Ordinal);
        foreach (var item in recipe.Layers)
        {
            if (item is null || item.Slot is not ("torso" or "leftSleeve" or "rightSleeve" or "leftBoot" or "rightBoot" or "head" or "back" or "glasses") || layers.ContainsKey(item.Slot))
                throw new InvalidDataException("Invalid or duplicate outfit layer.");
            AnimationResourcePath.Validate(item.Image);
            if (!item.Image.StartsWith(root + "Layers/", StringComparison.Ordinal) || !item.Image.EndsWith(".png", StringComparison.Ordinal) ||
                item.Rect is not { Length: 4 } || item.Rect.Any(x => !double.IsFinite(x) || Math.Abs(x) > 4) || item.Rect[2] <= 0 || item.Rect[3] <= 0 ||
                item.UvQuad is not { Length: 8 } || item.UvQuad.Any(x => !double.IsFinite(x) || x < 0 || x > 1))
                throw new InvalidDataException("Invalid outfit artwork placement.");
            using var image = AnimationResourcePath.ReadBounded(resources, item.Image, 8 * 1024 * 1024);
            var pixels = Pixels.Decode(image, maximumDimension: 2048);
            if (!Enumerable.Range(0, pixels.Width * pixels.Height).Any(i => pixels.Data[i * 4 + 3] != 0))
                throw new InvalidDataException("Empty outfit artwork: " + item.Image);
            layers.Add(item.Slot, new(item, pixels));
        }
        // Full costume packs may not silently ship a static hat in place of articulated clothes.
        if (layers.ContainsKey("torso") && new[] { "leftSleeve", "rightSleeve", "leftBoot", "rightBoot" }.Any(x => !layers.ContainsKey(x)))
            throw new InvalidDataException("A full-body outfit requires both sleeves and both boots.");
        return new(resources, bindings, targetPaths.ToDictionary(x => x.Value, x => x.Key, StringComparer.Ordinal), layers);
    }

    internal BitmapSource Load(string targetPath)
    {
        if (!_keys.TryGetValue(targetPath, out var key)) throw new InvalidDataException("Unknown layered outfit frame: " + targetPath);
        var binding = _bindings.Frames[key];
        using var original = AnimationResourcePath.ReadBounded(_resources, binding.Source, 4 * 1024 * 1024);
        var anatomy = Pixels.Decode(original, Width);
        if (anatomy.Width != Width || anatomy.Height != Height) throw new InvalidDataException("Changed base animation dimensions.");
        var map = _bindings.ReadMap(binding);
        var output = new byte[anatomy.Data.Length];
        // Back accessories are truly behind the actor, including at work/hunger table entrances.
        if (_layers.TryGetValue("back", out var back)) PaintAccessory(output, back, binding.Body);
        for (int p = 0; p < output.Length; p += 4) Over(output, p, anatomy.Data[p], anatomy.Data[p + 1], anatomy.Data[p + 2], anatomy.Data[p + 3]);
        string[] regions = ["", "torso", "leftSleeve", "rightSleeve", "leftBoot", "rightBoot"];
        for (int p = 0; p < output.Length; p += 4)
        {
            int region = map.Data[p + 2]; // map PNG: R=semantic region, G=U, B=V, A=coverage.
            if (region is < 1 or > 5 || map.Data[p + 3] == 0 || !_layers.TryGetValue(regions[region], out var layer)) continue;
            var color = layer.Sample(map.Data[p + 1] / 255.0, map.Data[p] / 255.0);
            // Preserve the moving garment's authored creases/ink without selecting pixels by RGB.
            // Region membership comes exclusively from the audited anatomical atlas.
            double shade = region <= 3 ? Math.Clamp((.2126 * anatomy.Data[p + 2] + .7152 * anatomy.Data[p + 1] + .0722 * anatomy.Data[p]) / 134, .55, 1.18) : 1;
            double alpha = color.A * map.Data[p + 3] / 255.0 * anatomy.Data[p + 3] / 255.0;
            Over(output, p, color.B * shade, color.G * shade, color.R * shade, alpha);
        }
        // Hands and held props remain above headwear. Their coverage is part of the frame's
        // semantic map, not a colour key (white suit sleeves cannot become a cream bowl).
        var clothed = (byte[])output.Clone();
        foreach (string slot in new[] { "head", "glasses" })
            if (_layers.TryGetValue(slot, out var head)) PaintAccessory(output, head, slot == "glasses" ? binding.Eyes ?? binding.Head : binding.Head);
        var foreground = new byte[Width * Height];
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
        {
            int p = (y * Width + x) * 4;
            if (map.Data[p + 2] is not (2 or 3 or 6 or 7) || map.Data[p + 3] == 0) continue;
            // Preserve the dark authored finger outline too: semantic material segmentation
            // intentionally avoids dark ink, whereas occlusion must include that whole contour.
            for (int yy = Math.Max(0, y - 2); yy <= Math.Min(Height - 1, y + 2); yy++)
            for (int xx = Math.Max(0, x - 2); xx <= Math.Min(Width - 1, x + 2); xx++)
                if (anatomy.Data[(yy * Width + xx) * 4 + 3] != 0) foreground[yy * Width + xx] = 255;
        }
        for (int p = 0; p < output.Length; p += 4)
        {
            double coverage = foreground[p / 4] / 255.0;
            if (coverage == 0) continue;
            for (int c = 0; c < 4; c++) output[p + c] = (byte)Math.Round(output[p + c] * (1 - coverage) + clothed[p + c] * coverage);
        }
        var result = Pixels.ToBitmap(output, Width, Height, decodeWidth: 320);
        if (key.StartsWith("Work", StringComparison.Ordinal) || key.StartsWith("Busy", StringComparison.Ordinal) || key.StartsWith("Hungry", StringComparison.Ordinal))
            OutfitCompositeMetadata.Attach(result, Pixels.ToBitmap(anatomy.Data, Width, Height, decodeWidth: 320));
        return result;
    }

    internal static Dictionary<string, string> FramePaths(AnimationAssets assets)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal) { ["neutral"] = assets.Neutral };
        foreach (var action in assets.Actions)
            for (int i = 0; i < action.FrameCount; i++) result.Add($"{action.Clip}/{i:0000}", $"{action.Directory}/frame-{i:0000}.png");
        return result;
    }

    private static void PaintAccessory(byte[] output, Layer layer, double[] transform)
    {
        // Unit-square affine basis: [originX,originY,axisXX,axisXY,axisYX,axisYY].
        double determinant = transform[2] * transform[5] - transform[3] * transform[4];
        var rect = layer.Definition.Rect;
        double[] xs = new double[4], ys = new double[4];
        for (int i = 0; i < 4; i++)
        {
            double u = rect[0] + (i % 2) * rect[2], v = rect[1] + (i / 2) * rect[3];
            xs[i] = transform[0] + u * transform[2] + v * transform[4];
            ys[i] = transform[1] + u * transform[3] + v * transform[5];
        }
        int minX = Math.Max(0, (int)Math.Floor(xs.Min())), maxX = Math.Min(Width, (int)Math.Ceiling(xs.Max()));
        int minY = Math.Max(0, (int)Math.Floor(ys.Min())), maxY = Math.Min(Height, (int)Math.Ceiling(ys.Max()));
        for (int y = minY; y < maxY; y++) for (int x = minX; x < maxX; x++)
        {
            double dx = x + .5 - transform[0], dy = y + .5 - transform[1];
            double u = ((dx * transform[5] - dy * transform[4]) / determinant - rect[0]) / rect[2];
            double v = ((dy * transform[2] - dx * transform[3]) / determinant - rect[1]) / rect[3];
            if (u < 0 || u > 1 || v < 0 || v > 1) continue;
            var c = layer.Art.Sample(u, v); if (c.A <= 0) continue;
            Over(output, (y * Width + x) * 4, c.B, c.G, c.R, c.A);
        }
    }

    private static void Over(byte[] destination, int p, double b, double g, double r, double alpha)
    {
        if (alpha <= 0) return;
        double a = Math.Clamp(alpha / 255, 0, 1), oldA = destination[p + 3] / 255.0;
        double finalA = a + oldA * (1 - a);
        destination[p] = (byte)Math.Clamp(Math.Round((b * a + destination[p] * oldA * (1 - a)) / finalA), 0, 255);
        destination[p + 1] = (byte)Math.Clamp(Math.Round((g * a + destination[p + 1] * oldA * (1 - a)) / finalA), 0, 255);
        destination[p + 2] = (byte)Math.Clamp(Math.Round((r * a + destination[p + 2] * oldA * (1 - a)) / finalA), 0, 255);
        destination[p + 3] = (byte)Math.Round(finalA * 255);
    }

    private sealed class BindingArchive
    {
        private readonly ZipArchive _archive;
        private readonly object _gate = new();
        private bool _validated;
        private readonly string _baseManifest;
        internal readonly Dictionary<string, FrameBinding> Frames;
        private BindingArchive(byte[] zip, BindingManifest manifest)
        {
            _archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
            (_baseManifest, Frames) = (manifest.BaseManifest, manifest.Frames);
        }

        internal static BindingArchive Load(IAnimationResourceProvider resources, string path)
        {
            using var input = AnimationResourcePath.ReadBounded(resources, path, 96 * 1024 * 1024);
            var zip = input.ToArray();
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            if (archive.Entries.Count is < 2 or > 16385 || archive.Entries.Select(x => x.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count ||
                archive.Entries.Any(x => x.Length > 8 * 1024 * 1024 || x.FullName.Contains("..", StringComparison.Ordinal) || x.FullName.Contains('\\') || x.FullName.StartsWith('/')))
                throw new InvalidDataException("Invalid anatomical binding archive entries.");
            using var manifestStream = archive.GetEntry("bindings.json")?.Open() ?? throw new InvalidDataException("Missing anatomical binding manifest.");
            using var bounded = AnimationResourcePath.ReadBounded(manifestStream, path, 8 * 1024 * 1024);
            var manifest = JsonSerializer.Deserialize<BindingManifest>(bounded, Json) ?? throw new InvalidDataException("Missing anatomical bindings.");
            if (manifest.Version != 1 || manifest.Width != Width || manifest.Height != Height || manifest.Frames is not { Count: > 0 and <= 16384 })
                throw new InvalidDataException("Unsupported anatomical binding format.");
            return new(zip, manifest);
        }

        internal void ValidateSources(IAnimationResourceProvider resources, string baseManifest, Dictionary<string, string> expected)
        {
            lock (_gate)
            {
                if (_baseManifest != baseManifest || !expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Frames.Keys))
                    throw new InvalidDataException("Anatomical binding/base action suite mismatch.");
                if (_validated) return;
                foreach (var (key, frame) in Frames)
                {
                    if (frame.Source != expected[key] || frame.Sha256 is not { Length: 64 } ||
                        frame.Map != "maps/" + key + ".png" || !ValidTransform(frame.Head) || !ValidTransform(frame.Body) || (frame.Eyes is not null && !ValidTransform(frame.Eyes)))
                        throw new InvalidDataException("Invalid anatomical frame binding: " + key);
                    using var source = AnimationResourcePath.ReadBounded(resources, frame.Source, 4 * 1024 * 1024);
                    if (!Convert.ToHexString(SHA256.HashData(source)).Equals(frame.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Anatomical bindings are stale: " + frame.Source);
                    source.Position = 0;
                    var pixels = Pixels.Decode(source, Width);
                    var map = ReadMap(frame);
                    if (pixels.Width != Width || pixels.Height != Height) throw new InvalidDataException("Invalid anatomical source dimensions.");
                    for (int p = 0; p < map.Data.Length; p += 4)
                    {
                        int label = map.Data[p + 2];
                        if (label > 7 || (label > 0 && map.Data[p + 3] > 0 && pixels.Data[p + 3] == 0))
                            throw new InvalidDataException("Anatomical garment mask escapes the actor: " + key);
                    }
                }
                _validated = true;
            }
        }

        internal Pixels ReadMap(FrameBinding frame)
        {
            // Keep the immutable archive/directory once per shared atlas. Only compressed-entry
            // reads share the stream lock; WIC decode and composition remain parallel off-thread.
            byte[] png;
            lock (_gate)
            {
                var entry = _archive.GetEntry(frame.Map) ?? throw new InvalidDataException("Missing anatomical UV map: " + frame.Map);
                using var source = entry.Open();
                using var bounded = AnimationResourcePath.ReadBounded(source, frame.Map, 2 * 1024 * 1024);
                png = bounded.ToArray();
            }
            using var image = new MemoryStream(png, writable: false);
            var result = Pixels.Decode(image, Width);
            if (result.Width != Width || result.Height != Height) throw new InvalidDataException("Invalid anatomical map dimensions.");
            return result;
        }

        private static bool ValidTransform(double[]? t) => t is { Length: 6 } && t.All(x => double.IsFinite(x) && Math.Abs(x) <= 1024) &&
            Math.Abs(t[2] * t[5] - t[3] * t[4]) > 16;
    }

    private sealed class Pixels(int width, int height, byte[] data)
    {
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal byte[] Data { get; } = data;
        internal static Pixels Decode(Stream input, int maximumDimension)
        {
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) throw new InvalidDataException("Expected one artwork PNG frame.");
            BitmapSource source = decoder.Frames[0];
            if (source.PixelWidth < 1 || source.PixelHeight < 1 || source.PixelWidth > maximumDimension || source.PixelHeight > maximumDimension)
                throw new InvalidDataException("Artwork PNG exceeds supported dimensions.");
            if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var data = new byte[source.PixelWidth * source.PixelHeight * 4];
            source.CopyPixels(data, source.PixelWidth * 4, 0);
            return new(source.PixelWidth, source.PixelHeight, data);
        }

        internal (double B, double G, double R, double A) Sample(double u, double v)
        {
            double x = Math.Clamp(u, 0, 1) * (Width - 1), y = Math.Clamp(v, 0, 1) * (Height - 1);
            int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
            double fx = x - x0, fy = y - y0;
            ReadOnlySpan<int> points = stackalloc int[] { (y0 * Width + x0) * 4, (y0 * Width + x1) * 4, (y1 * Width + x0) * 4, (y1 * Width + x1) * 4 };
            ReadOnlySpan<double> weights = stackalloc double[] { (1 - fx) * (1 - fy), fx * (1 - fy), (1 - fx) * fy, fx * fy };
            double b = 0, g = 0, r = 0, a = 0;
            for (int i = 0; i < 4; i++)
            {
                int p = points[i]; double contribution = Data[p + 3] * weights[i];
                b += Data[p] * contribution; g += Data[p + 1] * contribution; r += Data[p + 2] * contribution; a += contribution;
            }
            return a > 0 ? (b / a, g / a, r / a, a) : (0, 0, 0, 0);
        }

        internal static BitmapSource ToBitmap(byte[] data, int width, int height, int decodeWidth)
        {
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
            source.Freeze();
            if (width == decodeWidth) return source;
            var scaled = new TransformedBitmap(source, new ScaleTransform((double)decodeWidth / width, (double)decodeWidth / width));
            scaled.Freeze(); return scaled;
        }
    }

    private sealed record Layer(LayerDefinition Definition, Pixels Art)
    {
        internal (double B, double G, double R, double A) Sample(double u, double v)
        {
            // Author-provided sleeve axes turn a diagonal/horizontal isolated garment into a
            // shoulder-to-cuff material patch; no floating, axis-aligned rectangle is pasted.
            var q = Definition.UvQuad;
            double x = ((1 - u) * q[0] + u * q[2]) * (1 - v) + ((1 - u) * q[4] + u * q[6]) * v;
            double y = ((1 - u) * q[1] + u * q[3]) * (1 - v) + ((1 - u) * q[5] + u * q[7]) * v;
            return Art.Sample(x, y);
        }
    }
    private sealed class Recipe
    {
        public int Version { get; set; }
        public string BaseManifest { get; set; } = "";
        public string Bindings { get; set; } = "";
        public List<LayerDefinition> Layers { get; set; } = new();
    }
    private sealed class LayerDefinition
    {
        public string Slot { get; set; } = "";
        public string Image { get; set; } = "";
        public double[] Rect { get; set; } = [0, 0, 1, 1];
        public double[] UvQuad { get; set; } = [0, 0, 1, 0, 0, 1, 1, 1];
    }
    private sealed class BindingManifest
    {
        public int Version { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string BaseManifest { get; set; } = "";
        public Dictionary<string, FrameBinding> Frames { get; set; } = new(StringComparer.Ordinal);
    }
    private sealed class FrameBinding
    {
        public string Source { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string Map { get; set; } = "";
        public double[] Head { get; set; } = [];
        public double[] Body { get; set; } = [];
        public double[]? Eyes { get; set; }
    }
}
