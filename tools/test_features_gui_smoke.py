"""Isolated v1.8 GUI check: medals, safe client setup and GitHub fake transport.

No account credentials, real GitHub network traffic, or browser launches.
Screenshots are the application's own WPF visuals, not desktop captures.
"""
import argparse
from collections import deque
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import uuid

from test_gui_smoke import drain, hidden_startup, verify_png


def main():
    project = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gui", type=Path, default=project / "dist/v1.8.0/EagleDeskPet.exe")
    args = parser.parse_args()
    executable = args.gui.resolve(strict=True)
    build_root = (project / ".codex-build").resolve()
    print(f"GUI target: {executable}", flush=True)
    print(f"Fresh evidence parent: {build_root}", flush=True)
    build_root.mkdir(exist_ok=True)
    output = Path(tempfile.mkdtemp(prefix="features-gui-smoke-", dir=build_root)).resolve()
    data = output / "data"
    data.mkdir()
    print(f"Isolated output/data: {output}", flush=True)
    environment = os.environ.copy()
    environment.update(EAGLE_PET_SMOKE_DIR=str(output), EAGLE_PET_DATA_DIR=str(data),
                       EAGLE_PET_SMOKE_MODE="features", EAGLE_PET_TEST_CHANNEL=uuid.uuid4().hex)
    errors, stdout = deque(maxlen=30), deque(maxlen=30)
    gui = subprocess.Popen([str(executable)], cwd=executable.parent, env=environment,
                           stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                           **hidden_startup())
    threading.Thread(target=drain, args=(gui.stdout, stdout), daemon=True).start()
    threading.Thread(target=drain, args=(gui.stderr, errors), daemon=True).start()
    try:
        gui.wait(timeout=60)
        assert gui.returncode == 0, f"GUI failed: {gui.returncode}: {' | '.join(errors)}"
        report_path = output / "gui-smoke.json"
        assert report_path.exists(), "No GUI test report was written."
        report = json.loads(report_path.read_bytes().decode("utf-8-sig"))
        assert report.get("passed") is True, report
        assert report.get("featuresMode") is True and report.get("fakeHttpOnly") is True, report
        assert report.get("networkWrites") == 0 and report.get("checkCount", 0) >= 26, report
        assert Path(report["dataDirectory"]).resolve() == data, report
        assert Path(report["clientConfigHome"]).resolve() == output / "client-home", report
        for name in ("context-menu", "size-menu", "honor-wall", "care-panel", "github-connect",
                     "github-inbox", "github-bubble", "pet-unread", "github-new-message",
                     "client-setup-preview", "client-setup-installed"):
            verify_png(output / (name + ".png"))
        assert not (data / "github-credential.dpapi").exists(), "Disconnect left the test credential behind."
        print(f"PASS: {report['checkCount']} app-level checks, real WPF/gallery/DPAPI, fake GitHub and disposable client setup", flush=True)
        print(f"Evidence: {report_path}", flush=True)
        print("Limits: no real GitHub login/delivery, browser launch, or manual pointer/DPI test.", flush=True)
    finally:
        if gui.poll() is None:
            gui.terminate()
            gui.wait(timeout=5)
            print(f"Stopped only our timed-out test child, PID {gui.pid}.", flush=True)


if __name__ == "__main__":
    main()
