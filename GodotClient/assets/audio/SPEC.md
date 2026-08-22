# 音效规格表

机器可读源是同目录 `catalog.json`。改规格只改 JSON，再用
`python3 scripts/audio_pipeline.py list --markdown --write` 重出本表。

## 声音风格

- 名称：`starve-foley` / 饥荒式干福莱
- 提示词块：Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech
- 禁止：music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam
- 导出：48000 Hz / 1 ch / ogg
- 响度：SFX -18.0 LUFS，环境 -23.0 LUFS，峰值 -3.0 dB

## 管线

```bash
python3 scripts/audio_pipeline.py validate
python3 scripts/audio_pipeline.py generate --priority P0          # 无 key 时写 prompt
python3 scripts/audio_pipeline.py generate --priority P0 --backend elevenlabs
python3 scripts/audio_pipeline.py import --id sfx.ui.click --files click_a.wav click_b.wav
python3 scripts/audio_pipeline.py process --priority P0
python3 scripts/audio_pipeline.py package
```

后处理优先 `libvorbis`（`.ogg`），本机没有该编码器时退到 `libmp3lame`（`.mp3`）或 WAV。Godot 4 都能播。

条目 48：P0 17 / P1 26 / P2 5。

## P0

| ID | 名称 | 触发 | 时长 | 变体 | 空间 | 总线 |
| --- | --- | --- | --- | --- | --- | --- |
| `sfx.ui.click` | UI 点击 | Hud / button.pressed | 80–140ms | 3 | 否 | SFX |
| `sfx.ui.deny` | 操作拒绝 | GameRoot.TryAct / ActionOutcome / rejected or missing tool | 140–220ms | 2 | 否 | SFX |
| `sfx.ui.craft.open` | 打开制作抽屉 | Hud.ToggleCraft / drawer opens | 180–280ms | 2 | 否 | SFX |
| `sfx.ui.craft.start` | 开始制作 | GameRoot.DoCraftAsync / BeginCraft accepted | 200–320ms | 2 | 否 | SFX |
| `sfx.ui.craft.done` | 制作完成 | ActionOutcome / COMPLETED + ActionKind.Craft | 220–360ms | 2 | 否 | SFX |
| `sfx.ui.craft.fail` | 制作失败 | GameRoot.DoCraftAsync / ActionOutcome / craft failed | 160–260ms | 2 | 否 | SFX |
| `sfx.player.footstep.grass` | 草地脚步 | RigNode walk cycle / foot plant frames | 90–160ms | 4 | 是 | SFX |
| `sfx.player.swing` | 挥击起手 | ActionPresentationController / ActionKind.Attack/Chop/Mine start | 140–220ms | 3 | 是 | SFX |
| `sfx.gather.chop.wood` | 砍树命中 | ActionKind.Chop impact / windup reaches AttackImpactMs | 160–260ms | 4 | 是 | SFX |
| `sfx.gather.mine.stone` | 挖矿命中 | ActionKind.Mine impact / windup reaches AttackImpactMs | 160–260ms | 4 | 是 | SFX |
| `sfx.gather.pick.berry` | 采集浆果 | ActionKind.Pick / pick start or complete | 140–220ms | 3 | 是 | SFX |
| `sfx.gather.pickup` | 拾取掉落 | GameRoot.TryAct Pickup / Pickup command sent | 100–180ms | 3 | 是 | SFX |
| `sfx.combat.hit.flesh` | 命中血肉 | ImpactPresentationController / CombatImpactResult.Hit | 160–260ms | 4 | 是 | SFX |
| `sfx.combat.miss` | 未命中 | ImpactPresentationController.PresentNonHit / Miss | 120–200ms | 3 | 是 | SFX |
| `sfx.combat.blocked` | 格挡 | ImpactPresentationController.PresentNonHit / Blocked | 140–220ms | 3 | 是 | SFX |
| `sfx.player.death` | 死亡转魂 | RigNode.SetDead / dead becomes true | 400–700ms | 2 | 否 | SFX |
| `sfx.player.sleep` | 入睡 | Hud.SleepPressed / ActionKind.Sleep / sleep starts | 280–450ms | 2 | 否 | SFX |

## P1

