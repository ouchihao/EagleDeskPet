# Live2D production stage

This is the local WebGL renderer, not a business-rule engine or a sprite animation fallback. It loads the official Cubism Core and Framework supplied by the host from `/runtime/`; neither licensed runtime is embedded here. All asset requests remain on the host's allowlisted local origin.

## Authored motion

`actions.js` samples the exact durations in `Assets/actions.json`: Idle plus all 14 action/scene clips. Articulated wing lift/swing/elbow-fold, real bird-beak opening, body squash and eyes are driven through Core parameters. Parameter-space transitions use smooth, multi-key paths; they never flip or crossfade the whole eagle. Work/hungry phase boundaries match poses directly, without returning to neutral between loop phases.

Busy eyes are a distinct pupil-free yellow shape. `ParamBusyEyes` switches only while both eye-open parameters are zero, preventing an intermediate black/yellow pupil mixture. The second pair of eyes must be authored with the same closure parameters. Small tears and anger marks are separate procedural decorations, not replacements for character art.

## Scene coordinates and layering

The stable authoring canvas is 384 × 346. Core canvas origin and pixels-per-unit determine one fixed projection; the renderer never fits the camera to each moving pose. The feet remain model-authored anchors.

Each rendered frame calls `model.update()` exactly once. The first pass retains the model's internal ordering. A work/hungry frame then draws the complete desk and reuses that *same* Core result to render only the explicit forearm drawables into a transparent framebuffer. Each side becomes foreground only when its actual grip point rises above the desk surface, and is composited only where the moving desk covers it. The apron and computer remain in front. Hanging wings stay behind the entering desk.

`action-bindings.json`, alongside each model, supplies the exact source-name mapping for `forearm-l` and `forearm-r`, plus `handAttachments.l/r` with a `drawableId` and source-pixel grip point. `geometry.js` follows that point through real Core triangles using barycentric weights. Food is held by both wings, cups rotate about their grip, and limited prop-edge masks let the wings overlap the vessel rim without repainting their full silhouette. Drawable naming guesses and body-colour segmentation are not used.

`eyeAttachments.l/r` uses the same schema and binds the outer eye corners to the face mesh, not the compressing eyelid. Small translucent tears follow those real points and roll a short distance down the cheeks. The hungry loop uses gentle, paired sniffles and returns to the same pose and tear phase at its loop boundary.

## Host contract (protocol 1)

Navigate to `index.html?generation=<safe integer>`. The host provides `/stage/config.json` with:

```json
{
  "modelUrl": "/model/default/eagle.model3.json",
  "sceneCatalogUrl": "/assets/SceneProps/work-scenes.json",
  "assetRoot": "/assets/",
  "outfits": { "outfit.default": { "modelUrl": "/model/default/eagle.model3.json" } },
  "extras": { "bowlEmpty": { "url": "/model/props/bowlEmpty.png", "rect": [158,235,79,54] } },
  "defaultEquipment": { "desk": "desk.default", "computer": "computer.default", "outfit": "outfit.default" }
}
```

The snippet is abbreviated: startup requires all six outfit entries plus `bowlFull`, `bowlEmpty` and `tea`. Every outfit model and every declared desk/computer is loaded and validated before `ready`, so unsupported assets cannot be silently advertised as capabilities.

- Host `prepare`: `{protocol,generation,type,requestId,equipment}`. Complete resource preparation responds `prepared`; failure responds `prepare-error`. Preparation does not change the displayed outfit
- Host `state`: `{protocol,generation,type,revision,sequence,kind,phase,progress,durationSeconds,paused,equipment}`. Revision is monotonic; kind is the C# enum spelling; phase is Idle, Playing, ReturningToIdle, Paused or Terminal
- Stage `presented`: emitted only after the initial/equipment-changed state has actually drawn, with revision, sequence, kind and equipment
- Stage `terminal`: emitted once per sequence/kind only after an **authoritative host Terminal state** has actually drawn at progress 1. Local interpolation reaching 1 cannot advance business state

The C# timeline remains authoritative. Low-frequency snapshots are locally sampled with `requestAnimationFrame`, bounded at the terminal pose. Same-sequence snapshots do not rewind the displayed pose; repeated Idle snapshots do not reset breathing. Paused states stop local timeline progression. No wallet, save-state, shell-command or network APIs exist in this page.

## Isolated visual verification

Add `&qa=1` only in an isolated host/browser harness to expose the action selector and scrubber. Host smoke tests can use `window.eagleStage`:

- `capture()` redraws synchronously and returns a canvas-only PNG data URL
- `report()` / `diagnostics()` reports frame/Core-update/pass counters, errors, non-finite vertices, model load/release counters, required parameters, current model/outfit, sequence and progress
- `beginRecording(30)` and `finishRecording()` capture only the WebGL canvas and return WebM base64, bounded to 32 MiB; optional rate 60 is available
- `receive(state)` exercises the same host bridge; `sample(kind,progress)` is an isolated QA convenience

Recordings and rAF counters do not prove physical display Present FPS. Geometry tests likewise do not replace image-by-image validation of eyes, shoulder overlaps, hands, desk occlusion and outfit accessories.

Run the pure motion/bridge regressions with `node --test DuckDeskPet/Live2DStage/actions.test.cjs` from the repository root.
