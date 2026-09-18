using Starve.Core;
using Xunit;

namespace Starve.Core.Tests;

/// <summary>
/// 锁定客户端 ORCA 与服务端（Go）同解。
///
/// 这里的期望值是从 Go 实现 <c>internal/game/systems/orca.go</c> 实跑导出的，
/// 不是手算的——所以这个测试就是"两端同解"的契约。
/// 改动任何一侧的 ORCA 公式都会让这里红，从而强制两边一起改。
/// </summary>
public class OrcaParityTests
{
    private static OrcaAgent Self() => new()
    {
        VX = 1, VY = 0, PrefVX = 1, PrefVY = 0, X = 0, Z = 0, Radius = 0.3f, MaxSpeed = 1,
    };

    // 容差 4 位小数：C# 用 float、Go 用 float64，末位会有 ~1e-7 的舍入差；
    // 但公式若改动，偏差会是 1e-2 量级，4 位小数足以抓住真正的分叉。
    private static void AssertVec(float wantX, float wantY, params OrcaBody[] ns)
    {
        var solver = new OrcaAvoidance(OrcaOptions.Default);
        solver.Solve(Self(), ns, out var vx, out var vy);
        Assert.Equal(wantX, vx, 4);
        Assert.Equal(wantY, vy, 4);
    }

    // 迎面：偏置是世界系常量 ⇒ 结果与身份无关，恒定向同一个世界侧让。
    // Go(τ=0.5): head_on = (0.399375, 0.489770)
    [Fact]
    public void HeadOnTakesFixedWorldSide()
    {
        var n = new OrcaBody { X = 0.8f, Z = 0, VX = -1, Radius = 0.3f, MaxSpeed = 1 };
        AssertVec(0.399375f, 0.489770f, n);
    }

    // **互惠性**：把迎面场景整体镜像（x → −x）后，另一个人必须让到**相反的世界侧**。
    //
    // 这条才是"对称打破有没有用"的判据：双方各自解一次、坐标系镜像，
    // 若两边让到同一世界侧，相对横向间距不变 —— 顶住/对穿。
    // Go(τ=0.5): mirrored_head_on = (-0.399375, -0.489770)
    [Fact]
    public void MirroredHeadOnTakesOppositeWorldSide()
    {
        var solver = new OrcaAvoidance(OrcaOptions.Default);
        var self = new OrcaAgent { VX = -1, VY = 0, PrefVX = -1, PrefVY = 0, X = 0, Z = 0, Radius = 0.3f, MaxSpeed = 1 };
        var n = new OrcaBody { X = -0.8f, Z = 0, VX = 1, Radius = 0.3f, MaxSpeed = 1 };
        solver.Solve(self, new[] { n }, out var vx, out var vy);
        Assert.Equal(-0.399375f, vx, 4);
        Assert.Equal(-0.489770f, vy, 4);
    }

    [Fact]
    public void LegPassingMatchesServer()
    {
        // Go(τ=0.5): leg_passing id1=id2=(0.968354,-0.018987)
        var n = new OrcaBody { X = 1.5f, Z = 0.3f, VX = -1, Radius = 0.3f, MaxSpeed = 1 };
        AssertVec(0.968354f, -0.018987f, n);
    }

    [Fact]
    public void ParallelSameSpeedDoesNotAvoid()
    {
        var n = new OrcaBody { X = 0, Z = 1.5f, VX = 1, Radius = 0.3f, MaxSpeed = 1 };
        AssertVec(1f, 0f, n);
    }

    [Fact]
    public void OverlapPushesApart()
    {
        var agent = Self();
        agent.VX = 0; agent.VY = 0; agent.PrefVX = 0; agent.PrefVY = 0;
        var solver = new OrcaAvoidance(OrcaOptions.Default);
        solver.Solve(agent, new[] { new OrcaBody { X = 0.1f, Z = 0, Radius = 0.3f, MaxSpeed = 1 } },
            out var vx, out var vy);
        // Go(τ=0.5): overlap -> (0.620000, 0.000000)
        Assert.Equal(0.620000f, vx, 4);
        Assert.Equal(0f, vy, 4);
    }

    [Fact]
    public void NeighborOrderDoesNotChangeResult()
    {
        var a = new OrcaBody { X = 0.8f, Z = 0, VX = -1, Radius = 0.3f, MaxSpeed = 1 };
        var b = new OrcaBody { X = 1.2f, Z = 0.3f, VX = -1, Radius = 0.3f, MaxSpeed = 1 };
        var s1 = new OrcaAvoidance(OrcaOptions.Default);
        var s2 = new OrcaAvoidance(OrcaOptions.Default);
        s1.Solve(Self(), new[] { a, b }, out var x1, out var y1);
        s2.Solve(Self(), new[] { b, a }, out var x2, out var y2);
        Assert.Equal(x1, x2, 6);
        Assert.Equal(y1, y2, 6);
    }
}
