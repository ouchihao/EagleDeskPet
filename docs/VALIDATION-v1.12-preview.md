# v1.12 预览版 · 实现与验证记录

<a id="preview5"></a>
## 2026-09-30 · preview.5 加大跟随、减少哈欠

本机产物：`dist/v1.12.0-preview.5-gaze/EagleDeskPet.exe`，489,907,809 字节；产品版本 `1.12.0-preview.5+ad48556cba94a4fa94823fb6a00c9ffa4605258b`。SHA256：`A6E1F9401687DE21ECCB4D05085723FAF404633AD36D5975ABCE33139B773B48`。退出旧版后启动新 EXE，保留同目录 `Live2DStage`；沿用现有 SDK 配置，不改变存档或经济规则。

头部跟随参数提高约三分之一：待机 X/Y/Z 为 30/16/16，办公 24/12/12，忙碌 22/11/10；普通瞳孔两轴提高至 ±1，忙碌保留黄色无瞳孔眼。当前模型实际额外歪头上限约 5.33° / 4° / 3.33°，不改身体和脚点。

自动哈欠与交互间隔拆开：累计有效待机 45～90 秒随机触发，暂停、工作与摸鱼不积攒哈欠；工作结束重新安排间隔。排队互动仍为原来的 2 秒站立拍点，手动哈欠不必等待自动计时；动作本身仍为 121 帧 / 2 秒。

- Core 58 / 58、Emotion 49 / 49：覆盖 45 / 90 秒边界、随机中值、暂停 / 工作 / 摸鱼、手动哈欠、交互 2 秒及长帧限制
- JavaScript 动作 / 注视 / 摸鱼 49 / 49；六套真实 Core / Stage 166 / 166，4,565 次绘制，脚点最大位移与 GL / 运行 / 非有限顶点错误均为 0
- 已审 160px 图册和肩颈 / 眼睛 / 眼镜的极值画面，未见新增镂空或瞳孔越界；证据 `.codex-build/preview5-gaze-20260930-185754`
- 最终自包含 EXE 原业务 7 / 7，26.58 秒，实际 `live2d-native-hybrid`；证据 `%TEMP%/EagleExpansionSmoke-1bef7592d722485d91cf958197f7defa/evidence/live2d-smoke.json`
- 最终自包含 EXE 两种摸鱼完整流程通过，40.49 秒；证据 `%TEMP%/EagleExpansionSmoke-104c08865e034c42a70654e880c7c104/evidence/loafing-smoke.json`

发布的 `gaze.js` / `stage.js` 指纹与本轮实际 Stage 测试一致。本轮未重复上一版真实鼠标宿主 60 项检查，不将旧测试记作新结果；跨屏 DPI、物理帧率与长期稳定性边界不变。测试只用隔离窗口 / 数据，没有操作真实存档、关闭用户桌宠或 push。

<a id="preview4"></a>
## 2026-09-30 · preview.4 可见的鼠标跟随与办公注视

本机产物：`dist/v1.12.0-preview.4-gaze/EagleDeskPet.exe`，489,907,755 字节；产品版本 `1.12.0-preview.4+15bbfade65f956df5cb8f58e8e4f442220059a9e`。SHA256：`CF73A067A145E3B0A3B714CFE2DFD4CAA0B4FBF7792838CA1EEA4C448A999153`。使用前退出旧进程，保留同目录 `Live2DStage`；沿用本机 SDK 配置及存档 v4，不修改经济规则、不再次清币。

旧版真实宿主基线 48 / 48：站立时 `GetCursorPos → PointFromScreen → TickGaze → WebView` 已正常传递，不能归因为鼠标坐标丢失。主要可见度问题是 160 DIP 图像中瞳孔自身单向偏移上限仅约 0.365 / 0.156 DIP，加上脸附近方向目标较小，视觉反应弱。办公则确实被宿主的 `InteractionsUnavailable`（包含工作）与 Stage 的 Idle-only 条件禁用。

本轮提高脸附近响应与头眼幅度，增加朝光标侧的轻歪头，开放稳定 WorkLoop / BusyLoop；只叠加头眼，不动打字、身体、脚点或工位。办公跟随保留原低头基线和模型实际参数范围；忙碌保留黄色无瞳孔眼型。修复同一办公循环换序号时重复过渡，以及非摸鱼的 `none → none` 暂停消息导致一帧归中的问题。

