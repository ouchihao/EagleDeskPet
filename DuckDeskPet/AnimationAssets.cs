using System.IO;
using System.Text.Json;
using DuckDeskPet.Core;

namespace DuckDeskPet;

// Semantic actions stay in Core; the artwork's names and storage live here.
internal sealed class AnimationAssets
{
    public int Version { get; set; }
    public string Neutral { get; set; } = "";
    public List<ActionAsset> Actions { get; set; } = new();

    public static AnimationAssets Load() => Load(PackAnimationResourceProvider.Instance, "Assets/actions.json");

    internal static AnimationAssets Load(IAnimationResourceProvider resources, string manifestPath)
    {
        AnimationResourcePath.Validate(manifestPath);
        bool isDefault = manifestPath == "Assets/actions.json";
        string root = isDefault ? "Assets/" : manifestPath[..(manifestPath.LastIndexOf('/') + 1)];
        if (!isDefault && (!root.StartsWith("Assets/Outfits/", StringComparison.Ordinal) || root == "Assets/Outfits/" ||
                           manifestPath != root + "actions.json"))
            throw new InvalidDataException("Invalid outfit manifest path: " + manifestPath);
        using var stream = AnimationResourcePath.ReadBounded(resources, manifestPath, 1024 * 1024);
        var result = JsonSerializer.Deserialize<AnimationAssets>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing animation manifest.");
        if (result.Version != 1 || result.Actions is null || result.Actions.Count > 64 ||
            result.Neutral != (isDefault ? "Assets/mascot-animated-neutral.png" : root + "neutral.png"))
            throw new InvalidDataException("Unsupported animation manifest.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var clips = new HashSet<ClipKind>();
        foreach (var action in result.Actions)
        {
            if (action is null || string.IsNullOrWhiteSpace(action.Id) || action.Id.Length > 80 ||
                !Enum.TryParse<ClipKind>(action.Clip, out var kind) || action.Clip != kind.ToString() || !ClipCatalog.IsKnown(kind) ||
                kind == ClipKind.Idle || !ids.Add(action.Id) || !clips.Add(kind) ||
                action.Directory is null || !action.Directory.StartsWith(root + "Animations/", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid animation entry: " + action?.Id);
            AnimationResourcePath.Validate(action.Directory);
            var definition = ClipCatalog.GetDefinition(kind);
            if (!string.Equals(action.Group, ClipCatalog.GetGroup(kind).ToString(), StringComparison.Ordinal))
                throw new InvalidDataException("Animation group mismatch: " + action.Id);
            if (action.FrameCount != definition.FrameCount || !double.IsFinite(action.DurationSeconds) ||
                Math.Abs(action.DurationSeconds - definition.DurationSeconds) > 0.00001)
                throw new InvalidDataException("Animation timing mismatch: " + action.Id);
            string phase = kind switch
            {
                ClipKind.WorkEnter or ClipKind.HungryEnter => "enter",
                ClipKind.WorkLoop or ClipKind.BusyLoop or ClipKind.HungryLoop => "loop",
                ClipKind.WorkToBusy => "transition",
                ClipKind.WorkExit or ClipKind.BusyExit or ClipKind.HungryExit => "exit",
                _ => "once"
            };
            if (action.Phase != phase) throw new InvalidDataException("Animation phase mismatch: " + action.Id);
        }
        foreach (var required in new[] { ClipKind.Yawn, ClipKind.Shy, ClipKind.Eat,
                     ClipKind.WorkEnter, ClipKind.WorkLoop, ClipKind.WorkToBusy, ClipKind.BusyLoop, ClipKind.WorkExit, ClipKind.BusyExit })
            if (!clips.Contains(required)) throw new InvalidDataException("Missing action: " + required);
        return result;
    }
}

internal sealed class ActionAsset
{
    public string Id { get; set; } = "";
    public string Clip { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Phase { get; set; } = "once";
    public string Directory { get; set; } = "";
    public int FrameCount { get; set; }
    public double DurationSeconds { get; set; }
}
