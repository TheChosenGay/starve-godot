# Boss 资产接入清单（客户端）

> 面向美术 / 自己：Boss（`CreatureKind.Boss` = 服务端 `CREATURE_KIND_BOSS` = 8）的模型、动画、音效怎么接。
> 代码侧已经全部接好，**美术把模型放进来后只需要"填路径 + 对 clip 名"**。
> 对照：服务端 `boss_tree`（投弹 / 嚎叫 + 闪现 + 三拳一砸）、客户端 `proto/game.proto` 的
> `ACTION_KIND_BOSS_THROW/LEAP/SLAM/ROAR`。

---

## 0. 三步上手

1. 把模型导出成 **glb**，放到 `GodotClient/assets/models/boss/boss.glb`（见 §1）。
2. 让 glb 里的动画 clip 用下面的名字（见 §3）；名字对不上也能跑，会走回退链。
3. 体型要和服务器一致：改 `ActorCatalog3D.BossModelScale`，同步服务端 `models.json` 的 boss 条目，
   跑一次 `cmd/modelcollide` 重算碰撞（见 §2）。

现在模型**还没有**，所以场景里看到的是占位体，不是崩溃（见 §6）。

---

## 1. 模型放到哪

| 项 | 值 |
| --- | --- |
| Godot 资源路径 | `res://assets/models/boss/boss.glb` |
| 磁盘路径 | `<仓库>/GodotClient/assets/models/boss/boss.glb` |
| 目录约定 | 与 `res://assets/models/quaternius-animals/` 一致，都挂 `res://assets/models/` 下 |

`GodotClient/assets` 是指向子模块 `asset-starve/assets` 的软链，所以文件实际落在子模块里
（和 `quaternius-animals`、`ghibli-tree` 一样）。

路径常量在 `GodotClient/Game/ActorCatalog3D.cs` 的 `BossModelPath`——
**要换文件名/换目录只改这一行**，别在别处再写死一份。

glb 里需要：一份骨架（可选，纯静态模型也能跑）、一个 `AnimationPlayer`、若干动画。
导入后 Godot 会按 glb 内的动画名生成 clip。

---

## 2. 缩放与 body 尺寸（`cmd/modelcollide`）

### 2.1 现状

- 客户端的视觉缩放是 `ActorCatalog3D.BossModelScale`（缺省 `1.0`，即按导入尺寸 1:1）。
- 服务端的碰撞尺寸**不是手填的**：`configs/models.json` 里每个模型给一个 scale，
  服务端跑 `cmd/modelcollide`（对应 Makefile 里的 `make model-collide`）从模型 mesh 的 AABB 推出
  `body_radius` / `body_half_length`，写回配置。
- 运行时服务端通过 **`Collide` 组件**下发（proto 字段：`radius` / `half_length` / `body_height`；
  `body_height` 是碰撞体竖直高度，供调试渲染与契约用）；客户端 `GameRoot` 用同一个半径喂本地预测
  （`_ownSim.SetBodyRadius`），所以预测和服务端不会各算一套。
- 核对碰撞体：服务端开 `GATE_DEBUG_COLLISION=1`，`DebugShapeLayer3D` 会把圆柱/胶囊画在半透明里，
  直接看是不是刚好包住模型。

> 大致规则（**以服务端 `cmd/modelcollide` 实现为准**）：
> `body_radius ≈ max(size.x, size.z) / 2 × scale`，`body_half_length ≈ size.y / 2 × scale`。

### 2.2 boss.glb 定稿后的顺序

1. 服务端 `configs/models.json` 加 `boss` 条目（scale 先给个初值）；
   同时 `configs/creatures.json` 加 boss（名字、`body_radius` 等；名字要和客户端 `GameRoot` 的 `"首领"` 对齐）。
2. 跑 `cmd/modelcollide`（`make model-collide`），让它从 boss.glb 推出 radius/half_length。
3. 把服务端 scale 的结果抄进客户端 `ActorCatalog3D.BossModelScale`，**两边用同一个数**。
4. 进场景目视：体型、脚底贴地、（有 `DebugShape` 时）胶囊是否包住模型。

