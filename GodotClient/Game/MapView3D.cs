using System;
using Godot;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>3D 地形：高度场分块网格 + 高度混合 splat 材质。</summary>
public partial class MapView3D : Node3D
{
    public ShaderMaterial? TerrainMat { get; private set; }

    public void SetMap(TileMap tm)
    {
        foreach (var child in GetChildren())
            child.QueueFree();

        var cols = Mathf.CeilToInt(tm.Width / (float)MapMeshBuilder.ChunkTiles);
        var rows = Mathf.CeilToInt(tm.Height / (float)MapMeshBuilder.ChunkTiles);
        var mat = ToonMaterials.CreateTerrain();
        TerrainMat = mat;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var x0 = Math.Max(0, c * MapMeshBuilder.ChunkTiles - 1);
                var y0 = Math.Max(0, r * MapMeshBuilder.ChunkTiles - 1);
                var x1 = Math.Min((c + 1) * MapMeshBuilder.ChunkTiles + 1, tm.Width);
                var y1 = Math.Min((r + 1) * MapMeshBuilder.ChunkTiles + 1, tm.Height);
                AddChild(new MeshInstance3D
                {
                    Name = $"Chunk_{c}_{r}",
                    Mesh = MapMeshBuilder.BuildChunk3D(tm, x0, y0, x1, y1),
                    MaterialOverride = mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }
        }
    }
}
