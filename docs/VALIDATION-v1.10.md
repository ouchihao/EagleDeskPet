# v1.10.0 验证记录

日期：2026-09-20。范围：猜拳节奏、商品货架、荣誉扩充、俱乐部 UI 与短过渡。以下只列本轮实际执行的检查；v1.9 全部帧 / 48 组合的旧结果仍在 [历史验证](VALIDATION-v1.9.md)，不算作本轮重新执行。

## 独立回归

使用 .NET SDK 8 的 Release 构建。所有写盘测试使用新建隔离目录，不读取或修改真实宠物档案。

| 项目 | 本轮结果 | 重点 |
| --- | ---: | --- |
| DuckDeskPet.SelfTest | 38 / 38 | 养成、规范化、完整动作行为 |
| EconomySelfTest | 44 / 44 | 有效工时工资、边界和离线语义 |
| PetStoreSelfTest | 7 / 7 | 持久化、冲突与恢复 |
| FeedingIntegrationSelfTest | 15 / 15 | 已消费饭的独占演出、排队和取消保护 |
| PlayIntegrationSelfTest | 16 / 16 | 猜拳宿主接线、安全收场、奖励与冷却 |
| ContentSelfTest | 54 / 54 | 拥有权、事务、待装备和兼容性 |
| HonorSelfTest | 22 / 22 | 18 个稳定 ID、门槛、旧档补录和购买原子性 |
| HonorUiSelfTest | 34 / 34 | 18 张真实徽章、分页、筛选、默认 / 窄 / 矮窗及扫光清理 |
| ShopSelfTest | 29 / 29 | 商品图、分页、只读预览、真实模态确认、键盘取消与二次报价 |
| RpsSelfTest | 24 / 24 | 公平揭晓、节奏、取消 / 关闭清理界面时钟 |
| ExpansionIntegrationSelfTest | 16 / 16 | 小剧场全窗口、原有场景层序和独立预览不写盘 |

严格修正了一项旧 Core 断言：最大经验样本现在会补录三档成长荣誉。测试核对精确四个合法 ID 各一次，继续拒绝未知和重复值，没有简单放宽数量限制。

证据目录（构建产物，不随 Git 分发）：

- `.codex-build/shop-v1.10-final/`：商品网格、窄 / 矮窗、页码与购买确认。
- `.codex-build/honor-v110-final/honor-ui-report.json`：34 个断言；九张新徽章逐张目视复查，六系列全部三页可达。
- `.codex-build/preview-club-v1.10-final-qa/`：40 张自己的 WPF 视觉树图，深浅底、1.0 / 1.28 采样、正常 / 最小视口、入场 / 循环 / 收场。采样比例不是跨物理显示器 DPI 验收。
- `.codex-build/rps-v1.10-clock-final/`：猜拳小窗与动画时钟清理。

## 真实应用首轮

Release EXE 使用 `tools/run_expansion_smoke.ps1`，注入独立数据目录、测试管道和隔离标记。10 / 10 通过，97.75 秒；根目录名 `EagleExpansionSmoke-8282b24dde8443f189bd326e4a0835d7`，报告为其 `evidence/expansion-smoke.json`。

除原有九项真实购买、换装、工作、喂食、猜拳和任务消息用例，新增第十项验证：三页养成面板、设置跳转、商品分页与分类、18 枚荣誉、通知空态、可选 AI 接入只读预览、关闭动画与快速反复转场不泄漏时钟。浏览窗口不改变钱包、拥有权或装备；AI 预览使用空白临时客户端目录。

两次选拳到真实出拳分别为 **640.87 / 614.37 ms**。缩短等待没有截断上一动作或出招，不代表冷启动与首次解码没有开销。暖机工作循环短测 WPF 回调均值 60 Hz；这不是屏幕 Present 或长期 FPS 认证。

## 最终自包含包

发布目录 `dist/v1.10.0-verified/`；主程序文件版本 `1.10.0.0`，产品版本 `1.10.0+78fa814c312b2c8356a9f1c4ebc7a6a978c9da8c`。主程序与 MCP 均为 Windows x64 自包含单文件，发布成功，没有覆盖 v1.8 / v1.9 或本轮初次发布目录。

- 最终 EXE 不传 `DotnetRoot`，10 / 10 全部通过，86.34 秒，`passed` / `completed` / `fullAcceptance` 均为 true；证据根 `EagleExpansionSmoke-e6e47f0b11804df1a92e0645eeeb73f9`。
- 两次选拳到真实出拳为 **647.15 / 636.05 ms**；暖机 WPF 回调短测均值 60 Hz，P95 / 最大间隔 16.67 ms。前一个包的同类短测曾为 57.67 Hz，不能把最佳一次当作持续物理 60 FPS 承诺。
- 最终消息 / 配置联调 **26 项通过**：自己的 WPF 窗口、Windows DPAPI、假 GitHub HTTP、临时 AI 配置目录；网络写入 0。证据 `.codex-build/features-gui-smoke-5iwmtwp8/gui-smoke.json`。未接触真实账号、浏览器或 AI 配置。
- 最后修正的是验收截图渲染器：只取窗口客户区，并补回原生窗口底色，避免截图出现透明黑底或空白标题栏条带；没有改实际角色或场景图片。

| 文件 | SHA-256 |
| --- | --- |
| EagleDeskPet.exe | `33d01aa4f6dc9c2212123c8f597ad1934c666256a8e6d3605047e7b1389a654b` |
| EagleDeskPet.Mcp.exe | `4fba4aba231135a8b9c45b29531a54e8139285e1890bbec08727c70961acfb16` |
| EagleDeskPet-v1.10.0-win-x64.zip | `e4cf8a704f8ed805a32845c0d0ae5938996d22dae12d45d3286787b2872d262a` |

发布包仅含两份 EXE、README、第三方声明和 SHA256SUMS，不含存档、Token、个人配置或临时测试证据。最终压缩包为 `dist/EagleDeskPet-v1.10.0-win-x64.zip`。

归档大小 595096832 字节（约 567.5 MiB）；五个 ZIP 条目均逐一打开并计算 SHA-256，与发布目录文件一致。六份本轮文档中 60 个本地文档 / 图片链接存在，`git diff --check` 通过。

## 验证边界

- 窗口截图来自本应用自己的 WPF 视觉树，没有截图桌面、私人聊天或其他应用；图中余额 / 荣誉是隔离测试进度。
- 确认框通过真实 ShowDialog、Owner 禁用 / 恢复、Tab / Enter / Esc 路由测试；仍未做长期人工鼠标游玩和不同辅助技术验收。
- 关闭动画分支使用同一生产代码显式测试；高对比度有代码门控，但没有改用户系统设置做全套高对比视觉验收。
- 未认证多屏拖动、真实混合 DPI、远程桌面、长时间稳定性或持续物理 60 FPS。
- 本轮没有连接真实 GitHub 账号，没有改真实 AI 客户端配置；原 MCP 协议未扩展，真实客户端版本的自动投递不在本轮保证内。
- 图像生成不等于原角色已有商业授权。新图完整提示词与参考关系随源码保留。

发布复验命令：

```powershell
.\tools\run_expansion_smoke.ps1 -Exe .\dist\v1.10.0-verified\EagleDeskPet.exe
python .\tools\test_features_gui_smoke.py --gui .\dist\v1.10.0-verified\EagleDeskPet.exe
```
