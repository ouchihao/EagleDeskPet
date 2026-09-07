"""Exercise the published WPF GUI and its real MCP bridge with isolated pet data.

The GUI must contain the opt-in EAGLE_PET_SMOKE_DIR integration check. This runner
does not capture the desktop, alter client settings, or close existing pets.
All subprocess pipes use explicit UTF-8 bytes (independent of Windows code page).
"""

import argparse
from collections import deque
import json
import os
from pathlib import Path
import queue
import struct
import subprocess
import tempfile
import threading
import time
import uuid


def hidden_startup():
    options = {"creationflags": getattr(subprocess, "CREATE_NO_WINDOW", 0)}
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        options["startupinfo"] = startup
    return options


def drain(pipe, destination):
    for line in iter(pipe.readline, b""):
        destination.append(line[:4096].decode("utf-8", errors="replace").rstrip())


class Rpc:
    def __init__(self, executable, environment=None):
        self.process = subprocess.Popen([str(executable), "--source", "GUI 联调测试"],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, env=environment, **hidden_startup())
        self.output = queue.Queue()
        self.errors = deque(maxlen=32)
        self.next_id = 0
        threading.Thread(target=self._stdout, daemon=True).start()
        threading.Thread(target=drain, args=(self.process.stderr, self.errors), daemon=True).start()

    def _stdout(self):
        for line in iter(self.process.stdout.readline, b""):
            try:
                self.output.put(json.loads(line.decode("utf-8")))
            except (UnicodeDecodeError, json.JSONDecodeError):
                self.output.put({"invalid_stdout": line[:512].decode("utf-8", errors="replace")})
        self.output.put({"process_ended": True})

    def send(self, method, parameters=None, notification=False, timeout=7):
        self.next_id += 1
        request = {"jsonrpc": "2.0", "method": method}
        if parameters is not None:
            request["params"] = parameters
        if not notification:
            request["id"] = self.next_id
        self.process.stdin.write((json.dumps(request, ensure_ascii=False) + "\n").encode("utf-8"))
        self.process.stdin.flush()
        if notification:
            return None
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise AssertionError("MCP request timed out: " + method)
            try:
                response = self.output.get(timeout=remaining)
            except queue.Empty as error:
                raise AssertionError("MCP request timed out: " + method) from error
            assert "invalid_stdout" not in response, response
            assert not response.get("process_ended"), "MCP ended: " + " | ".join(self.errors)
            if response.get("id") == self.next_id:
                assert "error" not in response, response
                return response["result"]

    def tool(self, name, arguments=None):
        result = self.send("tools/call", {"name": name, "arguments": arguments or {}})
        reply = json.loads(result["content"][0]["text"])
        assert result.get("isError", False) == (not reply["accepted"]), result
        return reply

    def close(self):
        if self.process.poll() is not None:
            return
        try:
            self.process.stdin.close()
            self.process.wait(timeout=3)
        except (BrokenPipeError, subprocess.TimeoutExpired):
            # This is only the process launched above, never a name-based process search.
            self.process.kill()
            self.process.wait(timeout=3)


def verify_own_smoke_state(reply, gui, data):
    assert gui.poll() is None, "The launched GUI exited; refusing to notify another pet."
    assert reply.get("accepted"), reply
    state = reply.get("state")
    assert isinstance(state, dict) and state.get("smokeTest") is True, \
        "Refusing to notify a pet without the explicit GUI smoke-test marker."
    assert state.get("smokeProcessId") == gui.pid, "The bridge does not identify our exact smoke-test process."
    assert isinstance(state.get("smokeDataDirectory"), str), "The bridge did not identify its isolated data directory."
    assert Path(state["smokeDataDirectory"]).resolve() == data, "The bridge belongs to another smoke-test data directory."
    return state


