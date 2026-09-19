using System.Diagnostics;
using System.Text.Json;
using DuckDeskPet.Core;
using DuckDeskPet.Integration;
using EagleDeskPet.Mcp;
using TaskNotice = DuckDeskPet.Core.PetNotification;
using WireNotice = DuckDeskPet.Integration.PetNotification;

int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    passed++;
    Console.WriteLine("PASS " + description);
}

var now = new DateTimeOffset(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);
TaskNotice N(string task = "task-a", long revision = 1, string status = "running", string source = "Source A", string? id = null) =>
    new(source, "测试摘要", id ?? task + ":" + revision, null, task, revision, status);
WireNotice W(TaskNotice value) => new(value.Source, value.EventId!, value.SessionId, value.Message, "",
    value.TaskId, value.Revision, value.Status, value.OccurredAt, value.IsReplay);

var tracker = new TaskNotificationTracker();
Check(tracker.Apply(N(), now).Changed, "first running update accepted");
Check(tracker.GetItems(now).Count == 1 && tracker.TakeAnnouncement(now) is null && !tracker.GetItems(now)[0].IsUnread, "running is silent and read");
Check(tracker.Apply(N(revision: 2, status: "waiting"), now).Changed, "waiting updates existing task");
Check(tracker.GetItems(now).Count == 1 && tracker.GetItems(now)[0].IsUnread, "same task coalesces into one unread item");
Check(tracker.Apply(N(revision: 2, status: "waiting"), now).Outcome == "duplicate", "stable event retries deduplicated");
Check(tracker.Apply(N(revision: 1, status: "running", id: "late"), now).Outcome == "stale_revision", "older running revision cannot overwrite waiting");
Check(tracker.Apply(N(revision: 2, status: "failed", id: "conflict"), now).Outcome == "stale_revision", "equal revision conflicting payload ignored");
Check(tracker.Apply(N("task-b", status: "reply_ready"), now).Changed && tracker.Apply(N(source: "Source B", status: "waiting"), now).Changed && tracker.GetItems(now).Count == 3,
    "different tasks and sources coexist");
Check(tracker.TakeAnnouncement(now)?.Status == PetTaskStatus.Waiting, "attention prioritized");
Check(tracker.TakeAnnouncement(now) is null && tracker.TakeAnnouncement(now.AddSeconds(9)) is null, "global announcement interval enforced");
Check(tracker.TakeAnnouncement(now.AddSeconds(10)) is not null, "next announcement after interval");
tracker.MarkAllRead();
Check(tracker.GetItems(now).All(item => !item.IsUnread) && tracker.TakeAnnouncement(now.AddSeconds(20)) is null, "read all clears pending announcements");

var terminal = new TaskNotificationTracker();
terminal.Apply(N(status: "succeeded"), now);
Check(terminal.Apply(N(revision: 2), now).Outcome == "terminal_locked", "success never regresses to running even at higher revision");
Check(terminal.Apply(N(revision: 3, status: "failed"), now).Outcome == "terminal_locked", "terminal contradiction rejected");
Check(terminal.Apply(N(revision: 4, status: "succeeded") with { Message = "补充证据" }, now).Changed, "same terminal detail may advance revision");
Check(terminal.GetItems(now)[0].Message == "补充证据", "latest accepted summary retained");
Check(terminal.Apply(N("new-run"), now).Changed, "new run uses new task id");

var replay = new TaskNotificationTracker();
Check(replay.Apply(N(status: "waiting") with { IsReplay = true }, now).Outcome == "replay_suppressed", "explicit history import is silent");
Check(replay.TakeAnnouncement(now) is null && !replay.GetItems(now)[0].IsUnread, "replay does not create unread or bubble");
Check(replay.Apply(N("old", status: "reply_ready") with { OccurredAt = now.AddMinutes(-3) }, now).Outcome == "replay_suppressed", "event age suppresses backlog");
Check(replay.Apply(N("ancient") with { OccurredAt = now.AddHours(-24) }, now).Outcome == "expired_event", "events at retention limit ignored");
Check(!replay.Apply(N("future") with { OccurredAt = now.AddMinutes(3) }, now).Accepted, "future timestamp fails closed");
Check(replay.Apply(N("unknown") with { OccurredAt = now.AddMinutes(-11) }, now).Item?.Status == PetTaskStatus.Unknown, "stale active import immediately unknown");

