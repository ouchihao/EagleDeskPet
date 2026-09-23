# Eagle Live2D source · authoring proof

This is a **development sample**, not the production pet or a finished character pack. It does not replace the existing PNG animations, costumes, save files or shop catalog.

## Artwork provenance

`eagle-parts-v1.png` was generated with the built-in image-generation tool on 2026-09-23, using this project's `Assets/mascot-animated-neutral.png` as the identity reference. It is a 1254 × 1254 RGBA parts sheet, not a sprite animation. No third-party Live2D character was reskinned.

The generated sheet contains a complete blank head, torso, two wings, two eyes, eyebrows, upper/lower beak, two feet, cheeks, mouth interior and a closed eyelid. `tools/Live2DArt/pack-eagle.mjs` mechanically crops and positions 15 layers into a 512 × 512 PSD. Source coordinates are explicit because the generated parts do not obey exact grid boundaries. Unused alternative eyelid art remains on the original sheet.

The source is an initial likeness study: edge speckles and the beak need a later art cleanup. It is not approved final animation art. The first rig only establishes genuine Core-compatible parameter deformation; feeding, working, outfits and all semantic actions still require authoring and acceptance.

## Generation brief

Use case: precise-object-edit. Create a production 2D puppet PARTS SHEET for Live2D, not animation poses. Use the supplied neutral eagle as the exact identity reference: big white head with a jagged feather collar and warm golden-brown outline, plump warm-brown body, stubby wings, yellow feet and beak, black oval eyes with white glints, gray eyebrows and pink cheeks. Preserve its identity; no human character, realistic feathers or 3D rendering.

Square transparent canvas, preferably 2048 × 2048, exactly four rows and four columns of isolated parts with generous spacing, no frames, labels, text, background or cast shadows. Row 1: complete blank head with no facial features; complete closed torso including normally occluded areas; viewer-left wing; viewer-right wing. Row 2: viewer-left open eye; viewer-right open eye; viewer-left gray eyebrow; viewer-right gray eyebrow. Row 3: upper yellow beak; lower orange beak; viewer-left foot; viewer-right foot. Row 4: viewer-left pink cheek; viewer-right pink cheek; dark mouth interior with small tongue; a closed-eye eyelid line. Complete every hidden contour, smooth outlines, clean true alpha. No assembled character and no accessories.

Actual output size was 1254 × 1254, not the requested preferred size. The build script checks the actual size before applying its measured layout.

## Rebuild

See [the authoring tool](../../../tools/Live2DArt/README.md) and [toolchain provenance](../../../docs/LIVE2D-TOOLCHAIN.md). PSDs and CMO3s are authoring sources; generated MOC3/texture/motion files are runtime assets. Official Cubism Core is intentionally not included in this repository.
