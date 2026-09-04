using System;
using Godot;
using Starve.Core;
using TileMap = Starve.Core.TileMap;

namespace GodotClient.Game;

/// <summary>3D 地形：把 Core.TileMap 的高度场分块烘焙成 MeshInstance3D。</summary>
public partial class MapView3D : Node3D
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
        var mat = ToonMaterials.CreateTerrain(_atlas.Atlas);
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
                    Mesh = MapMeshBuilder.BuildChunk3D(tm, x0, y0, x1, y1, _atlas),
                    MaterialOverride = mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }
        }
    }
}
