# 工作 / 忙碌动画资源（v1）

本批由内置 **ImageGen** 生成角色关键姿势和独立道具，再由
`tools/prepare_work_animation.py` 使用既有 RIFE 管线生成连续 RGBA 帧。
没有把原 GIF 当作最终半身贴图，也没有重画替代形象的矢量鹰。

## 资源与运行时契约

所有角色和道具 PNG 都是 **384 × 346**，使用既有角色缩放，不改变屏幕脚点。
角色站立图从现有 `mascot-animated-neutral.png` 原样读取。

| 动作 | 秒 | 帧（含两端） | 首尾 |
| --- | ---: | ---: | --- |
| WorkEnter | 3.5 | 211 | 原站立 → 工作锚点 |
| WorkLoop | 2.0 | 121 | 工作锚点 → 同一锚点 |
| WorkToBusy | 1.5 | 91 | 工作锚点 → 忙碌锚点 |
| BusyLoop | 2.0 | 121 | 忙碌锚点 → 同一锚点 |
| WorkExit | 2.0 | 121 | 工作锚点 → 原站立 |
| BusyExit | 2.0 | 121 | 忙碌锚点 → 原站立 |

按从后到前绘制：

0. 忙碌时 `SceneProps/Work/Fire/frame-NNNN.png`（121 帧 / 2 秒，单独连续时钟）
1. `SceneProps/Work/desk-back.png`
2. 当前角色帧
3. `SceneProps/Work/desk-front.png`
4. `SceneProps/Work/laptop.png`

两张桌子图层使用完全相同的平移。桌面后方在鹰的后面，前沿和桌腿在鹰前面，
因此翅膀可以搁在桌面上而身体不会盖住桌子的前沿。电脑始终独立：不会交给
光流模型重画、扭曲或复制。具体边界与入退场时段见 `SceneProps/Work/layout.json`。

入场按当前 `WorkStageMotion.cs`：0.15–1.0 秒桌子从左边滑入；0.9–1.8 秒电脑落下
并经过两次小幅缓冲；鹰抬眼、受惊、松气、抬起双翼，随后进入敲键盘循环。
退出时 0.35–1.15 秒抬走电脑，0.9–1.95 秒向左滑走桌子，同时放下双翼。
工作循环和忙碌循环只有在自然完整边界上衔接后续片段。
火焰的生长锚点为画布 `(192, 334)`；转忙的 0.25–1.3 秒从这个底部向上长出，
忙碌退场的 0–0.85 秒缩回，
不能连同角色一起缩放。火焰在三个忙碌阶段之间使用连续时钟，不每次重新从第一帧开始。

## 图像生成提示词集

生成方式：内置 ImageGen；未调用付费 CLI/API。以下是本批可复用的生成规格。
共同参考为原角色站立 PNG，以及可选本地素材 `References/Memes/工作.gif`、`References/Memes/忙碌中.gif`（路径相对仓库根目录；原 GIF 不随公开仓分发）。
站立 PNG 约束身份和比例；GIF 只约束表演，不复制它们的半身裁剪或蓝色背景。

### work-entry-sheet-v1.png（15 姿势，5 × 3）

Use case: identity-preserve. Asset type: 15-keyframe character sprite sheet for
an existing Windows desktop pet; not a concept redesign. Image 1 is the exact
canonical eagle identity/proportion/color/outline reference; images 2 and 3
are acting and expression references only. Exactly 15 full-body drawings,
5 columns × 3 rows, read left-to-right and top-to-bottom. Genuine transparent
background, no text, grid, numbers, shadows, or props. Preserve the huge white
helmet-shaped head, jagged lower feathers, small plump brown body, round brown
wings, two planted yellow feet, warm orange-brown fine outlines and subtle warm
color. No duck redesign, hairy crest, realistic feathers, extra arms, walking,
or cropping. Equal character size and planted foot baseline. Front view,
mischievous expressive office worker. Wings type at an imaginary keyboard at
lower-belly height. Do not draw desk, laptop, keyboard, or any object.

Sequence: neutral arms down; eyes glance left with raised brow; looks up
surprised with small O-shaped beak as computer falls; relieved smirk and arms
starting to raise; both wings forward ready to type; left presses and right
raises; right presses and left raises; left presses while blinking; right
presses with eyes open; left presses with smug half-closed eyes; right presses
with amused open smile; balanced ready typing pose; satisfied closed eyes and
wings lift from keyboard; wings halfway lower; neutral arms down. Keep head
stable, only small tilts and intentional facial/wing motion. Generous clear
gutters, clean anti-aliased alpha, no white halos.

### busy-sheet-v1.png（15 姿势，5 × 3）

