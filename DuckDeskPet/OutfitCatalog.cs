using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using DuckDeskPet.Core;

namespace DuckDeskPet;

internal sealed record OutfitDefinition(string Id, string Name, string AnimationManifest);

internal sealed record OutfitAvailability(string OutfitId, bool IsAvailable, string? Warning,
    IReadOnlyList<ClipKind> SupportedClips);

/// <summary>
/// Whole-character animation packs. Availability verifies every advertised frame, without retaining
/// decoded sequences. An outfit is never assembled from frames belonging to different packs.
/// Embedded resources are immutable; availability tasks may therefore be shared across windows.
/// </summary>
internal sealed class OutfitCatalog
{
    public const string DefaultId = "outfit.default";
    private readonly IAnimationResourceProvider _resources;
    private readonly Dictionary<string, OutfitDefinition> _outfits;
    private readonly Dictionary<string, Task<ValidatedOutfit>> _validation = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private OutfitCatalog(IAnimationResourceProvider resources, IEnumerable<OutfitDefinition> outfits,
        AnimationAssets defaultAssets, string? warning)
    {
        _resources = resources;
        _outfits = outfits.ToDictionary(x => x.Id, StringComparer.Ordinal);
        DefaultAssets = defaultAssets;
        Warning = warning;
        Outfits = Array.AsReadOnly(_outfits.Values.ToArray());
    }

    public string DefaultOutfitId => DefaultId;
    public IReadOnlyList<OutfitDefinition> Outfits { get; }
    public string? Warning { get; }
    internal AnimationAssets DefaultAssets { get; }
    internal IAnimationResourceProvider Resources => _resources;

