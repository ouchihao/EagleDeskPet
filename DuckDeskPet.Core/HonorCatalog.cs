namespace DuckDeskPet.Core;

public enum HonorTier { Bronze, Silver, Gold }
public enum HonorSeries { Meals, Affection, Growth, Work, Collection, Bond }

public sealed record HonorSeriesDefinition(HonorSeries Series, string Name, string Description, string BadgeKey);

public sealed record HonorDefinition(
    string Id, HonorSeries Series, HonorTier Tier, string Name,
    string Description, int Target, string Unit);

public sealed record HonorProgress(HonorDefinition Definition, int Current)
{
    public bool IsEarned => Current >= Definition.Target;
    public double Fraction => Math.Clamp((double)Current / Definition.Target, 0.0, 1.0);
}

/// <summary>
/// Read-only exhibition of existing lifetime progress. Opening the wall never
/// changes saves or grants rewards. Old achievement flags remain valid evidence
/// for bronze honors when an older save is missing its corresponding counter.
/// </summary>
public static class HonorCatalog
{
    public static IReadOnlyList<HonorSeriesDefinition> Series { get; } = Array.AsReadOnly(new[]
    {
        new HonorSeriesDefinition(HonorSeries.Meals, "干饭搭子", "每一顿好好吃的饭，都值得认真纪念。", "meals"),
        new HonorSeriesDefinition(HonorSeries.Affection, "摸头交情", "只记成功的温柔摸头，不记冷却中的连点。", "affection"),
        new HonorSeriesDefinition(HonorSeries.Growth, "成长足迹", "从初来乍到，到这个桌面的老熟鹰。", "growth"),
        new HonorSeriesDefinition(HonorSeries.Work, "工位值班", "累计有效工作分钟；休息、饥饿停工不凑数。", "work"),
        new HonorSeriesDefinition(HonorSeries.Collection, "装扮收藏", "动作、桌子、电脑和服装；不含三件默认赠品。", "collection"),
        new HonorSeriesDefinition(HonorSeries.Bond, "朝夕搭档", "累计成功喂食与摸头次数，不推算在线时长。", "bond"),
    });

    public static IReadOnlyList<HonorDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new HonorDefinition("first-meal", HonorSeries.Meals, HonorTier.Bronze,
            "第一口真香", "嘴上说客气，第一碗已经见底。", 1, "顿饭"),
        new HonorDefinition("meals-silver", HonorSeries.Meals, HonorTier.Silver,
            "干饭有编制", "饭点准时出现，碗底从不积压。", 10, "顿饭"),
        new HonorDefinition("meals-gold", HonorSeries.Meals, HonorTier.Gold,
            "饭碗终身制", "三十顿交情，这个饭搭子赖定你了。", 30, "顿饭"),
        new HonorDefinition("gentle-hands", HonorSeries.Affection, HonorTier.Bronze,
            "手法已认证", "十次温柔摸头，获得本鹰点头认可。", 10, "次摸头"),
        new HonorDefinition("affection-silver", HonorSeries.Affection, HonorTier.Silver,
            "指定摸头师", "五十次熟练手法，别人来摸我不服。", 50, "次摸头"),
        new HonorDefinition("affection-gold", HonorSeries.Affection, HonorTier.Gold,
            "头号自己人", "一百次亲近，头可以借你，发型不行。", 100, "次摸头"),
        new HonorDefinition("level-2", HonorSeries.Growth, HonorTier.Bronze,
            "熟门熟路", "升到二级，正式认领这块桌面。", 2, "级"),
        new HonorDefinition("growth-silver", HonorSeries.Growth, HonorTier.Silver,
            "工位老熟鹰", "升到五级，论资历，键盘都得让三分。", 5, "级"),
        new HonorDefinition("growth-gold", HonorSeries.Growth, HonorTier.Gold,
            "桌面名誉总监", "升到十级，不管人，只管可爱。", 10, "级"),
        new HonorDefinition("work-bronze", HonorSeries.Work, HonorTier.Bronze,
            "准点打卡鹰", "半小时有效上工，键盘已经认得我。", 30, "分钟"),
        new HonorDefinition("work-silver", HonorSeries.Work, HonorTier.Silver,
            "工位续航王", "累计五小时，工作量有记录，茶也别忘喝。", 300, "分钟"),
        new HonorDefinition("work-gold", HonorSeries.Work, HonorTier.Gold,
            "资深打工鹰", "二十小时攒出来的资历，休息同样要认真。", 1200, "分钟"),
        new HonorDefinition("collection-bronze", HonorSeries.Collection, HonorTier.Bronze,
            "小窝有讲究", "两件新收藏，小鹰开始有自己的审美。", 2, "件收藏"),
        new HonorDefinition("collection-silver", HonorSeries.Collection, HonorTier.Silver,
            "桌面百宝箱", "五件藏品各有脾气，总有一件配今天的心情。", 5, "件收藏"),
        new HonorDefinition("collection-gold", HonorSeries.Collection, HonorTier.Gold,
            "鹰的收藏馆", "九件藏品摆满小窝，每一件都算数。", 9, "件收藏"),
        new HonorDefinition("bond-bronze", HonorSeries.Bond, HonorTier.Bronze,
            "来都来了嘛", "喂饭加摸头二十五回，顺手照料成了习惯。", 25, "次照料"),
        new HonorDefinition("bond-silver", HonorSeries.Bond, HonorTier.Silver,
            "每日惦记你", "一百次小小照料，本鹰全都记在心里。", 100, "次照料"),
        new HonorDefinition("bond-gold", HonorSeries.Bond, HonorTier.Gold,
            "自己鹰认证", "三百次饭与摸头的交情，妥妥是自己鹰。", 300, "次照料"),
    });

    public static IReadOnlyList<HonorProgress> Evaluate(PetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var progress = new Dictionary<HonorSeries, int>
        {
            [HonorSeries.Meals] = Math.Max(0, state.TotalMeals),
            [HonorSeries.Affection] = Math.Max(0, state.TotalPets),
            [HonorSeries.Growth] = state.Level,
            [HonorSeries.Work] = double.IsFinite(state.TotalWorkSeconds) && state.TotalWorkSeconds > 0
                ? (int)Math.Min(int.MaxValue, Math.Floor(state.TotalWorkSeconds / 60.0)) : 0,
            // Missing optional resources never remove ownership or earned honors.
            [HonorSeries.Collection] = state.Content?.OwnedContentIds?.Count(id =>
                ContentCatalog.TryGet(id, out var item) && !item!.IsDefault) ?? 0,
            [HonorSeries.Bond] = (int)Math.Min(int.MaxValue, (long)Math.Max(0, state.TotalMeals) + Math.Max(0, state.TotalPets)),
        };
        foreach (var definition in Definitions)
            if (state.Achievements?.Contains(definition.Id, StringComparer.Ordinal) == true)
                progress[definition.Series] = Math.Max(progress[definition.Series], definition.Target);

        return Array.AsReadOnly(Definitions.Select(definition =>
            new HonorProgress(definition, progress[definition.Series])).ToArray());
    }

    /// <summary>Record proven milestones on a save candidate; grants no currency or items.</summary>
    public static bool RecordEarned(PetState state)
    {
        var earned = Evaluate(state).Where(x => x.IsEarned).Select(x => x.Definition.Id).ToArray();
        state.Achievements ??= new();
        bool changed = false;
        foreach (string id in earned)
            if (!state.Achievements.Contains(id, StringComparer.Ordinal))
            {
                state.Achievements.Add(id);
                changed = true;
            }
        return changed;
    }

}