var expiry = new TaskNotificationTracker();
expiry.Apply(N(status: "waiting"), now);
Check(expiry.GetItems(now.AddMinutes(10)).Single() is { Status: PetTaskStatus.Unknown, Message.Length: 0, IsUnread: false }, "active TTL expires to unknown and clears body");
Check(expiry.GetItems(now.AddMinutes(2)).Single().Status == PetTaskStatus.Unknown, "backward clock cannot resurrect expired state");
Check(expiry.Apply(N(revision: 2, status: "waiting"), now.AddMinutes(11)).Item?.IsUnread == true, "fresh revision can recover expired task");
Check(expiry.TakeAnnouncement(now.AddMinutes(11)) is not null, "recovered waiting state can announce again");
Check(expiry.GetItems(now.AddHours(25)).Count == 0 && expiry.RetainedEventCount == 0, "retention expires state and dedup metadata");
Check(terminal.GetItems(now.AddMinutes(11)).Single(item => item.TaskId == "task-a").Status == PetTaskStatus.Succeeded, "explicit terminal remains known until retention");

var mute = new TaskNotificationTracker();
mute.Apply(N(status: "waiting"), now);
mute.SetMuted(true);
Check(mute.TakeAnnouncement(now) is null && mute.GetItems(now).Single().IsUnread, "mute preserves unread but drops announcements");
mute.SetMuted(false);
Check(mute.TakeAnnouncement(now.AddSeconds(20)) is null, "unmute never replays backlog");
mute.SetEnabled(false);
Check(mute.Apply(N("off", status: "failed"), now.AddSeconds(21)).Outcome == "disabled" && mute.GetItems(now).Count == 1, "disabled receiver does not record new task");
mute.SetEnabled(true);
mute.Clear();
Check(mute.GetItems(now).Count == 0, "clear removes all visible messages");
Check(mute.Apply(N(status: "waiting"), now.AddSeconds(22)).Outcome == "duplicate" && mute.GetItems(now).Count == 0, "clear preserves bounded retry tombstones");
Check(mute.Apply(N(revision: 2, status: "reply_ready"), now.AddSeconds(23)).Changed, "genuinely new revision can return after clear");
mute.MarkRead("Source A", "task-a");
Check(!mute.GetItems(now).Single().IsUnread, "individual read works");

var bounded = new TaskNotificationTracker();
for (int index = 0; index < 600; index++) bounded.Apply(N("bounded-" + index, status: "waiting"), now);
Check(bounded.RetainedTaskCount == 64 && bounded.RetainedEventCount == 512, "task and event cache caps enforced");
Check(bounded.RetiredTaskCount <= TaskNotificationTracker.MaximumRetiredTasks, "retired revision watermarks are bounded and contain no bodies");
var eviction = new TaskNotificationTracker();
eviction.Apply(N("a-finished", status: "succeeded"), now);
for (int index = 0; index < 64; index++) eviction.Apply(N("b-other-" + index), now.AddSeconds(index + 1));
Check(!eviction.GetItems(now.AddMinutes(2)).Any(item => item.TaskId == "a-finished"), "old item evicted from visible cache");
Check(eviction.Apply(N("a-finished", 2), now.AddMinutes(2)).Outcome == "terminal_locked", "eviction retains terminal watermark against late running event");
Check(eviction.Apply(N("a-finished", 1, "succeeded", id: "late-duplicate"), now.AddMinutes(2)).Outcome == "stale_revision", "eviction retains revision watermark against changed event id");
int announcements = 0;
for (int index = 0; index < 12; index++)
{
    var time = now.AddSeconds(index * 5);
    bounded.Apply(N("new-" + index, status: "waiting"), time);
    if (bounded.TakeAnnouncement(time) is not null) announcements++;
}
Check(announcements <= 6, "announcement burst bounded to six per minute");
Check(bounded.TakeAnnouncement(now.AddMinutes(5)) is null, "pending announcements expire instead of catch-up burst");

Check(!tracker.Apply(N() with { TaskId = null }, now).Accepted, "incomplete identity invalid");
Check(!tracker.Apply(N() with { Revision = -1 }, now).Accepted, "negative revision invalid");
Check(!tracker.Apply(N() with { Revision = TaskNotificationTracker.MaximumRevision + 1 }, now).Accepted, "unsafe JSON integer rejected");
Check(!tracker.Apply(N() with { Source = "spoof\u202e" }, now).Accepted, "bidi source invalid");
Check(!tracker.Apply(N() with { Status = "unknown" }, now).Accepted, "unknown only produced by expiry, never accepted as fake source status");

