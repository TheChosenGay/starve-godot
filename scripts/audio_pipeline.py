#!/usr/bin/env python3
"""Starve Godot AI sound-effect pipeline.

catalog.json is the source of truth. Commands:

  validate   Check catalog schema
  list       Print or write the human spec table
  generate   Create raw takes (prompt sidecar or ElevenLabs)
  import     Drop existing wav/mp3 files into _raw/<id>/
  process    Trim / fade / loudness / ogg via ffmpeg
  package    Write playback manifest.json
  run        generate + process + package
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
AUDIO = ROOT / "GodotClient" / "assets" / "audio"
CATALOG_PATH = AUDIO / "catalog.json"
RAW_DIR = AUDIO / "_raw"
ID_RE = re.compile(r"^(sfx|amb)(\.[a-z0-9]+)+$")
PRIORITIES = {"P0", "P1", "P2"}
AUDIO_SUFFIXES = {".wav", ".mp3", ".ogg", ".flac", ".aiff", ".aif"}

sys.path.insert(0, str(Path(__file__).resolve().parent))
from audio_backends import compose_prompt, generate_entry, generate_seconds, import_files


def load_catalog(path: Path = CATALOG_PATH) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def entry_map(catalog: dict) -> dict[str, dict]:
    return {entry["id"]: entry for entry in catalog["entries"]}


def validate_catalog(catalog: dict) -> list[str]:
    errors: list[str] = []
    style = catalog.get("style")
    if not isinstance(style, dict):
        errors.append("style must be an object")
    else:
        for key in ("id", "name", "prompt_block", "sample_rate", "format"):
            if key not in style:
                errors.append(f"style missing {key}")
    seen: set[str] = set()
    entries = catalog.get("entries")
    if not isinstance(entries, list) or not entries:
        errors.append("entries must be a non-empty list")
        return errors
    for index, entry in enumerate(entries):
        prefix = f"entries[{index}]"
        if not isinstance(entry, dict):
            errors.append(f"{prefix} must be an object")
            continue
        ident = entry.get("id", "")
        if not ID_RE.match(str(ident)):
            errors.append(f"{prefix}.id invalid: {ident!r}")
        elif ident in seen:
            errors.append(f"duplicate id: {ident}")
        seen.add(str(ident))
        if entry.get("priority") not in PRIORITIES:
            errors.append(f"{ident or prefix}: priority must be P0/P1/P2")
        if not entry.get("category") or not entry.get("name") or not entry.get("prompt"):
            errors.append(f"{ident or prefix}: category/name/prompt required")
        trigger = entry.get("trigger")
        if not isinstance(trigger, dict) or "source" not in trigger or "when" not in trigger:
            errors.append(f"{ident or prefix}: trigger.source/when required")
        playback = entry.get("playback")
        if not isinstance(playback, dict):
            errors.append(f"{ident or prefix}: playback required")
            continue
        duration = playback.get("duration_ms")
        if (
            not isinstance(duration, list)
            or len(duration) != 2
            or not all(isinstance(value, int) and value > 0 for value in duration)
            or duration[0] > duration[1]
        ):
            errors.append(f"{ident or prefix}: duration_ms must be [low, high] ints")
        variations = playback.get("variations")
        if not isinstance(variations, int) or variations < 1:
            errors.append(f"{ident or prefix}: variations must be >= 1")
        if playback.get("type") not in {"one-shot", "loop"}:
            errors.append(f"{ident or prefix}: type must be one-shot or loop")
        if bool(playback.get("loop")) != (playback.get("type") == "loop"):
            errors.append(f"{ident or prefix}: loop flag must match type")
        if playback.get("bus") not in {"SFX", "Ambient", "Music"}:
            errors.append(f"{ident or prefix}: bus must be SFX/Ambient/Music")
    return errors


def select_entries(catalog: dict, priority: str | None, ids: list[str]) -> list[dict]:
    selected = catalog["entries"]
    if priority:
        selected = [entry for entry in selected if entry["priority"] == priority]
    if ids:
        wanted = set(ids)
        known = entry_map(catalog)
        missing = sorted(wanted - set(known))
        if missing:
            raise SystemExit(f"unknown id(s): {', '.join(missing)}")
        selected = [entry for entry in selected if entry["id"] in wanted]
    return selected


def markdown_spec(catalog: dict) -> str:
    style = catalog["style"]
    counts = Counter(entry["priority"] for entry in catalog["entries"])
    lines = [
        "# 音效规格表",
        "",
        "机器可读源是同目录 `catalog.json`。改规格只改 JSON，再用",
        "`python3 scripts/audio_pipeline.py list --markdown --write` 重出本表。",
        "",
        "## 声音风格",
        "",
        f"- 名称：`{style['id']}` / {style['name']}",
        f"- 提示词块：{style['prompt_block']}",
        f"- 禁止：{style.get('negative', '')}",
        f"- 导出：{style['sample_rate']} Hz / {style.get('channels', 1)} ch / {style['format']}",
        f"- 响度：SFX {style.get('sfx_lufs', -18)} LUFS，环境 {style.get('amb_lufs', -23)} LUFS，峰值 {style.get('peak_db', -3)} dB",
        "",
        "## 管线",
        "",
        "```bash",
        "python3 scripts/audio_pipeline.py validate",
        "python3 scripts/audio_pipeline.py generate --priority P0          # 无 key 时写 prompt",
        "python3 scripts/audio_pipeline.py generate --priority P0 --backend elevenlabs",
        "python3 scripts/audio_pipeline.py import --id sfx.ui.click --files click_a.wav click_b.wav",
        "python3 scripts/audio_pipeline.py process --priority P0",
        "python3 scripts/audio_pipeline.py package",
        "```",
        "",
        "后处理优先 `libvorbis`（`.ogg`），本机没有该编码器时退到 `libmp3lame`（`.mp3`）或 WAV。Godot 4 都能播。",
        "",
        f"条目 {len(catalog['entries'])}：P0 {counts.get('P0', 0)} / P1 {counts.get('P1', 0)} / P2 {counts.get('P2', 0)}。",
        "",
    ]
    current_priority = ""
    for entry in catalog["entries"]:
        if entry["priority"] != current_priority:
            if current_priority:
                lines.append("")
            current_priority = entry["priority"]
            lines.extend([f"## {current_priority}", "", "| ID | 名称 | 触发 | 时长 | 变体 | 空间 | 总线 |", "| --- | --- | --- | --- | --- | --- | --- |"])
        play = entry["playback"]
        low, high = play["duration_ms"]
        trigger = f"{entry['trigger']['source']} / {entry['trigger']['when']}".replace("|", "/")
        lines.append(
            f"| `{entry['id']}` | {entry['name']} | {trigger} | {low}–{high}ms | {play['variations']} | "
            f"{'是' if play['spatial'] else '否'} | {play['bus']} |"
        )
    lines.append("")
    lines.extend(["", "## 提示词", ""])
    for entry in catalog["entries"]:
        lines.append(f"### `{entry['id']}`")
        lines.append("")
        lines.append(entry["description"])
        lines.append("")
        lines.append(f"- 分类：{entry['category']} / {entry['priority']}")
        lines.append(f"- 完整提示：`{compose_prompt(catalog['style'], entry)}`")
        lines.append("")
    return "\n".join(lines).rstrip() + "\n"


def cmd_validate(_args: argparse.Namespace) -> int:
    errors = validate_catalog(load_catalog())
    if errors:
        print("catalog invalid:")
        for error in errors:
            print(f"  - {error}")
        return 1
    catalog = load_catalog()
    print(f"catalog ok: {len(catalog['entries'])} entries")
    return 0


def cmd_list(args: argparse.Namespace) -> int:
    catalog = load_catalog()
    errors = validate_catalog(catalog)
    if errors:
        raise SystemExit("catalog invalid; run validate")
    selected = select_entries(catalog, args.priority, args.id)
    if args.markdown:
        text = markdown_spec({"style": catalog["style"], "entries": selected if args.priority or args.id else catalog["entries"]})
        if args.write:
            dest = AUDIO / "SPEC.md" if args.write is True else Path(args.write)
            dest.write_text(text, encoding="utf-8")
            print(f"wrote {dest}")
        else:
            sys.stdout.write(text)
        return 0
    for entry in selected:
        play = entry["playback"]
        print(
            f"{entry['priority']:3} {entry['id']:28} {play['duration_ms'][0]:3}-{play['duration_ms'][1]:<4}ms "
            f"x{play['variations']}  {entry['name']}"
        )
    print(f"{len(selected)} entries")
    return 0


def cmd_generate(args: argparse.Namespace) -> int:
    catalog = load_catalog()
    errors = validate_catalog(catalog)
    if errors:
        raise SystemExit("catalog invalid; run validate")
    selected = select_entries(catalog, args.priority, args.id)
    if not selected:
        raise SystemExit("no entries selected")
    backend = args.backend
    RAW_DIR.mkdir(parents=True, exist_ok=True)
    (RAW_DIR / ".gdignore").write_text("", encoding="utf-8")
    print(f"generate {len(selected)} entries via {backend}")
    for entry in selected:
        generate_entry(backend, entry, catalog["style"], RAW_DIR)
    return 0


def cmd_import(args: argparse.Namespace) -> int:
    catalog = load_catalog()
    entry = entry_map(catalog).get(args.id)
    if entry is None:
        raise SystemExit(f"unknown id: {args.id}")
    files = [Path(item).expanduser() for item in args.files]
    copied = import_files(entry, files, RAW_DIR)
    (RAW_DIR / ".gdignore").write_text("", encoding="utf-8")
    print(f"imported {len(copied)} file(s) into {RAW_DIR / entry['id']}")
    return 0


def ffmpeg_bin() -> str:
    path = shutil.which("ffmpeg")
    if not path:
        raise SystemExit("ffmpeg not found; install it to process audio")
    return path


def encoder_args(ffmpeg: str) -> tuple[str, list[str]]:
    listing = subprocess.run(
        [ffmpeg, "-hide_banner", "-encoders"],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
    ).stdout
    if re.search(r"\blibvorbis\b", listing):
        return "ogg", ["-c:a", "libvorbis", "-q:a", "4"]
    if re.search(r"\blibmp3lame\b", listing):
        return "mp3", ["-c:a", "libmp3lame", "-q:a", "4"]
    return "wav", ["-c:a", "pcm_s16le"]


def raw_audio_files(entry_id: str) -> list[Path]:
    folder = RAW_DIR / entry_id
    if not folder.is_dir():
        return []
    return sorted(
        path
        for path in folder.iterdir()
        if path.is_file() and path.suffix.lower() in AUDIO_SUFFIXES
    )


def process_file(ffmpeg: str, source: Path, dest: Path, entry: dict, style: dict, encode: list[str]) -> None:
    dest.parent.mkdir(parents=True, exist_ok=True)
    play = entry["playback"]
    loop = bool(play.get("loop"))
    high_ms = play["duration_ms"][1]
    target_lufs = style.get("amb_lufs" if loop or play["bus"] == "Ambient" else "sfx_lufs", -18)
    peak = min(float(style.get("peak_db", -3)), -1.0)
    filters = ["highpass=f=90"]
    if not loop:
        filters.append(
            "silenceremove=start_periods=1:start_threshold=-38dB:start_silence=0.01:"
            "stop_periods=1:stop_threshold=-38dB:stop_silence=0.03"
        )
        filters.append(f"atrim=0:{max(high_ms / 1000.0, 0.12):.3f}")
        filters.append("afade=t=in:st=0:d=0.008")
        filters.append("areverse")
        filters.append("afade=t=in:st=0:d=0.012")
        filters.append("areverse")
    filters.append(f"loudnorm=I={target_lufs}:TP={peak}:LRA=8")
    command = [
        ffmpeg,
        "-y",
        "-i",
        str(source),
        "-af",
        ",".join(filters),
        "-ar",
        str(style["sample_rate"]),
        "-ac",
        str(style.get("channels", 1)),
        *encode,
        str(dest),
    ]
    result = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
    if result.returncode != 0:
        raise SystemExit(f"ffmpeg failed for {source.name}:\n{result.stderr[-800:]}")


def cmd_process(args: argparse.Namespace) -> int:
    catalog = load_catalog()
    selected = select_entries(catalog, args.priority, args.id)
    ffmpeg = ffmpeg_bin()
    suffix, encode = encoder_args(ffmpeg)
    print(f"encoder {encode[1]} -> .{suffix}")
    processed = 0
    for entry in selected:
        sources = raw_audio_files(entry["id"])
        if not sources:
            print(f"  skip {entry['id']}: no raw audio")
            continue
        for source in sources:
            dest = AUDIO / entry["category"] / f"{source.stem}.{suffix}"
            process_file(ffmpeg, source, dest, entry, catalog["style"], encode)
            print(f"  {source.name} -> {dest.relative_to(ROOT)}")
            processed += 1
    print(f"processed {processed} file(s)")
    return 0


def packaged_files(entry: dict) -> list[str]:
    folder = AUDIO / entry["category"]
    if not folder.is_dir():
        return []
    prefix = entry["id"]
    return sorted(
        f"{entry['category']}/{path.name}"
        for path in folder.iterdir()
        if path.is_file() and path.stem.startswith(f"{prefix}_") and path.suffix.lower() in {".ogg", ".wav", ".mp3"}
    )


def cmd_package(_args: argparse.Namespace) -> int:
    catalog = load_catalog()
    errors = validate_catalog(catalog)
    if errors:
        raise SystemExit("catalog invalid; run validate")
    sounds = []
    ready = 0
    for entry in catalog["entries"]:
        files = packaged_files(entry)
        if files:
            ready += 1
        play = entry["playback"]
        sounds.append(
            {
                "id": entry["id"],
                "priority": entry["priority"],
                "category": entry["category"],
                "name": entry["name"],
                "bus": play["bus"],
                "spatial": play["spatial"],
                "loop": play["loop"],
                "cooldown_ms": play["cooldown_ms"],
                "volume_db": play.get("volume_db", 0),
                "files": files,
                "trigger": entry["trigger"],
            }
        )
    manifest = {
        "version": 1,
        "style": catalog["style"]["id"],
        "ready": ready,
        "total": len(sounds),
        "sounds": sounds,
    }
    dest = AUDIO / "manifest.json"
    dest.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {dest.relative_to(ROOT)} ({ready}/{len(sounds)} ready)")
    return 0


def cmd_run(args: argparse.Namespace) -> int:
    generate_ns = argparse.Namespace(priority=args.priority, id=args.id, backend=args.backend)
    process_ns = argparse.Namespace(priority=args.priority, id=args.id)
    if cmd_generate(generate_ns) != 0:
        return 1
    if args.backend != "prompt" and cmd_process(process_ns) != 0:
        return 1
    return cmd_package(argparse.Namespace())


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("validate", help="check catalog.json")

    list_p = sub.add_parser("list", help="print spec table")
    list_p.add_argument("--priority", choices=sorted(PRIORITIES))
    list_p.add_argument("--id", action="append", default=[])
    list_p.add_argument("--markdown", action="store_true")
    list_p.add_argument("--write", nargs="?", const=True, default=False, help="write SPEC.md")

    gen_p = sub.add_parser("generate", help="create raw takes")
    gen_p.add_argument("--priority", choices=sorted(PRIORITIES))
    gen_p.add_argument("--id", action="append", default=[])
    gen_p.add_argument("--backend", choices=("prompt", "elevenlabs"), default="prompt")

    imp = sub.add_parser("import", help="copy local audio into _raw/<id>/")
    imp.add_argument("--id", required=True)
    imp.add_argument("--files", nargs="+", required=True)

    proc = sub.add_parser("process", help="ffmpeg post-process raw -> ogg")
    proc.add_argument("--priority", choices=sorted(PRIORITIES))
    proc.add_argument("--id", action="append", default=[])

    sub.add_parser("package", help="write manifest.json")

    run = sub.add_parser("run", help="generate + process + package")
    run.add_argument("--priority", choices=sorted(PRIORITIES))
    run.add_argument("--id", action="append", default=[])
    run.add_argument("--backend", choices=("prompt", "elevenlabs"), default="prompt")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    command = {
        "validate": cmd_validate,
        "list": cmd_list,
        "generate": cmd_generate,
        "import": cmd_import,
        "process": cmd_process,
        "package": cmd_package,
        "run": cmd_run,
    }[args.command]
    return command(args)


if __name__ == "__main__":
    raise SystemExit(main())
