#!/usr/bin/env python3
"""Extract clean, tightly cropped rig parts from a 2x4 fishman part sheet."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image
from rembg import new_session, remove
from scipy import ndimage
from scipy.optimize import linear_sum_assignment


def extract(sheet_path: Path, output: Path, session: object) -> None:
    sheet = Image.open(sheet_path).convert("RGBA")
    cutout = remove(sheet, session=session)
    output.mkdir(parents=True, exist_ok=True)
    data = np.asarray(cutout).copy()
    labels, count = ndimage.label(data[:, :, 3] >= 24)
    components: list[tuple[int, int, float, float]] = []
    for label in range(1, count + 1):
        ys, xs = np.where(labels == label)
        if len(xs) >= 500:
            components.append((label, len(xs), float(ys.mean()), float(xs.mean())))
    components = sorted(components, key=lambda item: item[1], reverse=True)[:12]
    if len(components) < 8:
        raise ValueError(f"expected 8 foreground components in {sheet_path}")
    expected = np.array(
        [
            (y * sheet.height, x * sheet.width)
            for y in (0.16, 0.40, 0.62, 0.85)
            for x in (0.25, 0.75)
        ]
    )
    centers = np.array([(item[2], item[3]) for item in components])
    cost = np.linalg.norm(
        (expected[:, None, :] - centers[None, :, :]) / np.array([sheet.height, sheet.width]),
        axis=2,
    )
    expected_rows, component_cols = linear_sum_assignment(cost)
    assignment = dict(zip(expected_rows, component_cols))
    for part in range(8):
        selected = components[assignment[part]][0]
        keep = ndimage.binary_fill_holes(labels == selected)
        part_data = data.copy()
        part_data[:, :, 3] = np.where(keep, np.maximum(data[:, :, 3], 240), 0)
        ys, xs = np.where(keep)
        padding = 8
        box = (
            max(0, int(xs.min()) - padding),
            max(0, int(ys.min()) - padding),
            min(sheet.width, int(xs.max()) + padding + 1),
            min(sheet.height, int(ys.max()) + padding + 1),
        )
        Image.fromarray(part_data, "RGBA").crop(box).save(output / f"part_{part}.png")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("sheet", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    extract(args.sheet, args.output, new_session("u2net"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
