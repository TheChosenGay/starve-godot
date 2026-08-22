using Godot;
using Camera = Starve.Core.Camera;

namespace GodotClient.Game;

/// <summary>
/// 体积光层（挂 LUT 之后，加法混合，不被乘法滤镜压暗/染色）：
/// 每个火堆是空间范围内的暖光团（多层径向光晕），以及黄昏斜射天光。
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

        // 火堆体积光：多层径向光晕，铺开成一块空间里的暖光，而不是竖着的锥光柱。
        for (var i = 0; i < _fires.Length && i < _seeds.Length; i++)
        {
            var s = _camera.WorldToScreen(_fires[i].X, _fires[i].Y, _screen.X, _screen.Y);
            var seedA = _seeds[i] % 7;
            var seedB = _seeds[i] % 13;
            var flicker =
                0.78f + Mathf.Sin(t * 11 + seedA) * 0.12f +
                Mathf.Sin(t * 23 + seedB) * 0.08f;
            var pulse = 0.94f + Mathf.Sin(t * 5 + seedB) * 0.05f;

            var center = new Vector2(s.X, s.Y);
            DrawFireVolume(center, 22f * _zoom * pulse, new Color(1f, 0.72f, 0.38f, 0.34f * flicker));
            DrawFireVolume(center, 42f * _zoom * pulse, new Color(1f, 0.55f, 0.22f, 0.18f * flicker));
            DrawFireVolume(center, 68f * _zoom * pulse, new Color(1f, 0.42f, 0.14f, 0.08f * flicker));
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

    /// <summary>等距空间里的光团：横向略宽，像铺在地面上的一摊光，不是竖锥。</summary>
    private void DrawFireVolume(Vector2 center, float radius, Color color)
    {
        var w = radius * 2.15f;
        var h = radius * 1.35f;
        DrawTextureRect(
            FxTextures.Glow,
            new Rect2(center.X - w * 0.5f, center.Y - h * 0.55f, w, h),
            false,
            color);
    }
}
