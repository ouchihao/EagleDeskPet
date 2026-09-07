# 荣誉纪念币 v2 · 资源与生成提示词

日期：2026-09-06。使用内置 imagegen 工具，共 9 次独立生成/编辑调用；没有使用 CLI、API Key、脚本重绘或手工栅格抠图。旧版 `*-v1.png` 保留。

## 设计

三种动作各有铜、银、金三种金属材质，所有头部、喙、道具都属于统一金属浮雕，不再用彩色珐琅鹰头贴在金属圈上。

- 干饭：大头鹰抱饭碗、举起饭勺，饭粒和蒸汽一体雕刻。
- 摸头：大头鹰闭眼侧头享受摸头，翅膀合拢，心形与脸颊线纹同样为金属浮雕。
- 成长：全身大头鹰举星标奖杯、指向上方，脚下三阶台阶。

UI 使用 166 DIP 圆形展位，174 DIP 图片微量超扫描与原生圆形裁切。工具部分输出为带纯色背景的 RGB，不能把这些文件称作透明图片。圆形裁切去掉外部背景，原始 PNG 像素未被程序修改。金色成长币由工具直接输出 RGBA，保留原始 alpha。

金属扫光每 7.2 秒只选一枚已获得且在视口中的纪念币，持续 1.15 秒；悬停抬升 2.5 DIP、放大 2.5%，无旋转或翻牌。仅荣誉窗口在前台、可见且系统允许客户端动画时运行；未解锁、滚出视口、隐藏、最小化、失焦、关闭窗口或系统关闭动效时移除时钟。没有订阅宠物的逐帧渲染事件，不更改养成阈值或存档。

## 文件映射

| 系列 | 等级 | 项目文件 | 内置工具原始输出 |
| --- | --- | --- | --- |
| meals | bronze | `DuckDeskPet/Assets/Badges/meals-bronze-v2.png` | `exec-2e754f87-5472-4979-aa35-4e68f384acd5.png` |
| meals | silver | `DuckDeskPet/Assets/Badges/meals-silver-v2.png` | `exec-3caaae8a-8f0b-45b5-a837-1255f3a6bf74.png` |
| meals | gold | `DuckDeskPet/Assets/Badges/meals-gold-v2.png` | `exec-67bbf25a-4709-4faf-9689-60ad35e8a6b1.png` |
| affection | bronze | `DuckDeskPet/Assets/Badges/affection-bronze-v2.png` | `exec-20a33794-3a4e-4e9f-94a2-5ae782919f18.png` |
| affection | silver | `DuckDeskPet/Assets/Badges/affection-silver-v2.png` | `exec-096238ea-12fa-47c6-ade2-e1e09bafefbd.png` |
| affection | gold | `DuckDeskPet/Assets/Badges/affection-gold-v2.png` | `exec-236847fa-f10e-48c7-8128-61c26fc7bd35.png` |
| growth | bronze | `DuckDeskPet/Assets/Badges/growth-bronze-v2.png` | `exec-827b46a5-607e-4392-a543-1ebd7d0d8a6f.png` |
| growth | silver | `DuckDeskPet/Assets/Badges/growth-silver-v2.png` | `exec-10b6c817-d2a4-4569-ad1a-8ec089fe911f.png` |
| growth | gold | `DuckDeskPet/Assets/Badges/growth-gold-v2.png` | `exec-418ee481-47de-44d6-83ed-76f96768263b.png` |

## 完整性与验证

9 个最终文件均为 1254 × 1254，8 张 RGB、1 张 RGBA（growth-gold）。以下为 SHA-256；项目中复制的 PNG 与工具原始文件逐字节相同。

| 文件 | SHA-256 |
| --- | --- |
| `affection-bronze-v2.png` | `be7f0dbc13443e4760761e0978d2e688a351c458225f711befd9cbe82e900171` |
| `affection-gold-v2.png` | `9ad36f4c8ebdc9b900b2909e007265363495fc89f4403803755bd2d7db769e05` |
| `affection-silver-v2.png` | `3b1f596930be082487e5d32ecb9e56adb2d565815d8c59b5569474249845bf4e` |
| `growth-bronze-v2.png` | `a9cf61631e7852b2cadb812ff065623a7c5d46d13919d43bd487cd803adc4afa` |
| `growth-gold-v2.png` | `5cbbfc5d458bbe6a0d7df93fc273df874485f5053e45f6e1aba5321b2d6e5246` |
| `growth-silver-v2.png` | `88da6fa00e016ee3279770afd56f8988b094a6dc71861a695dea4bc893396b21` |
| `meals-bronze-v2.png` | `3f7ec440c45d5c4b51a29fe5d50fbf7356f19722adf21045d7dd99d01b7481ba` |
| `meals-gold-v2.png` | `13ec73ba3118c0820e4820d68cc6a8664087c32259a810d215a33d57b8f09341` |
| `meals-silver-v2.png` | `f8c066f844a39a375e5699c0693556b42324f8f93b950e2dceda992a587654c4` |