| ID | 名称 | 触发 | 时长 | 变体 | 空间 | 总线 |
| --- | --- | --- | --- | --- | --- | --- |
| `sfx.player.footstep.stone` | 石地脚步 | RigNode walk cycle / foot plant on stone biome | 90–160ms | 4 | 是 | SFX |
| `sfx.player.footstep.mud` | 泥地脚步 | RigNode walk cycle / foot plant on swamp or wet ground | 100–180ms | 4 | 是 | SFX |
| `sfx.player.haunt` | 作祟 | GameRoot.TryHaunt / ActionKind.Haunt start | 350–600ms | 2 | 是 | SFX |
| `sfx.player.revive` | 复活 | RigNode.SetDead / dead becomes false | 350–550ms | 2 | 否 | SFX |
| `sfx.build.place` | 放置建筑 | GameRoot place confirm / Place sent | 180–280ms | 2 | 是 | SFX |
| `sfx.build.demolish` | 拆除 | Hud.DemolishPressed / demolish | 200–320ms | 2 | 是 | SFX |
| `sfx.world.campfire` | 火堆燃烧 | FirePitFire / placed BuildingKind.Campfire in range | 8000–12000ms | 1 | 是 | Ambient |
| `sfx.player.eat` | 进食 | Hud.BagUsePressed / use food item | 180–280ms | 2 | 否 | SFX |
| `sfx.combat.immune` | 免疫 | ImpactPresentationController.PresentNonHit / Immune | 140–220ms | 2 | 是 | SFX |
| `sfx.vitals.heal` | 回血 | WorldEvent.HealthChanged / local player delta > 0 | 180–280ms | 2 | 否 | SFX |
| `sfx.vitals.starve` | 饥饿掉血 | WorldEvent.HealthChanged / Starvation | 160–240ms | 2 | 否 | SFX |
| `sfx.vitals.weather` | 天气伤害 | WorldEvent.HealthChanged / Weather | 160–260ms | 2 | 否 | SFX |
| `amb.day` | 白天环境 | World clock / WeatherState.phase / daytime | 12000–16000ms | 1 | 否 | Ambient |
| `amb.night` | 夜晚环境 | World clock / WeatherState.phase / nighttime | 12000–16000ms | 1 | 否 | Ambient |
| `amb.rain` | 降雨 | WeatherView / WeatherSummary.Rain / rain > 0.15 | 10000–14000ms | 1 | 否 | Ambient |
| `sfx.creature.spider.attack` | 蜘蛛攻击 | ActionState Attack + Creature.Spider / spider attack start | 180–280ms | 3 | 是 | SFX |
| `sfx.creature.spider.hit` | 蜘蛛受击 | CombatImpact HIT + Creature.Spider / spider is hit | 140–220ms | 3 | 是 | SFX |
| `sfx.creature.wolf.attack` | 狼攻击 | ActionState Attack + Creature.Wolf / wolf attack start | 180–280ms | 3 | 是 | SFX |
| `sfx.creature.rabbit.flee` | 兔子逃跑 | Creature.state = flee / rabbit flees | 140–220ms | 2 | 是 | SFX |
| `sfx.creature.lizard.attack` | 蜥蜴攻击 | ActionState Attack + Creature.Lizard / lizard attack start | 180–280ms | 3 | 是 | SFX |
| `sfx.world.thunder` | 雷鸣 | WeatherView.OnLightning / lightning flash | 800–1600ms | 3 | 否 | Ambient |
| `sfx.vitals.poison` | 中毒掉血 | WorldEvent.HealthChanged / Poison | 160–260ms | 2 | 否 | SFX |
| `sfx.ui.equip` | 装备 | Hud.BagEquipPressed / equip or unequip | 120–200ms | 2 | 否 | SFX |
| `sfx.ui.drop` | 丢弃 | Hud.BagDropPressed / drop item | 120–200ms | 2 | 是 | SFX |
| `sfx.player.sleep.cancel` | 醒来 | Hud.CancelSleepPressed / sleep canceled | 160–260ms | 2 | 否 | SFX |
| `sfx.action.cancel` | 动作取消 | ActionPresentationController.ApplyOutcome / CANCELED and reason != Dead | 100–180ms | 2 | 否 | SFX |

## P2

| ID | 名称 | 触发 | 时长 | 变体 | 空间 | 总线 |
| --- | --- | --- | --- | --- | --- | --- |
| `sfx.creature.boar.attack` | 野猪攻击 | ActionState Attack + Creature.Boar / boar attack start | 200–320ms | 3 | 是 | SFX |
| `sfx.creature.deer.flee` | 鹿逃跑 | Creature.state = flee / deer flees | 180–280ms | 2 | 是 | SFX |
| `amb.winter` | 冬季环境 | Season.Winter / winter daytime | 12000–16000ms | 1 | 否 | Ambient |
| `amb.snow` | 降雪 | WeatherView.SetWeather / season == Winter | 10000–14000ms | 1 | 否 | Ambient |
| `sfx.world.alchemy` | 炼金台运转 | EntityLayer.EnsureAlchemy / alchemy engine in range | 8000–12000ms | 1 | 是 | Ambient |


## 提示词

### `sfx.ui.click`

制作槽、背包按钮、火/墙/睡等 HUD 按下

