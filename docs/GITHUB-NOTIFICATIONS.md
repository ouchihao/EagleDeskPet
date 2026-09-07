# 大头鹰 · GitHub 消息（v1.7 起，适用 v1.8）

这是小鹰直接连接 GitHub.com 的可选通知功能，不需要 AI、MCP、仓库管理员权限或另外部署服务器。没有配置 GitHub 账号时，不会发起 GitHub 网络请求；不连接也不影响养宠物。

## 第一次连接

本版使用 **Personal access token (classic)** 连接个人通知收件箱，不是 OAuth 网页登录，也不收集 GitHub 密码。通知接口目前不支持细粒度 PAT 或 GitHub App Token，依据 [GitHub 通知接口说明](https://docs.github.com/en/rest/activity/notifications)。

1. 右键小鹰，点“GitHub 消息…”，展开“连接与隐私设置”。
2. 点“去 GitHub 创建 Token ↗”。在浏览器登录你自己的 GitHub，创建 **Tokens (classic)**，为它设置便于辨认的名称和有效期。具体入口见 [GitHub Token 管理指南](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-personal-access-token-classic)。
3. 权限范围只勾选 `notifications`。本程序不读取仓库代码或评论正文，不需要额外勾选 `repo`、`workflow`、组织管理等权限。
4. 把新 Token 粘贴到小鹰窗口的密码式输入框，点“验证并连接”。验证账号及通知接口成功后，窗口显示 `已连接 @你的账号`。输入框会清空，凭据仅在本机加密保存。

不要把 Token 发给 AI、同事或贴进源码、聊天记录、截图。每位同事都应连接自己的账号。

注意：`notifications` **不是 GitHub 强制限定的只读权限**，它还允许标记通知已读、管理订阅等操作。本程序通过实现限制，只发送 GET 读取请求，不调用这些写接口；请仍像保护密码一样保护 Token。[权限范围定义](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/scopes-for-oauth-apps#available-scopes)

## 什么消息会通知我

默认“与我有关”包括提及你或你的团队、请求你评审、分配给你的事项，以及你发起、评论或手动订阅的 PR 动态。勾选“显示全部订阅通知”可显示其他通知类型；这只改变小鹰的显示过滤，不修改 GitHub 的订阅设置。

GitHub 返回的是讨论线程的更新，不是完整聊天事件流。曾经 `@` 过你的线程，之后的普通回复仍可能沿用 `mention` 原因。因此小鹰使用“曾提及你的讨论有更新”等措辞，**不保证每次提醒都是一次新的 @，也不声称能判断评论内容或是谁刚刚回复**。[通知原因语义](https://docs.github.com/en/rest/activity/notifications#about-notification-reasons)

- 第一次连接时，现有未读会安静放入列表，不把旧消息逐条弹出来；之后拉到的新线程版本才提醒。
- 相同线程、更新时间和最新评论版本不会反复播报；线程再次更新时会重新成为本地未读。
- 切换到“全部订阅通知”会展示已获取的历史，但不会把这些旧记录当成新消息补播。
- 小气泡消失不清除未读，小鹰旁的小红点及右键菜单会保留未读数量。消息窗口最多保留最近 **200 条本地记录**，不是 GitHub 完整历史归档。
- 只接收 GitHub 通知收件箱实际返回的未读线程；被取消订阅、没有进入收件箱，或在小鹰下次检查前已在其他客户端读掉的动态，可能不会被采集。

## 检查频率与打开消息

正常约每 60 秒检查一次；如果服务器要求更长间隔则延长。实现使用 `Last-Modified` / `If-Modified-Since` 条件请求，遵守 `X-Poll-Interval`，遇到网络错误会逐步退避，并遵守限流等待。手动“检查消息”也不会跳过间隔限制。这是分钟级轮询，不是秒级推送。[GitHub 轮询要求](https://docs.github.com/en/rest/activity/notifications#about-github-notifications)

新消息复用小鹰的小气泡，不改变正在待机或办公的动画；正在拖动、暂停动作、已有其他消息或气泡占用时等待可展示时机。养成面板的通知开关关闭时，不弹 GitHub 气泡，但后台检查及消息列表仍可用。

点气泡或列表的“去看看”，由默认浏览器打开对应 PR、Issue 或 Commit；能可靠转换最新评论链接时附带评论锚点，否则打开讨论页或 GitHub 通知中心。只接受经过校验的 `https://github.com` 讨论地址，不执行通知里任意 URL、命令或设置页跳转。

“本地全部看过”和成功打开消息只改变**小鹰本地**的阅读状态，不调用 GitHub 标记已读接口。浏览器打开后 GitHub 网站自身如何记录阅读，仍由网站行为决定；两边未读数量不承诺实时一致。

## 本机数据与断开连接

默认目录为 `%LOCALAPPDATA%\EagleDeskPet`：

| 文件 | 内容与保护 |
| --- | --- |
| `github-credential.dpapi` | Token 的 Windows DPAPI 加密数据，仅绑定当前 Windows 用户环境，不是明文配置。 |
| `github-inbox.json` | 账号名、通知标题、仓库名、安全链接、更新时间、去重和本地阅读状态；**普通 JSON 明文**，可能含私有仓库名称或标题。 |

加密凭据不上传给 AI，不写入源码或发布 ZIP。GitHub API 请求直接发到 `https://api.github.com`，不经过本项目自建中转服务，且不会带着凭据跟随 HTTP 重定向。本版只支持 GitHub.com，尚不支持自托管 GitHub Enterprise Server 地址。

DPAPI 保护的是磁盘上的凭据，不能代替安全的 Windows 账号环境。不要把整个数据目录打包给同事、同步到公开仓库或用作公开问题附件；分享程序使用发布 ZIP 即可，源码也不应包含个人 Token 或收件箱。

“断开并清除本机消息”会停止检查、移除本机加密凭据、清空 GitHub 账号和收件箱内容，并清除尚未显示的 GitHub 提醒；养成进度不受影响。若文件清理失败，界面会提示，不能将它理解成已经清理成功。**本地断开不会撤销 GitHub 上的 Token**；彻底停用时，请在 GitHub 的 Token 管理页删除它。

## 常见情况

- **提示不支持细粒度 Token**：需使用本功能要求的 classic Token，不要尝试用 GitHub 密码替代。
- **401、已失效或撤销**：生成新 Token 后重新连接；程序不会无限重复无效认证。
- **403、权限不足**：检查 `notifications` 范围、Token 有效期及组织政策。使用 SAML SSO 的组织可能要求单独授权 Token；组织也可以禁止 classic PAT。遵守组织策略，不能通过多勾选权限绕过。[GitHub Token 与组织限制](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)
- **连接成功但没有想要的消息**：先确认 GitHub 网页通知中心有这条未读，再尝试勾选全部通知。关注/订阅设置、权限和本地过滤都会影响显示。
- **提示超过 2500 条未读**：为避免把不完整分页当作完整结果，本轮停止更新；先在 GitHub 整理旧通知后重试。小鹰不会替你批量清理。
- **没有弹气泡但有红点**：检查养成面板的通知开关、动作暂停状态和当前气泡；也可直接打开消息列表，不必等待气泡。
- **换电脑后加密凭据无法恢复**：在新机器重新连接即可，不要尝试公开分享解密后的 Token。

本指南对照 v1.7 源码与 2026-09-06 的 GitHub 官方文档编写；真实账号能否连接还取决于你的网络、权限及组织策略。
