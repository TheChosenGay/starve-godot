using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 爆炸表现层：把服务端下发的 <c>BlastEvent</c>（位置/半径）画成一次扩散冲击。
///
/// 职责划分（与设计一致）：服务端只确定"在哪炸、多大、谁干的"，
/// **表现全部由客户端负责**——扩散圈、闪光、渐隐都在这里。
///
/// 用两个同心的扁圆环（内圈快、外圈慢）模拟冲击波，比单个圆更有"炸开"的感觉；
/// 环用 <see cref="ImmediateMesh"/> 每帧重建（爆炸是短时事件，数量很小）。
/// </summary>
public partial class BlastFxLayer3D : Node3D
{
    /// <summary>一次爆炸表现的存活时长（秒）。</summary>
    private const float LifeSeconds = 0.55f;

    /// <summary>同时存在的爆炸上限（防止极端情况下无限堆积）。</summary>
    private const int MaxConcurrent = 24;

    private readonly List<Blast> _blasts = new();

    private sealed class Blast
    {
        public float X;
        public float Y;
        public float GroundHeight;
        public float Radius;
        public float Age;
        // 环用 MeshInstance3D 而不是每帧重建 ImmediateMesh：
        // 圆环只要缩放即可，省去每帧重算顶点。
        public MeshInstance3D Outer = null!;
        public MeshInstance3D Inner = null!;
    }

    private StandardMaterial3D? _outerMat;
    private StandardMaterial3D? _innerMat;
    private TorusMesh? _ringMesh;
    private bool _ready;

    public override void _Ready() => BuildResources();

    private void BuildResources()
    {
        if (_ready) return;
        _ready = true;
        // 圆环基础半径 0.5 世界单位，缩放即得实际半径。
        _ringMesh = new TorusMesh { InnerRadius = 0.40f, OuterRadius = 0.5f, RingSegments = 40 };
        _outerMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.55f, 0.24f, 0.9f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Disabled,
        };
        _innerMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.92f, 0.62f, 0.95f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
    }

    /// <summary>
    /// 触发一次爆炸表现。
    /// </summary>
    /// <param name="x">爆心世界 X（格）</param>
    /// <param name="y">爆心世界 Y（格）</param>
    /// <param name="radius">爆炸半径（格）</param>
    /// <param name="groundHeight">地面高度（让环贴地）</param>
    public void Spawn(float x, float y, float radius, float groundHeight)
    {
        BuildResources();
        if (_blasts.Count >= MaxConcurrent)
        {
            // 超上限就先清掉最老的一个，避免无限增长
            var oldest = _blasts[0];
            oldest.Outer.QueueFree();
            oldest.Inner.QueueFree();
            _blasts.RemoveAt(0);
        }

        var outer = new MeshInstance3D { Mesh = _ringMesh, MaterialOverride = _outerMat };
        var inner = new MeshInstance3D { Mesh = _ringMesh, MaterialOverride = _innerMat };
        AddChild(outer);
        AddChild(inner);

        var world = Starve.Core.IsoCamera3D.WorldTo3D(x, y, groundHeight);
        var origin = new Vector3(world.X, world.Y + 0.03f, world.Z);
        outer.Position = origin;
        inner.Position = origin;

        _blasts.Add(new Blast
        {
            X = x, Y = y, GroundHeight = groundHeight,
            Radius = MathF.Max(0.5f, radius), Age = 0,
            Outer = outer, Inner = inner,
        });
    }

    public override void _Process(double delta)
    {
        if (_blasts.Count == 0) return;
        var dt = (float)delta;

        for (var i = _blasts.Count - 1; i >= 0; i--)
        {
            var b = _blasts[i];
            b.Age += dt;
            var t = b.Age / LifeSeconds;
            if (t >= 1)
            {
                b.Outer.QueueFree();
                b.Inner.QueueFree();
                _blasts.RemoveAt(i);
                continue;
            }

            float unit = Starve.Core.IsoCamera3D.WorldUnit;
            // 外圈：快速扩到 1.25×半径，颜色渐隐
            var outerScale = b.Radius * unit / 0.5f * (0.35f + 0.9f * EaseOut(t));
            b.Outer.Scale = new Vector3(outerScale, 1f, outerScale);
            // 内圈：稍慢、更亮，营造"核心"
            var innerScale = b.Radius * unit / 0.5f * (0.2f + 0.55f * EaseOut(t));
            b.Inner.Scale = new Vector3(innerScale, 1f, innerScale);

            var fade = 1f - t;
            if (_outerMat is not null)
                _outerMat.AlbedoColor = new Color(1.0f, 0.55f, 0.24f, 0.9f * fade);
            if (_innerMat is not null)
                _innerMat.AlbedoColor = new Color(1.0f, 0.92f, 0.62f, 0.95f * fade);
        }
    }

    /// <summary>缓出：开始快、后面慢，更像冲击波。</summary>
    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);
}
