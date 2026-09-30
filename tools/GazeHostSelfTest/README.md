# Real PetWindow gaze verification

This Windows-only harness loads a built `EagleDeskPet.dll` and creates the actual `PetWindow` with its production presenter, native `GetCursorPos`, WPF `PointFromScreen`, `TickGaze`, and WebView messaging. It does **not** inject gaze messages into JavaScript or move the system mouse

Build the desktop app and this tool in Release, then run:

```powershell
dotnet tools/GazeHostSelfTest/bin/Release/net8.0-windows10.0.19041.0/GazeHostSelfTest.dll `
  '<absolute desktop build directory>/EagleDeskPet.dll' `
  '<absolute repository>/.tool-cache/live2d-runtime/local-runtime.json' `
  '<absolute repository>/DuckDeskPet/Live2DStage' `
  '<new empty evidence directory>'
```

An optional fifth argument `baseline` expects older builds to disable gaze in WorkLoop and BusyLoop. To compare old behavior, supply the old built assembly and its matching published Live2DStage directory

## Isolation and scope

- A fresh data directory, WebView profile and unique IPC channel are set before creating the window
- The harness never reads the user's pet save or settings, moves the system cursor, or controls another window
- Its own non-activating, transparent test window moves around the current mouse position, bringing the real screen pointer into known local positions without changing the pointer
- Reflection drives only the isolated pet: it stops automatic scene progression and the care timer, chooses a representative clip, changes test-only gate states, and invokes the production presentation path
- The test records real Stage reports and PNGs for three pet sizes, four directions, near-pointer positions, normal/busy work, disabled preference, dragging, paused animation, equipment transition, open context menu, loafing and distant pointer
- Reports include image measurements, actual monitor DPI, host gate values and sampled direction. A hash manifest records the tested assembly and Stage files
- System client-area animations must be enabled for positive-following checks. The harness reports that setting but never changes it
- This is a real host-to-renderer integration test, not a claim that a hidden window certifies physical display frame rate or all possible mixed-DPI monitor arrangements

The runner uses WPF's private resource-assembly field only to make the separately hosted product XAML resolve its own resources. This test-only reflection is not part of the desktop application
