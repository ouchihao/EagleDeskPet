# 开发与测试

准备新增功能时，先查看[需求与路线图](REQUIREMENTS.md)及[参与指南](../CONTRIBUTING.md)，在明确范围和验收条件后再拆解具体任务。本页只负责构建与测试，不作为第二份需求进度表。

在仓库根目录执行下列命令。桌面程序、命名管道、DPAPI 和 WPF 测试需要 Windows；基础构建需要 .NET 8 SDK。不要在测试命令中传入真实用户存档、GitHub Token 或 AI 配置目录。

## 构建

```powershell
dotnet build .\DuckDeskPet\DuckDeskPet.csproj -c Release
dotnet build .\EagleDeskPet.Mcp\EagleDeskPet.Mcp.csproj -c Release
.\publish.ps1
```

发布脚本从主工程版本号读取目录，本预览版为 `dist/v1.12.0-preview.7/`，不纳入 Git。目标必须新建或为空；脚本拒绝覆盖非空目录，重发可用 `-OutputDirectory .\dist\v1.12.0-preview.7-recheck`。成功后自动复制 `THIRD-PARTY-NOTICES.md`，二进制分发应保留。首次构建会从 NuGet 还原锁定依赖。动画 PNG 已包含在仓库中，编译 EXE 不需要重新生成图片。普通 build 输出需要 .NET，只有指定自包含发布配置的包才可免安装运行时分发。下方旧版本命令保留历史测试语境，测试新版时应传入本次真实产物路径。

## preview.7 悬浮快捷栏

```powershell
dotnet run --project .\tools\QuickAccessSelfTest -c Release
.\tools\run_expansion_smoke.ps1 -Exe .\dist\v1.12.0-preview.7-quick-access\EagleDeskPet.exe -Mode quickaccess
```

独立策略测试验证停留、离开缓冲、显式关闭和阻塞；真实主程序 smoke 在新建隔离资料中检查五个窗口接线、复用、鼠标坐标转换、Popup 布局、缩放、动画、办公与暂停、遮挡和关闭。只渲染本程序的视觉树，不截桌面、不移动真实鼠标；不把程序化点击当成人工点击或混合 DPI 跨屏验收。可设置 `EAGLE_PET_RENDERER=png` 单独验证兼容后端。

<a id="preview3-摸鱼与光标注视"></a>
## preview.3–4 摸鱼与光标注视

