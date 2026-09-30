# 摸鱼素材

`nose-original.gif`、`buddha-original.gif` 来自用户提供的“抠鼻.gif”和“阿弥陀佛.gif”，保留原文件，不更改用户源文件。对应 `nose/`、`buddha/` 目录是 FFmpeg 解码的原始 250×250 帧，各 35 帧、40 ms/帧；没有生成或声称存在 60 张/秒原画。

运行时 `LoafingAssets` 去除蓝底、字幕和独立省略号。祈祷动画的光晕已烘焙在背景中，因此使用第 0 帧的固定角色轮廓去掉光晕，眼睛/袈裟保持原帧。角色内部为不透明，只有轮廓边缘计算去蓝底透明度，避免闭眼时露出旧瞳孔影子。源文件不被覆盖。

原 GIF 首尾不能直接连播。`LoafingVisualTimeline.SourceFrame` 使用 `0…34…1` 往返索引，仍每帧保持 40 ms，一轮 2.72 秒。WPF / Live2D 以 60 Hz 目标渲染过渡及道具；源角色仍是原生 25 fps，不用重复帧冒充额外原画。

## 新木鱼道具

`props-atlas.png` 通过内置 ImageGen 根据原表情配色生成，透明底，左边木鱼、右边木槌。生成时间：2026-09-30。仅增加道具，未用 AI 重画角色。运行时按横向 62% 的空白分隔区分别取得 alpha 包围盒，不按正中切割木鱼。

生成提示：

> Use case: stylized-concept. Asset type: transparent prop sprite atlas for a small 2D desktop eagle pet. The attached image is ONLY a palette/line-style reference, NOT an edit target. Create TWO SEPARATE OBJECTS on a genuinely transparent RGBA background, with wide clear space between them: LEFT HALF a single small traditional Chinese wooden fish percussion block (muyu), rounded squat honey-brown wood body, flat stable bottom, one readable curved carved slit, gently cartoonish; RIGHT HALF one matching simple wooden beater mallet, round light tan head and slim warm-brown handle, vertical orientation with head at top. Front / very slight top-down view suitable for sitting on the eagle's desk. Match the supplied flat sticker aesthetic: warm tan outlines, restrained two-tone shading, clear silhouette at 24-40 pixel display size. Do NOT draw the eagle, hands, table, robe, blue background, labels, text, glow, floating marks, grid or frame. Each object fully contained in its own half with at least 10% margin and no overlaps. The wooden fish and mallet must be separate for code-driven animation. No photorealistic texture, no 3D rendering, no cast shadow outside the object. Preserve real transparency; no checkerboard baked into pixels.

GIF 是半身画面，复用当前装备桌子遮挡下半身；不把半身贴图直接悬浮在桌面外。这两个特殊动作使用原始角色造型，不随衣服变装，结束后恢复原装备。木槌围绕手边支点运动；木鱼放在桌面、落槌时轻微挤压。

源表情包由用户提供；本记录仅描述来源，不代替第三方角色素材的分发授权。
