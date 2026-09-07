"""Check the work/busy scene and MCP bridge in an isolated GUI process.

The GUI's opt-in work smoke mode performs the deterministic care interactions.
This runner verifies its report, own-visual screenshots, and a real notification
delivered during WorkLoop. Existing user pets and their data are never targeted.
"""

import argparse
from collections import deque
import json
import math
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time
import uuid

from test_gui_smoke import Rpc, drain, hidden_startup, verify_own_smoke_state, verify_png


def main():
    project = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gui", type=Path, default=project / "dist/v1.8.0/EagleDeskPet.exe")
    parser.add_argument("--mcp", type=Path, default=project / "dist/v1.8.0/EagleDeskPet.Mcp.exe")
    args = parser.parse_args()
    gui_exe, mcp_exe = args.gui.resolve(strict=True), args.mcp.resolve(strict=True)
    assert gui_exe.name.lower() == "eagledeskpet.exe", gui_exe
    assert mcp_exe.name.lower() == "eagledeskpet.mcp.exe", mcp_exe
    build_root = (project / ".codex-build").resolve()
    print(f"GUI: {gui_exe}", flush=True)
    print(f"MCP: {mcp_exe}", flush=True)
    print(f"Fresh test output parent: {build_root}", flush=True)
    build_root.mkdir(exist_ok=True)
    output = Path(tempfile.mkdtemp(prefix="work-gui-smoke-", dir=build_root)).resolve()
    data = output / "data"
    data.mkdir()
    print(f"Isolated evidence and data: {output}", flush=True)

    environment = os.environ.copy()
    environment["EAGLE_PET_SMOKE_DIR"] = str(output)
    environment["EAGLE_PET_DATA_DIR"] = str(data)
    environment["EAGLE_PET_SMOKE_MODE"] = "work"
    environment["EAGLE_PET_TEST_CHANNEL"] = uuid.uuid4().hex
    gui_errors = deque(maxlen=32)
    gui_output = deque(maxlen=32)
    rpc = None
    started = time.monotonic()
    deadline = started + 90
    gui = subprocess.Popen([str(gui_exe)], cwd=gui_exe.parent, env=environment,
                           stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           **hidden_startup())
    threading.Thread(target=drain, args=(gui.stdout, gui_output), daemon=True).start()
    threading.Thread(target=drain, args=(gui.stderr, gui_errors), daemon=True).start()
    try:
        time.sleep(.3)
        assert gui.poll() is None, "Our isolated GUI exited at startup; existing pets were not touched."
        rpc = Rpc(mcp_exe, environment)
        init = rpc.send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                       "clientInfo": {"name": "eagle-work-gui-smoke", "version": "1"}})
        assert init["protocolVersion"] == "2025-11-25", init
        rpc.send("notifications/initialized", notification=True)
        tool_names = {tool["name"] for tool in rpc.send("tools/list")["tools"]}
        assert {"pet_get_state", "pet_notify"}.issubset(tool_names), tool_names

        state_before = None
        # No write/tool notification until BOTH the exact PID and data directory
        # identify the process we launched, even with an isolated pipe name.
        while time.monotonic() < min(deadline, started + 35):
            assert gui.poll() is None, "Our GUI exited before reaching WorkLoop."
            reply = rpc.tool("pet_get_state")
            if reply.get("status") == "unavailable":
                time.sleep(.15)
                continue
            state_before = verify_own_smoke_state(reply, gui, data)
            if state_before.get("currentAction") == "WorkLoop" and state_before.get("isWorking") is True:
                break
            time.sleep(.1)
        assert state_before and state_before.get("currentAction") == "WorkLoop" and state_before.get("isWorking") is True, \
            f"The isolated GUI did not reach active WorkLoop: {state_before}"
        verify_own_smoke_state(rpc.tool("pet_get_state"), gui, data)
        delivered = rpc.tool("pet_notify", {"eventId": "work-gui-smoke:" + uuid.uuid4().hex,
                                            "sessionId": output.name, "eventType": "reply_ready",
                                            "message": "办公中也能传话，桌子和电脑继续营业。"})
        assert delivered.get("status") == "accepted", delivered
        state_after = verify_own_smoke_state(rpc.tool("pet_get_state"), gui, data)
        assert state_after.get("isWorking") is True, "An MCP notification interrupted the work session."
        assert state_after.get("currentAction") in {"WorkLoop", "WorkToBusy", "BusyLoop"}, state_after
        print("PASS: exact GUI ownership verified; real MCP notification preserved continuous work", flush=True)

        remaining = deadline - time.monotonic()
        assert remaining > 0, "Work smoke test exceeded its 90-second deadline."
        gui.wait(timeout=remaining)
        assert gui.returncode == 0, f"GUI exited with {gui.returncode}: {' | '.join(gui_errors)}"
        report_path = output / "gui-smoke.json"
        assert report_path.is_file(), "The GUI did not produce gui-smoke.json."
        report = json.loads(report_path.read_bytes().decode("utf-8-sig"))
        for flag in ("passed", "workMode", "cancelExitVerified", "workStoppedAtZero", "propsHidden", "banterTogglePersisted"):
            assert report.get(flag) is True, f"{flag}: {report}"
        required_actions = {"Idle", "WorkEnter", "WorkLoop", "WorkToBusy", "BusyLoop", "BusyExit", "WorkExit"}
        assert required_actions.issubset(report.get("observedActions", [])), report
        drift = report.get("headAnchorDriftPixels")
        assert isinstance(drift, (float, int)) and not isinstance(drift, bool) and math.isfinite(drift) and 0 <= drift <= .1, report
        assert Path(report["dataDirectory"]).resolve() == data, report
        for filename in ("work-entry.png", "work-loop.png", "busy-loop.png", "work-exit.png", "pet-idle.png", "compact-banter.png", "care-panel-work.png"):
            verify_png(output / filename)
        saved = json.loads((data / "pet-state.json").read_bytes().decode("utf-8-sig"))
        assert saved.get("IsWorking") is False, saved
        preferences = json.loads((data / "companion.json").read_bytes().decode("utf-8-sig"))
        assert isinstance(preferences.get("ActiveBanterEnabled"), bool), preferences
        print("PASS: entry/loop/busy/cancel/hunger-zero exits, props cleanup, anchored scene, banter setting, own-visual PNGs", flush=True)
        print(f"Evidence: {report_path}", flush=True)
        print("Limits: accelerated isolated care clock; no manual pointer-drag or sustained physical-display 60 FPS certification.", flush=True)
        return 0
    finally:
        if rpc is not None:
            rpc.close()
        if gui.poll() is None:
            # Never enumerate/kill pets by name: only this exact launched child.
            gui.terminate()
            gui.wait(timeout=5)
            print(f"Stopped only this failed/timed-out work GUI (PID {gui.pid}); existing pets were untouched.", flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