离屏 WPF UI 自测：25 项通过，覆盖九张真实资源解码、九个独立图片 URI、原生圆形裁切、移除贴片标志、3/2/1 列布局、过滤与进度、隐藏时禁用动效、悬停时钟启动、Stop 完全移除时钟并复位。报告：`.codex-build/honor-ui-v1.8-smoke/honor-ui-report.json`；本人检查渲染图 `honor-all-earned.png` 与 `honor-one-column.png`，确认金属纹理与三个动作清晰、没有背景方块。此测试不读取真实养成存档、不截取用户桌面，也不宣称已认证多显示器交互或真实 60 Hz 显示效果。

## 完整提示词

银色干饭币使用用户最初表情包作为角色参考，v1 银徽章作为待改进的负面设计上下文。其余两个银币以干饭银币为一致性母版；六个材质版本分别使用该系列的银币作为编辑目标。

### meals-bronze-v2.png

```text
Use case: precise-object-edit. Create the BRONZE material tier of this same collectible achievement coin.
Image 1 is the exact edit target. Change ONLY the metal material color from silver to ANTIQUE BRONZE throughout: deep warm copper-brown recesses, medium burnished bronze field, restrained reddish golden bronze highlights. Strong polished relief definition, no green patina.
CRITICAL INVARIANTS: exactly preserve the eagle character face, expression, pose, hands, all action props, every relief shape, the coin rim/beading geometry, centered straight-on square framing, small equal exterior margins and original studio light direction. Do not add, remove, redraw, or move any object. Entire eagle head, beak, eyes, props, relief field and border must ALL be the SAME BRONZE metal, with tonal variations only from sculptural lighting; NO cartoon/enamel colors, no white face, no yellow painted beak, no red cheeks, no colored icon. This must look like an engraved/embossed bronze coin, not colored artwork pasted onto metal. Maintain clear high contrast brushed texture and sophisticated dimensional bas relief. No text/numbers/logos/watermark, no ribbon tails, exactly one circular coin.
Preserve clean light background outside coin; if genuine transparent alpha is available use it, but absolutely never paint a transparency checkerboard.
```

### meals-silver-v2.png

```text
Use case: stylized-concept
Asset type: one premium desktop-pet achievement coin, meals series, SILVER tier.
Primary request: Redesign the eagle achievement as a beautifully sculpted solid silver bas-relief coin. This is a COIN SCULPTURE, not a colored character sticker inside a ring.
Input image 1 is identity reference only: the big-headed comic eagle has an elongated rounded head and jagged lower head feathers, a wide broad beak, expressive eyebrows and a tiny body. Do not reproduce sticker sheet or lettering. Image 2 is negative/problem context: improve it, do NOT preserve its enamel colors, ribbon tails, or isolated-head-only design.
Subject: same recognisable cheeky big-headed eagle in a delighted eating pose. Its two little feathered arms hug a substantial overflowing rice bowl beneath its beak; one wing lifts a short spoon with rice towards open smiling beak. Rice grains and two gentle steam curls are clear silver relief details. All action is actually engraved/embossed into the coin, not an extra icon pasted outside.
Style: collectible numismatic medal, high-quality sculpted low relief, uniformly SILVER metal from head, beak, eyes, rice and bowl to field and rim. Dark recesses and polished edge highlights create depth. Brushed satin field, restrained fine radial machining, crisp raised double circular rim with tiny beading. No colored enamel, no black cartoon outlines, no white paint, no yellow beak, no red cheeks. The silly eagle likeness and eating action must remain legible at 160px.
Composition: exactly one perfectly circular flat-front coin, orthographic straight-on camera, centered, equal 3% padding, full rim visible, no tilt, no ribbon tails, no external objects, no pedestal. Square image. Large integrated character and bowl fill central 75%.
Lighting: subtle studio key from upper left, soft bright silver bevels and graphite silver recesses; medium-contrast expensive metal not white plastic.
Background: genuinely transparent RGBA background outside circular rim, no cast shadow outside coin, absolutely no checkerboard texture. If actual alpha cannot be emitted, use a perfectly flat single color #F8F4EB outside the rim; never fake transparency by painting checkerboards.
Constraints: no lettering, numbers, watermark or logos. Exactly one silver coin, not a grid or three coin variants.
```