需求与验收清单见 [REQ-012](REQUIREMENTS.md#req-012) / [REQ-013](REQUIREMENTS.md#req-013)。本节记录当前接口和验证边界，不宣告新版本已发布。

2026-09-30 本机 Demo 已完成：最终自包含 `dist/v1.12.0-preview.3-loafing/EagleDeskPet.exe` 的两种摸鱼后端与原业务联调通过，六套真实瞳孔 Stage 47 / 47。检查点、SHA256 和尚未验证的边界见 [本轮证据](VALIDATION-v1.12-preview.md#preview3)，不是远端 GitHub Release。

同日 preview.4 提升注视可见度并开放办公跟随：六套 Stage 166 / 166、真实 WPF 输入链路 60 / 60；最终 `dist/v1.12.0-preview.4-gaze/EagleDeskPet.exe` 原业务 7 / 7 与摸鱼联调通过。新增暂停消息连续性检查；完整证据与未测边界见 [preview.4 记录](VALIDATION-v1.12-preview.md#preview4)。

2026-10-03 preview.6 移除宿主距离限制，保留 preview.5 幅度与哈欠节奏。规则 51 / 51、实际发布构建的真实宿主 103 / 103、最终 EXE 原业务 7 / 7；远处负 X 与混合 DPI 跨屏的未测边界见 [preview.6 记录](VALIDATION-v1.12-preview.md#preview6)。

- `LoafingPolicy` 按有效工作区间和持久随机状态抽签；`PetCareService` 唯一结算金币、经验、食物与心情。休息固定半小时，无金币和任何自动经验，粮食仍积累；休息心情基础衰减为 1 / 小时。渲染模块不能补发奖励
- `LoafingVisualTimeline` / `LoafingSpriteLayer` 管理原味表情叠层；`Live2DStage/loafing.js` 管理独立模型 / 电脑转场。`SendLoafing(phase, seconds, paused, kind)` 以最多 20 Hz 同步，阶段 / 暂停边界立即发送，使用独立 generation / revision 过滤旧消息；正常工作时间轴冻结到交接结束
- `GazePolicy` 只使用本地鼠标坐标，宿主 `PointFromScreen` 转换、20 Hz 发送，`gaze.js` 在模型声明安全参数后平滑驱动。preview.4 开放站立与稳定办公 / 忙碌循环，其他动作禁用；遵循用户开关与系统减弱动画设置，无能力的旧包不伪造眼球运动
- 存档仍为 v4，新摸鱼字段有兼容默认值；没有新的经济迁移，已有 v4 钱包不能再次清零。旧版本升 v4 的独立备份与一次性重置沿用原保护，测试只写隔离资料
- 5,113.50 币 / 七天是关闭随机摸鱼的默认有效工作定标。`EconomySelfTest` 中该基线显式控制随机输入；不把它解释为新版实际挂机七天必得，价格本轮不再次调整

已执行的逻辑检查点：摸鱼 Core 提交 `518047d` 的自测 51 / 51、经济 64 / 64、存档 12 / 12；转场提交 `bbbf1df` 的 Stage 24、注视 11、摸鱼 10，共 45 / 45 JavaScript 测试。它们覆盖确定性调度、区间结算、存取档、协议、参数边界和暂停，不代替真实 Core 模型、WPF 叠层、输入或故障交接验收。

```powershell
dotnet run --project .\DuckDeskPet.SelfTest -c Release
dotnet run --project .\tools\EconomySelfTest -c Release -- "$((Get-Location).Path)"
dotnet run --project .\tools\PetStoreSelfTest -c Release -- "$((Get-Location).Path)"
node --test .\DuckDeskPet\Live2DStage\actions.test.cjs .\DuckDeskPet\Live2DStage\gaze.test.cjs .\DuckDeskPet\Live2DStage\loafing.test.mjs
dotnet run --project .\tools\LoafingSelfTest -c Release -- "$((Get-Location).Path)" .codex-build/loafing-review
.\tools\run_expansion_smoke.ps1 -Exe .\dist\v1.12.0-preview.3\EagleDeskPet.exe -Mode live2d-loafing
```

最后两项为新联调入口，必须保留本次实际结果再判断通过；不要复用证据目录。PNG 兼容后端另运行 `-Mode loafing` 并显式选择 `EAGLE_PET_RENDERER=png`。真实 Core 注视验证用 [GazeStageSelfTest](../tools/GazeStageSelfTest/README.md)，只对本地模型 / 窗口画布操作，不注入系统鼠标、不读真实用户存档。preview.4 新增 [GazeHostSelfTest](../tools/GazeHostSelfTest/README.md)：移动自己的隔离透明窗口，实际经过 `GetCursorPos → PointFromScreen → TickGaze → WebView`，记录三种宠物尺寸及办公等状态，避免仅注入 JS 参数就认为真实宿主有效。

已知素材边界：原抠鼻 / 袈裟 GIF 截为各 35 帧，40 ms / 帧（25 FPS），往返播放避免首末硬切；最高 60 Hz 仅指位置、转场及渲染调度，不声称有 60 张不同表情帧。摸鱼暂不换服装；真实手握木鱼、桌面遮挡、原图抠边、掉帧后双后端同步、取消 / 重启、跨 DPI 和长期性能均需另看画面和宿主报告。原有 14 段核心动作与六套衣装包不因新增叠层而重编号。

## v1.12 便签、原生提醒和角色包测试

```powershell
dotnet run --project .\tools\NotebookSelfTest -c Release -- .codex-build/notebook-audit
dotnet run --project .\tools\NativeReminderSelfTest -c Release
dotnet run --project .\tools\ReminderSelfTest -c Release
dotnet run --project .\tools\ReminderIntegrationSelfTest -c Release -- .codex-build/reminder-ui
dotnet run --project .\tools\CharacterPackSelfTest -c Release
.\tools\run_expansion_smoke.ps1 -Exe .\dist\v1.12.0-preview.3\EagleDeskPet.exe
```

这些测试创建隔离数据；主 EXE 联调的系统通知通道使用替身，不注册生产身份。真实 Windows 原生 API 的专用独立应用测试见 [NativeReminderSelfTest](../tools/NativeReminderSelfTest/README.md)，需显式操作并清理专用测试身份，不得改系统勿扰或生产资料来强求通过。角色包校验不等于通过真实 Cubism 模型加载；早期透明 WebGL 探针见 [Live2D P0](LIVE2D-P0-RESULTS.md)，当前真实主程序、六套模型和混合动画的入口与证据见 [动画验证](LIVE2D-FULL-ACTIONS.md)。便签 / 提醒的证据与未测边界见[预览版验证记录](VALIDATION-v1.12-preview.md)。

## 逻辑与文件测试

```powershell
dotnet run --project .\DuckDeskPet.SelfTest\DuckDeskPet.SelfTest.csproj -c Release
dotnet run --project .\tools\BanterSelfTest\BanterSelfTest.csproj -c Release
dotnet run --project .\tools\WorkStageSelfTest\WorkStageSelfTest.csproj -c Release
dotnet run --project .\tools\HonorSelfTest\HonorSelfTest.csproj -c Release
dotnet run --project .\tools\HonorUiSelfTest\HonorUiSelfTest.csproj -c Release
dotnet run --project .\tools\GitHubSelfTest\GitHubSelfTest.csproj -c Release
dotnet run --project .\tools\PetStoreSelfTest\PetStoreSelfTest.csproj -c Release -- "$((Get-Location).Path)"
dotnet run --project .\tools\ClientSetupSelfTest\ClientSetupSelfTest.csproj -c Release
```

测试创建自己的临时数据，GitHub 使用模拟响应，客户端安装器测试不写真实 AI 配置。最后一项不传额外参数时测试配置合并与安全边界；发布后可传 `--mcp` 指定桥接 EXE、`--bash` 指定 Git Bash，额外验证真实命令 Hook 包装层。

v1.9 新增的确定性规则与独立 WPF 接线测试：

```powershell
dotnet run --project .\tools\EconomySelfTest\EconomySelfTest.csproj -c Release -- "$((Get-Location).Path)"
dotnet run --project .\tools\ContentSelfTest\ContentSelfTest.csproj -c Release -- "$((Get-Location).Path)"
dotnet run --project .\tools\EmotionSelfTest\EmotionSelfTest.csproj -c Release
dotnet run --project .\tools\SceneCatalogSelfTest\SceneCatalogSelfTest.csproj -c Release
dotnet run --project .\tools\OutfitSelfTest\OutfitSelfTest.csproj -c Release -- --resources-root .\DuckDeskPet
dotnet run --project .\tools\FeedingIntegrationSelfTest\FeedingIntegrationSelfTest.csproj -c Release -- "$((Get-Location).Path)"
dotnet run --project .\tools\ShopSelfTest\ShopSelfTest.csproj -c Release
dotnet run --project .\tools\CareUiSelfTest\CareUiSelfTest.csproj -c Release -- .codex-build/care-v4-ui
dotnet run --project .\tools\CompositionControlSelfTest\CompositionControlSelfTest.csproj -c Release
dotnet run --project .\tools\TaskNotificationSelfTest\TaskNotificationSelfTest.csproj -c Release -- --mcp .\EagleDeskPet.Mcp\bin\Release\net8.0-windows\EagleDeskPet.Mcp.dll --dotnet dotnet
```

任务测试前先构建 MCP。当前 `OutfitSelfTest --resources-root` 检查全部六套形象、14 个动作的 1694 帧及站姿；分层服装另严格验证 1695 个绑定映射及衣片，但不代替场景遮挡和美术复查。猜拳窗口、状态机与专属测试已按用户决定移除；通用动作、喂饭和场景回归继续保留。`FeedingIntegrationSelfTest` 链接生产 Feeding partial、Core 和 PetStore，使用可控时钟 / 渲染外壳，验证队列准入、连续喂食、完整新序列、关闭和保存失败；实际主窗口的工作 / 暂停交接仍由下方 smoke 验证。

`CareUiSelfTest` 使用真实 CarePanel XAML、核心工资计算和仅供测试的宿主适配器，检查心情效率、零心情开工、迁移提示及两种窗口宽度；它不操作真实鼠标或用户存档。preview.2 的经济、迁移和界面回归结果见[当前经济规则](ECONOMY-v1.12.md#验证边界)。

`CompositionControlSelfTest` 创建隔离 WPF 窗口与 WebView 用户目录，检查真实浏览器加载、局部 COM 图形初始化故障、模板重应用和取消；不修改系统显卡配置，也不全局吞掉 Dispatcher 异常。这是控件边界测试，不代替真实 Live2D 业务回归。

## 动画与 GUI 测试

本机混合动画支持 `Assets/HybridActions/manifest.json` 的逐套能力路由，不把“缺少专用原帧”当成整个模型故障。本次没有晋级服装候选包：原味直接用已有两段帧，另外五套留在 Core。制作工具与全帧测试见 [混合动作制作](../tools/Live2DArt/HYBRID-ACTIONS.md)；运行 `node --test tools/Live2DArt/pack-hybrid-actions.test.mjs`，通过 `EAGLE_HYBRID_CANDIDATE` 指定实际完整候选后才会执行第 14 项全资源检查。没有候选时跳过项不应计为通过。

Python 工具使用 Pillow、NumPy；动画生产可选使用 SciPy 加速。可在独立环境安装测试依赖：

```powershell
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install pillow numpy
.\.venv\Scripts\python.exe .\tools\test_care_assets.py
.\.venv\Scripts\python.exe .\tools\test_work_scene_v2.py
```

原始 GIF 参考未随 Git 分发，相关哈希检查在原图缺失时跳过；其余测试检查仓内已有生成资源。不要为通过该项检查而随意下载同名表情包。

发布成功后，在有交互式桌面的 Windows 会话里运行：

```powershell
python .\tools\test_gui_smoke.py --gui .\dist\v1.9.0\EagleDeskPet.exe --mcp .\dist\v1.9.0\EagleDeskPet.Mcp.exe
python .\tools\test_work_gui_smoke.py --gui .\dist\v1.9.0\EagleDeskPet.exe --mcp .\dist\v1.9.0\EagleDeskPet.Mcp.exe
python .\tools\test_features_gui_smoke.py --gui .\dist\v1.9.0\EagleDeskPet.exe
```

如果使用虚拟环境，将上面的 `python` 替换为 `.\.venv\Scripts\python.exe`。这些测试使用隔离实例、临时存档和随机命名管道，不会关闭正在使用的桌宠；菜单、荣誉、GitHub 和客户端配置窗口会在测试中短暂出现，输出保存在 `.codex-build/`。

MCP 实际协议测试的宿主构建及运行方式见 [MCP 文档](MCP.md#开发与验证)。请勿把自动化断言当作长期物理屏幕帧率或所有 AI 客户端版本的验收。

### v1.9 真实宿主 smoke

```powershell
.\tools\run_expansion_smoke.ps1 -Exe .\dist\v1.9.0\EagleDeskPet.exe
```

普通 Release build 也可测试，但没有系统 .NET 时需附 `-DotnetRoot <包含 dotnet.exe 的 SDK / 运行时目录>`。只有阶段性资源尚未齐全时才加 `-SkipOutfit`，报告会明确 skipped、`fullAcceptance=false`，不得当作完整验收通过。

启动器只创建新的 `%TEMP%\EagleExpansionSmoke-<随机编号>\data` 和 `evidence`、隔离通信通道与标记文件；不允许传入或复用用户存档。测试在隔离状态中添加资金，再通过实际宿主验证菜单、独立预览、购买去重、任务乱序、三摸抗议、手动空碗场景后只吃一份、收工后应用待装备、服装切换及猜拳入口 / 资源不再存在。preview.2 另验证低心情持续工作、心情工资和饱食度字段移除。保存 `expansion-smoke.json` 与自己 WPF 窗口的 PNG，不截桌面。

内部 280 秒取消预算、正常收尾；启动器 295 秒请求关闭、300 秒硬限只结束本次子进程。失败会写报告，不伪装通过；截图、临时数据和未人工确认的边界保留给复查。工作时间自然增长或合法工资变化不能误判为预览 / 装备偷改状态；人工购买确认框仍不由 smoke 自动应答。

场景局部 WPF 验证另用新的证据目录：

```powershell
dotnet run --project .\tools\ExpansionIntegrationSelfTest\ExpansionIntegrationSelfTest.csproj -c Release -- .\.codex-build\expansion-wpf-review
dotnet run --project .\tools\WorkSceneV2SelfTest\WorkSceneV2SelfTest.csproj -c Release -- .\.codex-build\work-v2-review
dotnet run --project .\tools\OutfitSceneSelfTest\OutfitSceneSelfTest.csproj -c Release -- "$((Get-Location).Path)" .\.codex-build\outfit-scene-review
dotnet run --project .\tools\OutfitSceneSelfTest\OutfitSceneSelfTest.csproj -c Release -- "$((Get-Location).Path)" .\.codex-build\desk-motion-review --motion
```

重复运行请改用新的目录，不覆盖想保留的旧证据。具体本轮通过数量、资源 QA 和未测项目见 [v1.9 验证记录](VALIDATION-v1.9.md)。

## 修改动画

- 动作 ID、帧数、时长和目录映射在 `DuckDeskPet/Assets/actions.json`。
- `Assets/AnimationSources` 保留生成关键姿势和提示词，`AnimationKeys` 保留提取后的关键帧，`AnimationPreviews` 保留开发预览与历史 QA。
- `Assets/Animations` 是逐帧角色 PNG；`Assets/SceneProps` 是分层道具；发布工程只嵌入当前使用的资源。
- `Assets/outfits.json` 注册整套形象，`Assets/Outfits/Office/actions.json` 和 `Assets/Outfits/Hoodie/actions.json` 映射完整衣服动作；新增动作须同时满足已注册服装的兼容检查，不能单动作缺图时悄悄脱装。
- 重新插帧需要另行准备 RIFE NCNN Vulkan 程序与模型，按各个 `tools/prepare_*.py --help` 指定输入路径；不包含在源码仓库或正常运行依赖中。
- 不用重复帧凑数量、整图翻转或跨姿势淡化代替肢体运动。新动作应检查脚点、透明边缘、首尾衔接与时间轴。

制作过程见 [v1.9 素材规模与质量](ASSETS-v1.9.md)、[工作动画](WORK-ANIMATION-ASSETS.md) 与 [历史质量记录](animation-quality-notes.md)。历史验证文档中的 `.codex-build/` 路径表示当时的本机证据，测试产物本身不随 Git 分发；README 的精选预览另保存在 `docs/images/`。

## 提交前

```powershell
git status --short
git diff --check
```

只提交源码、必要资源和文档，不提交 `bin/`、`obj/`、`dist/`、`.codex-build/`、`.tool-cache/`、原始 GIF、个人存档、Token 或配置备份。不要把被 TSD 保护的密文字节当作源码提交；本项目需要以可读文本和标准图片格式进入 Git。
