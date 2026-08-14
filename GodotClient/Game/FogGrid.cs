using Godot;
using Starve.Game.V1;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>按格雾：天气帧每个 cell 的 fog 烘焙成一张世界锚定贴图（世界容器本地坐标）。</summary>
public partial class FogGrid : Node2D
{
    private readonly Sprite2D _sprite = new() { Centered = false, ZIndex = 3000 };

    public FogGrid() => AddChild(_sprite);

    public void SetFog(WeatherFrame? frame, TileMap tm)
    {
        var img = Image.CreateEmpty(tm.Width, tm.Height, false, Image.Format.Rgba8);
        var cs = frame?.CellSize ?? 10;
        var cpr = frame?.CellsPerRow ?? 0;
        var cells = frame?.Cells ?? new();
        for (var y = 0; y < tm.Height; y++)
        {
            for (var x = 0; x < tm.Width; x++)
            {
                var idx = (y / cs) * cpr + x / cs;
                var fog = idx >= 0 && idx < cells.Count ? cells[idx].Fog : 0f;
                var a = Mathf.Clamp(fog * 0.45f, 0, 0.4f);
                img.SetPixel(x, y, new Color(0.62f, 0.7f, 0.78f, a));
            }
        }
        _sprite.Texture = ImageTexture.CreateFromImage(img);
        _sprite.Scale = Vector2.One * 20; // 1 格 = 20 世界单位
    }
}
