using System;
using System.Collections.Generic;
using Godot;
using Starve.Core;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

public readonly record struct LightTune(
    float SunEnergyMul,
    float SunPitchOffset,
    float AmbientMul,
    bool FogEnabled,
    float FogNearMul,
    float FogFarMul,
    CloudTune Cloud,
    bool SkyAmbient = true)
{
    public static LightTune Default { get; } = new(1f, 0f, 1f, false, 1f, 1f, CloudTune.Default);

    public float CloudCoverage => Cloud.Coverage;
    public float CloudThickness => Cloud.Thickness;
    public float CloudWind => Cloud.Wind;
    public float CloudHeight => Cloud.Height;
}

/// <summary>
/// 3D 世界容器：世界钉在原点不转；相机枢轴跟玩家，只绕 Y 水平环绕。
/// </summary>
public partial class World3DView : Node3D
{
    public EntityLayer3D Entities { get; }
    public MapView3D Terrain { get; }
    public DirectionalLight3D Sun { get; }
    public LightTune Tune { get; private set; } = LightTune.Default;

    private readonly Camera3D _camera;
    private readonly Node3D _pivot;
    private readonly Node3D _world;
    private readonly MeshInstance3D _flatGround;
    private readonly Node3D _probes;
    private readonly Node3D _lamps;
    private readonly OmniLight3D _playerLamp;
    private readonly List<OmniLight3D> _fireLamps = new();
    private readonly Godot.Environment _env;
    private readonly ShaderMaterial _skyMat;
    private readonly CloudLayer3D _clouds;
    private MeshInstance3D? _toonMark;
    private TileMap? _map;
    private float _timeOfDay = 0.5f;
    private int _season;
    private float _rain;
    private bool _lightning;

