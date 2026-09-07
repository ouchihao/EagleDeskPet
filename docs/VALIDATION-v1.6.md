# v1.6 验证记录

2026-09-05，Windows x64，.NET 8。验证对象为 `dist/v1.6.0` 的自包含发布版。

## 逻辑与文件回归

82 项确定性检查通过：

- `DuckDeskPet.SelfTest`：38 项，覆盖 60 Hz 门控、完整端点、站立 2 秒、待机只选哈欠、互动队列、工作循环、30 分钟忙碌、取消/饿空、离线结算、经验及存档模型。
- `tools/BanterSelfTest`：9 项，覆盖 60 条唯一短句、每分钟一条、打乱袋不连续重复、开关与阻塞/恢复不补播。
- `tools/PetStoreSelfTest`：7 项，覆盖真实存档、备份、坏文件保护；正常 AppData 目标文件前后未变。
- `tools/WorkStageSelfTest`：10 项，直接测试生产 `WorkStageMotion.cs`；道具入退场、桌子先到/电脑落下、静止工位、火焰生长收回及端点连续性。
- `tools/test_care_assets.py`：4 项，覆盖原始素材哈希、定位、均匀插帧采样及重复帧拒绝。
- 可选 SciPy 离线加速：14 项与原四邻域算法逐像素等价检查。它不是发布版依赖。

## 动画资产

- 新增工作角色六段：211 + 121 + 91 + 121 + 121 + 121 = 786 张 RGBA PNG。
- 独立忙碌火焰循环：121 张 RGBA PNG；加上角色共 907 张新连续帧。
- 全部画布 384×346，60 Hz 含端点采样；原站立画面不变。
- 六段角色之间及站立之间的 10 个连接点像素完全相同；各片段相邻重复帧为 0。闭环片段首尾相同是有意设计。
- 角色脚点最大水平误差 0.5 px、垂直误差 0 px；深底边缘规则检查通过。自动边缘检查是质量辅助，不保证每个人对画风和轮廓的主观感受一致。
- 桌子前后层和电脑独立，不经角色光流插帧；电脑边界 `[141,210,243,275]`，给嘴巴与手部留出空间。
- 原有 Yawn/Shy/Eat PNG 未重建，旧动作的质量边界继续见 `animation-quality-notes.md`。Bomb/SideEye 原资源保留，但退出本版动作配置和 EXE 资源嵌入。
- GIF、QA 和源姿势图保存在 `DuckDeskPet/Assets/AnimationPreviews` / `AnimationSources`。新连续帧由 AI 关键姿势和 RIFE 光流生成，不是 907 张逐张独立 AI 绘画。

## 实际 GUI + MCP 联调

发布成功，GUI/MCP 无编译错误。测试只启动自己的子进程，使用独立单实例锁/命名管道、独立数据目录；发送测试通知前还核对进程 ID 与数据路径。未修改任何实际 AI 客户端配置、未关闭已有宠物。

工作联调证据：`.codex-build/work-gui-smoke-pe9fzd48/gui-smoke.json`。

- 实际依次观察到 Idle、WorkEnter、WorkLoop、WorkToBusy、BusyLoop、BusyExit、WorkExit。
- 办公与忙碌分别连续运行超过两个完整循环，没有中途站立。
- 在实际办公期间通过发布版 MCP 送达通知，工作状态和动作继续。
- 正常取消走忙碌退场；另一次工作将独立测试饱食度置近 0 后，由正常养成时钟触发工作退场，最终饱食度为 0、工作状态为 false。
- 收工后全部道具隐藏，预解码工作帧释放，保留 363 张待机/互动帧。
- 工作阶段 WPF 布局头部锚点漂移为 0 px；另有上述逐帧脚点检查，二者测量对象不同。
- 碎碎念关闭后安静、打开后触发小气泡，开关重新读取一致。
- 保存并重读工作状态，正常退出。

普通联调证据：`.codex-build/gui-smoke-e77129v6/gui-smoke.json`。

- 实际喂食、Eat 播放、MCP 通知、存档重读通过；粮食 5→4、进餐次数为 1。
- 图像证据来自本程序自己的 WPF 视觉树，不是用户桌面截图。

`tools/test_mcp_bridge.py` 的协议回归也通过：官方 SDK 握手、来源固定、去重、限频、字段/Unicode/大小限制、真实管道投递、一次性 Hook 模式和离线错误。测试脚本自身使用随机独立管道。

## 验证边界

- GUI 测试将独立测试存档的工作时长置于 30 分钟门槛附近，再让真实计时触发忙碌；没有实际等待半小时。工作结算的精确时长另有确定性测试。
- 未进行本轮人工指针拖动、混合 DPI 多屏检查或长期物理屏幕 60 FPS 性能认证。程序按最高 60 Hz 播放，不能把素材 60 Hz 或逻辑检查等同于所有机器稳定 60 FPS。
- 工作原画以既有角色为参考；普通办公表情比原 GIF 更圆眼，不宣称完全复刻原 GIF 每一像素。
- MCP 不自动监听所有 AI 回复，返回应用不保证跳到精确会话。
- 不包含趴桌哭、更多等级动作解锁或完整换肤系统。
- v1.4 主程序、v1.5 GUI/MCP 和 v1.5 ZIP 的 SHA-256 与本轮之前一致，旧版本可回退；本轮未提交或推送 Git。

## 复现

先用 .NET 8 SDK 发布，再运行独立 GUI 测试：

```powershell
.\publish.ps1
python tools/test_work_gui_smoke.py
python tools/test_gui_smoke.py --gui dist/v1.6.0/EagleDeskPet.exe --mcp dist/v1.6.0/EagleDeskPet.Mcp.exe
dotnet run --project DuckDeskPet.SelfTest
dotnet run --project tools/BanterSelfTest
dotnet run --project tools/PetStoreSelfTest -- .
dotnet run --project tools/WorkStageSelfTest
python tools/test_care_assets.py
```
