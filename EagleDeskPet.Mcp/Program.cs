using System.Text.Json;
using DuckDeskPet.Integration;
using EagleDeskPet.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args.Length > 0 && args[0] == "--hook") return await CompletionHook.RunAsync(args);

if (args.Length >= 3 && args[0] == "--source" && args[2] == "--notify")
{
    var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    var validNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["--event-id"] = "eventId", ["--session-id"] = "sessionId", ["--message"] = "message", ["--event-type"] = "eventType",
        ["--task-id"] = "taskId", ["--revision"] = "revision", ["--status"] = "status", ["--occurred-at"] = "occurredAt", ["--replay"] = "isReplay"
    };
    for (var index = 3; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length || !validNames.TryGetValue(args[index], out var key) || values.ContainsKey(key))
        {
            Console.Error.WriteLine("Invalid notification arguments. Use --source Name --notify --event-id ID with --event-type reply_ready, or --task-id ID --revision N --status running|waiting|reply_ready|succeeded|failed. Optional: --message, --session-id, --occurred-at RFC3339, --replay true|false.");
            return 2;
        }
        string value = args[index + 1];
        if (key == "revision")
        {
            if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long number))
            { Console.Error.WriteLine("revision must be a nonnegative integer."); return 2; }
            values[key] = JsonSerializer.SerializeToElement(number);
        }
        else if (key == "isReplay")
        {
            if (value is not ("true" or "false")) { Console.Error.WriteLine("replay must be true or false."); return 2; }
            values[key] = JsonSerializer.SerializeToElement(value == "true");
        }
        else values[key] = JsonSerializer.SerializeToElement(value);
    }
    if (!NotificationArguments.TryCreate(args[1].Trim(), values, out var notification, out var error))
    { Console.Error.WriteLine(error); return 2; }
    var reply = await new PetBridgeClient().SendAsync(new("notify", notification));
    Console.WriteLine(JsonSerializer.Serialize(reply, PetBridgeProtocol.JsonOptions));
    return reply.Accepted ? 0 : 1;
}

if (args.Length != 2 || args[0] != "--source" || !PetBridgeProtocol.IsSafeText(args[1], 32))
{
    Console.Error.WriteLine("Usage: EagleDeskPet.Mcp.exe --source <AI application display name, 1-32 printable characters>. Managed completion hooks use --hook stop|prompt --client Codex|ClaudeCode|CodeBuddyCode --owner eagle-desktop-pet-v1.");
    return 2;
}

var source = args[1].Trim();
var bridge = new PetBridgeClient();
var tools = new Tool[]
{
    new()
    {
        Name = "pet_notify",
        Description = "Send a short local eagle notification or update one task. Source is fixed by the user's connection configuration. Legacy calls use eventType; task updates require taskId, revision and status together. Increase revision per task; reuse eventId for retries. Only report succeeded/failed when explicitly known, never infer success from a Stop or completed reply. Running is quiet. Set isReplay for history/reconnect imports. Do not send secrets or full transcripts; this is not an application monitor.",
        InputSchema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type":"object",
              "properties":{
                "eventId":{"type":"string","minLength":1,"maxLength":128,"description":"Stable event ID; reuse for retries."},
                "sessionId":{"type":"string","maxLength":128,"description":"Optional opaque conversation ID, not a URL or command."},
                "message":{"type":"string","maxLength":240,"description":"Optional short plain-text summary, never a transcript."},
                "eventType":{"type":"string","enum":["reply_ready","needs_attention","task_failed","task_running","task_succeeded"],"description":"Legacy calls require reply_ready/needs_attention/task_failed. Omit for task updates, or match status exactly."},
                "taskId":{"type":"string","minLength":1,"maxLength":128,"description":"Opaque task ID unique within this source, including across sessions. Use a new ID for a new run after a terminal result."},
                "revision":{"type":"integer","minimum":0,"maximum":9007199254740991,"description":"Strictly increasing per source + taskId; do not reset on reconnect."},
                "status":{"type":"string","enum":["running","waiting","reply_ready","succeeded","failed"],"description":"Explicit source-reported status. reply_ready does not mean success."},
                "occurredAt":{"type":"string","format":"date-time","maxLength":40,"description":"Optional event time with Z or offset. Old events are silent; cannot determine an event's original age if omitted."},
                "isReplay":{"type":"boolean","description":"Set true for reconnect/history imports; updates inbox without announcements."}
              },
              "required":["eventId"],
              "anyOf":[{"required":["eventType"]},{"required":["taskId","revision","status"]}],
              "additionalProperties":false
            }
            """),
        Annotations = new() { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
    },
    new()
    {
        Name = "pet_get_state",
        Description = "Read local pet care state. Does not read conversation history, files, browser tabs, or any AI application's state.",
        InputSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{},"additionalProperties":false}"""),
        Annotations = new() { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false },
    },
};
var options = new McpServerOptions
{
    ServerInfo = new() { Name = "eagle-desktop-pet", Version = "1.8.0" },
    ServerInstructions = $"Your local connection display name is {source}. Notify only when useful to the user. A reply_ready event means a reply is ready, not that code was independently validated. Task states require explicit source evidence; a Stop is never success. The bounded task inbox is memory-only, not conversation memory or automatic app monitoring.",
    InitializationTimeout = TimeSpan.FromSeconds(30),
    Capabilities = new() { Tools = new() },
    Handlers = new()
    {
        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools }),
        CallToolHandler = async (request, token) =>
        {
            var parameters = request.Params;
            if (parameters is null) return Result(new(false, "invalid_request", "Missing tool parameters."));
            var arguments = parameters.Arguments;
            if (parameters.Name == "pet_get_state")
            {
                if (arguments?.Count > 0) return Result(new(false, "invalid_request", "pet_get_state takes no arguments."));
                return Result(await bridge.SendAsync(new("get_state"), token));
            }
            if (parameters.Name != "pet_notify") return Result(new(false, "unknown_tool", "Unknown pet tool."));
            if (!NotificationArguments.TryCreate(source, arguments, out var notification, out var error))
                return Result(new(false, "invalid_request", error));
            return Result(await bridge.SendAsync(new("notify", notification), token));
        },
    },
};

try
{
    await using var server = McpServer.Create(new StreamServerTransport(new BoundedStdinStream(Console.OpenStandardInput()),
        new BufferedStream(Console.OpenStandardOutput()), "eagle-desktop-pet"), options);
    await server.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // Protocol stdout must never contain logs or stack traces, including invalid-input failures.
    Console.Error.WriteLine($"Eagle MCP stopped: {ex.GetType().Name}. Check the client connection and input limits.");
    return 1;
}

static CallToolResult Result(PetBridgeReply reply) => new()
{
    IsError = !reply.Accepted,
    Content = [new TextContentBlock { Text = JsonSerializer.Serialize(reply, PetBridgeProtocol.JsonOptions) }],
};
