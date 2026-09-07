using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DuckDeskPet.Integration;

namespace EagleDeskPet.Mcp;

// This is a command-hook transport for the exact same notification operation as
// pet_notify, not an AI client, transcript watcher, or a shell evaluator.
internal static class CompletionHook
{
    internal const string Owner = "eagle-desktop-pet-v1";
    internal const int MaximumInputBytes = 256 * 1024;

    internal static async Task<int> RunAsync(string[] args)
    {
        // A hook must never block or continue the model. Even malformed payload,
        // unavailable pet and full queues produce the neutral JSON contract.
        try
        {
            if (args.Length != 6 || args[0] != "--hook" || args[1] is not ("stop" or "prompt") ||
                args[2] != "--client" || Source(args[3]) is null || args[4] != "--owner" || args[5] != Owner)
                return 0;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var input = await ReadInputAsync(Console.OpenStandardInput(), deadline.Token);
            var rootDirectory = StateDirectory();
            var notice = ParseAndPrepare(args[3], args[1], input, rootDirectory);
            if (notice is not null)
                _ = await new PetBridgeClient().SendAsync(new("notify", notice), deadline.Token);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            // No raw payload, answer text, source paths or exception detail in
            // client logs. A missing pet never fails the coding session.
        }
        finally { Console.WriteLine("{}"); }
        return 0;
    }

    internal static async Task<byte[]> ReadInputAsync(Stream input, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            // WaitAsync is intentional: Windows console stdin streams may not
            // honor cancellation inside ReadAsync when a writer never closes.
            var length = await input.ReadAsync(buffer, token).AsTask().WaitAsync(token);
            if (length == 0) return result.ToArray();
            if (result.Length + length > MaximumInputBytes) throw new InvalidDataException("Hook input exceeds limit.");
            result.Write(buffer, 0, length);
        }
    }

    internal static PetNotification? ParseAndPrepare(string client, string kind, byte[] input, string stateDirectory)
    {
        var source = Source(client);
        if (source is null || input.Length > MaximumInputBytes) return null;
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.EnumerateObject().GroupBy(pair => pair.Name, StringComparer.Ordinal).Any(group => group.Count() > 1)) return null;
        var expected = kind == "prompt" ? "UserPromptSubmit" : "Stop";
        if (Text(root, "hook_event_name") != expected) return null;
        // Subagents do not get announced as a finished top-level user reply.
        if (root.TryGetProperty("agent_id", out var agent) && agent.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(agent.GetString())) return null;
        if (root.TryGetProperty("stop_hook_active", out var active) && active.ValueKind is not JsonValueKind.False and not JsonValueKind.Null) return null;
        var session = Text(root, "session_id");
        if (!PetBridgeProtocol.IsSafeText(session, 512)) return null;
        var sessionKey = Hash(client + "\0" + session);
        if (kind == "prompt")
        {
            if (client != "Codex") _ = Sequence(stateDirectory, sessionKey, increment: true);
            return null;
        }
        var turn = Text(root, "turn_id");
        string eventKey;
        if (client == "Codex")
        {
            if (!PetBridgeProtocol.IsSafeText(turn, 512)) return null;
            eventKey = Hash(sessionKey + "\0" + turn);
        }
        else
        {
            // Claude/CodeBuddy Stop input does not guarantee a turn ID. A tiny
            // UserPromptSubmit counter provides stable retries without reading
            // prompt/answer text or touching the supplied transcript_path.
            var sequence = Sequence(stateDirectory, sessionKey, increment: false);
            eventKey = Hash(sessionKey + "\0" + sequence);
        }
        return new(source, "hook-" + eventKey, sessionKey, "有新的回复了，快去看看。", "reply_ready");
    }

    private static string? Source(string value) => value switch { "Codex" => "Codex", "ClaudeCode" => "Claude Code", "CodeBuddyCode" => "CodeBuddy Code", _ => null };
    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string StateDirectory()
    {
        var suffix = PetBridgeProtocol.TestChannelSuffix;
        if (suffix.Length > 0)
        {
            var testRoot = Environment.GetEnvironmentVariable("EAGLE_PET_DATA_DIR");
            if (string.IsNullOrWhiteSpace(testRoot) || !Path.IsPathFullyQualified(testRoot))
                throw new InvalidOperationException("Hook tests require an isolated data directory.");
            return Path.Combine(testRoot, "hook-state");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EagleDeskPet", "hook-state");
    }

    private static string Sequence(string directory, string sessionKey, bool increment)
    {
        EnsureOrdinaryPath(directory);
        Directory.CreateDirectory(directory);
        // OS mutex + per-session file: concurrent Stop retries use one event ID.
        // Only a random turn token is stored, never session IDs or conversation.
        var mutexName = "Local\\EagleDeskPet.Hook." + Hash(directory + sessionKey)[..32];
        using var mutex = new Mutex(false, mutexName);
        var taken = false;
        try
        {
            try { taken = mutex.WaitOne(300); }
            catch (AbandonedMutexException) { taken = true; }
            if (!taken) throw new IOException("Hook state busy.");
            var path = Path.Combine(directory, sessionKey + ".txt");
            EnsureOrdinaryPath(path);
            string? current = null;
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 64) throw new InvalidDataException("Invalid hook state.");
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
                current = reader.ReadToEnd();
                if (current.Length != 32 || current.Any(character => !char.IsAsciiHexDigit(character))) throw new InvalidDataException("Invalid hook state.");
            }
            if (increment || current is null)
            {
                current = Guid.NewGuid().ToString("N");
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { stream.Write(Encoding.ASCII.GetBytes(current)); stream.Flush(true); }
                    EnsureOrdinaryPath(path);
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return current;
        }
        finally { if (taken) mutex.ReleaseMutex(); }
    }

    private static void EnsureOrdinaryPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("Invalid hook directory.");
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Hook state links are not supported.");
    }
}