### meals-gold-v2.png

```text
Use case: precise-object-edit. Create the GOLD material tier of this same collectible achievement coin.
Image 1 is the exact edit target. Change ONLY the metal material color from silver to RICH GOLD throughout: deep warm honey-gold recesses, medium champagne gold field, polished yellow-gold bevel highlights. Warm but not orange.
CRITICAL INVARIANTS: exactly preserve the eagle character face, expression, pose, hands, all action props, every relief shape, the coin rim/beading geometry, centered straight-on square framing, small equal exterior margins and original studio light direction. Do not add, remove, redraw, or move any object. Entire eagle head, beak, eyes, props, relief field and border must ALL be the SAME GOLD metal, with tonal variations only from sculptural lighting; NO cartoon/enamel colors, no white face, no yellow painted beak, no red cheeks, no colored icon. This must look like an engraved/embossed gold coin, not colored artwork pasted onto metal. Maintain clear high contrast brushed texture and sophisticated dimensional bas relief. No text/numbers/logos/watermark, no ribbon tails, exactly one circular coin.
Preserve clean light background outside coin; if genuine transparent alpha is available use it, but absolutely never paint a transparency checkerboard.
```

### affection-bronze-v2.png

```text
Use case: precise-object-edit. Create the BRONZE material tier of this same collectible achievement coin.
Image 1 is the exact edit target. Change ONLY the metal material color from silver to ANTIQUE BRONZE throughout: deep warm copper-brown recesses, medium burnished bronze field, restrained reddish golden bronze highlights. Strong polished relief definition, no green patina.
CRITICAL INVARIANTS: exactly preserve the eagle character face, expression, pose, hands, all action props, every relief shape, the coin rim/beading geometry, centered straight-on square framing, small equal exterior margins and original studio light direction. Do not add, remove, redraw, or move any object. Entire eagle head, beak, eyes, props, relief field and border must ALL be the SAME BRONZE metal, with tonal variations only from sculptural lighting; NO cartoon/enamel colors, no white face, no yellow painted beak, no red cheeks, no colored icon. This must look like an engraved/embossed bronze coin, not colored artwork pasted onto metal. Maintain clear high contrast brushed texture and sophisticated dimensional bas relief. No text/numbers/logos/watermark, no ribbon tails, exactly one circular coin.
Preserve clean light background outside coin; if genuine transparent alpha is available use it, but absolutely never paint a transparency checkerboard.
```

### affection-silver-v2.png

```text
Use case: precise-object-edit. Asset: one SILVER collectible desktop-pet achievement coin.
Image 1 is the edit target / collection design master. Keep EXACT same round coin shape, border beading, sculpted finish, 1:1 square framing, straight-on camera, metal lighting, and consistent eagle's tall rounded head, large broad beak and jagged lower head feathers. Change only the CENTRAL ENGRAVED ACTION according to this request. Keep the entire coin solid monochrome SILVER, including eagle's head, beak, eyes and all props. No paint/enamel/color, no yellow beak, no white painted face, no red cheeks. This is a numismatic sculpture, not a sticker.
Keep fully integrated low-relief sculpted subject, textured silver satin field, polished highlights and dark silver recesses, full circular rim just inside image frame with minimal equal margin; no ribbon tails; no lettering/numbers/watermarks; no external objects.
Background: genuinely transparent alpha outside the coin. If alpha impossible use completely uniform #F8F4EB background outside rim, never draw checkerboard. Exactly ONE coin; no grid. Readable at 160px.
NEW ENGRAVED ACTION: same playful big-headed eagle is receiving a gentle HEAD PAT from a single hand reaching in from upper right INSIDE the coin. Eagle leans head sideways into pat, eyes shyly closed with a smug tiny smile, eyebrows mischievous, little wings clasped together under chin. Two tiny heart shapes are EMBOSSED SILVER behind eagle's shoulder. Engrave cheek hatching instead of colored blush. Show short upper body. Completely remove rice bowl, spoon, grains, eating mouth and steam from original master. Make the hand touching top of eagle's head immediately obvious.
```

### affection-gold-v2.png

