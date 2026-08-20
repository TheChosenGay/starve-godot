using System;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 鱼人侧向骨骼：用 fishman/parts/side 的 8 个紧边拆件按关节锚点装配，
/// 程序化摆臂/摆腿模拟侧向行走。横向移动时由 RigNode 切到这个视图，其余时间用正面烘焙帧。
/// </summary>
public partial class SideRig : Node2D
{
    // 部件索引（与 rig 模板 slots 一致）：0头 1躯干 2左臂上 3左臂下 4右臂上 5右臂下 6左腿 7右腿
    private static readonly string[] PartPaths =
    {
        "res://assets/fishman/parts/side/cutout/part_0.png",
        "res://assets/fishman/parts/side/cutout/part_1.png",
        "res://assets/fishman/parts/side/cutout/part_2.png",
        "res://assets/fishman/parts/side/cutout/part_3.png",
        "res://assets/fishman/parts/side/cutout/part_4.png",
        "res://assets/fishman/parts/side/cutout/part_5.png",
        "res://assets/fishman/parts/side/cutout/part_6.png",
        "res://assets/fishman/parts/side/cutout/part_7.png",
    };

    private static readonly (Vector2 Anchor, int Order)[] Layout =
    {
        (new Vector2(2, -47), 7),  // 0 头
        (new Vector2(0, -28), 5),  // 1 躯干
        (new Vector2(2, -31), 3),  // 2 近侧上臂
        (new Vector2(12, -22), 4), // 3 近侧前臂
        (new Vector2(-2, -30), 0), // 4 远侧上臂
        (new Vector2(-11, -21), 1),// 5 远侧前臂
        (new Vector2(-5, -8), 2),  // 6 近侧腿
        (new Vector2(5, -8), 6),   // 7 远侧腿
    };

    private const float PartScale = 0.09f;
    private static Texture2D[]? _textures;

    private readonly Sprite2D[] _parts = new Sprite2D[8];
    private float _elapsed;
    private float _facing = 1f;
    private string? _action;
    private double _actionElapsed;

    public static void Preload()
    {
        if (_textures is not null) return;
        _textures = new Texture2D[8];
        for (var i = 0; i < 8; i++) _textures[i] = GD.Load<Texture2D>(PartPaths[i]);
    }

    public SideRig()
    {
        Preload();
        var order = new (int Part, int Order)[8];
        for (var i = 0; i < 8; i++) order[i] = (i, Layout[i].Order);
        Array.Sort(order, (a, b) => a.Order.CompareTo(b.Order));
        foreach (var (part, _) in order)
        {
            _parts[part] = new Sprite2D
            {
                Texture = _textures![part],
                Centered = true,
                Scale = Vector2.One * PartScale,
                Position = Layout[part].Anchor,
            };
            AddChild(_parts[part]);
        }
    }

    public void SetFacing(float dir)
    {
        if (dir == 0 || dir == _facing) return;
        _facing = dir;
        Scale = new Vector2(Mathf.Abs(Scale.X) * dir, Scale.Y);
    }

    public void Play(string action)
    {
        if (action == "attack")
        {
            _action = "attack";
            _actionElapsed = 0;
        }
    }

    public void Update(double deltaMs, bool moving)
    {
        _elapsed += (float)deltaMs;
        _actionElapsed += deltaMs;
        var duration = _action == "attack" ? 400 : 0;
        if (_action is not null && duration > 0 && _actionElapsed >= duration)
        {
            _action = null;
            _actionElapsed = 0;
        }

        var phase = _elapsed / (moving ? 640f : 1200f);
        var swing = moving ? 0.34f : 0.04f;
        var bob = Mathf.Sin(phase * Mathf.Tau) * (moving ? 2.5f : 0.6f);

        // 近侧肢体摆幅略大：近臂/近腿 +swing，远侧 -swing（对侧相位）
        Rotate(2, Mathf.Sin(phase * Mathf.Tau) * swing);
        Rotate(3, Mathf.Sin(phase * Mathf.Tau) * swing * 0.8f);
        Rotate(4, -Mathf.Sin(phase * Mathf.Tau) * swing * 0.8f);
        Rotate(5, -Mathf.Sin(phase * Mathf.Tau) * swing * 0.65f);
        Rotate(6, -Mathf.Sin(phase * Mathf.Tau) * swing);
        Rotate(7, Mathf.Sin(phase * Mathf.Tau) * swing * 0.8f);

        var impact = Mathf.Sin((float)Math.PI * (float)Mathf.Clamp(_actionElapsed / 400f, 0, 1));
        if (_action == "attack")
        {
            Rotate(2, 0.15f - impact * 1.1f); // 近臂前挥
            Rotate(3, impact * 0.6f);
            Position = new Vector2(0, bob - 4f * impact);
        }
        else
        {
            Position = new Vector2(0, bob);
        }
    }

    private void Rotate(int part, float rad)
    {
        if (_parts[part] is { } s) s.Rotation = rad;
    }
}
