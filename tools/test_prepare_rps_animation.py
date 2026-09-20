"""RPS V2 staging contract tests; GPU interpolation and production art are not run."""
from __future__ import annotations

from contextlib import ExitStack
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image

import prepare_rps_animation as rps


class RpsPreparationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="eagle-rps-v2-tool-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.assets = self.root / "DuckDeskPet" / "Assets"
        self.sources = self.assets / "AnimationSources" / "RpsV2"
        self.sources.mkdir(parents=True)
        self.source = self.sources / "default-rock-v2.png"
        Image.new("RGBA", (8, 8), (80, 100, 140, 255)).save(self.source)
        self.neutral_path = self.assets / "mascot-animated-neutral.png"
        self.neutral = Image.new("RGBA", (8, 8), (10, 20, 30, 255))
        self.neutral.save(self.neutral_path)
        self.output = self.root / ".codex-build" / "rps-v2-build"

    def metadata(self, order):
        self.source.with_suffix(".json").write_text(json.dumps({"source_order": order}), encoding="utf-8")

    def mocks(self, *, preflight_pass=True, frame_pass=True):
        stack = ExitStack()
        self.addCleanup(stack.close)
        # No production image is read, normalized, written or interpolated.
        cells = [Image.new("RGBA", (8, 8), (40 + index, 60, 80, 255)) for index in range(16)]
        extract = stack.enter_context(patch.object(rps, "extract_native_cells", return_value=cells))
        align = stack.enter_context(patch.object(rps, "align_cells", side_effect=lambda frames, neutral: frames))
        stack.enter_context(patch.object(rps, "clean_registered_alpha", side_effect=lambda frame: frame))
        stack.enter_context(patch.object(rps.pipeline, "qa_frame_sequence", side_effect=lambda *args, **kwargs: {
            "passed": frame_pass, "errors": [] if frame_pass else ["Synthetic strict halo failure"]}))
        stack.enter_context(patch.object(rps.pipeline, "inspect_authored_key_preflight", return_value={
            "passed": preflight_pass, "errors": [] if preflight_pass else ["Synthetic preflight failure"]}))
        contacts = stack.enter_context(patch.object(rps.pipeline, "make_key_contact_sheet"))
        stack.enter_context(patch.object(rps.pipeline, "make_dark_background_contact_sheet"))
        stack.enter_context(patch.object(rps.pipeline, "make_60fps_gif"))
        return extract, align, contacts

    def test_throw_and_reaction_specs_have_exact_16_locked_positions(self):
        for clip in rps.CLIP_SOURCES:
            spec = rps.clip_spec(clip, "test.png")
            positions = rps.REACTION_POSITIONS if clip in ("RpsWin", "RpsLose") else rps.THROW_POSITIONS
            self.assertEqual(spec.authored_frame_indices, positions)
            self.assertEqual(len(positions), 16)
            self.assertEqual(spec.source_count, 16)
            self.assertTrue(spec.lock_authored_frames)
            self.assertTrue(spec.segmentwise_interpolation)
            self.assertEqual(round(spec.duration_seconds * 60), positions[-1])
            self.assertTrue(all(a < b for a, b in zip(positions, positions[1:])))

    def test_default_source_order_and_deliberate_reuse(self):
        self.assertEqual(rps.read_source_order(self.source), (tuple(range(16)), None))
        order = [0, 1, 2, 3, 4, 5, 6, 7, 8, 7, 10, 11, 12, 13, 14, 15]
        self.metadata(order)
        actual, metadata = rps.read_source_order(self.source)
        self.assertEqual(actual, tuple(order))
        info = rps.provenance(self.source, self.neutral_path, actual, metadata,
                              rps.clip_spec("RpsRock", self.source.name), self.assets)
        self.assertEqual(info["reused_source_cells"], [{"source_cell": 7, "key_positions": [7, 9]}])
        self.assertEqual(info["omitted_source_cells"], [9])
        self.assertEqual(info["source_order_metadata_sha256"], rps.sha256(metadata))

    def test_source_order_rejects_bool_missing_and_out_of_range(self):
        for order in ([*range(15), True], list(range(15)), [*range(15), 16], [*range(15), -1]):
            with self.subTest(order=order):
                self.metadata(order)
                with self.assertRaisesRegex(ValueError, "exactly 16 integer"):
                    rps.read_source_order(self.source)

    def test_output_guard_rejects_repository_root_assets_and_codex_build_root(self):
        for forbidden in (self.root, self.assets, self.root / ".codex-build", self.root / ".codex-build" / ".." / "Assets"):
            with self.subTest(path=forbidden), self.assertRaises(ValueError):
                rps.validate_output_root(forbidden, self.root)
        safe = self.root / ".codex-build" / "rps-v2-build"
        self.assertEqual(rps.validate_output_root(safe, self.root), safe.resolve())

    def test_keys_only_reorders_before_alignment_locks_endpoints_and_keeps_inputs(self):
        order = [0, 1, 2, 3, 4, 5, 6, 7, 8, 7, 10, 11, 12, 13, 14, 15]
        self.metadata(order)
        before = {p: rps.sha256(p) for p in (self.source, self.neutral_path, self.source.with_suffix(".json"))}
        extract, align, contacts = self.mocks()
        with patch.object(rps.pipeline, "run_rife") as rife:
            report = rps.build_one(self.assets, self.output, "default", "RpsRock", keys_only=True)
        rife.assert_not_called()
        extract.assert_called_once_with(self.source, 4, 4)
        self.assertEqual([frame.getpixel((0, 0))[0] for frame in align.call_args.args[0]], [40 + i for i in order])
        self.assertTrue(report["passed"])
        self.assertEqual(report["authored_frame_indices"], list(rps.THROW_POSITIONS))
        self.assertEqual(report["source_sha256"], before[self.source])
        for index in (0, 15):
            with Image.open(self.output / "default" / "Keys" / "RpsRock" / f"key-{index:02d}.png") as image:
                self.assertEqual(image.tobytes(), self.neutral.tobytes())
        self.assertEqual(before, {p: rps.sha256(p) for p in before})
        self.assertEqual(contacts.call_count, 1)
        self.assertFalse((self.output / "default" / "Animations").exists())

    def test_key_preflight_failure_preserves_reports_and_does_not_call_rife(self):
        self.mocks(preflight_pass=False)
        with patch.object(rps.pipeline, "run_rife") as rife:
            with self.assertRaisesRegex(RuntimeError, "authored keys failed QA"):
                rps.build_one(self.assets, self.output, "default", "RpsRock", keys_only=False)
        rife.assert_not_called()
        for name in ("RpsRock-keys-qa.json", "RpsRock-qa.json"):
            report = json.loads((self.output / "default" / "QA" / name).read_text())
            self.assertFalse(report["passed"])
            self.assertEqual(report["source_sha256"], rps.sha256(self.source))

    def test_run_rife_contract_and_final_provenance(self):
        self.mocks()

        def fake_rife(executable, model, keys, output, spec, neutral, *, exact_endpoints):
            self.assertEqual(executable, Path("test-rife.exe"))
            self.assertEqual(model, Path("test-model"))
            self.assertTrue(spec.lock_authored_frames)
            self.assertTrue(spec.segmentwise_interpolation)
            self.assertEqual([frame.tobytes() for frame in exact_endpoints], [neutral.tobytes()] * 2)
            output.mkdir(parents=True)
            count = round(spec.duration_seconds * 60) + 1
            for index in range(count):
                frame = neutral if index in (0, count - 1) else Image.new("RGBA", (8, 8), (index, 60, 80, 255))
                frame.save(output / f"frame-{index:04d}.png")
            return count, {"passed": True, "errors": []}

        with patch.object(rps.pipeline, "run_rife", side_effect=fake_rife):
            report = rps.build_one(self.assets, self.output, "default", "RpsRock", keys_only=False,
                                   rife=Path("test-rife.exe"), rife_model=Path("test-model"))
        self.assertTrue(report["passed"])
        self.assertEqual(report["provenance"]["expected_frame_count"], 169)
        self.assertFalse(report["provenance"]["qa_thresholds_relaxed"])
        self.assertEqual(report["temporal_sampling"]["adjacent_duplicate_indexes"], [])
        saved = json.loads((self.output / "default" / "QA" / "RpsRock-qa.json").read_text())
        self.assertEqual(saved["source_sha256"], rps.sha256(self.source))

    def test_rife_exception_retains_failure_report(self):
        self.mocks()
        with patch.object(rps.pipeline, "run_rife", side_effect=RuntimeError("Synthetic interpolation failure")):
            with self.assertRaisesRegex(RuntimeError, "interpolation failure"):
                rps.build_one(self.assets, self.output, "default", "RpsRock", keys_only=False,
                               rife=Path("test-rife.exe"), rife_model=Path("test-model"))
        report = json.loads((self.output / "default" / "QA" / "RpsRock-qa.json").read_text())
        self.assertFalse(report["passed"])
        self.assertIn("Synthetic interpolation failure", report["build_error"])
        self.assertEqual(report["authored_frame_indices"], list(rps.THROW_POSITIONS))

    def test_changed_input_or_new_mapping_is_detected(self):
        info = rps.provenance(self.source, self.neutral_path, tuple(range(16)), None,
                              rps.clip_spec("RpsRock", self.source.name), self.assets)
        rps.unchanged_inputs(info, self.source, self.neutral_path, None)
        self.metadata(list(range(16)))
        with self.assertRaisesRegex(RuntimeError, "appeared during build"):
            rps.unchanged_inputs(info, self.source, self.neutral_path, None)

    def test_cli_missing_rife_fails_before_creating_output(self):
        destination = rps.ROOT / ".codex-build" / "rps-v2-no-write-cli-test"
        self.assertFalse(destination.exists())
        with self.assertRaises(SystemExit) as result:
            rps.main(["--assets", str(self.assets), "--output-root", str(destination), "--outfits", "default", "--clips", "rock"])
        self.assertEqual(result.exception.code, 2)
        self.assertFalse(destination.exists())

    def test_keys_only_rerun_invalidates_previously_passed_final_report(self):
        self.mocks()
        qa_dir = self.output / "default" / "QA"
        qa_dir.mkdir(parents=True)
        (qa_dir / "RpsRock-qa.json").write_text('{"passed":true}', encoding="utf-8")
        rps.build_one(self.assets, self.output, "default", "RpsRock", keys_only=True)
        final_report = json.loads((qa_dir / "RpsRock-qa.json").read_text())
        self.assertFalse(final_report["passed"])
        self.assertEqual(final_report["build_state"], "pending_keys_or_interpolation")

    def test_extra_stale_key_prevents_pass_without_deleting_evidence(self):
        self.mocks()
        key_dir = self.output / "default" / "Keys" / "RpsRock"
        key_dir.mkdir(parents=True)
        stale = key_dir / "key-16.png"
        self.neutral.save(stale)
        with self.assertRaisesRegex(ValueError, "Unexpected stale keys"):
            rps.build_one(self.assets, self.output, "default", "RpsRock", keys_only=True)
        self.assertTrue(stale.exists())


