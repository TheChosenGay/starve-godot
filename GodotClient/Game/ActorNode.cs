using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 代码装配的角色：fantasy-player 8 部位按关节数学摆放（移植自 web 端 skeletal-actor）。
/// part_01头 / 02躯干 / 03左臂 / 04右臂 / 05左腿 / 06右腿 / 07左手 / 08右手。
/// </summary>
public partial class ActorNode : Node2D
{
    private static readonly Dictionary<int, Texture2D> Textures = new();

    private readonly Node2D _pose = new();
    private readonly Dictionary<string, Node2D> _joints = new();
    private double _elapsed;
    private double _actionElapsed;
    private bool _moving;
    private string? _action;

    public static void Preload()
    {
        for (var i = 1; i <= 8; i++)
        {
            Textures[i - 1] = GD.Load<Texture2D>($"res://assets/rigs/fantasy-player/part_{i:00}.png");
        }
    }

    public ActorNode()
    {
        Scale = Vector2.One * 0.15f;
        AddChild(_pose);
        Build();
    }

    public void Play(string action)
    {
        _action = action;
        _actionElapsed = 0;
    }

    public void Update(double deltaMs, bool moving)
    {
        _elapsed += deltaMs;
        _actionElapsed += deltaMs;
        _moving = moving;

        var duration = _action switch
        {
            "attack" => 440,
            "gather" => 620,
            _ => 0,
        };
        if (_action is not null && duration > 0 && _actionElapsed >= duration)
        {
            _action = null;
            _actionElapsed = 0;
        }

        var phase = _elapsed / (_moving ? 135 : 620);
        var bob = Math.Sin(phase) * (_moving ? 5 : 1.5);
        var progress = _action is not null && duration > 0
            ? Math.Min(1, _actionElapsed / duration)
            : 0;

        _pose.Position = new Vector2(0, (float)bob);
        UpdatePose((float)phase, _moving, (float)progress);
    }

    private void Build()
    {
        // 躯干
        Part(1, 0, -10, 0.55f);
        // 头：绕颈关节（图像底部压住躯干顶）
        var head = Joint("head", 0, -104);
        Part(0, 0, 0, 0.5f, head, 0.5f, 1f);
        // 肩关节 + 手臂（肩端点对准）
        var leftArm = Joint("leftArm", -62, -75);
        PartAt(2, leftArm, 0, 0, 178, 4, 0.5f);
        var rightArm = Joint("rightArm", 62, -75);
        PartAt(3, rightArm, 0, 0, 41, 4, 0.5f);
        // 双手挂手臂末端
        var leftHand = Joint("leftHand", -69, 173.5f, leftArm);
        PartAt(6, leftHand, 0, 0, 79, 4, 0.4f);
        var rightHand = Joint("rightHand", 68, 173.5f, rightArm);
        PartAt(7, rightHand, 0, 0, 90, 4, 0.4f);
        // 双腿：髋部固定
        PartAt(4, _pose, -51, 62, 121, 4, 0.48f);
        PartAt(5, _pose, 50, 62, 62, 4, 0.48f);
    }

    private void UpdatePose(float phase, bool moving, float impact)
    {
        var stride = moving ? 0.38f : 0.05f;
        var leftArm = JointRef("leftArm");
        var rightArm = JointRef("rightArm");
        var leftHand = JointRef("leftHand");
        var rightHand = JointRef("rightHand");
        var head = JointRef("head");
        if (leftArm is null || rightArm is null || leftHand is null || rightHand is null || head is null) return;

        _pose.Rotation = 0;
        leftArm.Rotation = Mathf.Sin(phase) * stride;
        rightArm.Rotation = -Mathf.Sin(phase) * stride;
        leftHand.Rotation = leftArm.Rotation * 0.7f;
        rightHand.Rotation = rightArm.Rotation * 0.7f;
        head.Rotation = Mathf.Sin(phase * 0.5f) * 0.02f;

        var i = Mathf.Sin((float)Math.PI * impact);
        if (_action == "attack")
        {
            _pose.Rotation = -0.1f * i;
            rightArm.Rotation = -0.45f + i * 1.25f;
            rightHand.Rotation = rightArm.Rotation * 0.8f;
            leftArm.Rotation = 0.1f - i * 0.25f;
            head.Rotation = 0.05f * i;
        }
        else if (_action == "gather")
        {
            _pose.Rotation = 0.15f * i;
            leftArm.Rotation = 0.16f + i * 0.55f;
            rightArm.Rotation = -0.16f - i * 0.7f;
            leftHand.Rotation = leftArm.Rotation * 0.7f;
            rightHand.Rotation = rightArm.Rotation * 0.7f;
            head.Rotation = -0.08f * i;
        }
    }

    private Sprite2D Part(int idx, float x, float y, float scale, Node2D? parent = null, float anchorX = 0.5f, float anchorY = 0.5f)
    {
        var s = new Sprite2D
        {
            Texture = Textures[idx],
            Centered = true,
            Scale = Vector2.One * scale,
            Position = new Vector2(x, y),
        };
        (parent ?? _pose).AddChild(s);
        return s;
    }

    /// <summary>让部件图像像素 (ix,iy) 精确对准父空间 (px,py)（挂点不在图像中心的部件）。</summary>
    private Sprite2D PartAt(int idx, Node2D parent, float px, float py, float ix, float iy, float scale)
    {
        var s = new Sprite2D
        {
            Texture = Textures[idx],
            Centered = false,
            Scale = Vector2.One * scale,
            Position = new Vector2(px - ix * scale, py - iy * scale),
        };
        parent.AddChild(s);
        return s;
    }

    private Node2D Joint(string name, float x, float y, Node2D? parent = null)
    {
        var j = new Node2D { Position = new Vector2(x, y) };
        (parent ?? _pose).AddChild(j);
        _joints[name] = j;
        return j;
    }

    private Node2D? JointRef(string name) => _joints.TryGetValue(name, out var j) ? j : null;
}
