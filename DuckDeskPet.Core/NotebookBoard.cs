using System.IO;

namespace DuckDeskPet.Core;

/// <summary>Local notes are not pet progress, timed reminders, or AI messages</summary>
public sealed record NotebookNote(string Id, string Text, DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc, DateTimeOffset? CompletedUtc = null)
{
    public bool IsCompleted => CompletedUtc.HasValue;
}

public sealed record NotebookSnapshot
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<NotebookNote> Notes { get; init; } = Array.Empty<NotebookNote>();
}

public readonly record struct NotebookChange(NotebookSnapshot Snapshot, bool Changed);

/// <summary>Pure candidate-state operations; the caller persists before publishing a change</summary>
public static class NotebookBoard
{
    // Includes archived notes; never silently trim somebody's completed history
    public const int MaximumNotes = 256;
    public const int MaximumTextLength = 1000;

    public static void Validate(NotebookSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Version != 1 || snapshot.Notes is null || snapshot.Notes.Count > MaximumNotes)
            throw new InvalidDataException("Unsupported or oversized notebook.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in snapshot.Notes)
        {
            if (note is null || !ValidId(note.Id) || !ids.Add(note.Id) || !ValidText(note.Text) ||
                note.Text != NormalizeText(note.Text) || !ValidTime(note.CreatedUtc) || !ValidTime(note.UpdatedUtc) ||
                note.UpdatedUtc < note.CreatedUtc || (note.CompletedUtc is { } completed &&
                    (!ValidTime(completed) || completed < note.CreatedUtc || completed > note.UpdatedUtc)))
                throw new InvalidDataException("Invalid notebook entry.");
        }
    }

    public static NotebookSnapshot Copy(NotebookSnapshot snapshot)
    {
        Validate(snapshot);
        return new() { Notes = Array.AsReadOnly(snapshot.Notes.ToArray()) };
    }

    public static NotebookChange Add(NotebookSnapshot snapshot, string id, string text, DateTimeOffset now)
    {
        Validate(snapshot);
        if (!ValidId(id)) throw new ArgumentException("无效的便签编号。", nameof(id));
        text = CheckedText(text);
        // An add attempt keeps its ID until persistence succeeds; retrying it is idempotent
        var existing = snapshot.Notes.FirstOrDefault(x => x.Id == id);
        if (existing is not null)
        {
            if (existing.Text != text) throw new InvalidOperationException("便签编号已存在，请重新创建。");
            return new(snapshot, false);
        }
        if (snapshot.Notes.Count >= MaximumNotes) throw new InvalidOperationException("已保存 256 张便签（含已完成），请先删除不需要的条目。");
        now = CheckedTime(now);
        return Changed(snapshot.Notes.Append(new NotebookNote(id, text, now, now)));
    }

    public static NotebookChange Edit(NotebookSnapshot snapshot, string id, string text, DateTimeOffset now)
    {
        Validate(snapshot);
        text = CheckedText(text);
        var note = Find(snapshot, id);
        if (note.Text == text) return new(snapshot, false);
        now = Later(now, note.UpdatedUtc);
        return Changed(snapshot.Notes.Select(x => x.Id == id ? x with { Text = text, UpdatedUtc = now } : x));
    }

    public static NotebookChange SetCompleted(NotebookSnapshot snapshot, string id, bool completed, DateTimeOffset now)
    {
        Validate(snapshot);
        var note = Find(snapshot, id);
        if (note.IsCompleted == completed) return new(snapshot, false);
        now = Later(now, note.UpdatedUtc);
        return Changed(snapshot.Notes.Select(x => x.Id == id
            ? x with { CompletedUtc = completed ? now : null, UpdatedUtc = now } : x));
    }

    public static NotebookChange Delete(NotebookSnapshot snapshot, string id)
    {
        Validate(snapshot);
        // Repeated confirmed deletion does not affect a neighboring note
        return snapshot.Notes.Any(x => x.Id == id) ? Changed(snapshot.Notes.Where(x => x.Id != id)) : new(snapshot, false);
    }

    private static NotebookNote Find(NotebookSnapshot snapshot, string id) =>
        snapshot.Notes.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("这张便签已不存在，请重新打开查看。");
    private static NotebookChange Changed(IEnumerable<NotebookNote> notes) =>
        new(new() { Notes = Array.AsReadOnly(notes.ToArray()) }, true);
    private static bool ValidId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out var guid) && guid != Guid.Empty && id == id.ToLowerInvariant();
    private static bool ValidTime(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value >= DateTimeOffset.UnixEpoch;
    private static DateTimeOffset CheckedTime(DateTimeOffset value)
    {
        value = value.ToUniversalTime();
        if (!ValidTime(value)) throw new ArgumentException("无法使用这个日期。");
        return value;
    }
    private static DateTimeOffset Later(DateTimeOffset now, DateTimeOffset previous) => CheckedTime(now) > previous ? now.ToUniversalTime() : previous;
    private static string NormalizeText(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    private static bool ValidText(string? text) => text is not null && ValidUnicodeText(text);
    private static string CheckedText(string text)
    {
        if (text is null) throw new ArgumentException("先写下一件要做的事吧。");
        text = NormalizeText(text);
        // Paired surrogate characters (emoji) are valid; lone surrogates are not
        if (!ValidUnicodeText(text)) throw new ArgumentException("便签需要 1–1000 个字符，不能包含无效或控制字符。");
        return text;
    }
    private static bool ValidUnicodeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength) return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c)) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(c) || (char.IsControl(c) && c != '\n' && c != '\t')) return false;
        }
        return true;
    }
}
