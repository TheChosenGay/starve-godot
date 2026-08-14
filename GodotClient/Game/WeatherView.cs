using System;
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
    private ColorRect? _flash;
    private Line2D? _bolt;
    private bool _raining;
    private double _nextLightning = 8;
    private double _flashLeft;

    /// <summary>闪电触发（GameRoot 借此提升环境光一瞬间）。</summary>
    public event Action? OnLightning;

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

        _flash = new ColorRect
        {
            Color = new Color(1, 1, 1, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_flash);
        _bolt = new Line2D { Width = 2, DefaultColor = new Color(0.9f, 0.95f, 1f), Visible = false };
        AddChild(_bolt);
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
            _raining = rain > 0.02;

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

    /// <summary>每帧推进：闪电随机触发（雨天），闪光 + 锯齿折线 + 事件。</summary>
    public void Tick(double delta, Vector2 viewport)
    {
        if (_raining && _nextLightning > 0)
        {
            _nextLightning -= delta;
            if (_nextLightning <= 0)
            {
                _nextLightning = 6 + GD.RandRange(3, 12);
                _flashLeft = 0.22;
                var x0 = (float)GD.RandRange(80, viewport.X - 80);
                var x1 = x0 + (float)GD.RandRange(-60, 60);
                var pts = new Vector2[9];
                pts[0] = new Vector2(x0, -10);
                for (var i = 1; i < 9; i++)
                {
                    var t = i / 8f;
                    pts[i] = new Vector2(x0 + (x1 - x0) * t + (float)GD.RandRange(-26, 26), viewport.Y * 0.75f * t);
                }
                _bolt!.Points = pts;
                _bolt.Visible = true;
                OnLightning?.Invoke();
            }
        }

        if (_flash is null) return;
        if (_flashLeft > 0)
        {
            _flashLeft -= delta;
            var a = Mathf.Clamp((float)_flashLeft * 2, 0, 0.3f);
            _flash.Color = new Color(1, 1, 1, a);
            if (_bolt is not null) _bolt.Visible = true;
        }
        else
        {
            _flash.Color = new Color(1, 1, 1, 0);
            if (_bolt is not null) _bolt.Visible = false;
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
