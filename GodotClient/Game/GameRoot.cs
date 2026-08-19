using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Google.Protobuf;
using Starve.Core;
using Starve.Game.V1;
using Starve.Protocol;
using Starve.Protocol.World;
using Camera = Starve.Core.Camera;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>客户端可发起的交互意图（输入路由与服务端一致）。</summary>
public enum Intent { Gather, Chop, Mine, Pickup, Attack }

/// <summary>
/// 游戏主节点：分层编排——
/// 协议层（StarveClient）→ Core 逻辑（相机/地形/平滑/法线）→ 渲染层。
/// 渲染层不直接碰协议细节，协议层不碰渲染。
/// </summary>
public partial class GameRoot : Node
{
    private readonly Camera _camera = new();
    private readonly Dictionary<ulong, PositionSmoother> _smoothers = new();
    private OwnMovementSim? _ownSim;
    private readonly HashSet<(int X, int Y)> _blocked = new();
    private readonly Dictionary<ulong, long> _movingUntil = new();
    private readonly Dictionary<ulong, (float X, float Y)> _lastServerPos = new();

    private StarveClient? _client;
    private TileMap? _tilemap;
    private Node2D? _worldPivot;
    private Node2D? _world;
    private MapView? _mapView;
    private EntityLayer? _entityLayer;
    private CloudShadowView? _clouds;
    private ParallaxView? _parallax;
    private WeatherView? _weather;
    private FogGrid? _fogGrid;
    private MinimapView? _minimap;
    private LightingPass? _lighting;
    private LutPass? _lut;
    private VolumetricView? _volumetric;
    private GhostNode? _ghost;
    private Hud? _hud;
    private int _lastRevision = -1;
    private int _lastWeatherRevision = -1;
    private long? _captureAt;
    private ulong _ownId;
    private string _ownUid = "";
    private ulong? _selected;
    private (ulong EntityId, int Kind, int W, int H, bool Ok)? _buildPreview;
    private System.Numerics.Vector2? _mouseWorld;
    private long _lastBuildCheckAt;
    private long _lightningAmbientUntil;
    private readonly bool _freeCamera = CameraArg is not null;
    private bool _autoHeld;
    private long _autoNextAt;
    private long _demoNextAt;
    private float _viewRotation;
    private int _blockedSignature = int.MinValue;
    private int _hudSignature = int.MinValue;

    /// <summary>道具图标（equipment/ 集）：kind → 资源路径；没有图标的物品继续用色块。</summary>
    private static readonly Dictionary<int, string> ItemIconFiles = new()
    {
        [(int)ItemKind.Axe] = "res://assets/equipment/wood/axe.png",
        [(int)ItemKind.Pickaxe] = "res://assets/equipment/wood/chisel.png", // 凿子充当镐图标
        [(int)ItemKind.WoodArmor] = "res://assets/equipment/wood/armor.png",
        [(int)ItemKind.Helmet] = "res://assets/equipment/wood/helmet.png",
    };
    private static readonly Dictionary<int, Texture2D> ItemIconCache = new();

    private static Texture2D? ItemIcon(int kind)
    {
        if (!ItemIconFiles.TryGetValue(kind, out var path)) return null;
        if (!ItemIconCache.TryGetValue(kind, out var tex))
        {
            tex = GD.Load<Texture2D>(path);
            ItemIconCache[kind] = tex;
        }
        return tex;
    }

    private static bool SmokeMode => OS.GetCmdlineUserArgs().Contains("--smoke");
    private static string? CapturePath => OS.GetCmdlineUserArgs()
        .SkipWhile(a => a != "--capture")
        .Skip(1)
        .FirstOrDefault();
    private static string? CameraArg => OS.GetCmdlineUserArgs()
        .SkipWhile(a => a != "--cam")
        .Skip(1)
        .FirstOrDefault();
    /// <summary>演示/截图辅助：STARVE_DEMO_MOVE="dx,dy" 时按住方向自动走（本地预测 + 服务端命令）。</summary>
    private static (int Dx, int Dy)? DemoMove =>
        System.Environment.GetEnvironmentVariable("STARVE_DEMO_MOVE") is { } s &&
        s.Split(',') is { Length: 2 } parts &&
        int.TryParse(parts[0], out var dx) && int.TryParse(parts[1], out var dy)
            ? (dx, dy)
            : null;

    public override void _Ready()
    {
        // Godot 内建 Bloom（辉光）：2D 也生效，配合光照 pass 的亮部
        var env = new Godot.Environment();
        env.GlowEnabled = true;
        env.GlowIntensity = 0.9f;
        env.GlowStrength = 1.1f;
        env.GlowBloom = 0.12f;
        env.GlowHdrThreshold = 0.55f; // 2D HDR 下让火堆加法亮部真正泛光
        AddChild(new WorldEnvironment { Environment = env });

        _parallax = new ParallaxView { Name = "Parallax" };
        AddChild(_parallax);
        _worldPivot = new Node2D { Name = "WorldPivot" };
        AddChild(_worldPivot);
        _world = new Node2D { Name = "World" };
        _worldPivot.AddChild(_world);
        _mapView = new MapView { Name = "MapView" };
        _world.AddChild(_mapView);
        _clouds = new CloudShadowView { Name = "CloudShadows" };
        _world.AddChild(_clouds);
        _entityLayer = new EntityLayer { Name = "EntityLayer" };
        _world.AddChild(_entityLayer);
        _fogGrid = new FogGrid { Name = "FogGrid" };
        _world.AddChild(_fogGrid);
        _ghost = new GhostNode { Name = "Ghost", ZIndex = 4096, Visible = false };
        _world.AddChild(_ghost);

        _weather = new WeatherView { Name = "Weather" };
        _weather.OnLightning += () => _lightningAmbientUntil = NowMs() + 350;
        AddChild(_weather);
        _lighting = new LightingPass { Name = "Lighting" };
        AddChild(_lighting);
        _lut = new LutPass { Name = "Lut" };
        _lut.SetAtlas(LutBuilder.Build().Atlas);
        AddChild(_lut);
        _volumetric = new VolumetricView { Name = "Volumetric" };
        AddChild(_volumetric);

        var ui = new CanvasLayer { Layer = 10 };
        AddChild(ui);
        _minimap = new MinimapView { Name = "Minimap" };
        ui.AddChild(_minimap);
        _hud = new Hud { Name = "Hud" };
        ui.AddChild(_hud);
        WireHud(_hud);

        AddChild(new CameraController { Camera = _camera });
        // 截图/演示辅助：STARVE_DEMO_ROTATE=45 启动即旋转视角
        if (System.Environment.GetEnvironmentVariable("STARVE_DEMO_ROTATE") is { } rr &&
            float.TryParse(rr, out var deg))
        {
            RotateView(deg * Mathf.Pi / 180f);
        }
        var move = new MoveController();
        _ownSim = new OwnMovementSim(IsWalkable);
        move.OnMove += dir => _client?.Commands.Move(dir.Dx, dir.Dy);
        move.OnIntent += dir =>
        {
            _ownSim?.SetIntent(dir.Dx, dir.Dy);
            // 本地预测先行：走路动画立即播放，不等服务端确认
            if (dir.Dx != 0 || dir.Dy != 0) _movingUntil[_ownId] = NowMs() + 240;
            else _movingUntil.Remove(_ownId);
        };
        AddChild(move);

        _hud.Log("连接中…");
        _ = StartAsync();
    }

