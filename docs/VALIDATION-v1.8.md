# 大头鹰 v1.8 验证与交付记录

日期：2026-09-06。项目：EagleDeskPet；下文路径均相对仓库根目录。

## 最终发布

最终 EXE 位于 `dist/v1.8.0`，两个程序都是 Windows x64 自包含单文件，无需接收者安装 .NET。主程序 1.8.0.0，通知桥 1.8.0。

| 文件 | 字节数 | SHA-256 |
| --- | ---: | --- |
| EagleDeskPet.exe | 172507883 | `1DD973D0D48CA9E8BCEBBEE25359C58FDC1ADEE26840AF1467A18AA1D871D390` |
| EagleDeskPet.Mcp.exe | 35304222 | `432BAF8980AC8430673FE6D454F721EB5A36CD772A4844570D056A6FC759EAA7` |

构建时已对 WPF 资源执行 Rebuild，最终发布包含全部九枚新徽章、新闭眼桥段和修正轴心的实心火焰。旧版本图片仍在源码中，但不再重复嵌入无用的旧桌子、火焰、桥段和徽章。旧发布包完整保留。

## 已通过的检查

| 检查 | 结果 | 主要覆盖 |
| --- | --- | --- |
| 养成、行为、节奏 Core | 38/38 | 原待机、互动、工作和 60 Hz 调度 |
| 本地碎碎念 | 9/9 | 60 条、节奏、开关、跳过积压 |
| 真实隔离存档 | 7/7 | 原子写入、备份、异常存档保护、正常 AppData 未变 |
| 原工作道具运动 | 10/10 | 进退场、忙碌和火焰阶段 |
| 原素材回归 | 4/4 | 干饭脚点、RIFE 采样、拒绝复制补帧、39 个原始 GIF 未变 |
| GitHub | 46/46 组 | 原通知、隐私、认证恢复、去重、限流与断开行为 |
| 荣誉逻辑 | 12/12 | 九个门槛、旧记录兼容、不变更存档 |
| 荣誉实际 WPF | 25/25 | 九张独立图、圆形裁切、响应式、筛选、进度、动画启停 |
| 新工作素材 | 8/8 | 闭眼屏障、实心火焰、脚点、真实帧和接口端点 |
| 新工作实际 WPF | 4/4 | 固定场景与火焰基线、退场清理、帧内存释放 |
| 一键配置与 Hook | 53/53 | 三客户端安装/撤销/幂等、安全合并、备份回滚及实际 shell 到 IPC |
| 最终 EXE 新功能 GUI | 26/26 | 菜单、九徽章、一键配置窗口的真实点击、保存时阻止退出、模拟 GitHub |
| 最终 EXE 日常 GUI + MCP | 通过 | 喂食 5→4、干饭、存档重读、真实通知桥和正常退出 |
| 最终 EXE 工作 GUI + MCP | 通过 | 入场、持续办公、转忙、两种收工、道具清理、通知不打断、锚点无漂移 |
| MCP 协议回归 | 通过 | SDK 握手、列工具、通知/状态、来源绑定、去重限流、Hook 一次性模式和离线错误 |

53 项客户端测试中的三项，真实执行安装器生成的 Codex PowerShell `-EncodedCommand` 及 Claude/CodeBuddy Git Bash 命令，将模拟 stdin 送入发布的 MCP EXE，再验证隔离命名管道收到固定来源的通知及中性 `{}` 输出。不是只验证 JSON 看起来正确；也没有配置或运行真实 AI 会话。

新工作素材指标：WorkToBusyV2 91 张均独立，BusyExitV2 121 张均独立，火焰 121 张中 120 张独立且首尾完全一致。火芯 alpha 最小值 255，无内部洞；242 张办公/忙碌循环均无露出的断开脚像素。新两段纵向脚点误差 0，横向最多 0.5 源像素，完整段接口保持旧锚点。

## 证据位置

以下路径相对于项目根目录，开发证据不包含在运行包中：