```text
Use case: precise-object-edit. Create the GOLD material tier of this same collectible achievement coin.
Image 1 is the exact edit target. Change ONLY the metal material color from silver to RICH GOLD throughout: deep warm honey-gold recesses, medium champagne gold field, polished yellow-gold bevel highlights. Warm but not orange.
CRITICAL INVARIANTS: exactly preserve the eagle character face, expression, pose, hands, all action props, every relief shape, the coin rim/beading geometry, centered straight-on square framing, small equal exterior margins and original studio light direction. Do not add, remove, redraw, or move any object. Entire eagle head, beak, eyes, props, relief field and border must ALL be the SAME GOLD metal, with tonal variations only from sculptural lighting; NO cartoon/enamel colors, no white face, no yellow painted beak, no red cheeks, no colored icon. This must look like an engraved/embossed gold coin, not colored artwork pasted onto metal. Maintain clear high contrast brushed texture and sophisticated dimensional bas relief. No text/numbers/logos/watermark, no ribbon tails, exactly one circular coin.
Preserve clean light background outside coin; if genuine transparent alpha is available use it, but absolutely never paint a transparency checkerboard.
```

### growth-bronze-v2.png

```text
Use case: precise-object-edit. Create the BRONZE material tier of this same collectible achievement coin.
Image 1 is the exact edit target. Change ONLY the metal material color from silver to ANTIQUE BRONZE throughout: deep warm copper-brown recesses, medium burnished bronze field, restrained reddish golden bronze highlights. Strong polished relief definition, no green patina.
CRITICAL INVARIANTS: exactly preserve the eagle character face, expression, pose, hands, all action props, every relief shape, the coin rim/beading geometry, centered straight-on square framing, small equal exterior margins and original studio light direction. Do not add, remove, redraw, or move any object. Entire eagle head, beak, eyes, props, relief field and border must ALL be the SAME BRONZE metal, with tonal variations only from sculptural lighting; NO cartoon/enamel colors, no white face, no yellow painted beak, no red cheeks, no colored icon. This must look like an engraved/embossed bronze coin, not colored artwork pasted onto metal. Maintain clear high contrast brushed texture and sophisticated dimensional bas relief. No text/numbers/logos/watermark, no ribbon tails, exactly one circular coin.
Preserve clean light background outside coin; if genuine transparent alpha is available use it, but absolutely never paint a transparency checkerboard.
```

### growth-silver-v2.png

```text
Use case: precise-object-edit. Asset: one SILVER collectible desktop-pet achievement coin.
Image 1 is the edit target / collection design master. Keep EXACT same round coin shape, border beading, sculpted finish, 1:1 square framing, straight-on camera, metal lighting, and consistent eagle's tall rounded head, large broad beak and jagged lower head feathers. Change only the CENTRAL ENGRAVED ACTION according to this request. Keep the entire coin solid monochrome SILVER, including eagle's head, beak, eyes and all props. No paint/enamel/color, no yellow beak, no white painted face, no red cheeks. This is a numismatic sculpture, not a sticker.
Keep fully integrated low-relief sculpted subject, textured silver satin field, polished highlights and dark silver recesses, full circular rim just inside image frame with minimal equal margin; no ribbon tails; no lettering/numbers/watermarks; no external objects.
Background: genuinely transparent alpha outside the coin. If alpha impossible use completely uniform #F8F4EB background outside rim, never draw checkerboard. Exactly ONE coin; no grid. Readable at 160px.
NEW ENGRAVED ACTION: same cheeky big-headed eagle proudly holds a small handled TROPHY engraved with a STAR in one little wing at chest height, the other wing points cheerfully upward. Tiny body stands on THREE ASCENDING STEPS sculpted at lower part of coin. Big head, proudly narrowed happy eyes and broad silly smile. Show the growth/success scene integrated into relief; trophy and steps as big readable forms. Completely remove rice bowl, spoon, grains and steam from original master. One trophy and one eagle only.
```

### growth-gold-v2.png

```text
Use case: precise-object-edit. Create the GOLD material tier of this same collectible achievement coin.
Image 1 is the exact edit target. Change ONLY the metal material color from silver to RICH GOLD throughout: deep warm honey-gold recesses, medium champagne gold field, polished yellow-gold bevel highlights. Warm but not orange.
CRITICAL INVARIANTS: exactly preserve the eagle character face, expression, pose, hands, all action props, every relief shape, the coin rim/beading geometry, centered straight-on square framing, small equal exterior margins and original studio light direction. Do not add, remove, redraw, or move any object. Entire eagle head, beak, eyes, props, relief field and border must ALL be the SAME GOLD metal, with tonal variations only from sculptural lighting; NO cartoon/enamel colors, no white face, no yellow painted beak, no red cheeks, no colored icon. This must look like an engraved/embossed gold coin, not colored artwork pasted onto metal. Maintain clear high contrast brushed texture and sophisticated dimensional bas relief. No text/numbers/logos/watermark, no ribbon tails, exactly one circular coin.
Preserve clean light background outside coin; if genuine transparent alpha is available use it, but absolutely never paint a transparency checkerboard.
```
