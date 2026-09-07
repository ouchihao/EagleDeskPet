# 大头鹰 AI 通知桥（v1.8）

v1.8 可在小鹰右键「AI 自动传话 · 一键接入」中配置 Codex、Claude Code、CodeBuddy Code 的本机 MCP 和自动完成 Hook；先预览、点击后才备份合并，不改真实账号或信任记录。步骤、冲突处理和撤销见 [一键接入指南](AI-AUTO-SETUP.md)。下面保留手动配置和协议说明。

宠物本身仍是 `EagleDeskPet.exe`。可选的 `EagleDeskPet.Mcp.exe` 是单独的控制台进程：每个 AI 客户端启动自己的一份，通过标准输入/输出使用 MCP，再把事件发给本机当前 Windows 用户的宠物。不开网络端口，不上传聊天记录，没有 API Key，也不自动启动或结束其他应用。

MCP **不是通用的聊天监听器**。客户端须主动调用 `pet_notify`；如果想在每次最终回复后自动通知，需要该客户端提供的 Hook/扩展事件。没有这些能力的网页 AI 暂时不会自动通知。`reply_ready` 只表示有回复，不证明任务成功、测试通过或代码已验证。

## 手动连接

先打开同一发布目录中的 `EagleDeskPet.exe`（源码发布路径为 `dist/v1.8.0/`）。把下面配置按客户端要求填写到其 MCP 设置中；本段示例不自动执行写入。路径要换成你实际保存的位置。示例是常见的 `mcpServers` 格式，具体配置结构以客户端文档为准：

```json
{
  "mcpServers": {
    "eagle-pet": {
      "command": "D:\\Apps\\EagleDeskPet-v1.8.0\\EagleDeskPet.Mcp.exe",
      "args": ["--source", "Claude Code"]
    }
  }
}
```

其他客户端使用各自的 `--source`，例如 `Gemini CLI`。这是应用显示名称，不是模型名。工具参数没有 `source` 字段，模型无法通过工具调用改名。请由你配置可信的名称。它不构成对客户端真实身份的密码学证明：同一 Windows 账户下的本地程序仍可自行设置名字。

可在客户端的个人指令中加入：“在有值得提醒我的最终回复、需要我处理的问题或任务失败时，调用 `pet_notify`，使用稳定的事件 ID、简短中文摘要。不要发送敏感内容或完整聊天记录。”这种指令只是调用约定，并不能保证每个客户端每轮都调用。

## 两个工具

`pet_notify`：

```json
{
  "eventId": "session-123:turn-8:reply",
  "sessionId": "session-123",
  "eventType": "reply_ready",
  "message": "回复好了，快回来看看。"
}
```

- `eventId`：必填，1–128 字符，同一事件重试保持不变。
- `eventType`：必填，`reply_ready`、`needs_attention` 或 `task_failed`。
- `sessionId`：可选，最多 128 字符，仅作去重分组，不是 URL/可执行命令。
- `message`：可选，最多 240 字符的简短纯文本；不支持控制字符或双向文本隐藏字符。
- 不接受额外参数，不执行消息中的指令、路径或 URL，不读取聊天文件。

`pet_get_state` 不带参数，读取 GUI 提供的本地宠物状态，不读取任何 AI 的回复或会话。回复中的 `accepted`/`status` 明确区分 `accepted`、`duplicate`、`busy`、`rate_limited`、`unavailable` 和 `invalid_request`。`duplicate` 表示之前已接收，本次没有再次弹泡；`accepted` 是加入本地交互流程，并不保证用户已看到。GUI 不运行时会返回工具错误，绝不伪装成送达。

`state` 保留 `level`、`fullness`、`mood`、`food`、`paused`、`notificationsEnabled`、`pendingNotifications` 和 `currentAction` 等字段，v1.6 增加：

- `isWorking`：是否正在累计本次工作时间和工作收益；取消或饿空后变为 `false`，此时收工动画仍可能在播放。
- `isBusy`：本次工作持续到 30 分钟后为 `true`；忙碌动画在当前循环完整结束后接入，不截断帧。
- `workSessionSeconds`：本次工作的累计秒数，返回值保留一位小数。
- `activeBanterEnabled`：用户是否开启每分钟本地职场碎碎念；与 AI 通知开关相互独立。

v1.7 另增加 `githubConnected` 和 `githubUnread`，仅表示连接状态和本地未读条数，不向 MCP 暴露 Token、GitHub 账号、通知标题或仓库名。GitHub 消息由主程序直接检查，不通过这两个 MCP 工具接入；设置见 [GitHub 指南](GITHUB-NOTIFICATIONS.md)。

这些都是只读状态。两个 MCP 工具不提供远程喂食、摸头、开工、收工或改设置的操作。通知只显示紧凑气泡，来源写在正文中，不再占用单独的名字栏；不会把站立/打哈欠强行切成别的动作，也不会中断办公、忙碌或收工动画。本地碎碎念优先级更低，已有消息时跳过该分钟，不累积补播。

