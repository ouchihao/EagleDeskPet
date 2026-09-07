# 开发与测试

在仓库根目录执行下列命令。桌面程序、命名管道、DPAPI 和 WPF 测试需要 Windows；基础构建需要 .NET 8 SDK。不要在测试命令中传入真实用户存档、GitHub Token 或 AI 配置目录。

## 构建

```powershell
dotnet build .\DuckDeskPet\DuckDeskPet.csproj -c Release
dotnet build .\EagleDeskPet.Mcp\EagleDeskPet.Mcp.csproj -c Release
.\publish.ps1
```

发布输出在 `dist/v1.8.0/`，不纳入 Git。首次构建会从 NuGet 还原锁定的依赖。动画 PNG 已包含在仓库中，编译 EXE 不需要重新生成图片。

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
python .\tools\test_gui_smoke.py --gui .\dist\v1.8.0\EagleDeskPet.exe --mcp .\dist\v1.8.0\EagleDeskPet.Mcp.exe
python .\tools\test_work_gui_smoke.py --gui .\dist\v1.8.0\EagleDeskPet.exe --mcp .\dist\v1.8.0\EagleDeskPet.Mcp.exe
python .\tools\test_features_gui_smoke.py
```

如果使用虚拟环境，将上面的 `python` 替换为 `.\.venv\Scripts\python.exe`。这些测试使用隔离实例、临时存档和随机命名管道，不会关闭正在使用的桌宠；菜单、荣誉、GitHub 和客户端配置窗口会在测试中短暂出现，输出保存在 `.codex-build/`。

MCP 实际协议测试的宿主构建及运行方式见 [MCP 文档](MCP.md#开发与验证)。请勿把自动化断言当作长期物理屏幕帧率或所有 AI 客户端版本的验收。

## 修改动画

- 动作 ID、帧数、时长和目录映射在 `DuckDeskPet/Assets/actions.json`。
- `Assets/AnimationSources` 保留生成关键姿势和提示词，`AnimationKeys` 保留提取后的关键帧，`AnimationPreviews` 保留开发预览与历史 QA。
- `Assets/Animations` 是逐帧角色 PNG；`Assets/SceneProps` 是分层道具；发布工程只嵌入当前使用的资源。
- 重新插帧需要另行准备 RIFE NCNN Vulkan 程序与模型，按各个 `tools/prepare_*.py --help` 指定输入路径；不包含在源码仓库或正常运行依赖中。
- 不用重复帧凑数量、整图翻转或跨姿势淡化代替肢体运动。新动作应检查脚点、透明边缘、首尾衔接与时间轴。

制作过程见 [工作动画](WORK-ANIMATION-ASSETS.md) 与 [质量记录](animation-quality-notes.md)。历史验证文档中的 `.codex-build/` 路径表示当时的本机证据，测试产物本身不随 Git 分发；README 的精选预览另保存在 `docs/images/`。

## 提交前

```powershell
git status --short
git diff --check
```

只提交源码、必要资源和文档，不提交 `bin/`、`obj/`、`dist/`、`.codex-build/`、`.tool-cache/`、原始 GIF、个人存档、Token 或配置备份。不要把被 TSD 保护的密文字节当作源码提交；本项目需要以可读文本和标准图片格式进入 Git。
