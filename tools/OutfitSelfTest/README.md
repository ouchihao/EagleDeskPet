# Outfit resource self-test

This is an isolated WPF imaging test, not a desktop pet instance. It creates flat-color PNG fixtures
in memory, pumps its own dispatcher, and never reads or writes user saves or AI configuration.

```powershell
dotnet build tools/OutfitSelfTest/OutfitSelfTest.csproj -c Release --artifacts-path .codex-build/outfit-artifacts
dotnet .codex-build/outfit-artifacts/bin/OutfitSelfTest/release/OutfitSelfTest.dll
# Optional: also decode every declared real resource, without launching the pet.
dotnet .codex-build/outfit-artifacts/bin/OutfitSelfTest/release/OutfitSelfTest.dll --resources-root D:\Code\github\EagleDeskPet\DuckDeskPet
```

The fixture declares all 19 current animation clips regardless of which art packs are installed.
Tests cover manifest identity/scope/timing, every expected frame, actual PNG decoding and dimensions,
legacy three-clip prewarming, lazy clip loading, isolated preview, cache replacement, partial failures,
safe-boundary checks, cancellation, delayed loads, and latest-request-wins selection.

## Integration contract

- `OutfitCatalog.Load()` reads optional `Assets/outfits.json`; invalid registry keeps the default pack
  with `Warning`. `GetAvailabilityAsync(id)` verifies the *entire* suite. Do not sell/equip a suite
  just because its directory or preview exists.
- `RasterFramePlayer.WarmAsync()` warms only Yawn/Shy/Eat. Await `WarmClipAsync` or `WarmClipsAsync`
  before scheduling an expansion; `IsClipReady` is the final readiness check. Failed clips never
  partially install and `Apply` never falls back to a different outfit or pose.
- Player instance methods run on its Image dispatcher. `SelectOutfitAsync` checks standing both
  before and after loading. Suspend automatic actions and retain any existing scene until its
  safe exit before requesting a change. `UnsafeBoundary` means the owner must defer/retry.
- Every selection request supersedes older requests, even selecting the already active suite.
  Successful selection replaces the whole live cache atomically; failure keeps the old suite.
- Use `RestoreOutfitAsync` only for startup restoration. Missing saved art explicitly displays
  default with a warning; this method never mutates saved ownership/equipment.
- `LoadOutfitPreviewFramesAsync(catalog, id, clip, token)` owns independent frozen frames and has no
  live-player side effects. Idle returns one neutral frame. Unsupported/incomplete packs throw.
- Embedded assets are immutable: availability is cached per catalog. To inspect changed fixture
  or development resources, create a new catalog. UI windows should reuse the live catalog when possible.
- Existing csproj tests that explicitly link `AnimationAssets.cs` or `RasterFramePlayer.cs` must
  also link `OutfitCatalog.cs`. Production must embed the registry, per-outfit manifests, neutral
  frames, and per-clip frames; this test's injected resources do not prove packaging is complete.
