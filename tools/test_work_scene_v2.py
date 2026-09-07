"""Read-only regression tests for v1.8's desk depth, filled fire and blink bridge."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import unittest

import numpy as np
from PIL import Image

from prepare_care_animation import measure_eat_root


ASSETS = Path(__file__).resolve().parents[1] / "DuckDeskPet/Assets"
METRICS: dict = {}


def pixels(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.array(image.convert("RGBA"))


class WorkSceneV2Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.phases = {}
        for name, relative, count in (
            ("transition", "Animations/WorkToBusyV2", 91),
            ("exit", "Animations/BusyExitV2", 121),
            ("fire", "SceneProps/WorkV2/Fire", 121),
        ):
            paths = sorted((ASSETS / relative).glob("frame-*.png"))
            if len(paths) != count:
                raise AssertionError(f"{relative}: {len(paths)} frames, expected {count}")
            cls.phases[name] = [pixels(path) for path in paths]
        cls.normal = pixels(ASSETS / "Animations/WorkLoop/frame-0000.png")
        cls.busy = pixels(ASSETS / "Animations/BusyLoop/frame-0000.png")
        cls.neutral = pixels(ASSETS / "mascot-animated-neutral.png")

    def test_01_complete_rgba_sequences(self):
        for frames in self.phases.values():
            self.assertTrue(all(frame.shape == (346, 384, 4) for frame in frames))
            self.assertTrue(all(frame[:, :, 3].min() == 0 for frame in frames))

    def test_02_exact_existing_anchor_seams(self):
        for first, second in ((self.phases["transition"][0], self.normal),
                              (self.phases["transition"][-1], self.busy),
                              (self.phases["exit"][0], self.busy),
                              (self.phases["exit"][-1], self.neutral),
                              (self.phases["fire"][0], self.phases["fire"][-1])):
            np.testing.assert_array_equal(first, second)

    def test_03_no_duplicate_adjacent_frames(self):
        for name, frames in self.phases.items():
            hashes = [hashlib.sha256(frame.tobytes()).hexdigest() for frame in frames]
            self.assertTrue(all(a != b for a, b in zip(hashes, hashes[1:])), name)
            METRICS[name] = {"frames": len(frames), "unique": len(set(hashes))}

    def test_04_unchanged_character_foot_anchor(self):
        target = measure_eat_root(Image.fromarray(self.neutral))
        for name in ("transition", "exit"):
            roots = [measure_eat_root(Image.fromarray(frame)) for frame in self.phases[name]]
            error_x = max(abs(root.torso_x - target.torso_x) for root in roots)
            error_y = max(abs(root.foot_contact_y - target.foot_contact_y) for root in roots)
            self.assertLessEqual(error_x, 1.0)
            self.assertLessEqual(error_y, 0.5)
            METRICS[name + "_anchor_error"] = [error_x, error_y]

    def test_05_fire_core_is_solid_in_every_frame(self):
        minimum_alpha = min(int(frame[180:313, 165:216, 3].min()) for frame in self.phases["fire"])
        self.assertGreaterEqual(minimum_alpha, 240, "No dark/transparent hollow may survive inside the flame.")
        for frame in self.phases["fire"]:
            core = frame[180:313, 165:216, :3].astype(np.int16)
            self.assertTrue(np.all(core[:, :, 0] - core[:, :, 2] > 100), "Core must contain red flame pigment, not checker/black.")
            self.assertTrue(np.all(core[:, :, 1] < 175), "The removed inner yellow ring must not return.")
        METRICS["minimum_fire_core_alpha"] = minimum_alpha

    def test_06_desk_naturally_occludes_all_loop_feet(self):
        desk = pixels(ASSETS / "SceneProps/WorkV2/desk-front.png")
        exposed = []
        for name in ("WorkLoop", "BusyLoop"):
            for path in (ASSETS / "Animations" / name).glob("frame-*.png"):
                character = pixels(path)
                exposed.append(int(np.count_nonzero((character[310:, :, 3] > 200) & (desk[310:, :, 3] < 240))))
        self.assertEqual(max(exposed), 0, "Not one opaque toe pixel can float below the new modesty panel.")
        METRICS["loop_frames_checked_for_exposed_feet"] = len(exposed)
        METRICS["maximum_exposed_feet_pixels"] = max(exposed)

    def test_07_eyelids_form_a_closed_barrier_before_yellow_opens(self):
        # Conservative pixel guard supplements visual review of generated keys.
        # Full black pupils exceed 100 dark pixels/eye in the same crop; closed
        # dark eyelid curves remain thin. Small warm pixels may be beak AA.
        maxima = {"yellow": 0, "dark": 0}
        for name, begin, end in (("transition", 18, 42), ("exit", 15, 33)):
            for frame in self.phases[name][begin:end + 1]:
                for eye in (frame[151:178, 148:174, :3], frame[151:178, 212:238, :3]):
                    warm = (eye[:, :, 0] > 180) & (eye[:, :, 1] > 120) & (eye[:, :, 2] < 130)
                    yellow = int(np.count_nonzero(warm))
                    dark = int(np.count_nonzero(eye.max(axis=2) < 80))
                    maxima["yellow"] = max(maxima["yellow"], yellow)
                    maxima["dark"] = max(maxima["dark"], dark)
                    # Optical flow may brighten a warm-brown eyelid's antialias
                    # into this hue range. Inspect its area AND thickness rather
                    # than misclassifying a 1–6 px curved line as an open iris.
                    self.assertLessEqual(yellow, 30, "Visible yellow iris during the fully closed bridge.")
                    if yellow:
                        ys, _ = np.where(warm)
                        self.assertLessEqual(int(ys.max() - ys.min() + 1), 8, "The closed eyelid became an open yellow eye.")
                    self.assertLessEqual(dark, 45, "A full black pupil survived the closed-eye bridge.")
        METRICS["closed_bridge_eye_pixels_maximum"] = maxima

    def test_08_original_art_and_legacy_animation_directories_still_exist(self):
        for relative in ("AnimationSources/work-props-sheet-v1.png", "AnimationSources/busy-fire-sheet-v1.png",
                         "SceneProps/Work/desk-front.png", "SceneProps/Work/Fire/frame-0120.png",
                         "Animations/WorkToBusy/frame-0090.png", "Animations/BusyExit/frame-0120.png"):
            self.assertTrue((ASSETS / relative).is_file(), relative)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(WorkSceneV2Tests))
    report = {"passed": result.wasSuccessful(), "tests": result.testsRun, "metrics": METRICS,
              "scope": "Read-only source PNG QA; separate WorkSceneV2SelfTest renders the production WPF scene."}
    print(json.dumps(report, indent=2))
    if args.report:
        args.report.resolve().write_text(json.dumps(report, indent=2), encoding="utf-8")
    raise SystemExit(0 if result.wasSuccessful() else 1)
