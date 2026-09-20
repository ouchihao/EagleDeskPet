# v1.9 新动作生成记录

生成器：Codex 内置 imagegen；无外部付费 API 回退。生成源图保留 RGBA 原始 alpha，透明区 RGB 的棕色不是背景素材。构建用离线 RIFE 光流插帧，不是复制帧或整图淡化。

参考角色：`../mascot-animated-neutral.png`。用户提供的本地 GIF 只作表演参考，以 ffmpeg `fps=10,scale=240:240,tile=4x3` 查看表情变化；不将原 GIF 作为发布资源。

## 饥饿 / 喝茶 / 猜拳的生成要求摘要

以下为实际生成要求的整理，不声称是逐字请求记录。统一保持原角色白色大头、黄嘴、棕身、橙脚、暖棕描边、完整身体、前视和脚底定点；透明底，均匀网格，不带文字或原表情包的蓝底。

- `hungry-sheet-v1.png`：5×3 共 15 张连续关键姿势。中性 → 低头找饭 → 逐步取出空碗 → 举到胸前 → 倾斜展示空碗 → 眼含泪 → 哭/闭眼/期待 → 放下并收起碗 → 中性。拆为进场 1.5 秒、可重复循环 2 秒、退场 1.5 秒；场景桌子由运行时分层提供。
- `tea-sheet-v1.png`：5×3 共 15 张。中性 → 左右张望 → 从身体旁取绿茶杯 → 抬杯 → 小口喝茶、半眯眼得意 → 慢慢放下 → 收回 → 中性。完整 2 秒。
- `rps-sheet-v1.png`：5×5 共 25 张，每行一个 5 关键姿势序列，均中性起止。石头握拳；布展开四个羽尖；剪刀两个羽尖成 V；赢后扬翅得意；输后吃惊、捂脸、偷看、放手。演技参考本地“没眼看”和“咬牙切齿”，不加入参考中的手机、文字或背景。每行动作 2 秒。

## 烦躁动作原始请求

```text
Create a production-ready animation keyframe sprite sheet of the EXACT full-body cute big-headed eagle desktop pet in reference 1. Use references 2 and 3 ONLY for the cheeky expressive acting, not their blue backgrounds, lettering, phones or cropped framing. Raster art, clean transparent background, 1536x1024, EXACTLY 15 separate non-touching full-body eagles arranged on an even 5-column by 3-row grid, read row-major. Same scale, front view, both orange feet fixed at identical baseline in each cell, consistent huge white helmet-like eagle head with pointed feather lower edge, golden yellow broad beak, brown oval body and round brown wings, pink cheek marks, warm brown outlines and gently shaded sticker aesthetic matching reference 1 precisely. No text, numbers, floor, shadow blobs, detached symbols, border, watermark or extra objects. This is one continuous exaggerated annoyed-but-funny reaction to too many head pats. Key 1 neutral wings down; 2 eyes glance up suspiciously and one wing starts lifting; 3 brow dips and wing rises toward head; 4 wing shields crown with playful side-eye; 5 both wings begin drawing down into small fists; 6 fists gather near chest and beak pulls wider; 7 comically big clenched rounded teeth visible, tense eyebrows, body squashes a tiny bit but feet stay planted; 8 the full gritted-teeth cheeky peak, looking directly at viewer; 9 still gritting but cheeks puff and fists tighten; 10 exhales and clenched teeth shrink toward normal beak, eyes roll sideways; 11 shoulders relax, fist opens; 12 half-lidded dismissive look and wings lowering; 13 beak closed, eyebrows softening; 14 wings almost resting; 15 neutral matching key 1. Draw genuinely intermediate articulated poses, not scene dissolves. Keep the exact character identity, head size and planted feet, do not make a realistic bird or skinny duck.
```

## 道具及办公服原始请求

### mint-desk

参考：desk.png

```text
Edit this transparent raster desktop-pet desk into a second premium furniture skin. Preserve the EXACT frontal three-quarter perspective, silhouette, tabletop and leg geometry, position, canvas proportions, transparent background, and occupied bounding rectangle from reference: this must fit an existing animation without moving the pet. Change the warm yellow wood into a charming mint-green lacquer desk, softly shaded teal edges, pale cream inset top, tiny tasteful leaf-shaped brass drawer pull centered on front apron. Same warm outlined cartoon quality, not photorealistic. Keep the desk empty with absolutely no character, computer, plant, cup, floor, shadow beyond the object, labels or extra objects. The complete table including both feet must be visible. Output single desk with true alpha transparency, no grid, no checkerboard paint.
```

### midnight-computer

参考：laptop.png

