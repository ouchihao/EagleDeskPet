using System.Collections.Immutable;

namespace DuckDeskPet.Core.CharacterPacks;

public enum CharacterBackend { Raster, Live2D }
public enum CharacterPackKind { AuthorTemplate, Runtime }
public enum CharacterActionSupport { Complete, Preview, Unavailable }
public enum CharacterValidationMode { AuthorTemplate, Runtime }
public enum CharacterDiagnosticSeverity { Information, Warning, Error }
public enum CharacterResourceRole { Model, Moc, Texture, Motion, Expression, Physics, Pose, Metadata, Audio, Preview, RasterManifest }

/// <summary>Data-only, versioned adapter contract with no gameplay state or executable code</summary>
public sealed record CharacterPackManifest
{
    public int Version { get; init; }
    public CharacterPackKind Kind { get; init; }
    public string CharacterId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string PackVersion { get; init; } = "";
    public string RigId { get; init; } = "";
    public CharacterBackend Backend { get; init; }
    public CharacterCapabilities Capabilities { get; init; } = new();
    public ImmutableDictionary<string, CharacterModel> Models { get; init; } = ImmutableDictionary<string, CharacterModel>.Empty;
    public ImmutableDictionary<string, CharacterAction> Actions { get; init; } = ImmutableDictionary<string, CharacterAction>.Empty;
    public ImmutableDictionary<string, CharacterParameter> Parameters { get; init; } = ImmutableDictionary<string, CharacterParameter>.Empty;
    public ImmutableDictionary<string, CharacterAnchor> Anchors { get; init; } = ImmutableDictionary<string, CharacterAnchor>.Empty;
    public ImmutableDictionary<string, CharacterHitArea> HitAreas { get; init; } = ImmutableDictionary<string, CharacterHitArea>.Empty;
    public CharacterScene Scene { get; init; } = new();
    public ImmutableDictionary<string, CharacterOutfit> Outfits { get; init; } = ImmutableDictionary<string, CharacterOutfit>.Empty;
    public ImmutableDictionary<string, CharacterResource> Resources { get; init; } = ImmutableDictionary<string, CharacterResource>.Empty;
    public CharacterSources Sources { get; init; } = new();
}

public sealed record CharacterCapabilities
{
    public bool EyeBlink { get; init; }
    public bool Gaze { get; init; }
    public bool Breath { get; init; }
    public bool SceneLayers { get; init; }
    public bool Outfits { get; init; }
    public bool HitTesting { get; init; }
}

public sealed record CharacterModel
{
    public string Entry { get; init; } = "";
    public string RigId { get; init; } = "";
    public string SdkCompatibility { get; init; } = "";
}

public sealed record CharacterAction
{
    public CharacterActionSupport Support { get; init; }
    public string Model { get; init; } = "";
    public string? Resource { get; init; }
    public double DurationSeconds { get; init; }
    public string Phase { get; init; } = "";
    public string EntryPose { get; init; } = "";
    public string ExitPose { get; init; } = "";
    public double? HoldStartSeconds { get; init; }
    public double? HoldEndSeconds { get; init; }
    public ImmutableArray<string> ControlledParameters { get; init; } = [];
}

public sealed record CharacterParameter
{
    public string Target { get; init; } = "";
    public double Minimum { get; init; }
    public double Maximum { get; init; }
    public double Default { get; init; }
    public string Owner { get; init; } = "";
}

/// <summary>Normalized top-left coordinates, independent of a character's source pixel dimensions</summary>
public sealed record CharacterAnchor
{
    public double X { get; init; }
    public double Y { get; init; }
    public string? Target { get; init; }
}

public sealed record CharacterHitArea
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public sealed record CharacterScene
{
    public string CoordinateSystem { get; init; } = "";
    public string Strategy { get; init; } = "";
    public bool UpdateOncePerFrame { get; init; }
    public ImmutableArray<string> LayerOrder { get; init; } = [];
    public ImmutableArray<string> BodyDrawables { get; init; } = [];
    public ImmutableArray<string> ForegroundDrawables { get; init; } = [];
}

