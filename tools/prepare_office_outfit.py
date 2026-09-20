"""Build outfit reference contact sheets and full authored outfit sequences.

ImageGen, not this script, draws clothing and articulated poses. This utility
assembles references, registers returned alpha sprites and builds optical-flow
frames with the existing QA pipeline. It never rewrites default pet art.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path

from PIL import Image, ImageDraw
import numpy as np
import prepare_generated_sheets as pipeline
from prepare_work_animation import align_cells, extract_native_cells, place_prop
from prepare_care_animation import measure_eat_root, add_temporal_qa
import work_asset_acceleration

WORK = ['WorkEnter', 'WorkLoop', 'WorkToBusyV2', 'BusyLoop', 'WorkExit', 'BusyExitV2']
GROUPS = {'Yawn':['Yawn'], 'Shy':['Shy'], 'Eat':['Eat'], 'Work':WORK,
          'Hungry':['HungryEnter','HungryLoop','HungryExit'], 'Tea':['Tea'],
          'Rps':['RpsRock','RpsPaper','RpsScissors','RpsWin','RpsLose'], 'Annoyed':['Annoyed']}
TIMES = {
    # These two original clips used the pipeline's uniform 15-key timing.
    'Yawn': (2, tuple(round(i*120/14) for i in range(15))),
    'Shy': (2, tuple(round(i*120/14) for i in range(15))),
    'WorkEnter': (3.5, (0,35,74,112,157,210)),
    'WorkLoop': (2, (0,10,20,30,40,50,60,70,80,92,106,120)),
    'WorkToBusyV2': (1.5, (0,18,42,90)),
    'BusyLoop': (2, (0,10,20,30,40,52,64,76,88,104,120)),
    'WorkExit': (2, (0,38,80,120)),
    'BusyExitV2': (2, (0,15,33,66,92,120)),
    'HungryEnter': (1.5, (0,12,25,40,56,73,90)),
    'HungryLoop': (2, (0,24,48,72,96,120)),
    'HungryExit': (1.5, (0,23,46,67,90)),
}
FIFTEEN = (0,8,16,24,32,40,48,56,64,72,80,90,100,110,120)
RPS_TIMES = {
    **{clip: (2.8, (0,8,16,26,36,46,56,72,92,108,118,130,142,152,162,168))
       for clip in ('RpsRock','RpsPaper','RpsScissors')},
    **{clip: (2.4, (0,8,16,24,34,44,54,64,74,84,94,104,114,126,136,144))
       for clip in ('RpsWin','RpsLose')},
}
TIMES.update(RPS_TIMES)
RPS_ROUTE = 'Rps v2 is built separately by tools/prepare_rps_animation.py (16 authored keys per clip)'

def guard_legacy_rps_request(args):
    """Reject explicit old-pipeline requests before even rewriting outfit neutral."""
    if 'Rps' in (args.groups or []) or any(clip in RPS_TIMES for clip in (args.clips or [])):
        raise ValueError(RPS_ROUTE + '; no legacy outfit assets have been written')

def audit_sources(assets:Path, outfit:str, refs:dict):
    """Keep historical mapping files readable, but route RPS to individual v2 sheets."""
    for group, info in refs.items():
        if group != 'Rps':
            yield assets/f'AnimationSources/{outfit}-{group.lower()}-sheet-v1.png', info
    for clip in RPS_TIMES:
        source = assets/f'AnimationSources/RpsV2/{outfit}-{clip[3:].lower()}-v2.png'
        yield source, {'mapping': {clip: list(range(16))}}

def rps_report_errors(clip:str, qa:dict, key_qa:dict, source_hash:str, source_order_hash:str|None=None):
    """Reject stale five-key evidence even if the caller copied it beside new PNGs."""
    seconds, positions = RPS_TIMES[clip]
    errors = []
    for label, report, count in (('final', qa, round(seconds*60)+1), ('keys', key_qa, 16)):
        if not report.get('passed') or report.get('source_sha256') != source_hash:
            errors.append(f'{clip}: {label} QA not passed or v2 source provenance changed')
        if report.get('frame_count') != count or report.get('expected_frame_count') != count:
            errors.append(f'{clip}: {label} QA has stale frame counts')
        if report.get('authored_frame_indices') != list(positions):
            errors.append(f'{clip}: {label} QA has stale authored timing')
        if report.get('provenance',{}).get('source_order_metadata_sha256') != source_order_hash:
            errors.append(f'{clip}: {label} QA source-order metadata changed')
    return errors

def clean_registered_alpha(frame:Image.Image) -> Image.Image:
    """Normalize near-opaque native artwork cores, retaining real AA coverage.

    The generator sometimes exports long orange outline cores at 240..244,
    unlike adjacent opaque outlines. Snap only >=94% coverage to full opacity;
    do not recolor the outline to the white face or shrink the silhouette.
    The normal matte gate still checks every resulting pixel unchanged.
    """
    rgba = np.array(frame.convert('RGBA'))
    rgba[:,:,3][rgba[:,:,3]>=240] = 255
    return pipeline.add_rgb_edge_bleed(pipeline.selective_matte_defringe(Image.fromarray(rgba)))

def load_mapping(assets:Path):
    info=json.loads((assets/'AnimationSources/OfficeReferences/mapping.json').read_text())
    if set(info) not in (set(GROUPS), set(GROUPS)-{'Rps'}):
        raise ValueError('Office reference groups do not match the known catalog')
    for group,item in info.items():
        if set(item['mapping'])!=set(GROUPS[group]):
            raise ValueError(f'{group}: unknown clip target in reference mapping')
        capacity=item['columns']*item['rows']
        if not 1<=item['unique']<=capacity<=36: raise ValueError(f'{group}: invalid reference layout')
        for indices in item['mapping'].values():
            if not indices or any(type(i)!=int or not 0<=i<item['unique'] for i in indices):
                raise ValueError(f'{group}: invalid reference pose index')
        if item['neutral_index'] is not None and not 0<=item['neutral_index']<item['unique']:
            raise ValueError(f'{group}: invalid neutral index')
    return info

def make_references(assets:Path):
    refs = assets/'AnimationSources/OfficeReferences'
    refs.mkdir(parents=True, exist_ok=True)
    neutral = Image.open(assets/'mascot-animated-neutral.png').convert('RGBA')
    neutral_hash = hashlib.sha256(neutral.tobytes()).hexdigest()
    info = {}
    for group, clips in GROUPS.items():
        if group == 'Rps':
            print(RPS_ROUTE + '; reference sheets are supplied per clip',flush=True)
            continue
        unique, by_hash, mapping = [], {}, {}
        for clip in clips:
            entries = []
            for path in sorted((assets/'AnimationKeys'/clip).glob('key-*.png')):
                frame = Image.open(path).convert('RGBA')
                digest = hashlib.sha256(frame.tobytes()).hexdigest()
                if digest not in by_hash:
                    by_hash[digest] = len(unique)
                    unique.append(frame)
                entries.append(by_hash[digest])
            if not entries: raise ValueError(f'Missing inspected default keys: {clip}')
            mapping[clip] = entries
        columns = 6 if group == 'Work' else 5
        rows = math.ceil(len(unique)/columns)
        canvas = Image.new('RGBA',(columns*256,rows*300))
        for index in range(columns*rows):
            frame = unique[index] if index < len(unique) else neutral
            art = frame.crop(frame.getbbox())
            ratio = min(220/art.width,270/art.height)
            art = art.resize((round(art.width*ratio),round(art.height*ratio)),Image.Resampling.LANCZOS)
            canvas.alpha_composite(art,((index%columns)*256+(256-art.width)//2,(index//columns)*300+285-art.height))
        canvas.save(refs/f'{group.lower()}-reference.png')
        info[group] = {'columns':columns,'rows':rows,'unique':len(unique),'mapping':mapping,
                       'neutral_index':by_hash.get(neutral_hash)}
    (refs/'mapping.json').write_text(json.dumps(info,indent=2),encoding='utf-8')

def prepare_neutral(assets:Path):
    base = Image.open(assets/'mascot-animated-neutral.png').convert('RGBA')
    source = assets/'AnimationSources/office-neutral-v1.png'
    # A single image goes through the same scale/foot registration as sheets.
    # The final helmet-width resample can create isolated subpixel fringes;
    # reuse the established matte-only cleanup after that last resample.
    frame = clean_registered_alpha(align_cells(extract_native_cells(source,1,1),base)[0])
    target = assets/'Outfits/Office'; target.mkdir(parents=True,exist_ok=True)
    frame.save(target/'neutral.png',optimize=True)
    return frame

def build(assets:Path,args):
    guard_legacy_rps_request(args)
    print(RPS_ROUTE + '; skipping Rps in this builder',flush=True)
    neutral = prepare_neutral(assets)
    refs = load_mapping(assets)
    target = assets/'Outfits/Office'
    preview = assets/'AnimationPreviews/Office'; preview.mkdir(parents=True,exist_ok=True)
    pipeline.measure_root_anchor = measure_eat_root
    for group, info in refs.items():
        if group == 'Rps': continue
        if args.groups and group not in args.groups: continue
        source = assets/f'AnimationSources/office-{group.lower()}-sheet-v1.png'
        cells = [clean_registered_alpha(frame)
                 for frame in align_cells(extract_native_cells(source,info['columns'],info['rows']),neutral)]
        if info['neutral_index'] is not None: cells[info['neutral_index']] = neutral
        # Cross-group scene anchors are reused by mapping in this same Work/Hungry sheet.
        for clip, indices in info['mapping'].items():
            if args.clips and clip not in args.clips: continue
            frames = [cells[i] for i in indices]
            keys = target/'Keys'/clip; keys.mkdir(parents=True,exist_ok=True)
            for i,frame in enumerate(frames): frame.save(keys/f'key-{i:02d}.png',optimize=True)
            pipeline.make_key_contact_sheet(keys,len(frames),preview/f'{clip}-keys-dark.png')
            seconds, positions = TIMES.get(clip,(2,FIFTEEN if len(frames)==15 else (0,24,58,93,120)))
            if len(frames)!=len(positions): raise ValueError(f'{clip}: {len(frames)} keys versus {len(positions)} positions')
            spec = pipeline.ClipSpec(name=clip,sheet_name=source.name,duration_seconds=seconds,
                                    columns=len(frames),rows=1,source_pose_count=len(frames),
                                    authored_frame_indices=positions,segmentwise_interpolation=True,
                                    lock_authored_frames=True)
            key_qa = pipeline.qa_frame_sequence(frames,len(frames),neutral,frames[0],frames[-1],component_spec=spec)
            key_qa['preflight'] = pipeline.inspect_authored_key_preflight(frames,spec)
            key_qa['passed'] = key_qa['passed'] and key_qa['preflight']['passed']
            key_qa['source_sha256'] = hashlib.sha256(source.read_bytes()).hexdigest()
            key_qa['authored_frame_indices'] = positions
            pipeline.write_qa_report(preview/f'{clip}-keys-qa.json',key_qa)
            if not key_qa['passed']:
                raise RuntimeError(f"Office/{clip} keys failed QA: {key_qa['errors']}; {key_qa['preflight']['errors']}")
            print(json.dumps({'outfit':'office','clip':clip,'keys_passed':True}),flush=True)
            if args.keys_only: continue
            if args.rife is None or args.rife_model is None:
                raise ValueError('Provide --rife and --rife-model; no placeholder frames are generated')
            output = target/'Animations'/clip
            try:
                count,qa = pipeline.run_rife(args.rife.resolve(strict=True),args.rife_model.resolve(strict=True),keys,output,spec,neutral,exact_endpoints=(frames[0],frames[-1]))
            except RuntimeError as error:
                # run_rife raises at its final gate; persist its actual frame evidence for review.
                rendered,_,errors = pipeline.load_frame_sequence(output)
                qa = pipeline.qa_frame_sequence(rendered,round(seconds*60)+1,neutral,frames[0],frames[-1],prior_errors=errors,component_spec=spec)
                qa['build_error'] = str(error)
                qa['passed'] = False
                pipeline.write_qa_report(preview/f'{clip}-qa.json',qa)
                if rendered: pipeline.make_dark_background_contact_sheet(output,len(rendered),preview/f'{clip}-dark.png')
                raise
            rendered,_,_=pipeline.load_frame_sequence(output)
            add_temporal_qa(qa,rendered)
            qa['outfit']='outfit.office'
            qa['source_sha256']=hashlib.sha256(source.read_bytes()).hexdigest()
            pipeline.write_qa_report(preview/f'{clip}-qa.json',qa)
            pipeline.make_dark_background_contact_sheet(output,count,preview/f'{clip}-dark.png')
            pipeline.make_60fps_gif(output,count,preview/f'{clip}-60fps.gif')
            print(json.dumps({'outfit':'office','clip':clip,'passed':qa['passed']}),flush=True)
            if not qa['passed']: raise RuntimeError(f'Office/{clip} failed QA; do not list for sale')

def props(assets:Path):
    target = assets/'SceneProps/Shop'; target.mkdir(parents=True,exist_ok=True)
    definitions = [('mint-desk-v2.png','desk-mint',306,39,345),
                   ('walnut-desk-v1.png','desk-walnut',306,39,345),
                   ('arcade-desk-v1.png','desk-arcade',306,39,345),
                   ('midnight-computer-v1.png','computer-midnight',102,141,275),
                   ('retro-computer-v1.png','computer-retro',102,141,275),
                   ('arcade-computer-v1.png','computer-arcade',102,141,275)]
    for source,name,width,x,bottom in definitions:
        rgba = Image.open(assets/'AnimationSources'/source).convert('RGBA')
        # Ignore nearly transparent export noise when finding a prop's physical size.
        # Fitting a whole canvas instead of the visible laptop made it only ~30 px wide.
        alpha = np.asarray(rgba.getchannel('A'))
        ys,xs = np.where(alpha >= 32)
        if not len(xs): raise ValueError(f'{source}: no visible prop')
        crop = rgba.crop((int(xs.min()),int(ys.min()),int(xs.max())+1,int(ys.max())+1))
        if name.startswith('desk-'):
            # All table surfaces and feet share the authored classic desk bounds.
            # Small source aspect variations must not lift the laptop off its table.
            art = pipeline.add_rgb_edge_bleed(crop, iterations=12).resize((306,96),Image.Resampling.LANCZOS)
            image = Image.new('RGBA',pipeline.CANVAS_SIZE)
            image.alpha_composite(art,(39,249))
        else:
            image = place_prop(crop,width,x,bottom)
        image.save(target/f'{name}.png',optimize=True)
        if name.startswith('desk-'):
            back,front=np.array(image),np.array(image)
            back[276:,:,3]=0; front[:276,:,3]=0
            Image.fromarray(back).save(target/f'{name}-back.png',optimize=True)
            Image.fromarray(front).save(target/f'{name}-front.png',optimize=True)

def audit(assets:Path):
    """Read every output and audit scene seams without modifying artwork."""
    target = assets/'Outfits/Office'
    preview = assets/'AnimationPreviews/Office'
    refs = load_mapping(assets)
    neutral = np.array(Image.open(target/'neutral.png').convert('RGBA'))
    errors, clips, endpoints = [], {}, {}
    total_bytes = (target/'neutral.png').stat().st_size
    for source, info in audit_sources(assets,'office',refs):
        if not source.is_file():
            errors.append(f'{source.name}: missing source artwork')
            continue
        source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
        for clip, keys in info['mapping'].items():
            output = target/'Animations'/clip
            seconds,positions = TIMES.get(clip,(2,FIFTEEN if len(keys)==15 else (0,24,58,93,120)))
            expected = round(seconds*60)+1
            paths = sorted(output.glob('frame-*.png'))
            if [p.name for p in paths] != [f'frame-{i:04d}.png' for i in range(expected)]:
                errors.append(f'{clip}: non-contiguous or incomplete frame sequence')
                continue
            report_path = preview/f'{clip}-qa.json'
            if not report_path.is_file():
                errors.append(f'{clip}: missing final QA report')
                continue
            qa = json.loads(report_path.read_text())
            if not qa.get('passed') or qa.get('source_sha256') != source_hash:
                errors.append(f'{clip}: QA not passed or source provenance changed')
            if clip in RPS_TIMES:
                key_report_path = preview/f'{clip}-keys-qa.json'
                key_qa = json.loads(key_report_path.read_text()) if key_report_path.is_file() else {}
                order_path = source.with_suffix('.json')
                order_hash = hashlib.sha256(order_path.read_bytes()).hexdigest() if order_path.is_file() else None
                errors.extend(rps_report_errors(clip,qa,key_qa,source_hash,order_hash))
                key_paths = sorted((target/'Keys'/clip).glob('key-*.png'))
                if [path.name for path in key_paths] != [f'key-{i:02d}.png' for i in range(16)]:
                    errors.append(f'{clip}: non-contiguous or incomplete v2 key sequence')
                    continue
            for i,path in enumerate(paths):
                with Image.open(path) as opened:
                    opened.load()
                    if opened.mode!='RGBA' or opened.size!=pipeline.CANVAS_SIZE:
                        errors.append(f'{clip}/{path.name}: incorrect image format')
                    frame=np.array(opened.convert('RGBA'))
                if i==0: endpoints[clip,'start']=frame
                if i==expected-1: endpoints[clip,'end']=frame
                if i in positions:
                    key=np.array(Image.open(target/'Keys'/clip/f'key-{positions.index(i):02d}.png').convert('RGBA'))
                    if not np.array_equal(frame,key): errors.append(f'{clip}/{path.name}: authored key was not locked exactly')
            size=sum(p.stat().st_size for p in paths); total_bytes+=size
            clips[clip]={'frames':expected,'bytes':size,'qa_passed':qa.get('passed'),
                         'root':qa.get('root'),'halo':qa.get('dark_background_halo',{}).get('passed'),
                         'unique_pixel_frames':qa.get('temporal_sampling',{}).get('unique_pixel_frames'),
                         'authored_frame_indices':positions}
    seams=[('WorkEnter','end','WorkLoop','start'),('WorkLoop','end','WorkLoop','start'),
           ('WorkLoop','end','WorkToBusyV2','start'),('WorkToBusyV2','end','BusyLoop','start'),
           ('BusyLoop','end','BusyLoop','start'),('WorkLoop','end','WorkExit','start'),
           ('BusyLoop','end','BusyExitV2','start'),('HungryEnter','end','HungryLoop','start'),
           ('HungryLoop','end','HungryLoop','start'),('HungryLoop','end','HungryExit','start')]
    seam_results=[]
    for a,ea,b,eb in seams:
        same=(a,ea) in endpoints and (b,eb) in endpoints and np.array_equal(endpoints[a,ea],endpoints[b,eb])
        seam_results.append({'from':f'{a}:{ea}','to':f'{b}:{eb}','pixel_exact':same})
        if not same: errors.append(f'Scene seam mismatch {a}:{ea} -> {b}:{eb}')
    ordinary=[name for names in GROUPS.values() for name in names if name not in WORK and not name.startswith('Hungry')]
    neutral_points=[(name,end) for name in ordinary for end in ('start','end')]
    neutral_points += [('WorkEnter','start'),('WorkExit','end'),('BusyExitV2','end'),('HungryEnter','start'),('HungryExit','end')]
    for name,end in neutral_points:
        if (name,end) not in endpoints or not np.array_equal(endpoints[name,end],neutral):
            errors.append(f'{name}:{end} differs from outfit neutral')
    report={'passed':not errors,'errors':errors,'outfit':'outfit.office','clips':clips,'scene_seams':seam_results,
            'frame_count':sum(item['frames'] for item in clips.values()),'clip_count':len(clips),
            'packaged_png_bytes':total_bytes,'canvas':list(pipeline.CANVAS_SIZE),
            'neutral_sha256':hashlib.sha256((target/'neutral.png').read_bytes()).hexdigest(),
            'source_art':'Built-in ImageGen full-body outfit poses; no programmatically drawn clothing or character parts',
            'native_alpha_policy':{'opaque_core_min_alpha':240,'final_resample_cleanup':'existing selective_matte_defringe',
                                   'qa_thresholds_relaxed':False,'character_redrawn_by_code':False},
            'decoded_common_three_bytes':363*320*288*4,
            'decoded_work_phases_bytes':786*320*288*4,
            'decoded_all_clips_bytes':sum(item['frames'] for item in clips.values())*320*288*4,
            'shipping_gate':'Requires this automated audit and human inspection; does not register shop availability.'}
    print(f'Exact audit output: {preview / "outfit-audit.json"}',flush=True)
    pipeline.write_qa_report(preview/'outfit-audit.json',report)
    print(json.dumps({'passed':report['passed'],'frames':report['frame_count'],'clips':report['clip_count'],'bytes':total_bytes,'errors':errors}),flush=True)
    if errors: raise RuntimeError('Office outfit audit failed; do not register for sale')

def transition_review(assets:Path,selected:list[str]|None=None):
    """Diagnostic contact sheets of actual frames, including quarter/half/three-quarter samples."""
    samples={'WorkToBusyV2':(0,18,23,42,45,68,90),'BusyExitV2':(0,15,30,33,60,90,120)}
    preview=assets/'AnimationPreviews/Office'
    for clip,indices in samples.items():
        if selected and clip not in selected: continue
        canvas=Image.new('RGB',(len(indices)*240,410),(18,21,28))
        draw=ImageDraw.Draw(canvas)
        for column,index in enumerate(indices):
            frame=Image.open(assets/'Outfits/Office/Animations'/clip/f'frame-{index:04d}.png').convert('RGBA')
            x=column*240
            draw.text((x+6,4),f'{clip} f{index:03d}',fill=(102,220,230))
            art=frame.resize((240,216),Image.Resampling.LANCZOS)
            canvas.paste(art,(x,20),art)
            face=frame.crop((112,105,272,235)).resize((224,182),Image.Resampling.NEAREST)
            canvas.paste(face,(x+8,225),face)
        output=preview/f'{clip}-transition-review.png'
        print(f'Exact diagnostic output: {output}',flush=True)
        canvas.save(output,optimize=True)

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--assets',type=Path,required=True)
    p.add_argument('--phase',choices=['refs','build','props','audit','review'],required=True)
    p.add_argument('--groups',nargs='*',choices=GROUPS);p.add_argument('--keys-only',action='store_true')
    p.add_argument('--clips',nargs='*',help='Optional selected clips within the requested groups')
    p.add_argument('--rife',type=Path);p.add_argument('--rife-model',type=Path)
    p.add_argument('--dependency-root',type=Path)
    args=p.parse_args();assets=args.assets.resolve(strict=True)
    print(f'Exact generated asset root: {assets}; phase={args.phase}',flush=True)
    work_asset_acceleration.enable(pipeline,args.dependency_root)
    if args.phase=='refs':make_references(assets)
    elif args.phase=='props':props(assets)
    elif args.phase=='audit':audit(assets)
    elif args.phase=='review':transition_review(assets,args.clips)
    else:build(assets,args)

if __name__=='__main__':main()
