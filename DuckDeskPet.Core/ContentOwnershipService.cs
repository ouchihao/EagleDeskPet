using System.IO;

namespace DuckDeskPet.Core;

/// <summary>
/// Serializable local ownership. Unknown but well-formed IDs survive normalization
/// so removing an optional asset pack never erases an acquired item.
/// </summary>
public sealed class ContentOwnershipState
{
    public HashSet<string> OwnedContentIds { get; set; } = new(ContentCatalog.DefaultContentIds, StringComparer.Ordinal);
    public Dictionary<ContentSlot, string> Equipped { get; set; } = new()
    {
        [ContentSlot.Desk] = ContentCatalog.DefaultDeskId,
        [ContentSlot.Computer] = ContentCatalog.DefaultComputerId,
        [ContentSlot.Outfit] = ContentCatalog.DefaultOutfitId,
    };
    public Dictionary<ContentSlot, string> PendingEquipment { get; set; } = new();

    public ContentOwnershipState CreateSnapshot() => new()
    {
        OwnedContentIds = new(OwnedContentIds, StringComparer.Ordinal),
        Equipped = new(Equipped),
        PendingEquipment = new(PendingEquipment),
    };
}

public enum ContentOperationStatus
{
    Granted, AlreadyOwned, PurchaseReady, RewardAvailable, UnknownContent,
    Unavailable, NotOwned, NotEquippable, Equipped, Pending, NoChange, ReadyToPlay,
}

public readonly record struct ContentOperationResult(
    ContentOperationStatus Status, string Message, ContentDefinition? Definition = null)
{
    public bool Changed => Status is ContentOperationStatus.Granted or ContentOperationStatus.Equipped or ContentOperationStatus.Pending;
    public bool CanPurchase => Status == ContentOperationStatus.PurchaseReady;
    public bool CanPlay => Status == ContentOperationStatus.ReadyToPlay;
}

public sealed record ContentResolution(ContentSlot Slot, string RequestedId,
    string? EffectiveId, bool IsFallback, string? Warning);

/// <summary>
/// Candidate-state operations only: no I/O, currency deduction, automatic idle
/// playback, or renderer dependencies. Persist changes atomically with the wallet.
/// A resource probe is required; a catalog entry alone never claims art exists.
/// </summary>
public static class ContentOwnershipService
{
    public const int MaximumOwnedContents = 4096;
    // Reserve room for every built-in grant. A full imported/unknown collection
    // must not make a later level-up or tenth meal throw halfway through care.
    // Keep the v2 reservation stable. Growing the catalog must not invalidate a
    // formerly valid full collection of optional/unknown IDs. The twelve reserved
    // slots still cover defaults and all automatic grants; purchases check space.
    private static int MaximumUnknownContents => MaximumOwnedContents - 12;
    private static readonly ContentSlot[] Slots = Enum.GetValues<ContentSlot>();

    public static ContentOwnershipState Normalize(ContentOwnershipState? saved)
    {
        if (saved is null) return new();
        if ((saved.OwnedContentIds?.Count ?? 0) > MaximumOwnedContents ||
            (saved.Equipped?.Count ?? 0) > 64 || (saved.PendingEquipment?.Count ?? 0) > 64)
            throw new InvalidDataException("Content ownership exceeds its save bounds.");
        var result = new ContentOwnershipState();
        foreach (string id in saved.OwnedContentIds ?? new())
            if (IsValidId(id)) result.OwnedContentIds.Add(id);
        if (result.OwnedContentIds.Count > MaximumOwnedContents ||
            result.OwnedContentIds.Count(x => !ContentCatalog.TryGet(x, out _)) > MaximumUnknownContents ||
            result.OwnedContentIds.Count + ReservedAutomaticGrants(result) > MaximumOwnedContents)
            throw new InvalidDataException("Content ownership exceeds its save bounds.");
        CopySelections(saved.Equipped, result.Equipped);
        CopySelections(saved.PendingEquipment, result.PendingEquipment);
        return result;
    }

    public static bool Owns(ContentOwnershipState state, string contentId) =>
        (ContentCatalog.TryGet(contentId, out var definition) && definition!.IsDefault) ||
        state.OwnedContentIds.Contains(contentId);

