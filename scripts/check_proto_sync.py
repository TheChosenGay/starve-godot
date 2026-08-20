#!/usr/bin/env python3
"""Fail when client protobuf sources drift from the server contract."""

from __future__ import annotations

import argparse
import difflib
import re
from pathlib import Path


PAIRS = (
    ("proto/game.proto", "pkg/proto/game/game.proto"),
    ("proto/message.proto", "pkg/proto/message.proto"),
)


def normalized_lines(path: Path) -> list[str]:
    return path.read_text(encoding="utf-8").replace("\r\n", "\n").splitlines(keepends=True)


def route_contract(path: Path, pattern: str) -> dict[str, str]:
    text = path.read_text(encoding="utf-8")
    return {match.group(1): match.group(2) for match in re.finditer(pattern, text, re.MULTILINE)}


def check_routes(client_root: Path, server_root: Path) -> bool:
    client_path = client_root / "Starve.Protocol/Routes.cs"
    server_path = server_root / "pkg/proto/routes.go"
    client_routes = route_contract(
        client_path,
        r'^\s*public const string (\w+)\s*=\s*"([^"]+)";',
    )
    server_routes = route_contract(
        server_path,
        r'^\s*Route(\w+)\s*=\s*"([^"]+)"',
    )
    if client_routes == server_routes:
        print(f"routes synchronized: {len(client_routes)} routes")
        return True
    print("route contract drift:")
    for name in sorted(client_routes.keys() | server_routes.keys()):
        if client_routes.get(name) != server_routes.get(name):
            print(
                f"  {name}: server={server_routes.get(name)!r} "
                f"client={client_routes.get(name)!r}"
            )
    return False


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--server-dir", type=Path, required=True)
    args = parser.parse_args()

    client_root = Path(__file__).resolve().parent.parent
    server_root = args.server_dir.resolve()
    failed = False
    for client_relative, server_relative in PAIRS:
        client_path = client_root / client_relative
        server_path = server_root / server_relative
        if not client_path.is_file() or not server_path.is_file():
            print(f"missing contract file: client={client_path} server={server_path}")
            failed = True
            continue
        client_lines = normalized_lines(client_path)
        server_lines = normalized_lines(server_path)
        if client_lines == server_lines:
            print(f"contract synchronized: {client_relative}")
            continue
        failed = True
        print(f"contract drift: {client_relative} != {server_relative}")
        diff = difflib.unified_diff(
            server_lines,
            client_lines,
            fromfile=str(server_path),
            tofile=str(client_path),
        )
        for index, line in enumerate(diff):
            if index >= 120:
                print("... diff truncated")
                break
            print(line, end="")
    if not check_routes(client_root, server_root):
        failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