    private void WireHud(Hud hud)
    {
        hud.GatherPressed += () => WithSelected(id => TryAct(id, Intent.Gather));
        hud.AttackPressed += () => WithSelected(id => TryAct(id, Intent.Attack));
        hud.ChopPressed += () => WithSelected(id => TryAct(id, Intent.Chop));
        hud.MinePressed += () => WithSelected(id => TryAct(id, Intent.Mine));
        hud.PickupPressed += () => WithSelected(id => TryAct(id, Intent.Pickup));
        hud.DemolishPressed += () => WithSelected(id => _client?.Commands.Demolish(id));
        hud.BuildPressed += kind => _ = DoBuildAsync(kind);
        hud.BagUsePressed += slot => WithBagSlot(slot, kind => _client?.Commands.Use(kind));
        hud.BagEquipPressed += slot => WithBagSlot(slot, kind =>
        {
            // M7：Equipped 下线，改用 Equip 槽位 + 玩家身上复制的主动能力组件。
            // 服务端 kind=0 = 卸下全部；手持工具或头/身护甲已在身上时再点 = 卸下。
            _client?.Commands.Equip(IsEquippedKind(kind) ? 0 : kind);
        });
        hud.BagDropPressed += slot => WithBagSlot(slot, kind =>
        {
            var count = OwnItemCount(slot);
            if (count > 0) _client?.Commands.Drop(kind, count);
        });
        hud.BagSplitPressed += slot => WithBagSlot(slot, kind =>
        {
            var count = OwnItemCount(slot);
            if (count > 1) _client?.Commands.Split(slot, count / 2);
        });
        hud.CraftPressed += recipeId => _ = DoCraftAsync(recipeId);
        hud.CancelCraftPressed += () => _client?.Commands.CancelCraft();
        hud.SleepPressed += () => _hud?.Log("睡眠：服务端暂无 world.sleep 接口，待实现后接入");
    }

    private async Task StartAsync()
    {
        _client = new StarveClient();
        try
        {
            var uid = System.Environment.GetEnvironmentVariable("STARVE_UID") ?? "42";
            _ownUid = uid;
            var info = await _client.ConnectAsync("ws://localhost:8081/ws", DevTokens.Mint(uid));
            _ownId = info.EntityId;
            _entityLayer?.SetOwnId(_ownId);
            _entityLayer?.SetNameProvider(EntityName);
            if (CameraArg is { } cam && cam.Split(',') is { Length: 2 } parts &&
                float.TryParse(parts[0], out var cx) && float.TryParse(parts[1], out var cy))
            {
                _camera.Teleport(cx, cy);
            }
            _hud?.Log($"[已连接] uid={info.UserId} entity={info.EntityId}");
        }
        catch (Exception ex)
        {
            _hud?.Log($"[连接失败] {ex.Message}");
        }
    }