def verify_png(path):
    raw = path.read_bytes()
    assert len(raw) > 32 and raw[:8] == b"\x89PNG\r\n\x1a\n", f"Missing/invalid visual evidence: {path}"
    width, height = struct.unpack(">II", raw[16:24])
    assert width > 1 and height > 1, f"Visual evidence has an empty layout: {path}"


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
    output = Path(tempfile.mkdtemp(prefix="gui-smoke-", dir=build_root)).resolve()
    data = output / "data"
    data.mkdir()
    print(f"Isolated evidence and data: {output}", flush=True)
    environment = os.environ.copy()
    environment["EAGLE_PET_SMOKE_DIR"] = str(output)
    environment["EAGLE_PET_DATA_DIR"] = str(data)
    environment["EAGLE_PET_TEST_CHANNEL"] = uuid.uuid4().hex
    environment.pop("EAGLE_PET_SMOKE_MODE", None)
    gui_errors = deque(maxlen=32)
    gui_output = deque(maxlen=32)
    rpc = None
    started = time.monotonic()
    gui = subprocess.Popen([str(gui_exe)], cwd=gui_exe.parent, env=environment,
                           stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           **hidden_startup())
    threading.Thread(target=drain, args=(gui.stdout, gui_output), daemon=True).start()
    threading.Thread(target=drain, args=(gui.stderr, gui_errors), daemon=True).start()
    try:
        # Give a mutex-collision/early startup failure time to exit before creating a client.
        time.sleep(.3)
        assert gui.poll() is None, \
            "GUI exited at startup (possibly an existing pet owns its mutex); no existing process was touched."
        rpc = Rpc(mcp_exe, environment)
        init = rpc.send("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                       "clientInfo": {"name": "eagle-gui-smoke", "version": "1"}})
        assert init["protocolVersion"] == "2025-11-25", init
        rpc.send("notifications/initialized", notification=True)
        tool_names = {tool["name"] for tool in rpc.send("tools/list")["tools"]}
        assert {"pet_get_state", "pet_notify"}.issubset(tool_names), tool_names
        state_before = None
        # Read-only probing is the only operation permitted until the smoke marker is verified.
        while time.monotonic() - started < 12:
            assert gui.poll() is None, "Our GUI exited before its bridge became ready."
            reply = rpc.tool("pet_get_state")
            if reply.get("status") == "unavailable":
                time.sleep(.2)
                continue
            state_before = verify_own_smoke_state(reply, gui, data)
            if state_before.get("food") == 4:
                break
            time.sleep(.2)
        assert state_before is not None and state_before.get("food") == 4, \
            "The isolated GUI did not finish its own feed operation in time."
        assert gui.poll() is None, "Our GUI ended before notification; refusing delivery."
        delivered = rpc.tool("pet_notify", {"eventId": "gui-smoke:" + uuid.uuid4().hex,
                                            "sessionId": output.name, "eventType": "reply_ready",
                                            "message": "实际 GUI 与 MCP 联调成功，记得给我加餐。"})
        assert delivered["status"] == "accepted", delivered
        state_after = verify_own_smoke_state(rpc.tool("pet_get_state"), gui, data)
        assert isinstance(state_after.get("pendingNotifications"), int), state_after
        assert state_after["pendingNotifications"] >= 0, state_after
        print("PASS: actual GUI identified, real MCP notification accepted, pending queue queried", flush=True)
        remaining = 45 - (time.monotonic() - started)
        assert remaining > 0, "GUI smoke test exceeded its total deadline."
        gui.wait(timeout=remaining)
        assert gui.returncode == 0, f"GUI exited with {gui.returncode}: {' | '.join(gui_errors)}"
        report_path = output / "gui-smoke.json"
        assert report_path.is_file(), "The GUI did not produce gui-smoke.json. Check assets/startup/mutex."
        report = json.loads(report_path.read_bytes().decode("utf-8-sig"))
        assert report.get("passed") is True, report
        assert report.get("assetsPredecoded") is True, report
        assert report.get("food") == 4 and report.get("meals") == 1, report
        assert "Eat" in report.get("observedActions", []), report
        assert Path(report["dataDirectory"]).resolve() == data, report
        saved = json.loads((data / "pet-state.json").read_bytes().decode("utf-8-sig"))
        assert saved.get("Food") == 4 and saved.get("TotalMeals") == 1, saved
        for filename in ("care-panel.png", "notification-bubble.png", "pet-eating.png"):
            verify_png(output / filename)
        print("PASS: GUI feed, Eat animation, own-visual PNGs, save/reload (food=4, meals=1), clean exit", flush=True)
        print(f"Evidence: {report_path}", flush=True)
        print("Limits: no manual pointer-drag test or sustained physical-display 60 FPS certification.", flush=True)
        return 0
    finally:
        if rpc is not None:
            rpc.close()
        if gui.poll() is None:
            # Only terminate the exact child process this run created, and only after failure/timeout.
            gui.terminate()
            gui.wait(timeout=5)
            print(f"Stopped only this failed/timed-out smoke GUI (PID {gui.pid}); existing pets were untouched.", flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
