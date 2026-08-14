# Godot 迁移方案（start-client → Godot 4 + C#）

> 目标：用 Godot 4（C#）替换现有 PixiJS 渲染层，协议层由 TS 直译成 C#。
> 结论先行：总工作量约 **3~6 周**（AI 辅助 2~4 周）；建议先做"协议通 + 能走路"
> 的垂直切片再决定全量投入。最贵的是**等距高度地形**和 **HUD 观感重做**，
> 最省的是粒子/点光/Bloom（Godot 内建）。

---

## 0. 现状盘点（已核实）

| 层 | 内容 | 行数 | 渲染依赖 |
| --- | --- | --- | --- |
| `src/core/` | 协议/世界状态/命令/相机数学/插值/tilemap | ~1000 | 零 |
| `src/render/scene.ts` | 地形/光照/天气/粒子/实体/建筑/小地图 | 2188 | Pixi |
| `src/render/` 其余 | 骨骼 285 / LUT 367 / 地形烘焙 422 / HUD 341 / 粒子 238 | ~1650 | Pixi |
| `src/main.ts` | 游戏流程胶水层（移动/动作/制作/建造） | 768 | 部分 |

- `src/core` 无任何 pixi/render 引用 → 可直接机械直译成 C#。
- `package.json` 里的 `pixi-dragonbones-runtime` 实际未使用（骨骼是自研代码骨架），迁移时直接丢弃。
- 协议契约（proto 文件 + 路由表）本来就是跨语言共享的，`proto/` 直接带走。

---

## 1. 协议层迁移清单（core → C#，约 1 周，低难度）

### 1.1 逐文件直译

| 文件 | 行数 | C# 对应 | 注意点 |
| --- | --- | --- | --- |
| `routes.ts` | 28 | `static class Routes` | 纯常量，零改动直译 |
| `pomelo/codec.ts` | 139 | 手写帧编解码 | 字节序/变长/UTF-8 字符串；Godot C# 无现成 pomelo 库 |
| `transport.ts` | 119 | `WebSocketPeer` 或 `System.Net.WebSockets` | 二进制消息收发 + 断线事件 |
| `session.ts` | 87 | 直译 | mid 分配、pending 表、超时（C# `CancellationTokenSource`/`Task.WhenAny`） |
| `client.ts` | 149 | 直译 | 连接状态机、推送分发、踢线 |
| `world-service.ts` | 225 | 直译 | 实体表 `Dictionary<ulong, EntityView>`、组件解码注册表、快照/增量合并、异步更新流（`Channel<T>` 或事件） |
| `command-service.ts` | 135 | 直译 | 每个路由一个方法；request/response 用 async/await |
| `camera.ts` | 145 | 直译 | 菱形投影/逆变换/缩放/平移/指数缓动（纯数学，照搬） |
| `position-smoother.ts` | 71 | 直译 | 插值逻辑照搬 |
| `tilemap.ts` | 54 | 直译 | corner type/height 查询照搬 |

### 1.2 类型适配点

- `bigint`（实体 id，uint64）→ C# `ulong`（注意 proto 生成与日志格式）；
- `ReadonlyMap<bigint, EntityView>` → `IReadOnlyDictionary<ulong, EntityView>`；
- `performance.now()` → `Time.GetTicksMsec()` / `Stopwatch`；
- TS Promise 链 → C# async/await（Godot C# 完全支持）；
- 组件解码表（字符串名 → 解码函数）→ C# 注册表（`Dictionary<string, Func<byte[], object>>` 或 switch）；
- 世界更新流（`for await`）→ `Channel<WorldUpdate>` / 事件订阅。

### 1.3 protobuf 生成选型（二选一，提前定）

1. **protoc + Grpc.Tools 生成 C#**：与现有 `buf generate proto` 同源，最规范，推荐；
2. **手写迷你 codec**：消息字段少（十几个），直接手写 Read/Write 也行，省掉生成链。

> 契约文件 `proto/game.proto`、`proto/message.proto` 直接复用，不改一行。

---

## 2. 渲染层工作量（Godot 4，约 2~3 周）

标注：🟢 省（Godot 内建/更简单） 🟡 平（概念照搬，代码重写） 🔴 费（无现成，要自研）

### 2.1 地形（🔴 最大头，约 1 周）

| 功能 | Godot 方案 | 难度 |
| --- | --- | --- |
| 菱形 + 高度投影 | Node2D 自绘或 Mesh2D；投影公式照搬 camera 数学 | 🟡 |
| 分块离屏烘焙 | SubViewport + ViewportTexture 烘焙（等价 RenderTexture）；变体着色/AO 逻辑照搬 | 🟡 |
| 主/次贴图软过渡 + 菱形 UV | 照搬烘焙逻辑 | 🟡 |
| 动态水面 | CanvasItem shader 重写（逻辑照搬） | 🟡 |
| 高度/坡度着色 + 烘焙 AO | 烘焙期 CPU 计算，照搬 | 🟢 |
| **等距 + 高度地形整体** | Godot 无现成"带高度的等距地形"，TileMapLayer 只支持平面 | 🔴 |

