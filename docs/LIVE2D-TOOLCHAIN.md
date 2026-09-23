# Live2D 小样工具链：来源、复现与许可边界

核对日期：2026-09-23。本文记录真实工具和使用边界，不代表 REQ-009 或生产角色迁移已经完成。

## 当前路线

本项目以自己的鹰拆层原画制作分层 PSD，通过独立的 PSD2Live 离线制作工具生成 Cubism 文件族，再用本地官方 Cubism Core 验证运行时兼容性。SDK 负责加载与运行已有模型，不会把普通图片自动变成模型。

- 原画和分层来源：[Live2DSource 说明](../DuckDeskPet/Assets/Live2DSource/README.md)
- 打包、禽类绑定 profile 和实际命令：[Live2DArt 入口](../tools/Live2DArt/README.md)
- 整体重构和验收边界：[Live2D 重构计划](LIVE2D-REFACTOR-PLAN.md)

离线制作工具不进入 WPF EXE，也不修改照料、经济、购买、成就或原有 PNG 动画后端。这里没有采购 PRO、订阅服务或采用 5.4 Alpha 的授权。

## 固定工具来源

| 工具 | 本轮固定版本 | 官方或上游来源 | 本地用途 |
|---|---|---|---|
| Cubism Editor | 5.3.04，Windows | [官方下载页](https://www.live2d.com/en/cubism/download/editor/)、[版本记录](https://docs.live2d.com/en/cubism-editor-manual/new-feature-introduction-title/) | 预留 CMO3 人工检查；尚未完成打开、编辑、保存、再导出的往返验证 |
| Cubism SDK for Web | 5-r.5 | [官方 ZIP](https://cubism.live2d.com/sdk-web/bin/CubismSdkForWeb-5-r.5.zip) | 本地读取 Core，做独立模型加载、参数与渲染探针；不复制 SDK 到源码或安装包 |
| PSD2Live | 1.1.1，Windows x86_64 portable | [固定发布包](https://github.com/tsunehimatoi/psd2live/releases/download/v1.1.1/PSD2Live-1.1.1-windows-x86_64-portable.zip)、[固定源码](https://github.com/tsunehimatoi/psd2live/tree/4fe5fcfffe11f579c9d0521ef8349d2116c6726c) | 独立离线分层识别、网格、绑定和文件导出 |
| ag-psd / sharp | 31.0.2 / 0.35.4 | [package.json](../tools/Live2DArt/package.json)、[pnpm-lock.yaml](../tools/Live2DArt/pnpm-lock.yaml) | RGBA 部件裁切、合成和 PSD 写入/读回检查；依赖分别为 MIT / Apache-2.0 |

PSD2Live 的固定源码提交为 `4fe5fcfffe11f579c9d0521ef8349d2116c6726c`。不将当前上游主分支与此版本混用；适配器依赖 1.1.1 的配置结构，升级工具必须重新检查绑定和输出。

### 已核对的 SHA-256

以下值对本轮实际下载文件重新计算过，不是只复制文件名中的版本号：

```text
CubismSdkForWeb-5-r.5.zip
67064A7FB1812CF502F5C4A03BFE12CC638C75A621BB4ACF06BB28763DF06BA0

Live2D_Cubism_Setup_5.3.04-complete.exe
457421E617CC836ECED24236CA3564935580B2D3B26BDEAB9A304A42B83308BC

PSD2Live-1.1.1-windows-x86_64-portable.zip
BBF8FC8B6D10FCF21925D58B28528C8A2BCF0D541D8E685CC3CC88F539ED599F

psd2live-1.1.1-9221781e48ba5a767e6b8fdf8f5dc3dd.jar
9F22CEA3FB5D4877FC852AD850563BF89EFC7A9D6E1195D704EC4DA036DF4826
```

`-complete.exe` 是完整下载文件的本地命名。该文件 Authenticode 状态为 `Valid`，签名主体为 `Live2D Inc.`。仅用 7-Zip 解包到 `.tool-cache/live2d-official/editor-5.3.04`，未注册安装；两次原生窗口激活未成功，已关闭本轮启动的进程。因此下载成功、签名有效和产出 `.cmo3` 均不能替代编辑器实测。

缓存位置均相对仓库根目录，且被 Git 忽略：

- `.tool-cache/live2d-official/`：官方 SDK ZIP、Editor 下载及解包结果
- `.tool-cache/psd2live-research/portable/`：独立 PSD2Live 工具及其自带运行时
- `.tool-cache/psd2live-research/source/`：只供审查的固定上游源码
- `.tool-cache/psd2live-research/output-eagle-*/`：离线导出实验，不自动成为商店角色包

## 复现时的边界

实际安装、输入和导出命令以 [Live2DArt README](../tools/Live2DArt/README.md) 为单一入口，不维护第二份易过期的 Java classpath 或工具路径。复现前先核对上述文件哈希，按锁文件安装依赖，并使用全新的输出目录。

1. 输入使用项目自己的 `eagle-parts-v1.png`，按 `pack-eagle.mjs` 的实际尺寸与坐标拆成 15 层 PSD；保留原图、布局和中性合成图
2. 导出使用 `ExportPsdModel.java` 和 `eagle-poc-rig.json`，不使用最初未经修正的默认人体绑定作为最终小样
3. 导出记录应保留输入 PSD、profile、工具 JAR 和各输出文件的哈希，以及图层语义、参数、脚底固定和眼睛几何绑定检查；导出目录中的 `authoring-profile.json` 和 `authoring-validation.json` 用于追溯
4. 独立官方 Core 检查必须读取这次导出的 MOC3；源绑定断言、JSON 元数据或第三方预览通过，都不等于官方运行时通过
5. 官方 Editor 往返、实际观感和主程序接入分别验收；只导出文件不算角色完成，不跳过角色包校验直接上架

### 禽类小样的特殊约束

- 眼睛原始命名为 `irides-l/r`，但它们实际是完整黑眼睛，没有独立眼白。默认人脸绑定依赖眼白裁剪，可能出现有眨眼参数却闭不上眼；小样 profile 显式绑定到闭眼几何，不能仅检查参数存在
- 两脚需独立固定到根层，不能随整身呼吸/摆动漂移；仍需检查翅膀、身体与固定脚的连接处
- 当前整块上喙属于闭嘴图层，默认 `MOUTH_CLOSE` 会随开口参数淡出，而 `mouth lower` 会被识别为人嘴。小样禁用自动嘴唇轮廓，嘴参数保持闭合；专用上/下喙开合、说话和进食绑定尚未完成
- 本轮降低头身变形强度，使用 2048 图集；眨眼、呼吸、摆头只是基础小样，不等于打哈欠、害羞、工作、猜拳等全部语义动作或 6 套服装已经重制
- 不能因图集缩为 2048 就保证 Cubism FREE 可保存。上游默认面部轮廓变形器为 8×16，超过官方 FREE 的 9×9 上限；如需 FREE 编辑器精修，必须处理该限制并实际打开、保存、再导出。FREE 的完整限制以[官方对照表](https://www.live2d.com/en/cubism/comparison/)为准

## 许可与发布边界

这是一份工程来源记录，不替代发布主体的许可判断。下载、本地开发、源代码开源和向他人分发应用是不同事项。

### 离线工具和独立原画

PSD2Live 是独立第三方项目，其代码采用 GPL-3.0，示例素材另有许可；本项目不 vendor 其 JAR、运行时、源码或示例角色。[上游说明与第三方清单](https://github.com/tsunehimatoi/psd2live/blob/4fe5fcfffe11f579c9d0521ef8349d2116c6726c/THIRD_PARTY_NOTICES.md)

GPL 对工具输出的适用取决于输出内容本身是否构成受该许可覆盖的作品，不能简单声称“用 GPL 工具，所有模型必然 GPL”，也不能声称任何导出结果都不受限制。本次仅以自己的拆层图生成作者输出，不带入第三方样例的纹理、模型或动作。[该版本 GPL 第 2 节](https://github.com/tsunehimatoi/psd2live/blob/4fe5fcfffe11f579c9d0521ef8349d2116c6726c/LICENSE)

`ExportPsdModel.java` 及其自测直接链接 PSD2Live 的作者工具 API，明确采用 `GPL-3.0-or-later`，见 [LICENSE-EXPORTER.txt](../tools/Live2DArt/LICENSE-EXPORTER.txt)。它们与生成出的美术资产是不同对象，不能用“输出不自动 GPL”替源码及依赖免除义务。WPF 宠物不会加载该 Java 适配器或 GPL 工具库。

鹰原画由项目形象参考生成，不是给第三方 Live2D 模型换皮。原图生成来源不等于已经完成最终形象或再分发权利审核；表情包原件仍按仓库规则留在本地，不能把别人的素材许可顺带授予本项目。

### 官方 SDK 和 Editor

Cubism Core 使用 [Live2D Proprietary Software License](https://www.live2d.com/eula/live2d-proprietary-software-license-agreement_en.html)，Framework 等组件另适用 [Live2D Open Software License](https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html)，示例模型还有各自条款。它们不是本仓库代码许可的一部分。本轮只从本地官方下载读取 Core 进行验证，不复制专有 Core、官方 sample 或 SDK 源码到仓库/发布包，也不重新许可这些文件。

本地验证无需先购买 SDK 发行许可；正式分发时仍要按发布主体、用途和应用类型检查当前规则，尤其不能默认“个人、开源或免费”就涵盖所有情形。官方对可扩展应用设有单独规则；将来允许用户导入角色包并作为 AI 界面的产品，应在发布前确认是否属于该类，必要时向 Live2D 询问。本次不代用户申请或购买任何许可。[官方 SDK 发行许可说明](https://www.live2d.com/en/sdk/license/)

本轮不采用 Cubism 5.4 Alpha、Alpha 外部结构编辑 API 或依赖其期限的 MCP。稳定版工具来源和本地验证记录不能被表述为官方认证、官方背书或所有目标环境的兼容保证。
