"""Regression coverage for routing legacy builders to the authored RPS v2 pipeline."""
from __future__ import annotations

import argparse
import contextlib
import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image

import prepare_expansion_assets as expansion
import prepare_office_outfit as office
import prepare_hoodie_outfit as hoodie


ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / 'DuckDeskPet' / 'Assets'


class RpsLegacyGuardTests(unittest.TestCase):
    def test_expansion_rps_rejects_before_reading_or_drawing(self):
        with patch.object(expansion, 'extract_native_cells') as extract:
            with self.assertRaisesRegex(ValueError, 'prepare_rps_animation.py'):
                expansion.prepare(Path('does-not-exist'), 'Rps', Image.new('RGBA', (1, 1)))
            extract.assert_not_called()

    def test_expansion_cli_rejects_before_resolving_assets(self):
        with patch('sys.argv', ['prepare_expansion_assets.py', '--assets', 'does-not-exist', '--family', 'Rps']):
            with contextlib.redirect_stderr(io.StringIO()) as output:
                with self.assertRaises(SystemExit) as failure:
                    expansion.main()
        self.assertEqual(failure.exception.code, 2)
        self.assertIn('no legacy RPS assets have been written', output.getvalue())

    def test_explicit_outfit_requests_reject_before_neutral_write(self):
        selections = [(['Rps'], None), (None, ['RpsRock']), (['Yawn'], ['Yawn', 'RpsLose'])]
        for module in (office, hoodie):
            for groups, clips in selections:
                with self.subTest(outfit=module.__name__, groups=groups, clips=clips):
                    with patch.object(module, 'prepare_neutral') as neutral:
                        with self.assertRaisesRegex(ValueError, 'no legacy outfit assets have been written'):
                            module.build(Path('does-not-exist'), argparse.Namespace(groups=groups, clips=clips))
                        neutral.assert_not_called()

    def test_default_build_skips_legacy_rps_mapping(self):
        for module in (office, hoodie):
            with self.subTest(outfit=module.__name__), tempfile.TemporaryDirectory(prefix='eagle-rps-guard-') as directory:
                assets = Path(directory)
                args = argparse.Namespace(groups=None, clips=None)
                with patch.object(module, 'prepare_neutral', return_value=Image.new('RGBA', (1, 1))):
                    with patch.object(module, 'load_mapping', return_value={'Rps': {'mapping': {'RpsRock': [0, 1, 2, 3, 0]}}}):
                        with patch.object(module, 'extract_native_cells') as extract:
                            with contextlib.redirect_stdout(io.StringIO()):
                                module.build(assets, args)
                            extract.assert_not_called()
                self.assertEqual(list(assets.rglob('*.png')), [])

    def test_reference_generation_does_not_read_or_assemble_rps_keys(self):
        for module, outfit in ((office, 'Office'), (hoodie, 'Hoodie')):
            with self.subTest(outfit=outfit), tempfile.TemporaryDirectory(prefix='eagle-rps-refs-') as directory:
                assets = Path(directory)
                neutral = Image.new('RGBA', (12, 12), (90, 60, 30, 255))
                neutral.save(assets / 'mascot-animated-neutral.png')
                keys = assets / 'AnimationKeys' / 'Yawn'
                keys.mkdir(parents=True)
                neutral.save(keys / 'key-00.png')
                with patch.object(module, 'GROUPS', {'Yawn': ['Yawn'], 'Rps': list(office.RPS_TIMES)}):
                    with contextlib.redirect_stdout(io.StringIO()):
                        module.make_references(assets)
                refs = assets / 'AnimationSources' / f'{outfit}References'
                self.assertEqual(set(json.loads((refs / 'mapping.json').read_text())), {'Yawn'})
                self.assertFalse((refs / 'rps-reference.png').exists())

    def test_mapping_accepts_both_historical_and_routed_groups(self):
        for module, outfit in ((office, 'Office'), (hoodie, 'Hoodie')):
            original = json.loads((ASSETS / 'AnimationSources' / f'{outfit}References' / 'mapping.json').read_text())
            for include_rps in (True, False):
                with self.subTest(outfit=outfit, legacy=include_rps), tempfile.TemporaryDirectory(prefix='eagle-rps-map-') as directory:
                    mapping = copy.deepcopy(original)
                    if not include_rps:
                        mapping.pop('Rps', None)
                    assets = Path(directory)
                    refs = assets / 'AnimationSources' / f'{outfit}References'
                    refs.mkdir(parents=True)
                    (refs / 'mapping.json').write_text(json.dumps(mapping), encoding='utf-8')
                    self.assertEqual(module.load_mapping(assets), mapping)

    def test_audit_routes_all_five_clips_to_separate_v2_artwork(self):
        for outfit in ('office', 'hoodie'):
            refs = {'Rps': {'mapping': {'RpsRock': [0, 1, 2, 3, 0]}}}
            records = list(office.audit_sources(Path('assets'), outfit, refs))
            self.assertEqual(len(records), 5)
            for source, info in records:
                clip = next(iter(info['mapping']))
                self.assertEqual(source, Path('assets/AnimationSources/RpsV2') / f'{outfit}-{clip[3:].lower()}-v2.png')
                self.assertEqual(info['mapping'][clip], list(range(16)))

    def test_audit_keeps_non_rps_source_and_mapping_unchanged(self):
        mapping = {'mapping': {'WorkEnter': [0, 1, 2, 3, 4, 5]}}
        entries = list(office.audit_sources(Path('assets'), 'office', {'Work': mapping}))
        self.assertEqual(entries[0], (Path('assets/AnimationSources/office-work-sheet-v1.png'), mapping))

    def test_both_outfits_share_the_v2_timing_contract(self):
        for module in (office, hoodie):
            for clip, (seconds, positions) in office.RPS_TIMES.items():
                self.assertEqual(module.TIMES[clip], (seconds, positions))
                self.assertEqual(len(positions), 16)
                self.assertEqual(positions[0], 0)
                self.assertEqual(positions[-1], round(seconds * 60))
                self.assertEqual(tuple(sorted(set(positions))), positions)
                if clip in ('RpsRock', 'RpsPaper', 'RpsScissors'):
                    self.assertEqual(round(seconds * 60) + 1, 169)
                    self.assertGreaterEqual((positions[9] - positions[6]) / 60, 0.85)
                else:
                    self.assertEqual(round(seconds * 60) + 1, 145)

    def test_v2_audit_accepts_current_evidence_and_rejects_stale_reports(self):
        for clip, (seconds, positions) in office.RPS_TIMES.items():
            count = round(seconds * 60) + 1
            final = {'passed': True, 'source_sha256': 'native-source-hash',
                     'frame_count': count, 'expected_frame_count': count,
                     'authored_frame_indices': list(positions)}
            keys = {**final, 'frame_count': 16, 'expected_frame_count': 16}
            self.assertEqual(office.rps_report_errors(clip, final, keys, 'native-source-hash'), [])
            mutations = [('passed', False), ('source_sha256', 'old-sheet'),
                         ('frame_count', 121), ('expected_frame_count', 121),
                         ('authored_frame_indices', [0, 24, 58, 93, 120])]
            for field, value in mutations:
                for target in ('final', 'keys'):
                    with self.subTest(clip=clip, field=field, target=target):
                        bad_final, bad_keys = copy.deepcopy(final), copy.deepcopy(keys)
                        (bad_final if target == 'final' else bad_keys)[field] = value
                        self.assertTrue(office.rps_report_errors(clip, bad_final, bad_keys, 'native-source-hash'))

    def test_v2_audit_rejects_changed_added_or_removed_source_order(self):
        positions = list(office.RPS_TIMES['RpsRock'][1])
        final = {'passed': True, 'source_sha256': 'sheet', 'frame_count': 169,
                 'expected_frame_count': 169, 'authored_frame_indices': positions,
                 'provenance': {'source_order_metadata_sha256': 'mapping'}}
        keys = {**final, 'frame_count': 16, 'expected_frame_count': 16}
        self.assertEqual(office.rps_report_errors('RpsRock',final,keys,'sheet','mapping'), [])
        for changed_hash in ('changed-mapping', None):
            self.assertTrue(office.rps_report_errors('RpsRock',final,keys,'sheet',changed_hash))
        final['provenance'] = {}
        keys['provenance'] = {}
        self.assertTrue(office.rps_report_errors('RpsRock',final,keys,'sheet','new-mapping'))

    def test_full_outfit_audits_accept_all_nineteen_clips_with_v2_rps(self):
        # Tiny generated fixtures test routing, provenance, frame/key counts and
        # exact endpoint checks; they do not stand in for visual/temporal QA.
        buffer = io.BytesIO()
        Image.new('RGBA', (1, 1), (90, 60, 30, 255)).save(buffer, format='PNG')
        png = buffer.getvalue()
        digest = hashlib.sha256(png).hexdigest()
        for module, outfit in ((office, 'Office'), (hoodie, 'Hoodie')):
            with self.subTest(outfit=outfit), tempfile.TemporaryDirectory(prefix='eagle-rps-audit-') as directory:
                assets = Path(directory)
                refs = module.load_mapping(ASSETS)
                target = assets / 'Outfits' / outfit
                target.mkdir(parents=True)
                (target / 'neutral.png').write_bytes(png)
                reference_dir = assets / 'AnimationSources' / f'{outfit}References'
                reference_dir.mkdir(parents=True)
                (reference_dir / 'mapping.json').write_text(json.dumps(refs), encoding='utf-8')
                preview = assets / 'AnimationPreviews' / outfit
                preview.mkdir(parents=True)
                for source, info in office.audit_sources(assets, outfit.lower(), refs):
                    source.parent.mkdir(parents=True, exist_ok=True)
                    source.write_bytes(png)
                    for clip, mapping in info['mapping'].items():
                        seconds, positions = module.TIMES.get(clip, (2, module.FIFTEEN if len(mapping) == 15 else (0, 24, 58, 93, 120)))
                        frame_count = round(seconds * 60) + 1
                        frame_dir = target / 'Animations' / clip
                        key_dir = target / 'Keys' / clip
                        frame_dir.mkdir(parents=True)
                        key_dir.mkdir(parents=True)
                        for index in range(frame_count):
                            (frame_dir / f'frame-{index:04d}.png').write_bytes(png)
                        for index in range(len(positions)):
                            (key_dir / f'key-{index:02d}.png').write_bytes(png)
                        qa = {'passed': True, 'source_sha256': digest,
                              'frame_count': frame_count, 'expected_frame_count': frame_count,
                              'authored_frame_indices': list(positions)}
                        (preview / f'{clip}-qa.json').write_text(json.dumps(qa), encoding='utf-8')
                        if clip in office.RPS_TIMES:
                            key_qa = {**qa, 'frame_count': 16, 'expected_frame_count': 16}
                            (preview / f'{clip}-keys-qa.json').write_text(json.dumps(key_qa), encoding='utf-8')
                with patch.object(module.pipeline, 'CANVAS_SIZE', (1, 1)):
                    with contextlib.redirect_stdout(io.StringIO()):
                        module.audit(assets)
                result = json.loads((preview / 'outfit-audit.json').read_text())
                self.assertTrue(result['passed'], result['errors'])
                self.assertEqual(result['clip_count'], 19)
                self.assertEqual(result['frame_count'], 2491)
                for clip, (seconds, positions) in office.RPS_TIMES.items():
                    self.assertEqual(result['clips'][clip]['frames'], round(seconds * 60) + 1)
                    self.assertEqual(result['clips'][clip]['authored_frame_indices'], list(positions))


if __name__ == '__main__':
    unittest.main(verbosity=2)