| 验证 | 结果 / 边界 |
| --- | --- |
| 规则与构建 | GazeSelfTest 41 / 41；主程序和真实宿主测试工具构建均 0 警告 / 0 错误 |
| JS 动作 / 注视 / 摸鱼 | 共 49 / 49；动作叠加、参数边界与原有协议通过 |
| 六套真实 Core / Stage | 166 / 166；4,551 次实际绘制，脚锚最大位移 0，GL / 运行 / 非有限顶点错误为 0；审查 160px 对比及肩颈极值 |
| 暂停连续性 | 按真实宿主消息顺序测试，头部归一化偏移首帧 0.912、100ms 后 0.567、1.5s 后 0.000289；不是一帧硬回正 |
| 真实 WPF 输入链路 | 60 / 60，24 场景；本机 125% DPI，三种宠物尺寸、四向、近距离、办公 / 忙碌和开关 / 拖动门控 / 暂停 / 装备切换 / 菜单 / 摸鱼 / 远离 |
| 最终自包含 EXE 原业务 | 7 / 7，26.68 秒，实际 `live2d-native-hybrid`；完整吃饭、办公至忙碌与退出、暂停、装备与独立预览 |
| 最终自包含 EXE 摸鱼 | 40.09 秒通过，实际 Live2D；两种表情完整进出、装备保持、零收益、暂停与取消 |

本机证据：

- `.codex-build/gaze-real-host-baseline-20260930-e`：旧版真实宿主基线
- `.codex-build/gaze-real-host-preview4-final-20260930`：最终宿主报告、隔离画布和源码指纹
- `.codex-build/gaze-office-pause-final-20260930-182051`：六套 Stage 报告、暂停关键帧、160px 图册与录像
- `%TEMP%/EagleExpansionSmoke-6f6edd236d1b4f60853636fb8cd7611c/evidence/live2d-smoke.json`：最终 EXE 原业务
- `%TEMP%/EagleExpansionSmoke-d090d469fd9f40038e5bcaeb6cc86398/evidence/loafing-smoke.json`：最终 EXE 摸鱼

真实宿主工具加载同版普通构建程序集，移动自己的透明、不激活窗口，将系统当前光标转换为本地坐标；没有注入 JS 跟随目标、移动系统鼠标、截取桌面或读取真实养成存档。它不是自包含 EXE 的鼠标交互人工验收；最终 EXE 另跑上述业务与摸鱼回归。发布包的 `gaze.js` / `stage.js` 与两套最终跟随测试的 SHA256 一致。仍未认证混合 DPI 跨屏拖动、物理屏幕 60 FPS 或长期稳定性；没有替用户关闭旧版或 push 到远端。

<a id="preview3"></a>
## 2026-09-30 · preview.3 无薪摸鱼与头眼跟随

本机产物：`dist/v1.12.0-preview.3-loafing/EagleDeskPet.exe`，489,907,247 字节；产品版本 `1.12.0-preview.3+239f31bd19e8cc3dc0e0764ad1043c19b6ee5cb3`。SHA256：`D18AA77DC52C2DA38C8076F1FCAF36F5F7ACE9043FEEC750A1BFD63194E2C2C6`。保留同目录 `Live2DStage` 与第三方说明；Live2D 使用原已验证本机 SDK 配置，不把它宣称为任意电脑免配置 Live2D 分发包。

| 验证 | 结果 / 边界 |
| --- | --- |
| 核心 / 经济 / 存档 | 51 / 64 / 12 项通过，含确定性调度、重启随机状态、半小时无金币且无任何自动经验、离线封顶、回拨、v4 不再清币 |
| 素材与视觉时间轴 | 14 项通过，70 张原帧保持 250×250、字幕透明、脸内部不透明、道具完整分割、40 ms 往返播放、暂停和完整复工 |
| 光标规则 / 作者 / Stage | 18 / 39 / 47 项通过；六套真实 Core 模型四向移动有效，默认中立 / 半闭 / 闭眼 / 忙碌与旧版逐像素 0 差 |
| JS 动作 / 注视 / 摸鱼 | 24 / 11 / 10 项通过，独立协议、终点、暂停、眼型闭合切换与桌面遮挡 |
| 最终 EXE Live2D 摸鱼 | 41.19 秒通过，两种动作完整进出，资本家衣装 / 薄荷桌 / 黄金电脑保持；暂停、零收益、取消、Core 诊断通过 |
| 最终 EXE PNG 摸鱼 | 39.00 秒通过，同一组合与业务路径；不声称 PNG 可以独立转动模型眼睛 |
| 最终 EXE 原业务 | 7 / 7，27.1 秒；吃饭终帧、暂停、办公至忙碌与退出、装备生效、商店与小剧场独立预览、GL / 参数诊断 |
| 养成 UI / 发布清单 | 16 / 34 项通过；展示收益暂停，Stage 新增两个模块纳入允许列表 |

本机原始证据（不含真实用户资料，临时目录不进入 Git）：

