# Composition device initialization regression

Run `dotnet run --project tools/CompositionControlSelfTest` on Windows. Each run creates a new `EagleCompositionControl-*` temporary directory containing its browser profile, JSON results and fallback screenshot. Existing pet saves are not used.

The tool links the production `GraphicsSafeWebView2CompositionControl` and pins the same WebView2 package and Windows target as the desktop app. Its five checks cover real browser navigation, injected D3D COM failure with visible raster pixels, cancellation before loading, a COM error on later template application, and propagation of unrelated exceptions. It does not install a global dispatcher exception handler.

## Observed failure

The 2026-09-30 Release `1.12.0.2` business smoke completed its first five cases and captured the shop preview, then exited while opening the separate craving-scene preview. Windows Application events 1026/1000 at 14:30:17/18 record an unhandled `COMException 0x8876086C` in `Direct3DHelper.CreateD3D9Device`, reached from `WebView2CompositionControl_Loaded`. WER report ID: `54c19aec-785c-4312-b954-7445082d53ec`. The executable SHA-256 was `BA82120EB3086CDB5A0CF4D6056D75B905645007781566AF017E6404303B1320`.

For the locked SDK `1.0.4191.47`, the private first `Loaded` handler creates the composition target and records its owning HWND, then invokes D3D image initialization. The virtual template hook supplies the image needed by that initialization. Deferring the base template hook until after the SDK's handler puts the observed D3D creation call inside an instance-local COM catch. The presenter receives the failure and uses its existing same-equipment raster fallback. No SDK private fields or event handlers are changed through reflection.

Instances belong to one host window and are disposed with it; cross-window reparenting is unsupported. SDK unload does not clear the D3D image or re-register its one-shot private `Loaded` handler. This fix does not claim to catch every GPU failure or exceptions in the earlier private composition-target setup. These control-level results are not complete Live2D model/preview acceptance: the production business smoke keeps requiring actual Live2D previews, and a fallback-only result does not satisfy that requirement.
