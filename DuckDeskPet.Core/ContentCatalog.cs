namespace DuckDeskPet.Core;

public enum ContentType { Desk, Computer, Outfit, Action }
public enum ContentSlot { Desk, Computer, Outfit }
public enum ContentPlaybackMode { None, ManualOnly }

/// <summary>The honor catalog remains the sole source of progress thresholds.</summary>
public sealed record ContentRewardCondition(string HonorId, string Description);

public sealed record ContentDefinition(
    string Id, ContentType Type, string Name, string Description, decimal Price,
    bool IsDefault = false, ContentRewardCondition? Reward = null,
    string? AnimationId = null, string? ScenePropId = null, string? OutfitId = null)
{
    public EquipmentBonuses Bonuses { get; init; } = EquipmentBonuses.None;
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
    public const string WalnutDeskId = "desk.walnut";
    public const string ArcadeDeskId = "desk.arcade";
    public const string RetroComputerId = "computer.retro";
    public const string ArcadeComputerId = "computer.arcade";
    public const string HoodieOutfitId = "outfit.hoodie";
    public const string NoodleDeskId = "desk.noodle";
    public const string CloudDeskId = "desk.cloud";
    public const string BoardroomDeskId = "desk.boardroom";
    public const string ServerComputerId = "computer.server";
    public const string GoldComputerId = "computer.gold";
    public const string UltrabookComputerId = "computer.ultrabook";
    public const string OxOutfitId = "outfit.ox";
    public const string HeroOutfitId = "outfit.hero";
    public const string AstronautOutfitId = "outfit.astronaut";

    public static IReadOnlyList<ContentDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new ContentDefinition(DefaultDeskId, ContentType.Desk, "经典木桌", "饭搭子的第一张工位桌。", 0,
            IsDefault: true, ScenePropId: DefaultDeskId),
        new ContentDefinition(DefaultComputerId, ContentType.Computer, "经典电脑", "开机有工作，关机有生活。", 0,
            IsDefault: true, ScenePropId: DefaultComputerId),
        new ContentDefinition(DefaultOutfitId, ContentType.Outfit, "原味大头鹰", "不穿工服，也有鹰的气场。", 0,
            IsDefault: true, OutfitId: DefaultOutfitId),
        new ContentDefinition(MintDeskId, ContentType.Desk, "薄荷小工位", "工位换清新，活还是那些活。", 150m,
            Reward: new("meals-silver", "累计吃满 30 顿饭免费解锁"), ScenePropId: MintDeskId)
            { Bonuses = new(0.05m, 0.03m, 0.02m, 0.02m) },
        new ContentDefinition(MidnightComputerId, ContentType.Computer, "水果电脑", "标志很体面，文件还是周报。", 240m,
            ScenePropId: MidnightComputerId) { Bonuses = new(WorkCoinBonus: 0.10m, WorkExperienceBonus: 0.10m) },
        new ContentDefinition(TeaActionId, ContentType.Action, "喝茶缓一缓", "工作可以等一口茶的工夫。", 120m,
            Reward: new("level-2", "升到 2 级免费解锁"), AnimationId: "tea"),
        new ContentDefinition(OfficeOutfitId, ContentType.Outfit, "资本家套装", "领带一系，今天的饼由我来画。", 10000m,
            OutfitId: OfficeOutfitId) { Bonuses = new(0.25m, 0.25m, 1.00m, 0.80m) },
        new ContentDefinition(WalnutDeskId, ContentType.Desk, "暖木复古桌", "桌子很沉稳，内心很想溜。", 300m,
            ScenePropId: WalnutDeskId) { Bonuses = new(0.08m, 0.06m, 0.05m, 0.05m) },
        new ContentDefinition(ArcadeDeskId, ContentType.Desk, "闪电电竞桌", "工位像开黑，日报照样追。", 600m,
            ScenePropId: ArcadeDeskId) { Bonuses = new(0.10m, 0.10m, 0.10m, 0.08m) },
        new ContentDefinition(RetroComputerId, ContentType.Computer, "电子木鱼一体机", "敲一下键盘，功德与日报都加一。", 120m,
            ScenePropId: RetroComputerId) { Bonuses = new(WorkCoinBonus: 0.05m, WorkExperienceBonus: 0.05m) },
        new ContentDefinition(ArcadeComputerId, ContentType.Computer, "显卡比工资贵", "跑分很努力，发薪很冷静。", 480m,
            ScenePropId: ArcadeComputerId) { Bonuses = new(WorkCoinBonus: 0.20m, WorkExperienceBonus: 0.15m) },
        new ContentDefinition(HoodieOutfitId, ContentType.Outfit, "摸鱼卫衣", "帽子放背后，小鱼放心头。", 600m,
            OutfitId: HoodieOutfitId) { Bonuses = new(0.05m, 0.10m, 0.10m, 0.10m) },
        new ContentDefinition(NoodleDeskId, ContentType.Desk, "泡面董事会", "三分钟开会，五分钟散香。", 1200m,
            ScenePropId: NoodleDeskId) { Bonuses = new(0.20m, 0.12m, 0.15m, 0.10m) },
        new ContentDefinition(CloudDeskId, ContentType.Desk, "云端摸鱼舱", "工位上了云，老板够不着。", 2400m,
            ScenePropId: CloudDeskId) { Bonuses = new(0.25m, 0.20m, 0.25m, 0.15m) },
        new ContentDefinition(BoardroomDeskId, ContentType.Desk, "董事长的饼桌", "饼画得够大，桌子才装得下。", 4800m,
            ScenePropId: BoardroomDeskId) { Bonuses = new(0.30m, 0.25m, 0.40m, 0.30m) },
        new ContentDefinition(ServerComputerId, ContentType.Computer, "赛博牛马工作站", "风扇一转，牛马精神上线。", 960m,
            ScenePropId: ServerComputerId) { Bonuses = new(WorkCoinBonus: 0.35m, WorkExperienceBonus: 0.25m) },
        new ContentDefinition(GoldComputerId, ContentType.Computer, "黄金电脑", "机身镀金，方案还得自己写。", 1920m,
            ScenePropId: GoldComputerId) { Bonuses = new(WorkCoinBonus: 0.50m, WorkExperienceBonus: 0.35m) },
        new ContentDefinition(UltrabookComputerId, ContentType.Computer, "资本家专用轻薄本", "轻的是电脑，厚的是收益。", 3840m,
            ScenePropId: UltrabookComputerId) { Bonuses = new(WorkCoinBonus: 0.60m, WorkExperienceBonus: 0.50m) },
        new ContentDefinition(OxOutfitId, ContentType.Outfit, "牛马也要体面", "工牌戴正，犄角也要梳齐。", 1200m,
            OutfitId: OxOutfitId) { Bonuses = new(0.10m, 0.10m, 0.20m, 0.20m) },
        new ContentDefinition(HeroOutfitId, ContentType.Outfit, "下班战神", "披风一甩，准点收工。", 2400m,
            OutfitId: HeroOutfitId) { Bonuses = new(0.15m, 0.20m, 0.35m, 0.30m) },
        new ContentDefinition(AstronautOutfitId, ContentType.Outfit, "摸鱼航天员", "离开地心引力，仍没离开工位。", 4800m,
            OutfitId: AstronautOutfitId) { Bonuses = new(0.20m, 0.25m, 0.60m, 0.50m) },
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
