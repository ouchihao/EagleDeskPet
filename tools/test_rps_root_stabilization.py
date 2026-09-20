"""CPU-only endpoint calibration regressions; never rewrite pet artwork."""
from __future__ import annotations

from dataclasses import replace
from contextlib import ExitStack
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
from PIL import Image, ImageDraw

import prepare_generated_sheets as pipeline


def fixture(*, wide_foot=False, faint_sole=False):
    image = Image.new('RGBA', (40, 50))
    draw = ImageDraw.Draw(image)
    draw.rectangle((12, 24, 27, 40), fill=(150, 90, 45, 255))
    draw.rectangle((8, 6, 31, 26), fill=(250, 250, 245, 255))
    draw.rectangle((13, 13, 15, 17), fill=(15, 15, 15, 255))
    draw.rectangle((24, 13, 26, 17), fill=(15, 15, 15, 255))
    draw.rectangle((17, 19, 24, 22), fill=(235, 185, 5, 255))
    draw.rectangle((12, 40, 27 if wide_foot else 26, 43), fill=(240, 150, 0, 255))
    if faint_sole:
        draw.rectangle((12, 44, 27 if wide_foot else 26, 44), fill=(240, 150, 0, 40))
    return image


def measure_fixture_root(image):
    rgba = np.asarray(image)
    mask = ((rgba[:, :, 0] > 200) & (rgba[:, :, 1] > 80) & (rgba[:, :, 1] < 180)
            & (rgba[:, :, 2] < 50) & (rgba[:, :, 3] >= 32))
    mask[:35] = False
    ys, xs = np.where(mask)
    return pipeline.RootAnchor(float((xs.min() + xs.max()) / 2), float(ys.max()), len(xs), len(xs))


def translate_fixture(image, x, y):
    # Test the coordinate transform, not the separate production matte cleaner.
    return image.transform(image.size, Image.Transform.AFFINE, (1, 0, -x, 0, 1, -y), Image.Resampling.BICUBIC)


def premultiplied(image):
    value = np.asarray(image, dtype=float) / 255
    return value[:, :, :3] * value[:, :, 3:4]


