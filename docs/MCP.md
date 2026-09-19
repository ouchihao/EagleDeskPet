# 大头鹰 AI 通知桥与任务小信使

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
- `eventType`：旧式通知必填，`reply_ready`、`needs_attention` 或 `task_failed`。按任务更新时可省略，规则见下节。
- `sessionId`：可选，最多 128 字符，仅作去重分组，不是 URL/可执行命令。
- `message`：可选，最多 240 字符的简短纯文本；不支持控制字符或双向文本隐藏字符。
- 不接受额外参数，不执行消息中的指令、路径或 URL，不读取聊天文件。

`pet_get_state` 不带参数，读取 GUI 提供的本地宠物状态，不读取任何 AI 的回复或会话。回复中的 `accepted`/`status` 明确区分 `accepted`、`duplicate`、`busy`、`rate_limited`、`unavailable` 和 `invalid_request`。`duplicate` 表示之前已接收，本次没有再次弹泡；`accepted` 是加入本地交互流程，并不保证用户已看到。GUI 不运行时会返回工具错误，绝不伪装成送达。

### 按任务更新（REQ-005）

旧客户端、旧 `pet_notify` 调用和已有手动 Hook 无需修改。支持明确任务事件的适配器，可在同一工具中使用以下扩展；新的 GUI 与桥接器需要配套更新。这里的 `taskId` 是大头鹰业务字段，**不是 MCP 协议的 Tasks 扩展或自动任务监听**。

```json
{
  "eventId": "build-47:2",
  "taskId": "project-a:build-47",
  "revision": 2,
  "status": "waiting",
  "message": "需要你选择下一步方案。",
  "occurredAt": "2026-09-19T08:00:00Z",
  "isReplay": false
}
```

示例时间只用于解释字段；实际适配器必须填写事件真实发生时间，不能原样发送历史示例来测试实时提醒。

| 字段 | 约定 |
| --- | --- |
| `taskId` | 1–128 个可打印字符的 opaque ID；与 `revision`、`status` 同时提供。不要放用户输入、文件路径或敏感任务名。 |
| `revision` | 0–9007199254740991 的 JSON 整数；每个任务严格递增，重连不重置。同一事件重试保持原 `eventId` 和版本。 |
| `status` | `running`、`waiting`、`reply_ready`、`succeeded`、`failed` 之一；仅能报告来源实际提供的事实。 |
| `occurredAt` | 可选，含 `Z` 或时区偏移的 RFC3339 时间；最多 7 位小数秒。超过本机时间 2 分钟的未来事件拒绝，超过 24 小时的历史事件忽略。 |
| `isReplay` | 可选布尔值，默认 `false`。重连/历史导入必须传 `true`，只更新收件箱、不播报。 |

任务键为 **`(source, taskId)`，区分大小写**。同一来源下，不同会话也必须使用不同任务 ID；`sessionId` 不参与新任务身份。两台同名适配器也应自行避免 ID 冲突。来源绑定仍由用户的 `--source` 配置决定，不能在工具参数里覆盖。

| 来源状态 | UI 含义 | 提醒策略 | 若同时提供 `eventType`，只能填写 |
| --- | --- | --- | --- |
| `running` | 进行中 | 安静更新 | `task_running` |
| `waiting` | 等待你处理 | 新的重要状态可提醒 | `needs_attention` |
| `reply_ready` | 回复就绪，不代表成功 | 新的重要状态可提醒 | `reply_ready` |
| `succeeded` | 来源明确报告成功，不代替独立验收 | 新的重要状态可提醒 | `task_succeeded` |
| `failed` | 来源明确报告失败，不补写错误原因 | 新的重要状态可提醒 | `task_failed` |

更新和过期规则：

- 同任务只保留一条最新状态。重复事件 ID、相同或更低版本不更新；版本较高才继续检查状态转移。
- 成功/失败为终态，不能改回运行、等待、回复或相反的终态；更高版本可以补充同一终态的摘要。重新执行须使用新的任务 ID。
- 不由消息正文猜状态。原来的 Codex / Claude Code / CodeBuddy Code Stop Hook **仍只发送无任务标识的 `reply_ready`**，不伪造开始、成功或失败，也不会因为本次扩展自动获得完整任务生命周期。
- 非终态以来源事件时间（未提供时为接收时间）为准，10 分钟未更新显示“状态未知 · 已过期”，清除旧正文与待播提醒；这不意味着任务失败或完成。来源如果仍掌握运行事实，可用更高版本明确更新。
- 明确终态在保留期内仍显示来源报告的结果。每个任务从最后一次接受更新起最多保留 24 小时，最多 64 项；超限淘汰最早更新项。淘汰后另保留最多 512 条无正文的版本/终态标记，继续拦截迟到回退；这些标记也按最后更新起 24 小时到期。事件去重最多 512 个 ID / 24 小时。超过容量或保留期、应用重启等场景不承诺永久去重，来源仍必须避免重放历史。
- 已超过 2 分钟的事件自动按历史处理，不新建未读或气泡；已有未读不会被历史刷新抹掉。没有时间、没有 `isReplay` 的导入无法可靠判断原始年龄，因此适配器不能省略重连标记。
- 同状态的摘要/进度刷新不反复提醒。同任务的待播内容合并为最新版本；等待/失败优先。最多 8 条待播、每条最多等 30 秒，任务气泡至少间隔 10 秒、每分钟最多 6 次，不补播过期积压。工作、忙碌、收工和拖动不被任务消息切换动作。

