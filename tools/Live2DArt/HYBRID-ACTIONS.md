# Native PNG / Live2D action handoffs

`pack-hybrid-actions.mjs` is an offline candidate builder for **Yawn and Eat only**. It never changes the approved model, original sprites, or production resources. The default frames are copied byte-for-byte. Office/Hoodie retain those original expressions and receive the current Live2D outfit appearance through spatial material correspondence, not an opacity crossfade between whole characters.

Full candidates currently permit `default`, `office`, and `hoodie` only. All 121 frames per action still require visual acceptance. Ox/Hero/Astronaut are **not visually accepted**: their inherited sleeve/torso UV partitions and transparent fabric cutouts break into patches in raised-arm poses. The tool refuses to publish complete tracks for those three outfits; `--samples-only` exists solely for further authoring diagnostics. Passing the neutral-pixel tests is not full-animation acceptance.

These routes describe the authoring tool's current reviewable capabilities, **not a final delivery scope already selected by the user**. Production promotion requires the user's scope decision and complete playback review by the main workflow.

```powershell
node tools/Live2DArt/pack-hybrid-actions.mjs .codex-build/hybrid-reviewed-subset --outfits=default,office,hoodie
node --test tools/Live2DArt/pack-hybrid-actions.test.mjs
$env:EAGLE_HYBRID_CANDIDATE = 'D:\Code\github\EagleDeskPet\.codex-build\hybrid-reviewed-subset'
node --test tools/Live2DArt/pack-hybrid-actions.test.mjs
```

Every output directory must be new and inside `.codex-build`. The builder records source hashes, all adjacent-frame difference curves, and native lower-foot bounds in `authoring-validation.json`. This report is not a runtime resource. The small `manifest.json` contains only the canvas, duration, frame count, and six explicit outfit routes. Supplied tracks use `route: "native-raster"` with their directory; excluded tracks use `route: "live2d"` without a directory and keep their original model animation. There are no placeholder PNGs. Diagnostic-only builds never receive a runtime manifest.

Each supplied directory contains `neutral.png`, `Yawn/frame-0000.png` through `frame-0120.png`, and the matching `Eat` sequence. The shared canvas is 384 × 346 and the original duration is 2 seconds. Frame 0 and frame 120 are checked against the matching approved FullActions source neutral. They pass through the same composition path as every other frame, with no endpoint replacement or neutral-only exception.

The compositor isolates source material slots so a raised sleeve cannot sample the neighboring jacket. Accessories use the PSD's actual pixels and relative anatomical head/body transforms. Forearms and the original held bowl occlude headwear; the white head and the closed cream-bowl domain are protected from clothing transfer. These safeguards do not make the inherited anatomical atlas a production-quality binding for every costume.

Review frames 0, 1, 6, 15, 30, 60, 90, 114, 119, and 120, then play the whole native-sized sequence. Inspect mouth-covering fingertips, cuffs, both bowl grips, the bowl's gold cavity, glasses/helmet contact, and the floor line. The original PNG sequences themselves have nonzero frame-0-to-1 changes; a renderer handoff can preserve their timing but cannot claim to remove those source-art changes.

Complex-outfit follow-up is explicit local chest/sleeve UV and occlusion re-authoring for these two actions. Do not replace a failed outfit with an undressed default animation, broaden a color mask until it covers the bowl, or label known-bad samples as complete.
