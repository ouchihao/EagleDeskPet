using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;

namespace DuckDeskPet.ClientSetup;

// Deliberately only edits user-scoped Windows client configuration, never project,
// organization policy, credentials, trust records, or a currently running client.
public sealed class ClientSetupService
{
    internal const string Owner = "eagle-desktop-pet-v1";
    internal const string ServerName = "eagle-desktop-pet";
    private const string BeginMarker = "# BEGIN EagleDeskPet managed MCP v1";
    private const string EndMarker = "# END EagleDeskPet managed MCP v1";
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, MaxDepth = 64 };
    private readonly Guid _id = Guid.NewGuid();
    private readonly string _home, _app, _codex;
    private readonly object _gate = new();
    private readonly Dictionary<ClientKind, string> _unsupportedOverrides = [];
    internal Action<int>? BeforeWriteForTest { get; set; }

    public ClientSetupService(string homeDirectory, string applicationDirectory, string? codexHome = null)
    {
        _home = Absolute(homeDirectory);
        _app = Absolute(applicationDirectory);
        _codex = codexHome is null ? Path.Combine(_home, ".codex") : Absolute(codexHome);
    }

    public static ClientSetupService CreateDefault(string applicationDirectory) => CreateForEnvironment(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), applicationDirectory, Environment.GetEnvironmentVariable);

    internal static ClientSetupService CreateForEnvironment(string homeDirectory, string applicationDirectory, Func<string, string?> getVariable)
    {
        var service = new ClientSetupService(homeDirectory, applicationDirectory, getVariable("CODEX_HOME") is { Length: > 0 } codexHome ? codexHome : null);
        foreach (var (client, variable) in new[] { (ClientKind.ClaudeCode, "CLAUDE_CONFIG_DIR"), (ClientKind.CodeBuddyCode, "CODEBUDDY_CONFIG_DIR") })
            if (!string.IsNullOrEmpty(getVariable(variable))) service._unsupportedOverrides[client] = variable;
        return service;
    }

    public ClientSetupPlan Preview(ClientKind client, SetupAction action = SetupAction.Install)
    {
        var targets = GetTargets(client);
        var notes = GetNotes(client).ToList();
        var changes = new List<SetupFileChange>();
        try
        {
            if (_unsupportedOverrides.TryGetValue(client, out var overrideVariable))
                throw new SetupException("检测到 " + overrideVariable + " 自定义配置目录。本版不自动合并此布局，不会读写默认配置位置；请继续手动配置此客户端。");
            var executable = Path.Combine(_app, "EagleDeskPet.Mcp.exe");
            CheckPath(executable);
            if (action == SetupAction.Install && !File.Exists(executable))
                throw new SetupException("同目录缺少 EagleDeskPet.Mcp.exe，请完整解压发行包后重试。");
            foreach (var path in targets) CheckPath(path);
            var manifestBytes = Read(targets[2]);
            var receipt = manifestBytes is null ? new JsonObject() : ReadJson(manifestBytes);
            if (receipt.Count > 0 && (String(receipt, "owner") != Owner || String(receipt, "client") != client.ToString()))
                throw new SetupException("发现不属于本安装器的配置记录，已停止，未修改任何文件。");

            var mcpBytes = Read(targets[0]);
            var hookBytes = Read(targets[1]);
            string? oldBlock = receipt["tomlBlock"]?.GetValue<string>();
            var previousServer = receipt["server"] as JsonObject;
            var previousHooks = receipt["hooks"] as JsonObject;
            if (receipt.ContainsKey("server") && previousServer is null || receipt.ContainsKey("hooks") && previousHooks is null)
                throw new SetupException("安装归属记录格式不正确，拒绝修改。");
            if (previousServer is not null &&
                (String(previousServer, "type") != "stdio" || String(previousServer, "command") is not { } previousCommand ||
                 !previousCommand.EndsWith("EagleDeskPet.Mcp.exe", StringComparison.OrdinalIgnoreCase) ||
                 previousServer["env"] is not JsonObject previousEnv || String(previousEnv, "EAGLE_PET_SETUP_OWNER") != Owner ||
                 !JsonNode.DeepEquals(previousServer["args"], new JsonArray("--source", Source(client)))))
                throw new SetupException("安装归属记录中的 MCP 来源无法验证，拒绝修改。");
            var nextServer = MakeServer(client, executable);
            var nextHooks = MakeHooks(client, executable);
            var installing = action == SetupAction.Install;
            byte[] newMcp;
            string? newBlock = null;
            if (client == ClientKind.Codex)
            {
                var text = Decode(mcpBytes);
                var originalText = text;
                var syntax = Toml.Parse(text);
                if (syntax.HasErrors) throw new SetupException("Codex config.toml 无法安全解析，已停止。请先修复配置语法。");
                var model = Toml.ToModel(text);
                if (oldBlock is not null && text.Contains(oldBlock, StringComparison.Ordinal))
                {
                    var receiptModel = Toml.Parse(oldBlock).HasErrors ? null : Toml.ToModel(oldBlock);
                    if (receiptModel is null || !model.TryGetValue("mcp_servers", out var configuredServers) || configuredServers is not TomlTable configuredTable ||
                        !configuredTable.TryGetValue(ServerName, out var configuredServer) || configuredServer is not TomlTable actualServer ||
                        !receiptModel.TryGetValue("mcp_servers", out var recordedServers) || recordedServers is not TomlTable recordedTable ||
                        !recordedTable.TryGetValue(ServerName, out var recordedServer) || recordedServer is not TomlTable expectedServer ||
                        Toml.FromModel(actualServer) != Toml.FromModel(expectedServer))
                        throw new SetupException("托管 TOML 标记与实际 MCP 配置不一致，拒绝按文本标记修改。");
                }
                if (installing && model.TryGetValue("features", out var features) && features is TomlTable flags &&
                    (flags.TryGetValue("hooks", out var enabled) && enabled is false || flags.TryGetValue("codex_hooks", out var legacy) && legacy is false))
                    throw new SetupException("Codex 已禁用 Hooks；请自行确认设置或公司策略后启用。安装器不会替你打开安全开关。");
                text = RemoveOwnedBlock(text, oldBlock);
                // Validate the remaining tree too: a marker inside a multiline string
                // must never be mistaken for our configuration.
                if (Toml.Parse(text).HasErrors) throw new SetupException("托管 TOML 区块已被改动，无法安全更新或移除。");
                model = Toml.ToModel(text);
                if (installing && ContainsPet(model))
                    throw new SetupException("已有手动配置的大头鹰 MCP / pet_notify（可能在内联 Hooks 中）。为避免重复提醒，本次不写入；可继续使用原配置。");
                if (installing && model.TryGetValue("hooks", out _))
                    throw new SetupException("当前 Codex 使用内联 Hooks；为避免混用 hooks.json，本版不自动改写。请保留现有配置或手动合并。");
                if (installing)
                {
                    var nl = Newline(mcpBytes);
                    newBlock = BeginMarker + nl + "[mcp_servers.\"" + ServerName + "\"]" + nl +
                        "command = " + JsonSerializer.Serialize(executable) + nl +
                        "args = [\"--source\", \"Codex\"]" + nl + EndMarker + nl;
                    text = oldBlock is not null && originalText.Contains(oldBlock, StringComparison.Ordinal)
                        ? originalText.Replace(oldBlock, newBlock, StringComparison.Ordinal)
                        : text + (text.Length == 0 || text.EndsWith('\n') ? "" : nl) + nl + newBlock;
                }
                if (Toml.Parse(text).HasErrors) throw new SetupException("Codex TOML 合并校验失败，未修改任何文件。");
                newMcp = Encode(text, mcpBytes);
            }
            else
            {
                var root = mcpBytes is null ? new JsonObject() : ReadJson(mcpBytes);
                var servers = Object(root, "mcpServers", false);
                RemoveExact(servers, ServerName, previousServer, "MCP");
                if (installing && ContainsPet(servers))
                    throw new SetupException("已有手动配置的大头鹰 MCP。为避免重复，本次不写入；不会覆盖或接管你的原配置。");
                if (installing) servers[ServerName] = nextServer.DeepClone();
                if (servers.Count > 0 || root.ContainsKey("mcpServers")) root["mcpServers"] = servers;
                newMcp = EncodeJson(root, mcpBytes);
            }

            var hookRoot = hookBytes is null ? new JsonObject() : ReadJson(hookBytes);
            if (installing && hookRoot["disableAllHooks"] is JsonValue disabled && disabled.TryGetValue<bool>(out var disabledFlag) && disabledFlag)
                throw new SetupException("此客户端已禁用全部 Hooks。安装器不会替你启用；请先确认你的设置或公司策略。");
            var events = Object(hookRoot, "hooks", false);
            ValidateHookShapes(events);
            RemoveOwnedHooks(events, previousHooks);
            if (installing && ContainsPet(events))
                throw new SetupException("已有手动配置的大头鹰 / pet_notify Hook。为避免同一条回复弹两次，本次不写入，保留原设置。");
            if (installing)
                foreach (var pair in nextHooks)
                {
                    var array = Array(events, pair.Key);
                    array.Add(pair.Value!.DeepClone());
                    events[pair.Key] = array;
                }
            if (events.Count > 0 || hookRoot.ContainsKey("hooks")) hookRoot["hooks"] = events;

            var nextReceipt = new JsonObject { ["owner"] = Owner, ["client"] = client.ToString() };
            if (installing)
            {
                nextReceipt["server"] = nextServer.DeepClone();
                nextReceipt["hooks"] = nextHooks.DeepClone();
                if (newBlock is not null) nextReceipt["tomlBlock"] = newBlock;
            }
            // Do not create empty files when removal has nothing owned to remove.
            AddChange(changes, targets[0], "用户级 MCP：来源固定为 " + Source(client), mcpBytes, newMcp, installing);
            AddChange(changes, targets[1], "自动提醒 Hooks（不读取对话文件）", hookBytes, EncodeJson(hookRoot, hookBytes), installing);
            AddChange(changes, targets[2], "大头鹰安装归属记录（只用于安全更新 / 移除）", manifestBytes, EncodeJson(nextReceipt, manifestBytes), installing);
            return new()
            {
                ServiceId = _id, Client = client, Action = action, CanApply = true, Changes = changes,
                Files = changes.Select(c => new SetupFilePreview(c.Path, c.Description, c.Changed)).ToArray(), Notes = notes,
                Status = changes.Any(c => c.Changed) ? installing ? "预览完成，确认后安装 MCP + 自动完成提醒。" : "只移除由此安装器创建且未被改动的配置。" : installing ? "已配置，无需重复写入。" : "没有本安装器可移除的配置。",
            };
        }
        catch (Exception ex) when (ex is SetupException or IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException)
        {
            return new()
            {
                ServiceId = _id, Client = client, Action = action, CanApply = false,
                Status = ex is SetupException ? ex.Message : "配置无法安全读取或形状不受支持，未修改任何文件。请检查文件格式、权限和路径。",
                Notes = notes, Files = targets.Select(path => new SetupFilePreview(path, "预期配置路径（尚未修改）", false)).ToArray(),
            };
        }
    }

    public ClientSetupResult Apply(ClientSetupPlan plan)
    {
        lock (_gate)
        {
            if (plan.ServiceId != _id || !plan.CanApply) return new(false, "此预览无效，请重新检查配置。", []);
            if (!plan.HasChanges) return new(true, plan.Status, []);
            var backups = new List<string>();
            var written = new List<SetupFileChange>();
            try
            {
                // Include unchanged read dependencies in this comparison; a newly
                // added manual Hook after Preview must invalidate the entire plan.
                foreach (var change in plan.Changes)
                    if (!Equal(Read(change.Path), change.Before)) throw new SetupException("预览后配置发生变化，请重新预览；没有应用过期设置。");
                var index = 0;
                foreach (var change in plan.Changes.Where(c => c.Changed))
                {
                    BeforeWriteForTest?.Invoke(index++);
                    CheckPath(change.Path);
                    if (!Equal(Read(change.Path), change.Before)) throw new SetupException("配置正被其他程序修改，本次安装已中止。");
                    Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
                    if (change.Before is not null)
                    {
                        var backup = change.Path + ".eagle-backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
                        WriteNew(backup, change.Before);
                        backups.Add(backup);
                    }
                    AtomicWrite(change.Path, change.After);
                    written.Add(change);
                    if (!Equal(Read(change.Path), change.After)) throw new SetupException("写入后校验失败，请检查文件保护策略。");
                }
                return new(true, plan.Action == SetupAction.Install ? "配置已保存。请在客户端审核 Hooks / MCP，并重新开启会话；未代替你授权或重启客户端。" : "已移除本安装器的设置，其他 MCP 和 Hooks 已保留。请重新开启客户端会话。", backups);
            }
            catch (Exception ex) when (ex is SetupException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                var incomplete = false;
                foreach (var change in written.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (!Equal(Read(change.Path), change.After)) { incomplete = true; continue; }
                        if (change.Before is null) File.Delete(change.Path);
                        else AtomicWrite(change.Path, change.Before);
                    }
                    catch { incomplete = true; }
                }
                var cause = ex is SetupException ? ex.Message : "写入失败（可能是权限、文件占用或保护策略）。";
                return new(false, cause + (incomplete ? "部分文件无法安全回滚；没有覆盖外部修改，请使用列出的备份人工检查。" : "已回滚本次已写入的文件。"), backups);
            }
        }
    }

    private string[] GetTargets(ClientKind client)
    {
        var folder = client switch { ClientKind.Codex => _codex, ClientKind.ClaudeCode => Path.Combine(_home, ".claude"), ClientKind.CodeBuddyCode => Path.Combine(_home, ".codebuddy"), _ => throw new ArgumentOutOfRangeException(nameof(client)) };
        return [client switch { ClientKind.Codex => Path.Combine(folder, "config.toml"), ClientKind.ClaudeCode => Path.Combine(_home, ".claude.json"), _ => Path.Combine(folder, "mcp.json") },
            Path.Combine(folder, client == ClientKind.Codex ? "hooks.json" : "settings.json"), Path.Combine(folder, "eagle-desktop-pet.install.json")];
    }

    private static IEnumerable<string> GetNotes(ClientKind client)
    {
        yield return "只支持本机 Windows 客户端；不适用于网页聊天、远程 / WSL 会话或其他同名产品。小鹰需要保持运行。";
        yield return "只发送固定的“有新的回复”提醒，不读取会话文件、不发送回答内容、不调用 AI API。Stop 不代表任务已验证成功。";
        yield return "确认应用后会创建同目录备份。备份可能包含原配置中的私密信息，请勿分享或上传。移动发行包后需重新配置。";
        yield return client switch
        {
            ClientKind.Codex => "Codex：用户级 config.toml + hooks.json；需在客户端审核并信任 Hooks。不会自动绕过信任、启用被禁用的功能或改动公司策略。",
            ClientKind.ClaudeCode => "Claude Code：不是 Claude Desktop 普通聊天。本版命令 Hook 使用 Git Bash，需安装 Git for Windows 并接受工作区信任；用 /hooks、/mcp 查看后重新开启会话。",
            _ => "CodeBuddy Code（CLI）：不是网页或普通 IDE 聊天。本版采用官方 Hooks 文档的 Git Bash 命令形式；须安装 Git for Windows，并在 /hooks 审核变更、重新开启会话。",
        };
    }

    private static string Source(ClientKind client) => client switch { ClientKind.Codex => "Codex", ClientKind.ClaudeCode => "Claude Code", _ => "CodeBuddy Code" };
    private static JsonObject MakeServer(ClientKind client, string executable) => new()
    {
        ["type"] = "stdio", ["command"] = executable,
        ["args"] = new JsonArray("--source", Source(client)),
        ["env"] = new JsonObject { ["EAGLE_PET_SETUP_OWNER"] = Owner },
    };

    private static JsonObject MakeHooks(ClientKind client, string executable)
    {
        JsonObject Handler(string kind)
        {
            string command;
            if (client == ClientKind.Codex)
            {
                // EncodedCommand contains only our fixed absolute path and fixed
                // arguments. User/assistant payload is never a shell fragment.
                var script = "& '" + executable.Replace("'", "''") + "' --hook " + kind + " --client " + client + " --owner " + Owner;
                command = "powershell.exe -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            }
            else
            {
                // Git Bash supports a drive-qualified forward-slash executable.
                command = "'" + executable.Replace('\\', '/').Replace("'", "'\"'\"'") + "' --hook " + kind + " --client " + client + " --owner " + Owner;
            }
            var handler = new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = 8 };
            if (client == ClientKind.Codex) handler["statusMessage"] = Owner + " completion notification";
            return new JsonObject { ["hooks"] = new JsonArray(handler) };
        }
        var result = new JsonObject { ["Stop"] = Handler("stop") };
        if (client != ClientKind.Codex) result["UserPromptSubmit"] = Handler("prompt");
        return result;
    }

    private static void RemoveOwnedHooks(JsonObject events, JsonObject? previous)
    {
        if (previous is null) return;
        foreach (var pair in previous)
        {
            if (pair.Key is not ("Stop" or "UserPromptSubmit") || !ContainsOwned(pair.Value) || !ContainsPet(pair.Value))
                throw new SetupException("安装归属记录中的 Hook 无法验证，拒绝修改。");
            var array = Array(events, pair.Key);
            var matches = array.Where(item => JsonNode.DeepEquals(item, pair.Value)).ToArray();
            if (matches.Length > 1) throw new SetupException("托管 Hook 出现重复，无法判断归属；请人工检查。");
            if (matches.Length == 1) array.Remove(matches[0]);
            else if (ContainsOwned(array)) throw new SetupException("大头鹰 Hook 已被手动改动，安装器不会覆盖或删除它。");
            if (array.Count > 0) events[pair.Key] = array;
            else events.Remove(pair.Key);
        }
    }

    private static void ValidateHookShapes(JsonObject events)
    {
        foreach (var pair in events)
        {
            if (pair.Value is not JsonArray groups) throw new SetupException("Hook 事件不是数组，无法安全合并。");
            foreach (var group in groups)
            {
                if (group is not JsonObject obj || obj["hooks"] is not JsonArray handlers ||
                    obj.TryGetPropertyValue("matcher", out var matcher) && (matcher is not JsonValue matcherValue || !matcherValue.TryGetValue<string>(out _)))
                    throw new SetupException("现有 Hook 分组格式不受支持，无法安全合并。");
                foreach (var handler in handlers)
                    if (handler is not JsonObject entry || String(entry, "type") is not { Length: > 0 })
                        throw new SetupException("现有 Hook 定义格式不受支持，无法安全合并。");
            }
        }
    }

    private static void RemoveExact(JsonObject root, string key, JsonObject? previous, string label)
    {
        if (!root.TryGetPropertyValue(key, out var current)) return;
        if (previous is null || !JsonNode.DeepEquals(current, previous)) throw new SetupException(label + " 同名设置不属于本安装器，或已被手动修改；不覆盖 / 不移除。");
        root.Remove(key);
    }

    private static string RemoveOwnedBlock(string text, string? block)
    {
        if (block is null)
        {
            if (text.Contains(BeginMarker, StringComparison.Ordinal) || text.Contains(EndMarker, StringComparison.Ordinal))
                throw new SetupException("发现缺少归属记录的托管标记，不能安全接管已有配置。");
            return text;
        }
        if (!block.StartsWith(BeginMarker + "\n", StringComparison.Ordinal) && !block.StartsWith(BeginMarker + "\r\n", StringComparison.Ordinal) ||
            !block.EndsWith(EndMarker + "\n", StringComparison.Ordinal) && !block.EndsWith(EndMarker + "\r\n", StringComparison.Ordinal) || Toml.Parse(block).HasErrors)
            throw new SetupException("安装归属记录中的 TOML 区块无法验证，拒绝修改。");
        var blockModel = Toml.ToModel(block);
        if (blockModel.Count != 1 || !blockModel.TryGetValue("mcp_servers", out var blockServers) || blockServers is not TomlTable serverTable ||
            serverTable.Count != 1 || !serverTable.TryGetValue(ServerName, out var blockServer) || blockServer is not TomlTable ownedServer ||
            ownedServer.Count != 2 || !ownedServer.TryGetValue("command", out var command) || command is not string commandText ||
            !commandText.EndsWith("EagleDeskPet.Mcp.exe", StringComparison.OrdinalIgnoreCase) ||
            !ownedServer.TryGetValue("args", out var arguments) || arguments is not TomlArray args || args.Count != 2 ||
            args[0] is not "--source" || args[1] is not "Codex")
            throw new SetupException("安装归属记录包含非大头鹰设置，拒绝修改。");
        var first = text.IndexOf(block, StringComparison.Ordinal);
        if (first < 0)
        {
            if (text.Contains(BeginMarker, StringComparison.Ordinal) || text.Contains(EndMarker, StringComparison.Ordinal))
                throw new SetupException("大头鹰 TOML 区块已被手动改动，安装器不会覆盖或删除它。");
            return text;
        }
        if ((first > 0 && text[first - 1] != '\n') || text.IndexOf(block, first + block.Length, StringComparison.Ordinal) >= 0)
            throw new SetupException("托管 TOML 标记不唯一或不在行首，无法安全修改。");
        return text.Remove(first, block.Length);
    }

    private static JsonObject Object(JsonObject root, string key, bool required)
    {
        if (!root.TryGetPropertyValue(key, out var value)) return required ? throw new SetupException("缺少必要对象。") : new JsonObject();
        return value as JsonObject ?? throw new SetupException("配置项 " + key + " 不是对象，已停止。");
    }
    private static JsonArray Array(JsonObject root, string key)
    {
        if (!root.TryGetPropertyValue(key, out var value)) return new JsonArray();
        return value as JsonArray ?? throw new SetupException("Hook 事件 " + key + " 不是数组，已停止。");
    }
    private static string? String(JsonObject root, string key) => root[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool PetText(string value) => value.Contains("EagleDeskPet", StringComparison.OrdinalIgnoreCase) || value.Contains("eagle-desktop-pet", StringComparison.OrdinalIgnoreCase) || value.Contains("eagle-pet", StringComparison.OrdinalIgnoreCase) || value.Contains("pet_notify", StringComparison.OrdinalIgnoreCase);
    private static bool ContainsPet(object? value) => value switch
    {
        string text => PetText(text),
        TomlTable table => table.Any(pair => PetText(pair.Key) || ContainsPet(pair.Value)),
        System.Collections.IEnumerable items => items.Cast<object?>().Any(ContainsPet),
        _ => false,
    };
    private static bool ContainsPet(JsonNode? value) => value switch
    {
        JsonObject obj => obj.Any(pair => PetText(pair.Key) || ContainsPet(pair.Value)),
        JsonArray array => array.Any(ContainsPet),
        JsonValue scalar => scalar.TryGetValue<string>(out var text) && PetText(text),
        _ => false,
    };
    private static bool ContainsOwned(JsonNode? value) => value?.ToJsonString().Contains(Owner, StringComparison.Ordinal) == true;
    private static void AddChange(List<SetupFileChange> changes, string path, string description, byte[]? before, byte[] after, bool installing)
    {
        if (!installing && before is null) return;
        changes.Add(new(path, description, before, after));
    }
    private static string Absolute(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.Any(char.IsControl)) throw new ArgumentException("必须是本机绝对路径。");
        return Path.GetFullPath(path);
    }
    private static void CheckPath(string path)
    {
        var current = Absolute(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new SetupException("配置路径含符号链接 / 目录联接，安装器不会沿链接修改文件。");
            current = Path.GetDirectoryName(current);
        }
    }
    private static byte[]? Read(string path)
    {
        CheckPath(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new SetupException("配置文件过大，无法安全自动合并。");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        _ = Decode(bytes);
        return bytes;
    }
    private static string Decode(byte[]? bytes)
    {
        if (bytes is null) return "";
        var span = bytes.AsSpan();
        if (span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) span = span[3..];
        var text = StrictUtf8.GetString(span);
        if (text.Contains("%TSD-Header-", StringComparison.Ordinal)) throw new SetupException("配置被 TSD 加密，无法直接安全合并。请在获准的编辑器中手动配置。");
        return text;
    }
    private static JsonObject ReadJson(byte[] bytes)
    {
        var text = Decode(bytes);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateKeys(document.RootElement);
        return JsonNode.Parse(text) as JsonObject ?? throw new SetupException("JSON 配置根节点必须是对象。");
    }
    private static void RejectDuplicateKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in node.EnumerateObject())
            {
                if (!keys.Add(pair.Name)) throw new SetupException("JSON 配置含重复键，无法无损合并。");
                RejectDuplicateKeys(pair.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) RejectDuplicateKeys(item);
    }
    private static string Newline(byte[]? original) => Decode(original).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    private static byte[] EncodeJson(JsonObject value, byte[]? original)
    {
        // Preserve exact bytes if semantic content is unchanged (idempotence,
        // including original indentation and ordering).
        if (original is not null && JsonNode.DeepEquals(value, ReadJson(original))) return original;
        return Encode(value.ToJsonString(Pretty).Replace("\n", Newline(original)) + Newline(original), original);
    }
    private static byte[] Encode(string text, byte[]? original)
    {
        var body = StrictUtf8.GetBytes(text);
        return original is not null && original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(body).ToArray() : body;
    }
    private static bool Equal(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private static void WriteNew(string path, byte[] bytes)
    {
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        CheckPath(path);
        var temp = path + ".eagle-tmp-" + Guid.NewGuid().ToString("N");
        try { WriteNew(temp, bytes); CheckPath(path); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private sealed class SetupException(string message) : Exception(message);
}
