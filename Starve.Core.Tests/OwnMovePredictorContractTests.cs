using Starve.Core;
using Starve.Netcode;

namespace Starve.Core.Tests;

/// <summary>
/// 真实预测模型（<see cref="OwnMovePredictor"/>）必须过组件的契约检查。
///
/// 这是"重放能不能对"的前提：模型一旦藏了跨步状态（ORCA 自身速度、邻居缓存、
/// 调用计数……），"玩家移动"和"组件内部重放"两条路就会给出不同结果。
/// 校验器用**干净实例**算期望值，所以实例级泄漏也跑不掉。
/// </summary>
public sealed class OwnMovePredictorContractTests
{
    /// <summary>平地、全可走、无障碍、无邻居。</summary>
    private static OwnMovePredictor Flat() => new((_, _) => true);

    /// <summary>有墙 + 有邻居：把 ORCA 与格子层判断都跑起来。</summary>
    private static OwnMovePredictor Crowded()
    {
        var model = new OwnMovePredictor((x, _) => x < 12);
        model.SetBodyRadius(0.3f);
        model.SetSpeedProfile(10f, 0.35f);
        model.SetBlockers([BlockerShape.Circle(11.5f, 10.5f, 0.4f)]);
        model.SetNeighbors(
        [
            new OrcaNeighbor { X = 10.6f, Y = 10.2f, VX = -8f, VY = 0f, Radius = 0.3f, HalfLength = 0.35f, MaxSpeed = 10f },
            new OrcaNeighbor { X = 9.4f, Y = 10.3f, VX = 6f, VY = 1f, Radius = 0.3f, HalfLength = 0.35f, MaxSpeed = 10f },
        ]);
        return model;
    }

    private static readonly MoveIntent[] Intents =
    [
        new MoveIntent(1, 0),
        new MoveIntent(1, 1),
        MoveIntent.Stop,
        new MoveIntent(-1, 0),
        new MoveIntent(0, 1),
        new MoveIntent(1, 0),
        new MoveIntent(1, -1),
    ];

    [Fact]
    public void FlatPredictorIsStatelessWithRespectToTheChain()
    {
        ModelContract.AssertContract(
            Flat(),
            Flat,
            OwnMoveState.FromContinuous(10f, 10f),
            OwnMoveState.FromContinuous(40f, 10f),
            Intents,
            0.05,
            baseVersion: 0,
            tolerance: 1e-3f);
    }

    [Fact]
    public void PredictorWithOrcaAndCollisionIsStillStateless()
    {
        // ORCA 最容易藏状态（自身速度、邻居缓存）——这里正面验一下。
        ModelContract.AssertContract(
            Crowded(),
            Crowded,
            OwnMoveState.FromContinuous(10f, 10f),
            OwnMoveState.FromContinuous(9.5f, 10.5f),
            Intents,
            0.05,
            baseVersion: 0,
            tolerance: 1e-3f);
    }

    [Fact]
    public void OrcaSelfVelocityLivesInTheStateNotInTheModel()
    {
        // 直接证明"自身速度在 state 里"：同一起点连推两步，
        // 只把状态喂回去才会得到有惯性的结果；模型本身不累积。
        var model = Flat();
        model.SetNeighbors(
        [
            new OrcaNeighbor { X = 12f, Y = 10f, VX = 0f, VY = 0f, Radius = 0.3f, HalfLength = 0.35f, MaxSpeed = 10f },
        ]);

        var a = OwnMoveState.FromContinuous(10f, 10f);
        model.Step(ref a, new MoveIntent(1, 0), 0.05, 0);

        var b = OwnMoveState.FromContinuous(10f, 10f);
        model.Step(ref b, new MoveIntent(1, 0), 0.05, 0);

        // 两次从同一基准出发 → 结果必须一致（模型里没有累积）
        Assert.Equal(a.X, b.X, 5);
        Assert.Equal(a.VelX, b.VelX, 5);
    }
}
