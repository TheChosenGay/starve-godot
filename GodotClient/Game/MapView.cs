using System;
using Godot;
using Starve.Core;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>地形视图：把 Core.TileMap 分块烘焙成静态 Mesh，地图变更时重建。</summary>
public partial class MapView : Node2D
{
    private static TileAtlasBuilder? _atlas;

    public void SetMap(TileMap tm)
    {
        _atlas ??= TileAtlasBuilder.Build();
        foreach (var child in GetChildren())
        {
            child.QueueFree();
        }

        var cols = Mathf.CeilToInt(tm.Width / (float)MapMeshBuilder.ChunkTiles);
        var rows = Mathf.CeilToInt(tm.Height / (float)MapMeshBuilder.ChunkTiles);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var x0 = Math.Max(0, c * MapMeshBuilder.ChunkTiles - 1);
                var y0 = Math.Max(0, r * MapMeshBuilder.ChunkTiles - 1);
                var x1 = Math.Min((c + 1) * MapMeshBuilder.ChunkTiles + 1, tm.Width);
                var y1 = Math.Min((r + 1) * MapMeshBuilder.ChunkTiles + 1, tm.Height);
                var mi = new MeshInstance2D
                {
                    Mesh = MapMeshBuilder.BuildChunk(tm, x0, y0, x1, y1, _atlas),
                    Material = MakeAtlasMaterial(_atlas.Atlas),
                };
                AddChild(mi);
            }
        }
    }

    /// <summary>canvas_item shader：采样图集 × 顶点色（顶点色烘焙高度/坡度/AO）。</summary>
    private static ShaderMaterial MakeAtlasMaterial(Texture2D atlas)
    {
        var shader = new Shader
        {
            Code = "shader_type canvas_item;\n" +
                   "uniform sampler2D uAtlas;\n" +
                   "void fragment() { COLOR = texture(uAtlas, UV) * COLOR; }",
        };
        var mat = new ShaderMaterial();
        mat.Shader = shader;
        mat.SetShaderParameter("uAtlas", atlas);
        return mat;
    }
}