```text
Edit this transparent raster laptop prop into a midnight-blue premium laptop for a cute desktop pet. Preserve EXACT rear-view geometry, silhouette, wide-open screen angle, tiny base, canvas proportions, position and occupied bounding rectangle. The pet stands behind and types: viewer sees only the back of laptop. Change silver shell to deep midnight blue with softly highlighted indigo bevels; replace the round plain rear emblem with a tiny subtle cyan crescent-moon emblem integrated in the case. Same rounded simple sticker/cartoon prop finish, not photorealism. True transparent background; no table, character, floor, surroundings, captions, cables or extra objects. One laptop only.
```

### office-neutral

参考：mascot-animated-neutral.png

```text
Edit the EXACT full-body eagle desktop pet in this reference by dressing its brown torso in a cute office uniform: muted navy-blue sleeveless waistcoat, small white shirt collar tucked below the jagged white head feather fringe, short warm coral-red tie centered, two tiny brass buttons near belly. Keep the brown wings completely uncovered and in the exact original resting pose over the sides of the waistcoat. Clothes must stop above the orange feet, no trousers, hat, glasses or shoes. Do NOT change the character's face, white huge head outline, eyes, golden yellow beak, blush, brown wing forms, brown body proportions, orange feet or foot placement. Preserve the exact image composition, same character scale, full-body, warm soft cartoon shading, and true alpha transparent background. The office clothing should be integrated to the original character with natural wing and feather occlusion, not a flat sticker floating in front. One neutral reference pose only; no sheet, text, other objects or background.
```

## 全动作办公服适配原始请求

参考 1 为 `OfficeReferences/<动作组>-reference.png`，参考 2 为 `office-neutral-v1.png`。共八组，重复中性帧及场景连接姿势通过 mapping.json 复用，避免循环边界换脸。

### yawn

```text
Edit reference 1, an existing full-body eagle animation KEYFRAME CONTACT SHEET. Output the SAME grid (5 columns and 3 rows, 15 cells), SAME row-major pose ordering, same separate full-body characters with generous transparent gutters. Reference 2 defines the ONLY new clothing: navy sleeveless waistcoat, white collar below the white head fringe, short coral-red tie, two small brass buttons, uncovered brown wings and orange feet. Dress EVERY eagle cell of reference 1 in this identical office uniform. This is an exact costume adaptation, NOT a redesign of the actions: preserve each cell's distinct eyes, eyebrows, mouth/beak shape, wing and hand articulation, head tilt, body twist, foot position, scale, and any held cup/bowl. Clothes follow the torso's squash and twist and lie BEHIND the white feather fringe, moving wings and held props. Do not copy the neutral pose onto expressive cells. Preserve even tiny differences between adjacent cells. Keep the normal eye pupils and also preserve the pupil-less golden angry eyes where shown in work cells; keep the closed-eye intermediate blinks as shown. Maintain the exact established huge white-headed brown eagle identity and warm clean cartoon shading, no realistic feathers. FULL BODY in every cell, feet never cropped. No extra limbs, no table/computer/fire/background scenery, no labels, numbers or text. True alpha transparent background; no painted black/checkerboard. For final padding cells that are neutral, keep them neutral but dressed. Output a clean high-resolution production sprite sheet.
```

### shy

```text
Edit reference 1, an existing full-body eagle animation KEYFRAME CONTACT SHEET. Output the SAME grid (5 columns and 3 rows, 15 cells), SAME row-major pose ordering, same separate full-body characters with generous transparent gutters. Reference 2 defines the ONLY new clothing: navy sleeveless waistcoat, white collar below the white head fringe, short coral-red tie, two small brass buttons, uncovered brown wings and orange feet. Dress EVERY eagle cell of reference 1 in this identical office uniform. This is an exact costume adaptation, NOT a redesign of the actions: preserve each cell's distinct eyes, eyebrows, mouth/beak shape, wing and hand articulation, head tilt, body twist, foot position, scale, and any held cup/bowl. Clothes follow the torso's squash and twist and lie BEHIND the white feather fringe, moving wings and held props. Do not copy the neutral pose onto expressive cells. Preserve even tiny differences between adjacent cells. Keep the normal eye pupils and also preserve the pupil-less golden angry eyes where shown in work cells; keep the closed-eye intermediate blinks as shown. Maintain the exact established huge white-headed brown eagle identity and warm clean cartoon shading, no realistic feathers. FULL BODY in every cell, feet never cropped. No extra limbs, no table/computer/fire/background scenery, no labels, numbers or text. True alpha transparent background; no painted black/checkerboard. For final padding cells that are neutral, keep them neutral but dressed. Output a clean high-resolution production sprite sheet.
```

