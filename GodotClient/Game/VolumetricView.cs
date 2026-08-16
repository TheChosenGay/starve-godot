using Godot;
using Camera = Starve.Core.Camera;

namespace GodotClient.Game;

/// <summary>
/// 体积光层（挂 LUT 之后，加法混合，不被乘法滤镜压暗/染色）：
/// 每个火堆的柔光晕（光模糊）+ 上收光柱（体积光），以及黄昏斜射天光。
/// 火焰本体在 FireView（世界空间），这里只画“光”。
/// </summary>
public partial class VolumetricView : Node2D
{
    private Camera? _camera;
    private Vector2[] _fires = System.Array.Empty<Vector2>();
    private long[] _seeds = System.Array.Empty<long>();
    private Vector2 _screen;
    private float _dayLight = 0.5f;
    private float _zoom = 1f;

    public override void _Ready()
    {
        Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
    }

    public void SetView(Camera camera, Vector2[] fires, long[] seeds, Vector2 screen, float dayLight, float zoom)
    {
        _camera = camera;
        _fires = fires;
        _seeds = seeds;
        _screen = screen;
        _dayLight = dayLight;
        _zoom = zoom;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var t = (float)Time.GetTicksMsec() / 1000f;
        if (_camera is null) return;

        // 火堆体积光：柔光晕 + 上收光柱，各自错相闪烁/摇摆
        for (var i = 0; i < _fires.Length && i < _seeds.Length; i++)
        {
            var s = _camera.WorldToScreen(_fires[i].X, _fires[i].Y, _screen.X, _screen.Y);
            var seedA = _seeds[i] % 7;
            var seedB = _seeds[i] % 13;
            var flicker =
                0.78f + Mathf.Sin(t * 11 + seedA) * 0.12f +
                Mathf.Sin(t * 23 + seedB) * 0.08f;

            // 柔光晕：屏幕半径 ≈ 2.8 格（1 格 = 20px × zoom）
            var glowR = 56f * _zoom * (0.92f + Mathf.Sin(t * 5 + seedB) * 0.06f);
            DrawTextureRect(
                FxTextures.Glow,
                new Rect2(s.X - glowR, s.Y - glowR, glowR * 2, glowR * 2),
                false,
                new Color(1f, 0.58f, 0.25f, 0.14f * flicker));

            // 光柱：底部宽 2 格、高约 5 格，轻微摇摆
            var shW = 40f * _zoom;
            var shH = 190f * _zoom;
            var sway = Mathf.Sin(t * 2.1f + seedA * 0.7f) * shW * 0.05f;
            DrawTextureRect(
                FxTextures.Shaft,
                new Rect2(s.X - shW / 2 + sway, s.Y - shH, shW, shH),
                false,
                new Color(1f, 0.62f, 0.28f, 0.085f * flicker));
        }

        // 黄昏天光：3 道斜射暖光楔（dark 处于黄昏区间时出现）
        var dark = Mathf.Max(0, 1 - _dayLight * 2);
        var dusk = Mathf.Clamp((0.5f - dark) / 0.38f, 0, 1);
        if (dusk <= 0.02f) return;

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