**不要**在客户端单独改半径——本地预测用的是服务端下发的 `Collide`，客户端改只会让自己来回校正。

---

## 3. 需要的 clip 名

匹配规则：`RiggedActor3D.FirstClip(...)` 按顺序找，大小写不敏感、允许子串命中，
`|` 和 `/` 会归一成 `_`（Godot 的 `Armature|walk` 也能命中 `walk`）。名字不要求完全一致，
但**优先用左列的名字**，回退链只是兜底。

| 用途 | 首选 clip 名 | 回退链（`RiggedActor3D` 里的顺序） | 触发点 |
| --- | --- | --- | --- |
| 待机 | `idle_loop` | `idle`、`idle_rest` | `SetLocomotion(false)` |
| 走 | `walk_loop` | `walk`、`jog` | `SetLocomotion(true, 速度 < 13)` |
| 跑/冲锋 | `sprint_loop` | `sprint`、`gallop`、`running`、`run`、`jog`、`walk_loop`、`walk` | 速度 ≥ 13 格/秒 |
| 近战三拳 | `punch` | `hook`、`attack`、`proc_attack` | `ActionKind.Attack`（boss_tree 的三拳） |
| 投弹 | `throw`（可选） | 并入挥击族：`punch`/`hook`/`attack`/`proc_attack` | `ActionKind.BossThrow` |
| 闪现突进 | `leap` | `jump`、`dash`、`attack` | `ActionKind.BossLeap` |
| 锤地 | `slam` | `smash`、`attack` | `ActionKind.BossSlam` |
| 嚎叫 | `roar` | `howl`、`cast`、`idle` | `ActionKind.BossRoar` |
| 受击 | `hitreact` | `hit`、`proc_hit` | 权威 `CombatImpact` HIT |
| 死亡 | `death01` | `death`、`proc_death` | `Dead` 组件出现 |

说明：

- `throw` 现在没有独立分支——投弹和挥击共用一支（服务端的投弹也是"起手→抛出"）。
  真要单独一个投掷 clip，改 `RiggedActor3D.PlayAction` 里 `BossThrow` 那一支。
- 模型没有任何 clip 时，`RiggedActor3D` 会自动合成 `idle_rest` / `proc_attack` / `proc_pick` /
  `proc_hit` / `proc_death` 几个兜底动画（`EnsureIdleFromRest` / `EnsureFallbackClips`），不会报错。
- clip 名想改：**只改 `GodotClient/Game/RiggedActor3D.cs` 的 `PlayAction` / `SetLocomotion`**。

---

## 4. 四个技能各自的"复制来源"

服务端每个技能都是一个普通 `ActionState`（带 `windup` / `recovery` 阶段，逐 tick 复制），
客户端据此播技能动画；技能**效果**在各自的 Commit 时刻由服务端结算，客户端只消费结果：

| 技能 | 动画触发来源 | 效果怎么到客户端 |
| --- | --- | --- |
| `BossThrow` 投弹 | `ActionState.kind = BOSS_THROW` → 导演 `Apply` → `PlayAction(BossThrow)`（挥击族 clip） | Commit 时服务端**实体化炸弹** → 之后是 `Thrown` 组件（`From/To/FlightTicks/Elapsed/Gravity`）逐 tick 复制；客户端 `ThrowFlightTracker` + 已有的投掷预测/`ThrowAimLayer3D` 走同一条抛物线 |
| `BossLeap` 闪现突进 | `ActionState.kind = BOSS_LEAP` → `PlayAction(BossLeap)`（`leap/jump/dash/attack`） | 出手时服务端**瞬移**到目标相邻格 → 客户端下一次快照里 `Position` 直接跳变，位置插值会看到位移（没有专属位移特效，属于"故意先这样"） |
| `BossSlam` 锤地 AOE | `ActionState.kind = BOSS_SLAM` → `PlayAction(BossSlam)`（`slam/smash/attack`） | 服务端广播 **`BlastEvent`**（`thrown_entity = 0`）→ `GameRoot` 消费 → `BlastFxLayer3D.Spawn` 画扩散圈 + 震屏 |
| `BossRoar` 嚎叫 | `ActionState.kind = BOSS_ROAR` → `PlayAction(BossRoar)`（`roar/howl/cast/idle`） | 只做阶段转换，**没有额外事件**；纯动画 + 以后的音效 |

