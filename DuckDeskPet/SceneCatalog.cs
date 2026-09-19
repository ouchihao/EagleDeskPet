using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DuckDeskPet.Core;

namespace DuckDeskPet;

/// <summary>Validated, immutable built-in art catalog; not an arbitrary user-file or URL loader.</summary>
internal sealed class SceneCatalog
{
    internal const string ResourcePath = "Assets/SceneProps/work-scenes.json";
    private readonly IReadOnlyDictionary<string, SceneDefinition> _scenes;
    private readonly IReadOnlyDictionary<string, SceneDeskDefinition> _desks;
    private readonly IReadOnlyDictionary<string, SceneComputerDefinition> _computers;
    private readonly IReadOnlyDictionary<string, string> _unavailable;

    internal SceneSelection DefaultSelection { get; }
    internal IReadOnlyCollection<SceneDeskDefinition> Desks { get; }
    internal IReadOnlyCollection<SceneComputerDefinition> Computers { get; }

    private SceneCatalog(CatalogDocument document, IReadOnlySet<string> missingResources)
    {
        DefaultSelection = document.DefaultSelection;
        _scenes = document.Scenes.Select(x => x with
        {
            LayerOrder = Array.AsReadOnly(x.LayerOrder.ToArray()),
            Phases = Array.AsReadOnly(x.Phases.ToArray()),
        }).ToDictionary(x => x.Id, StringComparer.Ordinal);
        _desks = document.Desks.ToDictionary(x => x.Id, StringComparer.Ordinal);
        _computers = document.Computers.ToDictionary(x => x.Id, StringComparer.Ordinal);
        // Built-in pack resources are immutable. Resolve is a dictionary lookup, not a file probe.
        var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var scene in document.Scenes)
        {
            string? missing = Enumerable.Range(0, scene.Fire.FrameCount).Select(scene.Fire.FramePath).FirstOrDefault(missingResources.Contains);
            if (missing is not null) unavailable[scene.Id] = missing;
        }
        foreach (var desk in document.Desks)
        {
            string? missing = new[] { desk.Back, desk.Front }.FirstOrDefault(missingResources.Contains);
            if (missing is not null) unavailable[desk.Id] = missing;
        }
        foreach (var computer in document.Computers)
            if (missingResources.Contains(computer.Image)) unavailable[computer.Id] = computer.Image;
        _unavailable = unavailable;
        Desks = Array.AsReadOnly(document.Desks.ToArray());
        Computers = Array.AsReadOnly(document.Computers.ToArray());
    }

    /// <summary>Build/audit entry point: every registered resource must be present.</summary>
    internal static SceneCatalog Parse(Stream json, Func<string, bool> resourceExists) => Parse(json, resourceExists, false);

    /// <summary>Runtime entry point: invalid metadata/defaults still fail closed, but missing optional
    /// prop sets remain catalogued and unavailable instead of preventing the default pet from starting.</summary>
    internal static SceneCatalog ParseRuntime(Stream json, Func<string, bool> resourceExists) => Parse(json, resourceExists, true);

    private static SceneCatalog Parse(Stream json, Func<string, bool> resourceExists, bool allowMissingOptionalResources)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(resourceExists);
        CatalogDocument document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 24,
            }) ?? throw new InvalidDataException("Missing work scene catalog.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid work scene catalog JSON.", exception);
        }
        var missingResources = new HashSet<string>(StringComparer.Ordinal);
        var availability = new Dictionary<string, bool>(StringComparer.Ordinal);
        Validate(document, path =>
        {
            if (!availability.TryGetValue(path, out bool exists)) availability[path] = exists = resourceExists(path);
            if (!exists) missingResources.Add(path);
            return exists || allowMissingOptionalResources;
        });
        var catalog = new SceneCatalog(document, missingResources);
        // The fallback is not fabricated: it must be a complete, compatible, real default set.
        catalog.Resolve(catalog.DefaultSelection);
        return catalog;
    }

    internal ResolvedWorkScene Resolve(SceneSelection selection)
    {
        if (selection is null || string.IsNullOrEmpty(selection.SceneId) ||
            string.IsNullOrEmpty(selection.DeskId) || string.IsNullOrEmpty(selection.ComputerId) ||
            !_scenes.TryGetValue(selection.SceneId, out var scene) ||
            !_desks.TryGetValue(selection.DeskId, out var desk) ||
            !_computers.TryGetValue(selection.ComputerId, out var computer))
            throw new InvalidDataException("Unknown work scene or prop selection.");
        if (desk.CompatibilityId != scene.CompatibilityId || computer.CompatibilityId != scene.CompatibilityId)
            throw new InvalidDataException("Work scene prop slots are incompatible.");
        foreach (string id in new[] { scene.Id, desk.Id, computer.Id })
            if (_unavailable.TryGetValue(id, out string? missing))
                throw new InvalidDataException("Work scene selection is unavailable; missing asset: " + missing);
        return new(scene, desk, computer);
    }

    // Callers restoring an old selection can fall back visibly without changing ownership data.
    internal bool TryResolve(SceneSelection selection, out ResolvedWorkScene? scene, out string? error)
    {
        try { scene = Resolve(selection); error = null; return true; }
        catch (InvalidDataException exception) { scene = null; error = exception.Message; return false; }
    }

    private static void Validate(CatalogDocument document, Func<string, bool> exists)
    {
        Require(document.Version == 1, "Unsupported work scene catalog version.");
        Require(document.DefaultSelection is not null, "Missing default work scene selection.");
        Require(document.Scenes is { Count: > 0 and <= 32 } && document.Desks is { Count: > 0 and <= 128 } &&
            document.Computers is { Count: > 0 and <= 128 }, "Missing or excessive scene entries.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scene in document.Scenes)
        {
            Require(scene is not null, "Null scene entry.");
            Entry(scene.Id, scene.Name, "work.", ids);
            Id(scene.CompatibilityId);
            // These bounds are an authored-character contract, not a new resampling option.
            Require(scene.Canvas is { Width: 384, Height: 346 }, "Unsupported work scene canvas; expected 384 x 346.");
            Require(scene.CharacterFootAnchor is { X: 191.5, Y: 336 }, "Work scene character anchor mismatch.");
            Require(scene.DeskSurfaceAnchor is { X: 192, Y: 276 }, "Work scene tabletop anchor mismatch.");
            Require(scene.LayerOrder is not null && scene.LayerOrder.SequenceEqual(
                new[] { "fire", "desk-back", "character", "desk-front", "computer" }), "Unsupported work scene layer order.");
            Require(scene.Phases is { Count: 6 }, "Work scene must declare all six authored phases.");
            var phases = new HashSet<ClipKind>();
            foreach (var phase in scene.Phases)
            {
                Require(phase is not null && Enum.TryParse<ClipKind>(phase.Clip, out _), "Unknown scene phase.");
                var kind = Enum.Parse<ClipKind>(phase.Clip);
                Require(ClipCatalog.IsWorkScene(kind) && phase.Clip == kind.ToString() && phases.Add(kind), "Unknown or duplicate scene phase.");
                var expected = ClipCatalog.GetDefinition(kind);
                Require(phase.FrameCount == expected.FrameCount && phase.DurationSeconds == expected.DurationSeconds,
                    "Scene phase cannot change the authored character timeline: " + kind);
            }
            Require(scene.Motion is not null && scene.Fire is not null, "Missing scene motion or fire rules.");
            var motion = scene.Motion;
            Number(motion.DeskTravelPixels, scene.Canvas.Width, 2048, "desk travel");
            Number(motion.ComputerTravelPixels, scene.Canvas.Height, 2048, "computer travel");
            Interval(motion.DeskEnter, 3.5, "desk entry");
            Interval(motion.ComputerEnter, 3.5, "computer entry");
            Interval(motion.DeskExit, 2, "desk exit");
            Interval(motion.ComputerExit, 2, "computer exit");
            Number(motion.DropLandingFraction, 0.01, 0.98, "drop landing fraction");
            Number(motion.DropFirstBounceEndFraction, motion.DropLandingFraction + 0.01, 0.99, "bounce fraction");
            Number(motion.DropFirstBouncePixels, 0, 50, "first bounce height");
            Number(motion.DropSecondBouncePixels, 0, 50, "second bounce height");
            Require(motion.DeskEnter.StartSeconds + motion.DeskEnter.DurationSeconds <=
                motion.ComputerEnter.StartSeconds + motion.ComputerEnter.DurationSeconds * motion.DropLandingFraction,
                "Computer cannot land before the tabletop arrives.");
            var fire = scene.Fire;
            Path(fire.Directory, directory: true);
            Require(fire.FrameCount is >= 2 and <= 601 && fire.LoopFrameCount == fire.FrameCount - 1,
                "Fire must exclude its duplicate terminal frame from looping.");
            Require(fire.SampleRate == ClipCatalog.SampleRate, "Fire must use the 60 Hz authored sample rate.");
            Require(fire.Anchor is not null, "Missing fire anchor.");
            Number(fire.Anchor.X, 0, scene.Canvas.Width, "fire anchor X");
            Number(fire.Anchor.Y, 0, scene.Canvas.Height, "fire anchor Y");
            Interval(fire.Grow, 1.5, "fire growth");
            Interval(fire.Shrink, 2, "fire shrink");
            for (int i = 0; i < fire.FrameCount; i++) Resource(fire.FramePath(i), exists);
        }
        foreach (var desk in document.Desks)
        {
            Require(desk is not null, "Null desk entry.");
            Entry(desk.Id, desk.Name, "desk.", ids); Id(desk.CompatibilityId);
            Require(document.Scenes.Any(x => x.CompatibilityId == desk.CompatibilityId), "Desk uses an unknown compatibility slot.");
            Resource(desk.Back, exists); Resource(desk.Front, exists);
            Require(desk.Back != desk.Front, "A desk requires distinct back and front layers.");
        }
        foreach (var computer in document.Computers)
        {
            Require(computer is not null, "Null computer entry.");
            Entry(computer.Id, computer.Name, "computer.", ids); Id(computer.CompatibilityId);
            Require(document.Scenes.Any(x => x.CompatibilityId == computer.CompatibilityId), "Computer uses an unknown compatibility slot.");
            Resource(computer.Image, exists);
        }
    }

    private static void Entry(string id, string name, string prefix, HashSet<string> ids)
    {
        Id(id);
        Require(id.StartsWith(prefix, StringComparison.Ordinal) && ids.Add(id), "Invalid or duplicate scene/prop ID: " + id);
        Require(!string.IsNullOrWhiteSpace(name) && name.Length <= 80, "Missing or excessive scene/prop name.");
    }

    private static void Id(string value) => Require(value is not null &&
        Regex.IsMatch(value, "^[a-z][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant), "Invalid stable scene/prop ID.");

    private static void Path(string value, bool directory = false)
    {
        Require(value is not null && value.Length <= 240 && value.StartsWith("Assets/SceneProps/", StringComparison.Ordinal) &&
            Regex.IsMatch(value, "^[A-Za-z0-9/_-]+(?:\\.png)?$", RegexOptions.CultureInvariant) &&
            !value.Contains("//", StringComparison.Ordinal) && !value.EndsWith('/') &&
            (directory ? !value.Contains('.') : value.EndsWith(".png", StringComparison.Ordinal)),
            "Invalid built-in work scene asset path.");
    }

    private static void Resource(string path, Func<string, bool> exists)
    {
        Path(path);
        Require(exists(path), "Missing work scene asset: " + path);
    }

    private static void Interval(SceneInterval interval, double phaseDuration, string name)
    {
        Require(interval is not null, "Missing " + name + " interval.");
        Number(interval.StartSeconds, 0, phaseDuration, name + " start");
        Number(interval.DurationSeconds, 0.001, phaseDuration, name + " duration");
        Require(interval.StartSeconds + interval.DurationSeconds <= phaseDuration + 1e-9, name + " exceeds its authored phase.");
    }

    private static void Number(double value, double minimum, double maximum, string name) =>
        Require(double.IsFinite(value) && value >= minimum && value <= maximum, "Invalid " + name + ".");

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record CatalogDocument
    {
        public required int Version { get; init; }
        public required SceneSelection DefaultSelection { get; init; }
        public required IReadOnlyList<SceneDefinition> Scenes { get; init; }
        public required IReadOnlyList<SceneDeskDefinition> Desks { get; init; }
        public required IReadOnlyList<SceneComputerDefinition> Computers { get; init; }
    }
}