    /// <summary>Reject malformed bounded save data, never reject well-formed unknown content IDs.</summary>
    public static void ValidateForSave(ContentOwnershipState? state)
    {
        if (state is null || state.OwnedContentIds is null || state.Equipped is null || state.PendingEquipment is null ||
            state.OwnedContentIds.Count > MaximumOwnedContents || state.OwnedContentIds.Any(x => !IsValidId(x)) ||
            state.OwnedContentIds.Count(x => !ContentCatalog.TryGet(x, out _)) > MaximumUnknownContents ||
            state.OwnedContentIds.Count + ReservedAutomaticGrants(state) > MaximumOwnedContents ||
            state.Equipped.Count > Slots.Length || state.PendingEquipment.Count > Slots.Length ||
            state.Equipped.Concat(state.PendingEquipment).Any(x => !Enum.IsDefined(x.Key) || !IsValidId(x.Value)))
            throw new InvalidDataException("Invalid content ownership save data.");
    }

    /// <summary>Read-only reward lookup; both shop and honor wall use this same evidence.</summary>
    public static bool IsRewardEligible(ContentDefinition definition, PetState progress) =>
        definition.Reward is not null && HonorCatalog.Evaluate(progress)
            .Any(x => x.Definition.Id == definition.Reward.HonorId && x.IsEarned);

    /// <summary>
    /// Grants even if optional art is absent. No coins are refunded or granted if
    /// the same content was bought earlier; there is one non-consumable ownership.
    /// </summary>
    public static IReadOnlyList<string> GrantEligibleRewards(ContentOwnershipState candidate, PetState progress)
    {
        string[] granted = ContentCatalog.Definitions
            .Where(x => (x.IsDefault || IsRewardEligible(x, progress)) && !candidate.OwnedContentIds.Contains(x.Id))
            .Select(x => x.Id).ToArray();
        if (candidate.OwnedContentIds.Count + granted.Length > MaximumOwnedContents)
            throw new InvalidDataException("Content ownership is full.");
        foreach (string id in granted) candidate.OwnedContentIds.Add(id);
        if (ReferenceEquals(candidate, progress.Content)) HonorCatalog.RecordEarned(progress);
        return Array.AsReadOnly(granted);
    }

    public static ContentOperationResult QuotePurchase(ContentOwnershipState state, string contentId,
        PetState progress, Func<ContentDefinition, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        if (!ContentCatalog.TryGet(contentId, out var definition)) return new(ContentOperationStatus.UnknownContent, "没有找到这件内容。");
        if (Owns(state, contentId)) return new(ContentOperationStatus.AlreadyOwned, "已经拥有啦，不会重复购买。", definition);
        if (IsRewardEligible(definition!, progress)) return new(ContentOperationStatus.RewardAvailable, "已达成荣誉条件，可以免费解锁。", definition);
        if (!isAvailable(definition!)) return new(ContentOperationStatus.Unavailable, "这件内容的资源尚未就绪，暂不售卖。", definition);
        if (state.OwnedContentIds.Count + ReservedAutomaticGrants(state) >= MaximumOwnedContents)
            return new(ContentOperationStatus.Unavailable, "收藏空间已满，或剩余位置已为免费荣誉奖励保留。", definition);
        return new(ContentOperationStatus.PurchaseReady, $"需要 {definition!.Price:F2} 鹰币。", definition);
    }

    /// <summary>
    /// Use inside PetStore.TryTransaction with the quoted Price and stable purchase
    /// transaction ID. Returning Changed=false must cancel the debit candidate.
    /// Rechecks ownership, reward eligibility, and resources at commit preparation.
    /// </summary>
    public static ContentOperationResult GrantPurchase(ContentOwnershipState candidate, string contentId,
        PetState progress, Func<ContentDefinition, bool> isAvailable)
    {
        var quote = QuotePurchase(candidate, contentId, progress, isAvailable);
        if (!quote.CanPurchase) return quote;
        candidate.OwnedContentIds.Add(contentId);
        if (ReferenceEquals(candidate, progress.Content)) HonorCatalog.RecordEarned(progress);
        return new(ContentOperationStatus.Granted, "已经放进你的收藏啦。", quote.Definition);
    }