    public override void _Process(double delta)
    {
        if (CapturePath is not null)
        {
            var delay = 3000;
            if (System.Environment.GetEnvironmentVariable("STARVE_CAPTURE_MS") is { } ms &&
                int.TryParse(ms, out var custom)) delay = custom;
            if (_captureAt is null) _captureAt = NowMs() + delay;
            if (NowMs() >= _captureAt)
            {
                var img = GetViewport().GetTexture().GetImage();
                img.SavePng(CapturePath);
                GD.Print($"CAPTURE saved: {CapturePath}");
                GetTree().Quit();
                return;
            }
        }

        var client = _client;
        if (client is null) return;

        if (client.World.Revision != _lastRevision)
        {
            _lastRevision = client.World.Revision;
            ApplyWorld(client.World);
        }

        var now = NowMs();
        if (_autoHeld && now >= _autoNextAt)
        {
            _autoNextAt = now + 150; // 按住空格：每 150ms 评估一次（服务端就近匹配/寻路）
            _client?.Commands.Automate();
        }
        if (DemoMove is { } dm && now >= _demoNextAt)
        {
            _demoNextAt = now + 100;
            _client?.Commands.Move(dm.Dx, dm.Dy);
            _ownSim?.SetIntent(dm.Dx, dm.Dy);
        }
        _ownSim?.Tick((float)(delta * 1000));
        System.Numerics.Vector2? own = _ownSim is { Has: true } sim
            ? new System.Numerics.Vector2(sim.Position.X, sim.Position.Y)
            : null;
        if (!_freeCamera) _camera.Follow(own?.X, own?.Y);
        _camera.Tick((float)(delta * 1000));

        var viewport = GetViewport().GetVisibleRect().Size;
        var hCam = _tilemap?.HeightAt(_camera.CenterX(), _camera.CenterY()) ?? 0;
        // Pivot 固定在屏幕中心，WorldContent 抵消相机中心投影：
        // Q/E 旋转 Pivot 时，玩家始终留在屏幕中心。
        var camLocal = IsoMath.WorldToLocal(_camera.CenterX(), _camera.CenterY(), hCam);
        _worldPivot!.Position = viewport / 2;
        _worldPivot.Rotation = _viewRotation;
        _worldPivot.Scale = Vector2.One * _camera.ZoomLevel;
        _world!.Position = new Vector2(-camLocal.X, -camLocal.Y);
        _world.Scale = Vector2.One;

        var fx = (_camera.CenterX() - _camera.CenterY()) * IsoMath.Step * _camera.ZoomLevel;
        var fy = ((_camera.CenterX() + _camera.CenterY()) * IsoMath.Step / 2 - hCam * IsoMath.Step) *
                 _camera.ZoomLevel;
        _parallax!.UpdateParallax(fx, fy, viewport);

        if (client.World.Revision != _lastWeatherRevision)
        {
            _lastWeatherRevision = client.World.Revision;
            var w = client.World.Weather;
            _weather!.SetWeather(w?.Rain ?? 0, w?.Fog ?? 0, client.World.Season, viewport);
            if (_tilemap is not null) _fogGrid!.SetFog(client.World.WeatherFrame, _tilemap);
            UpdateLut(client.World.DayLight);
        }

        _weather!.Tick(delta, viewport);
        UpdateLighting(client.World, viewport, _camera.ZoomLevel, own);
        _lighting!.Size = viewport;
        _lut!.Size = viewport;
        var fires = new List<Vector2>();
        var seeds = new List<long>();
        foreach (var view in client.World.Entities.Values)
        {
            var p = view.Get("Position", Starve.Game.V1.Position.Parser);
            if (p is null) continue;
            var ws = view.Get("Workstation", Workstation.Parser);
            var bld = view.Get("Building", Building.Parser);
            var isFire = (ws is not null && (int)ws.Type == 1) ||
                         (bld is not null && bld.Placed && (int)bld.Kind == 1);
            if (isFire)
            {
                fires.Add(new Vector2(p.X, p.Y));
                seeds.Add((long)view.EntityId);
            }
        }
        _volumetric!.SetView(_camera, fires.ToArray(), seeds.ToArray(), viewport, client.World.DayLight, _camera.ZoomLevel);
        if (_buildPreview is not null && _mouseWorld is not null) UpdateGhost();

        _entityLayer!.UpdatePositions(_smoothers, id => _movingUntil.GetValueOrDefault(id) > now, now, own);
        _entityLayer.SetDayLight(client.World.DayLight);
        _minimap!.SetView(
            client.World.Entities,
            new Vector2(_camera.CenterX(), _camera.CenterY()),
            _camera.ZoomLevel,
            viewport);
        UpdateHud();
    }

    private void UpdateLighting(WorldService world, Vector2 viewport, float zoom, System.Numerics.Vector2? own)
    {
        var dayLight = world.DayLight;
        var dark = Mathf.Max(0, 1 - dayLight * 2);
        var sunT = 1 - dark;
        var ambient = 0.92f - dark * 0.3f;
        if (NowMs() < _lightningAmbientUntil) ambient += 0.5f;
        if (world.Weather is { Rain: > 0.15f }) ambient *= 0.93f;
        var sunColor = new Color(
            0.3f * (0.33f + 0.67f * sunT),
            0.29f * (0.41f + 0.59f * sunT),
            0.26f * (0.62f + 0.38f * sunT));
        var fogColor = new Color(
            0.62f * (0.14f + 0.86f * sunT),
            0.7f * (0.15f + 0.85f * sunT),
            0.78f * (0.2f + 0.8f * sunT));

        var lightPos = new List<Vector2>();
        var lightColor = new List<Color>();
        var lightRadius = new List<float>();
        foreach (var view in world.Entities.Values)
        {
            var p = view.Get("Position", Starve.Game.V1.Position.Parser);
            if (p is null) continue;
            var ws = view.Get("Workstation", Workstation.Parser);
            var bld = view.Get("Building", Building.Parser);
            var isFire = (ws is not null && (int)ws.Type == 1) ||
                         (bld is not null && bld.Placed && (int)bld.Kind == 1);
            if (isFire)
            {
                lightPos.Add(new Vector2(p.X, p.Y));
                lightColor.Add(new Color(1.65f, 0.95f, 0.45f));
                lightRadius.Add(9f);
            }
        }
        if (own is { } ownPos)
        {
            lightPos.Add(new Vector2(ownPos.X, ownPos.Y));
            lightColor.Add(new Color(1f, 0.85f, 0.6f));
            lightRadius.Add(3.5f);
        }
        while (lightPos.Count > 8) lightPos.RemoveAt(lightPos.Count - 1);

        _lighting!.SetLights(
            viewport,
            zoom,
            ambient,
            new Vector2(0.707f, -0.707f),
            sunColor,
            fogColor,
            0.012f,
            lightPos.ToArray(),
            lightColor.ToArray(),
            lightRadius.ToArray());
    }

    private void UpdateLut(float dayLight)
    {
        var dark = Mathf.Max(0, 1 - dayLight * 2);
        var day = Mathf.Clamp((0.35f - dark) / 0.35f, 0, 1);
        var night = Mathf.Clamp((dark - 0.35f) / 0.65f, 0, 1);
        var dusk = Mathf.Max(0, 1 - day - night);
        _lut!.SetWeights(day, dusk, night);
    }