- `.codex-build/loafing-visual-qa-final`：两种动作深浅底关键帧与 14 项结果
- `.codex-build/live2d-gaze-v3-six-stage-qa`：六套四向图册、47 项报告及本地 Stage 录像
- `%TEMP%/EagleExpansionSmoke-9cea0238ed6d4c9b8e1a7332d3b22dbd/evidence/loafing-smoke.json`：最终 Live2D 摸鱼
- `%TEMP%/EagleExpansionSmoke-b69c0c41a558496bbe1eb66761104365/evidence/loafing-smoke.json`：最终 PNG 摸鱼
- `%TEMP%/EagleExpansionSmoke-cae08489fbba4cb593b8ea91b4ca5b00/evidence/live2d-smoke.json`：最终业务 7 项

主机快照由实际 Core 画布与生产 WPF 叠层同一绘制路径合成，不采集用户桌面。已人工检查默认 / 资本家角色的抛电脑、藏桌后、GIF 循环、起身与回收电脑关键帧；没有把单张截图作为连续 60 FPS 证明。原 GIF 为 25 fps，转场与头眼采用连续采样；未认证物理显示帧率、长时间内存 / 稳定性、人工跨屏 DPI 操作以及摸鱼中途图形设备故障。Core 已测试存档恢复，真实进程在各转场点强制退出 / 重开尚未逐项审片。

新增木鱼 / 木槌使用 ImageGen，角色原 GIF 不重画；资源来源与提示词在 [Loafing 素材说明](../DuckDeskPet/Assets/Loafing/README.md)。修复过本机加载器的 320 像素强制缩放、半透明脸内残影、道具跨中线裁切、跨后端轨迹差异；最终 EXE 是上述修复后的产物。没有访问或修改真实宠物存档，没有新一轮清金币，没有 push 或创建远端 Release。

## preview.1 历史记录

日期：2026-09-22。范围是正式需求 REQ-009～011，不包括备选 IDEA 的自动立项。REQ-001～008 保留既有 Demo 实现。

## 交付范围

| 需求 | 实现 | 边界 |
| --- | --- | --- |
| REQ-010 记事本 | 软木图钉便签墙、新增/编辑、完成归档、恢复、删除确认、独立原子存档、键盘操作、窄窗与减弱动效 | 85 项隔离测试；真实主程序办公中操作联调；实体多显示器 DPI 尚未验收 |
| REQ-011 原生提醒 | 独立系统通知/气泡开关、测试通知、每轮去重账本、旧存档迁移、失败/不确定状态、点击路由、单实例转发 | 专用 EXE 的真实系统提交及通知历史通过；可见横幅、实际点击/冷启动点击等人工系统场景尚未验收 |
| REQ-009 Live2D | 透明 WebView2/WebGL 技术探针；版本化角色包契约、受限资源解析、校验 CLI、作者模板 | **未完成**：无大头鹰 Cubism 模型/绑定，未接生产渲染后端、动作和服装迁移；不是用测试图形代替成品 |

便签、提醒不改变工资、等级、装备、AI 或 GitHub 配置。默认原生通知关闭；测试没有读取/修改真实养成存档或账号，测试程序通知注册已单独清理。

## 可复现测试

入口见[开发与测试](DEVELOPMENT.md)，均从仓库根目录运行。

| 测试 | 本轮结果 | 覆盖内容 |
| --- | --- | --- |
| `NotebookSelfTest` | 85/85 | 状态、长度/数量边界、归档、冲突/损坏保护、真实 WPF 窗口与截图 |
| `CharacterPackSelfTest` | 55/55 | 参数映射、资源与路径边界、分层/锚点、动作契约、假的 moc3 不能成为 RuntimeReady |
| 既有规则回归 | 266/266 | Core 38、Economy 61、Content 56、Honor 22、PetStore 7、Emotion 49、FeedingIntegration 15、PlayIntegration 18 |
| `ReminderSelfTest` | 51/51 | 既有调度与存储规则兼容 |
| `NativeReminderSelfTest` | 40/40 | 原生状态机、字面量 v1 迁移、失败注册回滚、Unknown 重启与过期点击 |
| `ReminderIntegrationSelfTest` | 25/25（含渲染） | 独立通道、Prepare 失败不落盘、异步 Failed 持久化、旧轮次不能覆盖新状态、隔离 UI |
| 主程序 `run_expansion_smoke.ps1` | 13/13；真实 `win-x64` 自包含单文件，134 秒 | 保留原 12 项交互/商店/场景验证，增加工作中便签新增/归档/恢复/重开、原生替身通道与旧通知无副作用 |
| 真实 GUI + MCP 桥 | 通过 | 同一发布包读取状态、发送通知、喂食完整 Eat、截图、存档重载与正常退出 |
| 依赖审计 | `dotnet list package --vulnerable --include-transitive` 未发现已知漏洞 | 依赖源 NuGet.org，审计时点 2026-09-22；不是无漏洞承诺 |

