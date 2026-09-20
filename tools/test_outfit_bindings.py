"""Read-only regression checks against actual articulated source poses (no artwork rewriting)."""
import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

import numpy as np
from PIL import Image

import prepare_outfit_bindings as p


class OutfitBindingsTests(unittest.TestCase):
    assets = p.ROOT / 'DuckDeskPet' / 'Assets'

    def image(self, directory, index):
        return Image.open(self.assets / 'Animations' / directory / f'frame-{index:04d}.png').convert('RGBA')

    def test_limb_regions_never_flood_the_belly_after_softened_outline(self):
        for directory, index in [('WorkExit', 90), ('BusyExitV2', 90), ('HungryEnter', 30), ('HungryLoop', 30)]:
            with self.subTest(directory=directory):
                atlas, _, report = p.bind(self.image(directory, index))
                region = np.asarray(atlas)[:, :, 0]
                self.assertGreater(report['torsoPixels'], 1500)
                self.assertGreater(np.count_nonzero(region == 6), 350)
                self.assertGreater(np.count_nonzero(region == 7), 350)

    def test_all_action_families_have_moving_semantics_inside_the_actor(self):
        import json
        manifest = json.loads((self.assets / 'actions.json').read_text(encoding='utf-8-sig'))
        for action in manifest['actions']:
            for index in {0, action['frameCount'] // 2, action['frameCount'] - 1}:
                with self.subTest(clip=action['clip'], frame=index):
                    source = Image.open(self.assets.parent / action['directory'] / f'frame-{index:04d}.png').convert('RGBA')
                    atlas, transform, report = p.bind(source)
                    data = np.asarray(atlas); alpha = np.asarray(source)[:, :, 3]
                    self.assertFalse(np.any((data[:, :, 0] != 0) & (alpha == 0)))
                    self.assertTrue(np.all(data[:, :, 0] <= 7))
                    self.assertGreater(report['torsoPixels'], 600)
                    for slot in ('head', 'body', 'eyes'):
                        t = transform[slot]
                        self.assertGreater(abs(t[2] * t[5] - t[3] * t[4]), 16)

    def test_canonical_seams_are_identical_for_every_standing_action(self):
        neutral = Image.open(self.assets / 'mascot-animated-neutral.png').convert('RGBA')
        reference, transform, _ = p.bind(neutral)
        for directory, final in [('Yawn', 120), ('Shy', 120), ('Eat', 120), ('RpsRock', 168), ('RpsWin', 144)]:
            for index in (0, final):
                source = self.image(directory, index)
                if not np.array_equal(np.asarray(source), np.asarray(neutral)):
                    continue  # Some legacy authored endpoints have residual art; do not invent equality.
                atlas, actual, _ = p.bind(source)
                self.assertEqual(actual, transform)
                self.assertTrue(np.array_equal(atlas, reference))

    def test_shy_hands_move_up_without_clothing_the_white_face(self):
        source = self.image('Shy', 60)
        atlas, transform, report = p.bind(source)
        rgba = np.asarray(source); mask = np.asarray(atlas)[:, :, 0]
        white = np.all(rgba[:, :, :3] > 235, axis=2) & (p.YY < 205)
        self.assertFalse(np.any(np.isin(mask, [1, 2, 3, 4, 5]) & white))
        self.assertGreater(np.count_nonzero(np.isin(mask, [6, 7]) & (p.YY < 220)), 700)
        self.assertGreater(abs(transform['head'][3]), 5)
        self.assertTrue(any(y < 224 for _, y in report['handAnchors']))

    def test_bowl_and_fingers_are_not_material_textures(self):
        source = self.image('Eat', 60)
        atlas, _, _ = p.bind(source)
        data = np.asarray(atlas)
        self.assertEqual(data[244, 194, 0], 7)
        self.assertEqual(data[244, 194, 3], 255)
        for directory in ('RpsPaper', 'RpsScissors'):
            atlas, _, _ = p.bind(self.image(directory, 96))
            region = np.asarray(atlas)[:, :, 0]
            self.assertGreater(np.count_nonzero((region == 6) & (p.YY < 230)), 350)

    def test_uvs_are_spatial_gradients_not_a_static_decoration(self):
        atlas, _, _ = p.bind(Image.open(self.assets / 'mascot-animated-neutral.png'))
        data = np.asarray(atlas); torso = data[:, :, 0] == 1
        self.assertGreater(np.ptp(data[:, :, 1][torso]), 160)
        self.assertGreater(np.ptp(data[:, :, 2][torso]), 160)
        self.assertGreater(np.count_nonzero(data[:, :, 0] == 4), 200)
        self.assertGreater(np.count_nonzero(data[:, :, 0] == 5), 200)

    def test_glasses_follow_actual_eye_band_not_the_head_box(self):
        # Native-pixel eye centres from the accepted Office source poses. The head-box
        # approximation formerly put Yawn's lenses across the mouth and Work's above eyes.
        cases = [('Yawn', 60, 196, 145), ('Shy', 30, 176, 159), ('Eat', 30, 193, 150),
                 ('WorkLoop', 0, 195, 176), ('BusyLoop', 60, 195, 168), ('RpsLose', 60, 198, 161)]
        for directory, index, expected_x, expected_y in cases:
            with self.subTest(clip=directory, frame=index):
                source = self.assets / 'Outfits' / 'Office' / 'Animations' / directory / f'frame-{index:04d}.png'
                measured = p.measure(Image.open(source))
                cx, cy, width, height, _ = measured['eyes']
                self.assertAlmostEqual(cx, expected_x, delta=2)
                self.assertAlmostEqual(cy, expected_y, delta=2)
                self.assertAlmostEqual(height / width, 173 / 512)
                self.assertNotEqual(measured['eyes'], measured['head'])

    def test_eye_retarget_only_changes_binding_metadata(self):
        source = self.assets / 'Outfits' / 'Office' / 'Animations' / 'Yawn' / 'frame-0060.png'
        data = source.read_bytes()
        frame = dict(source=source.relative_to(self.assets.parent).as_posix(),
                     sha256=hashlib.sha256(data).hexdigest(), map='maps/Yawn/0060.png',
                     head=[1, 2, 100, 0, 0, 100], body=[3, 4, 100, 0, 0, 100])
        with tempfile.TemporaryDirectory(prefix='eagle-eye-retarget-') as temporary:
            archive = Path(temporary) / 'Office-v1.zip'
            with zipfile.ZipFile(archive, 'w') as original:
                original.writestr('bindings.json', json.dumps({'frames': {'Yawn/0060': frame}}))
                original.writestr(frame['map'], data)
            original_bytes = archive.read_bytes()
            target_dir = Path(temporary) / 'updated'
            p.retarget_eyes(self.assets, archive, target_dir)
            self.assertEqual(archive.read_bytes(), original_bytes)
            with zipfile.ZipFile(target_dir / archive.name) as updated:
                self.assertEqual(updated.read(frame['map']), data)
                actual = json.loads(updated.read('bindings.json'))['frames']['Yawn/0060']
                self.assertEqual({key: value for key, value in actual.items() if key != 'eyes'}, frame)
                self.assertEqual(actual['eyes'], p.affine(*p.measure(Image.open(source))['eyes']))

    def test_anchor_smoothing_keeps_canonical_endpoints_and_reduces_detector_pulses(self):
        values = np.zeros((31, 5), float)
        values[:, 0] = np.linspace(180, 190, 31)
        values[15, 0] += 7  # Single-frame component-detector quantisation, not authored motion.
        smoothed = p.smooth_anchors(values)
        np.testing.assert_array_equal(smoothed[[0, -1]], values[[0, -1]])
        self.assertLess(abs(smoothed[15, 0] - 185), 1)
        self.assertLess(np.max(np.abs(np.diff(smoothed[:, 0]))), 1)


if __name__ == '__main__':
    unittest.main()
