# v1.7 验证与交付记录

日期：2026-09-06。项目：EagleDeskPet；下文路径均相对仓库根目录。本轮添加可选 GitHub 通知、三条路线共九项分级荣誉、独立荣誉展览窗口及平铺操作的新右键菜单。

## 构建与发布

`publish.ps1` 已成功发布两个 Windows x64、自包含、单文件 EXE 到 `dist/v1.7.0`；接收者无需安装 .NET。主程序和可选 MCP 桥版本均为 1.7.0。最终构建后再次完成下面三套实际 GUI 联调，随后打包；未使用真实 GitHub 账号或修改 AI 客户端配置。

| 产物 | 字节数 | SHA-256 |
| --- | ---: | --- |
| `EagleDeskPet.exe` | 151013505 | `7AFFAE50101AFE19D2FFECD87DA831CDB3AEE43973C593AFDD71BD4CC197FC23` |
| `EagleDeskPet.Mcp.exe` | 35300708 | `F60C4CCC22366CF2CD7F4B92C3B52E0A10074D990C31E01FF4F2B28E6B1CA4F9` |

发布 ZIP 只包含上述程序和使用/验证文档，不包含用户存档、GitHub 凭据、收件箱或测试目录。完整代码和生成素材保留在项目目录；徽章提示词和生成记录见 `docs/BADGE-ASSETS.md`。

## 已通过的检查

| 检查 | 结果 | 范围 |
| --- | --- | --- |
| GitHub 离线测试 | 46/46 组 | 生产 API 解析、条件轮询、分页、限流、断网恢复、账户切换、版本去重、URL 白名单、本地凭据和断开清理 |
| 荣誉逻辑 | 12/12 | 三条路线、九个门槛、旧荣誉兼容、进度、不变更存档/奖励 |
| 荣誉 WPF 布局 | 18/18 项断言 | 真实徽章解码、3/2/1 列、筛选、空态、进度条、刷新与滚动稳定 |
| 原逻辑与文件回归 | 82/82 | Core 38、Banter 9、PetStore 7、WorkStage 10、动画资源 4、SciPy 等价性 14 |
| 最终 EXE 新功能联调 | 17/17 项应用内断言 | 实际 WPF 菜单/大小子菜单、养成面板点击荣誉墙、GitHub 窗口/红点/气泡、真实 DPAPI 配合模拟 HTTP |
| 最终 EXE 日常 GUI + MCP | 通过 | 身份隔离、实际 MCP 通知、喂食 5→4、干饭、保存重读和正常退出 |
| 最终 EXE 工作 GUI + MCP | 通过 | 入场、连续办公、转忙、取消及饿空退场、道具清理、通知不打断工作、碎碎念设置保存 |
| 最终 MCP 协议回归 | 通过 | 实际 SDK 握手、列工具、工具调用、来源绑定、去重/限流、输入/Unicode 校验、一次性 Hook 和宠物离线错误 |

GitHub 的 46 组中，一组覆盖 12 种异常账户 JSON（根节点、ID 类型/范围及登录名错误），均验证连接失败后清除“连接中”状态、不写凭据/收件箱、按间隔后可重新连接。另有 Windows 真实文件锁测试，验证禁用状态写入和凭据删除各自失败、同时失败的清理结果与提示。没有把模拟授权当成真实账号连通证明。

最后发布版的本地证据目录（相对于项目根目录）：

- `.codex-build/features-gui-smoke-04oh825v/gui-smoke.json`：新功能结果；同目录包含菜单、荣誉墙、GitHub 连接/收件箱/气泡和红点 PNG。
- `.codex-build/gui-smoke-trcqj3k0/gui-smoke.json`：日常 GUI + MCP。
- `.codex-build/work-gui-smoke-2phqi5i1/gui-smoke.json`：持续办公 GUI + MCP；场景头部锚点漂移 0 像素，退场后释放本轮工作帧。
- `.codex-build/honor-ui-smoke/honor-ui-report.json`：荣誉独立窗口布局 18 项断言及多宽度截图。
- `.codex-build/pet-store-test/20260906-115546-ae71e5de677f4926a390f89c915eb50d/results.json`：隔离存档、异常文件和正常用户数据未变验证。

截图来自测试程序自身的 WPF 可视树，不是用户桌面截屏。所有 GUI/MCP 集成测试使用随机独立通道和新建数据目录，只清理自己创建的测试子进程；不停止用户正在运行的宠物。GitHub 测试使用合成 Token，不访问真实 API、不读取已有登录、不打开浏览器。

## 数据和安全边界

- 未配置 GitHub 时不请求 GitHub。连接后只使用固定 `api.github.com` HTTPS GET，关闭带凭据重定向；`notifications` classic scope 本身也有写能力，但本程序不调用写接口。
- Token 使用当前 Windows 用户 DPAPI；GitHub 通知标题和仓库名是本地普通 JSON，最多 200 条。请勿公开分享数据目录。
- 旧气泡携带精确版本键，点击不会误清线程后续新版本的本地未读；本地已读不写回 GitHub。外部 MCP 消息无法注入浏览器启动链接。
- 断开先保存禁用状态，再独立删除凭据；有清理失败则提示。若两项磁盘操作同时失败，不能保证下次启动不会恢复连接，界面会明确警告；用户仍可到 GitHub 撤销 Token。
- 制作时本地参考库的 39 个原始 GIF 哈希未变。原图不随公开仓分发；可选本地目录为 `References/Memes/`。旧 v1.6 ZIP 保留，SHA-256 仍为 `68CA13BFA291CE774DF4D5E003273B44E436493D832166378458F632DB777AD6`。
- 父仓库仍将 `desktop-pet/` 视为未跟踪目录；本轮未暂存、提交或推送。`git diff --check -- desktop-pet` 无报错，但不会检查未跟踪源码，源码主要由构建、针对性审查和上述测试验证。

## 尚未验证或未包含

- 用户真实 GitHub Token、组织 SSO 策略、公司网络、真实 @/PR 消息到达，以及默认浏览器实际打开链接；需要用户在本机完成一次授权和真实消息验收。
- 秒级推送、所有 GitHub 历史/评论事件、GitHub Enterprise Server、自建 OAuth/Webhooks 服务。轮询约每 60 秒，服务器间隔或限流可能更长；GitHub 线程原因不能保证每次都是新 @。
- 人工鼠标拖动、混合 DPI 多屏交互、物理显示器长期稳定 60 FPS。本轮没有新增角色动作帧，保留既有 60 Hz 动画调度；工作测试加速养成时钟，不代替真实半小时挂机验证。
- 更多解锁动作、趴桌哭场景、多角色换肤、联网养成和 AI 长时记忆仍是后续工作。

复现入口：`tools/GitHubSelfTest`、`tools/HonorSelfTest`、`tools/HonorUiSelfTest`、`tools/test_features_gui_smoke.py`。旧逻辑与 GUI/MCP 复现说明沿用 README、`docs/MCP.md` 和 v1.6 验证记录，但当前发布路径为 `dist/v1.7.0`。
