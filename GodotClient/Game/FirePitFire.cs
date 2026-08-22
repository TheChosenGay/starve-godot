using Godot;

namespace GodotClient.Game;

/// <summary>
/// 火盆火焰。粒子必须 LocalCoords，否则相机一斜移就会拖出一条斜尾。
/// 贴图都是 1024²，只能用很小的 Scale，否则会盖住半个屏幕。
/// </summary>
public partial class FirePitFire : Node2D
{
    private const string FlamePath = "res://assets/structures/fire-pit/particles/flame.png";
    private const string GlowPath = "res://assets/structures/fire-pit/particles/glow.png";
    private const string EmberPath = "res://assets/structures/fire-pit/particles/ember.png";

    private Sprite2D _glow = null!;
    private Sprite2D _shaft = null!;
    private PointLight2D? _light;

    public override void _Ready()
    {
        _glow = new Sprite2D
        {
            Texture = GD.Load<Texture2D>(GlowPath),
            Material = AddMat(),
            Modulate = new Color(1f, 0.62f, 0.32f, 0.55f),
            Scale = new Vector2(0.055f, 0.055f),
            Position = new Vector2(0, -6),
        };
        AddChild(_glow);

        _shaft = new Sprite2D
        {
            Texture = FxTextures.Shaft,
            Material = AddMat(),
            Modulate = new Color(1f, 0.7f, 0.35f, 0.28f),
            Scale = new Vector2(0.55f, 0.9f),
            Position = new Vector2(0, -28),
        };
        AddChild(_shaft);

        _light = new PointLight2D
        {
            Texture = GD.Load<Texture2D>(GlowPath),
            Color = new Color(1f, 0.58f, 0.28f),
            Energy = 0.85f,
            TextureScale = 0.16f,
            Position = new Vector2(0, -4),
        };
        AddChild(_light);

        AddChild(MakeFlame());
        AddChild(MakeEmbers());
    }

    public override void _Process(double delta)
    {
        var t = (float)Time.GetTicksMsec();
        var pulse = 0.92f + Mathf.Sin(t * 0.007f) * 0.07f;
        _glow.Scale = new Vector2(0.055f * pulse, 0.055f * pulse);
        _shaft.Modulate = new Color(1f, 0.7f, 0.35f, 0.22f + pulse * 0.08f);
        if (_light is not null)
            _light.Energy = 0.75f + pulse * 0.25f;
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
            Amount = 22,
            Lifetime = 0.55f,
            LocalCoords = true,
            Direction = new Vector2(0, -1),
            Spread = 10,
            Gravity = new Vector2(0, -18),
            InitialVelocityMin = 12,
            InitialVelocityMax = 28,
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 3,
            ScaleAmountMin = 0.012f,
            ScaleAmountMax = 0.022f,
            ColorRamp = ramp,
            Emitting = true,
            Position = new Vector2(0, -4),
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
            Amount = 8,
            Lifetime = 0.8f,
            LocalCoords = true,
            Direction = new Vector2(0, -1),
            Spread = 16,
            Gravity = new Vector2(0, -22),
            InitialVelocityMin = 16,
            InitialVelocityMax = 32,
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 2,
            ScaleAmountMin = 0.008f,
            ScaleAmountMax = 0.016f,
            ColorRamp = ramp,
            Emitting = true,
            Position = new Vector2(0, -6),
        };
    }

    private static CanvasItemMaterial AddMat() =>
        new() { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
}
