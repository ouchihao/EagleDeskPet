# 工作场景修订 v2 / v1.8

工具：内置 `image_gen.imagegen`。旧资源不覆盖；PNG 成品是 ImageGen 画面经既有抠图、定位与分段 RIFE 采样得到，不是代码重画的鹰头。

选定生成原件已完整复制到本目录；下表保留工具输出文件名用于追溯，不公开本机生成缓存路径：

| 项目内原件 | 生成文件 | 编辑参考 |
| --- | --- | --- |
| busy-fire-sheet-v2.png | exec-4427eb29-346e-4a34-9955-f7096b036a5f.png | busy-fire-sheet-v1.png |
| work-desk-v2.png | exec-032bea5b-902d-4e2c-972f-b212a6725693.png | SceneProps/Work/desk.png |
| work-blink-closed-v2.png | exec-438ea381-9e89-4640-b8f3-56cd943455f9.png | Animations/WorkLoop/frame-0000.png |
| busy-blink-closed-v2.png | exec-1d292013-d2cd-49c7-b1a5-742141bd9a1f.png | Animations/BusyLoop/frame-0000.png |

## 实心火焰：实际提示词

Use case: precise-object-edit. Asset type: four-frame 2x2 animation key sprite sheet for the existing desktop-pet flame effect. Image 1 is the exact edit target. Change ONLY the enclosed hollow centers of ALL FOUR flames: replace each entire checkerboard hole AND its INNER yellow outline with continuous opaque warm red-orange flame fill matching the surrounding red-orange. Keep the OUTER yellow outline, four distinct outer silhouettes, current proportions, heights, bottom baseline, grid positions and fiery flicker variations. Each flame must become ONE SOLID FILLED flame silhouette, NOT a ring, NOT a U-shape. No inner yellow ring, no inner cutout, no dark center. Preserve generous empty gutters. Outside all flames MUST be genuinely transparent alpha, not a painted checkerboard. No character, table, laptop, eyes, text, symbols or new objects. Keep same flat cartoon 2D red/orange/yellow palette. The eagle will be composited in front separately; the center must be solid filled everywhere behind it.

## 前挡板办公桌：实际提示词

Use case: precise-object-edit. Asset type: exact replacement front-facing desk prop for an existing 2D cartoon desktop pet. Image 1 is the existing desk; preserve its pale honey-yellow tabletop, amber outline, shallow top-down perspective, wide horizontal proportions and the top surface angle. Modify ONLY the lower desk structure: add a continuous warm honey-wood modesty panel spanning the inner gap between the left and right legs and reaching the same bottom baseline as the legs. It should look like a small sturdy closed-front office desk, thin/light design, NOT a massive cube or giant cabinet. The full lower front must hide the seated eagle's lower body and feet behind the desk, with NO large open gap below the tabletop. Maintain exactly the tabletop's visual width and position within the existing image. No eagle or animal, no laptop, chair or text, no extra prop. Genuine transparent background outside the isolated desk silhouette. Keep the original cartoon linework and minimal soft shading. Preserve the entire object uncropped with surrounding empty space; the source's desktop canvas is wide with the desk near its bottom.

## 正常闭眼关键姿势：实际提示词

Use case: identity-preserve. Asset type: one exact closed-eye animation key for the existing desktop eagle, calm office typing pose. Image 1 is the EDIT TARGET, not merely inspiration. Close BOTH EYES completely: replace each open black pupil eye with a single slim warm-brown gently curved CLOSED EYELID line on the white feather face. No visible eyeball, iris, pupil, yellow eye interior or black eye oval. Preserve the same eyebrows and their expression, identical head outline/size, beak, cheeks, jagged feather tips, plump brown belly, exact typing-hand positions and planted yellow feet. Preserve the whole original silhouette and all non-eye features as closely as possible. Do not add hair, clothes, props, text, shadow or scene. This is a blink key for interpolation; it must look like the EXACT SAME sprite with only its eyelids closed. Full body, original front view, same tiny body versus huge white head proportions. Genuine transparent alpha background, no checkerboard, clean smooth alpha edges. Keep complete uncropped body and centered layout.

## 紧张闭眼关键姿势：实际提示词

Use case: identity-preserve. Asset type: one exact closed-eye animation key for the existing desktop eagle, tense busy typing pose. Image 1 is the EDIT TARGET, not merely inspiration. Close BOTH EYES completely: replace each open yellow angry eye with a single slim warm-brown gently curved CLOSED EYELID line on the white feather face. No visible eyeball, iris, pupil, yellow eye interior or black eye oval. Preserve the same eyebrows and their expression, identical head outline/size, beak, cheeks, jagged feather tips, plump brown belly, exact typing-hand positions and planted yellow feet. Preserve the whole original silhouette and all non-eye features as closely as possible. Do not add hair, clothes, props, text, shadow or scene. This is a blink key for interpolation; it must look like the EXACT SAME sprite with only its eyelids closed. Full body, original front view, same tiny body versus huge white head proportions. Genuine transparent alpha background, no checkerboard, clean smooth alpha edges. Keep complete uncropped body and centered layout.

## 制作与验证契约

- `tools/prepare_work_scene_v2.py --phase keys` 提取关键姿势并配准。正常／忙碌开眼锚点以及退出最终站立都原样取自旧图，接口端点像素完全一致。
- 正常→忙碌，1.5 秒 / 91 帧：原工作锚点 0 → 闭眼正常 18 → 闭眼紧张 42 → 原忙碌锚点 90。不同眼球种类不在同一个 RIFE 段中。
- 忙碌退出，2 秒 / 121 帧：原忙碌锚点 0 → 闭眼紧张 15 → 闭眼正常 33 → 原退出中间姿势 66、92 → 原站立 120。
- 火焰 2 秒 / 121 帧含相同两端；四个新实心关键姿势分段采样，运行时跨片段使用连续火焰时钟。源图外部返回了棋盘 RGB，使用既有火焰红黄色域提取，内部颜色由 ImageGen 实际绘制而非 Python 填充。
- 新桌子尺寸 306 像素，位于 x39，底部 345，分层切点 y276。前挡板自然遮住工作坐姿下半身，未裁剪或改变角色脚点。
- `WorkStageRenderer` 火焰生长锚点改为 Image 局部坐标，避免重复计算 WPF Uniform 的留白造成红色“地毯”。
- `tools/test_work_scene_v2.py` 覆盖帧数、端点、无相邻重复、脚点、实心火焰、242 张循环帧零露脚、闭眼屏障、旧资源保留。
- `tools/WorkSceneV2SelfTest` 使用实际 WPF `WorkStageRenderer` 和嵌入素材逐帧渲染；不是桌面截图。PNG 联系表含 0.25/0.5/0.75 秒等节点。Windows WIC 会忽略原始 GIF 的延时，因此再使用 `tools/encode_work_scene_preview.py` 把已有捕获帧编码为 20 fps / 每帧 50 ms 的独立预览，逐帧回读确认延时；应用原帧仍为 60 Hz。