`ActionPresentationController` 是通用导演，不需要为 Boss 单开旁路：它只把 `kind` 转发给
`EntityLayer3D`（`IActionPresentationSink.Apply`），后者再调演员 `PlayAction(kind)`。

---

## 5. 音效待补（现在刻意留空）

音效目录：`GodotClient/assets/audio/catalog.json`（子模块 `asset-starve`）。
动作 → 音效的映射在 `GodotClient/Game/EntityLayer3D.cs` 的 `IActionPresentationSink.Apply`。

| 动作 | 现在 | 待补 |
| --- | --- | --- |
| `BossThrow` | ✅ 复用 `sfx.player.swing` | 可选：专门的"出手"音 |
| `BossLeap` | ⬜ 留空（注释说明） | `sfx.creature.boss.leap` |
| `BossSlam` | ⬜ 留空（注释说明） | `sfx.creature.boss.slam`（画面已由 `BlastFxLayer3D` 负责） |
| `BossRoar` | ⬜ 留空（注释说明） | `sfx.creature.boss.roar` |

补音效的顺序：在 catalog.json 加条目 → 生成音频 → 在 `EntityLayer3D` 对应 `case` 里 `_sfx?.Play(...)`。
**不要把对不上的音效随便配上顶替**，听感错了比没有更糟。

---

## 6. 模型缺失时会看到什么

`ActorCatalog3D.CreateBoss()` 先看 `ResourceLoader.Exists(BossModelPath)`：

- **存在** → 返回 `RiggedActor3D`（和 `CreateAnimal` 同一套：`ModelPath` + `ModelScale` + 头顶名字标记）。
- **不存在** → 返回 `BossPlaceholder3D`：暗紫胶囊身体 + 骨白头 + 红眼 + 头顶"首领"标记，
  体型明显大于玩家，会做待机起伏，4 个技能有前倾/起手反馈。**不会报错、不会崩、不会是空引用。**

占位体也实现了 `IAnimatedActor3D`，这样 `EntityLayer3D.ApplyStyle` 会跳过它
（否则通用占位着色会每帧把 Boss 配色刷成单色并重设缩放）。真模型一放进去，占位体自动不再出现。

---

## 7. 改哪些代码（单点清单）

| 想改什么 | 只改这里 |
| --- | --- |
| 模型路径 / 文件名 / 缩放常量 | `GodotClient/Game/ActorCatalog3D.cs`（`BossModelPath` / `BossModelScale` / `CreateBoss`） |
| 技能/Boss 的 clip 名 | `GodotClient/Game/RiggedActor3D.cs` 的 `PlayAction`（Boss 段注释就写着"只改这里"） |
| 待机/走/跑的 clip 名 | `GodotClient/Game/RiggedActor3D.cs` 的 `SetLocomotion` |
| Boss 显示名（HUD/提示） | `GodotClient/Game/GameRoot.cs` 两处 `CreatureKind.Boss`（占位名"首领"） |
| Boss 技能音效 | `GodotClient/Game/EntityLayer3D.cs` 的 `IActionPresentationSink.Apply` |
| 占位体外观 | `GodotClient/Game/BossPlaceholder3D.cs` |

---

## 8. 验收清单

- [ ] `boss.glb` 在 `GodotClient/assets/models/boss/boss.glb`，Godot 导入无报错。
- [ ] clip 名覆盖 §3 的左列；至少 idle/walk/attack 三件套有。
- [ ] 体型与服务端一致：`BossModelScale` ↔ `models.json` 的 boss scale ↔ `cmd/modelcollide` 结果。
- [ ] `body_radius/body_height` 已同步到 `creatures.json` 的 boss 条目。
- [ ] 四个技能肉眼可区分：投弹（挥击 + 抛物线炸弹）、闪现（位置跳变）、锤地（扩散圈/震屏）、嚎叫。
- [ ] `dotnet build GodotClient/GodotClient.csproj` 0 错误；`dotnet test Starve.Core.Tests/Starve.Core.Tests.csproj` 全绿。
