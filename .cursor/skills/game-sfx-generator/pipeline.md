# SFX pipeline reference

## Paths

| 路径 | 用途 |
| --- | --- |
| `GodotClient/assets/audio/catalog.json` | 规格源 |
| `GodotClient/assets/audio/SPEC.md` | 人读表（由 list --write 重出） |
| `GodotClient/assets/audio/_raw/<id>/` | 原片 + prompt sidecar（gitignore） |
| `GodotClient/assets/audio/<category>/<id>_NN.ogg\|mp3` | 游戏可用文件 |
| `GodotClient/assets/audio/manifest.json` | 播放清单（id → files / bus / spatial） |

## Backends

- `elevenlabs` — `POST /v1/sound-generation`，key = `ELEVENLABS_API_KEY`。
  最短 0.5s；短 one-shot 先出 0.5s，process 再裁到 `duration_ms`。
- `prompt` — 只写 `_raw/<id>/<id>_NN.prompt.txt`，给人去网页生成。
- `import` — 把本地 wav/mp3/ogg 拷进 `_raw/<id>/`。

P0 约 48 次调用。指定时长按 40 积分/秒估。

## Encoder

`process` 优先 `libvorbis` → `.ogg`。没有则 `libmp3lame` → `.mp3`，再退 WAV。
Godot 4 都能播。

## Listen checklist

扔掉（不要 process）：

- 开头/结尾空白过长
- click / pop
- 人声、旋律、choir
- 好莱坞长尾响
- loop 接缝能听出来
- 和「饥荒式干福莱」材质不符（金属科幻、管弦）

只重跑废片：

```bash
python3 scripts/audio_pipeline.py generate --id sfx.gather.chop.wood --backend elevenlabs
python3 scripts/audio_pipeline.py process --id sfx.gather.chop.wood
python3 scripts/audio_pipeline.py package
```

改提示词：先改 `catalog.json` 的 `prompt`，再
`python3 scripts/audio_pipeline.py list --markdown --write`。

## New catalog entry

必填：`id`（`sfx.*` / `amb.*`）、`priority`、`category`、`name`、
`description`、`trigger.source`、`trigger.when`、`playback`
（`type` one-shot|loop 与 `loop` 一致、`duration_ms`、`variations`、
`bus` SFX|Ambient|Music、`spatial`、`cooldown_ms`）、`prompt`。

循环音加 `generate_seconds`（8–16）。然后 `validate`。
