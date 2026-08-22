#!/usr/bin/env python3
"""Sound-effect generation backends.

Providers:
  prompt       Write prompt sidecars for manual generation (no API key)
  elevenlabs   ElevenLabs text-to-sound-effects (ELEVENLABS_API_KEY)
  import       Copy local audio files into the raw folder

The API key is read from an environment variable. Never paste a key in chat.
"""

from __future__ import annotations

import json
import os
import shutil
import time
import urllib.error
import urllib.request
from pathlib import Path

KEY_ENV = {
    "elevenlabs": "ELEVENLABS_API_KEY",
}

ELEVENLABS_URL = "https://api.elevenlabs.io/v1/sound-generation"
AUDIO_SUFFIXES = {".wav", ".mp3", ".ogg", ".flac", ".aiff", ".aif"}


def require_key(provider: str) -> str:
    env = KEY_ENV[provider]
    key = os.environ.get(env, "")
    if not key:
        raise SystemExit(
            f"Missing API key. Set the {env} environment variable and retry. "
            "Do not paste the key in chat."
        )
    return key


def compose_prompt(style: dict, entry: dict) -> str:
    parts = [entry["prompt"].rstrip("., ")]
    block = style.get("prompt_block", "").strip()
    if block:
        parts.append(block)
    negative = style.get("negative", "").strip()
    if negative:
        parts.append(f"avoid: {negative}")
    return ", ".join(parts)


def generate_seconds(entry: dict) -> float:
    if entry.get("generate_seconds"):
        return float(entry["generate_seconds"])
    low, high = entry["playback"]["duration_ms"]
    seconds = max(high / 1000.0, 0.5)
    if entry["playback"].get("loop"):
        return max(seconds, 8.0)
    # ElevenLabs rejects duration_seconds below 0.5.
    return min(max(seconds, 0.5), 30.0)


def write_sidecar(path: Path, payload: dict) -> None:
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def sibling(dest: Path, suffix: str) -> Path:
    return dest.parent / f"{dest.name}{suffix}"


def prompt_backend(entry: dict, style: dict, dest: Path, index: int) -> Path:
    dest.parent.mkdir(parents=True, exist_ok=True)
    prompt = compose_prompt(style, entry)
    text_path = sibling(dest, ".prompt.txt")
    text_path.write_text(prompt + "\n", encoding="utf-8")
    write_sidecar(
        sibling(dest, ".meta.json"),
        {
            "id": entry["id"],
            "index": index,
            "backend": "prompt",
            "prompt": prompt,
            "duration_seconds": generate_seconds(entry),
            "loop": bool(entry["playback"].get("loop")),
            "prompt_influence": entry.get("prompt_influence", 0.4),
        },
    )
    return text_path


def elevenlabs_backend(entry: dict, style: dict, dest: Path, index: int) -> Path:
    key = require_key("elevenlabs")
    prompt = compose_prompt(style, entry)
    duration = generate_seconds(entry)
    loop = bool(entry["playback"].get("loop"))
    influence = float(entry.get("prompt_influence", 0.4))
    body = {
        "text": prompt,
        "duration_seconds": duration,
        "prompt_influence": influence,
        "loop": loop,
        "model_id": "eleven_text_to_sound_v2",
    }
    request = urllib.request.Request(
        f"{ELEVENLABS_URL}?output_format=mp3_44100_128",
        data=json.dumps(body).encode("utf-8"),
        headers={
            "xi-api-key": key,
            "Content-Type": "application/json",
            "Accept": "audio/mpeg",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            audio = response.read()
    except urllib.error.HTTPError as error:
        detail = error.read().decode("utf-8", errors="replace")
        raise SystemExit(f"ElevenLabs HTTP {error.code}: {detail}") from error
    dest.parent.mkdir(parents=True, exist_ok=True)
    mp3 = sibling(dest, ".mp3")
    mp3.write_bytes(audio)
    write_sidecar(
        sibling(dest, ".meta.json"),
        {
            "id": entry["id"],
            "index": index,
            "backend": "elevenlabs",
            "prompt": prompt,
            "duration_seconds": duration,
            "loop": loop,
            "prompt_influence": influence,
        },
    )
    time.sleep(0.4)
    return mp3


def import_files(entry: dict, files: list[Path], raw_dir: Path) -> list[Path]:
    dest_dir = raw_dir / entry["id"]
    dest_dir.mkdir(parents=True, exist_ok=True)
    copied: list[Path] = []
    for index, source in enumerate(files, start=1):
        if not source.is_file():
            raise SystemExit(f"import missing file: {source}")
        if source.suffix.lower() not in AUDIO_SUFFIXES:
            raise SystemExit(f"unsupported audio suffix: {source}")
        dest = dest_dir / f"{entry['id']}_{index:02d}{source.suffix.lower()}"
        shutil.copy2(source, dest)
        write_sidecar(
            dest.parent / f"{dest.stem}.meta.json",
            {
                "id": entry["id"],
                "index": index,
                "backend": "import",
                "source": str(source.resolve()),
            },
        )
        copied.append(dest)
    return copied


def generate_entry(backend: str, entry: dict, style: dict, raw_dir: Path) -> list[Path]:
    dest_dir = raw_dir / entry["id"]
    written: list[Path] = []
    count = int(entry["playback"]["variations"])
    for index in range(1, count + 1):
        dest = dest_dir / f"{entry['id']}_{index:02d}"
        if backend == "prompt":
            written.append(prompt_backend(entry, style, dest, index))
        elif backend == "elevenlabs":
            written.append(elevenlabs_backend(entry, style, dest, index))
        else:
            raise SystemExit(f"unknown backend: {backend}")
        print(f"  {entry['id']} #{index:02d} -> {written[-1]}")
    return written
