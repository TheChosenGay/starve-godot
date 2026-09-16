using System.Numerics;
using Starve.Core;

namespace Starve.Core.Tests;

/// <summary>
/// 投掷瞄准的**决策逻辑**契约（不含 Godot 渲染）。
///
/// UI 层容易出现"看着能扔、实际被拒"或反之的不一致；这里把
/// "能不能扔 / 为什么不能"的判断抽成语义化断言，与服务端校验口径对齐。
/// </summary>
public sealed class ThrowAimPolicyTests
{
    /// <summary>玩家力量 20、炸弹质量 18 → 最大距离 8 格（与服务端实测一致）。</summary>
    private const int Strength = 20;
    private const int BombMass = 18;

    private static int MaxDist => ThrowPhysics.MaxThrowDistance(Strength, BombMass);

    private static bool CanThrow(Vector2 from, Vector2 to) =>
        MaxDist > 0 && Vector2.Distance(from, to) <= MaxDist;

    [Fact]
    public void MaxDistanceMatchesServerForBomb()
    {
        Assert.Equal(8, MaxDist);
    }

    /// <summary>范围内可投、范围外不可投（边界按欧氏距离）。</summary>
    [Fact]
    public void RangeGateUsesEuclideanDistance()
    {
        var from = new Vector2(64, 64);
        Assert.True(CanThrow(from, new Vector2(64 + 8, 64)));      // 正好 8 格
        Assert.False(CanThrow(from, new Vector2(64 + 9, 64)));     // 9 格 → 超
        // 对角：(5,5) 欧氏 ≈ 7.07 ≤ 8 可投；若误用切比雪夫会算成 5 也可投，
        // 但 (6,6) 欧氏 ≈ 8.49 > 8 不可投，而切比雪夫只有 6 会误判为可投。
        Assert.True(CanThrow(from, new Vector2(64 + 5, 64 + 5)));
        Assert.False(CanThrow(from, new Vector2(64 + 6, 64 + 6)));
    }

    /// <summary>更重的物品可达距离更短（玩家会明显感觉到）。</summary>
    [Fact]
    public void HeavierItemShortensRange()
    {
        var light = ThrowPhysics.MaxThrowDistance(Strength, 4);   // 石头
        var heavy = ThrowPhysics.MaxThrowDistance(Strength, 40);  // 铁块
        Assert.True(light > heavy);
        Assert.Equal(40, light);
        Assert.Equal(4, heavy);
    }

    /// <summary>力量为 0（没有投掷能力）时任何落点都不可投。</summary>
    [Fact]
    public void ZeroStrengthBlocksEverything()
    {
        var max = ThrowPhysics.MaxThrowDistance(0, BombMass);
        Assert.Equal(0, max);
        Assert.False(max > 0 && Vector2.Distance(new Vector2(0, 0), new Vector2(0, 0)) <= max);
    }

    /// <summary>预览抛物线端点必须精确落在瞄准点（与服务端一致）。</summary>
    [Fact]
    public void PreviewArcEndsAtTarget()
    {
        var from = new Vector2(64, 64);
        var to = new Vector2(70, 66);
        var traj = ThrowPhysics.Solve(from, to);
        var (pos, height) = traj.SampleAt(traj.FlightTicks);
        Assert.Equal(to.X, pos.X, 4);
        Assert.Equal(to.Y, pos.Y, 4);
        Assert.Equal(0.0, height, 6);
    }

    /// <summary>预览飞行时长应与服务端算出的一致（距离 4 → 20 tick）。</summary>
    [Fact]
    public void PreviewFlightTicksMatchServer()
    {
        var traj = ThrowPhysics.Solve(new Vector2(64, 64), new Vector2(68, 64));
        Assert.Equal(20, traj.FlightTicks);
    }
}
