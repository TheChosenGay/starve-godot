using System;
using Godot;
using Starve.Core;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>
/// 3D 世界容器：世界固定，相机 45° 俯视跟随玩家；地面来自 TileMap 高度场。
/// </summary>
public partial class World3DView : Node3D
{
    public EntityLayer3D Entities { get; }
    public MapView3D Terrain { get; }

    private readonly Camera3D _camera;
    private readonly Node3D _pivot;
    private readonly Node3D _world;
    private readonly MeshInstance3D _flatGround;
    private readonly Node3D _probes;

    public World3DView()
    {
        Name = "World3D";
        var (pos, rot) = IsoCamera3D.CameraPose();
        _camera = new Camera3D
        {
            Name = "IsoCamera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            Current = true,
            Size = IsoCamera3D.OrthoSize(1080, 1),
            Near = 0.1f,
            Far = 200,
            Position = ToGodot(pos),
            RotationDegrees = ToGodot(rot),
        };
        AddChild(_camera);

        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-55, 25, 0),
            LightEnergy = 1.35f,
            LightColor = new Color(1f, 0.96f, 0.88f),
            ShadowEnabled = false,
        });

        _pivot = new Node3D { Name = "WorldPivot3D" };
        AddChild(_pivot);
        _world = new Node3D { Name = "World" };
        _pivot.AddChild(_world);
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
    }

    public void SetMap(TileMap tm)
    {
        _flatGround.Visible = false;
        _probes.Visible = false;
        Terrain.SetMap(tm);
        var diag = MathF.Sqrt(tm.Width * tm.Width + tm.Height * tm.Height);
        _camera.Far = Math.Max(200f, IsoCamera3D.Distance + diag + 32f);
    }

    public void SyncView(float camX, float camY, float height, float zoom, float viewRotation, Vector2 viewport)
    {
        var target = IsoCamera3D.WorldTo3D(camX, camY, height);
        var (rel, rot) = IsoCamera3D.CameraPose(viewRotation);
        _camera.Position = ToGodot(target + rel);
        _camera.RotationDegrees = ToGodot(rot);
        _camera.Size = IsoCamera3D.OrthoSize(viewport.Y, zoom);
        _pivot.Rotation = Vector3.Zero;
        _world.Position = Vector3.Zero;
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
            h = heightAt(hit.X, hit.Z);
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
