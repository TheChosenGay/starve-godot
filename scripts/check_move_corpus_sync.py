#!/usr/bin/env python3
"""Fail when the two repos' movement corpus drifts apart (byte-for-byte).

为什么必须逐字节一致：语料的期望值由服务端权威侧生成，客户端是"被校验方"。
只要两仓文件不同，两端就各跑各的语料，跨端对齐就白测了 —— 而且这种不同
往往正是"有人只在一侧重新生成了语料"的信号（另一侧的实现可能已经悄悄不一致）。

用法（本地默认兄弟目录；CI 里服务端 checkout 在 .contract-server）：

    python3 scripts/check_move_corpus_sync.py --server-dir ../starve --client-dir .
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

REL = Path("testdata/move_corpus.jsonl")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def meta_of(path: Path) -> str:
    """读首行的 meta（seed/count），用于把"不一致"定位到"哪次生成"。"""
    with path.open("r", encoding="utf-8") as fh:
        first = fh.readline().strip()
    try:
        obj = json.loads(first)
        if isinstance(obj, dict) and "meta" in obj:
            return f"seed={obj['meta'].get('seed')} count={obj['meta'].get('count')}"
    except Exception:  # noqa: BLE001 - 元信息缺失不影响主判定
        pass
    return "（首行无 meta）"


def first_difference(a: Path, b: Path) -> str:
    la = a.read_text(encoding="utf-8").replace("\r\n", "\n").splitlines()
    lb = b.read_text(encoding="utf-8").replace("\r\n", "\n").splitlines()
    for i in range(max(len(la), len(lb))):
        x = la[i] if i < len(la) else "<缺行>"
        y = lb[i] if i < len(lb) else "<缺行>"
        if x != y:
            return f"第 {i + 1} 行不同：\n    服务端: {x[:180]}\n    客户端: {y[:180]}"
    return "行内容相同（差异只在行尾/编码）"


def main() -> int:
    ap = argparse.ArgumentParser(description="两仓移动语料逐字节一致性检查")
    ap.add_argument("--server-dir", default="../starve")
    ap.add_argument("--client-dir", default=".")
    args = ap.parse_args()

    server = Path(args.server_dir).resolve() / REL
    client = Path(args.client_dir).resolve() / REL
    for p in (server, client):
        if not p.is_file():
            print(f"[FAIL] 语料缺失：{p}\n       用 python3 scripts/corpus_align.py --regen 生成（权威侧生成后复制到客户端）")
            return 1

    hs, hc = sha256(server), sha256(client)
    print(f"服务端: {server}  {meta_of(server)}  sha256={hs[:16]}")
    print(f"客户端: {client}  {meta_of(client)}  sha256={hc[:16]}")
    if hs != hc:
        print("[FAIL] 两仓语料不一致 —— 跨端对齐会各跑各的，失去意义。")
        print("       " + first_difference(server, client))
        print("       修法：在服务端重新生成后复制到客户端（scripts/corpus_align.py --regen），")
        print("             若差异来自「只改了一侧的实现」，先解释清楚再重新生成。")
        return 1
    print("[ OK ] 两仓语料逐字节一致")
    return 0


if __name__ == "__main__":
    sys.exit(main())
