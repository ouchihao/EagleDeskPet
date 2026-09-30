# Official Cubism model probe（独立开发工具）

真正使用本机官方 **Cubism SDK for Web 5-r.5 Core + Framework WebGL renderer** 验证 `.moc3`，不是图片切换，也不是 psd2live 自己的回读器。不接入宠物主程序，不读取宠物存档，不运行外部模型里的脚本，不使用日常浏览器配置。

## 依赖与许可证

- Windows 10 2004 / 19041+（此探针目标）、.NET 8、已安装 Microsoft Edge WebView2 Runtime
- NuGet `Microsoft.Web.WebView2` **1.0.4191.47**（`packages.lock.json`），遵循包内 Microsoft 许可证
- Node.js，pnpm；构建工具 `esbuild` **0.25.10**（MIT，`pnpm-lock.yaml` 固定校验和）
- 用户自行下载并接受许可的 [官方 SDK 5-r.5](https://www.live2d.com/en/sdk/download/web/)，本工具不下载 SDK，不复制 Core 入仓
- Core 为 [Live2D Proprietary Software License](https://www.live2d.com/eula/live2d-proprietary-software-license-agreement_en.html)，**不是 MIT**；Framework/原始 shader 为 [Live2D Open Software License](https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html)，并非仅遵循本仓库许可证。模型素材有其独立授权。发布前须另查适用的 SDK/素材/商业许可，开发成功不代表获得公开再分发授权
- 本次官方下载 ZIP SHA-256：`67064a7fb1812cf502f5c4a03bfe12cc638c75a621bb4acf06bb28763df06ba0`

SDK、生成的 Framework bundle、WebView profile 和报告放 `.tool-cache` / `.codex-build`，不要提交、公开托管或随宠物发布。源码仅含我们自己的适配与检查代码。

## 构建与运行

以下变量使用你自己的绝对路径，`$sdkRoot` 指向包含 `Core`、`Framework`、`cubism-info.yml` 的官方解压根目录：

```powershell
Set-Location tools/Live2DModelProbe
pnpm install --frozen-lockfile
node build-framework.mjs $sdkRoot $frameworkBundle
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet run -c Release --no-build -- --sdk $sdkRoot --framework $frameworkBundle --model $modelFile --output $newEvidenceDirectory --seconds 8 --eagle-contract
```

`$frameworkBundle` 是新的、未占用的绝对 `.js` 文件路径，不能在 SDK 中；脚本从用户 SDK 原始 TypeScript 构建，不修改 SDK，生成 bundle 及 SHA-256 provenance 侧车。输出已有文件时拒绝覆盖。C# 启动再校验 bundle、SDK release/info、输入源码的 hash，不能拿过期侧车当通过证据。

`$newEvidenceDirectory` 必须为空或不存在，且不能位于 SDK / 模型目录。默认自动运行后关闭；加 `--preview` 保留独立窗口，参数滑条、播放/暂停、归位、深浅背景均可操作。不操纵已有浏览器。

`--eagle-contract` 仅用于本项目鹰的命名契约：左右 `ArtMeshEyelashL/R` 必须由对应 `ParamEyeLOpen/ROpen` 有效驱动且闭眼高度不超过原高 35%；`ArtMeshFootwearL/R` 在所有参数 min/default/max 的位移必须严格为 0。不要用该检查去要求无关人物模型固定脚。几何断言不能代替对眼睛高光、闭眼轮廓和动作节奏的目视验收。

## 验证内容与产物

1. 按[官方建议](https://docs.live2d.com/en/cubism-sdk-manual/moc3-consistency/)先执行 `Moc.prototype.hasMocConsistency`，失败不会调用模型创建
2. 真正调用 `Moc.fromArrayBuffer`、`Model.fromMoc`；记录官方 Core 版本、MOC 版本、参数/网格/顶点和文件 hash
3. 扫描每个参数的 min/default/max，记录每个 drawable 的顶点/透明度变化及非有限值；眼睛/呼吸/头摆必须有真实作用
4. 使用同版官方 Framework renderer 和 SDK shader 渲染，包含 clipping masks；固定 neutral 取景矩阵，不逐帧自动缩放掩盖漂移
5. 截图 neutral、闭眼、1/4 / 1/2 / 3/4 睁眼、呼吸、左右 / 上下摆头、左右歪头、两组 XYZ 组合极值与张嘴；记录 WebGL 错误与实际非空像素。共 15 个姿态固定使用中性取景，组合极值用于暴露头颈拉长或遮挡问题，不代表已批准作为待机动作幅度
6. 连续驱动与错误监视；必须实际产生帧并完成至少请求时长 90% 的动画/墙钟采样才可能通过，运行时 error/rejection 导致不通过

带全身动作参数的模型额外截图双翼上举、鸟喙张开以及哈欠、进食的肘部组合；带 `ParamBusyEyes` 的模型再截图黄色忙碌眼的全开、半闭与闭合。它们是原作者参数的固定姿态检查，不是完整动作播放器；生产 14 动作时序与场景层级由 `DuckDeskPet/Live2DStage` 和真实宿主 smoke 验证。

`ParamEyeBallX` 实扫有效时再追加九个视线姿态：独立瞳孔四向、生产小幅头眼四向、完全闭眼时向右上看。按真实 sweep 结果启用，不能仅以参数名称存在宣称瞳孔能动。新旧模型对照应裁同一实际画布，排除标题文字；中立、半闭、全闭都需要检查，动态跟随另由 `tools/GazeStageSelfTest` 验证。

`provenance.json` 为输入来源，`initial.json` 为加载/静态扫描结果（此时动画尚未验完，`passed` 为 false 正常），`report.json` 为最终结果。失败写 `failure.txt`，不会伪装成功。正常 exit 0 表示本工具限定检查通过，exit 1 表示运行/模型未通过，exit 2 表示参数/输入错误。

`animation-30fps.webm` 是 **canvas 的 30 fps 编码预览**，只含模型本身，不抓桌面、其它窗口或用户数据。报告中的 `requestAnimationFrame` 是回调间隔，不是物理显示帧率测量，不证明生产 EXE 60 FPS。WebView 运行配置单独放证据目录。

修复肩部裂口等联动缺陷时，可同时传 `--eagle-contract --stress-poses`，在常规采样后增加独立的 2 秒八个 XYZ 角点往返压力测试，身体角度反向、呼吸高低联动。每段 250 ms 先保持精确角点 40 ms，再用 210 ms 平滑过渡；`exactCorners` 从实际绘制样本中的精确 ±1 三元组计数，不能以经过八段或接近 0.99 冒充八角点到达。生成 `stress-60fps.webm` 与逐帧几何记录 `stress-report.json`，仍使用同一中性取景；该刻意极端的快速轨迹不是生产动作。帧数、八角点实际覆盖、非有限值、错误与固定脚必须检查通过，最后仍需逐帧目视羽缘、底色与遮挡。编码请求 60 fps 不能证明物理屏幕 60 FPS。

## 测试与边界

```powershell
node --test audit.test.mjs
node --check Stage/probe.js
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet run -c Release --no-build -- --self-test
```

当前仅证明官方 Core 兼容、基本参数驱动和 WebGL 预览。没有实现/验收全部 14 动作、动作调度、物理、motion3 播放、服装/场景适配、桌宠窗口穿透、性能预算或 Cubism Editor 的 `.cmo3` 编辑体验。后者由独立 Editor 实测确认，不能由此工具推断。
