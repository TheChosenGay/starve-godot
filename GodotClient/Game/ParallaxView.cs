using Godot;

namespace GodotClient.Game;

/// <summary>视差远景：两层程序山脊，按相机偏移的 18%/42% 移动（屏幕空间）。</summary>
public partial class ParallaxView : Node2D
{
    private Node2D? _far;
    private Node2D? _near;

    public override void _Ready()
    {
        _far = new Node2D();
        _far.AddChild(MakeMountains(new Color(0.09f, 0.12f, 0.18f), 7));
        AddChild(_far);
        _near = new Node2D();
        _near.AddChild(MakeMountains(new Color(0.12f, 0.17f, 0.25f), 23));
        AddChild(_near);
    }

    public void UpdateParallax(float fx, float fy, Vector2 viewport)
    {
        if (_far is not null)
            _far.Position = new Vector2(viewport.X / 2 - fx * 0.18f, viewport.Y / 2 - fy * 0.18f);
        if (_near is not null)
            _near.Position = new Vector2(viewport.X / 2 - fx * 0.42f, viewport.Y / 2 - fy * 0.42f);
    }

    private static Polygon2D MakeMountains(Color color, float seed)
    {
        var pts = new Vector2[42];
        for (var i = 0; i < 40; i++)
        {
            var x = i * 60 - 1200;
            var n = Mathf.Sin(x * 0.0013f + seed) * 0.5f +
                    Mathf.Sin(x * 0.0037f + seed * 2) * 0.3f +
                    Mathf.Sin(x * 0.0007f + seed * 3) * 0.2f;
            pts[i] = new Vector2(x, 260 - n * 150);
        }
        pts[40] = new Vector2(1200, 900);
        pts[41] = new Vector2(-1200, 900);
        return new Polygon2D { Polygon = pts, Color = color };
    }
}
