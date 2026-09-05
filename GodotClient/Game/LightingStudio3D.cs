using System;
using System.Collections.Generic;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 光照沙盘：季节 × 朝阳/盛阳/夕阳组合出光；冬天额外积雪。
/// F6 运行本场景，不必连服务器。
/// </summary>
public partial class LightingStudio3D : Node3D
{
    private Camera3D _camera = null!;
    private Node3D _pivot = null!;
    private DirectionalLight3D _sun = null!;
    private OmniLight3D _fireLight = null!;
    private OmniLight3D _lanternLight = null!;
    private Godot.Environment _env = null!;
    private ShaderMaterial _skyMat = null!;
    private CloudLayer3D _clouds = null!;
    private LightingStudioPanel _panel = null!;
    private FireTunePanel _firePanel = null!;
    private GpuParticles3D _snowFall = null!;
    private Shader _coverShader = null!;
    private readonly List<CoverSurf> _covers = [];
    private readonly List<MeshInstance3D> _snowCaps = [];
    private readonly List<FireFlame3D> _flames = [];
    private PigmanActor3D _pigman = null!;
    private AlchemyEngine3D _alchemy = null!;
    private float _alchemyBounceIn = 0.35f;

    private float _yaw = IsoCamera3D.YawDegrees;
    private float _pitch = 34f;
    private float _zoom = 12f;
    private bool _orbiting;
    private Solo _solo = Solo.All;

    public enum Solo
    {
        All,
        Sun,
        Fire,
        Lantern,
        Ambient,
    }

    private readonly record struct CoverSurf(ShaderMaterial Mat, CoverKind Kind);

    private enum CoverKind
    {
        Ground,
        Crown,
        Bush,
        Rock,
        Log,
    }

    public override void _Ready()
    {
        Name = "LightingStudio3D";
        BuildWorld();
        _panel = new LightingStudioPanel();
        _panel.Changed += ApplyScene;
        _panel.SoloChanged += solo =>
        {
            _solo = solo;
            ApplyScene();
        };
        _panel.ActorToonChanged += ApplyPigmanToon;
        AddChild(_panel);
        _firePanel = new FireTunePanel();
        AddChild(_firePanel);
        _firePanel.Bind(_flames);
        _panel.ReapplyCombo();
        ApplyCamera();
    }