Use case: identity-preserve. Exactly 15 coherent full-body animation key poses
in a 5 columns × 3 rows sheet. Image 1 is exact eagle identity; Image 2 is the
stressed-busy acting reference. Preserve huge white jagged helmet head, tiny
plump brown body, two round wing hands and planted yellow feet. Both wings
bent forward in front of the lower belly at the same imaginary keyboard level
in every cell. Alternate one wing tapping down and the other lifted about
10 px. No dangling arm, overhead wings, flying, props, motion lines, detached
sweat, text, or background flames. Full body, front view, fixed root and scale.

Row 1: relaxed working; neutral concentration; impatient eyebrows; fierce
half-lids; stressed narrowed golden eyes with tiny smug smile and balanced
typing hands (busy anchor). Row 2: left tap; right tap; left tap with head
leaning 3 px forward; right tap with clenched beak; both wings poised. Row 3:
left tap with squint; right tap with determined brows; both wings lift 10 px
with a tight blink; wings descend and eyes reopen; same busy anchor as cell 5.
Small rhythmic changes of one continuous action, no whole-body bobbing.

### work-loop-sheet-v1.png（12 姿势，4 × 3）

Use case: identity-preserve. Create 12 coherent happy-work typing key poses,
4 columns × 3 rows. Image 1 exact identity, image 2 geometry guide for two
forward bent wing hands, image 3 happy working acting reference. Same huge
white-headed brown eagle and planted feet in every cell. Both wing hands
remain forward at lower-belly keyboard level, alternating taps by about 10 px.
No dangling or overhead wing. Sequence: balanced ready typing with downward
gaze and smug closed-beak smile; left presses; right presses; left presses and
happy blink; right presses eyes reopen; both poised amused eyebrow; left
presses content smile; right presses slightly open beak; left presses blink;
right presses smile; both poised looking down; same initial typing anchor.
Only tiny natural head/breathing changes, fixed root and feet. No props,
accessories, shadow, text, numbering, watermark or grid. Genuine transparent
background, generous gutters, same warm fine outlines and subtle shading.

### work-props-sheet-v1.png（独立桌子 + 电脑）

Use case: stylized-concept. Two separate production-ready desktop-pet raster
prop assets on one transparent square canvas: desk in upper half, laptop in
lower half, empty transparent gutter. No eagle, hands or character. Warm pale
yellow wooden office desk, front view with slight top-down view of a thin
surface; wide rounded rectangular top, amber outline, two short honey-brown
legs near far sides and clear open middle. Width about three times total
height. Open silver-gray laptop seen from the back of its screen, front-on
symmetrical perspective; rounded screen-back rectangle and small blank round
logo, a narrow base below, no text or tiny details. Laptop width about half
desk width. Match the eagle's warm 2D cartoon shading and line style. Draw as
separate isolated objects, never overlapping; genuine transparency, no
painted checkerboard, ground shadow, labels, arrows or watermark.

### busy-fire-sheet-v1.png（4 姿势，2 × 2）

Use case: stylized-concept. Four authored key poses of a cartoon red/yellow
busy-work flame aura in a 2 columns × 2 rows sprite sheet. GIF is the flame
style reference and the neutral eagle only gives proportions. Draw no eagle,
character, laptop, desk or text. Four isolated connected silhouettes, equal
size and baseline: broad U-shaped red aura with a thin bright yellow rim,
connected low base and pointed tongues on both sides and above center. Large
transparent hollow center, about 60% width and 70% height, for the eagle.
Sequence: left tongue stretches and right bends; center peak stretches and
left curls lower; right tongue stretches and left lowers; center lowers and
left stretches back toward the initial frame. Local flame bending/stretching,
not whole-effect translation/rotation. Four coherent poses of one flowing
two-second loop. Genuine transparency, generous gutters, no painted checker,
black or blue background, detached sparks, smoke, shadow, gradient or neon glow.

火焰源图实际返回了 RGB 棋盘底，处理脚本按明确的红/黄色域去掉外部及中空区域的
棋盘像素，然后检查深色背景边缘。没有把白色棋盘残留作为透明纹理交付。

## 验证

每个序列的 `AnimationPreviews/{Clip}-qa.json` 记录固定脚点、深色背景边缘、
端点像素一致性、帧数与逐帧唯一性。帧间位置由原始姿势的光流生成；并非把姿势
重复到 60 帧，也不使用整图淡入/翻转。动画实际呈现帧率仍取决于运行机器和渲染负载。

`preview_work_scene.py` 额外验证六段之间的 10 个端点连接，输出整套道具进出场的
预览。`work_asset_acceleration.py` 可选使用仅安装在 `.tool-cache` 下的 SciPy
加快离线连通区域识别；启用前自动运行 14 个逐像素等价测试。它不是 EXE 依赖，
未启用时仍使用原来的四邻域算法，不改变抠图/衔接验证标准。
