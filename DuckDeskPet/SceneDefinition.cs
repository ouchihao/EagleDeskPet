using System.Text.Json.Serialization;

namespace DuckDeskPet;

// Version 1 props are authored on the same full canvas as the existing eagle.
// A skin changes art, never the character's position, gameplay or clip timing.
internal sealed record SceneSelection([property: JsonRequired] string SceneId,
    [property: JsonRequired] string DeskId, [property: JsonRequired] string ComputerId);
internal sealed record ScenePoint([property: JsonRequired] double X, [property: JsonRequired] double Y);
internal sealed record SceneCanvas([property: JsonRequired] double Width, [property: JsonRequired] double Height);
internal sealed record SceneInterval([property: JsonRequired] double StartSeconds, [property: JsonRequired] double DurationSeconds);
internal sealed record ScenePhase([property: JsonRequired] string Clip, [property: JsonRequired] int FrameCount,
    [property: JsonRequired] double DurationSeconds);

internal sealed record SceneMotionDefinition
{
    public required double DeskTravelPixels { get; init; }
    public required double ComputerTravelPixels { get; init; }
    public required SceneInterval DeskEnter { get; init; }
    public required SceneInterval ComputerEnter { get; init; }
    public required SceneInterval DeskExit { get; init; }
    public required SceneInterval ComputerExit { get; init; }
    public required double DropLandingFraction { get; init; }
    public required double DropFirstBounceEndFraction { get; init; }
    public required double DropFirstBouncePixels { get; init; }
    public required double DropSecondBouncePixels { get; init; }
}

internal sealed record SceneFireDefinition
{
    public required string Directory { get; init; }
    public required int FrameCount { get; init; }
    public required int LoopFrameCount { get; init; }
    public required double SampleRate { get; init; }
    public required ScenePoint Anchor { get; init; }
    public required SceneInterval Grow { get; init; }
    public required SceneInterval Shrink { get; init; }

    public string FramePath(int index) => $"{Directory}/frame-{index:0000}.png";
}

internal sealed record SceneDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string CompatibilityId { get; init; }
    public required SceneCanvas Canvas { get; init; }
    public required ScenePoint CharacterFootAnchor { get; init; }
    public required ScenePoint DeskSurfaceAnchor { get; init; }
    public required IReadOnlyList<string> LayerOrder { get; init; }
    public required IReadOnlyList<ScenePhase> Phases { get; init; }
    public required SceneMotionDefinition Motion { get; init; }
    public required SceneFireDefinition Fire { get; init; }
}

internal sealed record SceneDeskDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string CompatibilityId { get; init; }
    public required string Back { get; init; }
    public required string Front { get; init; }
}

internal sealed record SceneComputerDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string CompatibilityId { get; init; }
    public required string Image { get; init; }
}

internal sealed record ResolvedWorkScene(SceneDefinition Scene, SceneDeskDefinition Desk,
    SceneComputerDefinition Computer)
{
    public SceneSelection Selection => new(Scene.Id, Desk.Id, Computer.Id);
}
