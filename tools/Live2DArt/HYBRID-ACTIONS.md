# Native PNG / Live2D action handoffs

`pack-hybrid-actions.mjs` is an offline candidate builder for **Yawn and Eat only**. It never changes the approved model, original sprites, or production resources. The default frames are copied byte-for-byte. Office/Hoodie retain those original expressions and receive the current Live2D outfit appearance through spatial material correspondence, not an opacity crossfade between whole characters.

**Current production scope is Default native PNG actions and five outfits retaining Live2D.** The builder defaults to `default` only. Office/Hoodie can be generated explicitly as authoring candidates, but neither is approved for production: Office still has jacket-blue contamination on a raised wrist from incorrect chest/sleeve membership; Hoodie has one localized UV correction awaiting full outfit acceptance. Ox/Hero/Astronaut also remain **unaccepted**: inherited sleeve/torso UV partitions and transparent fabric cutouts fragment in raised-arm poses. The tool refuses complete tracks for those three outfits; `--samples-only` exists solely for diagnostics. Passing neutral-pixel tests is not full-animation acceptance.

Explicit Office/Hoodie candidate routes are development inputs, **not production permission or a completed delivery scope**. Complete playback review is required before promotion; the present integration must not enable those two candidate tracks. Keeping all five outfits on their existing Live2D actions preserves working behavior; it does not mean their exaggerated raster costume adaptation is finished.

```powershell
node tools/Live2DArt/pack-hybrid-actions.mjs .codex-build/hybrid-default-only
node tools/Live2DArt/pack-hybrid-actions.mjs .codex-build/hybrid-hoodie-author-candidate --outfits=hoodie
node --test tools/Live2DArt/pack-hybrid-actions.test.mjs
$env:EAGLE_HYBRID_CANDIDATE = 'D:\Code\github\EagleDeskPet\.codex-build\hybrid-default-only'
node --test tools/Live2DArt/pack-hybrid-actions.test.mjs
```

Every output directory must be new and inside `.codex-build`. The builder records source hashes, all adjacent-frame difference curves, and native lower-foot bounds in `authoring-validation.json`. This report is not a runtime resource. The small `manifest.json` contains only the canvas, duration, frame count, and six explicit outfit routes. Supplied tracks use `route: "native-raster"` with their directory; excluded tracks use `route: "live2d"` without a directory and keep their original model animation. There are no placeholder PNGs. Diagnostic-only builds never receive a runtime manifest.

Each supplied directory contains `neutral.png`, `Yawn/frame-0000.png` through `frame-0120.png`, and the matching `Eat` sequence. The shared canvas is 384 × 346 and the original duration is 2 seconds. Frame 0 and frame 120 are checked against the matching approved FullActions source neutral. They pass through the same composition path as every other frame, with no endpoint replacement or neutral-only exception.

The compositor isolates source material slots so a raised sleeve cannot sample the neighboring jacket. Accessories use the PSD's actual pixels and relative anatomical head/body transforms. Forearms and the original held bowl occlude headwear; the white head and the closed cream-bowl domain are protected from clothing transfer. These safeguards do not make the inherited anatomical atlas a production-quality binding for every costume.

Review frames 0, 1, 6, 15, 30, 60, 90, 114, 119, and 120, then play the whole native-sized sequence. Inspect mouth-covering fingertips, cuffs, both bowl grips, the bowl's gold cavity, glasses/helmet contact, and the floor line. The original PNG sequences themselves have nonzero frame-0-to-1 changes; a renderer handoff can preserve their timing but cannot claim to remove those source-art changes.

Complex-outfit follow-up is explicit local chest/sleeve UV and occlusion re-authoring for these two actions. Do not replace a failed outfit with an undressed default animation, broaden a color mask until it covers the bowl, or label known-bad samples as complete.

## Audited sleeve-axis correction

The inherited Default-v1 UV detector reverses the viewer-right sleeve axis by 171.22° at Yawn frame 46 and reverses back at frame 52, although the underlying original wing scarcely moves. Hoodie therefore has one explicit local authoring correction: frames 46–51 interpolate the UV affine transform between the reviewed frames 45 and 52. The correction changes neither the source PNG nor the material colors and does not apply to Office, Eat, the other wing, or any endpoint. Source/map anchor hashes and every corrected input hash are recorded; changed anchors or a no-longer-reversed source are rejected for re-review.

`authoring-validation.json` also scans both arms across all 121 frames of both actions, reporting native/corrected axis steps over 30°. The remaining larger Eat steps correspond to different original arm poses and require visual comparison, not automatic angle flattening. Optional `EAGLE_HYBRID_PREVIOUS_HOODIE` enables a regression against a previous candidate: only Yawn frames 46–51 may have changed.