class RpsPromotionTests(unittest.TestCase):
    setUp = RpsPreparationTests.setUp

    def complete_stage(self, clip="RpsRock"):
        source = self.sources / f"default-{rps.CLIP_SOURCES[clip]}-v2.png"
        if not source.exists():
            Image.new("RGBA", (8, 8), (80, 100, 140, 255)).save(source)
        spec = rps.clip_spec(clip, source.name)
        info = rps.provenance(source, self.neutral_path, tuple(range(16)), None, spec, self.assets)
        keys, animations, qa = rps.output_paths(self.output, "default", clip)
        for folder in (keys, animations, qa):
            folder.mkdir(parents=True, exist_ok=True)
        key_frames = [self.neutral if index in (0, 15) else Image.new("RGBA", (8, 8), (40 + index, 60, 80, 255))
                      for index in range(16)]
        for index, image in enumerate(key_frames):
            image.save(keys / f"key-{index:02d}.png")
        count = round(spec.duration_seconds * 60) + 1
        locked = dict(zip(spec.authored_frame_indices, key_frames))
        for index in range(count):
            image = locked.get(index, Image.new("RGBA", (8, 8), (index, 60, 80, 255)))
            image.save(animations / f"frame-{index:04d}.png")
        for suffix, frames in (("keys-qa", 16), ("qa", count)):
            report = rps.annotate({"passed": True, "errors": [], "frame_count": frames,
                                   "preflight": {"passed": True},
                                   "authored_key_frames": {"locked": True, "all_exact": True,
                                                           "frame_indices": list(spec.authored_frame_indices)}}, info, "default", clip)
            rps.pipeline.write_qa_report(qa / f"{clip}-{suffix}.json", report)
        for suffix in ("keys-dark.png", "dark.png", "60fps.gif"):
            self.neutral.save(qa / f"{clip}-{suffix}")
        return keys, animations, qa

    def asset_hashes(self):
        return {str(path.relative_to(self.assets)): rps.sha256(path) for path in self.assets.rglob("*") if path.is_file()}

    def test_bad_final_clip_in_batch_produces_zero_production_writes(self):
        import promote_rps_animation as promotion
        self.complete_stage("RpsRock")
        _, frames, _ = self.complete_stage("RpsPaper")
        (frames / "frame-0168.png").unlink()
        before = self.asset_hashes()
        with patch.object(rps.pipeline, "CANVAS_SIZE", (8, 8)), self.assertRaisesRegex(ValueError, "Missing/extra staged frames"):
            promotion.promote(self.assets, self.output, [("default", "RpsRock"), ("default", "RpsPaper")], repository_root=self.root)
        self.assertEqual(before, self.asset_hashes())
        self.assertFalse((self.output / "PromotionBackups").exists())

    def test_failed_or_stale_report_produces_zero_production_writes(self):
        import promote_rps_animation as promotion
        for field, value in (("passed", False), ("source_sha256", "stale"), ("neutral_sha256", "stale")):
            _, _, qa = self.complete_stage()
            path = qa / "RpsRock-qa.json"
            report = json.loads(path.read_text())
            report[field] = value
            path.write_text(json.dumps(report), encoding="utf-8")
            before = self.asset_hashes()
            with self.subTest(field=field), patch.object(rps.pipeline, "CANVAS_SIZE", (8, 8)), self.assertRaises(ValueError):
                promotion.promote(self.assets, self.output, [("default", "RpsRock")], repository_root=self.root)
            self.assertEqual(before, self.asset_hashes())
        self.assertFalse((self.output / "PromotionBackups").exists())

    def test_changed_locked_frame_rejected_without_production_writes(self):
        import promote_rps_animation as promotion
        _, frames, _ = self.complete_stage()
        self.neutral.save(frames / "frame-0008.png")
        before = self.asset_hashes()
        with patch.object(rps.pipeline, "CANVAS_SIZE", (8, 8)), self.assertRaisesRegex(ValueError, "Authored key changed"):
            promotion.promote(self.assets, self.output, [("default", "RpsRock")], repository_root=self.root)
        self.assertEqual(before, self.asset_hashes())

    def test_missing_review_artifact_produces_zero_production_writes(self):
        import promote_rps_animation as promotion
        _, _, qa = self.complete_stage()
        (qa / "RpsRock-dark.png").unlink()
        before = self.asset_hashes()
        with patch.object(rps.pipeline, "CANVAS_SIZE", (8, 8)), self.assertRaisesRegex(ValueError, "Missing staged review artifact"):
            promotion.promote(self.assets, self.output, [("default", "RpsRock")], repository_root=self.root)
        self.assertEqual(before, self.asset_hashes())
        self.assertFalse((self.output / "PromotionBackups").exists())

    def test_non_repository_assets_destination_is_rejected_before_writes(self):
        import promote_rps_animation as promotion
        self.complete_stage()
        wrong = self.root / "OtherAssets"
        wrong.mkdir()
        with self.assertRaisesRegex(ValueError, "exact Assets directory"):
            promotion.promote(wrong, self.output, [("default", "RpsRock")], repository_root=self.root)
        self.assertEqual(list(wrong.iterdir()), [])

    def test_valid_promotion_updates_only_selected_rps_and_preserves_backup(self):
        import promote_rps_animation as promotion
        self.complete_stage()
        target = self.assets / "Animations" / "RpsRock"
        target.mkdir(parents=True)
        old = target / "frame-0000.png"
        Image.new("RGBA", (8, 8), (1, 2, 3, 255)).save(old)
        old_digest = rps.sha256(old)
        stale = target / "frame-0200.png"
        self.neutral.save(stale)
        yawn = self.assets / "Animations" / "Yawn" / "frame-0000.png"
        yawn.parent.mkdir()
        self.neutral.save(yawn)
        before_yawn, before_neutral = rps.sha256(yawn), rps.sha256(self.neutral_path)
        with patch.object(rps.pipeline, "CANVAS_SIZE", (8, 8)):
            report = promotion.promote(self.assets, self.output, [("default", "RpsRock")], repository_root=self.root)
        self.assertTrue(report["passed"])
        self.assertFalse(stale.exists())
        self.assertEqual(before_yawn, rps.sha256(yawn))
        self.assertEqual(before_neutral, rps.sha256(self.neutral_path))
        saved = list((self.output / "PromotionBackups").glob("*/previous/Animations/RpsRock/frame-0000.png"))
        self.assertEqual(len(saved), 1)
        self.assertEqual(rps.sha256(saved[0]), old_digest)

    def test_copy_failure_rolls_back_existing_production_files(self):
        import promote_rps_animation as promotion
        self.complete_stage()
        target = self.assets / "AnimationKeys" / "RpsRock"
        target.mkdir(parents=True)
        Image.new("RGBA", (8, 8), (1, 2, 3, 255)).save(target / "key-00.png")
        before = self.asset_hashes()
        real_replace = promotion.os.replace
        calls = 0

        def fail_second(source, destination):
            nonlocal calls
            calls += 1
            if calls == 2:
                raise OSError("Synthetic promotion disk failure")
            real_replace(source, destination)

        with patch.object(rps.pipeline, "CANVAS_SIZE", (8, 8)), patch.object(promotion.os, "replace", side_effect=fail_second):
            with self.assertRaisesRegex(OSError, "disk failure"):
                promotion.promote(self.assets, self.output, [("default", "RpsRock")], repository_root=self.root)
        self.assertEqual(before, self.asset_hashes())


if __name__ == "__main__":
    unittest.main(verbosity=2)
