using System.Numerics;
using Starve.Core;

namespace Starve.Core.Tests;

/// <summary>
/// 投掷物理的两端一致性契约。
///
/// 客户端与服务端各自算一份（用户确认的设计），所以**公式必须逐字一致**。
/// 这里钉住的数值全部来自服务端实现
/// （internal/game/components/throwable.go）与 Go 侧测试，
/// 任何一边改了公式，这里都会失败。
/// </summary>
public sealed class ThrowPhysicsTests
{
    /// <summary>最大距离 = 基础距离 × 力量 / 质量（整数除法，两端一致）。</summary>
    [Fact]
    public void MaxDistanceMatchesServerFormula()
    {
        Assert.Equal(8, ThrowPhysics.MaxThrowDistance(10, 10));   // 力量==质量 → 基础距离
        Assert.Equal(16, ThrowPhysics.MaxThrowDistance(20, 10));  // 力量翻倍 → 距离翻倍
        Assert.Equal(4, ThrowPhysics.MaxThrowDistance(10, 20));   // 质量翻倍 → 距离减半
        // 服务端实测值：玩家力量 20、炸弹质量 18 → 8*20/18 = 8（整数除法）
        Assert.Equal(8, ThrowPhysics.MaxThrowDistance(20, 18));
    }

    [Fact]
    public void MaxDistanceRejectsInvalidInput()
    {
        Assert.Equal(0, ThrowPhysics.MaxThrowDistance(0, 10));
        Assert.Equal(0, ThrowPhysics.MaxThrowDistance(10, 0));
        Assert.Equal(0, ThrowPhysics.MaxThrowDistance(-1, 10));
        Assert.Equal(0, ThrowPhysics.MaxThrowDistance(10, -1));
        // 力量远小于质量 → 0（不能投掷）
        Assert.Equal(0, ThrowPhysics.MaxThrowDistance(1, 100));
    }

    /// <summary>飞行时长 t = sqrt(2d/g)，与服务端一致。</summary>
    [Fact]
    public void FlightTicksMatchesServerFormula()
    {
        // 服务端实测：距离 3、g=0.02 → sqrt(300) ≈ 17.3 → 17
        Assert.Equal(17, ThrowPhysics.FlightTicks(3));
        // 距离 4 → sqrt(400) = 20
        Assert.Equal(20, ThrowPhysics.FlightTicks(4));
        // 距离 8 → sqrt(800) ≈ 28.3 → 28
        Assert.Equal(28, ThrowPhysics.FlightTicks(8));
        // 距离 0 至少 1 tick（否则同一 tick 起落，看不出飞行）
        Assert.Equal(1, ThrowPhysics.FlightTicks(0));
    }

    [Fact]
    public void FlightTicksHandlesBadGravity()
    {
        foreach (var g in new[] { 0.0, -1.0 })
        {
            Assert.True(ThrowPhysics.FlightTicks(8, g) >= 1);
        }
    }

    /// <summary>峰值高度 h = g·t²/8，与服务端一致（时长翻倍 → 高度 4 倍）。</summary>
    [Fact]
    public void PeakHeightMatchesServerFormula()
    {
        var h1 = ThrowPhysics.PeakHeight(10);
        var h2 = ThrowPhysics.PeakHeight(20);
        Assert.True(h1 > 0);
        Assert.Equal(4.0, h2 / h1, 6);
        // 具体值：h = g·t²/8 = 0.08 × 1 / 8 = 0.01
        Assert.Equal(0.01, ThrowPhysics.PeakHeight(1, 0.08), 6);
        // 时长为 0 时高度必为 0（避免除零/负值）
        Assert.Equal(0, ThrowPhysics.PeakHeight(0), 6);
    }

    /// <summary>水平匀速：最后一 tick 精确落在落点（与服务端测试同一条契约）。</summary>
    [Fact]
    public void TrajectoryLandsExactlyOnTarget()
    {
        var traj = ThrowPhysics.Solve(new Vector2(10, 10), new Vector2(14, 10));
        var (pos, height) = traj.SampleAt(traj.FlightTicks);
        Assert.Equal(14f, pos.X, 5);
        Assert.Equal(10f, pos.Y, 5);
        Assert.Equal(0.0, height, 6); // 落地时高度归零
    }

    /// <summary>高度曲线对称，峰值在中点。</summary>
    [Fact]
    public void HeightIsSymmetricWithPeakAtMidpoint()
    {
        const double peak = 2.0;
        Assert.Equal(0, ThrowPhysics.HeightAt(0, peak), 6);
        Assert.Equal(0, ThrowPhysics.HeightAt(1, peak), 6);
        Assert.Equal(peak, ThrowPhysics.HeightAt(0.5, peak), 6);
        // 对称：t 与 1-t 高度相同
        Assert.Equal(ThrowPhysics.HeightAt(0.3, peak), ThrowPhysics.HeightAt(0.7, peak), 6);
    }

    /// <summary>求解结果与服务端 ThrowArc 的字段口径一致。</summary>
    [Fact]
    public void SolveProducesSameShapeAsServerArc()
    {
        var traj = ThrowPhysics.Solve(new Vector2(0, 0), new Vector2(6, 0));
        Assert.Equal(24, traj.FlightTicks);          // sqrt(2*6/0.02)=sqrt(600)≈24.5→24
        Assert.Equal(ThrowPhysics.DefaultGravity, traj.Gravity);
        Assert.True(traj.PeakHeight > 0);
        Assert.Equal(traj.FlightTicks / 20.0, traj.FlightSeconds, 6);
    }

    /// <summary>距离用欧氏（圆），不是切比雪夫（方）——与服务端一致。</summary>
    [Fact]
    public void DistanceIsEuclideanNotChebyshev()
    {
        // 对角 (3,4) 的欧氏距离是 5；切比雪夫会是 4
        var traj = ThrowPhysics.Solve(new Vector2(0, 0), new Vector2(3, 4));
        var expected = ThrowPhysics.FlightTicks(5.0);
        Assert.Equal(expected, traj.FlightTicks);
    }

    /// <summary>非法时长不崩（进度夹取）。</summary>
    [Fact]
    public void SampleHandlesInvalidTicks()
    {
        var traj = new ThrowTrajectory
        {
            From = new Vector2(0, 0), To = new Vector2(5, 0),
            FlightTicks = 0, PeakHeight = 1, Gravity = ThrowPhysics.DefaultGravity,
        };
        var (pos, height) = traj.SampleAt(99);
        Assert.Equal(5f, pos.X, 5); // FlightTicks<=0 → 视为立即落地
        Assert.Equal(0.0, height, 6);
    }

    /// <summary>进度超出总时长时停在落点，不越过。</summary>
    [Fact]
    public void SampleClampsPastEnd()
    {
        var traj = ThrowPhysics.Solve(new Vector2(0, 0), new Vector2(10, 0));
        var (pos, _) = traj.SampleAt(traj.FlightTicks * 5);
        Assert.Equal(10f, pos.X, 5);
    }
}