- 分类：ui / P0
- 完整提示：`short dry wooden UI click, tiny taut leather tap on a small board, one-shot only, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.ui.deny`

TryAct 提前返回、缺工具、ActionOutcome REJECTED

- 分类：ui / P0
- 完整提示：`short muffled wooden thud deny, dull blocked tap, no voice, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.ui.craft.open`

Hud.ToggleCraft / 按 C

- 分类：ui / P0
- 完整提示：`small wooden drawer sliding open, dry parchment rustle, short one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.ui.craft.start`

DoCraftAsync 成功提交 BeginCraft，ActionKind.Craft 起手

- 分类：ui / P0
- 完整提示：`soft wooden workbench start, light flint click and cloth fold, short crafting begin, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.ui.craft.done`

ActionOutcome COMPLETED 且 kind=Craft

- 分类：ui / P0
- 完整提示：`satisfying short wooden craft complete, light flint chime without melody, dry one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.ui.craft.fail`

DoCraftAsync 失败或 Craft 被 REJECTED

- 分类：ui / P0
- 完整提示：`dry failed craft clack, two small wood pieces missing each other, no voice, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.player.footstep.grass`

鱼人 walk 循环的接触帧；默认地形

- 分类：player / P0
- 完整提示：`short dry grass footstep, light leather sole on packed dirt and straw, one step only, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.player.swing`

ActionKind Attack/Chop/Mine 进入 WINDUP，对齐攻击动画开头

- 分类：player / P0
- 完整提示：`short dry whoosh of a small wooden tool swung through air, cloth rustle, no impact yet, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.gather.chop.wood`

Chop 接触帧，约 AttackImpactMs=400

- 分类：gather / P0
- 完整提示：`small hatchet hitting a thin dry tree trunk, short woody chop, bark chips, no echo, one hit only, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.gather.mine.stone`

Mine 接触帧，约 AttackImpactMs=400

- 分类：gather / P0
- 完整提示：`small pickaxe ticking flint and dry stone, short rocky mine hit, gravel fall, no echo, one hit only, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.gather.pick.berry`

ActionKind.Pick 起手/完成

- 分类：gather / P0
- 完整提示：`soft berry bush pick, leaves and twig snap, tiny fruit pluck, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.gather.pickup`

Commands.Pickup，Lootable

- 分类：gather / P0
- 完整提示：`short item pickup, small wood and cloth bundle lifted from dirt, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.combat.hit.flesh`

仅权威 CombatImpact HIT；预测 HIT 也可先播

- 分类：combat / P0
- 完整提示：`dry fleshy melee impact, short wooden thud on hide, no gore wetness overload, no scream, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.combat.miss`

PresentNonHit + CombatImpactResult.Miss

- 分类：combat / P0
- 完整提示：`weapon swing missing, air whoosh and light cloth, no impact, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.combat.blocked`

PresentNonHit + CombatImpactResult.Blocked

- 分类：combat / P0
- 完整提示：`short wooden armor block, dry clack of stick on wood plate, no metal clang, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.player.death`

Dead 组件出现 / RigNode.SetDead(true)

- 分类：player / P0
- 完整提示：`thin cold soul leaving the body, short dry wind and faint bone rattle, no choir, no words, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.player.sleep`

ActionKind.Sleep 起手

- 分类：player / P0
- 完整提示：`soft wood cot creak and a short sleepy exhale, cloth settle, no snore loop, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.player.footstep.stone`

矿区 / 石质地形 walk

- 分类：player / P1
- 完整提示：`short leather sole on dry stone and grit, one footstep only, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.player.footstep.mud`

沼泽 / 雨后 walk

- 分类：player / P1
- 完整提示：`short wet mud footstep, light squish on packed marsh dirt, one step only, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.player.haunt`

灵魂态 ActionKind.Haunt 起手

- 分类：player / P1
- 完整提示：`cold spirit whoosh into a stone statue, thin bone chime, no words, no choir, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.player.revive`

Dead 组件移除

- 分类：player / P1
- 完整提示：`short body returning, dry inhale and faint warmth whoosh, no fanfare, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.build.place`

Commands.Place 火堆/木墙

- 分类：build / P1
- 完整提示：`wooden structure planted into dirt, short thud and timber knock, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.build.demolish`

Hud.DemolishPressed

- 分类：build / P1
- 完整提示：`short wooden structure breaking apart, dry planks and dirt, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.world.campfire`

已放置 campfire / FirePitFire 附近 loop

- 分类：world / P1
- 完整提示：`small campfire crackle loop, dry twigs and soft flame, seamless, no music, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.player.eat`

背包使用浆果/肉

- 分类：player / P1
- 完整提示：`short cartoon bite of berry or meat, dry chew, no voice, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.combat.immune`

