namespace DuckDeskPet.Core;

/// <summary>Each new level costs 50 XP more than the previous one.</summary>
public static class GrowthPolicy
{
    public const int MaximumLevel = 1000;
    public const int MaximumExperience = 25_024_950;
    public const int LegacyMaximumExperience = 99_900;

    // L1 -> L2 costs 100; then 150, 200, 250, ... . Cumulative cost is quadratic.
    public static int ExperienceAtLevel(int level)
    {
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
        if (level >= 10_000) return int.MaxValue;
        long completed = level - 1L;
        return (int)Math.Min(int.MaxValue, 100L * completed + 25L * completed * (completed - 1));
    }

    public static int LevelForExperience(int experience)
    {
        int xp = Math.Clamp(experience, 0, MaximumExperience);
        int level = 1;
        while (ExperienceAtLevel(level + 1) <= xp) level++;
        return level;
    }

    public static int RequirementForLevel(int level) =>
        ExperienceAtLevel(level + 1) - ExperienceAtLevel(level);

    /// <summary>
    /// v1/v2 used exactly 100 XP per level. Map once at schema migration so the
    /// visible level and proportional progress survive a harder curve. This is a
    /// unit conversion, not work, a reward event, or currency back-pay.
    /// </summary>
    public static int MigrateLegacyExperience(int experience)
    {
        int old = Math.Clamp(experience, 0, LegacyMaximumExperience);
        int level = 1 + old / 100;
        if (level >= MaximumLevel) return MaximumExperience;
        return ExperienceAtLevel(level) + (old % 100) * RequirementForLevel(level) / 100;
    }
}
