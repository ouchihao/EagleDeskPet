# v1.9 Demo 验证记录

更新：2026-09-20。历史基线为 v1.8.0 / `46cd0df`。以下记录本轮实际执行证据；[v1.8 验证记录](VALIDATION-v1.8.md)只作历史对照，不自动继承为新服装、商品或整机回归通过。

当前状态：**v1.9 Demo 功能已实现，最终自包含 EXE 9/9 联调通过**（`fullAcceptance=true`，89.64 秒）。它包含追加家具 / 卫衣、桌子入场遮挡修复和喂饭交接；三套完整资源 102 项检查、48 组合 WPF 复查及逐帧桌身遮挡测试也已通过。跨 DPI、人工拖动、长期稳定性、真实账号投递和物理 60 FPS 不在本次认证范围。变化摘要见 [本版更新](RELEASE-NOTES-v1.9.md)。

## 最新真实宿主结果

核读 `EagleExpansionSmoke-3bf4fb8966094928bdd90850c03e5fc3/evidence/expansion-smoke.json`：`passed=true`、`completed=true`、`fullAcceptance=true`，9 个 case 全为 passed，89.64 秒；实际 `dist/v1.9.0-verified/EagleDeskPet.exe`、真实渲染时钟，存档及命名管道均隔离。启动命令未传 `DotnetRoot` 或 `SkipOutfit`：

```powershell
.\tools\run_expansion_smoke.ps1 -Exe .\dist\v1.9.0-verified\EagleDeskPet.exe
```

| 场景 | 结果 |
| --- | --- |
| 新数据、测试资金、情绪默认关闭及开关落盘 | 通过 |
| 真实菜单 / 商店、预览不改拥有权、重复购买不二扣 | 通过 |
| 任务按来源排序、乱序 / 终态保护、已读 / 清空、正文不入存档 | 通过 |
| 第三次摸头抗议且不重复发奖励 | 通过 |
| 饥饿后只吃一份；哈欠中喂饭，立刻开工 / 暂停被拒绝，2.1 秒后再喂仍不重复扣粮，Eat 完整结束才释放占用 | 通过 |
| 工作保持原桌 / 电脑，完整退场后应用待装备 | 通过 |
| 实际两局猜拳，出拳完整结束才揭晓，冷却奖励只保存一次 | 通过 |
| 办公服预览、购买、切换、实际哈欠和回默认 | 通过 |
| 五件追加商品逐一预览 / 按价购买 / 去重 / 装备，卫衣哈欠、电竞工位办公与装备保存 | 通过 |

热态卫衣 + 电竞桌 + 电竞电脑 WorkLoop 的三秒样本记录 180 次 WPF 渲染回调：平均 **59.67 Hz**、间隔 P95 **16.67 ms**、最大 **33.33 ms**，`physicalDisplayCertified=false`。这是本机短时回调观察，不是显示器 Present、长期帧率或其他机器性能保证。

扩展 smoke 曾在上一运行的第 9 case 失败：前一件装备仅已准备好，测试尚未等下一次真实渲染应用它，就开始后一个预览的状态指纹检查。修复测试为先等实际槽位生效，没有放宽“预览不得改变主宠物”的断言；本次 9/9 为修正后的结果。早先 7+1 / 8-case 记录见下方历史过程，不作为最终结论。

普通 Release 构建也曾以相同 9 项通过（90.10 秒，`EagleExpansionSmoke-852d18d4edde42cc8980c784dd655715`），此处以之后的自包含发布产物为最终验收对象。

### 最终发布产物

本轮使用新的 `dist/v1.9.0-verified/`，保留先前目录，未覆盖旧包。EXE 与最终 ZIP 的体积及 SHA256 均已重新读取核对：

| 文件 | 字节 | SHA256 |
| --- | ---: | --- |
| EagleDeskPet.exe | 547326291 | `041661A56D5A43855664E06328526AC8411703D71176D92DF80CC7BB64A9ABBD` |
| EagleDeskPet.Mcp.exe | 35307409 | `DC6BC9B54F63CF37A1F636F4D8B03A6A9452F81BE903F5534F953A9A56F10E9D` |
| EagleDeskPet-v1.9.0-win-x64.zip | 567193245（约 540.92 MiB） | `2630ECE1193F188C817E0DE2C306D1D9129C8471C4197ECB88BEE10A1F550B6A` |

