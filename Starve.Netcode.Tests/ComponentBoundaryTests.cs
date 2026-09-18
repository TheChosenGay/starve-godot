using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// "可复用"必须是被强制的约束，不是注释：
/// 组件不许依赖 Godot / protobuf / 本作的游戏程序集，而且换一种状态类型必须照常工作。
/// </summary>
public sealed class ComponentBoundaryTests
{
    [Fact]
    public void ComponentHasNoEngineProtocolOrGameDependencies()
    {
        var referenced = typeof(NetcodeConfig).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        string[] forbidden = ["Godot", "protobuf", "Starve.Core", "Starve.Protocol", "Starve.Game"];
        foreach (var name in referenced)
        {
            foreach (var bad in forbidden)
            {
                Assert.False(
                    name.Contains(bad, StringComparison.OrdinalIgnoreCase),
                    $"Starve.Netcode 不允许依赖 {bad}（实际引用了 {name}）");
            }
        }
    }

    // ── 换一套状态/操作类型：组件一行不用改 ──────────────────────────────
    private struct Pose2D
    {
        public float X;
        public float Y;
    }

    private struct TwinStick
    {
        public float Ax;
        public float Ay;
    }

    private sealed class PoseModel : INetModel<Pose2D, TwinStick>
    {
        public float Speed { get; set; } = 4f;

        public uint Step(ref Pose2D state, in TwinStick action, double dtSeconds, uint baseVersion)
        {
            state.X += action.Ax * Speed * (float)dtSeconds;
            state.Y += action.Ay * Speed * (float)dtSeconds;
            return baseVersion;
        }

        public float Distance(in Pose2D a, in Pose2D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        public Pose2D Delta(in Pose2D from, in Pose2D to) =>
            new() { X = to.X - from.X, Y = to.Y - from.Y };

        public Pose2D AddDelta(in Pose2D state, in Pose2D delta, float k) =>
            new() { X = state.X + k * delta.X, Y = state.Y + k * delta.Y };
    }

    [Fact]
    public void WorksWithADifferentStateAndActionType()
    {
        var smoother = new ClientSmoother<Pose2D, TwinStick>(new PoseModel(), new NetcodeConfig());
        smoother.OnSnapshot(
            new NetSnapshot<Pose2D>(500, 1, 3, new Pose2D { X = 0f, Y = 0f }, false),
            50_000);

        smoother.SetIntent(1, 3, new TwinStick { Ax = 1f, Ay = 0f }, 50_000);
        smoother.AdvanceTo(503);

        Assert.Equal(0.6f, smoother.State.X, 4); // 3 tick × 4 × 0.05
        Assert.Equal(0f, smoother.State.Y, 4);

        // 别人：默认播放延迟 2 tick，所以在 now=502.5 时播放时钟是 500.5
        var remote = smoother.Remote(9);
        remote.Push(500, new Pose2D { X = 10f, Y = 10f });
        remote.Push(501, new Pose2D { X = 12f, Y = 10f });
        Assert.Equal(11f, remote.Sample(502.5, out _).X, 4);
    }
}
