using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Starve.Core;
using Starve.Game.V1;
using Starve.Protocol;
using Starve.Protocol.World;
using Camera = Starve.Core.Camera;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>
/// 游戏主节点：分层编排——
/// 协议层（StarveClient）→ Core 逻辑（相机/地形/平滑/法线）→ 渲染层。
/// 渲染层不直接碰协议细节，协议层不碰渲染。
/// </summary>
public partial class GameRoot : Node
{
    private readonly Camera _camera = new();
    private readonly Dictionary<ulong, PositionSmoother> _smoothers = new();
    private readonly Dictionary<ulong, long> _movingUntil = new();
    private readonly Dictionary<ulong, (float X, float Y)> _lastServerPos = new();

    private StarveClient? _client;
    private TileMap? _tilemap;
    private Node2D? _world;
    private MapView? _mapView;
    private EntityLayer? _entityLayer;
    private ParallaxView? _parallax;
    private WeatherView? _weather;
    private MinimapView? _minimap;
    private LightingPass? _lighting;
    private LutPass? _lut;
    private GhostNode? _ghost;
    private Hud? _hud;
    private int _lastRevision = -1;
    private int _lastWeatherRevision = -1;
    private long? _captureAt;
    private ulong _ownId;
    private ulong? _selected;
    private (ulong EntityId, int Kind, int W, int H, bool Ok)? _buildPreview;
    private System.Numerics.Vector2? _mouseWorld;
    private long _lastBuildCheckAt;

    private static bool SmokeMode => OS.GetCmdlineUserArgs().Contains("--smoke");
    private static string? CapturePath => OS.GetCmdlineUserArgs()
        .SkipWhile(a => a != "--capture")
        .Skip(1)
        .FirstOrDefault();

    public override void _Ready()
    {
        ActorNode.Preload();

        _parallax = new ParallaxView { Name = "Parallax" };
        AddChild(_parallax);
        _world = new Node2D { Name = "World" };
        AddChild(_world);
        _mapView = new MapView { Name = "MapView" };
        _world.AddChild(_mapView);
        _entityLayer = new EntityLayer { Name = "EntityLayer" };
        _world.AddChild(_entityLayer);
        _ghost = new GhostNode { Name = "Ghost", ZIndex = 4096, Visible = false };
        _world.AddChild(_ghost);

        _weather = new WeatherView { Name = "Weather" };
        AddChild(_weather);
        _lighting = new LightingPass { Name = "Lighting" };
        AddChild(_lighting);
        _lut = new LutPass { Name = "Lut" };
        _lut.SetAtlas(LutBuilder.Build().Atlas);
        AddChild(_lut);

        var ui = new CanvasLayer { Layer = 10 };
        AddChild(ui);
        _minimap = new MinimapView { Name = "Minimap" };
        ui.AddChild(_minimap);
        _hud = new Hud { Name = "Hud" };
        ui.AddChild(_hud);
        WireHud(_hud);

        AddChild(new CameraController { Camera = _camera });
        var move = new MoveController();
        move.OnMove += dir => _client?.Commands.Move(dir.Dx, dir.Dy);
        AddChild(move);

        _hud.Log("连接中…");
        _ = StartAsync();
    }

    private void WireHud(Hud hud)
    {
        hud.GatherPressed += () => WithSelected(id =>
        {
            _entityLayer?.PlayAction(id, "gather");
            _client?.Commands.Gather(id);
        });
        hud.AttackPressed += () => WithSelected(id =>
        {
            _entityLayer?.PlayAction(id, "attack");
            _client?.Commands.Attack(id);
        });
        hud.PickupPressed += () => WithSelected(id => _client?.Commands.Pickup(id));
        hud.DemolishPressed += () => WithSelected(id => _client?.Commands.Demolish(id));
        hud.BuildPressed += kind => _ = DoBuildAsync(kind);
    }