`publish.ps1` 在两个程序发布成功后附带 `THIRD-PARTY-NOTICES.md`，分发须保留。新增复制步骤已用隔离无构建 fixture 验证逐字节一致，不将该 fixture 视为 EXE 验证，也不因此另造本项目 LICENSE 或素材授权。

最终自包含 `EagleDeskPet.Mcp.exe` 另通过 `test_mcp_bridge.py` 的 3 组真实协议回归，日志为独立回归目录下的 `McpBridge-final-selfcontained.log`：包含握手、工具调用、来源绑定、去重 / 限流、一次性通知及宿主离线错误，全程使用测试宿主与随机通道。

## 本轮确定性与隔离集成测试

这些是各套件自己的用例 / 断言数，不相加成“全部独立场景”；不同测试层可能检查同一规则。

| 测试入口 | 本轮结果 | 能证明与不能证明的边界 |
| --- | --- | --- |
| DuckDeskPet.SelfTest | 38 / 38 | 原核心逻辑回归，不是实屏帧率 |
| EconomySelfTest | 44 / 44 | 工资、零头、两小时 / 饥饿截断、回拨、迁移、原子保存、幂等；本次启动收入含启动补算且不因购物减少 |
| ContentSelfTest | 54 / 54（已重跑 12 项目录） | 内容 ID、拥有权、奖励、购买与装备规则；不是所有图片的目视验收 |
| EmotionSelfTest | 49 / 49 | 阈值、迟滞、冷却、连摸和完整场景调度 |
| BanterSelfTest | 39 / 39（2026-09-20 新增情境回归） | 60 条普通 + 16 条情境、固定随机种子可复现、共享分钟边界、情境切换不加频率、跳过不积压、睡眠与重新启用 |
| SceneCatalogSelfTest | 25 / 25（含最后遮挡修复回归） | 场景 / 插槽 / 资源结构与安全边界 |
| OutfitSelfTest | 102 / 102：89 隔离 + 13 真实资源检查 | 默认 / 办公服 / 卫衣各 19 段及站姿，完整解码 6900 PNG；不把解码当作物理屏幕表现 |
| RpsSelfTest | 23 / 23 | 九组合、先选拳、节奏、取消、奖励和真实独立 WPF 控件 |
| PlayIntegrationSelfTest | 16 / 16 | 生产 Play partial + 真游戏窗口 / 时间线 / PetStore；仅宿主外壳和位图解码器为替身 |
| FeedingIntegrationSelfTest | 15 / 15 | 生产 Feeding partial、Core、PetStore；队列准入 / 合并 / 满队列、重复消费、新序列完整结束、缺粮 / 冷却、CAS 冲突和关闭；时钟 / 渲染外壳受控 |
| TaskNotificationSelfTest | 90 项通过 | 规则、真实命名管道、实际 MCP SDK stdio / CLI、并发及限流；未连接真实 AI 客户端 |
| GitHubSelfTest | 46 / 46 | 模拟 HTTP 响应、合成凭据与隔离数据；没有连接真实 GitHub 账号 |
| ClientSetupSelfTest | 50 / 50 基础隔离用例；另验证实际 Codex 命令包装层 | 配置合并 / 冲突 / 回滚、Hook 解析与隐私；本轮 Claude / CodeBuddy 的真实 Git Bash 包装层未完成，见下文 |
| ShopSelfTest | 21 / 21 | 真交易与 WPF 控件，隔离存档 / 可控资源；人工确认与实际宿主另测 |
| HonorUiSelfTest | 25 / 25 | 荣誉与奖励 UI 规则 / 控件；不把全部卡片可展示当作真实玩家已解锁 |
| ExpansionIntegrationSelfTest | 14 项通过 | 真实局部 WPF 场景、深浅底 / 尺寸快照、完整独立预览及关闭释放 |
| WorkSceneV2SelfTest | 5 项通过（含最后遮挡修复回归） | 默认工作场景 WPF 与遮挡回归；其他组合另由全 48 组合测试覆盖 |

