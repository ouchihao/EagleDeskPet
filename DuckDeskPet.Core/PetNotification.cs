using System.Text;

namespace DuckDeskPet.Core;

/// <summary>
/// Display-only notification data. Source comes from the configured adapter, not
/// a model guess. No arbitrary command or launch target is carried by this type.
/// </summary>
public sealed record PetNotification(string Source, string Message, string? EventId = null, string? SessionId = null,
    string? TaskId = null, long? Revision = null, string? Status = null,
    DateTimeOffset? OccurredAt = null, bool IsReplay = false)
{
    public static PetNotification Create(string? source, string? message, string? eventId = null, string? sessionId = null) =>
        new(
            Clean(source, 40, "AI"),
            Clean(message, 180, "有新回复了，快去看看。"),
            Optional(eventId, 128),
            Optional(sessionId, 128));

    private static string? Optional(string? value, int length)
    {
        string result = Clean(value, length, string.Empty);
        return result.Length == 0 ? null : result;
    }

    private static string Clean(string? value, int maximumLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var builder = new StringBuilder();
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                continue;
            }

            if (builder.Length + rune.Utf16SequenceLength > maximumLength)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        string result = builder.ToString().Trim();
        return result.Length == 0 ? fallback : result;
    }
}