- `.codex-build/features-gui-smoke-uj0bez02/gui-smoke.json`：最终新功能 26 项及菜单、荣誉墙、接入预览/结果、GitHub 窗口和气泡截图。
- `.codex-build/gui-smoke-h1ucpund/gui-smoke.json`：最终日常 GUI / MCP。
- `.codex-build/work-gui-smoke-uewu3gmw/gui-smoke.json`：最终工作 GUI / MCP。
- `.codex-build/client-setup-tests/20260906-132142-d38382ec508a4ee6b714e3c42e2e92ce/results.json`：使用发布 MCP EXE 的 53 项接入测试。
- `.codex-build/github-selftest/c6b9625f659f44e6abb305b5b68de90d`：GitHub 合成输入测试夹具，不含真实凭据。
- `.codex-build/pet-store-test/20260906-132403-5c7302ceb2db4acaa430093279030137/results.json`：隔离存档。
- `.codex-build/honor-ui-v1.8-smoke/honor-ui-report.json`：25 项徽章窗口断言，另有完整荣誉墙、窄窗口截图。
- `.codex-build/work-scene-v2/asset-report.json`、`runtime-report.json`：8 项素材与 4 项实际渲染断言。
- `.codex-build/work-scene-v2/WorkToBusyV2-runtime-contact.png`、`BusyExitV2-runtime-contact.png`：实际阶段节点。
- `.codex-build/work-scene-v2/Work-scene-v2-preview.gif`：220 帧、每帧延时 50ms、总长 11 秒的 20 FPS 诊断预览。不是把桌宠生产播放降为 20 FPS。

徽章九图与完整提示词在 `docs/BADGE-ASSETS-v2.md`；工作图四次生成、输入图和分段采样说明在 `DuckDeskPet/Assets/AnimationSources/work-scene-v2-prompts.md`。均使用内置 ImageGen；关键帧后续使用现有 RIFE、抠图与定位流水线。工具输出的 RGB 不能冒称透明：徽章通过 WPF 圆形裁切，工作火焰按既有色域抠图流程处理，实心内芯由 ImageGen 绘制。

## 安全与验证边界

- 未修改真实 `.codex`、`.claude`、`.codebuddy` 配置、信任记录或 API Key。测试注入新建客户端目录，使用随机独立管道与存档，只处理自己启动的测试进程，不关闭用户宠物。
- 一键接入必须由用户在窗口点击；只更新自有条目，保留其他设置。配置备份可能包含用户原有密钥，不能分享或收入发布包。符号链接、TSD/非法编码、损坏或冲突配置会停止。
- Codex `CODEX_HOME` 支持绝对目录；Claude/CodeBuddy 自定义根映射本版不猜测，检测到对应变量便拒绝默认路径安装。
- 普通关闭在写入中被阻止；系统强制结束、断电和跨进程同时修改不等同于完整数据库事务保证。回滚无法安全完成时明确提示，不覆盖并发外部修改。
- 最终 ZIP 仅放程序、上手/连接/验证及新增依赖声明，不包含存档、收件箱、Token、客户端配置、配置备份或测试目录。
- 没有真实 AI 客户端的审核/会话终止事件验收，没有真实 GitHub 账号送达验收；后续需用户在本机确认。
- Claude Code / CodeBuddy Code 是本机客户端范围，不是网页、普通桌面聊天、所有 IDE 版本、WSL 或远程；Windows Hook 依赖所述 shell。MCP 接入不配置或调用模型。
- 未完成人工多屏拖动、混合 DPI、物理显示器长期 60 FPS。GIF 只是诊断预览；应用保持 60 Hz 素材采样与原有渲染节奏。
- 39 张原始 GIF 与旧发布包保留。父仓库仍将 `desktop-pet/` 视为未跟踪目录，本轮未暂存、提交或推送；`git diff --check` 不代替未跟踪源码检查。

## 复现

需要 .NET 8 SDK。`tools/ClientSetupSelfTest` 的 `--mcp` 请指定本次发布 EXE，`--bash` 请替换为实际 Git Bash 路径；所有测试仍只写测试目录。

```powershell
dotnet run --project tools/ClientSetupSelfTest -c Debug -- --mcp D:\Apps\EagleDeskPet-v1.8.0\EagleDeskPet.Mcp.exe --bash 'C:\Program Files\Git\bin\bash.exe'
python tools/test_features_gui_smoke.py
python tools/test_work_gui_smoke.py
python tools/test_work_scene_v2.py
```

更多手动接入说明见 `AI-AUTO-SETUP.md`、`MCP.md`。