原 PetStore 7、Honor 12、WorkStage 10 项亦有本轮基础回归记录，详见 [实施记录](IMPLEMENTATION-v1.9.md)。套件使用临时存档或模拟配置 / 传输；未把真实用户存档、AI 配置或 Token 用作测试输入。

桌面主工程与 MCP 本轮 Release 构建均通过，零警告、零错误；最终自包含主程序验证及哈希见上节。桥接兼容性和客户端配置的原有安全边界不因新增任务字段而放宽。

### 2026-09-20 独立回归补记

证据目录 `.codex-build/regression-v1.9-20260920-012614-99ddf14a/` 保留各次标准输出、独立 WPF 图片和重跑记录，不纳入 Git。本次重跑 Core 38、Economy 44、Content 54、Emotion 49、Banter 39、PetStore 7、Honor 12、GitHub 46、ClientSetup 基础 50、RPS 23、Play 16、Task 90、Shop 21、Honor UI 25 全部通过；不相加为独立产品场景总数。

Economy / Content 的命令需提供绝对仓库根；首次缺参只输出 Usage，已修正[开发文档](DEVELOPMENT.md)并重跑。目录从 7 项扩充为 12 项后，Content 的容量 fixture 仍按旧商品数填充，曾有 1 项失败；改为遍历实际无奖励商品后，`Content-updated-fixture.log` 确认 54/54。原始失败日志保留，不当作当前业务规则失败，也不隐藏重跑过程。

本轮 `ClientSetupSelfTest` 的基础配置 / Hook 用例 50/50 通过，实际生成的 Codex PowerShell 命令向隔离管道投递也通过；Claude Code 的 Git Bash 包装层两次在 12 秒超时，后续 CodeBuddy 分支未执行。独立最小探针 `bash --noprofile --norc -c "printf ready"` 同样 10 秒未退出，定位到本机 Git Bash 启动环境这一验证阻碍，尚不能据此判断生产 Hook 有缺陷或证明其可用。未更改真实客户端配置或修复机器环境；v1.8 的 53 项记录仅为历史，不冒充本次 53 项全过。

