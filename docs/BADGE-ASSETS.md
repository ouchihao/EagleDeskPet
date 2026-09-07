# 荣誉墙徽章资源 v1

生成日期：2026-09-06。用于大头鹰桌面宠物 v1.7 的荣誉墙。

## 交付文件与使用方式

| 等级 | 应用内路径 | PNG 尺寸 / 色彩 | SHA-256 |
| --- | --- | --- | --- |
| 铜 | `DuckDeskPet/Assets/Badges/bronze-v1.png` | 1254 × 1254 / RGB | `6a9428e0c32c8b3d904b53af909b1d17f9653c1c019d01a714645e1dfc2149e7` |
| 银 | `DuckDeskPet/Assets/Badges/silver-v1.png` | 1254 × 1254 / RGB | `f9cec921273999be1875b7652bb2b856262d726ade65522ec9de142bbc0da977` |
| 金 | `DuckDeskPet/Assets/Badges/gold-v1.png` | 1254 × 1254 / RGB | `c7f4195850cf4e92c98fe814d0aab639647a8e611722d0edcbbd6352da1d1df2` |

三张均是独立生成并保存的珐琅质感徽章：相同大头鹰头像、锯齿状白色羽毛、黄色喙、灰眉、腮红、月桂和飘带；金属颜色区分等级。角色身份参考 `DuckDeskPet/Assets/mascot-animated-neutral.png`。荣誉名称、进度、类别小图标与解锁状态由 UI 绘制，不烘焙进图片，三个等级底章可重复使用。

**这些 PNG 是不透明奶油色背景，不是透明图。** 内置生成工具在三次真 alpha 请求中均返回了 RGB 棋盘格背景。随后仍使用内置图片编辑工具替换为纯奶油色背景；最终交付不再含棋盘格。请求色为 `#FFF9EF`，实际角落像素在约 `#FDF7EB` 附近有少量生成色差，并非精确纯色。建议在荣誉卡片上以有意设计的圆角方形奶油色小画框呈现，不要声称可以无缝叠在任意深色背景上。建议实际展示 96–120 DIP，`Stretch=Uniform`，必要时用 UI 裁切圆角；不要使用色键抠掉白羽毛或银色高光。

## 工具与保留策略

- 使用内置 `image_gen.imagegen`；没有调用 API/CLI，没有读取 API Key。
- 首次铜章使用宠物 PNG 作身份参考；银章和金章分别单独编辑铜章，锁定构图和角色一致性。
- 三个背景修正分别独立调用内置图片编辑；没有手绘 SVG 替代，没有 Python 像素修改，没有自动抠图假装 alpha。
- 最终 PNG 从工具输出目录原样复制进工程；默认输出与原角色资源保留，未覆盖旧版本资源。
- 视觉检查：三档金属易区分，头像身份和徽章轮廓一致，无文字和棋盘格。文件检查确认三个 PNG 均为 RGB、同尺寸且可由 Pillow 解码。由于没有 alpha，深色页面应保留上面说明的奶油色画框。

以下保留工具原始输出文件名用于追溯；本机生成缓存目录不随公开仓分发。可用的最终资源位于上表所列项目路径。

| 阶段 | 输出文件 |
| --- | --- |
| 铜章首次生成（非交付；棋盘格背景） | `exec-6fc88094-9e61-4f87-adea-5201d5ebcdfa.png` |
| 银章变体（非交付；棋盘格背景） | `exec-18463ac1-0ab2-476a-b586-0804e6062733.png` |
| 金章变体（非交付；棋盘格背景） | `exec-403bc421-50f4-4b3c-8b96-1113605483f4.png` |
| 铜章最终交付 | `exec-d74161a6-6dd8-41c6-bb35-14a64d959a14.png` |
| 银章最终交付 | `exec-0863493b-0566-46b6-bf29-2e9becbe9266.png` |
| 金章最终交付 | `exec-8a5761ee-82c2-42ab-b099-d575e8b7f72d.png` |

## 完整提示词组

### 1. 铜章（参考：现有站立大头鹰）

