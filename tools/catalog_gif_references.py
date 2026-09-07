"""Inventory read-only GIF references and make diagnostic contact sheets.

No original GIF is changed. Outputs document provenance and candidate behaviors;
they are reference material, not runtime sprite assets.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


CANDIDATES = {
    "干饭": ("Eat", "P0", "全身捧碗、送饭入口、鼓腮咀嚼、满足收手"),
    "害羞": ("Shy", "existing", "保留已有全身害羞动作；GIF 用作神态参考"),
    "炸你": ("Bomb", "existing", "保留已有单向抛掷与烟雾的全身动作"),
    "乞讨": ("HungryTable", "P1", "饥饿场景的眼神与讨饭手势，需重绘全身和桌子"),
    "哭": ("HungryTable", "P1", "趴桌哭泣的表情参考，拆分进入/循环/离开"),
    "疲惫": ("Sleepy", "P1", "困倦情绪；不要用整身位移模拟动作"),
    "躺平": ("Rest", "P1", "休息场景；维持屏幕脚点或明确场景落点"),
    "开心": ("Happy", "P1", "喂食或升级后的开心反应"),
    "收到": ("Acknowledged", "P1", "收到 AI 通知时的简短回应"),
    "点赞": ("Praise", "P1", "摸头、成果通知与成就反应"),
    "工作": ("Working", "P2", "挂机工作小场景，不读取用户屏幕内容"),
    "忙碌中": ("Working", "P2", "工作情景循环"),
    "吃惊": ("Surprised", "P2", "稀有事件或互动反馈"),
    "飞吻": ("Affection", "P2", "亲密互动反馈"),
}


def font(size: int) -> ImageFont.ImageFont:
    candidate = Path("C:/Windows/Fonts/msyh.ttc")
    return ImageFont.truetype(str(candidate), size) if candidate.is_file() else ImageFont.load_default()


def inspect_gif(path: Path) -> tuple[dict, list[Image.Image]]:
    frames, durations = [], []
    with Image.open(path) as gif:
        size, loop = gif.size, gif.info.get("loop")
        for index in range(gif.n_frames):
            gif.seek(index)
            frames.append(gif.convert("RGBA").copy())
            durations.append(int(gif.info.get("duration", 0)))
    behavior, priority, notes = CANDIDATES.get(path.stem, (None, "library", "保留为表情与动作参考，尚未安排实现"))
    return {
        "file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "width": size[0], "height": size[1], "frame_count": len(frames),
        "duration_ms": sum(durations), "frame_durations_ms": durations, "loop": loop,
        "candidate_behavior": behavior, "priority": priority, "notes": notes,
        "runtime_ready": False,
    }, frames


def thumbnail(frame: Image.Image, size: tuple[int, int]) -> Image.Image:
    result = Image.new("RGBA", size, (32, 36, 48, 255))
    fitted = frame.copy()
    fitted.thumbnail(size, Image.Resampling.LANCZOS)
    result.alpha_composite(fitted, ((size[0] - fitted.width) // 2, (size[1] - fitted.height) // 2))
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source, output = args.source.resolve(strict=True), args.output.resolve()
    if not source.is_dir() or source == output or source in output.parents:
        raise SystemExit("Output must be separate from the original reference directory")
    print(f"Read-only source: {source}\nOutput: {output}")
    output.mkdir(parents=True, exist_ok=True)
    all_entries, all_frames = [], {}
    for path in sorted(source.glob("*.gif"), key=lambda p: p.name.casefold()):
        entry, frames = inspect_gif(path)
        all_entries.append(entry)
        all_frames[path.stem] = frames
    if not all_entries:
        raise SystemExit("No GIF references found")
    # Public provenance uses an optional repository-local location, never the
    # generator operator's absolute --source path. Original files are not copied.
    manifest = {"schema_version": 1, "source_directory": "References/Memes", "originals_modified": False,
                "purpose": "Reference inventory, not a runtime animation manifest", "items": all_entries}
    (output / "assets-catalog.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    columns, cell_w, cell_h = 6, 184, 220
    sheet = Image.new("RGB", (columns * cell_w, ((len(all_entries) + columns - 1) // columns) * cell_h), (23, 26, 35))
    draw = ImageDraw.Draw(sheet)
    for index, entry in enumerate(all_entries):
        x, y = (index % columns) * cell_w, (index // columns) * cell_h
        frames = all_frames[Path(entry["file"]).stem]
        sheet.paste(thumbnail(frames[len(frames) // 2], (172, 172)).convert("RGB"), (x + 6, y + 6))
        draw.text((x + 8, y + 179), Path(entry["file"]).stem, font=font(16), fill=(242, 243, 248))
        draw.text((x + 8, y + 199), f'{entry["frame_count"]} frames / {entry["duration_ms"] / 1000:.2f}s', font=font(12), fill=(174, 184, 204))
    sheet.save(output / "reference-overview.jpg", quality=92)
    for name in ("干饭", "乞讨", "哭", "害羞"):
        frames = all_frames.get(name)
        if not frames:
            continue
        sequence = Image.new("RGB", (240 * 4, 270 * 2), (23, 26, 35))
        draw = ImageDraw.Draw(sequence)
        for i in range(8):
            frame_index = round(i * (len(frames) - 1) / 7)
            x, y = (i % 4) * 240, (i // 4) * 270
            sequence.paste(thumbnail(frames[frame_index], (232, 232)).convert("RGB"), (x + 4, y + 4))
            draw.text((x + 8, y + 239), f"{name} / {frame_index + 1}", font=font(16), fill=(242, 243, 248))
        sequence.save(output / f"reference-{name}-sequence.png")
    rows = ["# GIF 参考素材目录", "", f"可选本地参考目录：仓库根目录下的 `References/Memes/`。制作时共核验 {len(all_entries)} 个 GIF，原件未改动。公开仓仅保留清单与哈希，原图和联系表不随仓库分发。", "",
            "这些 GIF 用来保留角色身份、神态和动作语义；大多数有背景、文字或身体裁切，不能直接作为透明全身桌宠动画。", "",
            "运行资源需要重新生成全身关键帧，再经过透明边缘、脚点稳定、真实动作补间与 60 Hz 播放验证。", "",
            "首批：Eat（干饭）是新动作；Shy / Bomb 保留已经验证的动画。桌子哭泣场景需单独的进入、循环、退出资源，不能把现有 GIF 直接拉伸代替。", "",
            "如已取得素材使用许可，可自行放入上述本地目录；本脚本生成的概览和动作联系表仅供本机查看。缺少参考库不影响编译或运行桌宠。", "", "| 文件 | 帧数 | 总时长 | 候选行为 | 排期 |", "|---|---:|---:|---|---|"]
    for entry in all_entries:
        rows.append(f'| {entry["file"]} | {entry["frame_count"]} | {entry["duration_ms"] / 1000:.2f}s | {entry["candidate_behavior"] or "表情库"} | {entry["priority"]} |')
    rows += ["", "## 接入要求", "", "- 不带入表情包的字幕、紫色底板、背景边框。", "- 白色大头、暖棕轮廓、黄嘴黄脚、短棕身体的身份保持一致。", "- 原地动作由肢体、嘴、眼睛和身体变形完成；不以整张图片翻转或淡入淡出替代。", "- 目前普通动作持续 2 秒，首尾接同一 neutral，再站立 2 秒。", "- GIF 时间粒度通常不是精确 60 fps；目标应用使用独立 RGBA PNG 帧并按 60 Hz 时间轴播放。", "- 更完整参考并不代表一次性把全部 39 个动作都纳入首版；新动画需要逐个验收。", ""]
    (output / "assets-catalog.md").write_text("\n".join(rows), encoding="utf-8")
    print(json.dumps({"count": len(all_entries), "total_frames": sum(x["frame_count"] for x in all_entries), "output": str(output)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