    private void ApplyWorld(WorldService world)
    {
        var map = world.Map;
        if (map is not null && _tilemap is null)
        {
            _tilemap = new TileMap(map);
            _camera.HeightAt = _tilemap.HeightAt;
            _mapView!.SetMap(_tilemap);
            _entityLayer!.SetTilemap(_tilemap);
            _entityLayer.SetViewRotation(_viewRotation);
            _minimap!.SetMap(_tilemap);
            _lighting!.SetNormalMap(BakeNormalTexture(_tilemap));
            _lighting!.SetMapSize(new Vector2(_tilemap.Width, _tilemap.Height));
            if (SmokeMode)
            {
                GD.Print(
                    $"SMOKE map={_tilemap.Width}x{_tilemap.Height} " +
                    $"chunks={_mapView.GetChildCount()} entities={world.Count}");
                GetTree().Quit();
            }
        }

        // 放置成功 → 自动退出建造预览
        if (_buildPreview is { } bp &&
            world.Entities.TryGetValue(bp.EntityId, out var placedView) &&
            placedView.Get("Building", Building.Parser) is { Placed: true })
        {
            _hud?.Log($"建筑已放置（#{bp.EntityId}）");
            ExitBuildPreview();
        }

        var now = NowMs();
        var tick = world.WorldTick;
        RebuildBlocked(world.Entities);
        foreach (var (id, view) in world.Entities)
        {
            var pos = view.Get("Position", Starve.Game.V1.Position.Parser);
            if (pos is null) continue;
            // M7 连续速度：真实位置 = Position(整格) + sub（sub∈[0,1) 分数偏移，Moveable 携带）
            var mv = view.Get("Moveable", Moveable.Parser);
            var fx = pos.X + (float)(mv?.SubX ?? 0);
            var fy = pos.Y + (float)(mv?.SubY ?? 0);
            if (id == _ownId)
            {
                // 自己的位置走本地预测 + 服务端校正，不进插值缓冲
                if (mv is not null) _ownSim?.SetSpeed((float)mv.Speed);
                // 服务端确认停止 = Dir 清空 + 无路径 + sub 归零；确认后才落定，避免停止前被拽
                var serverStopped = mv is { DirX: 0, DirY: 0 } &&
                                    mv.Path.Count == 0 &&
                                    mv.SubX == 0 && mv.SubY == 0;
                _ownSim?.Reconcile(fx, fy, serverStopped);
            }
            else if (!_smoothers.TryGetValue(id, out var smoother))
            {
                smoother = new PositionSmoother();
                _smoothers[id] = smoother;
                smoother.Update(fx, fy, tick, now);
            }
            else
            {
                smoother.Update(fx, fy, tick, now);
            }
            if (_lastServerPos.TryGetValue(id, out var prev) &&
                (MathF.Abs(prev.X - fx) > 0.001f || MathF.Abs(prev.Y - fy) > 0.001f))
            {
                _movingUntil[id] = now + 240;
            }
            _lastServerPos[id] = (fx, fy);
        }

        _entityLayer!.SyncEntities(world.Entities);
        UpdateBagAndCraft(world);
    }

    /// <summary>从快照重建动态阻挡层（树/矿/建筑等 Block 组件），本地预测墙停用。</summary>
    private void RebuildBlocked(IReadOnlyDictionary<ulong, EntityView> entities)
    {
        var signature = 17;
        unchecked
        {
            foreach (var view in entities.Values.OrderBy(v => v.EntityId))
            {
                var b = view.Get("Block", Block.Parser);
                var p = view.Get("Position", Position.Parser);
                if (b is null || p is null) continue;
                signature = signature * 31 + view.EntityId.GetHashCode();
                signature = signature * 31 + p.X;
                signature = signature * 31 + p.Y;
                signature = signature * 31 + b.Width;
                signature = signature * 31 + b.Height;
            }
        }
        if (signature == _blockedSignature) return;
        _blockedSignature = signature;

        _blocked.Clear();
        foreach (var view in entities.Values)
        {
            var b = view.Get("Block", Block.Parser);
            var p = view.Get("Position", Position.Parser);
            if (b is null || p is null) continue;
            for (var dy = 0; dy < b.Height; dy++)
            {
                for (var dx = 0; dx < b.Width; dx++)
                {
                    _blocked.Add((p.X + dx, p.Y + dy));
                }
            }
        }
    }

    /// <summary>与服务端 Walkable 一致：非水 + 无动态阻挡。</summary>
    private bool IsWalkable(int x, int y)
    {
        if (_tilemap is null) return true;
        // 地图边界：越界不可走（否则本地预测会走出地图到负坐标，角色跑到角外“消失”）
        if (x < 0 || y < 0 || x >= _tilemap.Width || y >= _tilemap.Height) return false;
        if (_blocked.Contains((x, y))) return false;
        return _tilemap.CornerType(x, y) != (int)TerrainType.Water;
    }

    /// <summary>取目标受激能力组件（Choppable/Minable/Pickable 共用 WorkTarget 载荷）。</summary>
    private static WorkTarget? WorkTargetOf(EntityView view) =>
        view.Get("Choppable", WorkTarget.Parser)
        ?? view.Get("Minable", WorkTarget.Parser)
        ?? view.Get("Pickable", WorkTarget.Parser);

    /// <summary>玩家是否持有指定主动能力（服务端把工具能力复制到玩家身上）。</summary>
    private bool HasOwnCapability(string component) =>
        OwnComponent<Capability>(component, Capability.Parser) is not null;

    /// <summary>当前手持工具的物品 kind（0 = 徒手）。</summary>
    private int EquippedKind()
    {
        if (HasOwnCapability("Chopper")) return (int)ItemKind.Axe;
        if (HasOwnCapability("Miner")) return (int)ItemKind.Pickaxe;
        return 0;
    }

    private string EquippedName() => EquippedKind() switch
    {
        (int)ItemKind.Axe => "斧头",
        (int)ItemKind.Pickaxe => "镐",
        _ => "徒手",
    };