```text
Use case: stylized-concept. Asset type: transparent desktop pet honor-wall game badge, bronze tier, one square PNG. Input image 1 is the eagle mascot identity reference, NOT a background. Create ONE polished warm bronze enamel achievement medal featuring a large recognizable portrait of this exact goofy cartoon eagle: white rounded head with a jagged three-point feather hem, yellow rounded beak, black oval eyes, grey arched eyebrows, subtle pink cheeks, warm caramel outline; give it a friendly cheeky slightly smug look, NOT an aggressive realistic bald eagle. Keep its head proportions and identity. Head only, no full body. Design family: round softly beveled copper-bronze medal, caramel enamel recess, short restrained side laurel branches and two small folded bronze ribbon ends below. Broad simple forms remain readable at 96px. Face occupies about half the whole badge width. Centered frontal symmetrical medal, entire silhouette visible, approx 10 percent transparent margin on all four sides. Soft polished game UI raster illustration with crisp clean alpha antialiasing and restrained highlights, no external drop shadow. Copper-bronze metal should unambiguously read as bronze not yellow gold. Background must be truly transparent alpha, not white, not black, and not a painted checkerboard. No words, letters, digits, watermark, extra character, trophy, unrelated logos or rectangular panel. Return only the completed single badge image.
```

### 2. 银章（编辑目标：首次铜章）

```text
Use case: precise-object-edit. Asset type: silver tier badge in a desktop pet honor wall. Input image 1 is the bronze badge edit target. Change only the medal metal and recess materials to cool polished SILVER (steel rim, pale grey-blue steel recess, silver laurel leaves and ribbon ends). Keep the identical eagle face, white jagged feather silhouette, yellow beak, pink cheeks, cheeky expression, original complete medal geometry, composition, scale and lighting unchanged. Output one square badge, no other assets. REMOVE the checkerboard background completely: output a genuine transparent PNG alpha channel around the entire medal silhouette, including gaps between ribbon and round medal. Transparency means fully transparent pixels, not a rendered checkerboard, not a white or black backdrop. No outer drop shadow, no text, letters, numbers or watermark. This is a functional app asset that must sit cleanly on both dark and cream backgrounds.
```

### 3. 金章（编辑目标：首次铜章）

```text
Use case: precise-object-edit. Asset type: gold tier badge in a desktop pet honor wall. Input image 1 is the bronze badge edit target. Change only the medal metal and recess materials to bright warm GOLD (lemon-gold rim, warm golden ochre recess, gold laurel leaves and ribbon ends). Keep the identical eagle face, white jagged feather silhouette, yellow beak, pink cheeks, cheeky expression, original complete medal geometry, composition, scale and lighting unchanged. Output one square badge, no other assets. Carefully REMOVE the checkered background completely: output a genuine transparent PNG alpha channel around the entire medal silhouette, including gaps between ribbon and round medal. Transparency means fully transparent pixels, never a rendered checkerboard, never a white or black backdrop. If your image renderer cannot encode transparent alpha, use a perfectly uniform flat cream background exact RGB(255,248,235), no texture and no shadow instead of a checkerboard. No outer drop shadow, no text, letters, numbers or watermark. This is a functional app asset that must display cleanly at 96px. Keep gold clearly more yellow than the copper-orange bronze reference.
```

### 4–6. 最终背景修正（每个等级独立调用）

下列模板中的 `{tier}` 分别替换为 `bronze`、`silver`、`gold`；编辑目标分别为上述三个对应的首次输出。

```text
Use case: precise-object-edit. Input image 1 is the edit target. Replace ONLY the entire checkerboard backdrop behind this {tier} eagle medal with a completely plain solid uniform pale cream background, exact RGB(255,249,239), HEX #FFF9EF. The entire square canvas outside the medal must be ONE single opaque cream color: no squares, no checks, no pattern, no noise, no gradients, no vignette and no external shadow. Keep the medal exactly unchanged: the same eagle face, proportions, eye shape, colors, white feathers, yellow beak, pink cheeks, {tier} metal, laurel leaves, ribbon and highlights, same centered placement and full silhouette. One single square badge asset. No text. This is a background replacement, not a redesign. OPAQUE FLAT CREAM BACKGROUND.
```