生产 EXE 的第 13 项使用真实主窗口、菜单、便签窗口、提醒窗口和持久化，**仅 OS 传输替换为 fake**，因此不会把隔离 EXE 注册为真实用户的桌宠身份。测试不会证明真实 Windows 横幅已经出现或已读。

本轮主 EXE SHA256：`d9daa8b9b243a9ec6a2eb8e9ca26d68e119c7d8616dede5f34f38b900c02caf7`。可复用结果摘要见[本版证据摘要](validation/v1.12-preview-summary.json)。热身后卫衣工位的 WPF 回调约 59.68 Hz，P95 16.67 ms、最大间隔 33.33 ms；这不是物理屏幕或长时恒定 60 FPS 认证。

GUI/MCP 旧测试首跑在「启动后 12 秒内扣粮」超时；资源初始化需要先验证六套形象，这次到达 smoke 前约 19～22 秒。修正测试为独立 45 秒启动预算、启动预算加 30 秒的总上限，增加只读阶段诊断，未改喂食逻辑或放松 PID/数据目录隔离。相同发布 EXE 重跑 18.84 秒完成启动/喂食，之后全部通过。启动成本仍存在，不声称秒开；测试证据保留于本地 `.codex-build/gui-smoke-m90agx0e/`，首次超时样本也保留。

真实原生验证另用 `EagleNativeReminderSmoke.exe`：独立固定目录、独立 App 身份、独立存档。2026-09-22 14:06 UTC，`Show` 接受，3 秒后 Windows `History.GetHistory()` 返回对应 `tag=test`、`group=eagle-reminders`、`action=reminder-test`、本地鹰图标及静音配置的真实 XML；清理日志已记录专用身份卸载。未修改系统通知设置。UI 工具无法激活捕获窗口，两次失败后停止，因此不声称实际横幅和点击通过。

## Live2D 的真实边界

此节记录 2026-09-22 已打包预览版的边界，不是后续源码开发的冻结状态。2026-09-23 已继续标准 Live2D、生成首个鹰模型并使用官方 SDK 验证，见 [模型小样记录](LIVE2D-MODEL-SAMPLE.md)；该增量未重新打包进本页的 v1.12 生产 EXE，也未改变下述历史测试结果。

透明宿主探针通过透明/明暗背景合成与 5 项 WebGL 层次取样；采样 10 秒得到 599 次回调、约 59.95 Hz、P95 16.8 ms。数据见[原始报告](validation/live2d-host-p0-20260922.json)和 [P0 结论](LIVE2D-P0-RESULTS.md)。这是简单几何与 `requestAnimationFrame` 数据，**不是 Cubism 性能、物理屏幕 Present FPS 或 2 小时稳定性证明**。

角色包区分 ContractValid、FilesVerified、RuntimeReady。缺少受信任的实际 SDK 加载探针时不能报告 RuntimeReady，模板也不进入商店。两个不同比例、参数名与路径的合同夹具只能证明映射不依赖角色名称，不能证明两个真实模型可替换。

当前缺少可编辑的大头鹰模型工程与 Cubism 绑定。官方 SDK 负责加载，不提供把 PNG 自动变成 `.moc3` 的接口；已查的编辑器外部 API 不包含从零完成网格、变形器和绑定的完整制作流程。没有把他人模型冒充大头鹰，也未绕过 SDK/Core 的授权条款。标准 Cubism 模型制作与改用另一种参数化 2D 方案是不同路线，后者需用户明确选择，不能静默替换需求。

## 未测与升级注意

- 真实 OS 横幅可见性、正在运行/退出后实际点击、权限/勿扰/锁屏组合、睡眠恢复和跨路径通知身份升级
- 便签墙真实多显示器 DPI、辅助技术读屏，以及长时间使用；离屏 150%/200% 渲染不替代硬件实测
- Live2D 实际模型、遮罩、命中/拖拽、所有动作和六套服装、上下文丢失/睡眠恢复、低端设备和两小时性能
- 仍使用原逐帧角色，不降低其既有完整动作与装备兼容承诺来冒充迁移成功

升级前退出并备份本机数据。便签是独立 `notebook.json`；提醒升级为 v2，养成仍为 v3。回退旧版应恢复升级前备份。原生通知正文进入系统通知中心，锁屏隐私由 Windows 设置控制。完整使用边界见[原生提醒指南](NATIVE-REMINDERS.md)。

发布包 `dist/EagleDeskPet-v1.12.0-preview.1-win-x64.zip` 已逐项复核，5 个条目与发布目录 SHA256 全部一致：两个 EXE、README、第三方声明、SHA256SUMS；不包含测试数据、私人存档、注册信息或账号。ZIP SHA256：`64136c1bedbd1f80370e41dc31443d62d23e42ded4abfe25a01917d45496a126`。预览版未经代码签名，SHA256 是完整性校验而非安全背书。
