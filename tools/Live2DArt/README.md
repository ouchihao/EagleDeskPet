# Live2D 原画与离线模型制作工具

本目录是实验性作者工具，不是桌宠运行时。默认 PNG 后端和已发布模型不会被这里的命令覆盖。当前验收目标仅为全身中立、眨眼、轻微转头、呼吸和固定双脚；没有宣称完成 19 动作、六套衣服或官方 Editor 可编辑性验收。

## 依赖与许可证

- Node.js 与 pnpm：`package.json` / `pnpm-lock.yaml` 固定 `sharp`、`ag-psd` 版本
- JDK 21：运行或编译 Java 作者适配器
- 用户自行取得的 [PSD2Live v1.1.1 Windows 便携版](https://github.com/tsunehimatoi/psd2live/releases/tag/v1.1.1)，解压后的 `app` 目录包含所需 JAR
- 该工具源码固定版本为 `4fe5fcfffe11f579c9d0521ef8349d2116c6726c`；本仓库不附第三方源代码、JAR、Cubism Core 或官方 Editor

实测发布 ZIP SHA-256：`BBF8FC8B6D10FCF21925D58B28528C8A2BCF0D541D8E685CC3CC88F539ED599F`

实测主 JAR SHA-256：`9F22CEA3FB5D4877FC852AD850563BF89EFC7A9D6E1195D704EC4DA036DF4826`

这些是本次下载的指纹，不是发布者签名；发布 EXE 未签名，本流程使用本机 JDK 执行 JAR。工具不启用可选高清化、不下载权重、不启动 GUI/MCP 服务。

`ExportPsdModel.java` 与 `ExportPsdModelSelfTest.java` 采用 `GPL-3.0-or-later`，见 [LICENSE-EXPORTER.txt](LICENSE-EXPORTER.txt)，因为它们直接链接 GPL 作者库；此许可范围不改变独立 WPF 应用。独立的 `pack-eagle.mjs` / `inspect-parts.mjs` 遵循仓库现有许可声明，未在此单独授予额外许可；仓库尚未指定统一开源许可证。原画、人物模型和第三方程序的来源及许可分别记录，不能从工具许可推定第三方美术授权。

## 1. 拆件图组装 PSD

### 当前形象基准：原版像素分层

2026-09-30 后使用 `pack-canonical-eagle.mjs`。**唯一外观基准是程序当前的 `mascot-animated-neutral.png`**，它来自哈欠原画首帧，不是 `mascot.png` 的缩小版。不能再混用两幅稿件的坐标、独立压扁脸或重新生成可见五官。

```powershell
node tools/Live2DArt/pack-canonical-eagle.mjs `
  DuckDeskPet/Assets/mascot-animated-neutral.png `
  DuckDeskPet/Assets/Live2DSource/eagle-parts-v2.png `
  .codex-build/live2d-canonical-source
node --test tools/Live2DArt/pack-canonical-eagle.test.mjs
```

原生画布保留 384×346，每个可见原像素以图层蒙版机械分区，不重新描绘或独立缩放。隐藏肩部、翅膀上端和内部嘴形可以使用补绘，但站立时必须被原像素完全遮住。双翼上端保留二十多像素隐藏余量，脚上沿有 4 像素重叠；眼底取原脸相邻空白色带，避免闭眼露出另一张画的白色矩形。

输出 `eagle.psd`、`eagle-neutral.png`、`identity.json`。发布前要求 alpha 与全部可见 RGB 零差异；回归从解码后的 PSD 图层独立重合成，故意清空缓存预览，并用单像素损坏用例验证检查有效。完全透明像素的 RGB 扩边不是可见画稿，不作为颜色一致断言对象。

原版中立图与隐藏补绘均验证原生尺寸和 RGBA SHA-256，`identity.json` 记录两者来源。隐藏底片即使只改颜色、中立依然零差异，也必须重新审核动态遮挡，不能悄悄换稿通过旧验收。

对应导出 profile 为 `eagle-canonical-rig.json`。它把摆头支点放在胸口 `(193,215)`，羽缘通过 `187..219` 软锚区连续连接身体；生成 45 个 XYZ 联合关键形，关闭人形五官大幅位移，保留真实头部运动。`pinFaceFeathers` 同时约束内层 `DeformFaceNinePose` 的下羽缘，避免外层已连接、内层仍横移而拉出双边；中立与上方控制点不变。模型截图仍需逐帧检查：静态像素一致**不能**证明纹理采样接缝、隐藏肩膀、闭眼或动态遮挡合格。

### 历史 / 实验拆件布局

以下命令从仓库根目录运行。`pack-eagle.mjs` 默认保留已检查的 1254×1254 `eagle-parts-v1.png` 布局；可传第三个布局 JSON，但不是任意图片的自动分层工具。`eagle-compact-layout.json` / `eagle-compact-rig.json` 是本轮被否决的重绘比例实验，不是当前原味模型，不能据此替换生产资产。

```powershell
pnpm --dir tools/Live2DArt install --frozen-lockfile
node tools/Live2DArt/pack-eagle.mjs `
  DuckDeskPet/Assets/Live2DSource/eagle-parts-v1.png `
  .codex-build/live2d-author-source
```

输出目录必须为空。生成 `eagle.psd`、`eagle-neutral.png`、`layout.json`，并回读 PSD 检查 512×512、15 个独立 RGBA 图层及层名。左/右按角色自身定义。

打包器回归（真实 PSD 字节重建、非空目录不改写、错误尺寸拒绝）：

```powershell
node --test tools/Live2DArt/pack-eagle.test.mjs
```

## 2. 生成 CMO3 / MOC3

将 `$java`、`$psd2liveApp` 改为本机真实绝对路径；输出目录必须不存在。类路径只指向用户已检查、取得的外部工具，不会自动安装依赖。

```powershell
$java = 'D:\Program Files\JetBrains\apps\Android Studio\jbr\bin\java.exe'
$psd2liveApp = 'D:\Code\github\EagleDeskPet\.tool-cache\psd2live-research\portable\app'
& $java -Xmx4g -Djava.awt.headless=true -Dfile.encoding=UTF-8 `
  --class-path "$psd2liveApp\*" `
  tools/Live2DArt/ExportPsdModel.java `
  .codex-build/live2d-canonical-source/eagle.psd `
  .codex-build/live2d-canonical-export `
  tools/Live2DArt/eagle-canonical-rig.json
if ($LASTEXITCODE -ne 0) { throw 'Model export failed' }
```

适配器使用显式 JSON 配置匹配图层，不按角色名字写运行时分支。当前 profile：

- 将完整黑眼 `irides-l/r` 绑定为 `EYELASH` 区域，再写入专用 `0 / 0.5 / 1` 网格关键形：全闭压成约 1.25 像素高、1.5 像素弧深的眼缝，中间线性收拢，睁眼保持原形，不整眼淡出；单独瞳孔预设依赖眼白裁剪，而这份原画没有独立眼白，默认人形闭眼预设也不适合该黑眼形
- 双脚重新挂在 root，并重建正确坐标，不继承身体呼吸或转头变形；导出前检查脚没有参数几何
- 关闭自动人嘴轮廓和眼球物理；首个演示将 `ParamMouthOpenY` 固定为 0，不能把当前喙绑定当作可用的张嘴动作
- 2048 图集、32 像素网格间距、0.65 头部与身体强度

以上数值为旧 `eagle-poc-rig.json`。当前 canonical profile 使用 16 像素网格间距、原生画布坐标和胸口软锚。作者回归读取实际 profile / PSD 尺寸生成坏配置用例，不能依赖旧 512 画布的硬编码坐标。

导出产生标准文件族和 `authoring-profile.json` / `authoring-validation.json`。后者记录源 PSD、profile、作者 JAR、每个导出文件的 SHA-256，以及脚与闭眼绑定断言。其 `officialRuntimeValidation`、`editorRoundTripValidation` 明确为 `pending`；不能把作者自己的回读通过等同官方验证。

导出拒绝未知配置、缺层、重复匹配、错误源层数、无效图集、缺参数、空眼部形变和已有输出目录。检测到不兼容的第三方配置 ABI 也会停止，不猜参数位置。生成失败时保留独立输出证据，不自动晋级到应用资源。

## 3. 作者适配器回归

使用已有 PSD 和新的证据目录，不访问真实宠物存档：

```powershell
$javac = 'D:\Program Files\JetBrains\apps\Android Studio\jbr\bin\javac.exe'
& $javac -encoding UTF-8 -cp "$psd2liveApp\*" `
  -d .codex-build/live2d-exporter-classes `
  tools/Live2DArt/ExportPsdModel.java `
  tools/Live2DArt/ExportPsdModelSelfTest.java
if ($LASTEXITCODE -ne 0) { throw 'Authoring tests did not compile' }
& $java -Xmx4g -Djava.awt.headless=true -Dfile.encoding=UTF-8 `
  -cp ".codex-build/live2d-exporter-classes;$psd2liveApp\*" `
  ExportPsdModelSelfTest `
  .codex-build/live2d-canonical-source/eagle.psd `
  tools/Live2DArt/eagle-canonical-rig.json `
  .codex-build/live2d-exporter-regression
if ($LASTEXITCODE -ne 0) { throw 'Authoring regression failed' }
```

回归包含真实导出、现有输出拒绝及 hash 不变、无效配置/缺层拒绝、源 PSD 不变。它不是官方 Cubism 兼容性测试。

## 4. 必须另做的验收

用官方 Core 检查 MOC 一致性、创建模型、所有顶点有限、纹理真实绘制，并测量睁眼/半闭/全闭、头摆、呼吸和脚部位移；必须同时目视截图/播放效果。然后在正常授权的稳定版 Cubism Editor 打开 `.cmo3`、修改保存、再官方导出 `.moc3` 并重测。

当前自动脸部变形器包含超过 FREE 网格上限的配置，不能因图层/参数数量较少而宣称 FREE 可保存；官方 Editor 激活、打开和 round-trip 必须有独立证据。不得修改时间或绕过软件许可。工具链状态与限制见 [LIVE2D-TOOLCHAIN.md](../../docs/LIVE2D-TOOLCHAIN.md)。