    public static OutfitCatalog Load(IAnimationResourceProvider? resources = null)
    {
        resources ??= PackAnimationResourceProvider.Instance;
        var defaultAssets = AnimationAssets.Load(resources, "Assets/actions.json");
        var defaults = new[] { new OutfitDefinition(DefaultId, "原味大头鹰", "Assets/actions.json") };
        try
        {
            // Optional for old releases: the original default pack remains usable without a registry.
            using var registry = resources.Open("Assets/outfits.json");
            if (registry is null) return new OutfitCatalog(resources, defaults, defaultAssets, null);
            using var bounded = AnimationResourcePath.ReadBounded(registry, "Assets/outfits.json", 1024 * 1024);
            var data = JsonSerializer.Deserialize<Registry>(bounded,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (data is null || data.Version != 1 || data.DefaultOutfitId != DefaultId ||
                data.Outfits is null || data.Outfits.Count is < 1 or > 32)
                throw new InvalidDataException("Unsupported outfit registry.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.Outfits)
            {
                if (item is null || !ValidOutfitId(item.Id) || !ids.Add(item.Id) ||
                    string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 80)
                    throw new InvalidDataException("Invalid or duplicate outfit entry.");
                AnimationResourcePath.Validate(item.AnimationManifest);
                if (!paths.Add(item.AnimationManifest) || (item.Id == DefaultId
                        ? item.AnimationManifest != "Assets/actions.json"
                        : !item.AnimationManifest.StartsWith("Assets/Outfits/", StringComparison.Ordinal) ||
                          item.AnimationManifest == "Assets/Outfits/actions.json" ||
                          !item.AnimationManifest.EndsWith("/actions.json", StringComparison.Ordinal)))
                    throw new InvalidDataException("Invalid outfit manifest location: " + item.Id);
            }
            if (!ids.Contains(DefaultId)) throw new InvalidDataException("Missing default outfit.");
            return new OutfitCatalog(resources, data.Outfits, defaultAssets, null);
        }
        catch (Exception ex) when (IsResourceError(ex))
        {
            return new OutfitCatalog(resources, defaults, defaultAssets, "服装清单不可用，保留默认外观：" + ex.Message);
        }
    }

    public async Task<OutfitAvailability> GetAvailabilityAsync(string outfitId, CancellationToken token = default)
        => (await GetValidatedAsync(outfitId).WaitAsync(token)).Availability;

    internal Task<ValidatedOutfit> GetValidatedAsync(string outfitId)
    {
        lock (_gate)
        {
            if (_validation.TryGetValue(outfitId, out var task)) return task;
            task = Task.Run(() => ValidateOutfit(outfitId));
            _validation.Add(outfitId, task);
            return task;
        }
    }

    private ValidatedOutfit ValidateOutfit(string outfitId)
    {
        if (!_outfits.TryGetValue(outfitId, out var item))
            return Unavailable(outfitId, "找不到服装清单：" + outfitId);
        try
        {
            var assets = outfitId == DefaultId ? DefaultAssets : AnimationAssets.Load(_resources, item.AnimationManifest);
            // A complete suite follows every currently installed default action (including later expansions).
            foreach (var required in DefaultAssets.Actions)
            {
                var actual = assets.Actions.SingleOrDefault(x => x.Clip == required.Clip);
                if (actual is null || actual.Id != required.Id)
                    throw new InvalidDataException("Missing or mismatched outfit action: " + required.Clip);
            }
            LayeredOutfitPack? layers = null;
            if (assets.Appearance is not null) layers = LayeredOutfitPack.Load(_resources, assets);
            else
            {
                OutfitBitmap.Validate(_resources, assets.Neutral);
                foreach (var action in assets.Actions)
                    for (int i = 0; i < action.FrameCount; i++)
                        OutfitBitmap.Validate(_resources, $"{action.Directory}/frame-{i:0000}.png");
            }
            return new ValidatedOutfit(new OutfitAvailability(outfitId, true, null,
                Array.AsReadOnly(assets.Actions.Select(x => Enum.Parse<ClipKind>(x.Clip)).Prepend(ClipKind.Idle).ToArray())), assets, layers);
        }
        catch (Exception ex) when (IsResourceError(ex))
        {
            return Unavailable(outfitId, "服装资源尚未齐全，暂不可使用：" + ex.Message);
        }
    }

    private static ValidatedOutfit Unavailable(string id, string warning) =>
        new(new OutfitAvailability(id, false, warning, Array.Empty<ClipKind>()), null);

    internal static bool IsResourceError(Exception ex) => ex is InvalidDataException or IOException or JsonException or
        ArgumentException or FormatException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException;

    private static bool ValidOutfitId(string? id) => id is { Length: > 7 and <= 80 } &&
        id.StartsWith("outfit.", StringComparison.Ordinal) &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    private sealed class Registry
    {
        public int Version { get; set; }
        public string DefaultOutfitId { get; set; } = "";
        public List<OutfitDefinition> Outfits { get; set; } = new();
    }

    internal sealed record ValidatedOutfit(OutfitAvailability Availability, AnimationAssets? Assets, LayeredOutfitPack? Layers = null);
}

/// <summary>Injectable only for isolated resource tests; production reads application pack resources.</summary>
internal interface IAnimationResourceProvider
{
    Stream? Open(string path);
}

internal sealed class PackAnimationResourceProvider : IAnimationResourceProvider
{
    public static PackAnimationResourceProvider Instance { get; } = new();
    public Stream? Open(string path)
    {
        AnimationResourcePath.Validate(path);
        try { return Application.GetResourceStream(new Uri("pack://application:,,,/" + path, UriKind.Absolute))?.Stream; }
        catch (IOException) { return null; }
    }
}

internal static class AnimationResourcePath
{
    public static void Validate(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
            path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." ||
                segment.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.'))))
            throw new InvalidDataException("Invalid local animation resource path: " + path);
    }

    public static MemoryStream ReadBounded(IAnimationResourceProvider resources, string path, int limit)
    {
        Validate(path);
        using var stream = resources.Open(path) ?? throw new InvalidDataException("Missing animation resource: " + path);
        return ReadBounded(stream, path, limit);
    }

    internal static MemoryStream ReadBounded(Stream input, string path, int limit)
    {
        var result = new MemoryStream();
        try
        {
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (result.Length + count > limit) throw new InvalidDataException("Animation resource is too large: " + path);
                result.Write(buffer, 0, count);
            }
            result.Position = 0;
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}

internal static class OutfitBitmap
{
    internal const int Width = 384;
    internal const int Height = 346;

    public static void Validate(IAnimationResourceProvider resources, string path)
    {
        using var stream = ReadPng(resources, path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Expected one PNG frame: " + path);
        var frame = decoder.Frames[0];
        int stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
        frame.CopyPixels(new byte[stride * frame.PixelHeight], stride, 0); // Force full image decompression, not just IHDR.
    }

    public static BitmapSource Load(IAnimationResourceProvider resources, string path)
    {
        using var stream = ReadPng(resources, path);
        return Decode(stream);
    }

    internal static BitmapSource Decode(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.DecodePixelWidth = 320;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static MemoryStream ReadPng(IAnimationResourceProvider resources, string path)
    {
        var stream = AnimationResourcePath.ReadBounded(resources, path, 4 * 1024 * 1024);
        var data = stream.GetBuffer().AsSpan(0, checked((int)stream.Length));
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 33 || !data[..8].SequenceEqual(signature) ||
            !data.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4)) != Width ||
            BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4)) != Height)
        {
            stream.Dispose();
            throw new InvalidDataException("Expected a 384x346 outfit PNG: " + path);
        }
        return stream;
    }
}
