# 开发与测试

准备新增功能时，先查看[需求与路线图](REQUIREMENTS.md)及[参与指南](../CONTRIBUTING.md)，在明确范围和验收条件后再拆解具体任务。本页只负责构建与测试，不作为第二份需求进度表。

在仓库根目录执行下列命令。桌面程序、命名管道、DPAPI 和 WPF 测试需要 Windows；基础构建需要 .NET 8 SDK。不要在测试命令中传入真实用户存档、GitHub Token 或 AI 配置目录。

## 构建

```powershell
dotnet build .\DuckDeskPet\DuckDeskPet.csproj -c Release
dotnet build .\EagleDeskPet.Mcp\EagleDeskPet.Mcp.csproj -c Release
.\publish.ps1
```

发布脚本从主工程版本号读取目录，本版为 `dist/v1.9.0/`，不纳入 Git。目标必须新建或为空；脚本拒绝覆盖非空目录，重发可用 `-OutputDirectory .\dist\v1.9.0-recheck`。成功后自动复制 `THIRD-PARTY-NOTICES.md`，二进制分发应保留。首次构建会从 NuGet 还原锁定依赖。动画 PNG 已包含在仓库中，编译 EXE 不需要重新生成图片。普通 build 输出需要 .NET，只有指定自包含发布配置的包才可免安装运行时分发。

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
dotnet run --project .\tools\RpsSelfTest\RpsSelfTest.csproj -c Release
dotnet run --project .\tools\PlayIntegrationSelfTest\PlayIntegrationSelfTest.csproj -c Release
dotnet run --project .\tools\FeedingIntegrationSelfTest\FeedingIntegrationSelfTest.csproj -c Release -- "$((Get-Location).Path)"
dotnet run --project .\tools\ShopSelfTest\ShopSelfTest.csproj -c Release
dotnet run --project .\tools\TaskNotificationSelfTest\TaskNotificationSelfTest.csproj -c Release -- --mcp .\EagleDeskPet.Mcp\bin\Release\net8.0-windows\EagleDeskPet.Mcp.dll --dotnet dotnet
```

任务测试前先构建 MCP。`OutfitSelfTest --resources-root` 除隔离用例外会完整解码默认、办公服、卫衣三套各 2299 帧及站姿，但不代替场景遮挡和美术复查。`PlayIntegrationSelfTest` 链接生产 Play partial、真实游戏窗口、时间线和存档，只有宿主外壳 / 位图解码器是测试替身。`FeedingIntegrationSelfTest` 链接生产 Feeding partial、Core 和 PetStore，使用可控时钟 / 渲染外壳，验证队列准入、连续喂食、完整新序列、关闭和保存失败；实际主窗口的工作 / 暂停交接仍由下方 smoke 验证。

## 动画与 GUI 测试

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

启动器只创建新的 `%TEMP%\EagleExpansionSmoke-<随机编号>\data` 和 `evidence`、隔离通信通道与标记文件；不允许传入或复用用户存档。测试在隔离状态中添加资金，再通过实际宿主验证菜单、独立预览、购买去重、任务乱序、三摸抗议、饥饿后只吃一份、收工后应用待装备、真实猜拳两局和服装切换。保存 `expansion-smoke.json` 与自己 WPF 窗口的 PNG，不截桌面。

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
