# 源码与发布包内容

当前源码目标：v1.9.0 Demo，整体验收状态见 [v1.9 验证记录](docs/VALIDATION-v1.9.md)。项目首页和构建步骤见 [README](README.md)。

## Git 仓库

仓库提供 WPF 主程序、养成核心、MCP 桥、测试、动画制作脚本，以及构建需要的生成资源。

- `DuckDeskPet/`：界面、互动、通知功能和动画资源。
- `DuckDeskPet.Core/`：养成、工资、拥有权、情绪、猜拳、场景与任务通知规则。
- `EagleDeskPet.Mcp/`：本地 MCP 与 Hook 通知。
- `DuckDeskPet.SelfTest/`、`tools/`：逻辑、资源与集成测试，以及制作工具。
- `docs/`：使用、制作和验证说明；`docs/images/` 为首页预览。
- `References/`：第三方原始素材说明。原始 GIF 和其拼图只保留在本机，不随 Git 分发。

`bin/`、`obj/`、`dist/`、`.codex-build/`、`.tool-cache/`、个人配置、凭据和备份均排除。历史动画与 QA 保留供制作参考，不代表这些动作仍在当前待机池中。

v1.9 增加默认形象 10 段新动画（1150 帧），办公服和摸鱼卫衣各有 19 段（2299 帧）和站姿；当前三套形象共 6900 张运行 PNG，另有独立桌子 / 电脑 / 特效图层。源关键图、关键帧和 QA 用于制作复核；运行程序嵌入受清单控制的逐帧资源，不需要原 GIF、RIFE 或图像生成账号。详细口径见 [资源说明](docs/ASSETS-v1.9.md)。

## 自行构建的程序

在仓库根目录执行 `.\publish.ps1`，得到：

```text
dist/v1.9.0/
  EagleDeskPet.exe       桌宠主程序
  EagleDeskPet.Mcp.exe   可选 AI 通知桥
  THIRD-PARTY-NOTICES.md 第三方依赖声明，分发时保留
```

两个程序均为 Windows x64 自包含 EXE，接收者不需要安装 .NET。主程序可单独使用；一键 AI 接入要求桥接器放在同一目录。脚本在两个程序发布成功后复制第三方依赖声明，分发时请保留；不自动制作 ZIP 或上传 GitHub Release，也不生成本项目的统一 LICENSE。

默认目录从主工程版本号读取。脚本拒绝非空目标目录，不清理或覆盖旧程序包；重发时指定新的 `-OutputDirectory`。开发用的普通 `dotnet build` 产物不是自包含分发包，不能据此宣称接收者无需运行时。

向同事分发时须保留自动附带的 `THIRD-PARTY-NOTICES.md`，还可附上 `docs/RELEASE-NOTES-v1.9.md`、`docs/QUICK-START.md`、`docs/MCP.md`、`docs/AI-AUTO-SETUP.md` 和 `docs/GITHUB-NOTIFICATIONS.md`。不要复制 `%LOCALAPPDATA%\EagleDeskPet` 或 AI 配置备份，每个人使用独立存档与账号授权。

## 本轮便携发布包

本轮打包为 `dist/EagleDeskPet-v1.9.0-win-x64.zip`（567193245 字节，约 540.92 MiB），使用新目录保存已验证产物，不覆盖早先的发布尝试。ZIP 内仅包含：

```text
v1.9.0-verified/
  EagleDeskPet.exe
  EagleDeskPet.Mcp.exe
  README.md
  THIRD-PARTY-NOTICES.md
  SHA256SUMS.txt
```

其中简明 README 和校验清单由本次打包步骤补入；自动发布脚本仅生成两个 EXE 并附带第三方声明。实际自包含 EXE 已通过 9/9 联调，包与程序的 SHA256 见 [最终发布验证](docs/VALIDATION-v1.9.md#最终发布产物)。本地生成 ZIP 不等于已经创建 GitHub Release 或上传附件。
