namespace DuckDeskPet.Core;

public enum ContentType { Desk, Computer, Outfit, Action }
public enum ContentSlot { Desk, Computer, Outfit }
public enum ContentPlaybackMode { None, ManualOnly }

/// <summary>The honor catalog remains the sole source of progress thresholds.</summary>
public sealed record ContentRewardCondition(string HonorId, string Description);

public sealed record ContentDefinition(
    string Id, ContentType Type, string Name, string Description, int Price,
    bool IsDefault = false, ContentRewardCondition? Reward = null,
    string? AnimationId = null, string? ScenePropId = null, string? OutfitId = null)
{
    public ContentSlot? Slot => Type switch
    {
        ContentType.Desk => ContentSlot.Desk,
        ContentType.Computer => ContentSlot.Computer,
        ContentType.Outfit => ContentSlot.Outfit,
        _ => null,
    };

    // Buying an action grants an explicit play button, not entry into the idle pool.
    public ContentPlaybackMode PlaybackMode => Type == ContentType.Action
        ? ContentPlaybackMode.ManualOnly : ContentPlaybackMode.None;
}

/// <summary>
/// Local demo definitions, not proof that their art is installed. The GUI must
/// independently validate each definition's animation/scene/outfit resources.
/// Stable content IDs are shared by shop purchases, honor grants, and save data.
/// </summary>
public static class ContentCatalog
{
    public const string DefaultDeskId = "desk.default";
    public const string DefaultComputerId = "computer.default";
    public const string DefaultOutfitId = "outfit.default";
    public const string MintDeskId = "desk.mint";
    public const string MidnightComputerId = "computer.midnight";
    public const string TeaActionId = "action.tea";
    public const string OfficeOutfitId = "outfit.office";

    public static IReadOnlyList<ContentDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new ContentDefinition(DefaultDeskId, ContentType.Desk, "经典木桌", "饭搭子的第一张工位桌。", 0,
            IsDefault: true, ScenePropId: DefaultDeskId),
        new ContentDefinition(DefaultComputerId, ContentType.Computer, "经典电脑", "开机有工作，关机有生活。", 0,
            IsDefault: true, ScenePropId: DefaultComputerId),
        new ContentDefinition(DefaultOutfitId, ContentType.Outfit, "原味大头鹰", "不穿工服，也有鹰的气场。", 0,
            IsDefault: true, OutfitId: DefaultOutfitId),
        new ContentDefinition(MintDeskId, ContentType.Desk, "薄荷小工位", "工位换清新，活还是那些活。", 20,
            Reward: new("meals-silver", "累计吃满 10 顿饭免费解锁"), ScenePropId: MintDeskId),
        new ContentDefinition(MidnightComputerId, ContentType.Computer, "午夜小电脑", "屏幕很酷，下班要准时。", 30,
            ScenePropId: MidnightComputerId),
        new ContentDefinition(TeaActionId, ContentType.Action, "喝茶缓一缓", "工作可以等一口茶的工夫。", 15,
            Reward: new("level-2", "升到 2 级免费解锁"), AnimationId: "tea"),
        new ContentDefinition(OfficeOutfitId, ContentType.Outfit, "认真上班装", "穿得很专业，心里想下班。", 40,
            OutfitId: OfficeOutfitId),
    });

    private static readonly IReadOnlyDictionary<string, ContentDefinition> ById =
        Definitions.ToDictionary(x => x.Id, StringComparer.Ordinal);

    public static IReadOnlyList<string> DefaultContentIds { get; } = Array.AsReadOnly(
        Definitions.Where(x => x.IsDefault).Select(x => x.Id).ToArray());

    public static bool TryGet(string? id, out ContentDefinition? definition)
    {
        definition = null;
        return id is not null && ById.TryGetValue(id, out definition);
    }

    public static ContentDefinition Get(string id) =>
        TryGet(id, out var definition) ? definition! : throw new ArgumentException("Unknown content ID.", nameof(id));

    public static string DefaultForSlot(ContentSlot slot) => slot switch
    {
        ContentSlot.Desk => DefaultDeskId,
        ContentSlot.Computer => DefaultComputerId,
        ContentSlot.Outfit => DefaultOutfitId,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public static string PurchaseTransactionId(string contentId) => "purchase:" + Get(contentId).Id;
}