var legacy = new WireNotice("Legacy", "legacy-event", "session", "有回复", "reply_ready");
Check(PetBridgeProtocol.Validate(legacy) is null, "legacy wire event still valid");
Check(!JsonSerializer.Serialize(legacy, PetBridgeProtocol.JsonOptions).Contains("taskId", StringComparison.Ordinal), "legacy serialization omits extension fields for old receivers");
Check(PetBridgeProtocol.Validate(legacy with { EventType = "task_succeeded" }) is not null, "legacy API cannot manufacture terminal task without task identity");
Check(PetBridgeProtocol.Validate(W(N())) is null, "structured update may omit eventType");
Check(PetBridgeProtocol.Validate(W(N()) with { EventType = "task_failed" }) is not null, "conflicting eventType rejected");
Check(PetBridgeProtocol.Validate(legacy with { Revision = 1 }) is not null, "partial task metadata rejected");
Check(PetBridgeProtocol.Validate(legacy with { IsReplay = true }) is not null, "task-only fields rejected on legacy event");
using (var memory = new MemoryStream())
{
    var request = new PetBridgeRequest("notify", W(N(status: "waiting")) with { OccurredAt = now, IsReplay = true });
    await PetBridgeProtocol.WriteAsync(memory, request, CancellationToken.None);
    memory.Position = 0;
    Check(await PetBridgeProtocol.ReadAsync<PetBridgeRequest>(memory, CancellationToken.None) == request, "task packet roundtrip preserves typed metadata");
}

bool Parse(string json, out WireNotice notice) => NotificationArguments.TryCreate("Fixed Source",
    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json), out notice, out _);
Check(Parse("""{"eventId":"e","taskId":"t","revision":0,"status":"running","occurredAt":"2026-09-19T08:00:00Z","isReplay":true}""", out var parsed) && parsed.IsReplay && parsed.Source == "Fixed Source", "MCP typed arguments parsed and source fixed");
Check(!Parse("""{"eventId":"e","taskId":"t","revision":"1","status":"running"}""", out _), "MCP revision string rejected");
Check(!Parse("""{"eventId":"e","taskId":"t","revision":1.5,"status":"running"}""", out _), "MCP fractional revision rejected");
Check(!Parse("""{"eventId":"e","taskId":"t","revision":1,"status":"running","occurredAt":"2026-09-19"}""", out _), "timestamp without explicit time and zone rejected");
Check(!Parse("""{"eventId":"e","eventType":"reply_ready","source":"Fake"}""", out _), "MCP caller cannot replace bound source");
Check(!Parse("""{"eventId":"e","taskId":"t","revision":1,"status":"running","isReplay":"false"}""", out _), "MCP replay must be boolean");
var stop = CompletionHook.ParseAndPrepare("Codex", "stop", System.Text.Encoding.UTF8.GetBytes("""{"hook_event_name":"Stop","session_id":"test-session","turn_id":"test-turn","stop_hook_active":false}"""), "unused-for-codex-stop");
Check(stop is { EventType: "reply_ready", TaskId: null, Status: null }, "managed Stop remains legacy reply_ready, never succeeded");

