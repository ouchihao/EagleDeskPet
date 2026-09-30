# Gaze Stage verification

Runs the **actual production Stage and official local Core** in an isolated WebView2 profile. No pet state, desktop screenshot, global cursor injection, network or model copies are used. All requests are served from an explicit local file map.

Arguments: repository root, official SDK Web 5-r.5 root, locally built Framework JavaScript, model-pack directory containing `runtime-manifest.json`, new evidence directory, and optionally a default-model candidate directory. All paths are absolute.

The scenario records four directions, diagonal sweeps, distant/stale input release, pause, Idle → Shy/Work transitions and the exact-neutral PNG Yawn handoff. Stable WorkLoop and BusyLoop are also exercised: tracking adds to the authored head baseline, typing and body parameters remain unchanged, ordinary work pupils move, and the pupil-less yellow busy expression retains its original eyes. Sequence boundaries preserve the gaze contribution instead of dropping it every two seconds. Office pause releases gaze, and loafing blocks it entirely. It verifies the actual Core geometry foot anchor and error counters. Canvas PNGs, WebM, model/Stage hashes and sampled diagnostics are retained. Review the images and video before visual acceptance; numeric checks alone do not prove anatomy quality.

Without a candidate override, the same run additionally switches through all six models and captures each model's four idle directions and extreme office diagonals. A default candidate override validates only that outfit's tracking, not all six models.

This is not a physical mouse, mixed-DPI monitor or Windows PNG-overlay test. No physical 60 Hz Present claim follows from browser frame callbacks.

The pause regression mirrors the real host order: disabled gaze sample, paused state for the same action sequence, then a paused `loafing:none` heartbeat. It checks that the first rendered frame retains the current gaze, subsequent frames ease towards center, and the final pose settles. A none-to-none heartbeat must not reset gaze or cancel an unrelated action transition; actual loafing takeover still suppresses gaze immediately.

The production profiles use head X/Y/Z parameter offsets of 22/12/12 in Idle, 18/9/9 while working, and 16/8/7 while busy. These are Cubism parameter units, not literal head angles: the current short-neck rig maps a Z value of 30 to a 10° maximum tilt, so the additional gaze lean is at most 4°/3°/2.33°. It makes the response legible at the desktop's 160-DIP image width without changing the model proportions. Offsets are added to the original action and clamped to the intersection of authored and actual Core parameter ranges. Pupil ranges are ±0.9/±0.85 for Idle/Work; Busy keeps its pupil-less eye art. Direction inputs ease in, and distant or stale inputs ease back to the authored action.
