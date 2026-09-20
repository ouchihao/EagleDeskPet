"""Fast tests for diagnostic math; no repository pet asset is written."""
from __future__ import annotations

from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

import numpy as np
from PIL import Image

import inspect_rps_playback as qa


class PlaybackInspectorTests(unittest.TestCase):
    def test_raf_timestamp_older_than_preload_completion_cannot_select_negative_frame(self):
        node = shutil.which("node")
        if node is None:
            self.skipTest("Node is only needed for the pure-JS RAF regression")
        tick = re.search(r"^function tick\(now\).*?$", qa.HTML, re.MULTILINE).group(0)
        program = r'''
const assert = require('node:assert/strict');
let ready=true,playing=true,elapsed=0,last=100.1,frame=0,actual=Array(121).fill({}),scheduled=0,rendered=0;
const controls={speed:{value:'1'},status:{textContent:''}};
const $=id=>controls[id];
function controlsDisabled(disabled){}
function render(){assert.ok(frame>=0);assert.ok(actual[frame]);rendered++;}
function requestAnimationFrame(callback){scheduled++;}
''' + tick + r'''
tick(100); // RAF's scheduled timestamp predates async load's performance.now().
assert.equal(frame,0);assert.equal(elapsed,0);assert.equal(rendered,1);assert.equal(scheduled,1);
tick(117);assert.equal(frame,1);assert.equal(rendered,2);assert.equal(scheduled,2);
render=()=>{throw new Error('synthetic canvas error');};
tick(134);assert.equal(ready,false);assert.equal(scheduled,3);
assert.ok(controls.status.textContent.includes('synthetic canvas error'));
'''
        result = subprocess.run([node, "-e", program], capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_html_only_writer_does_not_replace_snapshot_report_or_frames(self):
        with tempfile.TemporaryDirectory(prefix="eagle-rps-player-html-test-") as temporary:
            root = Path(temporary)
            (root / "report.json").write_text("unchanged diagnostic report", encoding="utf-8")
            frame = root / "frame-0000.png"
            Image.new("RGBA", (2, 2), (50, 90, 120, 255)).save(frame)
            original = frame.read_bytes()
            qa.write_playback_page(root, {"clips": []})
            self.assertEqual(frame.read_bytes(), original)
            self.assertEqual((root / "report.json").read_text(), "unchanged diagnostic report")
            self.assertIn("Math.max(0,Math.min((now-last)/1000,.1))", (root / "index.html").read_text(encoding="utf-8"))

    def test_partial_staging_page_only_lists_captured_outfits_and_clips(self):
        with tempfile.TemporaryDirectory(prefix="eagle-rps-player-partial-test-") as temporary:
            root = Path(temporary)
            qa.write_playback_page(root, {"clips": [{"pack": "default", "clip": "RpsPaper"},
                                                    {"pack": "default", "clip": "Yawn"}]})
            page = (root / "index.html").read_text(encoding="utf-8")
            pack = re.search(r'<select id="pack".*?</select>', page).group(0)
            clip = re.search(r'<select id="clip".*?</select>', page).group(0)
            self.assertIn("原味大头鹰", pack)
            self.assertNotIn("Office", pack)
            self.assertIn("RpsPaper", clip)
            self.assertNotIn("RpsRock", clip)
            self.assertNotIn("Yawn", clip)

    def test_fully_transparent_rgb_does_not_create_a_visible_spike(self):
        blank = np.zeros((4, 4, 4), dtype=np.uint8)
        hidden = blank.copy()
        hidden[:, :, :3] = 255
        self.assertEqual(qa.visible_difference(blank, hidden),
                         {"rgb_mae": 0.0, "alpha_mae": 0.0, "changed_fraction": 0.0})

    def test_visible_union_avoids_diluting_difference_with_canvas_padding(self):
        blank = np.zeros((4, 4, 4), dtype=np.uint8)
        visible = blank.copy()
        visible[2, 2] = (255, 0, 0, 255)
        actual = qa.visible_difference(blank, visible)
        self.assertAlmostEqual(actual["rgb_mae"], 1 / 3)
        self.assertEqual(actual["alpha_mae"], 1)
        self.assertEqual(actual["changed_fraction"], 1)

    def test_hold_run_counts_intervals_not_files(self):
        runs = qa.contiguous_runs([False, True, True, True, False, True])
        self.assertEqual(runs, [
            {"start": 1, "end": 3, "frame_count": 3, "seconds": .0333},
            {"start": 5, "end": 5, "frame_count": 1, "seconds": 0.0}])

    def test_report_detects_endpoint_and_duplicate_frames(self):
        neutral = np.zeros((10, 10, 4), dtype=np.uint8)
        neutral[3:7, 3:7] = (255, 100, 20, 255)
        pose = neutral.copy()
        pose[5, 2] = (255, 100, 20, 255)
        with tempfile.TemporaryDirectory(prefix="eagle-rps-playback-test-") as temporary:
            folder = Path(temporary) / "default" / "RpsRock"
            folder.mkdir(parents=True)
            for index, frame in enumerate([neutral, pose, pose, neutral]):
                Image.fromarray(frame).save(folder / f"frame-{index:04d}.png")
            report = qa.inspect_clip(folder, neutral, 1)
            self.assertEqual(report["endpoints"], {"first_exact_neutral": True, "last_exact_neutral": True})
            self.assertEqual(report["duplicate_adjacent_frame_indexes"], [2])
            self.assertEqual(report["unique_frames"], 2)
            self.assertEqual(report["duration_seconds"], .05)
            self.assertTrue((folder / "timeline-contact.png").is_file())


if __name__ == "__main__":
    unittest.main(verbosity=2)
