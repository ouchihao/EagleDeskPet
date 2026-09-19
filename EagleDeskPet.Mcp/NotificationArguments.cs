using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDeskPet.Integration;

namespace EagleDeskPet.Mcp;

internal static class NotificationArguments
{
    private static readonly HashSet<string> StringFields = new(StringComparer.Ordinal)
        { "eventId", "sessionId", "message", "eventType", "taskId", "status", "occurredAt" };

    internal static bool TryCreate(string source, IEnumerable<KeyValuePair<string, JsonElement>>? arguments,
        out PetNotification notification, out string error)
    {
        notification = new(source, "", null, "", "");
        error = "Only documented notification arguments are accepted; source cannot be overridden.";
        if (arguments is null) return false;
        var values = arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        long? revision = null;
        bool replay = false;
        foreach (var (name, value) in values)
        {
            if (StringFields.Contains(name)) { if (value.ValueKind != JsonValueKind.String) return false; }
            else if (name == "revision")
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number)) return false;
                revision = number;
            }
            else if (name == "isReplay")
            {
                if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False) return false;
                replay = value.GetBoolean();
            }
            else return false;
        }
        string? Get(string name) => values.TryGetValue(name, out var value) ? value.GetString() : null;
        DateTimeOffset? occurredAt = null;
        if (Get("occurredAt") is { } timestamp)
        {
            if (!Regex.IsMatch(timestamp, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) ||
                !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            { error = "occurredAt must be an RFC3339 timestamp with Z or an explicit offset."; return false; }
            occurredAt = parsed;
        }
        notification = new(source, Get("eventId") ?? "", Get("sessionId"), Get("message") ?? "", Get("eventType") ?? "",
            Get("taskId"), revision, Get("status"), occurredAt, replay);
        error = PetBridgeProtocol.Validate(notification) ?? "";
        return error.Length == 0;
    }
}
