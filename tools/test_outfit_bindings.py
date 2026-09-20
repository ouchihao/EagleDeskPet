"""Read-only regression checks against actual articulated source poses (no artwork rewriting)."""
import unittest

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
                    for slot in ('head', 'body'):
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


if __name__ == '__main__':
    unittest.main()