### eat

```text
Edit reference 1, an existing full-body eagle animation KEYFRAME CONTACT SHEET. Output the SAME grid (5 columns and 3 rows, 15 cells), SAME row-major pose ordering, same separate full-body characters with generous transparent gutters. Reference 2 defines the ONLY new clothing: navy sleeveless waistcoat, white collar below the white head fringe, short coral-red tie, two small brass buttons, uncovered brown wings and orange feet. Dress EVERY eagle cell of reference 1 in this identical office uniform. This is an exact costume adaptation, NOT a redesign of the actions: preserve each cell's distinct eyes, eyebrows, mouth/beak shape, wing and hand articulation, head tilt, body twist, foot position, scale, and any held cup/bowl. Clothes follow the torso's squash and twist and lie BEHIND the white feather fringe, moving wings and held props. Do not copy the neutral pose onto expressive cells. Preserve even tiny differences between adjacent cells. Keep the normal eye pupils and also preserve the pupil-less golden angry eyes where shown in work cells; keep the closed-eye intermediate blinks as shown. Maintain the exact established huge white-headed brown eagle identity and warm clean cartoon shading, no realistic feathers. FULL BODY in every cell, feet never cropped. No extra limbs, no table/computer/fire/background scenery, no labels, numbers or text. True alpha transparent background; no painted black/checkerboard. For final padding cells that are neutral, keep them neutral but dressed. Output a clean high-resolution production sprite sheet.
```

### work

```text
Edit reference 1, an existing full-body eagle animation KEYFRAME CONTACT SHEET. Output the SAME grid (6 columns and 5 rows, 30 cells), SAME row-major pose ordering, same separate full-body characters with generous transparent gutters. Reference 2 defines the ONLY new clothing: navy sleeveless waistcoat, white collar below the white head fringe, short coral-red tie, two small brass buttons, uncovered brown wings and orange feet. Dress EVERY eagle cell of reference 1 in this identical office uniform. This is an exact costume adaptation, NOT a redesign of the actions: preserve each cell's distinct eyes, eyebrows, mouth/beak shape, wing and hand articulation, head tilt, body twist, foot position, scale, and any held cup/bowl. Clothes follow the torso's squash and twist and lie BEHIND the white feather fringe, moving wings and held props. Do not copy the neutral pose onto expressive cells. Preserve even tiny differences between adjacent cells. Keep the normal eye pupils and also preserve the pupil-less golden angry eyes where shown in work cells; keep the closed-eye intermediate blinks as shown. Maintain the exact established huge white-headed brown eagle identity and warm clean cartoon shading, no realistic feathers. FULL BODY in every cell, feet never cropped. No extra limbs, no table/computer/fire/background scenery, no labels, numbers or text. True alpha transparent background; no painted black/checkerboard. For final padding cells that are neutral, keep them neutral but dressed. Output a clean high-resolution production sprite sheet.
```

### hungry

```text
Edit reference 1, an existing full-body eagle animation KEYFRAME CONTACT SHEET. Output the SAME grid (5 columns and 3 rows, 15 cells), SAME row-major pose ordering, same separate full-body characters with generous transparent gutters. Reference 2 defines the ONLY new clothing: navy sleeveless waistcoat, white collar below the white head fringe, short coral-red tie, two small brass buttons, uncovered brown wings and orange feet. Dress EVERY eagle cell of reference 1 in this identical office uniform. This is an exact costume adaptation, NOT a redesign of the actions: preserve each cell's distinct eyes, eyebrows, mouth/beak shape, wing and hand articulation, head tilt, body twist, foot position, scale, and any held cup/bowl. Clothes follow the torso's squash and twist and lie BEHIND the white feather fringe, moving wings and held props. Do not copy the neutral pose onto expressive cells. Preserve even tiny differences between adjacent cells. Keep the normal eye pupils and also preserve the pupil-less golden angry eyes where shown in work cells; keep the closed-eye intermediate blinks as shown. Maintain the exact established huge white-headed brown eagle identity and warm clean cartoon shading, no realistic feathers. FULL BODY in every cell, feet never cropped. No extra limbs, no table/computer/fire/background scenery, no labels, numbers or text. True alpha transparent background; no painted black/checkerboard. For final padding cells that are neutral, keep them neutral but dressed. Output a clean high-resolution production sprite sheet.
```

### tea

