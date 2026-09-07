using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DuckDeskPet.ClientSetup;
using EagleDeskPet.Mcp;
using Tomlyn;
using System.IO.Pipes;
using System.Diagnostics;
using DuckDeskPet.Integration;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.codex-build/client-setup-tests", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
Console.WriteLine("Isolated test root: " + root);
var results = new List<object>();
void Check(string name, Action test)
{
    try { test(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { results.Add(new { name, passed = false }); Console.WriteLine("FAIL " + name + ": " + ex.Message); throw; }
}
static void Require(bool value, string reason = "assertion failed") { if (!value) throw new Exception(reason); }
(ClientSetupService Service, string Home, string App) Fixture()
{
    var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var home = Path.Combine(folder, "user"); var app = Path.Combine(folder, "app with spaces");
    Directory.CreateDirectory(home); Directory.CreateDirectory(app);
    File.WriteAllText(Path.Combine(app, "EagleDeskPet.Mcp.exe"), "inert executable fixture");
    return (new ClientSetupService(home, app), home, app);
}
void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
static JsonObject Json(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

foreach (var client in Enum.GetValues<ClientKind>())
{
    Check(client + " preview is read-only", () =>
    {
        var fixture = Fixture();
        var plan = fixture.Service.Preview(client);
        Require(plan.CanApply, plan.Status); Require(plan.HasChanges); Require(!Directory.EnumerateFiles(fixture.Home, "*", SearchOption.AllDirectories).Any());
        Require(plan.Files.All(file => file.Path.StartsWith(fixture.Home, StringComparison.OrdinalIgnoreCase)));
    });
    Check(client + " install/idempotence/remove", () =>
    {
        var fixture = Fixture(); var service = fixture.Service;
        var plan = service.Preview(client); var applied = service.Apply(plan);
        Require(applied.Success, applied.Message);
        var again = service.Preview(client); Require(again.CanApply, again.Status); Require(!again.HasChanges, "second installation would rewrite files");
        Require(service.Apply(again).Success);
        var removal = service.Preview(client, SetupAction.Remove); Require(removal.CanApply, removal.Status);
        Require(service.Apply(removal).Success);
        var after = service.Preview(client, SetupAction.Remove); Require(after.CanApply, after.Status); Require(!after.HasChanges);
        Require(service.Preview(client).CanApply);
    });
    Check(client + " preserve unrelated settings and hooks", () =>
    {
        var fixture = Fixture(); var plan = fixture.Service.Preview(client);
        Write(plan.Files[0].Path, client == ClientKind.Codex ? "# user's comment\r\nmodel = \"existing-model\"\r\n[mcp_servers.other]\r\ncommand = \"keep.exe\"\r\n" : "{\"privateExistingValue\":\"keep-me\",\"mcpServers\":{\"other\":{\"command\":\"keep.exe\",\"args\":[]}}}");
        Write(plan.Files[1].Path, "{\"permissions\":{\"allow\":[\"Read\"]},\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"echo keep\"}]}]}}");
        var beforeMcp = File.ReadAllText(plan.Files[0].Path);
        var install = fixture.Service.Preview(client); Require(install.CanApply, install.Status);
        var outcome = fixture.Service.Apply(install); Require(outcome.Success, outcome.Message); Require(outcome.BackupPaths.Count == 2);
        var hooks = Json(plan.Files[1].Path); Require(hooks["permissions"]!["allow"]![0]!.GetValue<string>() == "Read");
        Require(hooks["hooks"]!["Stop"]!.AsArray().Count == 2);
        if (client == ClientKind.Codex) Require(File.ReadAllText(plan.Files[0].Path).StartsWith(beforeMcp));
        else Require(Json(plan.Files[0].Path)["privateExistingValue"]!.GetValue<string>() == "keep-me");
        var removed = fixture.Service.Apply(fixture.Service.Preview(client, SetupAction.Remove)); Require(removed.Success, removed.Message);
        Require(Json(plan.Files[1].Path)["hooks"]!["Stop"]!.AsArray().Count == 1);
    });
    Check(client + " manual MCP conflict not adopted", () =>
    {
        var fixture = Fixture(); var target = fixture.Service.Preview(client).Files[0].Path;
        Write(target, client == ClientKind.Codex ? "[mcp_servers.eagle]\ncommand='C:/my/EagleDeskPet.Mcp.exe'\n" : "{\"mcpServers\":{\"my-eagle\":{\"command\":\"C:/my/EagleDeskPet.Mcp.exe\"}}}");
        var before = File.ReadAllBytes(target); var plan = fixture.Service.Preview(client);
        Require(!plan.CanApply); Require(File.ReadAllBytes(target).SequenceEqual(before));
    });
    Check(client + " manual pet_notify hook conflict", () =>
    {
        var fixture = Fixture(); var path = fixture.Service.Preview(client).Files[1].Path;
        Write(path, "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"mcp_tool\",\"server\":\"custom\",\"tool\":\"pet_notify\"}]}]}}");
        Require(!fixture.Service.Preview(client).CanApply);
    });
    Check(client + " modified owned hook refused", () =>
    {
        var fixture = Fixture(); var plan = fixture.Service.Preview(client); Require(fixture.Service.Apply(plan).Success);
        var config = Json(plan.Files[1].Path); config["hooks"]!["Stop"]![0]!["hooks"]![0]!["timeout"] = 100;
        Write(plan.Files[1].Path, config.ToJsonString());
        Require(!fixture.Service.Preview(client, SetupAction.Remove).CanApply);
    });
}
Check("Codex inline hooks conflict", () =>
{
    var fixture = Fixture(); var target = fixture.Service.Preview(ClientKind.Codex).Files[0].Path;
    Write(target, "[[hooks.Stop]]\n[[hooks.Stop.hooks]]\ntype='mcp_tool'\nserver='eagle'\ntool='pet_notify'\n");
    Require(!fixture.Service.Preview(ClientKind.Codex).CanApply);
});
Check("Codex does not override disabled hooks", () =>
{
    var fixture = Fixture(); Write(fixture.Service.Preview(ClientKind.Codex).Files[0].Path, "[features]\nhooks=false\n");
    Require(!fixture.Service.Preview(ClientKind.Codex).CanApply);
});
Check("Codex complex valid TOML retained byte-exact prefix", () =>
{
    var fixture = Fixture(); var path = fixture.Service.Preview(ClientKind.Codex).Files[0].Path;
    const string before = "# comment\nmodel='keep'\n[instructions]\nvalue=\"\"\"multi\nline\"\"\"\nquoted.'nested.key' = [1, 2, 3]\n";
    Write(path, before); var plan = fixture.Service.Preview(ClientKind.Codex); Require(plan.CanApply, plan.Status);
    Require(fixture.Service.Apply(plan).Success); Require(File.ReadAllText(path).StartsWith(before)); Require(!Toml.Parse(File.ReadAllText(path)).HasErrors);
});
Check("app relocation updates only owned entries", () =>
{
    var fixture = Fixture(); Require(fixture.Service.Apply(fixture.Service.Preview(ClientKind.Codex)).Success);
    var app = Path.Combine(fixture.App, "new release"); Directory.CreateDirectory(app); File.WriteAllText(Path.Combine(app, "EagleDeskPet.Mcp.exe"), "fixture");
    var next = new ClientSetupService(fixture.Home, app); var plan = next.Preview(ClientKind.Codex); Require(plan.CanApply, plan.Status); Require(plan.HasChanges); Require(next.Apply(plan).Success); Require(!next.Preview(ClientKind.Codex).HasChanges);
});
foreach (var bad in new[] { "[]", "null", "{", "{\"hooks\":null}", "{\"hooks\":{\"Stop\":{}}}", "{\"x\":1,\"x\":2}", "%TSD-Header- protected" })
    Check("invalid JSON/shape refused " + bad, () =>
    {
        var fixture = Fixture(); Write(fixture.Service.Preview(ClientKind.ClaudeCode).Files[1].Path, bad); Require(!fixture.Service.Preview(ClientKind.ClaudeCode).CanApply);
    });
foreach (var bad in new[] { "{\"hooks\":{\"Stop\":[null]}}", "{\"hooks\":{\"Stop\":[{\"hooks\":[{}]}]}}" })
    Check("invalid nested Hook refused " + bad, () =>
    {
        var fixture = Fixture(); Write(fixture.Service.Preview(ClientKind.ClaudeCode).Files[1].Path, bad); Require(!fixture.Service.Preview(ClientKind.ClaudeCode).CanApply);
    });
Check("custom client config roots explicitly refused without default writes", () =>
{
    var fixture = Fixture();
    var service = ClientSetupService.CreateForEnvironment(fixture.Home, fixture.App, name => name is "CLAUDE_CONFIG_DIR" or "CODEBUDDY_CONFIG_DIR" ? Path.Combine(root, "custom") : null);
    foreach (var client in new[] { ClientKind.ClaudeCode, ClientKind.CodeBuddyCode })
    {
        Require(!service.Preview(client).CanApply); Require(!service.Preview(client, SetupAction.Remove).CanApply);
        Require(!service.Apply(service.Preview(client)).Success);
    }
    Require(!Directory.EnumerateFiles(fixture.Home, "*", SearchOption.AllDirectories).Any()); Require(service.Preview(ClientKind.Codex).CanApply);
});
Check("injected home ignores real environment and explicit Codex root resolved", () =>
{
    var fixture = Fixture(); var customCodex = Path.Combine(fixture.Home, "codex-custom");
    var service = ClientSetupService.CreateForEnvironment(fixture.Home, fixture.App, name => name == "CODEX_HOME" ? customCodex : null);
    Require(service.Preview(ClientKind.Codex).Files.All(file => file.Path.StartsWith(customCodex)));
    Require(fixture.Service.Preview(ClientKind.Codex).Files.All(file => file.Path.StartsWith(Path.Combine(fixture.Home, ".codex"))));
});
Check("malformed receipt cannot adopt unrelated server", () =>
{
    var fixture = Fixture(); var plan = fixture.Service.Preview(ClientKind.ClaudeCode); Require(fixture.Service.Apply(plan).Success);
    var receipt = Json(plan.Files[2].Path); var config = Json(plan.Files[0].Path);
    receipt["server"]!["command"] = "other.exe"; config["mcpServers"]!["eagle-desktop-pet"]!["command"] = "other.exe";
    Write(plan.Files[2].Path, receipt.ToJsonString()); Write(plan.Files[0].Path, config.ToJsonString());
    Require(!fixture.Service.Preview(ClientKind.ClaudeCode, SetupAction.Remove).CanApply);
});
Check("invalid TOML refused", () =>
{
    var fixture = Fixture(); Write(fixture.Service.Preview(ClientKind.Codex).Files[0].Path, "x = 'unclosed"); Require(!fixture.Service.Preview(ClientKind.Codex).CanApply);
});
Check("UTF8 BOM and CRLF retained", () =>
{
    var fixture = Fixture(); var path = fixture.Service.Preview(ClientKind.Codex).Files[0].Path;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "# test\r\nmodel='x'\r\n", new UTF8Encoding(true));
    Require(fixture.Service.Apply(fixture.Service.Preview(ClientKind.Codex)).Success);
    var bytes = File.ReadAllBytes(path); Require(bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 })); Require(!File.ReadAllText(path).Replace("\r\n", "").Contains('\n'));
});
Check("stale preview rejects all writes", () =>
{
    var fixture = Fixture(); var plan = fixture.Service.Preview(ClientKind.ClaudeCode);
    Write(plan.Files[1].Path, "{\"newExternalKey\":true}"); var result = fixture.Service.Apply(plan);
    Require(!result.Success); Require(!File.Exists(plan.Files[0].Path)); Require(Json(plan.Files[1].Path)["newExternalKey"]!.GetValue<bool>());
});
Check("foreign plan rejected", () =>
{
    var first = Fixture(); var second = Fixture(); Require(!second.Service.Apply(first.Service.Preview(ClientKind.Codex)).Success);
});
Check("failed second write rolls back first exactly", () =>
{
    var fixture = Fixture(); var plan = fixture.Service.Preview(ClientKind.ClaudeCode); Write(plan.Files[0].Path, "{\"before\": 42}");
    fixture.Service.BeforeWriteForTest = index => { if (index == 1) throw new IOException("simulated disk failure"); };
    var result = fixture.Service.Apply(fixture.Service.Preview(ClientKind.ClaudeCode)); Require(!result.Success); Require(File.ReadAllText(plan.Files[0].Path) == "{\"before\": 42}"); Require(!File.Exists(plan.Files[1].Path));
});
Check("rollback preserves concurrent external modifications", () =>
{
    var fixture = Fixture(); var plan = fixture.Service.Preview(ClientKind.ClaudeCode);
    fixture.Service.BeforeWriteForTest = index => { if (index == 1) { Write(plan.Files[0].Path, "{\"external\":true}"); throw new IOException("simulated"); } };
    var result = fixture.Service.Apply(plan); Require(!result.Success); Require(result.Message.Contains("无法安全回滚")); Require(Json(plan.Files[0].Path)["external"]!.GetValue<bool>());
});
Check("no-op remove never creates config files", () =>
{
    var fixture = Fixture(); var plan = fixture.Service.Preview(ClientKind.ClaudeCode, SetupAction.Remove); Require(plan.CanApply && !plan.HasChanges); Require(fixture.Service.Apply(plan).Success); Require(!Directory.EnumerateFiles(fixture.Home, "*", SearchOption.AllDirectories).Any());
});
Check("oversize and invalid encoding refused", () =>
{
    var fixture = Fixture(); var path = fixture.Service.Preview(ClientKind.ClaudeCode).Files[0].Path;
    Write(path, new string(' ', 2 * 1024 * 1024 + 1)); Require(!fixture.Service.Preview(ClientKind.ClaudeCode).CanApply);
    File.WriteAllBytes(path, [0xff, 0xfe, 0, 0]); Require(!fixture.Service.Preview(ClientKind.ClaudeCode).CanApply);
});
Check("reparse-point target refused", () =>
{
    var fixture = Fixture(); var outside = Path.Combine(root, "link-target-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(outside);
    var script = "New-Item -ItemType Junction -Path '" + Path.Combine(fixture.Home, ".claude").Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' | Out-Null";
    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) } })!;
    process.WaitForExit(); Require(process.ExitCode == 0);
    Require(!fixture.Service.Preview(ClientKind.ClaudeCode).CanApply); Require(!Directory.EnumerateFiles(outside).Any());
});
Check("marker inside TOML multiline string refused", () =>
{
    var fixture = Fixture(); var plan = fixture.Service.Preview(ClientKind.Codex); Require(fixture.Service.Apply(plan).Success);
    var receipt = Json(plan.Files[2].Path); var block = receipt["tomlBlock"]!.GetValue<string>();
    Write(plan.Files[0].Path, "description = '''\n" + block + "'''\n");
    Require(!fixture.Service.Preview(ClientKind.Codex, SetupAction.Remove).CanApply);
});