// A random SID-scoped test channel prevents touching a user's running pet.
string? oldChannel = Environment.GetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL");
Environment.SetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL", "tasks-" + Guid.NewGuid().ToString("N"));
try
{
    var delivered = new List<WireNotice>();
    var bridgeTracker = new TaskNotificationTracker();
    var gate = new object();
    using var server = new PetBridgeServer((notice, cancellation) =>
    {
        cancellation.ThrowIfCancellationRequested();
        lock (gate)
        {
            delivered.Add(notice);
            if (notice.TaskId is not null)
                return Task.FromResult(bridgeTracker.Apply(new(notice.Source, notice.Message, notice.EventId, notice.SessionId,
                    notice.TaskId, notice.Revision, notice.Status, notice.OccurredAt, notice.IsReplay), DateTimeOffset.UtcNow).Accepted);
            return Task.FromResult(true);
        }
    }, cancellation =>
    {
        cancellation.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult<object>(new { taskTestHarness = true, delivered = delivered.Count, count = bridgeTracker.GetItems(DateTimeOffset.UtcNow).Count });
    });
    server.Start();
    var client = new PetBridgeClient();
    var state = await client.SendAsync(new("get_state"));
    Check(state.Accepted && JsonSerializer.SerializeToElement(state.State).GetProperty("taskTestHarness").GetBoolean(), "isolated bridge host verified before writes");
    Check((await client.SendAsync(new("notify", legacy))).Accepted, "legacy event delivered through actual pipe");
    Check((await client.SendAsync(new("notify", W(N())))).Accepted, "task progress is not blocked by legacy three-second limiter");
    Check((await client.SendAsync(new("notify", W(N(revision: 2, status: "waiting"))))).Accepted, "rapid same-task status transition delivered");
    Check((await client.SendAsync(new("notify", W(N(revision: 2, status: "waiting"))))).Status == "duplicate", "actual pipe event deduplication");
    Check((await client.SendAsync(new("notify", W(N("task-b", status: "failed", id: "task-a:2"))))).Accepted, "wire dedup includes task identity");
    Check((await client.SendAsync(new("notify", W(N(source: "Source B", status: "reply_ready"))))).Accepted, "wire sources isolated");
    Check((await client.SendAsync(new("notify", W(N(revision: 1, id: "late-wire"))))).Accepted, "obsolete revision handled as received without queue retry");
    lock (gate) Check(bridgeTracker.GetItems(DateTimeOffset.UtcNow).Single(item => item.Source == "Source A" && item.TaskId == "task-a").Status == PetTaskStatus.Waiting, "actual pipe out-of-order event cannot roll back tracker");
    Check((await client.SendAsync(new("notify", W(N()) with { Revision = -1 }))).Status == "invalid_request", "actual pipe validation rejects invalid revision");
    if (args.Length > 0)
    {
        string? mcp = null, dotnet = null;
        for (int index = 0; index + 1 < args.Length; index += 2)
        { if (args[index] == "--mcp") mcp = args[index + 1]; else if (args[index] == "--dotnet") dotnet = args[index + 1]; else throw new ArgumentException("Use --mcp path [--dotnet path]."); }
        if (mcp is null || !File.Exists(mcp)) throw new ArgumentException("An existing --mcp target is required.");
        using var rpc = new McpProbe(mcp, dotnet);
        var initialized = await rpc.Call("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "task-self-test", version = "1" } });
        Check(initialized.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25", "actual MCP SDK initializes");
        await rpc.Notify("notifications/initialized");
        var listed = await rpc.Call("tools/list", new { });
        Check(listed.GetProperty("result").GetProperty("tools").EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "pet_notify")
            .GetProperty("inputSchema").GetProperty("properties").TryGetProperty("revision", out _), "actual MCP schema advertises task revision");
        Check((await rpc.Tool("pet_get_state", new { })).GetProperty("state").GetProperty("taskTestHarness").GetBoolean(), "actual MCP confirms isolated task harness");
        Check((await rpc.Tool("pet_notify", new { eventId = "rpc-1", taskId = "rpc-task", revision = 1, status = "running" })).GetProperty("accepted").GetBoolean(), "actual MCP typed running update delivered");
        Check((await rpc.Tool("pet_notify", new { eventId = "rpc-2", taskId = "rpc-task", revision = 2, status = "succeeded", message = "测试明确完成" })).GetProperty("accepted").GetBoolean(), "actual MCP terminal update delivered");
        Check((await rpc.Tool("pet_notify", new { eventId = "rpc-3", taskId = "rpc-task", revision = 3, status = "running" })).GetProperty("accepted").GetBoolean(), "actual MCP late running processed without regress");
        lock (gate) Check(bridgeTracker.GetItems(DateTimeOffset.UtcNow).Single(item => item.Source == "MCP Task Test").Status == PetTaskStatus.Succeeded, "actual MCP terminal remains terminal");
        Check((await rpc.Tool("pet_notify", new { eventId = "rpc-bad", taskId = "rpc-task", revision = "4", status = "running" })).GetProperty("status").GetString() == "invalid_request", "actual MCP rejects mistyped revision");
        Check((await rpc.Tool("pet_notify", new { eventId = "rpc-spoof", eventType = "reply_ready", source = "Forged" })).GetProperty("status").GetString() == "invalid_request", "actual MCP source override rejected");
        Check((await rpc.Tool("pet_notify", new { eventId = "rpc-future", taskId = "future-task", revision = 1, status = "waiting", occurredAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O") })).GetProperty("status").GetString() == "invalid_request", "actual MCP rejects impossible future event time");
        var cli = await RunCli(mcp, dotnet, "--source", "CLI Task Test", "--notify", "--event-id", "cli-1", "--task-id", "cli-task", "--revision", "1", "--status", "waiting");
        using var cliReply = JsonDocument.Parse(cli.Output);
        Check(cli.ExitCode == 0 && cliReply.RootElement.GetProperty("accepted").GetBoolean(), "actual one-shot CLI task update delivered");
        var badCli = await RunCli(mcp, dotnet, "--source", "CLI Task Test", "--notify", "--event-id", "cli-bad", "--task-id", "cli-task", "--revision", "-1", "--status", "waiting");
        Check(badCli.ExitCode == 2, "actual one-shot CLI rejects invalid revision without sending");
    }
    else Console.WriteLine("SKIP actual MCP process: pass --mcp path [--dotnet path] to exercise SDK stdio.");

    var parallelRequests = Enumerable.Range(0, 8).Select(index => new PetBridgeRequest("notify", W(N("parallel-" + index, status: "waiting", source: "Parallel " + index % 2)))).ToArray();
    var parallelReplies = await Task.WhenAll(parallelRequests.Select(request => new PetBridgeClient().SendAsync(request)));
    for (int index = 0; index < parallelReplies.Length; index++)
        if (!parallelReplies[index].Accepted && parallelReplies[index].Status == "busy")
            parallelReplies[index] = await client.SendAsync(parallelRequests[index]);
    Check(parallelReplies.All(reply => reply.Accepted), "concurrent sources/tasks safely accepted or retried after explicit busy");
    lock (gate) Check(bridgeTracker.GetItems(DateTimeOffset.UtcNow).Count(item => item.TaskId.StartsWith("parallel-", StringComparison.Ordinal)) == 8, "parallel tasks remain distinct");
    bool limited = false;
    int burstAccepted = 0;
    for (int index = 0; index < 121; index++)
    {
        var reply = await client.SendAsync(new("notify", W(N("burst", revision: index, id: "burst-" + index))));
        if (reply.Status == "rate_limited") { limited = true; break; }
        if (!reply.Accepted) throw new InvalidOperationException("Unexpected response within the task burst: " + reply.Status);
        burstAccepted++;
    }
    Check(burstAccepted > 0, "task update burst accepted below transport limit");
    Check(limited, "actual bridge caps task updates at 120 per minute");
    lock (gate) Check(delivered.Count(notice => notice.TaskId is not null) <= 120, "rejected and duplicate updates never consume extra callback capacity");
}
finally { Environment.SetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL", oldChannel); }
Console.WriteLine($"Task notifications: {passed} checks passed.");

static async Task<(int ExitCode, string Output)> RunCli(string path, string? dotnet, params string[] arguments)
{
    bool dll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    var start = new ProcessStartInfo(dll ? dotnet ?? "dotnet" : path)
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    if (dll) start.ArgumentList.Add(path);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start test CLI.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token);
        await error;
        return (process.ExitCode, await output);
    }
    finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
}

sealed class McpProbe : IDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderr;
    private int _id;
    public McpProbe(string path, string? dotnet)
    {
        bool dll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(dll ? dotnet ?? "dotnet" : path)
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (dll) start.ArgumentList.Add(path);
        start.ArgumentList.Add("--source"); start.ArgumentList.Add("MCP Task Test");
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MCP probe.");
        _stderr = _process.StandardError.ReadToEndAsync();
    }
    public async Task Notify(string method) { await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method })); await _process.StandardInput.FlushAsync(); }
    public async Task<JsonElement> Call(string method, object parameters)
    {
        int id = ++_id;
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await _process.StandardInput.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            string line = await _process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new InvalidOperationException("MCP stdout ended.");
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id) return document.RootElement.Clone();
        }
    }
    public async Task<JsonElement> Tool(string name, object arguments)
    {
        var result = await Call("tools/call", new { name, arguments });
        using var text = JsonDocument.Parse(result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        return text.RootElement.Clone();
    }
    public void Dispose()
    {
        _process.StandardInput.Close();
        if (!_process.WaitForExit(5000)) { _process.Kill(entireProcessTree: true); _process.WaitForExit(); }
        _process.Dispose();
    }
}
