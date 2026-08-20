# starve · Godot 客户端

把 PixiJS 渲染层替换为 Godot 4（C#）的客户端：协议层直译 + 三层架构
（协议 / 纯逻辑 / 渲染），三个阶段均已落地。

## 目录

```
godot-client/
  proto/                    # 协议契约（与 start-client/proto 同步）
  Starve.Protocol/          # 纯 C# 协议库（无 Godot 依赖，可独立测试）
    Pomelo/Codec.cs         # pomelo 帧/消息编解码（移植自 TS）
    Transport.cs            # WebSocket + 握手 + 心跳
    Session.cs              # mid 关联 + 登录鉴权（JWT）
    CommandService.cs       # 移动/采集/攻击/建造/拆除…命令
    World/WorldService.cs   # 快照/增量 → 实体表 + 昼夜/天气/地图配置
    StarveClient.cs         # 协议层入口
    DevTokens.cs            # 开发 JWT（匹配服务端 feeds-dev-secret）
  Starve.Core/              # 纯逻辑（零渲染依赖）
    Camera.cs               # 菱形投影/缩放/平移/跟随
    TileMap.cs              # 地形高度场（角粒度）
    PositionSmoother.cs     # 实体位置平滑
    IsoMath.cs              # 世界↔容器本地坐标
    MoveInput.cs            # 按键 → 世界方向
  Starve.Core.Tests/        # 移动预测等纯逻辑单元测试
  GodotClient/              # Godot 4 C# 工程
    Game/                   # 渲染与交互（相机/地形/实体/天气/视差/小地图/HUD/主流程）
    scenes/Main.tscn
  ProtocolSmoke/            # 控制台冒烟测试（不经 Godot 验证协议）
```

## 运行

1. 启动网关：`cd ../starve && go run ./cmd/gate`
2. 用 **Godot 4.7.1 .NET 版**打开 `GodotClient/project.godot`
3. 首次打开会提示编译 C#（或手动 `dotnet build GodotClient/GodotClient.csproj`）
4. 运行（F5）：WASD/方向键移动、Q/E 围绕玩家旋转、滚轮缩放、左键选中；
   空格自动执行最近行为，F 自动寻找 AOI 内最近可攻击角色（超距自动寻路，按住持续攻击）

调试参数（`--` 后传）：`--smoke` 连接后打印地图/实体数并退出；`--capture <path>` 3 秒后截图退出。

工程验收：

```bash
make check
```

在线协议 E2E 独立执行，不会让本地 `make check` 依赖运行中的服务端：

```bash
STARVE_GATE_URL=ws://127.0.0.1:8081/ws make e2e
```

当前稳定边界、移动/相机/制作契约见 [P0.1 客户端真实基线](P0.1-CLIENT-BASELINE.md)。
质量门禁、协议同步和移动校正指标见 [P0.2 客户端质量门禁](P0.2-QUALITY-GATES.md)。
低频诊断采样、可靠协议 E2E 和 CI 临时 gate 见 [P0.3 客户端 E2E](P0.3-CLIENT-E2E.md)。
输入 epoch/seq、服务端 tick、ACK 与预测领先保护见 [P1.1 客户端预测契约](P1.1-PREDICTION-CONTRACT.md)。

## P1.2 权威动作契约

- `proto/game.proto`、`proto/message.proto` 与服务端协议源字节同步，C# 类型由项目构建时生成。
- 握手声明协议 1.2，并按能力检查 `action_state_snapshot`、`action_outcome` 与 `world_events`；
  能力缺失时在登录前拒绝，
  不依赖版本字符串做脆弱的精确匹配。
- `ActionState` 是玩家与 NPC 共用的唯一持续动作事实源：组件出现表示 start/confirm，
  `action_id + phase` 标识一次可去重的时间轴更新；组件移除表示完成或取消，客户端立即清除表现。
- `ActionOutcome` 主路径嵌入同一 `SnapshotDelta.events` 原子下发；旧 `world.action.outcome` route 仅兼容解析。
  WorldService 对 event_id 与 outcome key 双重有界去重，重复或旧 outcome 不会二次清理或重播。
- `SnapshotDelta.events` 是瞬时事实：WorldService 先合并组件与移除项，再按 `event_id` 有界去重并发布
  `WorldEvent`、`CombatImpactEvent`、`HealthChangedEvent`。新全量快照会重置事件幂等作用域。
