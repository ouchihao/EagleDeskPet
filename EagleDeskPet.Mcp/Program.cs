using System.Text.Json;
using DuckDeskPet.Integration;
using EagleDeskPet.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args.Length > 0 && args[0] == "--hook") return await CompletionHook.RunAsync(args);

if (args.Length >= 3 && args[0] == "--source" && args[2] == "--notify")
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var validNames = new HashSet<string> { "--event-id", "--session-id", "--message", "--event-type" };
    for (var index = 3; index < args.Length; index += 2)
    {
        if (index + 1 >= args.Length || !validNames.Contains(args[index]) || !values.TryAdd(args[index], args[index + 1]))
        {
            Console.Error.WriteLine("Invalid notification arguments. Use --source Name --notify --event-id ID --event-type reply_ready [--message Text] [--session-id ID].");
            return 2;
        }
    }
    var notification = new PetNotification(args[1].Trim(), values.GetValueOrDefault("--event-id", ""),
        values.GetValueOrDefault("--session-id"), values.GetValueOrDefault("--message", ""), values.GetValueOrDefault("--event-type", ""));
    var error = PetBridgeProtocol.Validate(notification);
    if (error is not null) { Console.Error.WriteLine(error); return 2; }
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
        Description = "Show a short notification on the local desktop eagle. Source is fixed by the user's connection configuration. Call only for a meaningful completed reply, a need for user attention, or a task failure; this does not monitor any application. Do not send secrets or full transcripts. Reuse eventId when retrying the same event.",
        InputSchema = JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"eventId":{"type":"string","minLength":1,"maxLength":128,"description":"Stable identifier for this event; reuse for retries."},"sessionId":{"type":"string","maxLength":128,"description":"Optional opaque conversation ID. Not a URL or command."},"message":{"type":"string","maxLength":240,"description":"Optional short plain-text summary, not a transcript."},"eventType":{"type":"string","enum":["reply_ready","needs_attention","task_failed"]}},"required":["eventId","eventType"],"additionalProperties":false}
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
    ServerInstructions = $"Your local connection display name is {source}. Notify only when useful to the user. A reply_ready event means a reply is ready, not that code was independently validated. No memory or automatic app monitoring is provided.",
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
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "eventId", "sessionId", "message", "eventType" };
            if (arguments is null || arguments.Any(p => !allowed.Contains(p.Key) || p.Value.ValueKind != JsonValueKind.String))
                return Result(new(false, "invalid_request", "Only the documented string arguments are accepted; source cannot be overridden."));
            string? Get(string key) => arguments.TryGetValue(key, out var value) ? value.GetString() : null;
            var notification = new PetNotification(source, Get("eventId") ?? "", Get("sessionId"), Get("message") ?? "", Get("eventType") ?? "");
            var error = PetBridgeProtocol.Validate(notification);
            if (error is not null) return Result(new(false, "invalid_request", error));
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