    public World3DView()
    {
        Name = "World3D";

        _pivot = new Node3D { Name = "CameraPivot" };
        AddChild(_pivot);
        var offset = IsoCamera3D.OrbitLocalOffset();
        _camera = new Camera3D
        {
            Name = "IsoCamera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            Current = true,
            Size = IsoCamera3D.OrthoSize(1080, 1),
            Near = 0.1f,
            Far = 200,
            Position = ToGodot(offset),
            RotationDegrees = new Vector3(-IsoCamera3D.PitchDegrees, 0, 0),
        };
        _pivot.AddChild(_camera);

        Sun = new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-50, 35, 0),
            LightEnergy = 1.55f,
            LightColor = new Color(1f, 0.95f, 0.82f),
            ShadowEnabled = false,
        };
        AddChild(Sun);

        _skyMat = new ShaderMaterial { Shader = ShaderLibrary.Load(ShaderLibrary.PanoramaTint) };
        _skyMat.SetShaderParameter("panorama", TerrainHaven.Load(TerrainHaven.SkyPanorama, new Color(0.38f, 0.58f, 0.86f)));
        _skyMat.SetShaderParameter("sky_tint", Colors.White);
        _skyMat.SetShaderParameter("energy", 1f);
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky
            {
                SkyMaterial = _skyMat,
                ProcessMode = Sky.ProcessModeEnum.Realtime,
                RadianceSize = Sky.RadianceSizeEnum.Size256,
            },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightColor = new Color(0.78f, 0.82f, 0.88f),
            AmbientLightEnergy = 0.38f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            FogEnabled = false,
            FogMode = Godot.Environment.FogModeEnum.Depth,
            FogLightColor = new Color(0.78f, 0.8f, 0.86f),
            FogDepthBegin = 14f,
            FogDepthEnd = 62f,
            FogAerialPerspective = 0.45f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            TonemapExposure = 1.05f,
            GlowEnabled = true,
            GlowIntensity = 0.5f,
            GlowBloom = 0.07f,
            GlowHdrThreshold = 0.72f,
        };
        AddChild(new WorldEnvironment { Name = "Atmosphere", Environment = _env });
        _clouds = new CloudLayer3D();
        AddChild(_clouds);

        _world = new Node3D { Name = "World" };
        AddChild(_world);
        _flatGround = MakeGround();
        _world.AddChild(_flatGround);
        _probes = new Node3D { Name = "DebugProbes" };
        _probes.AddChild(MakeProbe(Vector3.Zero, new Color(1f, 1f, 1f), "Origin"));
        _probes.AddChild(MakeProbe(new Vector3(5, 0, 0), new Color(0.9f, 0.25f, 0.2f), "AxisX"));
        _probes.AddChild(MakeProbe(new Vector3(0, 0, 5), new Color(0.2f, 0.45f, 0.95f), "AxisZ"));
        _world.AddChild(_probes);

        Terrain = new MapView3D { Name = "Terrain" };
        _world.AddChild(Terrain);
        Entities = new EntityLayer3D { Name = "EntityLayer3D" };
        _world.AddChild(Entities);
        _lamps = new Node3D { Name = "Lamps" };
        _world.AddChild(_lamps);
        _playerLamp = new OmniLight3D
        {
            Name = "PlayerLamp",
            LightColor = new Color(1f, 0.78f, 0.42f),
            OmniRange = 5.5f,
            OmniAttenuation = 1.1f,
            LightEnergy = 0.2f,
            ShadowEnabled = false,
        };
        _lamps.AddChild(_playerLamp);
    }

    public void SetMap(TileMap tm)
    {
        _map = tm;
        _flatGround.Visible = false;
        _probes.Visible = false;
        Terrain.SetMap(tm);
        var diag = MathF.Sqrt(tm.Width * tm.Width + tm.Height * tm.Height);
        _camera.Far = Math.Max(200f, IsoCamera3D.Distance + diag + 32f);
        ApplyCycle();
    }

    /// <summary>高度比例或细分改了之后重烘焙网格，角色仍走同一套 WorldTo3D。</summary>
    public void RebuildTerrain()
    {
        if (_map is { } tm)
            Terrain.SetMap(tm);
    }

    /// <summary>世界 UV 密度：只改 shader，不用重烘焙。</summary>
    public void SetTerrainTiling(float tiling)
    {
        MapMeshBuilder.WorldTiling = tiling;
        Terrain.TerrainMat?.SetShaderParameter("uWorldTiling", MapMeshBuilder.WorldTiling);
    }

    /// <summary>高度混合锐度与陡坡出岩：只改 shader。</summary>
    public void SetTerrainBlend(float sharpness, float slopeRock)
    {
        if (Terrain.TerrainMat is not { } mat) return;
        mat.SetShaderParameter("uHeightSharpness", Mathf.Clamp(sharpness, 0.04f, 0.8f));
        mat.SetShaderParameter("uSlopeRock", Mathf.Clamp(slopeRock, 0f, 1f));
    }

    public void SyncView(float camX, float camY, float height, float zoom, float viewRotation, Vector2 viewport)
    {
        var target = IsoCamera3D.WorldTo3D(camX, camY, height);
        _pivot.Position = ToGodot(target);
        _pivot.RotationDegrees = new Vector3(
            0,
            IsoCamera3D.YawDegrees + viewRotation * (180f / MathF.PI),
            0);
        _camera.Position = ToGodot(IsoCamera3D.OrbitLocalOffset());
        _camera.RotationDegrees = new Vector3(-IsoCamera3D.PitchDegrees, 0, 0);
        _camera.Size = IsoCamera3D.OrthoSize(viewport.Y, zoom);
        _world.Position = Vector3.Zero;
        _world.Rotation = Vector3.Zero;
        _clouds.Follow(_pivot.Position);
        PushCloudShadow();
    }

    public void SetDayCycle(float timeOfDay, int season, float rain, bool lightning)
    {
        _timeOfDay = timeOfDay;
        _season = season;
        _rain = rain;
        _lightning = lightning;
        ApplyCycle();
    }

    public void SetTune(LightTune tune)
    {
        Tune = tune;
        ApplyCycle();
    }

    public void SetCloudsVisible(bool visible) => _clouds.Visible = visible;

    public void SetGlowEnabled(bool enabled) => _env.GlowEnabled = enabled;

    public void SyncPointLights(
        IReadOnlyList<(float X, float Y, float H)> fires,
        float ownX,
        float ownY,
        float ownH)
    {
        var look = DayCyclePalette.Evaluate(_timeOfDay, _season, _lightning, _rain);
        while (_fireLamps.Count < fires.Count)
        {
            var lamp = new OmniLight3D
            {
                LightColor = new Color(1.7f, 0.78f, 0.28f),
                OmniRange = 10f,
                OmniAttenuation = 0.85f,
                ShadowEnabled = false,
            };
            _lamps.AddChild(lamp);
            _fireLamps.Add(lamp);
        }
        var now = Time.GetTicksMsec() * 0.001f;
        for (var i = 0; i < _fireLamps.Count; i++)
        {
            var lamp = _fireLamps[i];
            if (i >= fires.Count)
            {
                lamp.Visible = false;
                continue;
            }
            var f = fires[i];
            var p = IsoCamera3D.WorldTo3D(f.X, f.Y, f.H);
            lamp.Position = new Vector3(p.X, p.Y + 0.7f, p.Z);
            lamp.Visible = true;
            var flick = 1f
                + 0.14f * MathF.Sin(now * 8.7f + i * 2.1f)
                + 0.08f * MathF.Sin(now * 17.3f + i * 5.9f);
            lamp.LightEnergy = look.FireEnergy * flick;
            lamp.OmniRange = 7.5f + 3.5f * look.NightWeight;
        }

        var op = IsoCamera3D.WorldTo3D(ownX, ownY, ownH);
        _playerLamp.Position = new Vector3(op.X, op.Y + 1.05f, op.Z);
        _playerLamp.LightEnergy = look.LanternEnergy;
        _playerLamp.OmniRange = 4.5f + 2.2f * look.NightWeight;
        _playerLamp.Visible = look.LanternEnergy > 0.06f;
    }

    private void ApplyCycle()
    {
        var look = DayCyclePalette.Evaluate(_timeOfDay, _season, _lightning, _rain);
        Sun.LightEnergy = look.SunEnergy * Tune.SunEnergyMul;
        Sun.LightColor = ToColor(look.SunColor);
        Sun.RotationDegrees = new Vector3(
            -(look.SunPitchDegrees + Tune.SunPitchOffset),
            look.SunYawDegrees,
            0);
        _env.AmbientLightSource = Tune.SkyAmbient
            ? Godot.Environment.AmbientSource.Sky
            : Godot.Environment.AmbientSource.Color;
        _env.ReflectedLightSource = Tune.SkyAmbient
            ? Godot.Environment.ReflectionSource.Sky
            : Godot.Environment.ReflectionSource.Disabled;
        _env.AmbientLightEnergy = look.AmbientEnergy * Tune.AmbientMul;
        _env.AmbientLightColor = ToColor(look.AmbientColor);
        _env.FogEnabled = Tune.FogEnabled;
        _env.TonemapExposure = 0.88f + 0.22f * look.NoonWeight;
        _env.SkyRotation = new Vector3(0f, look.SunYawDegrees * (MathF.PI / 180f), 0f);
        var skyTint = new Color(
            Mathf.Lerp(look.SkyTop.X, 1f, look.NoonWeight * 0.35f),
            Mathf.Lerp(look.SkyTop.Y, 1f, look.NoonWeight * 0.28f),
            Mathf.Lerp(look.SkyTop.Z, 1f, look.NoonWeight * 0.15f));
        skyTint = skyTint.Lerp(ToColor(look.SkyHorizon), look.DuskWeight * 0.45f + look.MorningWeight * 0.25f);
        _skyMat.SetShaderParameter("sky_tint", skyTint.Lerp(Colors.White, 0.35f));
        _skyMat.SetShaderParameter("energy", Mathf.Lerp(0.12f, 1.05f, look.SunElevation));
        GhibliSky.Apply(_clouds.VolumeMat, look, Tune, Sun.GlobalTransform.Basis.Z, _rain);
        GhibliSky.Apply(_clouds.ShadowMat, look, Tune, Sun.GlobalTransform.Basis.Z, _rain);
        _clouds.SetHeight(Tune.CloudHeight);
        PushCloudShadow(look);
        ToonMaterials.ApplyDayLightToTree(Terrain, look.SunElevation);
        ToonMaterials.ApplyDayLightToTree(Entities, look.SunElevation);
    }

    private void PushCloudShadow(DayCycleLook? look = null)
    {
        if (Terrain.TerrainMat is not { } terrainMat) return;
        var cycle = look ?? DayCyclePalette.Evaluate(_timeOfDay, _season, _lightning, _rain);
        ToonMaterials.ApplyCloudShadow(
            terrainMat, cycle, Tune, Sun.GlobalTransform.Basis.Z, _pivot.Position, Tune.CloudHeight, _rain);
    }

    private static Color ToColor(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>按相机射线点选最近实体（点模型身体，不依赖脚底落点）。</summary>
    public bool TryPickVisual(Vector2 screen, out ulong id, out Node3D node)
    {
        id = 0;
        node = null!;
        var origin = _camera.ProjectRayOrigin(screen);
        var dir = _camera.ProjectRayNormal(screen);
        if (dir.LengthSquared() < 1e-10f) return false;
        dir = dir.Normalized();

        var best = 1.6f;
        foreach (var (eid, visual) in Entities.VisualsById)
        {
            if (!GodotObject.IsInstanceValid(visual)) continue;
            var center = visual.GlobalPosition + new Vector3(0, 0.7f, 0);
            var t = (center - origin).Dot(dir);
            if (t < 0.15f) continue;
            var dist = (origin + dir * t).DistanceTo(center);
            if (dist >= best) continue;
            best = dist;
            id = eid;
            node = visual;
        }
        return node is not null;
    }

    public void ShowToonMark(Node3D? host)
    {
        _toonMark ??= MakeToonMark();
        var parent = _toonMark.GetParent();
        if (parent is not null) parent.RemoveChild(_toonMark);
        if (host is null || !GodotObject.IsInstanceValid(host)) return;
        host.AddChild(_toonMark);
        _toonMark.Position = new Vector3(0, 0.05f, 0);
        _toonMark.Visible = true;
    }

    private static MeshInstance3D MakeToonMark()
    {
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.85f, 0.2f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        return new MeshInstance3D
        {
            Name = "ToonSelectMark",
            Mesh = new TorusMesh { InnerRadius = 0.42f, OuterRadius = 0.52f, Material = mat },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>屏幕点 → 世界格：射线与高度场求交，迭代一次修正高度。</summary>
    public System.Numerics.Vector2 ScreenToWorld(Vector2 screen, Func<float, float, float>? heightAt)
    {
        var origin = _camera.ProjectRayOrigin(screen);
        var dir = _camera.ProjectRayNormal(screen);
        var localOrigin = _world.ToLocal(origin);
        var localTip = _world.ToLocal(origin + dir);
        var localDir = localTip - localOrigin;
        if (localDir.LengthSquared() < 1e-10f) return System.Numerics.Vector2.Zero;
        localDir = localDir.Normalized();
        if (MathF.Abs(localDir.Y) < 1e-5f) return System.Numerics.Vector2.Zero;

        var h = 0f;
        var hit = Vector3.Zero;
        for (var i = 0; i < 2; i++)
        {
            var t = (h - localOrigin.Y) / localDir.Y;
            hit = localOrigin + localDir * t;
            if (heightAt is null) break;
            h = IsoCamera3D.VisualY(heightAt(hit.X, hit.Z));
        }
        return IsoCamera3D.WorldFrom3D(hit.X, hit.Z);
    }

    private static MeshInstance3D MakeGround()
    {
        return new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh
            {
                Size = new Vector2(256, 256),
                Material = ToonMaterials.CreateGround(),
            },
        };
    }

    private static MeshInstance3D MakeProbe(Vector3 position, Color color, string name)
    {
        var mat = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.5f };
        return new MeshInstance3D
        {
            Name = name,
            Position = position + new Vector3(0, 0.2f, 0),
            Mesh = new BoxMesh { Size = new Vector3(0.4f, 0.4f, 0.4f), Material = mat },
        };
    }

    private static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
