# 全身 Live2D 绑定契约

`eagle-full-rig.json` 是在 Canonical 原像素分层、胸口软锚、固定双脚基础上的独立增量，不会修改旧 profile。模型作者工具只负责网格、参数、透明度和模型导出；生产动作时序由 `DuckDeskPet/Live2DStage/actions.js` 驱动，不能以导出成功代替全部动作验收。

## 参数

L / R 指角色自身左右，因此 R 是屏幕左侧猜拳手。以下新增参数默认均为 0。

| 参数 | 范围 | 实际绑定 |
| --- | --- | --- |
| `ParamWingLLift` / `ParamWingRLift` | 0..1 | 独立举翼，最高约 105°，肩根连续蒙皮 |
| `ParamWingLSwing` / `ParamWingRSwing` | -1..1 | 左右摆翼，附加约 ±25° |
| `ParamWingLFold` / `ParamWingRFold` | 0..1 | 独立肘弯约 0..110°，肘部连续柔性蒙皮 |
| `ParamBodySquash` | -1..1 | 负值舒展、正值压低身体，脚不继承形变 |
| `ParamBeakOpen` | 0..1 | 上喙、下喙、内部口腔分别运动，禁用旧人嘴拉伸 |
| `ParamHandRock/Paper/Scissors` | 0..1 | 三种独立手型显现，调用方保持三者之和不大于 1 |
| `ParamHandMorph` | 0..1 | 必须等于三手型之和，将旧圆翅远端收为腕，不叠加两个手掌 |
| `ParamBusyEyes` | 0 或 1 | 普通黑眼 / 无瞳黄色眼切换，仅在完全闭眼时切型 |

每个上臂、前臂有 5×3×3 个 Lift / Swing / Fold 联合关键形。右前臂另外有六个掌腕重塑关键形；三个手型另外有六个真实展开关键形，各为 270 个联合形。旧圆翅的远端先收成腕部，新掌才从开放腕口连续展开；掌根与前臂重叠，不能把另一个掌贴到旧圆掌上。手腕继承肩与肘的真实方向，使用朝上掌轴与朝下前臂轴间的 180°基准变换，主动翻腕不超过 65°。活动翼与手型使用 3 像素边界 / 8 像素内部网格间距，保留小手轮廓；两臂独立，不通过贴图翻转或整身淡入淡出切换。

原生 `ParamAngleX/Y/Z`、`ParamBodyAngleX/Y/Z`、`ParamBreath`、`ParamEyeLOpen/ROpen`、眉毛参数继续可用。忙碌眼分别有 0 / 0.5 / 1 睁眼几何，不能用跨眼型渐变制造黑瞳与黄色眼重影。`ParamMouthOpenY` 是保留的第三方原生参数，不作为鸟喙动作接口。

## 图层与遮挡

上臂留在脸后，前臂和猜拳手留在嘴前；桌面遮挡的前景候选只有两个前臂与三种手型，不能把带隐藏肩根的整翼强行提升到脸前。运行时读取每份模型的 `action-bindings.json`，其 `drawables` 为源层名到实际 drawable ID 的映射，`foregroundDrawableIds` 为明确的前景集合，`parameters` 包含完整 id / min / max / default。不得猜测 PSD2Live 自动编号。

`handAttachments.l/r` 显式记录前臂 drawable ID 和原生画布握点。道具应以中立网格三角形重心权重定位该点，再随 Core 变形追踪，不以时间曲线或固定屏幕偏移猜测手的位置。

新增部件必须 `hidden=false`，默认 opacity=0，才能保留可见动作所需的像素与网格。衣服沿用同一参数契约；头饰和眼镜显式挂 `DeformHeadContainer`，背饰挂 `DeformBodyZBreath`。`calibrationLayers` 只包含原 17 个主体层，新增帽子或披风不得改变头身比例的自动估计。

## 六套变体导出

先用 `pack-action-eagle.mjs` 产生六套分层源，再运行下面的批处理。`$java` 与 `$psd2liveApp` 指向已取得并审核过的本地工具；输出必须不存在，脚本不会覆盖旧模型。

```powershell
node tools/Live2DArt/export-action-models.mjs `
  .codex-build/live2d-full-source `
  .codex-build/live2d-full-models `
  $java $psd2liveApp
```

每份输出有自己的 `authoring-profile.json`、`authoring-validation.json`、`action-bindings.json` 和标准模型文件族；批处理同时保留六个实际 profile。脚本只生成新目录，不复制资产到生产，不装载或分发官方 Core。

## 验收边界

作者回归断言中立顶点不变、全部翼关键形非空、真实鸟喙位移、独立手型透明度、黄色眼闭合/反向透明度、双脚 root 固定、原源文件不变、未知/失配输入及已有输出拒绝。Canonical 另做回归，不能因 full profile 改动而改变已批准的旧 MOC。

仍须用官方 Core 实际渲染所有动作与六套服装：特别检查举翼后露出的隐藏圆肚底片、前臂切口、脚边、肩羽、闭眼换型、吃饭与工作道具遮挡。数值有效、模型加载成功、静态中立一致都不能代替这些目视检查。作者报告始终将独立官方运行时和 Editor round-trip 验收标为 pending。