    /// <summary>
    /// 已穿戴护甲：从 Equip.head/body 反查护甲实体（服务端 Defense 只挂护甲实体，
    /// 穿戴者身上不存防御），返回 (槽位名, 物品 kind, 减免百分比)。
    /// </summary>
    private List<(string Slot, int Kind, int Percent)> WornArmor()
    {
        var result = new List<(string, int, int)>();
        if (_client is null ||
            !_client.World.Entities.TryGetValue(_ownId, out var own) ||
            own.Get("Equip", Equip.Parser) is not { } eq)
        {
            return result;
        }
        var world = _client.World;
        AddArmor(eq.Head, "头戴");
        AddArmor(eq.Body, "身穿");
        return result;

        void AddArmor(ulong id, string slot)
        {
            if (id == 0 || !world.Entities.TryGetValue(id, out var item)) return;
            var def = item.Get("Defense", Defense.Parser);
            if (def is null) return;
            var kind = item.Get("Equipment", ItemStack.Parser) is { } eq ? (int)eq.Kind : 0;
            result.Add((slot, kind, def.Percent));
        }
    }

    /// <summary>总防御减免 = 头/身护甲之和（与服务端 Attackable 受击口径一致）。</summary>
    private int DefensePercent() => WornArmor().Sum(a => a.Percent);

    /// <summary>已装备物品的展示文本（手持 + 头戴/身穿护甲名）。</summary>
    private string EquipText()
    {
        var wear = string.Concat(WornArmor().Select(a =>
            $" {a.Slot} {ItemName(_client?.World.Config, a.Kind)}"));
        return $"手持 {EquippedName()}{wear}";
    }

    /// <summary>背包 kind 是否已装备（手持工具，或头/身护甲）。</summary>
    private bool IsEquippedKind(int kind)
    {
        if (kind <= 0) return false;
        return EquippedKind() == kind || WornArmor().Any(a => a.Kind == kind);
    }

    /// <summary>一次交互：按新组件校验 + 距离检查，再发命令。</summary>
    private void TryAct(ulong id, Intent intent)
    {
        if (_client is null || !_client.World.Entities.TryGetValue(id, out var view))
        {
            _hud?.Log("目标已消失");
            return;
        }
        if (!_client.World.Entities.TryGetValue(_ownId, out var own) ||
            own.Get("Position", Position.Parser) is not { } mePos ||
            view.Get("Position", Position.Parser) is not { } tPos)
        {
            _hud?.Log("目标不可达");
            return;
        }
        var dx = mePos.X - tPos.X;
        var dy = mePos.Y - tPos.Y;
        // 与服务端 withinRange 一致：曼哈顿距离 ≤2（客户端曾用欧氏 2.5，
        // 对角 2 格会被服务端静默拒绝，造成“点了没反应”）
        if (Math.Abs(dx) + Math.Abs(dy) > 2)
        {
            _hud?.Log("距离不够，请靠近后再操作");
            return;
        }

        switch (intent)
        {
            case Intent.Gather:
                if (view.Get("Pickable", WorkTarget.Parser) is null)
                {
                    _hud?.Log("目标不可采集（不是浆果丛）");
                    return;
                }
                _client.Commands.Gather(id);
                break;
            case Intent.Chop:
                if (view.Get("Choppable", WorkTarget.Parser) is null)
                {
                    _hud?.Log("目标不可砍伐（不是树木）");
                    return;
                }
                if (!HasOwnCapability("Chopper"))
                {
                    _hud?.Log("徒手无法砍伐，请先装备斧头");
                    return;
                }
                _client.Commands.Chop(id);
                break;
            case Intent.Mine:
                if (view.Get("Minable", WorkTarget.Parser) is null)
                {
                    _hud?.Log("目标不可挖掘（不是矿脉）");
                    return;
                }
                if (!HasOwnCapability("Miner"))
                {
                    _hud?.Log("徒手无法挖掘，请先装备镐");
                    return;
                }
                _client.Commands.Mine(id);
                break;
            case Intent.Pickup:
                if (view.LootOf() is null)
                {
                    _hud?.Log("目标没有掉落物");
                    return;
                }
                _client.Commands.Pickup(id);
                break;
            case Intent.Attack:
                if (view.Get("Health", Health.Parser) is null ||
                    view.Get("Dead", Dead.Parser) is not null)
                {
                    _hud?.Log("目标不可攻击");
                    return;
                }
                _client.Commands.Attack(id);
                break;
        }
        _entityLayer?.PlayAction(_ownId, intent switch
        {
            Intent.Chop or Intent.Mine or Intent.Attack => "attack",
            Intent.Pickup => "gather",
            _ => intent.ToString().ToLowerInvariant(),
        });
    }

    /// <summary>选中实体的可读描述（名称/血量/工作量/可用动作）。</summary>
    private string DescribeSelected()
    {
        if (_selected is not { } id || _client is null ||
            !_client.World.Entities.TryGetValue(id, out var view))
        {
            return "无";
        }
        var cfg = _client.World.Config;
        if (view.Get("Player", Player.Parser) is not null)
            return $"玩家 #{id}";
        if (view.Get("Dead", Dead.Parser) is not null)
            return $"尸体 #{id}";
        var loot = view.LootOf();
        if (loot is not null)
        {
            var names = loot.Items.Select(i => $"{ItemName(cfg, (int)i.Kind)}×{i.Count}");
            return $"掉落物 #{id}：{string.Join("、", names)} [拾取]";
        }
        var wt = WorkTargetOf(view);
        if (wt is not null)
        {
            var action = view.Get("Choppable", WorkTarget.Parser) is not null ? "砍伐"
                : view.Get("Minable", WorkTarget.Parser) is not null ? "挖掘"
                : "采集";
            return $"{ItemName(cfg, (int)wt.Kind)} #{id} 工作量 {wt.WorkLeft}/{wt.MaxWork} [{action}]";
        }
        var ws = view.Get("Workstation", Workstation.Parser);
        if (ws is not null)
            return $"工作站#{ws.Type} #{id}";
        var bld = view.Get("Building", Building.Parser);
        if (bld is not null)
            return $"{((int)bld.Kind == 1 ? "火堆" : "木墙")} #{id}" + (bld.Placed ? "" : " [未放置]");
        var cr = view.Get("Creature", Creature.Parser);
        if (cr is not null)
        {
            var hp = view.Get("Health", Health.Parser);
            var hpTxt = hp is null ? "" : $" hp={hp.Cur}/{hp.Max}";
            var name = cr.Kind switch
            {
                CreatureKind.Rabbit => "兔子",
                CreatureKind.Wolf => "狼",
                CreatureKind.Boar => "野猪",
                CreatureKind.Deer => "鹿",
                CreatureKind.Spider => "蜘蛛",
                CreatureKind.Fishman => "鱼人",
                CreatureKind.Lizard => "蜥蜴",
                _ => "生物",
            };
            return $"{name} #{id}{hpTxt} [攻击]";
        }
        return $"实体 #{id}";
    }