    public override void _Process(double delta)
    {
        var yaw = 0f;
        var pitch = 0f;
        if (Input.IsPhysicalKeyPressed(Key.Q)) yaw -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.E)) yaw += 1f;
        if (Input.IsPhysicalKeyPressed(Key.R)) pitch -= 1f;
        if (Input.IsPhysicalKeyPressed(Key.F)) pitch += 1f;
        if (yaw != 0f || pitch != 0f)
        {
            _yaw += yaw * 70f * (float)delta;
            _pitch = Mathf.Clamp(_pitch + pitch * 45f * (float)delta, 12f, 68f);
            ApplyCamera();
        }
        var flick = 1f
            + 0.12f * MathF.Sin((float)Time.GetTicksMsec() * 0.0087f)
            + 0.07f * MathF.Sin((float)Time.GetTicksMsec() * 0.0173f);
        if (_solo is Solo.All or Solo.Fire)
            _fireLight.LightEnergy = _panel.FireEnergy * flick;

        _alchemyBounceIn -= (float)delta;
        if (_alchemyBounceIn <= 0f)
        {
            _alchemy.PlayBounce();
            _alchemyBounceIn = 3.8f;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex is MouseButton.Right or MouseButton.Middle)
            {
                _orbiting = mb.Pressed;
                GetViewport().SetInputAsHandled();
                return;
            }
            if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
            {
                _zoom = Mathf.Clamp(_zoom - 0.8f, 5f, 22f);
                ApplyCamera();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown)
            {
                _zoom = Mathf.Clamp(_zoom + 0.8f, 5f, 22f);
                ApplyCamera();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (mb.Pressed && mb.ButtonIndex == MouseButton.Left && !_firePanel.Hits(mb.Position))
            {
                var pick = PickFlame(mb.Position);
                if (pick >= 0)
                {
                    _firePanel.Select(pick);
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
        }
        if (@event is InputEventMouseMotion mm && _orbiting)
        {
            _yaw += mm.Relative.X * 0.28f;
            _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.22f, 12f, 68f);
            ApplyCamera();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            var solo = OS.GetKeycodeString(key.Keycode) switch
            {
                "0" or "Kp 0" => Solo.All,
                "1" or "Kp 1" => Solo.Sun,
                "2" or "Kp 2" => Solo.Fire,
                "3" or "Kp 3" => Solo.Lantern,
                "4" or "Kp 4" => Solo.Ambient,
                _ => (Solo?)null,
            };
            if (solo is { } s)
            {
                _solo = s;
                _panel.SetSolo(s);
                ApplyScene();
            }
        }
    }

    private int PickFlame(Vector2 screen)
    {
        var best = -1;
        var bestD = 56f;
        for (var i = 0; i < _flames.Count; i++)
        {
            var flame = _flames[i];
            var tip = flame.GlobalPosition + new Vector3(0, flame.Style.MeshHeight * 0.5f, 0);
            var d = _camera.UnprojectPosition(tip).DistanceTo(screen);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    private void ApplyPigmanToon(ToonShaderKind? kind)
    {
        if (kind is null)
        {
            _pigman.ApplyToon = false;
            return;
        }
        ToonMaterials.CreateKind = kind.Value;
        var style = kind == ToonShaderKind.Cel
            ? ToonStyle.CelDefaults()
            : ToonMaterials.ActorDefaults.Clone();
        style.Kind = kind.Value;
        if (!_pigman.ApplyToon)
            _pigman.ApplyToon = true;
        ToonMaterials.ApplyStyleToTree(_pigman, style);
    }

    private void ApplyCamera()
    {
        _pivot.RotationDegrees = new Vector3(0, _yaw, 0);
        var elev = _pitch * (MathF.PI / 180f);
        _camera.Position = new Vector3(
            0,
            IsoCamera3D.Distance * MathF.Sin(elev),
            IsoCamera3D.Distance * MathF.Cos(elev));
        _camera.RotationDegrees = new Vector3(-_pitch, 0, 0);
        _camera.Size = _zoom;
    }

    private void ApplyScene()
    {
        ApplyLights();
        ApplySeasonLook();
    }

    private void ApplyLights()
    {
        var sunOn = _solo is Solo.All or Solo.Sun;
        var fireOn = _solo is Solo.All or Solo.Fire;
        var lanternOn = _solo is Solo.All or Solo.Lantern;
        var ambientOn = _solo is Solo.All or Solo.Ambient;
        var look = DayCyclePalette.Evaluate(_panel.TimeOfDay, _panel.Season);

        _sun.LightEnergy = sunOn ? _panel.SunEnergy : 0f;
        _sun.LightColor = _panel.SunColor;
        _sun.RotationDegrees = new Vector3(-_panel.SunPitch, _panel.SunYaw, 0);
        _sun.Visible = sunOn && _panel.SunEnergy > 0.01f;

        _fireLight.LightEnergy = fireOn ? _panel.FireEnergy : 0f;
        _fireLight.LightColor = _panel.FireColor;
        _fireLight.OmniRange = _panel.FireRange;
        _fireLight.Visible = fireOn && _panel.FireEnergy > 0.01f;
        foreach (var flame in _flames)
            flame.LightOn = fireOn;

        _lanternLight.LightEnergy = lanternOn ? _panel.LanternEnergy : 0f;
        _lanternLight.LightColor = _panel.LanternColor;
        _lanternLight.OmniRange = _panel.LanternRange;
        _lanternLight.Visible = lanternOn && _panel.LanternEnergy > 0.01f;

        _env.AmbientLightEnergy = ambientOn ? _panel.AmbientEnergy : 0.008f;
        _env.AmbientLightColor = _panel.AmbientColor;
        _env.TonemapExposure = 0.88f + 0.22f * look.NoonWeight;
        var tune = new LightTune(1f, 0f, 1f, false, 1f, 1f,
            new CloudTune(_panel.CloudCoverage, _panel.CloudThickness, _panel.CloudWind, _panel.CloudHeight));
        var camLook = -_camera.GlobalTransform.Basis.Z;
        GhibliSky.Apply(_skyMat, look, tune, _sun.GlobalTransform.Basis.Z, 0f, camLook);
        GhibliSky.Apply(_clouds.VolumeMat, look, tune, _sun.GlobalTransform.Basis.Z, 0f, camLook);
        GhibliSky.Apply(_clouds.ShadowMat, look, tune, _sun.GlobalTransform.Basis.Z, 0f, camLook);
        _clouds.SetHeight(_panel.CloudHeight);
    }

    private void ApplySeasonLook()
    {
        var season = _panel.Season;
        var winter = season == DayCyclePalette.SeasonWinter;
        foreach (var cover in _covers)
        {
            cover.Mat.SetShaderParameter("albedo", AlbedoOf(cover.Kind, season));
            cover.Mat.SetShaderParameter("grid", GridOf(season));
            cover.Mat.SetShaderParameter("grid_mix", cover.Kind == CoverKind.Ground ? (winter ? 0.16f : 0.5f) : 0f);
            cover.Mat.SetShaderParameter("snow_cover", winter ? 1f : 0f);
        }
        foreach (var cap in _snowCaps)
            cap.Visible = winter;
        _snowFall.Emitting = winter;
        _snowFall.Visible = winter;
    }

    private void BuildWorld()
    {
        _coverShader = new Shader { Code = CoverShaderCode };
        _pivot = new Node3D { Name = "CameraPivot" };
        AddChild(_pivot);
        _camera = new Camera3D
        {
            Name = "IsoCamera",
            Projection = Camera3D.ProjectionType.Orthogonal,
            Current = true,
            Size = _zoom,
            Near = 0.1f,
            Far = 160f,
        };
        _pivot.AddChild(_camera);

        _skyMat = GhibliSky.Create();
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = GhibliSky.CreateSky(_skyMat),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.55f, 0.58f, 0.65f),
            AmbientLightEnergy = 0.28f,
            FogEnabled = false,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            GlowEnabled = true,
            GlowIntensity = 0.45f,
            GlowBloom = 0.06f,
            GlowHdrThreshold = 0.7f,
        };
        AddChild(new WorldEnvironment { Environment = _env });
        _clouds = new CloudLayer3D(groundShadow: true);
        AddChild(_clouds);

        _sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightColor = new Color(1f, 0.96f, 0.88f),
            LightEnergy = 1.5f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
        };
        AddChild(_sun);

        AddChild(MakeGround());
        PlaceTree(-3.2f, -2.4f, 1.15f);
        PlaceTree(3.6f, -2.8f, 0.95f);
        PlaceTree(-4.1f, 2.2f, 1.35f);
        PlaceTree(4.4f, 1.6f, 1.05f);
        PlaceTree(0.8f, -4.2f, 0.85f);
        PlaceTree(-0.6f, 4.6f, 0.72f);
        PlaceRock(-1.6f, 2.8f, 0.7f);
        PlaceRock(2.4f, 3.2f, 0.55f);
        PlaceRock(-2.8f, -3.6f, 0.45f);
        PlaceRock(4.8f, -0.4f, 0.38f);
        PlaceBush(-2.2f, 0.6f, 0.7f);
        PlaceBush(1.4f, -1.8f, 0.55f);
        PlaceBush(3.8f, 3.4f, 0.8f);
        PlaceLog(-1.1f, -0.85f);

        var fire = MakeFirePit();
        fire.Position = new Vector3(0, 0, 0);
        AddChild(fire);
        _fireLight = fire.GetNode<OmniLight3D>("Light");
        PlaceTestFlame(new Vector3(-4.2f, 0, 6.4f), FireStyle.Tall());
        PlaceTestFlame(new Vector3(-2.1f, 0, 6.4f), FireStyle.Wide());
        PlaceTestFlame(new Vector3(0.0f, 0, 6.4f), FireStyle.Blue());
        PlaceTestFlame(new Vector3(2.1f, 0, 6.4f), FireStyle.WillOWisp());
        PlaceTestFlame(new Vector3(4.2f, 0, 6.4f), FireStyle.Flicker());

        var lantern = MakeLantern();
        lantern.Position = new Vector3(2.3f, 0, 1.1f);
        AddChild(lantern);
        _lanternLight = lantern.GetNode<OmniLight3D>("Light");

        _pigman = new PigmanActor3D
        {
            Name = "Pigman",
            Playing = false,
            ApplyToon = false,
            Position = new Vector3(2.5f, 0, 2.2f),
        };
        AddChild(_pigman);
        _alchemy = new AlchemyEngine3D
        {
            Name = "AlchemyEngine",
            Position = new Vector3(-2.4f, 0, 1.6f),
        };
        AddChild(_alchemy);

        _snowFall = MakeSnowFall();
        AddChild(_snowFall);

        AddLabel(new Vector3(2.3f, 2.15f, 1.1f), "灯笼");
        AddLabel(new Vector3(2.5f, 1.85f, 2.2f), "角色 Toon");
        AddLabel(new Vector3(-2.4f, 2.15f, 1.6f), "炼金引擎");
        AddLabel(new Vector3(-3.2f, 2.1f, -2.4f), "树");
    }

    private MeshInstance3D MakeGround()
    {
        var mat = MakeCover(CoverKind.Ground, 0.05f, 0.35f);
        return new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(24, 24), Material = mat },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    private void PlaceTree(float x, float z, float scale)
    {
        var trunk = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.38f, 0.26f, 0.16f),
            Roughness = 0.92f,
        };
        var crownMat = MakeCover(CoverKind.Crown, 0.28f, 0.78f);
        var capMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.93f, 0.96f, 0.99f),
            Roughness = 0.62f,
        };
        var tree = new Node3D
        {
            Position = new Vector3(x, 0, z),
            Scale = Vector3.One * scale,
        };
        tree.AddChild(new MeshInstance3D
        {
            Position = new Vector3(0, 0.45f, 0),
            Mesh = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.16f, Height = 0.9f },
            MaterialOverride = trunk,
        });
        tree.AddChild(new MeshInstance3D
        {
            Position = new Vector3(0, 1.15f, 0),
            Mesh = new SphereMesh { Radius = 0.55f, Height = 1.05f },
            MaterialOverride = crownMat,
        });
        var cap = new MeshInstance3D
        {
            Name = "SnowCap",
            Position = new Vector3(0, 1.58f, 0),
            Scale = new Vector3(0.82f, 0.28f, 0.82f),
            Mesh = new SphereMesh { Radius = 0.55f, Height = 1.05f },
            MaterialOverride = capMat,
            Visible = false,
        };
        tree.AddChild(cap);
        _snowCaps.Add(cap);
        AddChild(tree);
    }

    private void PlaceBush(float x, float z, float scale)
    {
        var mat = MakeCover(CoverKind.Bush, 0.22f, 0.75f);
        var bush = new MeshInstance3D
        {
            Position = new Vector3(x, 0.22f * scale, z),
            Scale = Vector3.One * scale,
            Mesh = new SphereMesh { Radius = 0.32f, Height = 0.44f },
            MaterialOverride = mat,
        };
        AddChild(bush);
        var cap = new MeshInstance3D
        {
            Name = "SnowCap",
            Position = new Vector3(x, 0.38f * scale, z),
            Scale = new Vector3(0.7f, 0.22f, 0.7f) * scale,
            Mesh = new SphereMesh { Radius = 0.32f, Height = 0.44f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.93f, 0.96f, 0.99f),
                Roughness = 0.62f,
            },
            Visible = false,
        };
        _snowCaps.Add(cap);
        AddChild(cap);
    }

    private void PlaceLog(float x, float z)
    {
        var mat = MakeCover(CoverKind.Log, 0.35f, 0.82f);
        AddChild(new MeshInstance3D
        {
            Position = new Vector3(x, 0.1f, z),
            RotationDegrees = new Vector3(0, 38f, 90f),
            Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.1f, Height = 1.15f },
            MaterialOverride = mat,
        });
    }

    private void PlaceRock(float x, float z, float scale)
    {
        var mat = MakeCover(CoverKind.Rock, 0.32f, 0.8f);
        AddChild(new MeshInstance3D
        {
            Position = new Vector3(x, 0.12f * scale, z),
            Scale = Vector3.One * scale,
            Mesh = new SphereMesh { Radius = 0.38f, Height = 0.42f },
            MaterialOverride = mat,
        });
    }

    private ShaderMaterial MakeCover(CoverKind kind, float snowStart, float snowEnd)
    {
        var mat = new ShaderMaterial { Shader = _coverShader };
        mat.SetShaderParameter("albedo", AlbedoOf(kind, DayCyclePalette.SeasonSpring));
        mat.SetShaderParameter("grid", GridOf(DayCyclePalette.SeasonSpring));
        mat.SetShaderParameter("grid_mix", kind == CoverKind.Ground ? 0.5f : 0f);
        mat.SetShaderParameter("snow_cover", 0f);
        mat.SetShaderParameter("snow_color", new Color(0.92f, 0.95f, 0.98f));
        mat.SetShaderParameter("snow_start", snowStart);
        mat.SetShaderParameter("snow_end", snowEnd);
        _covers.Add(new CoverSurf(mat, kind));
        return mat;
    }

    private static GpuParticles3D MakeSnowFall()
    {
        var process = new ParticleProcessMaterial
        {
            Direction = new Vector3(0.22f, -1f, 0.06f),
            Spread = 16f,
            Gravity = new Vector3(0.12f, -0.28f, 0f),
            InitialVelocityMin = 0.55f,
            InitialVelocityMax = 1.25f,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(11f, 0.4f, 11f),
            ScaleMin = 0.7f,
            ScaleMax = 1.35f,
            Color = new Color(0.95f, 0.97f, 1f),
        };
        return new GpuParticles3D
        {
            Name = "SnowFall",
            Amount = 320,
            Lifetime = 7.5,
            Preprocess = 4,
            Emitting = false,
            VisibilityAabb = new Aabb(new Vector3(-14f, -2f, -14f), new Vector3(28f, 16f, 28f)),
            ProcessMaterial = process,
            DrawPass1 = new SphereMesh
            {
                Radius = 0.035f,
                Height = 0.07f,
                RadialSegments = 6,
                Rings = 3,
            },
            Position = new Vector3(0, 8.5f, 0),
            Visible = false,
        };
    }

    private void PlaceTestFlame(Vector3 pos, FireStyle style)
    {
        var flame = new FireFlame3D { OwnsLight = true };
        flame.ApplyStyle(style);
        flame.Position = pos;
        AddChild(flame);
        _flames.Add(flame);
    }

    private Node3D MakeFirePit()
    {
        var root = FireFlame3D.CreatePit(caption: true);
        if (root.GetNodeOrNull<FireFlame3D>("Flame") is { } flame)
            _flames.Add(flame);
        root.AddChild(new OmniLight3D
        {
            Name = "Light",
            Position = new Vector3(0, 0.7f, 0),
            LightColor = new Color(1.7f, 0.72f, 0.22f),
            LightEnergy = 3.2f,
            OmniRange = 8f,
            OmniAttenuation = 0.85f,
            ShadowEnabled = false,
        });
        return root;
    }

    private static Node3D MakeLantern()
    {
        var root = new Node3D { Name = "Lantern" };
        var wood = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.38f, 0.26f, 0.16f),
            Roughness = 0.9f,
        };
        var glass = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.82f, 0.45f),
            EmissionEnabled = true,
            Emission = new Color(1f, 0.75f, 0.35f),
            EmissionEnergyMultiplier = 1.6f,
        };
        root.AddChild(new MeshInstance3D
        {
            Position = new Vector3(0, 0.55f, 0),
            Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.05f, Height = 1.1f },
            MaterialOverride = wood,
        });
        root.AddChild(new MeshInstance3D
        {
            Name = "Lamp",
            Position = new Vector3(0, 1.25f, 0),
            Mesh = new BoxMesh { Size = new Vector3(0.22f, 0.28f, 0.22f) },
            MaterialOverride = glass,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        root.AddChild(new OmniLight3D
        {
            Name = "Light",
            Position = new Vector3(0, 1.28f, 0),
            LightColor = new Color(1f, 0.78f, 0.4f),
            LightEnergy = 2.2f,
            OmniRange = 5f,
            OmniAttenuation = 1.1f,
            ShadowEnabled = false,
        });
        return root;
    }

    private void AddLabel(Vector3 pos, string text)
    {
        AddChild(new Label3D
        {
            Text = text,
            Position = pos,
            PixelSize = 0.012f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            OutlineSize = 8,
            FontSize = 28,
        });
    }

    private static Color AlbedoOf(CoverKind kind, int season) => kind switch
    {
        CoverKind.Ground => season switch
        {
            DayCyclePalette.SeasonSummer => new Color(0.30f, 0.46f, 0.20f),
            DayCyclePalette.SeasonAutumn => new Color(0.46f, 0.32f, 0.16f),
            DayCyclePalette.SeasonWinter => new Color(0.52f, 0.56f, 0.60f),
            _ => new Color(0.36f, 0.46f, 0.30f),
        },
        CoverKind.Crown or CoverKind.Bush => season switch
        {
            DayCyclePalette.SeasonSummer => new Color(0.18f, 0.44f, 0.16f),
            DayCyclePalette.SeasonAutumn => new Color(0.72f, 0.34f, 0.12f),
            DayCyclePalette.SeasonWinter => new Color(0.40f, 0.42f, 0.36f),
            _ => new Color(0.32f, 0.54f, 0.26f),
        },
        CoverKind.Rock => new Color(0.42f, 0.40f, 0.38f),
        _ => new Color(0.34f, 0.22f, 0.12f),
    };

    private static Color GridOf(int season) => season == DayCyclePalette.SeasonWinter
        ? new Color(0.72f, 0.78f, 0.86f)
        : new Color(0.24f, 0.28f, 0.20f);

    // 朝上的面混雪色：游戏里最常见的「积雪」做法，树冠顶白、底仍是叶子。
    private const string CoverShaderCode =
        """
        shader_type spatial;
        render_mode specular_schlick_ggx;

        uniform vec4 albedo : source_color = vec4(0.36, 0.40, 0.32, 1.0);
        uniform vec4 grid : source_color = vec4(0.24, 0.28, 0.20, 1.0);
        uniform float grid_mix : hint_range(0.0, 1.0) = 0.0;
        uniform float snow_cover : hint_range(0.0, 1.0) = 0.0;
        uniform vec4 snow_color : source_color = vec4(0.92, 0.95, 0.98, 1.0);
        uniform float snow_start : hint_range(-0.2, 1.0) = 0.28;
        uniform float snow_end : hint_range(0.0, 1.0) = 0.78;
        varying vec3 world_pos;
        varying vec3 world_n;

        void vertex() {
        	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        	world_n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
        }

        void fragment() {
        	vec2 cell = abs(fract(world_pos.xz) - 0.5);
        	float line = 1.0 - step(0.03, min(cell.x, cell.y));
        	vec3 base = mix(albedo.rgb, grid.rgb, line * grid_mix);
        	float cap = smoothstep(snow_start, snow_end, clamp(world_n.y, 0.0, 1.0));
        	float snow = snow_cover * cap;
        	ALBEDO = mix(base, snow_color.rgb, snow);
        	ROUGHNESS = mix(0.94, 0.58, snow);
        	METALLIC = 0.0;
        	EMISSION = snow_color.rgb * snow * 0.035;
        }
        """;
}

