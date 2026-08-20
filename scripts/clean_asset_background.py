#!/usr/bin/env python3
"""Segment a centered game asset and emit a defringed transparent PNG."""

from __future__ import annotations

import argparse
from pathlib import Path

import cv2
import numpy as np


def largest_component(mask: np.ndarray) -> np.ndarray:
    count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
    if count <= 1:
        return mask
    largest = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    return np.where(labels == largest, 255, 0).astype(np.uint8)


def paper_mask(image: np.ndarray) -> np.ndarray:
    lab = cv2.cvtColor(image, cv2.COLOR_BGR2LAB).astype(np.float32)
    border = np.concatenate((lab[0], lab[-1], lab[:, 0], lab[:, -1]), axis=0)
    background = np.median(border, axis=0)
    distance = np.linalg.norm(lab - background, axis=2)
    return np.where(distance >= 34, 255, 0).astype(np.uint8)


def segment(path: Path, strategy: str) -> np.ndarray:
    image = cv2.imread(str(path), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError(f"cannot read {path}")
    if strategy == "paper":
        alpha = paper_mask(image)
    else:
        mask = np.full(image.shape[:2], cv2.GC_PR_BGD, dtype=np.uint8)
    if strategy == "color":
        hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
        saturation = hsv[:, :, 1]
        value = hsv[:, :, 2]
        # 原图背景是低饱和灰纸；主体有颜色或深色墨线。
        candidate = (saturation >= 35) | (value <= 90)
        strong = (saturation >= 70) | (value <= 55)
        mask[candidate] = cv2.GC_PR_FGD
        mask[strong] = cv2.GC_FGD
    elif strategy == "rect":
        height, width = image.shape[:2]
        inset_x, inset_y = width // 20, height // 20
        mask[inset_y : height - inset_y, inset_x : width - inset_x] = cv2.GC_PR_FGD
        hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
        strong = (hsv[:, :, 1] >= 55) | (hsv[:, :, 2] <= 65)
        mask[strong] = cv2.GC_FGD
    if strategy != "paper":
        border = max(2, min(image.shape[:2]) // 80)
        mask[:border, :] = cv2.GC_BGD
        mask[-border:, :] = cv2.GC_BGD
        mask[:, :border] = cv2.GC_BGD
        mask[:, -border:] = cv2.GC_BGD
        cv2.grabCut(
            image,
            mask,
            None,
            np.zeros((1, 65)),
            np.zeros((1, 65)),
            5,
            cv2.GC_INIT_WITH_MASK,
        )
        alpha = np.where(
            (mask == cv2.GC_FGD) | (mask == cv2.GC_PR_FGD), 255, 0
        ).astype(np.uint8)
    alpha = cv2.morphologyEx(alpha, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))
    alpha = largest_component(alpha)
    # 填满主体墨线围成的低饱和区域（眼白、灯泡、金属高光）。
    inverse = cv2.bitwise_not(alpha)
    flood = inverse.copy()
    cv2.floodFill(flood, None, (0, 0), 0)
    alpha = cv2.bitwise_or(alpha, flood)
    alpha = cv2.morphologyEx(alpha, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
    # 收一像素去掉原灰底污染，再做轻微抗锯齿。
    alpha = cv2.erode(alpha, np.ones((3, 3), np.uint8), iterations=1)
    alpha = cv2.GaussianBlur(alpha, (3, 3), 0)
    return np.dstack((image, alpha))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--strategy", choices=("color", "rect", "paper"), default="color")
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if not cv2.imwrite(str(args.output), segment(args.input, args.strategy)):
        raise RuntimeError(f"cannot write {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
