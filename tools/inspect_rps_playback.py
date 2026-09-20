"""Snapshot and inspect the actual RPS PNG sequences without changing pet assets.

The report is diagnostic, not a claim that an animation is perceptually smooth.
Use the offline playback page at 1x and 0.25x and inspect worst-delta contacts.
Only writes to a new snapshot under .codex-build, or to analysis artifacts inside
an existing snapshot. Before/after runs remain independent of future repo edits.
"""
from __future__ import annotations

import argparse
import hashlib
import html
import json
import math
from pathlib import Path
import shutil

import numpy as np
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parent.parent
CLIPS = ("RpsRock", "RpsPaper", "RpsScissors", "RpsWin", "RpsLose", "Yawn")
PACKS = ("default", "Office", "Hoodie")


def pack_name(value: str) -> str:
    for pack in PACKS:
        if value.lower() == pack.lower():
            return pack
    raise argparse.ArgumentTypeError(f"Unknown outfit: {value}")


def rps_clip_name(value: str) -> str:
    for clip in CLIPS[:-1]:
        if value.lower() in (clip.lower(), clip[3:].lower()):
            return clip
    raise argparse.ArgumentTypeError(f"Unknown RPS clip: {value}")


def rgba(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        return np.asarray(image.convert("RGBA"), dtype=np.uint8)


def visible_difference(a: np.ndarray, b: np.ndarray, crop=None) -> dict:
    if crop:
        left, top, right, bottom = crop
        a, b = a[top:bottom, left:right], b[top:bottom, left:right]
    af, bf = a.astype(np.float32) / 255, b.astype(np.float32) / 255
    active = (af[:, :, 3] > 4 / 255) | (bf[:, :, 3] > 4 / 255)
    if not active.any():
        return {"rgb_mae": 0.0, "alpha_mae": 0.0, "changed_fraction": 0.0}
    premul_a, premul_b = af[:, :, :3] * af[:, :, 3:4], bf[:, :, :3] * bf[:, :, 3:4]
    diff = np.abs(premul_a - premul_b)
    return {
        "rgb_mae": float(diff[active].mean()),
        "alpha_mae": float(np.abs(af[:, :, 3] - bf[:, :, 3])[active].mean()),
        "changed_fraction": float((diff.max(axis=2)[active] > 0.08).mean()),
    }


def contiguous_runs(values: list[bool]) -> list[dict]:
    result = []
    start = None
    for index, value in enumerate([*values, False]):
        if value and start is None:
            start = index
        elif not value and start is not None:
            result.append({"start": start, "end": index - 1, "frame_count": index - start,
                           "seconds": round((index - start - 1) / 60, 4)})
            start = None
    return result


def contact(frames: list[np.ndarray], indices: list[int], path: Path, title: str, columns=6):
    width, height = 230, 230
    result = Image.new("RGB", (columns * width, math.ceil(len(indices) / columns) * height + 38), "#111923")
    draw = ImageDraw.Draw(result)
    draw.text((10, 12), title, fill="#f1d79c")
    for slot, index in enumerate(indices):
        x, y = (slot % columns) * width, (slot // columns) * height + 38
        frame = Image.fromarray(frames[index])
        frame.thumbnail((width, height - 22), Image.Resampling.LANCZOS)
        result.paste(frame, (x + (width - frame.width) // 2, y), frame)
        draw.text((x + 8, y + height - 18), f"#{index:03d} / {index / 60:.3f}s", fill="#9edeca")
    result.save(path)


def inspect_clip(folder: Path, neutral: np.ndarray, gesture_frame: int | None) -> dict:
    paths = sorted(folder.glob("frame-*.png"))
    if len(paths) < 2:
        raise ValueError(f"Missing frame sequence: {folder}")
    expected = [f"frame-{i:04d}.png" for i in range(len(paths))]
    if [p.name for p in paths] != expected:
        raise ValueError(f"Noncontiguous frame sequence: {folder}")
    frames = [rgba(path) for path in paths]
    if any(frame.shape != neutral.shape for frame in frames):
        raise ValueError(f"Canvas differs from neutral: {folder}")
    deltas = [{"frame": i, **visible_difference(frames[i - 1], frames[i])} for i in range(1, len(frames))]
    for index, item in enumerate(deltas):
        neighbors = [deltas[j]["rgb_mae"] for j in range(max(0, index - 5), min(len(deltas), index + 6)) if j != index]
        item["neighbor_ratio"] = item["rgb_mae"] / max(float(np.median(neighbors)), 0.0001)
    worst = sorted(deltas, key=lambda d: d["rgb_mae"], reverse=True)[:8]
    spike = sorted(deltas, key=lambda d: d["neighbor_ratio"], reverse=True)[:8]
    peak = min(gesture_frame if gesture_frame is not None else (len(frames) - 1) // 2, len(frames) - 1)
    w, h = neutral.shape[1], neutral.shape[0]
    # This broad foreground-hand area is a diagnostic, not hand recognition.
    hand_crop = (int(w * .23), int(h * .43), int(w * .76), int(h * .89))
    peak_distance = [visible_difference(frames[peak], frame, hand_crop)["rgb_mae"] for frame in frames]
    near_peak = [d <= .012 for d in peak_distance]
    runs = contiguous_runs(near_peak)
    peak_run = next(run for run in runs if run["start"] <= peak <= run["end"])
    fingerprints = [hashlib.sha256(frame.tobytes()).hexdigest() for frame in frames]
    duplicate_edges = [i for i in range(1, len(frames)) if fingerprints[i] == fingerprints[i - 1]]
    evenly = [round(i * (len(frames) - 1) / 17) for i in range(18)]
    contact(frames, evenly, folder / "timeline-contact.png", folder.parent.name + " / " + folder.name + " timeline")
    contact(frames, [v for d in worst[:6] for v in (d["frame"] - 1, d["frame"])],
            folder / "worst-adjacent-contact.png", "Worst adjacent changes (pairs, descending visible RGB MAE)")
    around = sorted({i for d in worst[:3] for i in range(max(0, d["frame"] - 3), min(len(frames), d["frame"] + 4))})
    contact(frames, around, folder / "worst-neighborhood-contact.png", "Neighborhoods around three largest changes")
    return {
        "pack": folder.parent.name, "clip": folder.name, "frame_count": len(frames),
        "duration_seconds": (len(frames) - 1) / 60, "fps": 60,
        "canvas": [w, h], "frame_hashes": fingerprints,
        "endpoints": {"first_exact_neutral": bool(np.array_equal(frames[0], neutral)),
                      "last_exact_neutral": bool(np.array_equal(frames[-1], neutral))},
        "adjacent": deltas, "worst_adjacent": worst, "largest_local_spikes": spike,
        "mean_adjacent_rgb_mae": float(np.mean([x["rgb_mae"] for x in deltas])),
        "median_adjacent_rgb_mae": float(np.median([x["rgb_mae"] for x in deltas])),
        "p95_adjacent_rgb_mae": float(np.percentile([x["rgb_mae"] for x in deltas], 95)),
        "duplicate_adjacent_frame_indexes": duplicate_edges, "unique_frames": len(set(fingerprints)),
        "gesture_candidate": {"reference_frame": peak, "roi": hand_crop, "mae_threshold": .012,
                              "near_reference_run": peak_run, "distance_to_reference": peak_distance,
                              "warning": "Candidate-only: manually confirm visible hand meaning; similar pixels do not establish a readable gesture."},
    }


HTML = r'''<!doctype html>
<html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>RPS PNG 原帧回放</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#121b26;color:#eee7d6;font:15px system-ui,sans-serif}main{max-width:1180px;margin:auto;padding:24px}h1{margin:0 0 8px;font-size:26px}p{color:#adbecb;line-height:1.6}button,select{font:inherit;color:#f4e9d3;background:#26394a;border:1px solid #50677b;border-radius:9px;padding:9px 13px;margin:4px;cursor:pointer}button:hover{background:#36566d}button:focus-visible,select:focus-visible{outline:3px solid #edbd59}section{display:grid;grid-template-columns:1fr 1fr;gap:18px}article{background:#1b2939;border:1px solid #385064;border-radius:18px;padding:16px}h2{margin:0 0 6px}.stage{background:#111923;border-radius:12px;text-align:center;overflow:hidden}canvas.frame{display:block;margin:auto;max-width:100%;height:auto;width:384px}canvas.chart{display:block;width:100%;height:90px;cursor:crosshair;background:#12202d;margin:12px 0}input[type=range]{width:100%}.note{font-size:13px;color:#adbecb;min-height:100px}a{color:#d8bd72}.meta{font-variant-numeric:tabular-nums;color:#aad7cd;margin:8px 0}small{color:#b7c7d4}.light .stage{background:#e6e8e7}.light canvas.frame{background:#e6e8e7}.checker .stage{background:repeating-conic-gradient(#344452 0 25%,#1a2735 0 50%) 0/24px 24px}.checker canvas.frame{background:transparent}@media(max-width:720px){section{grid-template-columns:1fr}}
</style><main><h1>猜拳：原始 PNG 逐帧回放</h1><p>这里没有转场、淡入淡出或浏览器补帧。按原序列以 60 帧/秒取样；对照为相同服装的打哈欠。先看 1×，再慢放找突变；差分数值只是定位线索，不是“丝滑”评分。所有图像均为独立快照，仓库后续替换不会改变此页。</p>
<nav><select id="pack" aria-label="服装"><option value="default">原味大头鹰</option><option value="Office">上班装</option><option value="Hoodie">卫衣</option></select><select id="clip" aria-label="动作"><option>RpsRock</option><option>RpsPaper</option><option>RpsScissors</option><option>RpsWin</option><option>RpsLose</option></select><button id="play">暂停</button><button id="previous">前一帧</button><button id="next">后一帧</button><select id="speed" aria-label="速度"><option value=".25">0.25× 慢放</option><option value=".5">0.5×</option><option value="1" selected>1× 原速</option></select><select id="background" aria-label="背景"><option value="">深色</option><option value="light">浅色</option><option value="checker">透明棋盘</option></select></nav>
<div id="status" class="meta">加载快照…</div><section><article><h2 id="label"></h2><div class="stage"><canvas id="actual" class="frame" width="384" height="346"></canvas></div><div id="actualMeta" class="meta"></div><input id="scrub" aria-label="当前帧" type="range" min="0" value="0"><canvas id="chart" class="chart" width="1000" height="100"></canvas><div id="links"></div><p id="details" class="note"></p></article><article><h2>Yawn / 打哈欠对照</h2><div class="stage"><canvas id="reference" class="frame" width="384" height="346"></canvas></div><div id="referenceMeta" class="meta"></div><canvas id="referenceChart" class="chart" width="1000" height="100"></canvas><p id="referenceDetails" class="note"></p></article></section><p>方向键逐帧、空格暂停。图表表示相邻两帧的前景预乘 RGB 平均变化；金色柱为变化，薄荷色线为当前帧。点击图表跳到该位置。所有数值和帧像素 SHA-256 保存在 <a href="report.json">report.json</a>。</p></main>
<script>const REPORT=__DATA__;
const $=id=>document.getElementById(id); let selectionToken=0,ready=false,playing=true,frame=0,elapsed=0,last=0,actual=[],reference=[],record=null,refRecord=null;
function loadImages(pack,clip,count){return Promise.all(Array.from({length:count},(_,i)=>new Promise((resolve,reject)=>{const image=new Image();image.onload=()=>resolve(image);image.onerror=()=>reject(new Error('Missing '+image.src));image.src=pack+'/'+clip+'/frame-'+String(i).padStart(4,'0')+'.png';})));}
function description(r){const w=r.worst_adjacent[0],g=r.gesture_candidate;return `${r.frame_count} 帧 / ${r.duration_seconds.toFixed(2)} s；最大变化 #${w.frame-1} → #${w.frame}：${w.rgb_mae.toFixed(5)}，邻域比 ${w.neighbor_ratio.toFixed(2)}。首尾逐像素匹配站立：${r.endpoints.first_exact_neutral&&r.endpoints.last_exact_neutral?'是':'否'}。参考帧 #${g.reference_frame} 附近低变化候选区间 #${g.near_reference_run.start}–${g.near_reference_run.end}（${g.near_reference_run.seconds.toFixed(2)}s），是否真正留住清晰手势须人工看图。相邻完全重复：${r.duplicate_adjacent_frame_indexes.length}。`;}
function controlsDisabled(disabled){['play','previous','next','scrub'].forEach(id=>$(id).disabled=disabled);}
async function choose(){const token=++selectionToken;ready=false;controlsDisabled(true);$('status').textContent='加载独立快照…';record=REPORT.clips.find(r=>r.pack===$('pack').value&&r.clip===$('clip').value);refRecord=REPORT.clips.find(r=>r.pack===record.pack&&r.clip==='Yawn');try{const images=await Promise.all([loadImages(record.pack,record.clip,record.frame_count),loadImages(record.pack,'Yawn',refRecord.frame_count)]);if(token!==selectionToken)return;[actual,reference]=images;frame=elapsed=0;last=performance.now();ready=true;controlsDisabled(false);$('label').textContent=record.pack+' / '+record.clip;$('scrub').max=record.frame_count-1;$('details').textContent=description(record);$('referenceDetails').textContent=description(refRecord);$('links').innerHTML=`<a href="${record.pack}/${record.clip}/timeline-contact.png">全程联系表</a> · <a href="${record.pack}/${record.clip}/worst-adjacent-contact.png">最大相邻差分</a> · <a href="${record.pack}/${record.clip}/worst-neighborhood-contact.png">突变邻域</a>`;$('status').textContent='已预解码 '+(actual.length+reference.length)+' 帧，离线原帧播放';render();}catch(error){if(token===selectionToken)$('status').textContent=error.message;}}
function draw(canvas,image){const ctx=canvas.getContext('2d');ctx.clearRect(0,0,canvas.width,canvas.height);ctx.drawImage(image,0,0);}
function chart(canvas,r,position){const ctx=canvas.getContext('2d');ctx.clearRect(0,0,canvas.width,canvas.height);const max=Math.max(...r.adjacent.map(d=>d.rgb_mae),.001),bar=canvas.width/(r.frame_count-1);ctx.fillStyle='#eabd5b';r.adjacent.forEach(d=>ctx.fillRect((d.frame-1)*bar,canvas.height*(1-d.rgb_mae/max),Math.max(1,bar-1),canvas.height*d.rgb_mae/max));ctx.fillStyle='#93edc9';ctx.fillRect(position/(r.frame_count-1)*(canvas.width-2),0,2,canvas.height);}
function render(){if(!ready)return;const rf=Math.min(frame,reference.length-1);draw($('actual'),actual[frame]);draw($('reference'),reference[rf]);$('scrub').value=frame;$('actualMeta').textContent=`#${frame} / ${actual.length-1} · ${(frame/60).toFixed(3)} s`;$('referenceMeta').textContent=`#${rf} / ${reference.length-1} · ${(rf/60).toFixed(3)} s${frame>rf?'（对照已播完）':''}`;chart($('chart'),record,frame);chart($('referenceChart'),refRecord,rf);}
function step(index){if(!ready)return;playing=false;$('play').textContent='播放';frame=Math.max(0,Math.min(actual.length-1,index));elapsed=frame/60;render();}
function toggle(){if(!ready)return;playing=!playing;$('play').textContent=playing?'暂停':'播放';last=performance.now();}
function tick(now){try{if(ready&&playing){elapsed+=Math.max(0,Math.min((now-last)/1000,.1))*Number($('speed').value);const duration=(actual.length-1)/60+.25;if(elapsed>=duration)elapsed%=duration;frame=Math.max(0,Math.min(actual.length-1,Math.floor(elapsed*60)));render();}}catch(error){ready=false;controlsDisabled(true);$('status').textContent='回放错误，请重新选择动作：'+error.message;}finally{last=now;requestAnimationFrame(tick);}}
$('pack').onchange=$('clip').onchange=choose;$('play').onclick=toggle;$('next').onclick=()=>step(frame+1);$('previous').onclick=()=>step(frame-1);$('scrub').oninput=()=>step(Number($('scrub').value));$('background').onchange=()=>document.body.className=$('background').value;$('chart').onclick=event=>step(Math.round((event.clientX-event.currentTarget.getBoundingClientRect().left)/event.currentTarget.clientWidth*(actual.length-1)));document.addEventListener('keydown',event=>{if(['SELECT','INPUT'].includes(event.target.tagName))return;if(event.code==='Space'){event.preventDefault();toggle();}if(event.code==='ArrowRight'){event.preventDefault();step(frame+1);}if(event.code==='ArrowLeft'){event.preventDefault();step(frame-1);}});choose();requestAnimationFrame(tick);
</script></html>'''


def write_playback_page(target: Path, report: dict) -> None:
    page_data = {**report, "clips": [{key: value for key, value in r.items() if key != "frame_hashes"} for r in report["clips"]]}
    packs = list(dict.fromkeys(r["pack"] for r in report["clips"]))
    clips = list(dict.fromkeys(r["clip"] for r in report["clips"] if r["clip"] != "Yawn"))
    names = {"default": "原味大头鹰", "Office": "上班装", "Hoodie": "卫衣"}
    pack_options = ''.join(f'<option value="{html.escape(pack)}">{names.get(pack, html.escape(pack))}</option>' for pack in packs)
    clip_options = ''.join(f'<option>{html.escape(clip)}</option>' for clip in clips)
    page = HTML.replace('__DATA__', json.dumps(page_data, ensure_ascii=True))
    start = page.index('<select id="pack"')
    content = page.index('>', start) + 1
    page = page[:content] + pack_options + page[page.index('</select>', content):]
    start = page.index('<select id="clip"')
    content = page.index('>', start) + 1
    page = page[:content] + clip_options + page[page.index('</select>', content):]
    (target / "index.html").write_text(page, encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot", type=Path, required=True)
    parser.add_argument("--capture", action="store_true", help="Capture current production assets into a NEW snapshot first")
    parser.add_argument("--staging", type=Path, help="With --capture, read RPS frames from this staging root; neutral/Yawn remain read-only production references")
    parser.add_argument("--outfits", nargs="+", type=pack_name, default=list(PACKS))
    parser.add_argument("--clips", nargs="+", type=rps_clip_name, default=list(CLIPS[:-1]))
    parser.add_argument("--html-only", action="store_true", help="Rebuild player from existing report; do not read/copy/modify any frame or report")
    parser.add_argument("--gesture-frame", type=int, help="Optional manually confirmed reveal-pose frame (default: midpoint)")
    args = parser.parse_args()
    if args.gesture_frame is not None and args.gesture_frame < 0:
        parser.error("--gesture-frame must be nonnegative")
    if args.html_only and args.capture:
        parser.error("--html-only cannot be combined with --capture")
    if args.staging is not None and not args.capture:
        parser.error("--staging requires --capture into a new independent snapshot")
    target = args.snapshot.resolve()
    allowed = (ROOT / ".codex-build").resolve()
    if not target.is_relative_to(allowed) or target == allowed:
        raise SystemExit("Snapshot must be a named child of repository .codex-build")
    print(f"Exact QA output target: {target}", flush=True)
    if args.html_only:
        report = json.loads((target / "report.json").read_text(encoding="utf-8"))
        write_playback_page(target, report)
        print(f"Updated playback HTML only: {target / 'index.html'}", flush=True)
        return
    if args.capture:
        if target.exists():
            raise SystemExit("Refusing to overwrite existing snapshot")
        assets = ROOT / "DuckDeskPet" / "Assets"
        staging = args.staging.resolve(strict=True) if args.staging else None
        if staging is not None and (staging == allowed or not staging.is_relative_to(allowed)):
            parser.error("--staging must be an existing named child of repository .codex-build")
        copies = []
        for pack in dict.fromkeys(args.outfits):
            source = assets if pack == "default" else assets / "Outfits" / pack
            for clip in [*dict.fromkeys(args.clips), "Yawn"]:
                frames = staging / pack.lower() / "Animations" / clip if staging and clip != "Yawn" else source / "Animations" / clip
                if not frames.is_dir() or len(list(frames.glob("frame-*.png"))) < 2:
                    parser.error(f"Missing selected read-only sequence: {frames}")
                copies.append((frames, target / pack / clip))
            neutral_source = source / ("mascot-animated-neutral.png" if pack == "default" else "neutral.png")
            if not neutral_source.is_file():
                parser.error(f"Missing read-only neutral: {neutral_source}")
            copies.append((neutral_source, target / pack / "neutral.png"))
        target.mkdir(parents=True)
        for source, destination in copies:
            if source.is_dir():
                shutil.copytree(source, destination)
            else:
                shutil.copy2(source, destination)
    if not target.is_dir():
        raise SystemExit("Snapshot absent; use --capture for a new snapshot")
    records = []
    for pack in dict.fromkeys(args.outfits):
        neutral = rgba(target / pack / "neutral.png")
        for clip in [*dict.fromkeys(args.clips), "Yawn"]:
            record = inspect_clip(target / pack / clip, neutral, args.gesture_frame if clip.startswith("Rps") else None)
            records.append(record)
            print(json.dumps({key: record[key] for key in ("pack", "clip", "frame_count", "endpoints", "mean_adjacent_rgb_mae")}))
    report = {"version": 1, "source": str(target), "fps": 60,
              "diagnostic_only": "Image differences locate transitions; human playback review must judge anatomy, gesture readability and timing.",
              "hold_note": "Near-reference ROI match is not a semantic gesture detector and does not require frozen duplicate frames.",
              "clips": records}
    (target / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    write_playback_page(target, report)
    print(f"Offline playback page: {target / 'index.html'}", flush=True)


if __name__ == "__main__":
    main()