/// <summary>沙盘右侧：季节 × 时段组合，再单独微调火/灯。</summary>
public partial class LightingStudioPanel : CanvasLayer
{
    public event Action? Changed;
    public event Action<LightingStudio3D.Solo>? SoloChanged;
    public event Action<ToonShaderKind?>? ActorToonChanged;

    public int Season { get; private set; } = DayCyclePalette.SeasonSpring;
    public float TimeOfDay { get; private set; } = 0.5f;
    public float SunEnergy { get; private set; } = 1.5f;
    public float SunPitch { get; private set; } = 50f;
    public float SunYaw { get; private set; } = 35f;
    public Color SunColor { get; private set; } = new(1f, 0.96f, 0.88f);
    public float AmbientEnergy { get; private set; } = 0.28f;
    public Color AmbientColor { get; private set; } = new(0.55f, 0.58f, 0.65f);
    public float FireEnergy { get; private set; } = 3.2f;
    public float FireRange { get; private set; } = 8f;
    public Color FireColor { get; private set; } = new(1.7f, 0.72f, 0.22f);
    public float LanternEnergy { get; private set; } = 2.2f;
    public float LanternRange { get; private set; } = 5f;
    public Color LanternColor { get; private set; } = new(1f, 0.78f, 0.4f);
    public float CloudCoverage { get; private set; } = 0.77f;
    public float CloudThickness { get; private set; } = CloudTune.Default.Thickness;
    public float CloudWind { get; private set; } = CloudTune.Default.Wind;
    public float CloudHeight { get; private set; } = CloudTune.Default.Height;

