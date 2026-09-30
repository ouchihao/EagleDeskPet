# Presentation acknowledgement regression

Run `dotnet run --project tools/PresentationSelfTest/PresentationSelfTest.csproj` from the repository root.

The 15 deterministic checks exercise the production `PresentationAcknowledgementGate` and actual `PetBehaviorController`. A generation, sequence and action must all match a terminal sample already sent by the owner. Predictive playback, stale callbacks and duplicate receipts cannot complete feeding or advance a work segment.

This test does not load a browser or certify visual quality. `ExpansionIntegrationSelfTest` additionally exercises preview cancellation, terminal waiting and matching raster fallback through a controlled backend.

For actual WebGL/CompositionControl integration, first prepare the locally licensed official SDK with `tools/prepare-live2d-runtime.ps1`, then run:

```powershell
./tools/run_expansion_smoke.ps1 -Exe 'D:\absolute\path\EagleDeskPet.exe' -Mode live2d
```

`live2d-business` checks the actual production feeding, work, equipment and preview coordinators. `live2d-gallery` renders all fourteen actions in six outfits, all 294 outfit/desk/computer combinations and all 42 outfit/hungry-desk combinations. Every outfit has its own complete recording and three captures per action. The full `live2d` mode runs both. All modes require the launcher's newly created temporary profile and sentinel; none reuse real saves. The launcher retains evidence and limits its own child to 650 seconds.

For a faster anatomy review, run `-Mode live2d-review -ReviewOutfit outfit.default`. This partial run plays all fourteen actions in the requested outfit, records one WebM and captures progress 0, .2, .4, .6, .8 and 1 for each action. It does not run business scenarios or equipment combinations, and its report explicitly marks full-gallery coverage false.

The gallery uses its own presentation clock at catalog durations while care and gameplay are stopped. Every run verifies six real models render visible pixels, zero planted-foot displacement, no missing parameters, no GL errors and no non-finite vertices. Images and recordings still need visual review. Callback cadence is not a physical-monitor 60 FPS measurement, and native dragging/multi-monitor input need separate manual validation.