- `ImpactPresentationController` 独占命中预测/确认/纠错：只有权威 HIT 能补播受击；MISS、BLOCKED、
  IMMUNE 不映射为 HIT。Health 组件只更新血条，普通掉血、DOT、饥饿与天气事件不会推断攻击受击动画。
- GameRoot 在主线程按队列先把 CombatImpact 交给 EntityLayer 播放 HIT，再仅为命中本地玩家的权威
  HIT 触发全屏红色 `DamageFlashOverlay`；350ms 平滑衰减，连续命中叠加有上限，不根据 HP 差推断。
- 玩家 `Dead` 组件是持续死亡视觉与 HUD 的唯一事实源：RigNode 使用鱼人正面 idle 轮廓显示白蓝半透明、
  脉冲漂浮的魂魄，内部 visual root 漂浮而不改变世界坐标；复活后恢复方向、颜色与动作显示。
- HUD 顶部独立显示“生命 cur / max”，死亡显示“灵魂状态 · 生命 0/max”，并禁用采集、攻击、砍伐、
  挖掘、拾取和制作；Space/F 不再发命令但 WASD 观察移动保持可用，状态切换只记录一次提示。
- COMPLETED 只调用 FinishAction，让非循环 clip 自然收尾；移动、主动取消、拒绝、受击中断和死亡则
  CancelAction 立即恢复 walk/idle（DAMAGED 的 hit 只由同批 CombatImpact HIT 驱动）。
- 本地交互只做 500ms 短预测。权威状态到达后确认或校准；未收到状态会自动超时停止；
  本地移动意图开始时立即清除动作视觉，并抑制仍残留在快照中的旧 `action_id`，直到组件移除
  或新动作替换。P1.1 的 `OwnMovementSim` 与 ACK 契约不变。
- 所有 Control 命令共享同一 `input_epoch + seq` 输入流；会产生持续动作的命令额外携带进程内
  单调且跨重连不重置的 `request_id`。预测以 `InputCommandRef` 关联，旧请求的 ActionState/Outcome
  或迟到制作响应不能覆盖、完成或取消较新的预测；Automate/AttackNearest 只发送身份，等待权威状态展示。
- 制作在发送时立即公开命令身份并开始预测，不等待最长 5 秒的响应；`CraftResponse.started` 仅表示
  前置校验通过并排队，不保证同 tick 最终胜出，被后续 Control 命令 superseded 时静默且材料不变。
- `ActionPresentationController` 独占预测/权威/超时状态，`EntityLayer` 统一消费所有 Rig 实体，
  `RigNode` 只把动作类型适配为素材。Attack/Chop/Mine/Pick 暂共用 attack 动画；
  Craft 暂无专用素材，保持 idle 且不参与任何玩法结算。
- 攻击表现统一为 800ms：鱼人 15 帧 18.75fps、蜥蜴 8 帧 10fps，服务端 400ms 命中点约在中帧。
  鱼人四方向以 `FishmanVisualHeight=64px` 归一；侧/背 tight-crop 分件通过
  `DirectionalRigNormalizer` 对齐整体高度、水平中心与 bottom=0 脚底线，左右仅镜像同一 SideRig。
- NPC 攻击只读取 `ActionState`，不根据 `AI.state` 推测。
- `ActionNetworkFaultTests` 以纯确定性方式覆盖 loss/latency/reorder：start 或 outcome 丢包、
  500ms 延迟超时、结果与旧快照重排，以及旧动作之后的新 `action_id` 恢复；不 sleep、不依赖真实网络。
- `PlayerPresentationStateTests` 纯逻辑验证红屏只接受本地 HIT、350ms 曲线与叠加上限、魂魄生死切换，
  以及 Health/Dead 均参与 HUD vitals 签名。

