namespace DuckDeskPet.Core;

/// <summary>Additive local-game ratios, never percentages of real money.</summary>
public sealed record EquipmentBonuses(decimal FullnessDecayReduction = 0m,
    decimal MoodDecayReduction = 0m, decimal WorkCoinBonus = 0m,
    decimal WorkExperienceBonus = 0m)
{
    public static EquipmentBonuses None { get; } = new();
}

public static class EquipmentPolicy
{
    public const decimal MaximumDecayReduction = 0.50m;
    public const decimal MaximumCoinBonus = 2.50m;
    public const decimal MaximumExperienceBonus = 2.00m;

    /// <summary>
    /// Only the host-confirmed active selections count. Pending selections,
    /// previews, unknown IDs and unowned items never provide statistics.
    /// </summary>
    public static EquipmentBonuses Resolve(ContentOwnershipState ownership,
        IReadOnlyDictionary<ContentSlot, string> effectiveEquipment)
    {
        decimal fullness = 0, mood = 0, coins = 0, experience = 0;
        foreach (var slot in Enum.GetValues<ContentSlot>())
        {
            if (!effectiveEquipment.TryGetValue(slot, out string? id) ||
                !ContentCatalog.TryGet(id, out var item) || item!.Slot != slot ||
                !ContentOwnershipService.Owns(ownership, id)) continue;
            var bonus = item.Bonuses;
            fullness += bonus.FullnessDecayReduction;
            mood += bonus.MoodDecayReduction;
            coins += bonus.WorkCoinBonus;
            experience += bonus.WorkExperienceBonus;
        }
        return Clamp(new(fullness, mood, coins, experience));
    }

    public static EquipmentBonuses Clamp(EquipmentBonuses bonus) => new(
        Math.Clamp(bonus.FullnessDecayReduction, 0, MaximumDecayReduction),
        Math.Clamp(bonus.MoodDecayReduction, 0, MaximumDecayReduction),
        Math.Clamp(bonus.WorkCoinBonus, 0, MaximumCoinBonus),
        Math.Clamp(bonus.WorkExperienceBonus, 0, MaximumExperienceBonus));
}