    private bool _syncing;
    private Label? _soloLabel;
    private Label? _comboLabel;
    private readonly List<(HSlider Slider, Label Num, Func<float> Get)> _sliders = [];
    private readonly List<(ColorPickerButton Picker, Func<Color> Get)> _colors = [];

    public override void _Ready()
    {
        Name = "LightingStudioPanel";
        var ui = new Control
        {
            Name = "Ui",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        ui.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(ui);

        var frame = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        frame.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        frame.OffsetLeft = -330;
        frame.OffsetTop = 12;
        frame.OffsetRight = -12;
        frame.OffsetBottom = 860;
        frame.Theme = HudTheme.Create();
        ui.AddChild(frame);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        frame.AddChild(scroll);
        var box = new VBoxContainer();
        scroll.AddChild(box);
        box.AddThemeConstantOverride("separation", 5);

        box.AddChild(new Label { Text = "光照沙盘" });
        box.AddChild(new Label
        {
            Text = "右键拖旋转  Q/E 左右  R/F 俯仰  滚轮远近\n0 全开  1 太阳  2 火  3 灯  4 只环境\n左侧改火焰外形和颜色，左键点选火焰",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _comboLabel = new Label { Text = "组合：春 · 盛阳" };
        box.AddChild(_comboLabel);
        _soloLabel = new Label { Text = "当前：全部开" };
        box.AddChild(_soloLabel);

        var row = new HBoxContainer();
        AddSolo(row, "全开", LightingStudio3D.Solo.All);
        AddSolo(row, "太阳", LightingStudio3D.Solo.Sun);
        AddSolo(row, "火", LightingStudio3D.Solo.Fire);
        AddSolo(row, "灯", LightingStudio3D.Solo.Lantern);
        AddSolo(row, "环境", LightingStudio3D.Solo.Ambient);
        box.AddChild(row);

        box.AddChild(new Label { Text = "季节" });
        var seasons = new HBoxContainer();
        var seasonGroup = new ButtonGroup();
        AddChoice(seasons, seasonGroup, "春", true, () => SetSeason(DayCyclePalette.SeasonSpring));
        AddChoice(seasons, seasonGroup, "夏", false, () => SetSeason(DayCyclePalette.SeasonSummer));
        AddChoice(seasons, seasonGroup, "秋", false, () => SetSeason(DayCyclePalette.SeasonAutumn));
        AddChoice(seasons, seasonGroup, "冬", false, () => SetSeason(DayCyclePalette.SeasonWinter));
        box.AddChild(seasons);

        box.AddChild(new Label { Text = "一天三段（叠在当前季节上）" });
        var times = new HBoxContainer();
        var timeGroup = new ButtonGroup();
        AddChoice(times, timeGroup, "朝阳", false, () => SetTime(0.25f));
        AddChoice(times, timeGroup, "盛阳", true, () => SetTime(0.5f));
        AddChoice(times, timeGroup, "夕阳", false, () => SetTime(0.75f));
        box.AddChild(times);

        box.AddChild(new Label { Text = "角色 Toon（原色阶 / gameidea Cel）" });
        var toonRow = new HBoxContainer();
        var toonGroup = new ButtonGroup();
        AddChoice(toonRow, toonGroup, "关", true, () => ActorToonChanged?.Invoke(null));
        AddChoice(toonRow, toonGroup, "色阶", false, () => ActorToonChanged?.Invoke(ToonShaderKind.Bands));
        AddChoice(toonRow, toonGroup, "Cel", false, () => ActorToonChanged?.Invoke(ToonShaderKind.Cel));
        box.AddChild(toonRow);

        box.AddChild(new Label { Text = "太阳（组合写入后可再微调）" });
        AddSlider(box, "能量", 0, 3, 0.05f, () => SunEnergy, v => SunEnergy = v);
        AddSlider(box, "高度°", 5, 80, 1, () => SunPitch, v => SunPitch = v);
        AddSlider(box, "方位°", -120, 160, 1, () => SunYaw, v => SunYaw = v);
        AddColor(box, "颜色", () => SunColor, c => SunColor = c);

        box.AddChild(new Label { Text = "环境光" });
        AddSlider(box, "能量", 0, 1.2f, 0.02f, () => AmbientEnergy, v => AmbientEnergy = v);
        AddColor(box, "颜色", () => AmbientColor, c => AmbientColor = c);

        box.AddChild(new Label { Text = "火堆" });
        AddSlider(box, "能量", 0, 8, 0.05f, () => FireEnergy, v => FireEnergy = v);
        AddSlider(box, "范围", 1, 16, 0.1f, () => FireRange, v => FireRange = v);
        AddColor(box, "颜色", () => FireColor, c => FireColor = c);

        box.AddChild(new Label { Text = "灯笼" });
        AddSlider(box, "能量", 0, 6, 0.05f, () => LanternEnergy, v => LanternEnergy = v);
        AddSlider(box, "范围", 1, 12, 0.1f, () => LanternRange, v => LanternRange = v);
        AddColor(box, "颜色", () => LanternColor, c => LanternColor = c);

        box.AddChild(new Label { Text = "吉卜力云" });
        AddSlider(box, "云量", 0.05f, 0.95f, 0.01f, () => CloudCoverage, v => CloudCoverage = v);
        AddSlider(box, "厚度", 18f, 90f, 1f, () => CloudThickness, v => CloudThickness = v);
        AddSlider(box, "风速", 0f, 2f, 0.01f, () => CloudWind, v => CloudWind = v);
        AddSlider(box, "高度", 8f, 48f, 0.5f, () => CloudHeight, v => CloudHeight = v);
    }

    public void ReapplyCombo() => ApplyLook(DayCyclePalette.Evaluate(TimeOfDay, Season));

    public void SetSolo(LightingStudio3D.Solo solo)
    {
        if (_soloLabel is null) return;
        _soloLabel.Text = solo switch
        {
            LightingStudio3D.Solo.Sun => "当前：只太阳",
            LightingStudio3D.Solo.Fire => "当前：只火堆",
            LightingStudio3D.Solo.Lantern => "当前：只灯笼",
            LightingStudio3D.Solo.Ambient => "当前：只环境光",
            _ => "当前：全部开",
        };
    }

    private void SetSeason(int season)
    {
        Season = season;
        ReapplyCombo();
    }

    private void SetTime(float time)
    {
        TimeOfDay = time;
        ReapplyCombo();
    }

    private void AddSolo(HBoxContainer row, string title, LightingStudio3D.Solo solo)
    {
        var btn = new Button { Text = title, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        btn.Pressed += () =>
        {
            SetSolo(solo);
            SoloChanged?.Invoke(solo);
        };
        row.AddChild(btn);
    }

    private static void AddChoice(HBoxContainer row, ButtonGroup group, string title, bool on, Action pick)
    {
        var btn = new Button
        {
            Text = title,
            ToggleMode = true,
            ButtonGroup = group,
            ButtonPressed = on,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        btn.Pressed += pick;
        row.AddChild(btn);
    }

    private void ApplyLook(DayCycleLook look)
    {
        _syncing = true;
        SunEnergy = look.SunEnergy;
        SunPitch = look.SunPitchDegrees;
        SunYaw = look.SunYawDegrees;
        SunColor = ToColor(look.SunColor);
        AmbientEnergy = look.AmbientEnergy;
        AmbientColor = ToColor(look.AmbientColor);
        FireEnergy = look.FireEnergy;
        LanternEnergy = look.LanternEnergy;
        if (_comboLabel is not null)
            _comboLabel.Text = $"组合：{SeasonName(Season)} · {TimeName(TimeOfDay)}";
        ReloadSliderValues();
        _syncing = false;
        Changed?.Invoke();
    }

    private void ReloadSliderValues()
    {
        foreach (var (slider, num, get) in _sliders)
        {
            var v = get();
            slider.SetValueNoSignal(v);
            num.Text = Format(v);
        }
        foreach (var (picker, get) in _colors)
            picker.Color = get();
    }

    private void AddSlider(VBoxContainer box, string title, float min, float max, float step, Func<float> get, Action<float> set)
    {
        var value = get();
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = title, CustomMinimumSize = new Vector2(52, 0) });
        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        var num = new Label { Text = Format(value), CustomMinimumSize = new Vector2(36, 0) };
        slider.ValueChanged += v =>
        {
            num.Text = Format((float)v);
            if (_syncing) return;
            set((float)v);
            Changed?.Invoke();
        };
        _sliders.Add((slider, num, get));
        row.AddChild(slider);
        row.AddChild(num);
        box.AddChild(row);
    }

    private void AddColor(VBoxContainer box, string title, Func<Color> get, Action<Color> set)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = title, CustomMinimumSize = new Vector2(52, 0) });
        var picker = new ColorPickerButton
        {
            Color = get(),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(120, 28),
        };
        picker.ColorChanged += c =>
        {
            if (_syncing) return;
            set(c);
            Changed?.Invoke();
        };
        _colors.Add((picker, get));
        row.AddChild(picker);
        box.AddChild(row);
    }

    private static string SeasonName(int season) => season switch
    {
        DayCyclePalette.SeasonSummer => "夏",
        DayCyclePalette.SeasonAutumn => "秋",
        DayCyclePalette.SeasonWinter => "冬",
        _ => "春",
    };

    private static string TimeName(float time) => time switch
    {
        <= 0.3f => "朝阳",
        >= 0.7f => "夕阳",
        _ => "盛阳",
    };

    private static Color ToColor(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    private static string Format(float v) => v >= 10 ? v.ToString("0") : v.ToString("0.00");
}