    private async Task StartAsync()
    {
        _client = new StarveClient();
        try
        {
            var uid = System.Environment.GetEnvironmentVariable("STARVE_UID") ?? "42";
            var info = await _client.ConnectAsync("ws://localhost:8081/ws", DevTokens.Mint(uid));
            _ownId = info.EntityId;
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
            if (_captureAt is null) _captureAt = NowMs() + 3000;
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
        System.Numerics.Vector2? own = _smoothers.TryGetValue(_ownId, out var sm) ? sm.Current(now) : null;
        _camera.Follow(own?.X, own?.Y);
        _camera.Tick((float)(delta * 1000));

        var viewport = GetViewport().GetVisibleRect().Size;
        var hCam = _tilemap?.HeightAt(_camera.CenterX(), _camera.CenterY()) ?? 0;
        var pos = IsoMath.ContainerPosition(
            viewport.X,
            viewport.Y,
            _camera.CenterX(),
            _camera.CenterY(),
            hCam,
            _camera.ZoomLevel);
        _world!.Position = new Vector2(pos.X, pos.Y);
        _world.Scale = Vector2.One * _camera.ZoomLevel;

        var fx = (_camera.CenterX() - _camera.CenterY()) * IsoMath.Step * _camera.ZoomLevel;
        var fy = ((_camera.CenterX() + _camera.CenterY()) * IsoMath.Step / 2 - hCam * IsoMath.Step) *
                 _camera.ZoomLevel;
        _parallax!.UpdateParallax(fx, fy, viewport);

        if (client.World.Revision != _lastWeatherRevision)
        {
            _lastWeatherRevision = client.World.Revision;
            var w = client.World.Weather;
            _weather!.SetWeather(w?.Rain ?? 0, w?.Fog ?? 0, client.World.Season, viewport);
            UpdateLut(client.World.DayLight);
        }

        UpdateLighting(client.World, viewport, _camera.ZoomLevel, own);
        _lighting!.Size = viewport;
        _lut!.Size = viewport;
        if (_buildPreview is not null && _mouseWorld is not null) UpdateGhost();

        _entityLayer!.UpdatePositions(_smoothers, id => _movingUntil.GetValueOrDefault(id) > now, now);
        UpdateHud();
    }

    private void UpdateLighting(WorldService world, Vector2 viewport, float zoom, System.Numerics.Vector2? own)
    {
        var dayLight = world.DayLight;
        var dark = Mathf.Max(0, 1 - dayLight * 2);
        var sunT = 1 - dark;
        var ambient = 0.92f - dark * 0.3f;
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
                lightColor.Add(new Color(1.35f, 0.85f, 0.4f));
                lightRadius.Add(8f);
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
        foreach (var (id, view) in world.Entities)
        {
            var pos = view.Get("Position", Starve.Game.V1.Position.Parser);
            if (pos is null) continue;
            if (!_smoothers.TryGetValue(id, out var smoother))
            {
                smoother = new PositionSmoother(id == _ownId ? 60 : 160);
                _smoothers[id] = smoother;
            }
            smoother.Update(pos.X, pos.Y, now);
            if (_lastServerPos.TryGetValue(id, out var prev) &&
                (MathF.Abs(prev.X - pos.X) > 0.001f || MathF.Abs(prev.Y - pos.Y) > 0.001f))
            {
                _movingUntil[id] = now + 240;
            }
            _lastServerPos[id] = (pos.X, pos.Y);
        }

        _entityLayer!.SyncEntities(world.Entities);
    }

    private static Texture2D BakeNormalTexture(TileMap tm)
    {
        var buf = new byte[tm.Width * tm.Height * 4];
        NormalMapBaker.Bake(tm, buf);
        var img = Image.CreateFromData(tm.Width, tm.Height, false, Image.Format.Rgba8, buf);
        return ImageTexture.CreateFromImage(img);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mm)
        {
            var viewport = GetViewport().GetVisibleRect().Size;
            var w = _camera.ScreenToWorld(mm.Position.X, mm.Position.Y, viewport.X, viewport.Y);
            _mouseWorld = new System.Numerics.Vector2(w.X, w.Y);
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
            var viewport = GetViewport().GetVisibleRect().Size;
            var w = _camera.ScreenToWorld(mb.Position.X, mb.Position.Y, viewport.X, viewport.Y);
            _selected = FindNearest(new System.Numerics.Vector2(w.X, w.Y));
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
        _hud.Log($"已创建蓝图 #{resp.Entity}，移动鼠标选位置，点击放置");
    }

    private void UpdateHud()
    {
        if (_hud is null || _client is null) return;
        var w = _client.World;
        var own = w.Entities.TryGetValue(_ownId, out var view) ? view : null;
        var pos = own?.Get("Position", Starve.Game.V1.Position.Parser);
        var hp = own?.Get("Health", Health.Parser);
        var hunger = own?.Get("Hunger", Hunger.Parser);
        var selected = _selected is not null && w.Entities.TryGetValue(_selected.Value, out _)
            ? $"#{_selected.Value}"
            : "无";
        _hud.SetStatus(
            $"实体数 {w.Count} | 昼夜 {w.DayLight:0.00} | 季节 {SeasonName(w.Season)}\n" +
            $"我 @({pos?.X ?? 0},{pos?.Y ?? 0}) hp={hp?.Cur}/{hp?.Max} 饥饿 {hunger?.Level}\n" +
            $"选中: {selected}");
    }

    private static string SeasonName(int season) => season switch
    {
        1 => "春",
        2 => "夏",
        3 => "秋",
        4 => "冬",
        _ => "?",
    };

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
