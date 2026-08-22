---
name: game-sfx-generator
description: >-
  Generates and packages game-ready sound effects from catalog.json: confirm a
  spec slice, generate via ElevenLabs or import files, ffmpeg post-process,
  write manifest.json. Use when the user asks for SFX, Foley, 音效, 生成音效,
  出音效, ambient loops, or to run the starve-godot audio pipeline.
---

# Game SFX Generator

starve-godot 音效管线。**未确认规格切片前禁止 generate。**
用户点名阶段（generate / process / package / 只改一条）就跳到那一步。

仓库根：含 `GodotClient/assets/audio/catalog.json` 的 git 根。
命令一律从该根目录跑。

## Pipeline

0. **读规格** — `catalog.json` 是唯一数据源。先
   `python3 scripts/audio_pipeline.py validate`，再
   `python3 scripts/audio_pipeline.py list`（可加 `--priority P0` / `--id <id>`）。
   人读表是 `GodotClient/assets/audio/SPEC.md`。风格块用 catalog 里的
   `style.prompt_block`，不要另写一套。
1. **Spec + confirm（闸门）** — 把本批条目短列表给用户（id / 时长 / 变体 /
   触发）。问「要改什么」。未确认禁止 ElevenLabs。
   「生成 P0 / 按这个出 / 开干」算确认。新声音必须先写入 catalog 再生成。
2. **Generate** — 默认 `--backend elevenlabs`（读环境变量
   `ELEVENLABS_API_KEY`）。无 key 或用户要手工时用 `prompt` 后端。
   库音效走 `import`。见 [pipeline.md](pipeline.md)。
3. **Listen** — 生成后列出 `_raw/<id>/` 文件，请用户听。废片：太长、
   带人声/旋律、电影尾响、循环接缝。不勾/说扔掉的不要 process。
4. **Process + package** — `process` 切静音、fade、响度对齐；`package`
   写 `manifest.json`。改了 catalog 再
   `list --markdown --write` 重出 SPEC.md。

## Hard rules

- **No generate before confirm.** 「做点音效」不是绿灯。
- **不要在聊天里要 API key。** 缺失就说设 `ELEVENLABS_API_KEY`，用 `prompt`
  后端继续。
- **事件 id 对齐协议。** 新 id 必须能挂到 ActionKind / CombatImpact /
  HealthChanged / HUD / WeatherView。不要发明没挂点的声音。
- **用仓库脚本，不要现场写生成代码。**
- **只做音效资产。** 不在本 skill 里写 `SfxService` / AudioBus，除非用户明确要接线。
- 一条 `--id` 失败不要整批重跑；改 prompt 写回 catalog 后再出那一条。

## Defaults

| 用户没指定时 | 默认 |
| --- | --- |
| 范围 | `P0` |
| 后端 | `elevenlabs`（无 key 则 `prompt`） |
| 风格 | catalog `starve-foley`，不要换成科幻/管弦 |

## 对用户怎么说（闸门）

```text
本批按 catalog 出：

- 范围: P0（17 条）或 列出 id
- 后端: elevenlabs / prompt / import
- 风格: starve-foley

要改哪一条直接说。确认后开始生成。
```

## Scripts

从仓库根执行：

| 命令 | 作用 |
| --- | --- |
| `python3 scripts/audio_pipeline.py validate` | 校验 catalog |
| `python3 scripts/audio_pipeline.py list [--priority P0] [--id ID]` | 列规格 |
| `python3 scripts/audio_pipeline.py generate --priority P0 --backend elevenlabs` | 出原片 |
| `python3 scripts/audio_pipeline.py import --id ID --files a.wav b.wav` | 导入现成文件 |
| `python3 scripts/audio_pipeline.py process --priority P0` | 后处理 |
| `python3 scripts/audio_pipeline.py package` | 写 manifest |
| `python3 scripts/audio_pipeline.py run --priority P0 --backend elevenlabs` | generate+process+package |

细节、目录和审听标准见 [pipeline.md](pipeline.md)。