    /// <summary>Queues an equipment change while an authored scene/action is active.</summary>
    public static ContentOperationResult RequestEquip(ContentOwnershipState candidate, string contentId,
        bool deferUntilSafeBoundary, Func<ContentDefinition, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        if (!ContentCatalog.TryGet(contentId, out var definition)) return new(ContentOperationStatus.UnknownContent, "没有找到这件内容。");
        if (definition!.Slot is not { } slot) return new(ContentOperationStatus.NotEquippable, "动作请点击播放，不会加入待机。", definition);
        if (!Owns(candidate, contentId)) return new(ContentOperationStatus.NotOwned, "先解锁这件内容再装备吧。", definition);
        if (!isAvailable(definition)) return new(ContentOperationStatus.Unavailable, "资源暂时不可用，已拥有记录仍会保留。", definition);
        string current = Requested(candidate, slot);
        if (current == contentId)
        {
            bool canceled = candidate.PendingEquipment.Remove(slot);
            return new(canceled ? ContentOperationStatus.Equipped : ContentOperationStatus.NoChange,
                canceled ? "已取消待生效的更换，继续使用当前装备。" : "已经装备了这一件。", definition);
        }
        if (deferUntilSafeBoundary)
        {
            if (candidate.PendingEquipment.TryGetValue(slot, out string? pending) && pending == contentId)
                return new(ContentOperationStatus.NoChange, "已经在等待本次动作结束后更换。", definition);
            candidate.PendingEquipment[slot] = contentId;
            return new(ContentOperationStatus.Pending, "已选好，当前动作结束后生效。", definition);
        }
        candidate.Equipped[slot] = contentId;
        candidate.PendingEquipment.Remove(slot);
        return new(ContentOperationStatus.Equipped, "换好啦。", definition);
    }

    /// <summary>
    /// Call only at the renderer/controller's safe boundary, never halfway through
    /// an authored action. Missing-resource requests remain pending for a later try.
    /// </summary>
    public static IReadOnlyList<ContentOperationResult> ApplyPendingAtSafeBoundary(ContentOwnershipState candidate,
        Func<ContentDefinition, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        var results = new List<ContentOperationResult>();
        foreach (var slot in Slots)
        {
            if (!candidate.PendingEquipment.TryGetValue(slot, out string? id)) continue;
            if (!ContentCatalog.TryGet(id, out var definition) || definition!.Slot != slot)
            {
                results.Add(new(ContentOperationStatus.Unavailable, "待生效的内容暂不可用，选择和所有权已保留。"));
                continue;
            }
            results.Add(RequestEquip(candidate, id, deferUntilSafeBoundary: false, isAvailable));
        }
        return results.AsReadOnly();
    }

    /// <summary>Read-only render fallback: never replace the saved choice or remove ownership.</summary>
    public static ContentResolution ResolveEquipped(ContentOwnershipState state, ContentSlot slot,
        Func<ContentDefinition, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        string fallback = ContentCatalog.DefaultForSlot(slot);
        string requested = Requested(state, slot);
        if (ContentCatalog.TryGet(requested, out var definition) && definition!.Slot == slot &&
            Owns(state, requested) && isAvailable(definition))
            return new(slot, requested, requested, false, null);
        bool fallbackAvailable = isAvailable(ContentCatalog.Get(fallback));
        return new(slot, requested, fallbackAvailable ? fallback : null, true,
            fallbackAvailable ? "所选资源暂不可用，正在使用默认外观；已拥有内容和选择均已保留。"
                : "默认资源也暂不可用，请修复安装；已拥有内容和选择均已保留。");
    }

    public static ContentOperationResult CanPlayAction(ContentOwnershipState state, string contentId,
        Func<ContentDefinition, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        if (!ContentCatalog.TryGet(contentId, out var definition)) return new(ContentOperationStatus.UnknownContent, "没有找到这个动作。");
        if (definition!.PlaybackMode != ContentPlaybackMode.ManualOnly) return new(ContentOperationStatus.NotEquippable, "这不是可播放的动作。", definition);
        if (!Owns(state, contentId)) return new(ContentOperationStatus.NotOwned, "先解锁这个动作吧。", definition);
        if (!isAvailable(definition)) return new(ContentOperationStatus.Unavailable, "动作资源暂未就绪，已拥有记录会保留。", definition);
        return new(ContentOperationStatus.ReadyToPlay, "可以主动播放，待机动作保持不变。", definition);
    }

    private static string Requested(ContentOwnershipState state, ContentSlot slot) =>
        state.Equipped.TryGetValue(slot, out string? id) ? id : ContentCatalog.DefaultForSlot(slot);

    private static int ReservedAutomaticGrants(ContentOwnershipState state) => ContentCatalog.Definitions.Count(
        x => (x.IsDefault || x.Reward is not null) && !state.OwnedContentIds.Contains(x.Id));

    private static void CopySelections(Dictionary<ContentSlot, string>? source, Dictionary<ContentSlot, string> target)
    {
        if (source is null) return;
        foreach (var entry in source)
            if (Enum.IsDefined(entry.Key) && IsValidId(entry.Value)) target[entry.Key] = entry.Value;
    }

    private static bool IsValidId(string? id) => id is { Length: >= 1 and <= 64 } &&
        id[0] is >= 'a' and <= 'z' && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
}