    private void UpdateBagAndCraft(WorldService world)
    {
        if (_hud is null || !world.Entities.TryGetValue(_ownId, out var own)) return;
        var inv = own.Get("Inventory", Inventory.Parser);
        var crafting = own.Get("Crafting", Crafting.Parser);
        var cfg = world.Config;
        var signature = ComputeHudSignature(world, own);
        if (signature == _hudSignature) return;
        _hudSignature = signature;

        var items = (inv?.Items ?? new()).Select(it =>
            new ItemView((int)it.Kind, ItemName(cfg, (int)it.Kind), it.Count, ItemColor(cfg, (int)it.Kind),
                ItemIcon((int)it.Kind))).ToList();
        // M7：Equipped 下线——手持看玩家身上复制的主动能力组件，护甲从 Equip 反查护甲实体。
        var equipped = new HashSet<int> { EquippedKind() };
        foreach (var a in WornArmor()) equipped.Add(a.Kind);
        _hud.RenderInventory(items, equipped, cfg?.InventorySlots ?? 12);

        if (cfg is null) return;
        var ownPos = own.Get("Position", Position.Parser);
        var near = StationNear(world, ownPos);
        var materials = (inv?.Items ?? new())
            .Where(i => (int)i.Kind > 0)
            .GroupBy(i => (int)i.Kind)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Count)); // 同种多堆合并，否则重复键抛异常
        var recipes = cfg.Recipes.Select(r =>
        {
            var stationOk = (int)r.Workstation == 0 || near.Contains((int)r.Workstation);
            var can = stationOk && r.Ingredients.All(i => materials.GetValueOrDefault((int)i.Kind) >= i.Count);
            return new RecipeView(
                r.Id,
                ItemName(cfg, (int)r.Output.Kind),
                r.Ticks,
                (int)r.Workstation == 0
                    ? "徒手可做"
                    : stationOk
                        ? $"{WorkstationName((int)r.Workstation)}附近 ✓"
                        : $"需要靠近{WorkstationName((int)r.Workstation)}",
                can,
                r.Ingredients.Select(i =>
                    new IngredientView(ItemName(cfg, (int)i.Kind), materials.GetValueOrDefault((int)i.Kind), i.Count)).ToList());
        }).ToList();
        var total = crafting is null
            ? 0
            : (long)(cfg.Recipes.FirstOrDefault(r => r.Id == crafting.RecipeId)?.Ticks ?? 0);
        _hud.RenderCraft(
            recipes,
            crafting is null ? null : new CraftingView(crafting.RecipeId, (long)crafting.TicksLeft, total));
    }

    private static int ComputeHudSignature(WorldService world, EntityView own)
    {
        var hash = new HashCode();
        hash.Add(world.Config?.GetHashCode() ?? 0);
        foreach (var name in new[] { "Inventory", "Equip", "Chopper", "Miner", "Position" })
        {
            if (own.Components.TryGetValue(name, out var data)) AddBytes(ref hash, data);
        }
        if (own.Get("Crafting", Crafting.Parser) is { } crafting)
        {
            hash.Add(crafting.RecipeId);
            var total = world.Config?.Recipes.FirstOrDefault(r => r.Id == crafting.RecipeId)?.Ticks ?? 0;
            hash.Add(total > 0 ? crafting.TicksLeft * 20 / total : crafting.TicksLeft);
        }
        foreach (var view in world.Entities.Values.OrderBy(v => v.EntityId))
        {
            if (view.Components.ContainsKey("Workstation"))
            {
                hash.Add(view.EntityId);
                if (view.Components.TryGetValue("Workstation", out var ws)) AddBytes(ref hash, ws);
                if (view.Components.TryGetValue("Position", out var pos)) AddBytes(ref hash, pos);
            }
            if (view.Components.ContainsKey("Equipment") || view.Components.ContainsKey("Defense"))
            {
                hash.Add(view.EntityId);
                if (view.Components.TryGetValue("Equipment", out var eq)) AddBytes(ref hash, eq);
                if (view.Components.TryGetValue("Defense", out var def)) AddBytes(ref hash, def);
            }
        }
        return hash.ToHashCode();
    }

    private static void AddBytes(ref HashCode hash, byte[] data)
    {
        foreach (var b in data) hash.Add(b);
    }

    private static string WorkstationName(int type) => type switch
    {
        1 => "火堆",
        2 => "工作台",
        _ => $"工作站#{type}",
    };

    private static HashSet<int> StationNear(WorldService world, Position? ownPos)
    {
        var set = new HashSet<int>();
        if (ownPos is null) return set;
        foreach (var view in world.Entities.Values)
        {
            var ws = view.Get("Workstation", Workstation.Parser);
            var p = view.Get("Position", Position.Parser);
            if (ws is null || p is null) continue;
            if (Math.Abs(p.X - ownPos.X) + Math.Abs(p.Y - ownPos.Y) <= 3) set.Add((int)ws.Type);
        }
        return set;
    }

    private static string ItemName(GameConfig? cfg, int kind)
    {
        var t = cfg?.Templates.FirstOrDefault(x => (int)x.Kind == kind);
        return t?.Name ?? kind.ToString();
    }

    /// <summary>世界实体标签：资源带动作/掉落/工具提示，掉落物带数量，生物/建筑带中文名。</summary>
    private string? EntityName(EntityView view)
    {
        var pl = view.Get("Player", Player.Parser);
        if (pl is not null)
            return pl.Uid == _ownUid ? "我" : $"玩家 {pl.Uid}";
        if (view.LootOf() is { } lt)
            return string.Join("、", lt.Items.Select(i => $"{ItemName(_client?.World.Config, (int)i.Kind)}×{i.Count}"));
        if (view.Get("Choppable", WorkTarget.Parser) is not null)
            return HasOwnCapability("Chopper") ? "树·砍伐→木头" : "树·需斧头";
        if (view.Get("Minable", WorkTarget.Parser) is not null)
            return HasOwnCapability("Miner") ? "矿石·挖掘→燧石" : "矿石·需镐";
        if (view.Get("Pickable", WorkTarget.Parser) is not null)
            return "浆果丛·采集→浆果";
        if (view.Get("Creature", Creature.Parser) is { } cr)
        {
            var name = cr.Kind switch
            {
                CreatureKind.Rabbit => "兔子",
                CreatureKind.Wolf => "狼",
                CreatureKind.Boar => "野猪",
                CreatureKind.Deer => "鹿",
                CreatureKind.Spider => "蜘蛛",
                CreatureKind.Fishman => "鱼人",
                CreatureKind.Lizard => "蜥蜴",
                _ => "生物",
            };
            return view.Get("Dead", Dead.Parser) is not null ? name + "尸体" : name;
        }
        if (view.Get("Workstation", Workstation.Parser) is { } ws)
            return (int)ws.Type == 1 ? "火堆工作站" : "工作台";
        if (view.Get("Building", Building.Parser) is { } bld)
            return (int)bld.Kind == 1 ? "火堆" : "木墙";
        return null;
    }

    private static Color ItemColor(GameConfig? cfg, int kind)
    {
        var t = cfg?.Templates.FirstOrDefault(x => (int)x.Kind == kind);
        if (t is not null && t.Color.StartsWith("#") && int.TryParse(t.Color.AsSpan(1), NumberStyles.HexNumber, null, out var v))
        {
            return new Color(((v >> 16) & 0xff) / 255f, ((v >> 8) & 0xff) / 255f, (v & 0xff) / 255f);
        }
        return Colors.White;
    }

    private void WithBagSlot(int slot, Action<int> act)
    {
        var inv = OwnComponent("Inventory", Inventory.Parser);
        if (inv is null || slot < 0 || slot >= inv.Items.Count) return;
        var kind = (int)inv.Items[slot].Kind;
        if (kind > 0) act(kind);
    }

    private int OwnItemCount(int slot)
    {
        var inv = OwnComponent("Inventory", Inventory.Parser);
        return inv is not null && slot >= 0 && slot < inv.Items.Count ? inv.Items[slot].Count : 0;
    }

    private T? OwnComponent<T>(string name, MessageParser<T> parser) where T : class, IMessage<T> =>
        _client is not null && _client.World.Entities.TryGetValue(_ownId, out var view)
            ? view.Get(name, parser)
            : null;

    private async Task DoCraftAsync(string recipeId)
    {
        if (_client is null) return;
        var resp = await _client.Commands.CraftAsync(recipeId);
        _hud?.Log(resp is { Started: true }
            ? $"开始制作 {recipeId}（{resp.Ticks} ticks）"
            : $"制作失败: {CraftFailureText(resp?.Message)}");
    }

    private static string CraftFailureText(string? code) => code switch
    {
        null or "" => "请求超时，请检查连接",
        "insufficient materials" => "材料不足，请查看配方中的持有数量",
        "need workstation nearby" => "需要靠近配方指定的工作站（曼哈顿距离不超过 3 格）",
        "output stack full" => "背包没有足够空间",
        "already crafting" => "已有物品正在制作",
        "player dead" => "死亡状态无法制作",
        "player not found" => "玩家状态尚未就绪",
        "unknown recipe" => "配方不存在或客户端配置已过期",
        "world_unavailable" => "世界服务暂不可用",
        _ => code,
    };

    private static Texture2D BakeNormalTexture(TileMap tm)
    {
        var buf = new byte[tm.Width * tm.Height * 4];
        NormalMapBaker.Bake(tm, buf);
        var img = Image.CreateFromData(tm.Width, tm.Height, false, Image.Format.Rgba8, buf);
        return ImageTexture.CreateFromImage(img);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey rk && !rk.Echo && rk.Pressed)
        {
            var rkName = OS.GetKeycodeString(rk.Keycode);
            if (rkName == "Q") RotateView(-Mathf.Pi / 4);
            else if (rkName == "E") RotateView(Mathf.Pi / 4);
        }
        if (@event is InputEventKey ak && !ak.Echo && OS.GetKeycodeString(ak.Keycode) == "Space")
        {
            if (ak.Pressed)
            {
                _autoHeld = true;
                _autoNextAt = NowMs() + 150;
                _client?.Commands.Automate(); // 按下立即触发一次
            }
            else
            {
                _autoHeld = false;
            }
        }
        if (@event is InputEventMouseMotion mm)
        {
            _mouseWorld = ScreenToWorld(mm.Position);
        }
        else if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            if (_buildPreview is { } bp && _mouseWorld is { } mw)
            {
                _client?.Commands.Place(bp.EntityId, (int)MathF.Round(mw.X), (int)MathF.Round(mw.Y));
                _hud?.Log($"已请求放置 #{bp.EntityId} 到 ({mw.X:0},{mw.Y:0})");
                ExitBuildPreview();
                return;
            }
            var picked = ScreenToWorld(mb.Position);
            _selected = FindNearest(picked);
            // 点击实体 = 选中并直接执行对应动作（掉落物→拾取、浆果→采集、树→砍伐、矿→挖掘、生物→攻击）。
            if (_selected is { } sel &&
                sel != _ownId &&
                _client is not null &&
                _client.World.Entities.TryGetValue(sel, out var selView))
            {
                if (selView.LootOf() is not null)
                    TryAct(sel, Intent.Pickup);
                else if (selView.Get("Pickable", WorkTarget.Parser) is not null)
                    TryAct(sel, Intent.Gather);
                else if (selView.Get("Choppable", WorkTarget.Parser) is not null)
                    TryAct(sel, Intent.Chop);
                else if (selView.Get("Minable", WorkTarget.Parser) is not null)
                    TryAct(sel, Intent.Mine);
                else if (selView.Get("Health", Health.Parser) is not null &&
                         selView.Get("Dead", Dead.Parser) is null)
                    TryAct(sel, Intent.Attack);
            }
        }
    }

    private void UpdateGhost()
    {
        if (_buildPreview is not { } bp || _ghost is null || _mouseWorld is not { } mw) return;
        var local = IsoMath.WorldToLocal(mw.X, mw.Y);
        _ghost.SetLocal(new Vector2(local.X, local.Y));
        var now = NowMs();
        if (now - _lastBuildCheckAt < 100) return;
        _lastBuildCheckAt = now;
        var x = (int)MathF.Round(mw.X);
        var y = (int)MathF.Round(mw.Y);
        _ = CheckPlaceAsync(bp.EntityId, x, y);
    }

    private async Task CheckPlaceAsync(ulong entity, int x, int y)
    {
        if (_client is null || _buildPreview is null) return;
        var resp = await _client.Commands.BuildCheckAsync(entity, x, y);
        if (_buildPreview is not { } bp) return;
        _buildPreview = (bp.EntityId, bp.Kind, bp.W, bp.H, resp?.Ok ?? false);
        _ghost?.SetOk(resp?.Ok ?? false);
    }

    private void ExitBuildPreview()
    {
        _buildPreview = null;
        if (_ghost is not null) _ghost.Visible = false;
    }

    private ulong? FindNearest(System.Numerics.Vector2 world)
    {
        if (_client is null) return null;
        ulong? best = null;
        var bestDist = 0.6f;
        foreach (var (id, view) in _client.World.Entities)
        {
            var pos = view.Get("Position", Starve.Game.V1.Position.Parser);
            if (pos is null) continue;
            var dx = pos.X - world.X;
            var dy = pos.Y - world.Y;
            var d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < bestDist)
            {
                best = id;
                bestDist = d;
            }
        }
        return best;
    }

    private void WithSelected(Action<ulong> act)
    {
        if (_selected is null)
        {
            _hud?.Log("先点击选中目标");
            return;
        }
        act(_selected.Value);
    }

    private async Task DoBuildAsync(int kind)
    {
        if (_client is null) return;
        var resp = await _client.Commands.BuildAsync(kind);
        if (resp is null || !resp.Ok)
        {
            _hud?.Log($"建造失败: {resp?.Message ?? "超时"}");
            return;
        }
        var cfg = _client.World.Config;
        var b = cfg?.Buildings.FirstOrDefault(x => (int)x.Kind == kind);
        var w = b?.Width ?? 1;
        var h = b?.Height ?? 1;
        _buildPreview = (resp.Entity, kind, w, h, true);
        _ghost!.Configure(w, h);
        _ghost.Visible = true;
        if (_mouseWorld is not null) UpdateGhost();
        _hud?.Log($"已创建蓝图 #{resp.Entity}，移动鼠标选位置，点击放置");
    }

    private void UpdateHud()
    {
        if (_hud is null || _client is null) return;
        var w = _client.World;
        var own = w.Entities.TryGetValue(_ownId, out var view) ? view : null;
        var pos = own?.Get("Position", Starve.Game.V1.Position.Parser);
        var hp = own?.Get("Health", Health.Parser);
        var hunger = own?.Get("Hunger", Hunger.Parser);
        // M7：Health 不再携带防御（服务端只回 cur/max），总减免从 Equip 头/身槽位反查护甲求和。
        var defense = DefensePercent();
        var defTxt = defense > 0 ? $" 防御{defense}%" : "";
        _hud.SetStatus(
            $"实体数 {w.Count} | 昼夜 {w.DayLight:0.00} | 季节 {SeasonName(w.Season)}\n" +
            $"我 @({pos?.X ?? 0},{pos?.Y ?? 0}) hp={hp?.Cur}/{hp?.Max} 饥饿 {hunger?.Level} {EquipText()}{defTxt}\n" +
            $"选中: {DescribeSelected()}");
        _hud.SetToolState(HasOwnCapability("Chopper"), HasOwnCapability("Miner"));
    }

    private static string SeasonName(int season) => season switch
    {
        1 => "春",
        2 => "夏",
        3 => "秋",
        4 => "冬",
        _ => "?",
    };

    /// <summary>Q/E 旋转视角：WorldPivot 固定在屏幕中心，世界围绕跟随中的玩家旋转。</summary>
    private void RotateView(float delta)
    {
        _viewRotation += delta;
        _entityLayer?.SetViewRotation(_viewRotation);
    }

    /// <summary>屏幕坐标经 Godot 场景变换逆投影为世界坐标，自动覆盖旋转、缩放和平移。</summary>
    private System.Numerics.Vector2 ScreenToWorld(Vector2 screen)
    {
        if (_world is null) return System.Numerics.Vector2.Zero;
        var local = _world.ToLocal(screen);
        Func<float, float, float>? heightAt = _tilemap is null ? null : _tilemap.HeightAt;
        return IsoMath.LocalToWorld(local.X, local.Y, heightAt);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
