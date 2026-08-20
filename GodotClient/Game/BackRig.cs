using Godot;
using System.Linq;

namespace GodotClient.Game;

/// <summary>鱼人背面分件骨骼：头、躯干、双臂、双腿按稳定锚点装配并程序化行走。</summary>
public partial class BackRig : Node2D
{
    private static readonly string[] PartPaths =
    {
        "res://assets/fishman/parts/back/cutout/part_0.png",
        "res://assets/fishman/parts/back/cutout/part_1.png",
        "res://assets/fishman/parts/back/cutout/part_2.png",
        "res://assets/fishman/parts/back/cutout/part_3.png",
        "res://assets/fishman/parts/back/cutout/part_4.png",
        "res://assets/fishman/parts/back/cutout/part_5.png",
        "res://assets/fishman/parts/back/cutout/part_6.png",
        "res://assets/fishman/parts/back/cutout/part_7.png",
    };

    private static readonly Vector2[] Anchors =
    {
        new(0, -47), new(0, -29),
        new(-17, -31), new(17, -31),
        new(-24, -21), new(24, -21),
        new(-7, -8), new(7, -8),
    };

    private const float PartScale = 0.09f;
    private static Texture2D[]? _textures;
    private readonly Sprite2D[] _parts = new Sprite2D[8];
    private float _elapsed;

    public BackRig()
    {
        _textures ??= PartPaths.Select(GD.Load<Texture2D>).ToArray();
        var order = new[] { 3, 5, 7, 1, 6, 2, 4, 0 };
        foreach (var part in order)
        {
            _parts[part] = new Sprite2D
            {
                Texture = _textures[part],
                Scale = Vector2.One * PartScale,
                Position = Anchors[part],
            };
            AddChild(_parts[part]);
        }
    }

    public void Update(double deltaMs, bool moving)
    {
        _elapsed += (float)deltaMs;
        var phase = _elapsed / (moving ? 640f : 1200f) * Mathf.Tau;
        var swing = moving ? 0.20f : 0.025f;
        _parts[2].Rotation = Mathf.Sin(phase) * swing;
        _parts[3].Rotation = -Mathf.Sin(phase) * swing;
        _parts[4].Rotation = Mathf.Sin(phase) * swing * 0.8f;
        _parts[5].Rotation = -Mathf.Sin(phase) * swing * 0.8f;
        _parts[6].Rotation = -Mathf.Sin(phase) * swing * 0.55f;
        _parts[7].Rotation = Mathf.Sin(phase) * swing * 0.55f;
        Position = new Vector2(0, moving ? Mathf.Sin(phase * 2) * 1.2f : 0);
    }
}
