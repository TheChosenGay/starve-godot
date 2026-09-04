#!/usr/bin/env python3
"""Cut dark-studio spider frames to transparent 1024² clips with a shared foot line."""

from __future__ import annotations

import hashlib
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "GodotClient/assets/creatures/spider/normal"
DST = ROOT / "GodotClient/assets/spider/anim"
CANVAS = 1024
SUBJECT_HEIGHT = 520
FOOT_Y = 820
DARK_LAB_THRESHOLD = 12.0

IMPORT_TEMPLATE = """[remap]

importer="texture"
type="CompressedTexture2D"
uid="uid://{uid}"
path="res://.godot/imported/{stem}.png-{digest}.ctex"
metadata={{
"vram_texture": false
}}

[deps]

source_file="res://assets/spider/anim/{rel}"
dest_files=["res://.godot/imported/{stem}.png-{digest}.ctex"]

[params]

compress/mode=0
compress/high_quality=false
compress/lossy_quality=0.7
compress/uastc_level=0
compress/rdo_quality_loss=0.0
compress/hdr_compression=1
compress/normal_map=0
compress/channel_pack=0
mipmaps/generate=false
mipmaps/limit=-1
roughness/mode=0
roughness/src_normal=""
process/channel_remap/red=0
process/channel_remap/green=1
process/channel_remap/blue=2
process/channel_remap/alpha=3
process/fix_alpha_border=true
process/premult_alpha=false
process/normal_map_invert_y=false
process/hdr_as_srgb=false
process/hdr_clamp_exposure=false
process/size_limit=0
detect_3d/compress_to=1
"""

JOBS: list[tuple[str, Path]] = [
    ("idle/cutout/frame_000.png", SRC / "front/idle-v3.png"),
    ("walk/cutout/frame_000.png", SRC / "front/walk-v2/00.png"),
    ("walk/cutout/frame_001.png", SRC / "front/walk-v2/01.png"),
    ("walk/cutout/frame_002.png", SRC / "front/walk-v2/02.png"),
    ("walk/cutout/frame_003.png", SRC / "front/walk-v2/03.png"),
    ("idle_side/cutout/frame_000.png", SRC / "side/idle.png"),
    ("walk_side/cutout/frame_000.png", SRC / "side/walk/00.png"),
    ("walk_side/cutout/frame_001.png", SRC / "side/walk/01.png"),
    ("walk_side/cutout/frame_002.png", SRC / "side/walk/02.png"),
    ("walk_side/cutout/frame_003.png", SRC / "side/walk/03.png"),
    ("walk_side/cutout/frame_004.png", SRC / "side/walk/04.png"),
    ("walk_side/cutout/frame_005.png", SRC / "side/walk/05.png"),
    ("walk_side/cutout/frame_006.png", SRC / "side/walk/06.png"),
    ("walk_side/cutout/frame_007.png", SRC / "side/walk/07.png"),
    ("idle_back/cutout/frame_000.png", SRC / "back/walk-v2/00.png"),
    ("walk_back/cutout/frame_000.png", SRC / "back/walk-v2/00.png"),
    ("walk_back/cutout/frame_001.png", SRC / "back/walk-v2/01.png"),
    ("walk_back/cutout/frame_002.png", SRC / "back/walk-v2/02.png"),
    ("walk_back/cutout/frame_003.png", SRC / "back/walk-v2/03.png"),
]


def cutout_dark(path: Path) -> np.ndarray:
    image = cv2.imread(str(path), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError(f"cannot read {path}")
    lab = cv2.cvtColor(image, cv2.COLOR_BGR2LAB).astype(np.float32)
    border = np.concatenate((lab[0], lab[-1], lab[:, 0], lab[:, -1]), axis=0)
    background = np.median(border, axis=0)
    distance = np.linalg.norm(lab - background, axis=2)
    close = (distance < DARK_LAB_THRESHOLD).astype(np.uint8) * 255
    filled = close.copy()
    # 只从左上角灌背景：脚下暗阴影/细腿若从底边灌会整条被吃掉。
    cv2.floodFill(filled, None, (0, 0), 128)
    alpha = np.where(filled == 128, 0, 255).astype(np.uint8)
    alpha = cv2.morphologyEx(alpha, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
    count, labels, stats, _ = cv2.connectedComponentsWithStats(alpha, 8)
    if count > 1:
        areas = stats[1:, cv2.CC_STAT_AREA]
        floor = max(80, int(areas.max() * 0.02))
        keep = {1 + i for i, area in enumerate(areas) if area >= floor}
        alpha = np.where(np.isin(labels, list(keep)), 255, 0).astype(np.uint8)
    alpha = cv2.GaussianBlur(alpha, (3, 3), 0)
    return np.dstack((image, alpha))


def normalize(bgra: np.ndarray) -> Image.Image:
    rgba = cv2.cvtColor(bgra, cv2.COLOR_BGRA2RGBA)
    image = Image.fromarray(rgba, "RGBA")
    bbox = image.getchannel("A").point(lambda value: 255 if value >= 16 else 0).getbbox()
    if bbox is None:
        raise ValueError("empty cutout")
    subject = image.crop(bbox)
    scale = SUBJECT_HEIGHT / subject.height
    width = max(1, round(subject.width * scale))
    subject = subject.resize((width, SUBJECT_HEIGHT), Image.Resampling.LANCZOS)
    output = Image.new("RGBA", (CANVAS, CANVAS))
    output.alpha_composite(subject, ((CANVAS - width) // 2, FOOT_Y - SUBJECT_HEIGHT))
    return output


def write_import(rel: str) -> None:
    source = f"res://assets/spider/anim/{rel}"
    digest = hashlib.md5(source.encode()).hexdigest()
    stem = Path(rel).stem
    uid = "s" + hashlib.sha1(source.encode()).hexdigest()[:12]
    text = IMPORT_TEMPLATE.format(uid=uid, stem=stem, digest=digest, rel=rel)
    (DST / f"{rel}.import").write_text(text)


def main() -> int:
    for rel, source in JOBS:
        if not source.exists():
            raise FileNotFoundError(source)
        dest = DST / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        frame = normalize(cutout_dark(source))
        frame.save(dest)
        write_import(rel)
        alpha = np.array(frame)[:, :, 3]
        ys, xs = np.where(alpha >= 16)
        print(
            f"{rel:32s} {frame.size} bbox=({xs.min()},{ys.min()})-({xs.max()},{ys.max()}) "
            f"h={ys.max() - ys.min() + 1} foot={(ys.max() + 1) / CANVAS:.4f}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
