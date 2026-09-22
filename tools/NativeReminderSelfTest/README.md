# REQ-011 native reminder validation

The pure state tests never call Windows notification APIs, load real pet data, or
read account configuration. They cover stable due-cycle identifiers, independent
channels, atomic reservations, uncertain results, coalescing, migration, stale
clicks, and local save conflicts.

```powershell
dotnet run --project tools/NativeReminderSelfTest -c Release
dotnet run --project tools/ReminderSelfTest -c Release
dotnet run --project tools/ReminderIntegrationSelfTest -c Release -- .codex-build/native-reminder-ui
```

## Real Windows smoke (explicit opt-in)

`tools/NativeReminderSmoke` is a dedicated **test application identity**, not the
production pet. Its executable entry point always uses a fixture directory next
to that executable and an EXE-path-derived test mutex/pipe. A Windows COM cold
activation therefore cannot lose temporary environment variables and fall back
to the real pet's profile. Never replace this with a production EXE started with
`EAGLE_PET_DATA_DIR` or `EAGLE_PET_TEST_CHANNEL`: production deliberately refuses
native registration in those modes.

```powershell
dotnet publish tools/NativeReminderSmoke -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .codex-build/native-reminder-smoke
```

Run `EagleNativeReminderSmoke.exe` from that directory, then use its test button;
`--headless --send` is also available. This registers **only the dedicated test
EXE** and creates an actual Windows toast. `isolated-native-reminder-data/events.jsonl`
records submission, native notification-history XML and any actual activation
callback. The process exits after four minutes. Do not change Do Not Disturb,
notification permissions or lock-screen/privacy settings just to make a test pass.

Manual checks, under the user's existing system settings:

1. Confirm the real Windows banner and notification-center entry, not a WPF lookalike
2. Click while running; expect one `activated` event in the existing process
3. Send again, close the test app, click from notification center; expect the
   dedicated test EXE to restart and write `toast: true` plus an activation event
4. Confirm only one test process/window handles the click and no production data appears

After the test process exits, run the **same test EXE path** with `--cleanup` to
remove its test registration/history. Do not call uninstall on the production pet
or erase arbitrary registry keys. Keep the event log if it is needed as evidence.

## Recorded scope (2026-09-22)

- Native state/registration tests: 40 checks; existing reminder state/storage: 51 checks
- Existing WPF integration: 23 checks, or 25 with rendered UI evidence
- Dedicated `win-x64` self-contained single-file EXE: native `Show` accepted and
  `ToastNotificationManagerCompat.History.GetHistory()` returned the real toast,
  correct launch argument, group/tag, silent audio and local mascot image
- Actual visible banner and actual user click/cold click were **not verified**:
  Computer Use could not activate its captured test window twice, so UI automation
  stopped. API acceptance/history is not evidence that a banner was displayed/read
- No tests changed Windows Do Not Disturb, lock-screen privacy, app permissions,
  production profile, or AI/GitHub accounts. Disabled/failed/unknown behavior is
  state/transport-test coverage, not proof of all Windows policy combinations

## Transport and delivery semantics

Microsoft currently recommends Windows App SDK `AppNotificationManager` for WPF.
This implementation deliberately pins the older documented
`Microsoft.Toolkit.Uwp.Notifications` **7.1.3**, preserving the existing unpackaged
WPF single-file deployment without a Windows App SDK native runtime/bootstrapper.
It uses the maintenance compatibility API, not a claim that it is the newest API.
See [current WPF guidance](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet?pivots=wpf)
and [compatibility API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.toolkit.uwp.notifications.toastnotificationmanagercompat?view=win-comm-toolkit-dotnet-7.1).
The package is MIT-licensed; `System.Drawing.Common` is explicitly pinned to
8.0.31 rather than inheriting the old 4.7 dependency.

The compatibility identity is tied to the EXE path; keep the published executable
at a stable path across upgrades. Moving it creates a different identity and old
notifications can still refer to the former location. This release does not claim
arbitrary-path upgrade registration/click continuity has been tested.

Deadlines, settings and the per-cycle channel ledger are committed in one
`reminders.json` transaction. Native submission is reserved before the OS call;
therefore crashes cannot cause automatic duplicate submissions. There is no atomic
transaction spanning disk and Windows: a crash immediately before/after `Show`
can leave an uncertain result, visible in the reminder list and not auto-retried.
An accepted call is not a displayed banner, a read receipt, or acknowledgement of
a to-do. Failed/uncertain records remain visible until that reminder's next cycle
or an explicit pause/restart/delete. Clicking only locates the current ID/cycle;
stale clicks open the list with an explanation and perform no reminder mutation.
