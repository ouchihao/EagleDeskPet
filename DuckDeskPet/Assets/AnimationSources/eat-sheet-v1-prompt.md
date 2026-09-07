# Eat keyframe sheet prompt

Generator: built-in imagegen, 2026-09-05.

Identity reference: `Assets/mascot-animated-neutral.png`.
Behavior reference: diagnostic 8-frame contact sheet from optional local `References/Memes/干饭.gif` (relative to the repository root; original GIF not redistributed).

This prompt generated 15 authored key poses, not 121 direct image-generation frames. The asset preparation script supplies segmentwise motion interpolation and quality checks afterward. The generated sheet contains a baked checker matte despite the requested alpha; the existing border-connected extraction pipeline removes it while preserving the enclosed white head.

```text
Use case: identity-preserve
Asset type: production desktop-pet full-body animation keyframe sprite sheet, 15 poses, 5 columns x 3 rows.
Input images: Image 1 is the EXACT established eagle character identity and neutral pose. Image 2 is ONLY eating expression/behavior reference, not its backdrop or text.
Primary request: Draw a new consecutive FULL-BODY eating animation for this very same mischievous big-headed eagle. Keep its very large rounded white head ending in pointed white feather scallops, small grey eyebrows, shiny black oval eyes, soft pink blush, broad yellow-orange beak, short chubby brown body and two orange-yellow feet. Preserve Image 1 proportions, warm tan/brown outlines, soft flat cartoon shading and identity; do not redesign.
Scene/backdrop: actual transparent alpha background, no white fill, no checkerboard pixels. No floor, no shadow, no scene, no text.
Layout: exactly 15 isolated equal-size cells, 5 columns and 3 rows, read left-to-right then top-to-bottom. Every cell has the entire eagle including both feet. Fixed front camera, same character scale, feet touching the exact same baseline and same planted position within every cell, generous transparent clearance, no overlap between cells.
Action continuity: this is ONE smooth 2-second action, not a collection of unrelated expression poses. The wings, bowl, beak, eyes and body deform through coherent small sequential changes. The short torso stays planted while leaning a little toward food. Only ONE small cream-colored rice bowl, gripped naturally in its wings, not floating or teleported.
Pose 01: EXACT Image 1 neutral, both wings down, no bowl.
02: eyes glance down eagerly, one wing reaches close behind the hip to retrieve the bowl; only a slim bowl edge appears from behind its body.
03: bowl slides into view from behind hip, held low in BOTH wings, eager eyebrow lift.
04: both wings gently raise bowl to mid chest; head inclines a little, beak starting to part.
05: bowl raised to chin, beak opens in anticipation, no covering eyes.
06: bowl tilts to beak, eagle takes a greedy mouthful, eyes focused on food.
07: bowl still touching beak, small cheek puff begins, beak closes around food.
08: bowl lowers just below beak, comically puffed cheeks and pleased half-lidded eyes.
09: subtle alternating chew, bowl steady at chest, cheek shape slightly changes, playful smug eyebrow.
10: content closed-eye chew and tiny torso squeeze, bowl lowers a little.
11: swallows, puffed cheeks relax, beak returns close to its normal shape, pleased mischievous eyes.
12: bowl and wings lower toward hip, head returns upright.
13: bowl smoothly tucks behind the hip, only rim remains visible; body near neutral.
14: bowl completely occluded behind body, empty wings easing down, eyes and head settle.
15: EXACT Image 1 neutral, both wings down, no bowl.
Constraints: precise character identity across all 15 images; full-body, two visible feet in every frame; subtle coordinated limb and mouth changes; same bowl object with consistent cream color and size; subtle goofy personality, no realistic feathers, no brown round owl head, no human teeth, no extra legs or arms, no floating rice particles, no wing-spreading, no giant props, no captions, no numbers, no grid lines, no logos, no motion blur. The character should fill about 75 percent of each cell height with empty margins.
```
