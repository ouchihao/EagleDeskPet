using System.IO;

namespace DuckDeskPet.Core;

/// <summary>Local wall-clock reminders. No networking, animation or pet-economy state.</summary>
public sealed record LocalReminder
{
    public string Id { get; init; } = "";
    public string Message { get; init; } = "";
    public int Minutes { get; init; }
    public bool Repeat { get; init; }
    public bool IsPaused { get; init; }
    public bool IsCompleted { get; init; }
    public bool IsPending { get; init; }
    public DateTimeOffset? DueAtUtc { get; init; }
    public long PausedRemainingTicks { get; init; }
    public DateTimeOffset? LastNotifiedUtc { get; init; }

    public TimeSpan Remaining(DateTimeOffset now) => IsCompleted ? TimeSpan.Zero : IsPaused
        ? TimeSpan.FromTicks(PausedRemainingTicks)
        : TimeSpan.FromTicks(Math.Max(0, (DueAtUtc!.Value - now).Ticks));
}

public sealed record ReminderSnapshot
{
    public int Version { get; init; } = 1;
    public List<LocalReminder> Items { get; init; } = new();
}

public sealed class ReminderScheduler
{
    public const int MaximumReminders = 8;
    public const int MaximumMinutes = 7 * 24 * 60;
    public const int MaximumMessageLength = 120;
    private readonly List<LocalReminder> _items;
    public IReadOnlyList<LocalReminder> Items => _items.ToArray();
    public bool HasPending => _items.Any(item => item.IsPending);

    public ReminderScheduler(ReminderSnapshot? snapshot = null)
    {
        snapshot ??= new();
        Validate(snapshot);
        _items = snapshot.Items.ToList();
    }

    public ReminderSnapshot Snapshot() => new() { Items = _items.ToList() };

    public LocalReminder Add(string message, int minutes, bool repeat, DateTimeOffset now)
    {
        message = message?.Trim() ?? "";
        if (_items.Count >= MaximumReminders) throw new InvalidOperationException("最多保存 8 条提醒，请先删除不需要的条目。");
        if (!ValidMessage(message)) throw new ArgumentException("提醒内容需要 1–120 个字符，不能包含换行或控制字符。");
        if (minutes is < 1 or > MaximumMinutes) throw new ArgumentException("请输入 1–10080 之间的整数分钟。");
        var item = new LocalReminder { Id = Guid.NewGuid().ToString("N"), Message = message,
            Minutes = minutes, Repeat = repeat, DueAtUtc = NextDue(now, minutes) };
        _items.Add(item);
        return item;
    }

    public bool Remove(string id) => _items.RemoveAll(item => item.Id == id) > 0;

    public bool TogglePause(string id, DateTimeOffset now)
    {
        int index = _items.FindIndex(item => item.Id == id);
        if (index < 0) return false;
        var item = _items[index];
        if (item.IsCompleted)
            _items[index] = item with { IsCompleted = false, IsPending = false, IsPaused = false,
                DueAtUtc = NextDue(now, item.Minutes), PausedRemainingTicks = 0 };
        else if (item.IsPaused)
            _items[index] = item with { IsPaused = false, DueAtUtc = now.ToUniversalTime().AddTicks(item.PausedRemainingTicks), PausedRemainingTicks = 0 };
        else
            _items[index] = item with { IsPaused = true, IsPending = false, DueAtUtc = null,
                PausedRemainingTicks = Math.Clamp(item.Remaining(now).Ticks, 0, TimeSpan.FromMinutes(item.Minutes).Ticks) };
        return true;
    }

    /// <summary>After a long absence, each reminder accrues at most one pending message.
    /// Repeating timers restart from now; missed historical intervals are never replayed.</summary>
    public bool Advance(DateTimeOffset now)
    {
        bool changed = false;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            if (item.IsPaused || item.IsCompleted || item.DueAtUtc > now) continue;
            _items[i] = item with { IsPending = true, IsCompleted = !item.Repeat,
                DueAtUtc = item.Repeat ? NextDue(now, item.Minutes) : null };
            changed = true;
        }
        return changed;
    }

    /// <summary>The caller persists this candidate before showing one coalesced bubble.</summary>
    public IReadOnlyList<LocalReminder> AcknowledgePending(DateTimeOffset now)
    {
        var pending = _items.Where(item => item.IsPending).ToArray();
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].IsPending) _items[i] = _items[i] with { IsPending = false, LastNotifiedUtc = now.ToUniversalTime() };
        return pending;
    }

    public static string BubbleMessage(IReadOnlyList<LocalReminder> pending)
    {
        if (pending.Count == 0) return "";
        if (pending.Count == 1) return "时间到啦：" + pending[0].Message;
        string Brief(string text) => text.Length <= 42 ? text : text[..41] + "…";
        string first = string.Join("；", pending.Take(2).Select(item => Brief(item.Message)));
        return $"有 {pending.Count} 件事到点啦：{first}" +
            (pending.Count > 2 ? "。其余请在右键「定时提醒」查看。" : "。");
    }

    public static void Validate(ReminderSnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Items is null || snapshot.Items.Count > MaximumReminders)
            throw new InvalidDataException("Unsupported or oversized reminder state.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            if (item is null || !Guid.TryParseExact(item.Id, "N", out _) || !ids.Add(item.Id) ||
                !ValidMessage(item.Message) || item.Message != item.Message.Trim() ||
                item.Minutes is < 1 or > MaximumMinutes || item.PausedRemainingTicks < 0 ||
                item.PausedRemainingTicks > TimeSpan.FromMinutes(item.Minutes).Ticks ||
                (item.IsCompleted && (item.Repeat || item.IsPaused)) ||
                ((item.IsCompleted || item.IsPaused) != (item.DueAtUtc is null)) ||
                (item.IsPaused && item.IsPending) || (!item.IsPaused && item.PausedRemainingTicks != 0) ||
                (item.DueAtUtc is { } due && due.Offset != TimeSpan.Zero) ||
                (item.LastNotifiedUtc is { } last && last.Offset != TimeSpan.Zero))
                throw new InvalidDataException("Invalid local reminder state.");
        }
    }

    private static bool ValidMessage(string? text) => !string.IsNullOrWhiteSpace(text) &&
        text.Length <= MaximumMessageLength && !text.Any(char.IsControl);

    private static DateTimeOffset NextDue(DateTimeOffset now, int minutes) => now.ToUniversalTime().AddMinutes(minutes);
}