右键「任务小信使」可查看未读、标记已读、全部已读、清空、静音气泡或关闭任务接收。静音仍更新收件箱，恢复不补播；关闭接收不新增任务记录。两个开关只在本次运行有效，不修改客户端配置；原有 AI 通知总开关仍具有优先权。打开应用只使用用户在本机选择的入口，不保证定位到具体任务。GitHub 仍使用原消息面板和真实通知接口，评审请求不被映射为 CI 成败。

任务正文和状态 **仅驻留进程内存，不落盘、不写入 `pet_get_state`、不暴露给其他 MCP 客户端**。关闭程序清空；手动清空立即去除正文和可见项，但保留有界的 ID/版本标记，防止近期重试把已清消息恢复。来源必须最小化摘要，勿发送密钥或聊天全文。返回 `accepted` 仅表示 GUI 已处理该事件；旧版本、终态回退、历史过期或用户关闭任务接收时可能被安静忽略，不保证状态改变或用户已读。

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

一次性 EXE 模式也支持 `--task-id ID --revision N --status waiting`，并可传 `--occurred-at RFC3339时间`、`--replay true|false`；省略 `--event-type` 时由任务状态确定语义。旧 PowerShell 示例脚本不自动升级为任务适配器；既有 Stop Hook 保持旧式回复提醒。

## 安全和节奏边界

- Windows 当前用户 SID 派生的命名管道名，服务端/客户端均使用 `PipeOptions.CurrentUserOnly`；不支持跨 Windows 用户或远程连接。宠物与客户端请使用相同运行账户和权限级别。
- 每包不超过 16 KiB，4 个固定监听工作者；读写有超时，GUI 回调限时 2 秒。MCP 标准输入每行最多 64 KiB，超限中断连接。
- 旧式事件仍合计至少间隔 3 秒、每分钟最多 20 个通知。带完整任务身份的更新独立限制为每分钟最多 120 次接收，气泡另按上节低频限制。桥接去重缓存合计最多 512 个 ID / 10 分钟，旧式键为来源/会话/事件，任务键为来源/任务/事件；GUI 任务收件箱另保留有界版本记录。GUI 重启后缓存清空，不是永久消息存储。
- 通知正文和通知桥的 10 分钟去重缓存只保留在内存。v1.8 自动 Hook 另在 `hook-state` 保存哈希文件名及随机轮次标识，移除配置不删除这些小文件，见一键接入指南。没有长期对话记忆、模型请求、账号读取或聊天监听。
- 此接口是同用户本地辅助功能，不是对同账户恶意软件的安全沙箱。不要把它暴露成公网服务。

## 开发与验证

实现采用官方 [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) 的 `ModelContextProtocol.Core` 2.2.0（支持 .NET 8），不是手写 JSON-RPC 协议。NuGet 依赖版本锁定在 `EagleDeskPet.Mcp/packages.lock.json`。SDK 负责握手、工具协议和 JSON-RPC，宠物侧只负责两个业务工具。

任务扩展继续使用官方 [Tools 的 JSON Schema 参数规则](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)，不宣称实现 MCP Tasks。纯逻辑、实际管道及可选真实 MCP stdio 的隔离自测：

```powershell
dotnet run --project .\tools\TaskNotificationSelfTest\TaskNotificationSelfTest.csproj -c Debug
dotnet build .\EagleDeskPet.Mcp\EagleDeskPet.Mcp.csproj -c Debug
dotnet run --project .\tools\TaskNotificationSelfTest\TaskNotificationSelfTest.csproj -c Debug -- --mcp .\EagleDeskPet.Mcp\bin\Debug\net8.0-windows\EagleDeskPet.Mcp.dll --dotnet dotnet
```

不传 `--mcp` 会明确跳过 SDK 子进程测试，其余逻辑和实际命名管道仍运行。测试采用随机隔离通道、模拟事件和固定来源，发送前核验测试宿主；不读取或写入真实客户端配置，不读取真实任务正文，不需要 GitHub Token。GUI 窗口与动画的人工验收不能由纯逻辑测试替代。

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
