"""Fast regression tests for reference provenance and animation registration."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image

import prepare_care_animation as care
import prepare_generated_sheets as pipeline


ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "DuckDeskPet" / "Assets"


class CareAssetTests(unittest.TestCase):
    def test_rife_segment_sampling_covers_whole_interval_without_tail_freeze(self) -> None:
        requests = []

        def fake_rife(rife, model, inputs, outputs, requested_count):
            requests.append(requested_count)
            for index in range(requested_count):
                # Reproduce the official CLI directory-mode time mapping.
                time = min(index * 2 / requested_count, 1.0)
                (outputs / f"{index:08d}.png").write_text(str(time), encoding="ascii")

        with tempfile.TemporaryDirectory(prefix="eagle-care-sampling-test-") as temporary:
            root = Path(temporary)
            inputs, outputs = root / "keys", root / "frames"
            inputs.mkdir()
            outputs.mkdir()
            for index in range(3):
                (inputs / f"{index:08d}.png").write_text("test key", encoding="ascii")
            with patch.object(pipeline, "_invoke_rife", fake_rife):
                pipeline._interpolate_rife_timeline(Path("unused.exe"), Path("unused-model"), inputs, outputs,
                                                    [0, 8, 18], root / "work", "sampling test")
            values = [float(path.read_text(encoding="ascii")) for path in sorted(outputs.glob("*.png"))]
        self.assertEqual(requests, [16, 20])
        self.assertEqual(len(values), 19)
        self.assertEqual(values[:9], [index / 8 for index in range(9)])
        self.assertEqual(values[9:], [index / 10 for index in range(1, 11)])

    def test_eat_keys_registered_to_both_feet_not_bowl_occluded_torso(self) -> None:
        neutral = Image.open(ASSETS / "mascot-animated-neutral.png").convert("RGBA")
        target = care.measure_eat_root(neutral)
        keys = sorted((ASSETS / "AnimationKeys" / "Eat").glob("key-*.png"))
        self.assertEqual(len(keys), 15)
        for path in keys:
            anchor = care.measure_eat_root(Image.open(path).convert("RGBA"))
            self.assertLessEqual(abs(anchor.torso_x - target.torso_x), 0.5, path.name)
            self.assertEqual(anchor.foot_contact_y, target.foot_contact_y, path.name)

    def test_original_gifs_have_not_been_modified(self) -> None:
        catalog = json.loads((ROOT / "docs" / "references" / "assets-catalog.json").read_text(encoding="utf-8"))
        source = Path(catalog["source_directory"])
        if not source.is_absolute():
            source = ROOT / source
        if not source.is_dir():
            self.skipTest("Original read-only reference directory is not available on this machine")
        self.assertEqual(len(catalog["items"]), 39)
        for item in catalog["items"]:
            self.assertEqual(hashlib.sha256((source / item["file"]).read_bytes()).hexdigest(), item["sha256"], item["file"])

    def test_temporal_qa_rejects_file_count_padding_with_repeated_frames(self) -> None:
        neutral = Image.open(ASSETS / "mascot-animated-neutral.png").convert("RGBA")
        frozen = {"passed": True, "errors": []}
        care.add_temporal_qa(frozen, [neutral] * 5)
        self.assertFalse(frozen["passed"])
        self.assertEqual(frozen["temporal_sampling"]["adjacent_duplicate_indexes"], [1, 2, 3, 4])
        key = Image.open(ASSETS / "AnimationKeys" / "Eat" / "key-01.png").convert("RGBA")
        neutral_endpoints = {"passed": True, "errors": []}
        care.add_temporal_qa(neutral_endpoints, [neutral, key, neutral])
        self.assertTrue(neutral_endpoints["passed"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
