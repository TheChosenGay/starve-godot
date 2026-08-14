using System;
using Godot;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>小地图（阶段 2 简化版）：从地形烘焙一张俯视纹理，右上角显示。</summary>
public partial class MinimapView : Control
{
    private readonly TextureRect _tex = new()
    {
        Size = new Vector2(150, 150),
    };

    public override void _Ready()
    {
        _tex.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _tex.Position = new Vector2(-170, 20);
        AddChild(_tex);
    }

    public void SetMap(TileMap tm)
    {
        var img = Image.CreateEmpty(tm.Width, tm.Height, false, Image.Format.Rgba8);
        var colors = new[]
        {
            new Color(0.4f, 0.4f, 0.4f),
            new Color(0.17f, 0.42f, 0.69f),
            new Color(0.85f, 0.7f, 0.55f),
            new Color(0.35f, 0.56f, 0.31f),
            new Color(0.54f, 0.56f, 0.6f),
            new Color(0.85f, 0.9f, 0.93f),
        };
        for (var cy = 0; cy < tm.Height; cy++)
        {
            for (var cx = 0; cx < tm.Width; cx++)
            {
                var type = tm.CornerType(cx, cy);
                var color = colors[Math.Clamp(type, 0, colors.Length - 1)];
                img.SetPixel(cx, cy, color);
            }
        }
        _tex.Texture = ImageTexture.CreateFromImage(img);
    }
}
