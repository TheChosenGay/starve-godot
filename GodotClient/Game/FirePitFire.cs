using Godot;

namespace GodotClient.Game;

/// <summary>
/// 火盆火焰（使用手绘贴图 flame / glow / ember.png）。
/// 挂在火盆 Node2D 上，原点放在火盆中心即可；所有粒子均用加色混合。
/// </summary>
public partial class FirePitFire : Node2D
{
    private const string FlamePath = "res://assets/structures/fire-pit/particles/flame.png";
    private const string GlowPath = "res://assets/structures/fire-pit/particles/glow.png";
    private const string EmberPath = "res://assets/structures/fire-pit/particles/ember.png";

    private Sprite2D _glow = null!;
    private PointLight2D? _light;

    public override void _Ready()
    {
        // 光晕（氛围光）
        _glow = new Sprite2D
        {
            Texture = GD.Load<Texture2D>(GlowPath),
            Material = AddMat(),
            Modulate = new Color(1f, 0.72f, 0.4f, 0.45f),
            // glow.png 是 1024²：0.22 ≈ 225px 柔和光晕（贴图很大，不能按 1:1 缩放）
            Scale = new Vector2(0.22f, 0.22f),
        };
        AddChild(_glow);

        // 点光源（照亮地面）
        _light = new PointLight2D
        {
            Texture = GD.Load<Texture2D>(GlowPath),
            Color = new Color(1f, 0.58f, 0.25f),
            Energy = 1.1f,
            TextureScale = 0.42f, // 1024² 光照贴图，0.42 ≈ 430px 照亮半径
        };
        AddChild(_light);

        AddChild(MakeFlame());
        AddChild(MakeEmbers());
    }

    public override void _Process(double delta)
    {
        var t = (float)Time.GetTicksMsec();
        var pulse = 0.9f + Mathf.Sin(t * 0.006f) * 0.08f;
        _glow.Scale = new Vector2(0.22f * pulse, 0.22f * pulse);
        if (_light is not null)
            _light.Energy = 1.0f + pulse * 0.3f;
    }

    private CpuParticles2D MakeFlame()
    {
        var ramp = new Gradient
        {
            Colors = new[]
            {
                new Color(1f, 1f, 0.55f, 1f),
                new Color(1f, 0.2f, 0f, 0f),
            },
            Offsets = new[] { 0f, 1f },
        };
        return new CpuParticles2D
        {
            Texture = GD.Load<Texture2D>(FlamePath),
            Material = AddMat(),
            Amount = 40,
            Lifetime = 0.7f,
            Direction = new Vector2(0, -1),
            Spread = 18,
            Gravity = new Vector2(0, -25),
            InitialVelocityMin = 30,
            InitialVelocityMax = 70,
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 12,
            ScaleAmountMin = 0.04f,
            ScaleAmountMax = 0.1f,
            ColorRamp = ramp,
            Emitting = true,
        };
    }

    private CpuParticles2D MakeEmbers()
    {
        var ramp = new Gradient
        {
            Colors = new[]
            {
                new Color(1f, 0.95f, 0.7f, 1f),
                new Color(1f, 0.3f, 0f, 0f),
            },
            Offsets = new[] { 0f, 1f },
        };
        return new CpuParticles2D
        {
            Texture = GD.Load<Texture2D>(EmberPath),
            Material = AddMat(),
            Amount = 14,
            Lifetime = 1.1f,
            Direction = new Vector2(0, -1),
            Spread = 30,
            Gravity = new Vector2(0, -45),
            InitialVelocityMin = 40,
            InitialVelocityMax = 90,
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 8,
            ScaleAmountMin = 0.02f,
            ScaleAmountMax = 0.06f,
            ColorRamp = ramp,
            Emitting = true,
            Position = new Vector2(0, -5),
        };
    }

    private static CanvasItemMaterial AddMat() =>
        new() { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
}
