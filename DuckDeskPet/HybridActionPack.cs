using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal static class HybridActionPack
{
    private const string Prefix = "Assets/HybridActions/";

    internal static async Task<IReadOnlyDictionary<ClipKind, IReadOnlyList<BitmapSource>>> LoadAsync(string outfit)
    {
        string? directory = Environment.GetEnvironmentVariable("EAGLE_PET_HYBRID_ACTIONS_DIR");
        IAnimationResourceProvider resources = string.IsNullOrEmpty(directory) ? PackAnimationResourceProvider.Instance : new LocalPack(directory);
        using var manifest = resources.Open(Prefix + "manifest.json");
        if (manifest is null)
        {
            if (!string.IsNullOrEmpty(directory))
                throw new InvalidDataException("缺少与 Live2D 中立造型一致的专用吃饭 / 哈欠帧包：" + outfit);
            if (outfit != OutfitCatalog.DefaultId) return new Dictionary<ClipKind, IReadOnlyList<BitmapSource>>();
            var catalog = OutfitCatalog.Load();
            return await Task.Run<IReadOnlyDictionary<ClipKind, IReadOnlyList<BitmapSource>>>(() =>
            {
                var legacy = new Dictionary<ClipKind, IReadOnlyList<BitmapSource>>();
                var neutral = ReadFrame(catalog.Resources, catalog.DefaultAssets.Neutral);
                foreach (var kind in new[] { ClipKind.Yawn, ClipKind.Eat })
                {
                    string action = catalog.DefaultAssets.Actions.Single(item => item.Clip == kind.ToString()).Directory;
                    var frames = Enumerable.Range(0, 121).Select(index => ReadFrame(catalog.Resources, $"{action}/frame-{index:0000}.png")).ToArray();
                    if (!SamePixels(neutral, frames[0]) || !SamePixels(neutral, frames[^1]))
                        throw new InvalidDataException("Original default action no longer shares the neutral boundary: " + kind);
                    legacy[kind] = Array.AsReadOnly(frames);
                }
                return legacy;
            });
        }
        using var bounded = AnimationResourcePath.ReadBounded(manifest, "hybrid manifest", 64 * 1024);
        using var document = JsonDocument.Parse(bounded);
        var route = HybridActionManifest.Resolve(document.RootElement, outfit, OutfitCatalog.Load().Outfits.Select(item => item.Id));
        if (route.Backend == "live2d") return new Dictionary<ClipKind, IReadOnlyList<BitmapSource>>();
        string root = Prefix + route.Directory + "/";
        AnimationResourcePath.Validate(root + "neutral.png");
        return await Task.Run<IReadOnlyDictionary<ClipKind, IReadOnlyList<BitmapSource>>>(() =>
        {
            var neutral = ReadFrame(resources, root + "neutral.png");
            var clips = new Dictionary<ClipKind, IReadOnlyList<BitmapSource>>();
            foreach (var kind in new[] { ClipKind.Yawn, ClipKind.Eat })
            {
                var frames = Enumerable.Range(0, 121).Select(index => ReadFrame(resources, $"{root}{kind}/frame-{index:0000}.png")).ToArray();
                if (!SamePixels(neutral, frames[0]) || !SamePixels(neutral, frames[^1]))
                    throw new InvalidDataException("Hybrid action does not share its complete neutral boundary: " + outfit + "/" + kind);
                clips.Add(kind, Array.AsReadOnly(frames));
            }
            return clips;
        });
    }

    private static BitmapSource ReadFrame(IAnimationResourceProvider resources, string path)
    {
        using var stream = AnimationResourcePath.ReadBounded(resources, path, 4 * 1024 * 1024);
        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        if (stream.Length < png.Length || !stream.GetBuffer().AsSpan(0, png.Length).SequenceEqual(png))
            throw new InvalidDataException("Hybrid action frame is not PNG: " + path);
        // Retain the original canvas ratio so Core and native neutral poses share one footprint
        var frame = new BitmapImage();
        frame.BeginInit();
        frame.CacheOption = BitmapCacheOption.OnLoad;
        frame.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        frame.StreamSource = stream;
        frame.EndInit();
        if (frame.PixelWidth != 384 || frame.PixelHeight != 346)
            throw new InvalidDataException("Hybrid frames must retain the original 384x346 canvas: " + path);
        int stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
        frame.CopyPixels(new byte[stride * frame.PixelHeight], stride, 0);
        frame.Freeze();
        return frame;
    }

    private static bool SamePixels(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        byte[] Pixels(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
            converted.CopyPixels(pixels, source.PixelWidth * 4, 0);
            return pixels;
        }
        return Pixels(a).SequenceEqual(Pixels(b));
    }

    private sealed class LocalPack : IAnimationResourceProvider
    {
        private readonly string _root;
        internal LocalPack(string directory)
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
                throw new InvalidDataException("Hybrid developer override must be an existing absolute directory");
            _root = Path.GetFullPath(directory);
        }
        public Stream? Open(string path)
        {
            AnimationResourcePath.Validate(path);
            if (!path.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected hybrid resource prefix");
            string local = Path.Combine(_root, path[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(local) ? File.OpenRead(local) : null;
        }
    }
}