## 当前状态

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| 协议层 | pomelo / WS / JWT 登录 / 快照增量 / 命令 | ✅ 冒烟通过 |
| 阶段 1 | 菱形投影 + 相机 + 分块地形 + 实体 + WASD 移动 | ✅ 截图验证 |
| 阶段 2 | 昼夜调制 / 雨雪雾 / 视差山脊 / 小地图 / 火堆点光 | ✅ 截图验证 |
| 阶段 2.5 | 全屏法线光照 shader（环境光+太阳+8点光+深度雾）+ 3D LUT 调色 + 建造幽灵预览 | ✅ 截图验证 |
| 阶段 3 | HUD（状态/日志/操作按钮）/ 点选 / 采集攻击拾取拆除 / 建造预览放置 | ✅ 截图验证 |
| 阶段 4 | 背包（槽位/使用/装备/丢弃/拆分）+ 制作迷你版（材料/工作站/进度）+ 闪电 + 按格雾 + Bloom | ✅ 截图验证 |
| M7 交互 | Choppable/Minable/Pickable、装备/防御、点击操作；空格 ANY 自动行为与 F ATTACK_ONLY 最近目标持续攻击/寻路 | ✅ 冒烟+实测通过 |
| P1.2 动作 | ActionState 权威时间轴 + ActionOutcome + WorldEvent/CombatImpact + 500ms 本地表现预测 | ✅ 单测覆盖 |
| P1.2 玩家反馈 | 权威 HIT 红屏 + 死亡魂魄 + 独立生命条/灵魂交互状态 | ✅ 纯模型单测覆盖 |

> 睡眠：服务端暂无 world.sleep 接口，客户端已留按钮与提示，待服务端接入后接通。

### 资产接入（已做）

- 地形：sheet-cut 156 张贴图按变体映射打包图集，菱形填充预处理 + 高度/坡度/AO 顶点色
- 角色：主角 = 鱼人（人鱼）；正面 idle/walk/attack/hit 各 15 帧并统一脚底锚点，
  侧面/背面使用 8 分件程序化骨骼行走；角色与工作站灰底、水印均离线抠成透明
- 法线图：高度场梯度烘焙，全屏光照 shader 采样（地面随太阳/点光明暗起伏）
- LUT：白天/黄昏/夜晚三套预设按昼夜权重混合（青橙电影/胶片/阴天预留）
- 幽灵预览：建造后绿/红占格跟随鼠标，build.check 节流，点击放置
- 实体：方向投影（随太阳高度）、血条、受击闪白；树木贴图 + 随风摇晃；云影漂移
- 移动手感（M7 连续速度契约）：服务端 Moveable{speed,dir,sub,path} 方向保持输入、按 speed×dt
  连续位移（对角归一化，跨格校验可走、不可走贴墙停）；客户端 OwnMovementSim 使用相同锚点/子格
  `stepAxis` 本地预测（含负方向借位和 0.001/0.999 贴墙），
  快照 Position+dir×sub 只做校正（两边一致误差趋近 0）；其他实体 50ms 延迟插值；移动命令
  按住 100ms 重发当前方向、松开发 0,0；相机平滑 40ms
- 相机：`WorldPivot` 固定视口中心，Q/E 旋转时围绕跟随中的玩家；拾取通过场景逆变换同步旋转
- 制作：客户端材料/工作站检查只做提示，按钮可请求服务端并展示明确失败原因；HUD 按状态签名增量刷新
- 小地图：实体点位（玩家/生物/工作站/建筑）+ 视口框
- 光照：雨天整体压暗 7%（与 web 一致）
- 火堆/工作站：火盆底座贴图 + FirePitFire 手绘粒子火焰（flame/glow/ember 加色混合 + 点光）；
  工作台 = alchemy-engine 15 帧空闲动画；旧程序化 FireView 保留未接线

### 尚未做

- 睡眠（依赖服务端 world.sleep 路由）
- 水面波纹 shader（当前水用程序贴图，静态波纹）
- 天气帧里的风向驱动雨滴偏斜（当前雨垂直下落）
- 草簇（贴地草 + 摇摆）
- 实体受光球面明暗（当前用方向投影近似）
- 玩法：点击实体已做校验提示与一键动作；自动靠近目标执行动作未做（当前需走近再点）

## 分层约定

- `Starve.Protocol` / `Starve.Core`：纯 .NET，零 Godot 依赖，可独立编译与测试；
- `GodotClient/Game`：渲染与输入，只消费 Core/Protocol 的公开接口，不反向依赖；
- 实体/地图变更走 `WorldService.Revision` 轮询（主线程），网络线程只写、主线程只读。