class AuthoredRootCalibrationTests(unittest.TestCase):
    def setUp(self):
        self.anchor = patch.object(pipeline, 'measure_root_anchor', side_effect=measure_fixture_root)
        self.translate = patch.object(pipeline, 'translate_rgba', side_effect=translate_fixture)
        self.canvas = patch.object(pipeline, 'CANVAS_SIZE', (40, 50))
        for context in (self.anchor, self.translate, self.canvas):
            context.start()
            self.addCleanup(context.stop)

    def test_flag_is_opt_in_and_legacy_stabilization_still_centers_to_canonical(self):
        spec = pipeline.ClipSpec('Unchanged', 'unused.png', 2)
        self.assertFalse(spec.authored_root_calibration)
        canonical, key = fixture(wide_foot=True), fixture()
        with patch.object(pipeline, 'stabilize_authored_root_frames') as calibrated:
            _, report = pipeline.stabilize_frames([key.copy() for _ in range(9)], canonical)
        calibrated.assert_not_called()
        self.assertEqual(report['translation']['per_frame_offsets'], [[0.5, 0.0]] * 9)
        self.assertNotIn('authored_root_calibration', report)

    def test_half_pixel_locked_key_reverse_pulse_is_removed_without_unlocking(self):
        canonical, key = fixture(wide_foot=True), fixture()
        frames = [key.copy() for _ in range(9)]
        old, _ = pipeline.stabilize_frames(frames, canonical)
        new, report = pipeline.stabilize_frames(frames, canonical, authored_frame_indices=(0, 4, 8))
        for index in (0, 4, 8):
            old[index] = key.copy()
            new[index] = key.copy()
        previous, middle, following = map(premultiplied, old[3:6])
        first_step, second_step = middle - previous, following - middle
        active = np.max(abs(first_step), axis=2) > .02
        self.assertGreater(int(active.sum()), 25)
        self.assertTrue(np.all(np.sum(first_step * second_step, axis=2)[active] < 0))
        self.assertTrue(all(np.array_equal(np.asarray(new[index]), np.asarray(key)) for index in range(9)))
        self.assertEqual(report['translation']['per_frame_offsets'], [[0.0, 0.0]] * 9)
        self.assertEqual(report['authored_root_calibration']['maximum_authored_translation'], 0)

    def test_alpha_sole_tightening_does_not_move_opaque_body_one_pixel(self):
        key = fixture(faint_sole=True)
        array = np.array(key)
        alpha = np.clip((array[:, :, 3].astype(float) - 42) * 255 / 185, 0, 255).astype('uint8')
        alpha[alpha < 10] = 0
        array[:, :, 3] = alpha
        reconstructed = Image.fromarray(array)
        self.assertEqual(measure_fixture_root(key).foot_contact_y, 44)
        self.assertEqual(measure_fixture_root(reconstructed).foot_contact_y, 43)
        frames = [reconstructed.copy() for _ in range(9)]
        old, old_report = pipeline.stabilize_frames(frames, key)
        new, report = pipeline.stabilize_frames(frames, key, authored_frame_indices=(0, 4, 8))
        self.assertEqual(old_report['translation']['per_frame_offsets'][3][1], 1)
        self.assertEqual(report['translation']['per_frame_offsets'], [[0.0, 0.0]] * 9)
        for index in (0, 4, 8):
            new[index] = key.copy()
        # The alpha-only sole change remains at the sole; eyes/body do not hop.
        body = (slice(5, 35), slice(5, 35))
        self.assertGreater(float(np.abs(premultiplied(old[3])[body] - premultiplied(key)[body]).sum()), 1)
        self.assertEqual(float(np.abs(premultiplied(new[3])[body] - premultiplied(new[4])[body]).sum()), 0)

    def test_real_intra_segment_displacement_is_still_corrected(self):
        key = fixture()
        frames = [key.copy() for _ in range(13)]
        for index in (4, 5, 6, 7, 8):
            frames[index] = translate_fixture(key, 1, 0)
        result, report = pipeline.stabilize_frames(frames, key, authored_frame_indices=(0, 12))
        self.assertEqual(report['translation']['per_frame_offsets'][6], [-1.0, 0.0])
        self.assertEqual(report['translation']['per_frame_offsets'][0], [0.0, 0.0])
        self.assertEqual(report['translation']['per_frame_offsets'][12], [0.0, 0.0])
        self.assertTrue(np.array_equal(np.asarray(result[6]), np.asarray(key)))

    def test_endpoint_baseline_is_interpolated_and_offsets_are_zero_at_every_key(self):
        canonical = fixture(wide_foot=True)
        frames = [fixture() if index < 4 or index >= 7 else fixture(wide_foot=True) for index in range(9)]
        _, report = pipeline.stabilize_frames(frames, canonical, authored_frame_indices=(0, 4, 8))
        baseline = report['authored_root_calibration']['endpoint_baseline_offsets']
        self.assertEqual([offset[0] for offset in baseline], [.5, .375, .25, .125, 0, .125, .25, .375, .5])
        offsets = report['translation']['per_frame_offsets']
        self.assertTrue(all(offsets[index] == [0, 0] for index in (0, 4, 8)))

    def test_exact_endpoints_and_residual_stage_preserve_calibration(self):
        key = fixture(faint_sole=True)
        reconstructed = fixture()
        frames = [reconstructed.copy() for _ in range(9)]
        result, _ = pipeline.stabilize_frames(frames, key, exact_endpoints=(key, key), authored_frame_indices=(0, 4, 8))
        for index in (0, 8):
            self.assertTrue(np.array_equal(np.asarray(result[index]), np.asarray(key)))
        before = [frame.tobytes() for frame in result]
        result, report = pipeline.preserve_calibrated_root_residuals(result, key, {0, 4, 8})
        self.assertEqual([frame.tobytes() for frame in result], before)
        self.assertEqual(report['corrected_frame_count'], 0)
        self.assertEqual(report['maximum_foot_contact_y_error'], 1)
        self.assertFalse(report['qa_thresholds_relaxed'])
        # Canonical residual correction would reintroduce the error we avoided.
        legacy, legacy_report = pipeline.correct_final_root_residuals(result, key, {0, 4, 8})
        self.assertGreater(legacy_report['corrected_frame_count'], 0)
        self.assertNotEqual(legacy[3].tobytes(), result[3].tobytes())

    def test_bad_registered_key_is_not_exempt_from_existing_root_qa(self):
        canonical = fixture()
        wrong_key = translate_fixture(canonical, 6, 0)
        frames, _ = pipeline.stabilize_frames([wrong_key.copy() for _ in range(3)], canonical, authored_frame_indices=(0, 2))
        report = pipeline.qa_frame_sequence(frames, 3, canonical, wrong_key, wrong_key)
        self.assertFalse(report['passed'])
        self.assertTrue(any('torso root x error' in error for error in report['errors']), report['errors'])
        self.assertEqual(pipeline.ROOT_X_TOLERANCE_PIXELS, 4.0)
        self.assertEqual(pipeline.ROOT_Y_TOLERANCE_PIXELS, 1.5)

    def test_invalid_endpoint_indexes_fail_closed(self):
        frames = [fixture() for _ in range(9)]
        for indexes in ((0,), (1, 8), (0, 7), (0, 4, 4, 8), (0, 9), (False, 8), (0, -1, 8)):
            with self.subTest(indexes=indexes), self.assertRaisesRegex(ValueError, 'increasing endpoint'):
                pipeline.stabilize_frames(frames, fixture(), authored_frame_indices=indexes)

    def test_incompatible_clip_flags_reject_before_any_output_cleanup(self):
        base = pipeline.ClipSpec('RpsRock', 'test.png', 2.8, authored_root_calibration=True)
        for spec in (base, replace(base, lock_authored_frames=True), replace(base, segmentwise_interpolation=True)):
            with self.subTest(spec=spec), patch.object(pipeline, 'clear_generated_pngs') as clear:
                with self.assertRaisesRegex(ValueError, 'requires locked authored frames'):
                    pipeline.run_rife(Path('unused'), Path('unused'), Path('unused'), Path('unused'), spec, fixture())
                clear.assert_not_called()

    def test_run_rife_routes_calibration_and_does_not_recenter_after_locking(self):
        # Exercise the actual staging pipeline with a deterministic CPU RIFE
        # stand-in, not production inputs or a GPU. Image QA is separately
        # covered; this checks wiring and exact key ownership end to end.
        key, canonical = fixture(), fixture(wide_foot=True)
        spec = pipeline.ClipSpec('RpsRock', 'test.png', 8 / 60, columns=3, rows=1,
                                 source_pose_count=3, authored_frame_indices=(0, 4, 8),
                                 lock_authored_frames=True, segmentwise_interpolation=True,
                                 authored_root_calibration=True)

        def fake_rife(command, **kwargs):
            inputs = Path(command[command.index('-i') + 1])
            outputs = Path(command[command.index('-o') + 1])
            count = int(command[command.index('-n') + 1])
            with Image.open(inputs / '00000000.png') as first:
                frame = first.copy()
            for index in range(count):
                frame.save(outputs / f'{index:08d}.png')

        with tempfile.TemporaryDirectory(prefix='eagle-calibrated-rife-') as directory, ExitStack() as stack:
            root = Path(directory)
            keys, outputs = root / 'keys', root / 'frames'
            keys.mkdir()
            for index in range(3):
                key.save(keys / f'key-{index:02d}.png')
            stack.enter_context(patch.object(pipeline.subprocess, 'run', side_effect=fake_rife))
            stack.enter_context(patch.object(pipeline, 'selective_matte_defringe', side_effect=lambda frame: frame))
            stack.enter_context(patch.object(pipeline, 'add_rgb_edge_bleed', side_effect=lambda frame: frame))
            stack.enter_context(patch.object(pipeline, 'keep_character_component', side_effect=lambda alpha, **kwargs: alpha))
            stack.enter_context(patch.object(pipeline, 'enforce_detached_component_policy', side_effect=lambda frames, spec: (frames, {})))
            stack.enter_context(patch.object(pipeline, 'inspect_detached_component_policy', return_value={}))
            stack.enter_context(patch.object(pipeline, 'clean_bomb_interpolated_matte', side_effect=lambda frames, *args: (frames, {})))
            stack.enter_context(patch.object(pipeline, 'qa_frame_sequence', return_value={'passed': True, 'errors': []}))
            legacy_residual = stack.enter_context(patch.object(pipeline, 'correct_final_root_residuals'))
            count, report = pipeline.run_rife(Path('fake-rife'), Path('fake-model'), keys, outputs, spec,
                                               canonical, exact_endpoints=(key, key))
            legacy_residual.assert_not_called()
            self.assertEqual(count, 9)
            self.assertTrue(report['authored_key_frames']['locked'])
            self.assertTrue(report['authored_key_frames']['all_exact'])
            self.assertTrue(report['stabilization']['authored_root_calibration']['enabled'])
            self.assertEqual(report['stabilization']['translation']['per_frame_offsets'], [[0.0, 0.0]] * 9)
            self.assertTrue(report['final_root_residual_correction']['authored_root_calibration'])
            self.assertTrue(report['calibrated_outer_edge_rgb_cleanup']['enabled'])
            for index in (0, 4, 8):
                with Image.open(outputs / f'frame-{index:04d}.png') as final:
                    self.assertTrue(np.array_equal(np.asarray(final), np.asarray(key)))

    def test_near_opaque_matte_seed_repair_changes_only_polluted_outer_rgb(self):
        image = Image.new('RGBA', (40, 50))
        draw = ImageDraw.Draw(image)
        brown = (170, 90, 30)
        draw.rectangle((8, 8, 25, 30), fill=(*brown, 255))
        draw.rectangle((11, 11, 22, 27), fill=(250, 250, 245, 255))
        for y, alpha in enumerate(range(239, 245), 10):
            image.putpixel((7, y), (20, 200, 230, alpha))
        image.putpixel((26, 20), (*brown, 100))  # Already valid antialiasing.
        image.putpixel((15, 15), (20, 200, 230, 242))  # Interior is never repaired.
        before = np.asarray(image)
        prior_halo = pipeline.measure_dark_background_halo(image)
        self.assertEqual(prior_halo['color_polluted_edge_pixels'], 6)
        frames, report = pipeline.repair_calibrated_outer_edge_rgb([image, image], {0})
        self.assertEqual(frames[0].tobytes(), image.tobytes())
        after = np.asarray(frames[1])
        self.assertTrue(np.array_equal(before[:, :, 3], after[:, :, 3]))
        self.assertTrue(np.array_equal(before[before[:, :, 3] >= 245], after[before[:, :, 3] >= 245]))
        self.assertEqual(frames[1].getpixel((26, 20)), image.getpixel((26, 20)))
        self.assertEqual(frames[1].getpixel((15, 15)), image.getpixel((15, 15)))
        self.assertEqual(report['recolored_pixel_count'], 6)
        self.assertFalse(report['alpha_modified'])
        self.assertFalse(report['opaque_pixels_modified'])
        self.assertFalse(report['authored_frames_modified'])
        self.assertFalse(report['qa_thresholds_relaxed'])
        self.assertEqual(pipeline.measure_dark_background_halo(frames[1])['maximum_visible_pixels'], 0)
        second, repeated = pipeline.repair_calibrated_outer_edge_rgb(frames, {0})
        self.assertEqual(second[1].tobytes(), frames[1].tobytes())
        self.assertEqual(repeated['recolored_pixel_count'], 0)

    def test_rgb_repair_does_not_hide_unsupported_alpha_or_change_coverage(self):
        image = Image.new('RGBA', (40, 50))
        ImageDraw.Draw(image).rectangle((8, 8, 25, 30), fill=(170, 90, 30, 255))
        image.putpixel((35, 40), (220, 220, 30, 160))
        frames, report = pipeline.repair_calibrated_outer_edge_rgb([image], set())
        self.assertEqual(frames[0].tobytes(), image.tobytes())
        self.assertEqual(report['recolored_pixel_count'], 0)
        self.assertGreater(pipeline.measure_dark_background_halo(frames[0])['unsupported_low_alpha_pixels'], 0)


if __name__ == '__main__':
    unittest.main(verbosity=2)