PresentNonHit + CombatImpactResult.Immune

- 分类：combat / P1
- 完整提示：`dull immune impact, strike absorbed by hide, short dry puff, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.vitals.heal`

本地玩家 HealthChanged delta>0 且 cause=Healing

- 分类：vitals / P1
- 完整提示：`soft warm heal tick, faint dry chime without melody, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.vitals.starve`

HealthChanged cause=Starvation

- 分类：vitals / P1
- 完整提示：`hollow stomach pang, dry body ache tick, no voice, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.vitals.weather`

HealthChanged cause=Weather

- 分类：vitals / P1
- 完整提示：`cold wind sting on skin, short shiver whoosh, no voice, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `amb.day`

白天 bed，随昼夜切换

- 分类：ambient / P1
- 完整提示：`thin daytime wilderness ambience loop, distant dry insects and light breeze, seamless, no music, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `amb.night`

夜晚 bed

- 分类：ambient / P1
- 完整提示：`thin night wilderness ambience loop, distant crickets and cold air, seamless, no music, no wolf howl, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `amb.rain`

Weather rain > 0.15 时叠在 bed 上

- 分类：ambient / P1
- 完整提示：`soft rain on leaves and dirt loop, light survival-game rain, seamless, no thunder, no music, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.creature.spider.attack`

CreatureKind.Spider ActionKind.Attack

- 分类：creature / P1
- 完整提示：`small dry spider lunge, chitin click and short hiss, no human voice, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.creature.spider.hit`

蜘蛛作为 HIT 目标

- 分类：creature / P1
- 完整提示：`small spider body hit, dry chitin crack, short skitter, no scream, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.creature.wolf.attack`

CreatureKind.Wolf ActionKind.Attack

- 分类：creature / P1
- 完整提示：`short wolf snap and growl, dry cartoon animal, no long howl, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.creature.rabbit.flee`

CreatureKind.Rabbit 进入 flee

- 分类：creature / P1
- 完整提示：`tiny rabbit dash through dry grass, short rustle, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.creature.lizard.attack`

CreatureKind.Lizard ActionKind.Attack

- 分类：creature / P1
- 完整提示：`dry lizard hiss and quick claw swipe, short cartoon reptile, no roar, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.world.thunder`

WeatherView.OnLightning，与闪电环境光同步

- 分类：world / P1
- 完整提示：`distant dry thunder rumble, short survival-game storm, no explosion, no music, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.vitals.poison`

HealthChanged cause=Poison，无 CombatImpact

- 分类：vitals / P1
- 完整提示：`short venom sting tick, dry insect hiss and body ache, no voice, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.ui.equip`

Hud.BagEquipPressed / Commands.Equip

- 分类：ui / P1
- 完整提示：`short wooden tool or armor being equipped, dry leather strap, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.ui.drop`

Hud.BagDropPressed / Commands.Drop

- 分类：ui / P1
- 完整提示：`small bundle dropped onto dirt, short wood and cloth thud, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.player.sleep.cancel`

Hud.CancelSleepPressed / Commands.CancelSleep

- 分类：player / P1
- 完整提示：`short waking stir, cloth and wood cot creak, dry inhale, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.action.cancel`

ActionOutcome CANCELED（移动打断、主动取消等，不含死亡）

- 分类：player / P1
- 完整提示：`tiny interrupted action whoosh, short cloth stop, no impact, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.creature.boar.attack`

CreatureKind.Boar ActionKind.Attack

- 分类：creature / P2
- 完整提示：`heavy boar snort and short charge grunt, dry hide impact windup, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.creature.deer.flee`

CreatureKind.Deer 进入 flee

- 分类：creature / P2
- 完整提示：`deer hooves on dry leaves, short startled dash, one-shot, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `amb.winter`

Season.Winter 替换白天 bed

- 分类：ambient / P2
- 完整提示：`thin winter wind over dry grass loop, cold air, seamless, no music, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `amb.snow`

WeatherView 冬季雪粒子叠在 bed 上

- 分类：ambient / P2
- 完整提示：`soft snow and cold air loop, light flakes on dry grass, seamless, no music, no thunder, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`

### `sfx.world.alchemy`

EntityLayer.EnsureAlchemy 附近 loop

- 分类：world / P2
- 完整提示：`small wooden alchemy engine idle loop, soft bubbling and gear tick, dry workshop, seamless, no music, Don't Starve gothic cartoon foley, dry wood leather bone wet mud and flint, short slightly cartoon survival game, thin analog lo-fi, no orchestra, no cinematic reverb tail, no sci-fi, no melody, no human speech, avoid: music, melody, singing, speech, voice-over, choir, cinematic boom, long reverb, explosion, laser, synth lead, trailer braam`
