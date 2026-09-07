using System.IO;
using System.Text.Json;
using System.Windows;
using DuckDeskPet.Core;

namespace DuckDeskPet;

// Semantic actions stay in Core; the artwork's names and storage live here.
internal sealed class AnimationAssets
{
    public int Version { get; set; }
    public string Neutral { get; set; } = "";
    public List<ActionAsset> Actions { get; set; } = new();

    public static AnimationAssets Load()
    {
        using var stream = Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/actions.json"))!.Stream;
        var result = JsonSerializer.Deserialize<AnimationAssets>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing animation manifest.");
        if (result.Version != 1 || result.Neutral != "Assets/mascot-animated-neutral.png")
            throw new InvalidDataException("Unsupported animation manifest.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var clips = new HashSet<ClipKind>();
        foreach (var action in result.Actions)
        {
            if (!Enum.TryParse<ClipKind>(action.Clip, out var kind) || !ClipCatalog.IsKnown(kind) ||
                kind == ClipKind.Idle || !ids.Add(action.Id) || !clips.Add(kind) ||
                !action.Directory.StartsWith("Assets/Animations/", StringComparison.Ordinal) ||
                action.Directory.Contains("..") || action.Directory.Contains(':') || action.Directory.Contains('\\'))
                throw new InvalidDataException("Invalid animation entry: " + action.Id);
            var definition = ClipCatalog.GetDefinition(kind);
            if (!string.Equals(action.Group, ClipCatalog.GetGroup(kind).ToString(), StringComparison.Ordinal))
                throw new InvalidDataException("Animation group mismatch: " + action.Id);
            if (action.FrameCount != definition.FrameCount ||
                Math.Abs(action.DurationSeconds - definition.DurationSeconds) > 0.00001)
                throw new InvalidDataException("Animation timing mismatch: " + action.Id);
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
