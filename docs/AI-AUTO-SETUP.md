# 大头鹰 v1.8 · 一键接入 AI

这是配置**本机 AI 客户端**，不是安装或配置本地模型。不需要新的 API Key，不更改模型或账号，也不会读取聊天记录。

## 怎么用

1. 完整解压发布 ZIP，把 `EagleDeskPet.exe` 和 `EagleDeskPet.Mcp.exe` 放在同一固定目录。先退出旧版，再启动新版。
2. 右键小鹰 → **AI 自动传话 · 一键接入**；也可从「我的饭搭子 → AI 传话与返回应用」进入。
3. 选择 Codex、Claude Code 或 CodeBuddy Code，查看实际文件路径和提示。此时只读，不会写配置。
4. 点击「一键接入」。程序备份原文件，再合并自己的 MCP 和自动提醒 Hook，保留其他设置。显示“配置已保存”不等于客户端已信任或已经成功送达。
5. 在客户端检查 MCP / Hooks，审核信任新 Hook，再开启新会话。保持小鹰运行，用一次真实回复验证提醒。

Codex 的非托管 Hook 需要审核信任；可用 `/hooks` 或客户端提供的 Hook 审核入口。安装器不写信任记录，也不会打开已禁用的 Hooks 或绕过组织策略。[官方 Hooks 说明](https://learn.chatgpt.com/docs/hooks)

Claude Code 和 CodeBuddy Code 的这套 Windows Hook 使用 Git Bash 命令形式，请安装客户端要求的 Git for Windows。本功能不是 Claude 网页/普通桌面聊天，也不承诺所有 CodeBuddy IDE 扩展都采用相同配置。[Claude Code Hooks](https://code.claude.com/docs/en/hooks)、[CodeBuddy Code Hooks](https://www.codebuddy.ai/docs/cli/hooks)

## 写在哪里

`用户目录` 指当前 Windows 账户的主目录；预览中会显示完整绝对路径。

| 客户端 | MCP | Hooks |
| --- | --- | --- |
| Codex | `用户目录/.codex/config.toml` | `用户目录/.codex/hooks.json` |
| Claude Code | `用户目录/.claude.json` | `用户目录/.claude/settings.json` |
| CodeBuddy Code | `用户目录/.codebuddy/mcp.json` | `用户目录/.codebuddy/settings.json` |

Codex 尊重绝对路径 `CODEX_HOME`。若设置了 `CLAUDE_CONFIG_DIR` 或 `CODEBUDDY_CONFIG_DIR`，本版明确暂停对应客户端的自动配置，不猜测自定义目录映射，也不写默认目录。[Claude MCP 配置](https://code.claude.com/docs/en/mcp)、[CodeBuddy 目录说明](https://www.codebuddy.ai/docs/cli/codebuddy-dir)

各客户端配置目录内还会写入 `eagle-desktop-pet.install.json`，记录本安装器创建的内容，用于识别、更新和移除自己的条目。不要删除此归属记录后再指望安装器能安全接管旧配置。

## MCP 与自动提醒的区别

- MCP 注册服务器 `eagle-desktop-pet`，来源名固定为 `Codex`、`Claude Code` 或 `CodeBuddy Code`，提供原来的 `pet_notify` / `pet_get_state`。
- 自动提醒走 `Stop → 命令 Hook → EagleDeskPet.Mcp.exe → 本机小鹰`，发送与 `pet_notify` 相同的通知操作，不需要模型决定是否调用工具，也不需要额外启动一次 AI 会话。
- Codex 用会话和轮次 ID 去重。Claude/CodeBuddy 用额外的 `UserPromptSubmit` Hook 保存随机轮次标识，随后 Stop 使用该标识去重；不读取提示词、回答或 `transcript_path` 指向的文件。
- 只发送固定的简短“有新的回复”提醒；Stop 不代表任务成功、测试通过或代码已验证。递归 Stop、子代理结束和无效输入不播报。
- Hook 返回中性的 `{}`，小鹰不在线、管道繁忙或超时都不阻断、不续写模型。因通知桥的频率限制，极短时间内多条完成提醒可能不会全部弹出，不是可靠消息队列。

如果既要求 AI 每轮主动调用 `pet_notify`，又启用了自动 Stop，可能产生两条不同事件。建议自动 Stop 管普通完成提醒，AI 主动通知只留给失败或需要你处理的事。

## 已经手动接入过怎么办

你已有的 Codex Stop Hook 可以继续使用。安装器发现已有大头鹰 MCP、`pet_notify` Hook 或冲突的手动设置时，会停止新增，避免一条回复提醒两次；不会擅自迁移、覆盖或删除你的配置。已有 Codex 内联 `[hooks]` 时，本版也不自动混用 `hooks.json`。

该检查针对当前用户配置层，不能穷尽项目、插件或组织层里的重复 Hook。配置后仍应在客户端查看有效 Hooks，避免在其他层重复注册。

## 备份、更新与移除

修改前的备份与原文件在同一目录，文件名含 `.eagle-backup-时间-随机标识`，成功或失败后会列出位置。**备份可能包含原配置中的 API Key 等私密信息，不要上传、分享或放进程序发布包。**

重复接入不重复添加。移动桌宠目录后，重新打开新位置的桌宠并接入，可更新本安装器管理的路径；用户手动改过的托管条目会提示冲突，不会强行覆盖。

「移除小鹰配置」只移除归属明确、未被手动修改的条目，保留其他 MCP 和 Hook。预览后文件被外部修改则拒绝应用过期内容；写入失败尝试回滚，不能安全回滚时明确提示并保留备份。保存期间会阻止普通退出，完成后再退出；系统强制结束或断电不在多文件事务保证内。

无法解析、重复 JSON 键、TSD 密文、非 UTF-8、符号链接/目录联接或其他不安全形状会停止自动配置，请在获准的编辑器里处理。客户端配置中的原有账号和密钥只被原样保留/备份，不发送给小鹰或 AI。

辅助去重文件在 `%LOCALAPPDATA%/EagleDeskPet/hook-state`，只保存哈希文件名和随机轮次标识；移除配置不删除这些小文件，也不影响养成进度。

本轮验证使用隔离目录、模拟事件、实际 PowerShell/Git Bash 和通知桥，没有改动你的真实客户端配置。仍需在你使用的客户端版本、权限和组织策略下完成一次真实回复验收。
