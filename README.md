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
  GodotClient/              # Godot 4 C# 工程
    Game/                   # 渲染与交互（相机/地形/实体/天气/视差/小地图/HUD/主流程）
    scenes/Main.tscn
  ProtocolSmoke/            # 控制台冒烟测试（不经 Godot 验证协议）
```

## 运行

1. 启动网关：`cd ../starve && go run ./cmd/gate`
2. 用 **Godot 4.7.1 .NET 版**打开 `GodotClient/project.godot`
3. 首次打开会提示编译 C#（或手动 `dotnet build GodotClient/GodotClient.csproj`）
4. 运行（F5）：等距地形 + 实体 + 天气 + HUD；WASD 移动、滚轮缩放、左键选中

调试参数（`--` 后传）：`--smoke` 连接后打印地图/实体数并退出；`--capture <path>` 3 秒后截图退出。

## 当前状态

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| 协议层 | pomelo / WS / JWT 登录 / 快照增量 / 命令 | ✅ 冒烟通过 |
| 阶段 1 | 菱形投影 + 相机 + 分块地形 + 实体 + WASD 移动 | ✅ 截图验证 |
| 阶段 2 | 昼夜调制 / 雨雪雾 / 视差山脊 / 小地图 / 火堆点光 | ✅ 截图验证 |
| 阶段 2.5 | 全屏法线光照 shader（环境光+太阳+8点光+深度雾）+ 3D LUT 调色 + 建造幽灵预览 | ✅ 截图验证 |
| 阶段 3 | HUD（状态/日志/操作按钮）/ 点选 / 采集攻击拾取拆除 / 建造预览放置 | ✅ 截图验证 |
| 阶段 4 | 背包（槽位/使用/装备/丢弃/拆分）+ 制作迷你版（材料/工作站/进度）+ 闪电 + 按格雾 + Bloom | ✅ 截图验证 |
| M7 交互 | 适配服务端交互重构：Choppable/Minable/Pickable 资源、Equip 槽位、防御减免、点击即操作 + 选中描述、徒手限制置灰 | ✅ 冒烟通过 |

> 睡眠：服务端暂无 world.sleep 接口，客户端已留按钮与提示，待服务端接入后接通。

### 资产接入（已做）

- 地形：sheet-cut 156 张贴图按变体映射打包图集，菱形填充预处理 + 高度/坡度/AO 顶点色
- 角色：fantasy-player 8 部位代码装配骨骼（移植 web 端关节数学，行走摆臂/待机呼吸）
- 法线图：高度场梯度烘焙，全屏光照 shader 采样（地面随太阳/点光明暗起伏）
- LUT：白天/黄昏/夜晚三套预设按昼夜权重混合（青橙电影/胶片/阴天预留）
- 幽灵预览：建造后绿/红占格跟随鼠标，build.check 节流，点击放置
- 实体：方向投影（随太阳高度）、血条、受击闪白；树木贴图 + 随风摇晃；云影漂移
- 移动手感：自己的角色本地预测（按下即走、客户端阻挡网格墙停、快照缓合校正）；
  其他实体服务端 tick 延迟插值（固定 100ms + 相邻两帧线性插值，消除格步 40ms 停顿）；
  移动命令 80ms 发送对齐服务端消费节奏；相机平滑 40ms
- 小地图：实体点位（玩家/生物/工作站/建筑）+ 视口框
- 光照：雨天整体压暗 7%（与 web 一致）
- 火堆：程序化三层火焰（白芯/橙/红外焰，错相闪烁）+ 余烬粒子 + LUT 后加法柔光晕/上收光柱（体积光）+ 2D HDR Bloom

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
