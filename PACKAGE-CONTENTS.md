# 源码与发布包内容

当前版本：v1.8.0。项目首页和构建步骤见 [README](README.md)。

## Git 仓库

仓库提供 WPF 主程序、养成核心、MCP 桥、测试、动画制作脚本，以及构建需要的生成资源。

- `DuckDeskPet/`：界面、互动、通知功能和动画资源。
- `DuckDeskPet.Core/`：养成与动作调度。
- `EagleDeskPet.Mcp/`：本地 MCP 与 Hook 通知。
- `DuckDeskPet.SelfTest/`、`tools/`：逻辑、资源与集成测试，以及制作工具。
- `docs/`：使用、制作和验证说明；`docs/images/` 为首页预览。
- `References/`：第三方原始素材说明。原始 GIF 和其拼图只保留在本机，不随 Git 分发。

`bin/`、`obj/`、`dist/`、`.codex-build/`、`.tool-cache/`、个人配置、凭据和备份均排除。历史动画与 QA 保留供制作参考，不代表这些动作仍在当前待机池中。

## 自行构建的程序

在仓库根目录执行 `.\publish.ps1`，得到：

```text
dist/v1.8.0/
  EagleDeskPet.exe       桌宠主程序
  EagleDeskPet.Mcp.exe   可选 AI 通知桥
```

两个程序均为 Windows x64 自包含 EXE，接收者不需要安装 .NET。主程序可单独使用；一键 AI 接入要求桥接器放在同一目录。发布脚本不自动制作 ZIP 或上传 GitHub Release。

向同事分发时可附上 `docs/QUICK-START.md`、`docs/MCP.md`、`docs/AI-AUTO-SETUP.md`、`docs/GITHUB-NOTIFICATIONS.md` 与第三方许可说明。不要复制 `%LOCALAPPDATA%\EagleDeskPet` 或 AI 配置备份，每个人使用独立存档与账号授权。
