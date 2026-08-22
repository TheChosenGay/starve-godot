using Godot;

namespace GodotClient.Game;

/// <summary>
/// 火堆本体（世界空间）：三层程序化火焰（白芯/橙焰/红外焰）+ 余烬粒子 + 点光闪烁。
/// 空间暖光团由 VolumetricView 在 LUT 之后绘制，
/// 避免被夜间 LUT/环境光压暗染色——火焰本体在世界上仍随光照变化。
/// </summary>
public partial class FireView : Node2D
{
    private readonly Sprite2D _core = new() { Centered = true, Material = AddMat() };
    private readonly Sprite2D _mid = new() { Centered = true, Material = AddMat() };
    private readonly Sprite2D _outer = new() { Centered = true, Material = AddMat() };
    private PointLight2D? _light;
    private GpuParticles2D? _embers;
    private ulong _seed;

    public override void _Ready()
    {
        _seed = GetInstanceId();

        _core.Texture = FxTextures.Flame;
        _core.Position = new Vector2(0, -3);
        _core.Scale = new Vector2(0.55f, 0.55f);
        _core.Modulate = new Color(1f, 0.96f, 0.78f, 0.95f);

        _mid.Texture = FxTextures.Flame;
        _mid.Position = new Vector2(0, -5);
        _mid.Scale = new Vector2(0.85f, 0.85f);
        _mid.Modulate = new Color(1f, 0.62f, 0.24f, 0.9f);

        _outer.Texture = FxTextures.Flame;
        _outer.Position = new Vector2(0, -7);
        _outer.Scale = new Vector2(1.15f, 1.15f);
        _outer.Modulate = new Color(0.95f, 0.28f, 0.06f, 0.5f);

        AddChild(_core);
        AddChild(_mid);
        AddChild(_outer);

        _light = new PointLight2D
        {
            Texture = FxTextures.Glow,
            Color = new Color(1f, 0.58f, 0.25f),
            Energy = 1.6f,
            TextureScale = 3.4f,
        };
        AddChild(_light);

        _embers = MakeEmbers();
        AddChild(_embers);
    }

    public override void _Process(double delta)
    {
        var t = (float)Time.GetTicksMsec();
        var s1 = (float)(_seed % 7) / 7f;
        var s2 = (float)((_seed >> 3) % 13) / 13f;

        // 三层火焰错相闪烁：芯最活泼、外焰最舒缓
        var f0 = 0.82f + Mathf.Sin(t * 0.013f + s1 * Mathf.Tau) * 0.09f
                      + Mathf.Sin(t * 0.027f + s2 * Mathf.Tau) * 0.07f;
        var f1 = 0.85f + Mathf.Sin(t * 0.009f + s2 * Mathf.Tau) * 0.11f
                      + Mathf.Sin(t * 0.021f + s1 * Mathf.Tau) * 0.06f;
        var f2 = 0.80f + Mathf.Sin(t * 0.007f + s1 * Mathf.Tau) * 0.13f
                      + Mathf.Sin(t * 0.017f + s2 * Mathf.Tau) * 0.08f;

        _core.Scale = new Vector2(0.55f * f0, 0.55f * f0);
        _core.Modulate = new Color(1f, 0.96f, 0.78f, 0.95f * Mathf.Clamp(f0, 0.72f, 1f));
        _mid.Scale = new Vector2(0.85f * f1, 0.85f * f1);
        _mid.Modulate = new Color(1f, 0.62f, 0.24f, 0.9f * Mathf.Clamp(f1, 0.7f, 1f));
        _outer.Scale = new Vector2(1.15f * f2, 1.15f * f2);
        _outer.Modulate = new Color(0.95f, 0.28f, 0.06f, 0.5f * Mathf.Clamp(f2, 0.6f, 1f));

        if (_light is not null) _light.Energy = 1.5f + f1 * 0.5f;
    }

    private GpuParticles2D MakeEmbers()
    {
        var colorRamp = new GradientTexture1D
        {
            Gradient = new Gradient
            {
                Colors = new[]
                {
                    new Color(1f, 0.95f, 0.7f, 1f),
                    new Color(1f, 0.55f, 0.2f, 0.9f),
                    new Color(0.9f, 0.2f, 0.05f, 0f),
                },
                Offsets = new[] { 0f, 0.55f, 1f },
            },
            Width = 16,
        };
        var mat = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, -1, 0),
            InitialVelocityMin = 24,
            InitialVelocityMax = 62,
            Gravity = new Vector3(0, -18, 0),
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 6,
            ScaleMin = 0.35f,
            ScaleMax = 0.9f,
            ColorRamp = colorRamp,
        };
        return new GpuParticles2D
        {
            Texture = FxTextures.Dot,
            ProcessMaterial = mat,
            Amount = 26,
            Lifetime = 1.5f,
            Preprocess = 0.8f,
            Emitting = true,
            Position = new Vector2(0, -10),
            Modulate = new Color(1f, 0.8f, 0.45f),
        };
    }

    private static CanvasItemMaterial AddMat() =>
        new() { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
}
