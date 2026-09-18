using Starve.Netcode;

namespace Starve.Netcode.Tests;

/// <summary>
/// 契约校验器本身必须先被验证：合规模型要过，各类违规模型要被抓。
/// 这样游戏侧写自己的 Predictor 时，跑一遍 <see cref="ModelContract.AssertContract"/> 才有意义。
/// </summary>
public sealed class ModelContractTests
{
    private static FakeAction A(int dx) => new() { Dx = dx };

    private static readonly FakeAction[] Sequence =
        [A(1), A(1), A(0), A(-1), A(1), A(1), A(0), A(1)];

    [Fact]
    public void CompliantModelPassesTheContract()
    {
        var model = new FakeModel { Speed = 10f };
        ModelContract.AssertContract(
            model,
            () => new FakeModel { Speed = 10f },
            new FakeState { X = 0f }, new FakeState { X = 100f }, Sequence, 0.05);
    }

    [Fact]
    public void ModelDependingOnCallCountIsRejected()
    {
        // 现实里的样子："用调用计数器做对称打破"
        var model = new CallCountModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelContract.AssertContract(
                model, () => new CallCountModel(),
                new FakeState(), new FakeState { X = 100f }, Sequence, 0.05));
    }

    [Fact]
    public void ModelCachingTheFirstBaseItSawIsRejected()
    {
        // 幂等检查抓不到这种（两次都从同一基准出发 → 结果一样），
        // 只有"两条链交替经过同一实例"才会暴露。
        var model = new FirstBaseOriginModel();

        // 幂等检查过得了（两次同基准 → 结果一样），只有"和干净实例对比"才暴露
        ModelContract.AssertIdempotent(model, new FakeState { X = 5f }, A(1), 0.05);
        Assert.Throws<InvalidOperationException>(() =>
            ModelContract.AssertChainStateless(
                model, () => new FirstBaseOriginModel(),
                new FakeState { X = 0f }, new FakeState { X = 100f }, Sequence, 0.05));
    }

    [Fact]
    public void ModelThatDoesNotEchoTheVersionIsRejected()
    {
        var model = new VersionLyingModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelContract.AssertVersionEchoed(model, new FakeState(), A(1), 0.05, baseVersion: 7));
    }

    [Fact]
    public void ModelWithSideEffectsInQueriesIsRejected()
    {
        var model = new QuerySideEffectModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelContract.AssertQueriesArePure(
                model, () => new QuerySideEffectModel(),
                new FakeState { X = 0f }, Sequence, 0.05));
    }

    // ── 违规模型样例 ─────────────────────────────────────────────
    private sealed class QuerySideEffectModel : INetModel<FakeState, FakeAction>
    {
        private float _bias; // 被查询函数偷偷改动，再由 Step 使用

        public uint Step(ref FakeState state, in FakeAction action, double dtSeconds, uint baseVersion)
        {
            state.X += action.Dx * 10f * (float)dtSeconds + _bias;
            return baseVersion;
        }

        public float Distance(in FakeState a, in FakeState b)
        {
            _bias += 0.01f; // 查询有副作用
            return MathF.Abs(a.X - b.X);
        }

        public FakeState Delta(in FakeState from, in FakeState to) => new() { X = to.X - from.X };

        public FakeState AddDelta(in FakeState state, in FakeState delta, float k) =>
            new() { X = state.X + k * delta.X };
    }

    private sealed class CallCountModel : INetModel<FakeState, FakeAction>
    {
        private int _calls;

        public uint Step(ref FakeState state, in FakeAction action, double dtSeconds, uint baseVersion)
        {
            _calls++;
            state.X += action.Dx * 10f * (float)dtSeconds * (1f + (_calls % 2) * 0.1f);
            return baseVersion;
        }

        public float Distance(in FakeState a, in FakeState b) => MathF.Abs(a.X - b.X);

        public FakeState Delta(in FakeState from, in FakeState to) => new() { X = to.X - from.X };

        public FakeState AddDelta(in FakeState state, in FakeState delta, float k) =>
            new() { X = state.X + k * delta.X };
    }

    private sealed class FirstBaseOriginModel : INetModel<FakeState, FakeAction>
    {
        private bool _hasOrigin;
        private FakeState _origin;

        public uint Step(ref FakeState state, in FakeAction action, double dtSeconds, uint baseVersion)
        {
            if (!_hasOrigin)
            {
                _origin = state;
                _hasOrigin = true;
            }

            state.X = _origin.X + action.Dx * 10f * (float)dtSeconds;
            return baseVersion;
        }

        public float Distance(in FakeState a, in FakeState b) => MathF.Abs(a.X - b.X);

        public FakeState Delta(in FakeState from, in FakeState to) => new() { X = to.X - from.X };

        public FakeState AddDelta(in FakeState state, in FakeState delta, float k) =>
            new() { X = state.X + k * delta.X };
    }

    private sealed class VersionLyingModel : INetModel<FakeState, FakeAction>
    {
        public uint Step(ref FakeState state, in FakeAction action, double dtSeconds, uint baseVersion)
        {
            state.X += action.Dx * 10f * (float)dtSeconds;
            return baseVersion + 1; // 不回传原版本
        }

        public float Distance(in FakeState a, in FakeState b) => MathF.Abs(a.X - b.X);

        public FakeState Delta(in FakeState from, in FakeState to) => new() { X = to.X - from.X };

        public FakeState AddDelta(in FakeState state, in FakeState delta, float k) =>
            new() { X = state.X + k * delta.X };
    }
}