byte[] Hook(string client, string kind = "Stop", string? turn = "turn-1", bool active = false) => JsonSerializer.SerializeToUtf8Bytes(new { session_id = "private-session", hook_event_name = kind, turn_id = turn, stop_hook_active = active, transcript_path = "Z:/must-not-read/transcript.jsonl", last_assistant_message = "SECRET ANSWER MUST NEVER APPEAR" });
foreach (var client in new[] { "Codex", "ClaudeCode", "CodeBuddyCode" })
{
    Check(client + " hook fixed source/privacy/stable dedup", () =>
    {
        var folder = Path.Combine(root, "hooks-" + Guid.NewGuid().ToString("N"));
        var first = CompletionHook.ParseAndPrepare(client, "stop", Hook(client), folder)!;
        var second = CompletionHook.ParseAndPrepare(client, "stop", Hook(client), folder)!;
        Require(first.EventId == second.EventId); Require(first.Message == "有新的回复了，快去看看。"); Require(!JsonSerializer.Serialize(first).Contains("SECRET")); Require(first.SessionId != "private-session");
        if (client == "Codex") Require(CompletionHook.ParseAndPrepare(client, "stop", Hook(client, turn: "turn-2"), folder)!.EventId != first.EventId);
        else { Require(CompletionHook.ParseAndPrepare(client, "prompt", Hook(client, "UserPromptSubmit"), folder) is null); Require(CompletionHook.ParseAndPrepare(client, "stop", Hook(client), folder)!.EventId != first.EventId); }
        Require(CompletionHook.ParseAndPrepare(client, "stop", Hook(client, active: true), folder) is null);
    });
}
Check("hook unknown client/event/id and hostile payload rejected", () =>
{
    var folder = Path.Combine(root, "invalid-hooks");
    Require(CompletionHook.ParseAndPrepare("forged-source", "stop", Hook("Codex"), folder) is null);
    Require(CompletionHook.ParseAndPrepare("Codex", "stop", Hook("Codex", "SubagentStop"), folder) is null);
    Require(CompletionHook.ParseAndPrepare("Codex", "stop", Hook("Codex", turn: null), folder) is null);
    Require(CompletionHook.ParseAndPrepare("Codex", "stop", Encoding.UTF8.GetBytes("[]"), folder) is null);
    Require(CompletionHook.ParseAndPrepare("Codex", "stop", Encoding.UTF8.GetBytes("{\"session_id\":\"a\",\"session_id\":\"b\"}"), folder) is null);
});
Check("hook input size bound", () =>
{
    using var stream = new MemoryStream(new byte[CompletionHook.MaximumInputBytes + 1]);
    try { CompletionHook.ReadInputAsync(stream, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("accepted oversized input"); } catch (InvalidDataException) { }
});
Check("hook waiting stdin canceled", () =>
{
    using var deadline = new CancellationTokenSource(100);
    try { CompletionHook.ReadInputAsync(new NeverEndingStream(), deadline.Token).GetAwaiter().GetResult(); throw new Exception("ignored deadline"); } catch (OperationCanceledException) { }
});
if (args.Length == 4 && args[0] == "--mcp" && args[2] == "--bash")
{
    var mcpExecutable = Path.GetFullPath(args[1]); var bashExecutable = Path.GetFullPath(args[3]);
    Require(File.Exists(mcpExecutable) && File.Exists(bashExecutable));
    foreach (var client in Enum.GetValues<ClientKind>())
    {
        Check(client + " actual generated shell hook to isolated IPC", () =>
        {
            var fixture = Fixture(); var service = new ClientSetupService(fixture.Home, Path.GetDirectoryName(mcpExecutable)!);
            var plan = service.Preview(client); Require(plan.CanApply, plan.Status); Require(service.Apply(plan).Success);
            var command = Json(plan.Files[1].Path)["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
            var channel = Guid.NewGuid().ToString("N");
            var previousChannel = Environment.GetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL");
            Environment.SetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL", channel);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var server = new NamedPipeServerStream(PetBridgeProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var exchange = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync(deadline.Token);
                    var request = await PetBridgeProtocol.ReadAsync<PetBridgeRequest>(server, deadline.Token);
                    await PetBridgeProtocol.WriteAsync(server, new PetBridgeReply(true, "accepted", "fixture"), deadline.Token);
                    return request;
                });
                var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["EAGLE_PET_TEST_CHANNEL"] = channel;
                start.Environment["EAGLE_PET_DATA_DIR"] = Path.Combine(fixture.Home, "isolated-pet-data");
                start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(Environment.ProcessPath)!;
                if (client == ClientKind.Codex)
                {
                    start.FileName = "powershell.exe";
                    foreach (var item in command.Split(' ').Skip(1)) start.ArgumentList.Add(item);
                }
                else
                {
                    start.FileName = bashExecutable;
                    foreach (var item in new[] { "--noprofile", "--norc", "-c", command }) start.ArgumentList.Add(item);
                }
                using var process = Process.Start(start)!;
                try
                {
                    process.StandardInput.Write(Encoding.UTF8.GetString(Hook(client.ToString())));
                    process.StandardInput.Close();
                    process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
                    var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd();
                    Require(process.ExitCode == 0, "wrapper exit=" + process.ExitCode + " stderr=" + stderr);
                    Require(stdout.Trim() == "{}", "wrapper stdout=" + stdout + " stderr=" + stderr);
                    var request = exchange.GetAwaiter().GetResult();
                    Require(request.Operation == "notify" && request.Notification!.Message == "有新的回复了，快去看看。");
                    Require(request.Notification!.Source == (client == ClientKind.Codex ? "Codex" : client == ClientKind.ClaudeCode ? "Claude Code" : "CodeBuddy Code"));
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
            finally { Environment.SetEnvironmentVariable("EAGLE_PET_TEST_CHANNEL", previousChannel); }
        });
    }
}
File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = results.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"All {results.Count} isolated client setup/hook tests passed.");

sealed class NeverEndingStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(new TaskCompletionSource<int>().Task);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
