using System;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 鱼人侧向骨骼：用 fishman/parts/side 的 8 个拆件按 rig 模板骨骼位置装配（内容中心对齐骨骼），
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

    // 装配数据：(骨骼局部坐标, 部件内容中心(512×384 板内), 绘制顺序)。
    // 骨骼坐标 = rig 模板换算（root 在原点、y 向上为负），侧视近/远肢体只错开少量 x。
    private static readonly (Vector2 Bone, Vector2 Content, int Order)[] Layout =
    {
        (new Vector2(0, -280), new Vector2(253, 247), 7),   // 0 头
        (new Vector2(0, -130), new Vector2(248, 230), 5),   // 1 躯干
        (new Vector2(-14, -235), new Vector2(285, 189), 3), // 2 左臂上（近）
        (new Vector2(-14, -145), new Vector2(221, 196), 4), // 3 左臂下
        (new Vector2(14, -235), new Vector2(258, 202), 0),  // 4 右臂上（远）
        (new Vector2(14, -145), new Vector2(272, 206), 1),  // 5 右臂下
        (new Vector2(-10, 0), new Vector2(287, 163), 2),    // 6 左腿（近）
        (new Vector2(10, 0), new Vector2(235, 183), 6),     // 7 右腿（远）
    };

    private const float PartScale = 0.15f;
    private static readonly Vector2 SheetCenter = new(256, 192);
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
            var (bone, content, _) = Layout[part];
            // 内容中心对齐骨骼：sprite 中心 = 骨骼 - (内容中心 - 板中心) × scale
            var offset = (content - SheetCenter) * PartScale;
            _parts[part] = new Sprite2D
            {
                Texture = _textures![part],
                Centered = true,
                Scale = Vector2.One * PartScale,
                Position = bone - offset,
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

        var phase = _elapsed / (moving ? 360f : 720f);
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