### 2.2 光照 / 后处理（约 4~5 天）

| 功能 | Godot 方案 | 难度 |
| --- | --- | --- |
| 火堆点光 | `PointLight2D`（内建，带闪烁动画） | 🟢 |
| 全屏法线光照 + 深度雾 | CanvasItem shader + SCREEN_TEXTURE（比 Pixi 滤镜链简单） | 🟡 |
| Bloom/辉光 | `WorldEnvironment` 内建 glow | 🟢 |
| 3D LUT 调色 | CanvasItem shader 查大贴图（方案照搬），或 ColorRect + shader | 🟡 |
| 体积光/光柱 | PointLight2D 光锥贴图 | 🟢 |
| 昼夜色温 | `CanvasModulate` + shader 混合 | 🟢 |
| 暗角 | 简单 shader / 贴图 | 🟢 |

### 2.3 天气 / 表现（约 2~3 天，最省）

| 功能 | Godot 方案 | 难度 |
| --- | --- | --- |
| 雨/雪 | `GPUParticles2D` | 🟢 |
| 雾 | CanvasItem shader / ColorRect 噪点 | 🟡 |
| 云影 / 闪电 / 草摇摆 / 树晃动 | Sprite2D/Line2D 池 + 相位动画 | 🟢 |
| 视差远景 | `Parallax2D` 内建 | 🟢 |
| 火光粒子 | `GPUParticles2D` | 🟢 |

### 2.4 实体 / 骨骼（约 3~5 天）

| 功能 | Godot 方案 | 难度 |
| --- | --- | --- |
| fantasy-player 骨骼 | `Bone2D`/`Skeleton2D` 重新装配（现有 PNG 关节数据要对位） | 🔴 |
| 实体球面法线近似/受光 | Polygon2D 渐变叠加，逻辑照搬 | 🟡 |
| 实时方向投影 | Sprite2D shadow / Polygon2D | 🟢 |
| 小地图 | Control 自绘 | 🟡 |
| 建筑/幽灵预览 | Polygon2D 半透明占格 | 🟡 |

### 2.5 HUD（🔴 容易低估，约 4~5 天）

- DOM/CSS 哥特观感（焦木/青铜/旧金）→ Godot Theme 重调，观感还原是细活；
- 背包/制作/建造面板、状态栏、日志、toast → Control 节点重建 + 信号；
- 输入（WASD/点击/滚轮/拖拽平移）→ Godot Input 事件。

### 2.6 胶水层 main.ts（约 3~4 天）

- 游戏流程状态机（移动/自动靠近/采集/制作/建造预览/放置/拆除）→ C# 直译 + Godot 信号；
- build.check 节流、F2 调试清屏 → 直译。

---

## 3. 工作量汇总

| 模块 | 现有规模 | Godot 工作量 | 难度 |
| --- | --- | --- | --- |
| 协议 core | ~1000 行 | ~1 周 | 低（机械直译） |
| 地形 | ~800 行 | ~1 周 | 高（等距高度无现成） |
| 光照/LUT | ~500 行 | 4~5 天 | 中（内建点光/Bloom 省很多） |
| 天气/粒子/视差 | ~300 行 | 2~3 天 | 低 |
| 骨骼 | 285 行 | 2~3 天 | 中（重装配） |
| HUD | 341 行 + CSS | 4~5 天 | 中高 |
| 胶水 main.ts | 768 行 | 3~4 天 | 中 |
| **合计** | **~5700 行** | **3~6 周** | |

---

## 4. 建议推进顺序（垂直切片优先）

1. **阶段 0（2~3 天）**：Godot C# 工程 + 协议直译 + WebSocket 连上服务器 + 快照解析 → 日志里能看到世界实体。
2. **阶段 1（3~5 天）**：菱形投影 + 简化地形 + 玩家移动 → 能走。
3. **阶段 2（1~2 周）**：光照/天气/粒子/实体渲染 → 视觉对等。
4. **阶段 3（3~5 天）**：HUD + 交互流程（采集/制作/建造）。
5. **阶段 4（收尾）**：骨骼精修、性能、打包。

---

## 5. 风险与提醒

- **等距 + 高度地形是最大的无现成部分**，别指望 Godot 的 TileMap 直接给；
- **HUD 观感重做**容易被低估（CSS → Theme 不是等价翻译）；
- **协议层先通**：避免渲染做了半天连不上服务器才发现协议坑；
- **protobuf 生成选型提前定**（protoc C# vs 手写 codec）；
- **发布形态提前定**（桌面/移动/Web），影响 C# 还是 GDScript、以及是否要保留 TS 桥。
