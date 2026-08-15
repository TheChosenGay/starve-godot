using System;
using Godot;
using Camera = Starve.Core.Camera;

namespace GodotClient.Game;

/// <summary>
/// 体积光（滤镜外加法层，对齐 web）：火堆上方收窄的暖光锥 + 黄昏斜射天光。
/// 挂光照/LUT 之后，不被乘法滤镜压暗。
/// </summary>
public partial class VolumetricView : Node2D
{
    private Camera? _camera;
    private Vector2[] _fires = Array.Empty<Vector2>();
    private long[] _seeds = Array.Empty<long>();
    private Vector2 _screen;
    private float _dayLight = 0.5f;
    private float _scale = 40f;

    public override void _Ready()
    {
        Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
    }

    public void SetView(Camera camera, Vector2[] fires, long[] seeds, Vector2 screen, float dayLight, float scale)
    {
        _camera = camera;
        _fires = fires;
        _seeds = seeds;
        _screen = screen;
        _dayLight = dayLight;
        _scale = scale;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var t = (float)Time.GetTicksMsec() / 1000f;
        if (_camera is null) return;

        // 火堆光锥：三层收窄椭圆叠加（web 同款公式）
        for (var i = 0; i < _fires.Length && i < _seeds.Length; i++)
        {
            var s = _camera.WorldToScreen(_fires[i].X, _fires[i].Y, _screen.X, _screen.Y);
            var flicker =
                0.78f + Mathf.Sin(t * 11 + _seeds[i] % 7) * 0.12f +
                Mathf.Sin(t * 23 + _seeds[i] % 13) * 0.08f;
            var r = 5.5f * _scale * (0.92f + Mathf.Sin(t * 5) * 0.06f);
            for (var k = 0; k < 3; k++)
            {
                var tt = k / 2f;
                DrawEllipsePoly(
                    new Vector2(s.X, s.Y - r * 0.5f * tt),
                    r * (0.32f - tt * 0.12f),
                    r * 0.09f,
                    new Color(1f, 0.63f, 0.29f, Mathf.Max(0, 0.08f * flicker * (1 - tt * 0.4f))));
            }
        }

        // 黄昏天光：3 道斜射暖光楔（dark 处于黄昏区间时出现）
        var dark = Mathf.Max(0, 1 - _dayLight * 2);
        var dusk = Mathf.Clamp((0.5f - dark) / 0.38f, 0, 1);
        if (dusk > 0.02f)
        {
            for (var i = 0; i < 3; i++)
            {
                var baseX = _screen.X * (0.2f + i * 0.24f) + Mathf.Sin(t * 0.05f + i * 2.1f) * 26;
                var ang = 0.62f + i * 0.32f + Mathf.Sin(t * 0.04f + i) * 0.07f;
                var len = _screen.Y * 1.15f;
                var dx = Mathf.Sin(ang) * len;
                var dy = Mathf.Cos(ang) * len;
                DrawColoredPolygon(
                    new[]
                    {
                        new Vector2(baseX, -20),
                        new Vector2(baseX + 30, -20),
                        new Vector2(baseX + 30 + dx * 0.18f, dy),
                        new Vector2(baseX + dx * 0.18f, dy),
                    },
                    new Color(1f, 0.85f, 0.63f, dusk * 0.09f));
            }
        }
    }

    private void DrawEllipsePoly(Vector2 center, float rx, float ry, Color color)
    {
        var pts = new Vector2[20];
        for (var i = 0; i < 20; i++)
        {
            var a = Mathf.Tau * i / 20;
            pts[i] = center + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
        }
        DrawColoredPolygon(pts, color);
    }
}
