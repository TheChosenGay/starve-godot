#!/usr/bin/env python3
"""Normalize transparent animation frames to a stable center and foot line."""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image


def normalize(path: Path, target_height: int, foot_y: int) -> None:
    image = Image.open(path).convert("RGBA")
    alpha = image.getchannel("A")
    bbox = alpha.point(lambda value: 255 if value >= 16 else 0).getbbox()
    if bbox is None:
        return
    subject = image.crop(bbox)
    scale = target_height / subject.height
    width = max(1, round(subject.width * scale))
    subject = subject.resize((width, target_height), Image.Resampling.LANCZOS)
    output = Image.new("RGBA", image.size)
    output.alpha_composite(subject, ((image.width - width) // 2, foot_y - target_height))
    output.save(path)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--height", type=int, required=True)
    parser.add_argument("--foot-y", type=int, required=True)
    args = parser.parse_args()
    for path in sorted(args.directory.glob("frame_*.png")):
        normalize(path, args.height, args.foot_y)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
