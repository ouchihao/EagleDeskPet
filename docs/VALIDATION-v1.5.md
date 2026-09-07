# v1.5 验证记录

2026-09-05，本机 Windows x64，.NET 8。

## 通过

- GUI 和 MCP 自包含单文件发布成功，编译无警告、无错误。
- `DuckDeskPet.SelfTest`：26 项确定性测试通过（旧节奏、60 Hz 门控、完整端点、2 秒站立、队列、喂食、挂机结算、冷却、时钟回拨、存档模型）。
- `tools/PetStoreSelfTest`：7 项通过；真实文件原子保存和备份、坏 JSON/null/未知版本/超大文件保护；正常 AppData 的目标文件哈希未变化。
- `tools/test_care_assets.py`：4 项通过；GIF 来源哈希、脚点定位、RIFE 均匀分段采样及重复帧拒绝。
- `tools/test_mcp_bridge.py`：官方 SDK 实际握手、工具调用、命名管道送达、来源固定、去重、限频、输入大小限制、Hook 一次性调用、宠物离线错误。
- `tools/test_gui_smoke.py`：发布版 GUI 预解码完成，喂食粮食 5→4、饱食度至 100，观察到 Eat 实际播放；经过真实 MCP 连接向该 GUI 送达通知；存档读取验证 TotalMeals=1；正常退出。
- GUI 联调产物：`.codex-build/gui-smoke-t0caowvg/gui-smoke.json` 及同目录的 `care-panel.png`、`notification-bubble.png`、`pet-eating.png`。这些图片来自本应用自身 WPF 视觉树渲染，不是用户桌面截图。
- Eat：121 个 RGBA PNG / 2 秒；120 个不同像素画面（首尾相同），相邻重复 0；脚心误差最大 0.5 px、脚底垂直误差 0；黑/暗底白边 QA 通过。
- 旧四套动画 484 个 PNG 与 v1.4 发布 ZIP 逐项 SHA-256 一致；旧版 EXE 和 ZIP 保留。39 个 GIF 原件未改动，项目内留有哈希一致副本。

## 未宣称完成

- 未替用户配置任何实际 AI 客户端；没有验证特定客户端的结束 Hook。MCP 不自动监控全部 AI 回复。
- 返回入口打开本地配置的应用，不保证定位到精确聊天会话。
- 未做本次人工指针拖动/混合 DPI 多屏回归或长时间物理屏幕 60 FPS 性能认证。原生拖动路径保留；自动测试不能代替这些验证。
- 未完成趴桌哭的 enter/loop/exit 资源、更多动作等级解锁或完整角色替换系统。
- 旧动作本轮未重新生成，其中的段尾停帧情况见 `animation-quality-notes.md`；修复生成工具不代表旧 PNG 也已重建。

## 可复现命令

```powershell
dotnet run --project DuckDeskPet.SelfTest -c Release
dotnet run --project tools/PetStoreSelfTest -- .
python tools/test_care_assets.py
python tools/test_gui_smoke.py
```

GUI 联调要求先发布新版本。测试使用单独的 `EAGLE_PET_DATA_DIR`，核验自己启动的进程 ID 和存档目录后才发通知；不会终止已有桌宠、修改正常存档或修改 AI 配置。
