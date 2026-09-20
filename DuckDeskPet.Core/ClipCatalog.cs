using System;

namespace DuckDeskPet.Core;

/// <summary>
/// Central timing contract shared by the timeline and renderer. The frame
/// counts describe endpoint-inclusive, authored 60 fps raster sequences.
/// </summary>
public static class ClipCatalog
{
    public const double SampleRate = 60.0;
    public const int PlayableClipCount = 1;

    public static bool IsKnown(ClipKind kind) =>
        (uint)kind <= (uint)ClipKind.Stretch || kind == ClipKind.Eat || IsScene(kind) ||
        kind is >= ClipKind.Tea and <= ClipKind.Annoyed;

    public static bool IsAutomaticAction(ClipKind kind) =>
        kind == ClipKind.Yawn;

    public static bool IsWorkScene(ClipKind kind) => kind is >= ClipKind.WorkEnter and <= ClipKind.BusyExit;
    public static bool IsHungryScene(ClipKind kind) => kind is >= ClipKind.HungryEnter and <= ClipKind.HungryExit;
    public static bool IsScene(ClipKind kind) => IsWorkScene(kind) || IsHungryScene(kind);

    public static PetActionGroup GetGroup(ClipKind kind)
    {
        if (!IsKnown(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return kind is ClipKind.Idle or ClipKind.Yawn ? PetActionGroup.Idle : PetActionGroup.Interaction;
    }

    public static ClipDefinition GetDefinition(ClipKind kind) => kind switch
    {
        ClipKind.Idle => new(kind, 1, 0.0, 0.0),
        ClipKind.SideEye => new(kind, 121, 2.00, 0.48),
        ClipKind.Bomb => new(kind, 121, 2.00, 0.64),
        ClipKind.Yawn => new(kind, 121, 2.00, 0.56),
        ClipKind.Shy => new(kind, 121, 2.00, 0.55),
        ClipKind.Eat => new(kind, 121, 2.00, 0.55),
        ClipKind.WorkEnter => new(kind, 211, 3.50, 0.50),
        ClipKind.WorkLoop => new(kind, 121, 2.00, 0.50),
        ClipKind.WorkToBusy => new(kind, 91, 1.50, 0.50),
        ClipKind.BusyLoop => new(kind, 121, 2.00, 0.50),
        ClipKind.WorkExit => new(kind, 121, 2.00, 0.50),
        ClipKind.BusyExit => new(kind, 121, 2.00, 0.50),
        ClipKind.HungryEnter => new(kind, 91, 1.50, 0.50),
        ClipKind.HungryLoop => new(kind, 121, 2.00, 0.50),
        ClipKind.HungryExit => new(kind, 91, 1.50, 0.50),
        ClipKind.RpsRock or ClipKind.RpsPaper or ClipKind.RpsScissors => new(kind, 169, 2.80, 0.50),
        ClipKind.RpsWin or ClipKind.RpsLose => new(kind, 145, 2.40, 0.50),
        ClipKind.Tea or ClipKind.Annoyed => new(kind, 121, 2.00, 0.50),
        ClipKind.Wiggle => new(kind, 30, 1.00, 0.50),
        ClipKind.Hop => new(kind, 36, 1.15, 0.47),
        ClipKind.Nod => new(kind, 24, 0.82, 0.50),
        ClipKind.Shimmy => new(kind, 42, 1.25, 0.52),
        ClipKind.Stretch => new(kind, 42, 1.42, 0.56),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "The clip kind is not recognized."),
    };

    public static double GetProgressAtFrame(ClipKind kind, int frameIndex)
    {
        ClipDefinition definition = GetDefinition(kind);
        if (definition.FrameCount <= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "Idle has no normalized frame timeline.");
        }

        if (frameIndex < 0 || frameIndex >= definition.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        return frameIndex / (double)(definition.FrameCount - 1);
    }
}
