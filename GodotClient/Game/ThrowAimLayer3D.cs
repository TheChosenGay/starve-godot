using System;
using System.Collections.Generic;
using Godot;
using Starve.Core;

// 只用 System.Numerics 的 Vector2（格坐标）；3D 向量一律用 Godot.Vector3。
// 两者同名，不 using System.Numerics 可以避免到处写全限定名。
using Vector2 = System.Numerics.Vector2;

namespace GodotClient.Game;

/// <summary>
/// 投掷瞄准的可视化层（客户端本地，不依赖服务端下发）。
///
/// 画三样东西：
///   1. **抛物线预览**——由 <see cref="ThrowPhysics"/> 用与服务端同一套公式算出，
///      沿弧线摆一串小球（离散点比 Line3D 简单，也不需要材质资源）；
///   2. **落点标记**——命中点画一个圈，颜色表示"能否投掷"（绿=可，红=不可）；
///   3. **可达范围**——以玩家为圆心的虚线圆，半径 = 投掷者力量算出的最大距离。
///
/// 为什么点用离散球体而不是一条线：Godot 的 ImmediateMesh 需要自己管材质与
/// 顶点，而这里点数量很小（约 24 个），用共享的小球 Mesh 实例最省事，
/// 且天然有体积感、在 3D 里比细线更易看清。
///
/// 这一层**只做表现**：能不能扔由服务端决定（本地算的只是预览）。
/// 两边用同一公式（见 ThrowPhysics 注释），所以预览与实际结果一致。
/// </summary>
public partial class ThrowAimLayer3D : Node3D
{
    /// <summary>弧线上的采样点数（越多越平滑；24 在 20 tick 飞行下已足够）。</summary>
    private const int ArcSamples = 24;

    private readonly List<MeshInstance3D> _arcDots = new();
    private MeshInstance3D? _landingMark;
    private MeshInstance3D? _rangeRing;
    private StandardMaterial3D? _okMat;
    private StandardMaterial3D? _badMat;
    private StandardMaterial3D? _rangeMat;
    private SphereMesh? _dotMesh;
    private TorusMesh? _ringMesh;

    private bool _built;

    /// <summary>
    /// Starve.Core 的 IsoCamera3D 返回 System.Numerics.Vector3（核心库刻意不依赖 Godot），
    /// 而节点 Position 需要 Godot.Vector3 —— 逐个分量转换（与 EntityLayer3D 同一做法）。
    /// </summary>
    private static Godot.Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    public override void _Ready() => BuildResources();

    private void BuildResources()
    {
        if (_built) return;
        _built = true;

        _dotMesh = new SphereMesh { Radius = 0.09f, Height = 0.18f, RadialSegments = 8, Rings = 4 };
        _okMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.42f, 0.85f, 0.52f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        _badMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.95f, 0.35f, 0.30f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        _rangeMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.43f, 0.66f, 1.0f, 0.45f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        _ringMesh = new TorusMesh { InnerRadius = 0.42f, OuterRadius = 0.5f, RingSegments = 24 };
    }

    /// <summary>
    /// 显示一次瞄准预览。
    /// </summary>
    /// <param name="from">起点（玩家手部，格坐标）</param>
    /// <param name="to">目标落点（格坐标）</param>
    /// <param name="maxDistance">最大投掷距离（格），用于画可达范围</param>
    /// <param name="canThrow">服务端/本地校验是否允许（决定配色）</param>
    /// <param name="heightAt">地形高度查询（让弧线贴合地形）</param>
    public void Show(
        Vector2 from,
        Vector2 to,
        int maxDistance,
        bool canThrow,
        Func<float, float, float>? heightAt)
    {
        BuildResources();
        Visible = true;

        var mat = canThrow ? _okMat : _badMat;
        var traj = ThrowPhysics.Solve(from, to);

        // 抛物线采样点：水平匀速 + 高度 h(t) = 4·peak·t(1-t)
        EnsureDots(ArcSamples);
        for (var i = 0; i < ArcSamples; i++)
        {
            var t = (i + 1) / (float)(ArcSamples + 1);
            var x = from.X + (to.X - from.X) * t;
            var y = from.Y + (to.Y - from.Y) * t;
            var ground = heightAt?.Invoke(x, y) ?? 0f;
            var world = IsoCamera3D.WorldTo3D(x, y, ground);
            // 高度加到世界 Y 上（游戏世界 Y 向上）
            world.Y += (float)ThrowPhysics.HeightAt(t, traj.PeakHeight) * IsoCamera3D.WorldUnit;
            _arcDots[i].Position = ToGodot(world);
            _arcDots[i].MaterialOverride = mat;
            _arcDots[i].Visible = true;
        }

        // 落点标记
        if (_landingMark is null)
        {
            _landingMark = new MeshInstance3D { Mesh = _ringMesh };
            AddChild(_landingMark);
        }
        var landingGround = heightAt?.Invoke(to.X, to.Y) ?? 0f;
        var landPos = IsoCamera3D.WorldTo3D(to.X, to.Y, landingGround);
        landPos.Y += 0.02f; // 稍微抬高，避免与地面 z-fighting
        _landingMark.Position = ToGodot(landPos);
        _landingMark.MaterialOverride = mat;
        _landingMark.Scale = new Godot.Vector3(1.4f, 1f, 0.5f); // 压扁成地面圈
        _landingMark.Visible = true;

        // 可达范围：以玩家为圆心的圆环
        if (maxDistance > 0)
        {
            if (_rangeRing is null)
            {
                _rangeRing = new MeshInstance3D { Mesh = _ringMesh };
                AddChild(_rangeRing);
            }
            var selfGround = heightAt?.Invoke(from.X, from.Y) ?? 0f;
            var center = IsoCamera3D.WorldTo3D(from.X, from.Y, selfGround);
            center.Y += 0.02f;
            _rangeRing.Position = ToGodot(center);
            _rangeRing.MaterialOverride = _rangeMat;
            // 圆环基础半径 0.5 世界单位 → 缩放到 maxDistance
            var s = maxDistance * IsoCamera3D.WorldUnit / 0.5f;
            _rangeRing.Scale = new Godot.Vector3(s, 1f, s);
            _rangeRing.Visible = true;
        }
        else if (_rangeRing is not null)
        {
            _rangeRing.Visible = false;
        }
    }

    /// <summary>隐藏预览（退出瞄准模式）。</summary>
    public void Hide()
    {
        Visible = false;
        foreach (var d in _arcDots) d.Visible = false;
        if (_landingMark is not null) _landingMark.Visible = false;
        if (_rangeRing is not null) _rangeRing.Visible = false;
    }

    private void EnsureDots(int count)
    {
        while (_arcDots.Count < count)
        {
            var dot = new MeshInstance3D { Mesh = _dotMesh };
            AddChild(dot);
            _arcDots.Add(dot);
        }
        for (var i = count; i < _arcDots.Count; i++) _arcDots[i].Visible = false;
    }
}