public sealed record CharacterOutfit
{
    public string Model { get; init; } = "";
    public string? Thumbnail { get; init; }
    public string? RasterFallback { get; init; }
    public ImmutableDictionary<string, double> PartOpacity { get; init; } = ImmutableDictionary<string, double>.Empty;
    public ImmutableDictionary<string, double> ParameterValues { get; init; } = ImmutableDictionary<string, double>.Empty;
    public ImmutableDictionary<string, string> ActionOverrides { get; init; } = ImmutableDictionary<string, string>.Empty;
}

public sealed record CharacterResource
{
    public CharacterResourceRole Role { get; init; }
    public long ByteLength { get; init; }
    public string? Sha256 { get; init; }
}

public sealed record CharacterSources
{
    public string Artwork { get; init; } = "";
    public string ModelProject { get; init; } = "";
    public string AnimationProject { get; init; } = "";
    public string Attribution { get; init; } = "";
    public string License { get; init; } = "";
}

public sealed record CharacterPackDiagnostic(CharacterDiagnosticSeverity Severity, string Code, string Location, string Message);

/// <summary>Provided by a trusted host, never read from the package; offline file checks are not a SDK load</summary>
public interface ICharacterModelProbe
{
    CharacterModelInspection Inspect(CharacterPackManifest pack, string modelId, ICharacterPackResources resources);
}

public sealed record CharacterModelInspection(bool Loaded, string Detail,
    ImmutableHashSet<string> Parameters, ImmutableHashSet<string> Parts, ImmutableHashSet<string> Drawables);

public interface ICharacterPackResources
{
    Stream? Open(string safeRelativePath);
}

public sealed record CharacterPackValidationResult(CharacterPackManifest? Manifest,
    ImmutableArray<CharacterPackDiagnostic> Diagnostics, bool ContractValid, bool FilesVerified, bool RuntimeReady)
{
    public bool HasErrors => Diagnostics.Any(x => x.Severity == CharacterDiagnosticSeverity.Error);
    public bool CompleteCharacter => ContractValid && Manifest is not null && Manifest.Capabilities.SceneLayers &&
        Manifest.Capabilities.HitTesting && Manifest.Actions.Values.All(x => x.Support == CharacterActionSupport.Complete);
    public bool ReadyForCompletePet => RuntimeReady && CompleteCharacter;
    /// <summary>Validated bytes, not a reopened mutable author directory; present only after all file checks pass</summary>
    public ICharacterPackResources? VerifiedResources { get; init; }
    public CharacterPackAdapter CreateAdapter()
    {
        if (!ContractValid || Manifest is null) throw new InvalidOperationException("The package contract is invalid.");
        return new(Manifest, RuntimeReady);
    }
}

/// <summary>Character-independent semantic resolver; construction alone never authorizes runtime playback</summary>
public sealed class CharacterPackAdapter
{
    internal CharacterPackAdapter(CharacterPackManifest manifest, bool runtimeReady)
        => (Manifest, RuntimeReady) = (manifest, runtimeReady);
    public CharacterPackManifest Manifest { get; }
    public bool RuntimeReady { get; }
    public bool CanPlay(ClipKind kind) => RuntimeReady && Manifest.Actions.TryGetValue(kind.ToString(), out var action) && action.Support == CharacterActionSupport.Complete;
    public CharacterAction Action(ClipKind kind) => Manifest.Actions[kind.ToString()];
    public CharacterParameter Parameter(string semantic) => Manifest.Parameters[semantic];
    public CharacterAnchor Anchor(string semantic) => Manifest.Anchors[semantic];
    public CharacterOutfit Outfit(string itemId) => Manifest.Outfits[itemId];
    public string? ActionResource(ClipKind kind, string outfitId)
        => Outfit(outfitId).ActionOverrides.TryGetValue(kind.ToString(), out var resource) ? resource : Action(kind).Resource;
    public (double X, double Y) PlaceAnchor(string semantic, double left, double top, double width, double height)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var anchor = Anchor(semantic);
        return (left + anchor.X * width, top + anchor.Y * height);
    }
}
