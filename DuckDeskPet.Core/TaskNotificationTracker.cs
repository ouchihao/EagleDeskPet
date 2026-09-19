using System.Globalization;

namespace DuckDeskPet.Core;

public enum PetTaskStatus { Running, Waiting, ReplyReady, Succeeded, Failed, Unknown }

public sealed record TaskNotificationItem(string Source, string TaskId, long Revision, PetTaskStatus Status,
    string Message, string EventId, DateTimeOffset UpdatedAt, DateTimeOffset OccurredAt, bool IsUnread)
{
    public string StatusLabel => Status switch
    {
        PetTaskStatus.Running => "进行中", PetTaskStatus.Waiting => "等待你处理",
        PetTaskStatus.ReplyReady => "回复就绪", PetTaskStatus.Succeeded => "来源报告成功",
        PetTaskStatus.Failed => "来源报告失败", _ => "状态未知 · 已过期"
    };
    public bool IsTerminal => Status is PetTaskStatus.Succeeded or PetTaskStatus.Failed;
}

public sealed record TaskNotificationResult(bool Accepted, bool Changed, string Outcome, TaskNotificationItem? Item = null);

/// <summary>
/// UI-thread-owned, memory-only task inbox. Revision orders events, never elapsed
/// time or a model's prose. Stop hooks without task identity use the legacy path.
/// Caps and TTLs deliberately bound deduplication; this is not a durable task log.
/// </summary>
public sealed class TaskNotificationTracker
{
    public const int MaximumTasks = 64;
    public const int MaximumEventIds = 512;
    public const int MaximumRetiredTasks = 512;
    public const int MaximumPending = 8;
    public const long MaximumRevision = 9_007_199_254_740_991;
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    public static readonly TimeSpan AnnouncementLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AnnouncementInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReplayAge = TimeSpan.FromMinutes(2);
    private readonly Dictionary<TaskKey, Entry> _items = new();
    private readonly Dictionary<EventKey, DateTimeOffset> _events = new();
    private readonly Dictionary<TaskKey, Watermark> _retired = new();
    private readonly Queue<DateTimeOffset> _announced = new();
    private DateTimeOffset _clock = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastAnnouncement;

    public bool Enabled { get; private set; } = true;
    public bool IsMuted { get; private set; }
    public int RetainedTaskCount => _items.Count;
    public int RetainedEventCount => _events.Count;
    public int RetiredTaskCount => _retired.Count;

    public static bool TryParseStatus(string? value, out PetTaskStatus status)
    {
        status = value switch
        {
            "running" => PetTaskStatus.Running, "waiting" => PetTaskStatus.Waiting,
            "reply_ready" => PetTaskStatus.ReplyReady, "succeeded" => PetTaskStatus.Succeeded,
            "failed" => PetTaskStatus.Failed, _ => PetTaskStatus.Unknown
        };
        return status != PetTaskStatus.Unknown;
    }

    public TaskNotificationResult Apply(PetNotification value, DateTimeOffset now)
    {
        now = Advance(now);
        if (!Safe(value.Source, 32) || !Safe(value.TaskId, 128) || !Safe(value.EventId, 128) ||
            !Safe(value.Message, 240, true) || value.Revision is null or < 0 or > MaximumRevision ||
            !TryParseStatus(value.Status, out var status))
            return new(false, false, "invalid");
        if (!Enabled) return new(true, false, "disabled");
        var occurredAt = value.OccurredAt ?? now;
        if (occurredAt > now.AddMinutes(2)) return new(false, false, "future_event");
        if (now - occurredAt >= Retention) return new(true, false, "expired_event");
        var key = new TaskKey(value.Source, value.TaskId!);
        var eventKey = new EventKey(key, value.EventId!);
        if (_events.ContainsKey(eventKey)) return new(true, false, "duplicate");
        _items.TryGetValue(key, out var previous);
        _retired.TryGetValue(key, out var retired);
        long? previousRevision = previous?.Item.Revision ?? retired?.Revision;
        PetTaskStatus? previousStatus = previous?.ReportedStatus ?? retired?.Status;
        if (previousRevision is not null && value.Revision <= previousRevision)
            return new(true, false, "stale_revision", previous is { Hidden: false } ? previous.Item : null);
        if (previousStatus is { } prior && IsTerminal(prior) && status != prior)
            return new(true, false, "terminal_locked", previous is { Hidden: false } ? previous.Item : null);

        bool isReplay = value.IsReplay || now - occurredAt > ReplayAge;
        bool importantChange = status != PetTaskStatus.Running &&
            (previous is null || previous.Hidden || previous.Item.Status == PetTaskStatus.Unknown || previous.ReportedStatus != status);
        bool expired = !IsTerminal(status) && now - occurredAt >= StateLifetime;
        var item = new TaskNotificationItem(value.Source, value.TaskId!, value.Revision.Value,
            expired ? PetTaskStatus.Unknown : status, expired ? "" : value.Message.Trim(), value.EventId!,
            now, occurredAt, !expired && (previous?.Item.IsUnread == true || (!isReplay && importantChange)));
        if (previous is null && _items.Count >= MaximumTasks)
        {
            var oldest = _items.OrderBy(pair => pair.Value.Item.UpdatedAt).ThenBy(pair => pair.Key.Source, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Id, StringComparer.Ordinal).First();
            if (_retired.Count >= MaximumRetiredTasks) _retired.Remove(_retired.MinBy(pair => pair.Value.UpdatedAt).Key);
            _retired[oldest.Key] = new(oldest.Value.Item.Revision, oldest.Value.ReportedStatus, oldest.Value.Item.UpdatedAt);
            _items.Remove(oldest.Key);
        }
        bool pending = !expired && !isReplay && !IsMuted && importantChange;
        // A same-status text refresh may replace a still-pending announcement,
        // but must not extend its original deadline indefinitely.
        if (!pending && !expired && !isReplay && !IsMuted && previous?.PendingSince is not null &&
            previous.ReportedStatus == status) pending = true;
        var entry = new Entry(item, status, pending ? previous?.PendingSince ?? now : null, false);
        _items[key] = entry;
        _retired.Remove(key);
        if (_events.Count >= MaximumEventIds) _events.Remove(_events.MinBy(pair => pair.Value).Key);
        _events[eventKey] = now;
        while (_items.Values.Count(candidate => candidate.PendingSince is not null) > MaximumPending)
        {
            var oldest = _items.Where(pair => pair.Value.PendingSince is not null)
                .OrderBy(pair => pair.Value.PendingSince).ThenBy(pair => pair.Key.Source, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Id, StringComparer.Ordinal).First();
            _items[oldest.Key] = oldest.Value with { PendingSince = null };
        }
        return new(true, true, isReplay ? "replay_suppressed" : "updated", item);
    }

