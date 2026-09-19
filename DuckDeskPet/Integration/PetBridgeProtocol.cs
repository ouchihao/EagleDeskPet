using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuckDeskPet.Integration;

public sealed record PetNotification(string Source, string EventId, string? SessionId, string Message, string EventType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TaskId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Revision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Status = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? OccurredAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsReplay = false);

public sealed record PetBridgeRequest(string Operation, PetNotification? Notification = null);

public sealed record PetBridgeReply(bool Accepted, string Status, string Message, object? State = null);

public static class PetBridgeProtocol
{
    public const int MaxPacketBytes = 16 * 1024;
    public const int MaxMessageCharacters = 240;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // Opt-in integration tests use their own pipe AND GUI mutex. A malformed
    // channel must fail closed, never silently connect to the user's live pet.
    public static string TestChannelSuffix
    {
        get
        {
            string? channel = Environment.GetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL");
            if (channel is null) return "";
            if (channel.Length is < 1 or > 64 || channel.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new InvalidOperationException("EAGLE_PET_TEST_CHANNEL must contain 1-64 ASCII letters, digits, or hyphens.");
            return ".test." + channel;
        }
    }

    public static string PipeName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new InvalidOperationException("Windows user SID is unavailable.");
            return "EagleDeskPet.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid + TestChannelSuffix)))[..20];
        }
    }

    public static bool IsSafeText(string? value, int maximum, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value))) return false;
        // Do not let untrusted display text disguise the source label with control / bidi characters.
        return !value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);
    }

    public static string? Validate(PetNotification? value)
    {
        if (value is null) return "Notification is required.";
        if (!IsSafeText(value.Source, 32)) return "Source must contain 1-32 printable characters.";
        if (!IsSafeText(value.EventId, 128)) return "eventId must contain 1-128 printable characters.";
        if (value.SessionId is not null && !IsSafeText(value.SessionId, 128, true)) return "sessionId is too long or contains control characters.";
        if (!IsSafeText(value.Message, MaxMessageCharacters, true)) return "message must contain at most 240 printable characters.";
        bool task = value.TaskId is not null || value.Revision is not null || value.Status is not null;
        if (task)
        {
            if (!IsSafeText(value.TaskId, 128) || value.Revision is null or < 0 or > 9_007_199_254_740_991)
                return "Task updates require taskId (1-128 printable characters) and an integer revision (0-9007199254740991).";
            var eventType = TaskEventType(value.Status);
            if (eventType is null) return "Task updates require status: running, waiting, reply_ready, succeeded, or failed.";
            if (!string.IsNullOrEmpty(value.EventType) && value.EventType != eventType)
                return "eventType conflicts with task status.";
            if (value.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(2)) return "occurredAt is more than two minutes in the future.";
        }
        else
        {
            if (value.EventType is not ("reply_ready" or "needs_attention" or "task_failed")) return "Unsupported eventType.";
            if (value.OccurredAt is not null || value.IsReplay) return "occurredAt and isReplay require a task update.";
        }
        return null;
    }

    public static string? TaskEventType(string? status) => status switch
    {
        "running" => "task_running", "waiting" => "needs_attention", "reply_ready" => "reply_ready",
        "succeeded" => "task_succeeded", "failed" => "task_failed", _ => null
    };

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxPacketBytes) throw new InvalidDataException("Bridge packet exceeds the size limit.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Empty bridge packet.");
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length is <= 0 or > MaxPacketBytes) throw new InvalidDataException("Bridge packet exceeds the size limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