通知对应应用的“返回/打开”入口只能由用户在宠物本地界面配置；MCP 不接收任意启动目标，也不负责打开会话链接。

## 可选 Hook / 本地手动测试

对于支持结束事件的客户端，在其 Hook 中调用一次性通知模式即可，不必从 Hook 再建立 MCP 会话：

```powershell
& 'D:\Apps\EagleDeskPet-v1.8.0\EagleDeskPet.Mcp.exe' --source 'Claude Code' --notify --event-id 'session-123:turn-8:reply' --event-type reply_ready --message '回复已准备好' --session-id 'session-123'
```

或者调用 `tools/pet_notify_hook.ps1`，传入 `BridgeExe`、`Source` 和稳定 `EventId`，其他参数可选。该脚本只是通用示例；需由具体客户端适配它提供的事件字段，不能原样假设所有客户端都支持。请在 Hook 配置中固定应用名称，不从回复文字中解析它。无须也不建议把聊天记录全文发给宠物。每次真实新事件使用新 ID，网络/进程重试使用原 ID。一次性模式退出码：0 已接收/重复，1 未接收，2 参数错误。

## 安全和节奏边界

- Windows 当前用户 SID 派生的命名管道名，服务端/客户端均使用 `PipeOptions.CurrentUserOnly`；不支持跨 Windows 用户或远程连接。宠物与客户端请使用相同运行账户和权限级别。
- 每包不超过 16 KiB，4 个固定监听工作者；读写有超时，GUI 回调限时 2 秒。MCP 标准输入每行最多 64 KiB，超限中断连接。
- 全部客户端合计至少间隔 3 秒、每分钟最多 20 个通知；按来源、会话和事件 ID 去重 10 分钟，最多保留 256 个 ID。GUI 重启后去重记录清空，不是永久消息存储。
- 通知正文和通知桥的 10 分钟去重缓存只保留在内存。v1.8 自动 Hook 另在 `hook-state` 保存哈希文件名及随机轮次标识，移除配置不删除这些小文件，见一键接入指南。没有长期对话记忆、模型请求、账号读取或聊天监听。
- 此接口是同用户本地辅助功能，不是对同账户恶意软件的安全沙箱。不要把它暴露成公网服务。

## 开发与验证

实现采用官方 [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 的 `ModelContextProtocol.Core` 2.2.0（支持 .NET 8），不是手写 JSON-RPC 协议。NuGet 依赖版本锁定在 `EagleDeskPet.Mcp/packages.lock.json`。SDK 负责握手、工具协议和 JSON-RPC，宠物侧只负责两个业务工具。

```powershell
dotnet build .\EagleDeskPet.Mcp\EagleDeskPet.Mcp.csproj -c Release
dotnet build .\tools\McpBridgeHarness\McpBridgeHarness.csproj -c Release
python .\tools\test_mcp_bridge.py --dotnet dotnet --mcp .\EagleDeskPet.Mcp\bin\Release\net8.0-windows\EagleDeskPet.Mcp.dll --harness .\tools\McpBridgeHarness\bin\Release\net8.0-windows\McpBridgeHarness.dll
```

集成测试使用无界面的测试宿主和随机独立通道，发送通知前强制核验测试标记；测试不会关闭任何用户进程。覆盖实际 MCP 握手/列工具/调用、管道送达、来源绑定、去重、频率限制、输入校验和宠物未启动时的错误。尚未替任何实际 AI 客户端完成配置或验证其 Hook 事件。

发布后的实际 WPF 联调可运行：

```powershell
python .\tools\test_gui_smoke.py --gui .\dist\v1.8.0\EagleDeskPet.exe --mcp .\dist\v1.8.0\EagleDeskPet.Mcp.exe
python .\tools\test_work_gui_smoke.py --gui .\dist\v1.8.0\EagleDeskPet.exe --mcp .\dist\v1.8.0\EagleDeskPet.Mcp.exe
python .\tools\test_features_gui_smoke.py
```

前两个 GUI runner 为本次 GUI 和 MCP 同时配置随机 `EAGLE_PET_TEST_CHANNEL`，隔离单实例互斥锁和当前用户命名管道，并设置独立的测试存档目录；发送通知前还会比对精确 PID、测试标记和数据目录。普通运行不设置此变量，管道名称和单实例行为保持不变。测试通道只允许 1–64 个 ASCII 字母、数字或短横线，非法值拒绝运行，不回落到正常用户通道。工作联调用加速的测试状态覆盖进场、持续办公、30 分钟忙碌切换、取消和饿空收工；它不是长时间实机 60 FPS 或人工鼠标拖动验收。

第三个 runner 验证菜单、荣誉墙、GitHub 窗口和 v1.8 一键接入，同样使用独立数据和测试通道；以模拟 HTTP 响应和测试 Token 驱动真实通知服务与 Windows DPAPI，客户端安装只写注入的临时目录，不修改真实 AI 配置，不请求真实 GitHub、不启动浏览器。