    public IReadOnlyList<TaskNotificationItem> GetItems(DateTimeOffset now)
    {
        Advance(now);
        return _items.Values.Where(entry => !entry.Hidden).Select(entry => entry.Item)
            .OrderByDescending(item => item.IsUnread).ThenByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.Source, StringComparer.Ordinal).ThenBy(item => item.TaskId, StringComparer.Ordinal).ToArray();
    }

    public TaskNotificationItem? TakeAnnouncement(DateTimeOffset now)
    {
        now = Advance(now);
        if (!Enabled || IsMuted || _announced.Count >= 6 ||
            (_lastAnnouncement is { } previous && now - previous < AnnouncementInterval)) return null;
        var candidate = _items.Where(pair => !pair.Value.Hidden && pair.Value.PendingSince is not null && pair.Value.Item.IsUnread)
            .OrderBy(pair => pair.Value.Item.Status is PetTaskStatus.Waiting or PetTaskStatus.Failed ? 0 : 1)
            .ThenBy(pair => pair.Value.PendingSince).ThenBy(pair => pair.Key.Source, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Id, StringComparer.Ordinal).FirstOrDefault();
        if (candidate.Value is null) return null;
        _items[candidate.Key] = candidate.Value with { PendingSince = null };
        _lastAnnouncement = now;
        _announced.Enqueue(now);
        return candidate.Value.Item;
    }

    public void MarkRead(string source, string taskId)
    {
        var key = new TaskKey(source, taskId);
        if (_items.TryGetValue(key, out var entry))
            _items[key] = entry with { Item = entry.Item with { IsUnread = false }, PendingSince = null };
    }

    public void MarkAllRead()
    {
        foreach (var key in _items.Keys.ToArray()) MarkRead(key.Source, key.Id);
    }

    public void AcknowledgeAnnouncement(string source, string taskId)
    {
        var key = new TaskKey(source, taskId);
        if (_items.TryGetValue(key, out var entry)) _items[key] = entry with { PendingSince = null };
    }

    public void DiscardAnnouncements()
    {
        foreach (var key in _items.Keys.ToArray()) _items[key] = _items[key] with { PendingSince = null };
    }

    public void SetMuted(bool muted) { IsMuted = muted; DiscardAnnouncements(); }
    public void SetEnabled(bool enabled) { Enabled = enabled; DiscardAnnouncements(); }

    public void Clear()
    {
        // Keep only bounded revision/event tombstones to stop recent retries
        // from restoring cleared messages. No notification body survives Clear.
        foreach (var key in _items.Keys.ToArray())
            _items[key] = _items[key] with { Item = _items[key].Item with { Message = "", IsUnread = false }, Hidden = true, PendingSince = null };
    }

    private DateTimeOffset Advance(DateTimeOffset now)
    {
        // A backwards wall-clock adjustment must not resurrect expired entries.
        if (now < _clock) now = _clock;
        _clock = now;
        foreach (var key in _events.Where(pair => now - pair.Value >= Retention).Select(pair => pair.Key).ToArray()) _events.Remove(key);
        foreach (var key in _retired.Where(pair => now - pair.Value.UpdatedAt >= Retention).Select(pair => pair.Key).ToArray()) _retired.Remove(key);
        while (_announced.TryPeek(out var time) && now - time >= TimeSpan.FromMinutes(1)) _announced.Dequeue();
        foreach (var key in _items.Keys.ToArray())
        {
            var entry = _items[key];
            if (now - entry.Item.UpdatedAt >= Retention) { _items.Remove(key); continue; }
            if (!IsTerminal(entry.ReportedStatus) && now - entry.Item.OccurredAt >= StateLifetime)
                entry = entry with { Item = entry.Item with { Status = PetTaskStatus.Unknown, Message = "", IsUnread = false }, PendingSince = null };
            else if (entry.PendingSince is { } pending && now - pending >= AnnouncementLifetime)
                entry = entry with { PendingSince = null };
            _items[key] = entry;
        }
        return now;
    }

    private static bool IsTerminal(PetTaskStatus status) => status is PetTaskStatus.Succeeded or PetTaskStatus.Failed;
    private static bool Safe(string? value, int maximum, bool allowEmpty = false) => value is not null && value.Length <= maximum &&
        (allowEmpty || !string.IsNullOrWhiteSpace(value)) && !value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format);
    private readonly record struct TaskKey(string Source, string Id);
    private readonly record struct EventKey(TaskKey Task, string Event);
    private sealed record Entry(TaskNotificationItem Item, PetTaskStatus ReportedStatus, DateTimeOffset? PendingSince, bool Hidden);
    private sealed record Watermark(long Revision, PetTaskStatus Status, DateTimeOffset UpdatedAt);
}