```text
Edit reference 1, an existing full-body eagle animation KEYFRAME CONTACT SHEET. Output the SAME grid (5 columns and 3 rows, 15 cells), SAME row-major pose ordering, same separate full-body characters with generous transparent gutters. Reference 2 defines the ONLY new clothing: navy sleeveless waistcoat, white collar below the white head fringe, short coral-red tie, two small brass buttons, uncovered brown wings and orange feet. Dress EVERY eagle cell of reference 1 in this identical office uniform. This is an exact costume adaptation, NOT a redesign of the actions: preserve each cell's distinct eyes, eyebrows, mouth/beak shape, wing and hand articulation, head tilt, body twist, foot position, scale, and any held cup/bowl. Clothes follow the torso's squash and twist and lie BEHIND the white feather fringe, moving wings and held props. Do not copy the neutral pose onto expressive cells. Preserve even tiny differences between adjacent cells. Keep the normal eye pupils and also preserve the pupil-less golden angry eyes where shown in work cells; keep the closed-eye intermediate blinks as shown. Maintain the exact established huge white-headed brown eagle identity and warm clean cartoon shading, no realistic feathers. FULL BODY in every cell, feet never cropped. No extra limbs, no table/computer/fire/background scenery, no labels, numbers or text. True alpha transparent background; no painted black/checkerboard. For final padding cells that are neutral, keep them neutral but dressed. Output a clean high-resolution production sprite sheet.
```

### rps

```text
Edit reference 1, an existing full-body eagle animation KEYFRAME CONTACT SHEET. Output the SAME grid (5 columns and 4 rows, 20 cells), SAME row-major pose ordering, same separate full-body characters with generous transparent gutters. Reference 2 defines the ONLY new clothing: navy sleeveless waistcoat, white collar below the white head fringe, short coral-red tie, two small brass buttons, uncovered brown wings and orange feet. Dress EVERY eagle cell of reference 1 in this identical office uniform. This is an exact costume adaptation, NOT a redesign of the actions: preserve each cell's distinct eyes, eyebrows, mouth/beak shape, wing and hand articulation, head tilt, body twist, foot position, scale, and any held cup/bowl. Clothes follow the torso's squash and twist and lie BEHIND the white feather fringe, moving wings and held props. Do not copy the neutral pose onto expressive cells. Preserve even tiny differences between adjacent cells. Keep the normal eye pupils and also preserve the pupil-less golden angry eyes where shown in work cells; keep the closed-eye intermediate blinks as shown. Maintain the exact established huge white-headed brown eagle identity and warm clean cartoon shading, no realistic feathers. FULL BODY in every cell, feet never cropped. No extra limbs, no table/computer/fire/background scenery, no labels, numbers or text. True alpha transparent background; no painted black/checkerboard. For final padding cells that are neutral, keep them neutral but dressed. Output a clean high-resolution production sprite sheet.
```

### annoyed

```text
Edit reference 1, an existing full-body eagle animation KEYFRAME CONTACT SHEET. Output the SAME grid (5 columns and 3 rows, 15 cells), SAME row-major pose ordering, same separate full-body characters with generous transparent gutters. Reference 2 defines the ONLY new clothing: navy sleeveless waistcoat, white collar below the white head fringe, short coral-red tie, two small brass buttons, uncovered brown wings and orange feet. Dress EVERY eagle cell of reference 1 in this identical office uniform. This is an exact costume adaptation, NOT a redesign of the actions: preserve each cell's distinct eyes, eyebrows, mouth/beak shape, wing and hand articulation, head tilt, body twist, foot position, scale, and any held cup/bowl. Clothes follow the torso's squash and twist and lie BEHIND the white feather fringe, moving wings and held props. Do not copy the neutral pose onto expressive cells. Preserve even tiny differences between adjacent cells. Keep the normal eye pupils and also preserve the pupil-less golden angry eyes where shown in work cells; keep the closed-eye intermediate blinks as shown. Maintain the exact established huge white-headed brown eagle identity and warm clean cartoon shading, no realistic feathers. FULL BODY in every cell, feet never cropped. No extra limbs, no table/computer/fire/background scenery, no labels, numbers or text. True alpha transparent background; no painted black/checkerboard. For final padding cells that are neutral, keep them neutral but dressed. Output a clean high-resolution production sprite sheet.
```

## 管线和验收

`tools/prepare_expansion_assets.py` 提取原生 alpha 角色，匹配头部尺度和脚底锚点，建立真实关键帧；RGB/alpha 分别插值后重新组合、稳定脚点，保留精确段边界。各动作帧数、时间变化、锚点和边缘检查记录在 AnimationPreviews 的 `*-qa.json` 中。

60 Hz 是播放时间线目标，不保证每台电脑一直达到 60 FPS；WPF 合成、资源预载和运行环境都会影响实际帧率。
