# Transparent WebGL host probe

This is an isolated **host feasibility test**, not a Live2D renderer or a character model. It does not read pet saves, use AI/GitHub credentials, install a runtime, or replace the production PNG backend.

Requirements: .NET 8 SDK, Windows 10 19041+ for this probe build, an existing WebView2 Runtime, and an interactive Windows session. The WPF composition control needs the Windows SDK .NET projection; targeting plain `net8.0-windows` can compile but fail at runtime.

```powershell
dotnet restore tools/Live2DHostProbe/Live2DHostProbe.csproj --locked-mode
dotnet build tools/Live2DHostProbe/Live2DHostProbe.csproj -c Release --no-restore
dotnet tools/Live2DHostProbe/bin/Release/net8.0-windows10.0.19041.0/Live2DHostProbe.dll D:\Temp\EagleHostProbe-new-run
```

The output must be a new or empty absolute directory. The probe shows a small non-activating transparent test window, uses a private WebView2 profile under that directory, samples 10 seconds after warmup, saves its own WebGL/WPF renders and report, then closes. It never captures other applications. Exit 0 means its limited checks passed; 1 means failure; 2/3 means invalid arguments/nonempty output. The profile can be removed after the test and its browser processes exit; it contains no user's browser data.

Tests include transparent canvas pixels, body/desk/hand/front/computer semantic order using colored geometry, WPF overlay above WebView2, dark/light/transparent WPF composition, and rAF scheduling statistics. **rAF is not physical Present FPS.** No Cubism clipping mask, animated eagle, real mouse drag/drop, cross-monitor DPI, sleep/context recovery or long-run stability is certified by this test. Those remain explicit gates before a production renderer replacement.

The fixed local test page has CSP restrictions, no network API, no host objects, no external navigation/popups/downloads, and denied permission requests. The production bridge will additionally need versioned action/generation/capability validation; this diagnostic page only returns bounded probe messages.

Dependency: Microsoft.Web.WebView2 **1.0.4191.47**, pinned in the project and lock file. It is a Microsoft WebView2 SDK dependency under its NuGet license terms, not Cubism Core. The installed Evergreen Runtime is not copied into the repository or release package.