`McpBridgeHarness` Release 构建及 `test_mcp_bridge.py` 也通过，`McpBridgePython.log` 保留 3 组成功输出，覆盖原有握手 / 列工具 / 调用、来源绑定、去重、限流、输入安全、命名管道、一次性通知和宿主离线报错。发布配置用 `msbuild -p:PublishProfile=win-x64 -getProperty:Version,PublishDir` 验证，实际解析为 `1.9.0` 和 `..\dist\v1.9.0\`；这是配置检查，不是已经发布最终包。

独立批次没有和制作中的资源并发验收；后续由资源复查运行补齐三套 Outfit 与全部组合 / 入退场测试，记录见资源 QA。新增喂饭复审发现“先扣粮后被队列合并 / 清空”的交接缺口，现以明确喂饭占用和只读准入预检修复，冷却与站立节奏没有改动；`EagleFeedingIntegration-3c6d7b8f77064e76ab58196dfcc82685/results.json` 确认 15/15，真实 9-case 的第 5 项又验证主窗口接线。

## 历史阶段性 smoke（不是最新结论）

通过 `tools/run_expansion_smoke.ps1` 启动当时的实际 Release EXE；不是 shell double，不手动驱动动画时间线。测试总用时约 **54.18 秒**，进程正常退出。

| 实际宿主场景 | 结果 |
| --- | --- |
| 全新隔离数据、种入测试资金、自动情绪默认关闭、开关落盘 | 通过 |
| 真实菜单、商店预览 / 关闭、预览不改拥有权、重复购买不二扣 | 通过 |
| 多来源任务列表、乱序与终态保护、已读 / 清空、正文不进 JSON | 通过 |
| 10 秒内第三次摸头播放 Annoyed，只有第一次有效摸头奖励 | 通过 |
| 饥饿小场景先退场，连续喂食只消费一份并完整播放 Eat | 通过 |
| 工作中桌子 / 电脑保持原样，完整退场后应用待装备 | 通过 |
| 真实两局猜拳，出拳结束才揭晓；第一局保存心情与冷却、第二局不重复奖 | 通过 |
| 完整办公服购买、预览、换装及回默认 | **跳过**：明确使用 `-SkipOutfit` |

这次阶段性证据位于临时根 `EagleExpansionSmoke-3377db5c37334869828958e87e79eecc/evidence/`，包含 `expansion-smoke.json` 和自己 WPF 窗口 PNG；临时目录不随 Git 分发。

### 首批完整运行补记

随后重新构建真实 Release EXE，不带 `-SkipOutfit` 运行；报告 `EagleExpansionSmoke-6255642d135d4335bc430045085045a7/evidence/expansion-smoke.json` 已核读：`passed=true`、`completed=true`、`fullAcceptance=true`，8 个 case 全为 passed，60.61 秒。新增出拳中段断言通过；办公服 case 用时约 6.64 秒，验证资源完整性、独立预览不改拥有权、购买 / 切换、实际哈欠与回默认。

这曾取代“首批服装 smoke 尚未跑”的状态，但不把上表历史 skipped 改写成当时已经通过。随后新增卫衣 / 家具、桌入场修复及喂饭修复；最新 9-case 与资源组合结果已记录在本页顶部。

购买验证调用真实事务协调器，不自动应答生产购买确认 MessageBox。预览 / 装备检查允许自然工作计时与合法工资变化，不把正常养成推进误判为状态污染。截图只来自程序自己的视觉树，没有截图桌面、聊天窗口或其他应用。

## 资源 QA

- 默认形象新增 10 段 / 1150 帧，分为三段饥饿、喝茶、五段猜拳、烦躁。
- 办公服 19 段 / 2299 帧及站姿，运行 PNG 约 141.335 MiB。
- 摸鱼卫衣 19 段 / 2299 帧及站姿，运行 PNG 约 146.72 MiB；三套注册形象共 6900 PNG 已完整解码。
- [办公服自动审计](../DuckDeskPet/Assets/AnimationPreviews/Office/outfit-audit.json) 通过，19 段最终 QA、关键帧锁定与 10 个场景端点一致；不以文件数量代替完整人体 / 道具遮挡观察。
- 内存估算、制作工具和资源出处见 [素材说明](ASSETS-v1.9.md)。内存表是像素预算，不是整机峰值测量。

最终组合证据 `.codex-build/outfit-scene-all48-final/report.json`：48 工作组合、六阶段、五关键进度、深浅底、1.0 / 1.28 场景缩放，另有四桌饥饿组合；6000 张真实 WPF 合成、221 张汇总图，失败 0。全部 48 工作组合接触表及卫衣动作交接已复查。

完整入退场证据 `.codex-build/desk-occlusion-all3-final/motion-report.json`：三套 × 四桌的工作进入 / 两种退出及饥饿进入 / 退出，共 30480 张逐帧合成、840 页，失败 0；桌身重叠像素 912300、泄露身体像素 0。这里的零泄露是所测身体 / 桌面遮挡规则，不是任意美术缺陷都不存在的承诺。`.codex-build/outfit-final-three-packs.log` 保留三套解码与 102 项结果，工具峰值工作集 638.0 MiB 包含隔离 fixture，不代表真实宿主常态内存。

## 未完成 / 不作保证的项目

- 人工购买确认、长时间交互稳定性、多屏拖动、跨 DPI、远程桌面和长期物理 60 FPS。
- 真实 GitHub 账号投递，以及各种 AI 客户端版本的实际 Stop / 任务生命周期投递。本轮没有修改真实客户端配置。
- 角色 / 第三方参考素材的完整商业授权；生成资源存在不等于获得原角色商用许可。

后续补充结果时记录实际构建、命令、证据和限制；只更新已执行项目，不把全部需求验收框机械勾选。运行入口见 [开发与测试](DEVELOPMENT.md)，需求状态见 [需求池](REQUIREMENTS.md)。
