namespace DuckDeskPet.Core;

public enum HonorTier { Bronze, Silver, Gold }
public enum HonorSeries { Meals, Affection, Growth }

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
    });

    public static IReadOnlyList<HonorProgress> Evaluate(PetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        int meals = Math.Max(0, state.TotalMeals);
        int affection = Math.Max(0, state.TotalPets);
        int level = state.Level;
        if (state.Achievements?.Contains("first-meal", StringComparer.Ordinal) == true)
            meals = Math.Max(meals, 1);
        if (state.Achievements?.Contains("gentle-hands", StringComparer.Ordinal) == true)
            affection = Math.Max(affection, 10);
        if (state.Achievements?.Contains("level-2", StringComparer.Ordinal) == true)
            level = Math.Max(level, 2);

        return Array.AsReadOnly(Definitions.Select(definition => new HonorProgress(definition,
            definition.Series switch
            {
                HonorSeries.Meals => meals,
                HonorSeries.Affection => affection,
                HonorSeries.Growth => level,
                _ => throw new InvalidOperationException("Unknown honor series."),
            })).ToArray());
    }
}
