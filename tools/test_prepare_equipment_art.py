from pathlib import Path
import json
import unittest

import numpy as np
from PIL import Image, ImageDraw

import prepare_equipment_art as art


def atlas_fixture():
    image = Image.new('RGBA', (1536, 1024), (249, 162, 51, 0))
    draw = ImageDraw.Draw(image)
    # torso crosses the nominal x=512 grid border, as the real cow head does.
    bounds = [(390, 120, 560, 440), (640, 150, 930, 440), (1120, 150, 1430, 440),
              (20, 560, 558, 920), (630, 580, 990, 910), (1080, 720, 1230, 920), (1330, 720, 1480, 920)]
    for index, bound in enumerate(bounds):
        draw.rectangle(bound, fill=(20 + index * 20, 80, 130, 254))
    draw.rectangle((140, 680, 400, 820), fill=(2, 200, 20, 0))
    return image, bounds


class EquipmentArtTests(unittest.TestCase):
    def test_alpha_only_keeps_white_foreground_and_ignores_hidden_glow(self):
        image = Image.new('RGBA', (160, 100), (230, 40, 100, 0))
        ImageDraw.Draw(image).rectangle((20, 20, 100, 75), fill=(255, 255, 255, 210))
        pieces, qa = art.principal_components(image, 1)
        self.assertEqual(pieces[0]['bounds'], (20, 20, 101, 76))
        self.assertEqual(pieces[0]['image'].getpixel((0, 0)), (255, 255, 255, 210))
        self.assertEqual(qa['discarded_pixel_count'], 0)

    def test_atlas_components_cross_cells_without_clipping(self):
        image, _ = atlas_fixture()
        pieces, qa = art.atlas_parts(image)
        self.assertEqual(tuple(pieces), art.COMPONENT_NAMES)
        self.assertEqual(pieces['torso'].size, (171, 321))
        self.assertEqual(qa['components']['torso']['bounds'], (390, 120, 561, 441))
        self.assertEqual(pieces['head'].size, (539, 361))

    def test_holes_and_semialpha_are_preserved(self):
        pieces, _ = art.atlas_parts(atlas_fixture()[0])
        self.assertEqual(pieces['head'].getpixel((200, 160))[3], 0)
        self.assertEqual(pieces['head'].getpixel((10, 10))[3], 254)

    def test_boots_are_left_to_right_not_by_area_rank(self):
        pieces, _ = art.atlas_parts(atlas_fixture()[0])
        self.assertEqual(pieces['left-boot'].getpixel((0, 0))[0], 120)
        self.assertEqual(pieces['right-boot'].getpixel((0, 0))[0], 140)

    def test_disconnected_low_alpha_specks_are_recorded(self):
        image, _ = atlas_fixture()
        image.putpixel((1525, 1000), (200, 30, 100, 1))
        _, qa = art.atlas_parts(image)
        self.assertEqual(qa['discarded_pixel_count'], 1)
        self.assertGreater(qa['discarded_alpha_mass_fraction'], 0)

    def test_substantial_eighth_piece_is_not_silently_lost(self):
        image, _ = atlas_fixture()
        ImageDraw.Draw(image).rectangle((1030, 480, 1230, 660), fill=(200, 20, 20, 254))
        with self.assertRaises(ValueError): art.atlas_parts(image)

    def test_incomplete_atlas_rejected(self):
        image, _ = atlas_fixture()
        ImageDraw.Draw(image).rectangle((1330, 720, 1480, 920), fill=(0, 0, 0, 0))
        with self.assertRaises(ValueError): art.atlas_parts(image)

    def test_piece_long_side_is_capped_and_aspect_preserved(self):
        result = art.small_piece(Image.new('RGBA', (800, 400), (20, 30, 40, 254)))
        self.assertEqual(result.size, (512, 256))

    def test_premultiplied_resize_does_not_leak_transparent_white(self):
        image = Image.new('RGBA', (20, 20), (255, 255, 255, 0))
        ImageDraw.Draw(image).rectangle((5, 5, 14, 14), fill=(220, 0, 0, 255))
        result = np.asarray(art.resized(image, (11, 11)))
        active = result[:, :, 3] > 0
        self.assertTrue(np.all(result[:, :, 1:3][active] == 0))

    def test_desk_partition_is_disjoint_and_exact(self):
        source = Image.new('RGBA', (500, 200), (160, 90, 25, 254))
        full = art.registered(source, (39, 249, 345, 345))
        back, front = art.desk_layers(full)
        self.assertEqual(full.size, (384, 346))
        self.assertEqual(back.getchannel('A').getbbox(), (39, 249, 345, 276))
        self.assertEqual(front.getchannel('A').getbbox(), (39, 276, 345, 345))
        np.testing.assert_array_equal(np.asarray(Image.alpha_composite(back, front)), np.asarray(full))

    def test_production_sources_and_components_present(self):
        for name in art.SOURCE_IMPORTS:
            with self.subTest(source=name):
                self.assertTrue((art.SOURCES / name).is_file())
        for outfit in ('Ox', 'Hero', 'Astronaut'):
            for component in art.COMPONENT_NAMES:
                with Image.open(art.ASSETS / f'Outfits/{outfit}/Layers/{component}.png') as image:
                    self.assertEqual(image.mode, 'RGBA')
                    self.assertLessEqual(max(image.size), 512)
                    self.assertIsNotNone(image.getchannel('A').getbbox())

    def test_manifest_registration_contract_and_paths(self):
        data = json.loads((art.ASSETS / 'SceneProps/work-scenes.json').read_text(encoding='utf-8'))
        self.assertEqual(data['scenes'][0]['deskSurfaceAnchor'], {'x': 192, 'y': 276})
        for kind, names in [('desks', ('noodle', 'cloud', 'boardroom')), ('computers', ('server', 'gold', 'ultrabook'))]:
            for name in names:
                identifier = ('desk.' if kind == 'desks' else 'computer.') + name
                item = next(item for item in data[kind] if item['id'] == identifier)
                for key in (('back', 'front') if kind == 'desks' else ('image',)):
                    with Image.open(art.ROOT / 'DuckDeskPet' / item[key]) as image:
                        self.assertEqual(image.size, art.CANVAS)
                        self.assertIsNotNone(image.getchannel('A').getbbox())


if __name__ == '__main__':
    unittest.main()
