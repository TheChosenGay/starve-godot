using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>建造幽灵预览：半透明占格（绿=可放，红=不可放），世界容器本地坐标。</summary>
public partial class GhostNode : Node2D
{
    private int _w = 1;
    private int _h = 1;
    private bool _ok = true;

    public void Configure(int w, int h)
    {
        _w = w;
        _h = h;
        QueueRedraw();
    }

    public void SetOk(bool ok)
    {
        _ok = ok;
        QueueRedraw();
    }

    public void SetLocal(Vector2 local)
    {
        Position = local;
        ZIndex = Mathf.Clamp((int)local.Y, -4096, 4096);
    }

    public override void _Draw()
    {
        var fill = _ok ? new Color(0.28f, 0.88f, 0.42f, 0.5f) : new Color(0.89f, 0.34f, 0.30f, 0.5f);
        var border = new Color(1f, 1f, 1f, 0.85f);
        for (var oy = 0; oy < _h; oy++)
        {
            for (var ox = 0; ox < _w; ox++)
            {
                var c = IsoMath.WorldToLocal(ox, oy);
                var pts = new[]
                {
                    new Vector2(c.X, c.Y - 10),
                    new Vector2(c.X + 10, c.Y),
                    new Vector2(c.X, c.Y + 10),
                    new Vector2(c.X - 10, c.Y),
                };
                DrawColoredPolygon(pts, fill);
                DrawPolyline(new[]
                {
                    pts[0], pts[1], pts[2], pts[3], pts[0],
                }, border, 1.5f);
            }
        }
    }
}
