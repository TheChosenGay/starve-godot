using Godot;

namespace GodotClient.Game;

/// <summary>
/// 天气视图（屏幕空间）：雨/雪粒子 + 雾色覆盖。
/// 数据来自协议层 WeatherSummary（平均雨/雾）+ 季节，渲染层不碰协议细节。
/// </summary>
public partial class WeatherView : Node2D
{
    private GpuParticles2D? _particles;
    private ColorRect? _fog;

    public override void _Ready()
    {
        _particles = new GpuParticles2D
        {
            Amount = 1,
            Emitting = false,
            Lifetime = 1.6,
            Texture = CreateStreakTexture(),
            ProcessMaterial = new ParticleProcessMaterial
            {
                Direction = new Vector3(0, 1, 0),
                Gravity = new Vector3(0, 1400, 0),
                InitialVelocityMin = 500,
                InitialVelocityMax = 720,
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(700, 40, 0),
            },
        };
        AddChild(_particles);

        _fog = new ColorRect
        {
            Color = new Color(0.62f, 0.7f, 0.78f, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _fog.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_fog);
    }

    public void SetWeather(float rain, float fog, int season, Vector2 viewport)
    {
        if (_particles is not null)
        {
            _particles.Position = viewport / 2;
            var mat = (ParticleProcessMaterial)_particles.ProcessMaterial;
            mat.EmissionBoxExtents = new Vector3(viewport.X, 40, 0);
            _particles.Amount = Mathf.Max(1, (int)(40 + rain * 400));
            _particles.Emitting = rain > 0.02;

            var winter = season == 4;
            _particles.Modulate = winter ? new Color(0.95f, 0.97f, 1f) : new Color(0.8f, 0.85f, 0.92f);
            mat.Gravity = new Vector3(0, winter ? 160 : 1400, 0);
            mat.InitialVelocityMin = winter ? 40 : 500;
            mat.InitialVelocityMax = winter ? 90 : 720;
        }

        if (_fog is not null)
        {
            var c = _fog.Color;
            c.A = Mathf.Clamp(fog * 0.35f, 0, 0.3f);
            _fog.Color = c;
        }
    }

    /// <summary>雨丝贴图：竖条渐变白（雪时靠 Modulate 变白）。</summary>
    private static Texture2D CreateStreakTexture()
    {
        var img = Image.CreateEmpty(4, 12, false, Image.Format.Rgba8);
        for (var y = 0; y < 12; y++)
        {
            var a = (byte)(y < 2 ? 0 : 160);
            for (var x = 0; x < 4; x++)
            {
                img.SetPixel(x, y, new Color(1, 1, 1, a / 255f));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
