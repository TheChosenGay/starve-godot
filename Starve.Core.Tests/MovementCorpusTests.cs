using System.Text.Json;
using System.Text.Json.Serialization;
using Starve.Core;
using Xunit.Abstractions;

namespace Starve.Core.Tests;

/// <summary>
/// 跨端一致性语料 <c>testdata/move_corpus.jsonl</c> 的**客户端回放**测试。
///
/// 语料的 <c>want_x/want_y</c> 由服务端真实求解路径（MoveSolver + ApplyDisplacement 三阶段）
/// 产出；本测试用客户端自己那套数学（<see cref="OwnMovePredictor"/>）逐条回放并比对，
/// 从而在 CI 里堵住"两端不一致"。它比 <see cref="MovementGoldenTests"/> 覆盖面更宽：
/// 9 类场景（base/diagonal/slope/wall/circle/box/capsule/orca/combo）各 40 条，
/// 首次把 **ORCA 动态避让** 与 **形状滑掠 + 坡度 + 邻居** 的组合也纳入契约。
///
/// 与服务端的对齐约定（任何一条改动都等于改契约，需两侧同步）：
///   - <c>blocked</c> 是硬墙格（地形水/悬崖），按左上角格判定，客户端 <c>walkable</c> 返回 false；
///   - <c>heights</c> 是线性高度场 <c>h(x,y)=base+grad_x*x+grad_y*y</c>，且语料保证
///     **全部角点高度是 [0,255] 内的整数**：服务端把它存进 <c>CornerHeights []byte</c>，
///     越界会按低 8 位回绕（-1→255、-2→254…），那时"真实求解用的高度场"就**不等于**
///     JSON 里声明的线性场，任何客户端用声明值都复现不了 —— seed=11 的
///     <c>slope_004</c>/<c>slope_015</c> 两条历史红就是这个原因（服务端生成器的 <c>base</c>
///     太小，已改成按梯度跨度自动抬到值域内）。本测试对此做硬校验，且**不放宽容差**；
///   - 高度回放跑**两条都必须一致**的路径：① 声明场解析式（验证契约自洽）；
///     ② 把声明场落成角点网格后走客户端生产采样 <see cref="TileMap.LogicalHeightAt"/>
///     （<c>= SlopeMesh.SampleHeight</c>，含 |Δh|≥1 的崖壁带，与 <c>worldmap.HeightAt</c> 同构），
///     验证两端**采样实现**本身也一致。不能用双线性代替：<c>TileMap.HeightAt</c> 只在
///     <c>SmoothSlopes</c> 时走双线性，移动/坡度必须用 <c>LogicalHeightAt</c>；
///   - <c>shapes</c> 的 x/y 已经是**对齐后的中心**（circle）/左上角锚点（box），
///     不能再套 <c>GameRoot.RebuildBlockers</c> 里那种 <c>+0.5f</c> 的"格锚点→中心"转换；
///   - <c>capsule</c> 场景里的胶囊是**动态邻居**（走 ORCA 软避让），不是静态 <c>SetBlockers</c>；
///   - ORCA 邻居按 <c>GameRoot.SyncOrcaNeighbors</c> 的确定性顺序（X → Y → Radius）排序，LP 对顺序敏感。
///
/// <para>
/// **语料世界固定 24×24**（与服务端 <c>cmd/movecorpus</c> 的 <c>worldSize</c> 同一约定），
/// 第二遍回放要用它建 <see cref="TileMap"/>；服务端改这个常量时这里会先报出明确信息，
/// 而不是变成难查的数值分叉。
/// </para>
/// </summary>
public sealed class MovementCorpusTests
{
    /// <summary>语料世界尺寸：必须与服务端 <c>cmd/movecorpus/main.go</c> 的 worldSize 一致。</summary>
    private const int WorldSize = 24;
    /// <summary>
    /// 比对容差（格）。沿用 <see cref="MovementGoldenTests"/> 的既有约定 1e-5：
    /// 语料期望值是服务端 float64 求解结果，客户端 <c>Starve.Core</c> 全程 float32，
    /// 单步的量级 ~0.5 格、float32 相对误差 ~1e-7 ⇒ 绝对差 ~1e-7，1e-5 有 100 倍余量。
    /// 出现超过容差的失败必须当成**真实的两端分叉**来查，不允许放宽这个值。
    /// </summary>
    private const double Tolerance = 1e-5;

    private readonly ITestOutputHelper _output;

    public MovementCorpusTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MatchesServerCorpusVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "move_corpus.jsonl");
        Assert.True(File.Exists(path), $"缺少跨端语料 {path}（应在 csproj 里以 CopyToOutputDirectory 链接）");

        var (metaCount, scenarios) = LoadScenarios(path);
        Assert.NotEmpty(scenarios);
        if (metaCount is { } declared)
        {
            Assert.Equal(declared, scenarios.Count);
        }

        // 语料世界尺寸的耦合检查：第二遍回放用 WorldSize 建 TileMap，服务端生成时也用同一个值。
        Assert.All(scenarios, s => Assert.True(
            s.StartX >= 1 && s.StartX <= WorldSize - 1 && s.StartY >= 1 && s.StartY <= WorldSize - 1,
            $"{s.Name} 起点 ({s.StartX},{s.StartY}) 越出语料世界 {WorldSize}×{WorldSize}；" +
            "worldSize 必须与服务端 cmd/movecorpus/main.go 保持一致"));

        var passed = new Dictionary<string, int>(StringComparer.Ordinal);
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var failed = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var scenario in scenarios)
        {
            var category = scenario.Category;
            totals[category] = totals.GetValueOrDefault(category) + 1;

            // 高度场硬校验：与服务端生成器的 assertHeightFieldFits 一一对应。
            AssertHeightFieldInDomain(scenario);

            var (gotX, gotY) = Replay(scenario, productionSampler: false);
            if (Math.Abs(gotX - scenario.WantX) <= Tolerance &&
                Math.Abs(gotY - scenario.WantY) <= Tolerance)
            {
                passed[category] = passed.GetValueOrDefault(category) + 1;
                continue;
            }

            failed[category] = failed.GetValueOrDefault(category) + 1;
            failures.Add(Describe(scenario, gotX, gotY));
        }

        var passedTotal = 0;
        foreach (var (category, total) in totals.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var ok = passed.GetValueOrDefault(category);
            passedTotal += ok;
            _output.WriteLine($"{category,-9} {ok,3}/{total}  通过");
        }

        _output.WriteLine($"{"TOTAL",-9} {passedTotal,3}/{scenarios.Count}  容差 {Tolerance:G}");

        // 第二遍：走客户端**生产采样路径**回放（TileMap.LogicalHeightAt = SlopeMesh.SampleHeight，
        // 含 |Δh|≥1 的崖壁带）。第一遍只证明"声明场解析式 == 服务端 want"，
        // 这一遍才覆盖两端**采样实现**（worldmap.HeightAt vs SlopeMesh.SampleHeight）也一致。
        var samplerPassed = 0;
        var samplerFailures = new List<string>();
        foreach (var scenario in scenarios)
        {
            var (samplerX, samplerY) = Replay(scenario, productionSampler: true);
            if (Math.Abs(samplerX - scenario.WantX) <= Tolerance &&
                Math.Abs(samplerY - scenario.WantY) <= Tolerance)
            {
                samplerPassed++;
                continue;
            }

            samplerFailures.Add(Describe(scenario, samplerX, samplerY));
        }

        _output.WriteLine($"{"采样路径",-9} {samplerPassed,3}/{scenarios.Count}  通过（TileMap.LogicalHeightAt）");

        // 通过条数必须等于语料条数；失败明细按类别聚合，方便直接定位是哪一类分叉。
        var detail = new System.Text.StringBuilder();
        detail.Append($"跨端语料回放：通过 {passedTotal}/{scenarios.Count} 条（容差 {Tolerance:G}）");
        foreach (var (category, count) in failed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            detail.Append($"\n  {category}: 失败 {count}/{totals[category]}");
        }

        const int maxShown = 40;
        detail.Append($"\n失败明细（最多 {maxShown} 条）：\n");
        detail.Append(string.Join("\n", failures.Take(maxShown)));
        if (failures.Count > maxShown)
        {
            detail.Append($"\n… 另有 {failures.Count - maxShown} 条失败未列出");
        }

        if (samplerFailures.Count > 0)
        {
            detail.Append($"\n\n生产采样路径（TileMap.LogicalHeightAt）失败 {samplerFailures.Count}/{scenarios.Count} 条：\n");
            detail.Append(string.Join("\n", samplerFailures.Take(maxShown)));
            if (samplerFailures.Count > maxShown)
            {
                detail.Append($"\n… 另有 {samplerFailures.Count - maxShown} 条失败未列出");
            }
        }

        Assert.True(passedTotal == scenarios.Count && samplerFailures.Count == 0, detail.ToString());
    }

    /// <summary>
    /// 高度场硬校验（服务端 <c>assertHeightFieldFits</c> 的客户端对应物）：
    /// 角点高度必须是 [0,255] 内的整数，否则服务端 <c>[]byte</c> 会回绕，语料自相矛盾。
    /// </summary>
    private static void AssertHeightFieldInDomain(CorpusScenario s)
    {
        if (s.Heights is not { } h) return;

        for (var cy = 0; cy <= WorldSize; cy++)
        {
            for (var cx = 0; cx <= WorldSize; cx++)
            {
                var v = h.Base + h.GradX * cx + h.GradY * cy;
                var inDomain = v == Math.Truncate(v) && v >= 0 && v <= 255;
                Assert.True(inDomain,
                    $"语料高度场越界：{s.Name} h({cx},{cy})={v:G17}，" +
                    "角点高度必须是 [0,255] 整数（服务端 []byte 会回绕 ⇒ 语料失效）");
            }
        }
    }

    /// <summary>用客户端数学回放一条语料，返回推进后的连续位置。</summary>
    /// <param name="productionSampler">
    /// true：把语料声明的线性场落成 byte 角点网格，走生产采样 <see cref="TileMap.LogicalHeightAt"/>；
    /// false：直接按声明场解析式求值。两者都必须与服务端 want 一致。
    /// </param>
    private static (float X, float Y) Replay(CorpusScenario s, bool productionSampler)
    {
        // blocked = 硬墙格（地形：水/悬崖）。按左上角格判定，与服务端 Walkable 一致。
        var blocked = new HashSet<(int X, int Y)>();
        foreach (var cell in s.Blocked)
        {
            blocked.Add((cell[0], cell[1]));
        }

        var pred = new OwnMovePredictor((x, y) => !blocked.Contains((x, y)));

        pred.HeightAt = BuildHeightAt(s, productionSampler);

        pred.SetSpeed((float)s.Speed);
        pred.SetSpeedProfile((float)s.Speed, 0f);
        pred.SetBodyRadius((float)s.BodyRadius);
        // 客户端 ORCA 带对称打破（服务端生产 solver 为 false，是已知差异）；
        // 语料的 ORCA 场景刻意留了 0.05~0.1 的横向偏移以避开完全共线退化，key 取 1。
        pred.SetSelfKey(1);

        // 静态形状：语料的 x/y 已经是中心（circle）/左上角锚点（box），
        // 不要再套 RebuildBlockers 的 +0.5 格转换。胶囊只作为邻居出现，绝不进静态形状。
        pred.SetBlockers(s.Shapes.Select(shape => shape.Kind == "box"
            ? BlockerShape.Box((float)shape.X, (float)shape.Y, (float)shape.W, (float)shape.H)
            : BlockerShape.Circle((float)shape.X, (float)shape.Y, (float)shape.R)).ToList());

        // ORCA 邻居：与 GameRoot.SyncOrcaNeighbors 同一排序规则（X → Y → Radius）。
        var neighbors = s.Neighbors
            .Select(n => new OrcaNeighbor
            {
                X = (float)n.X,
                Y = (float)n.Y,
                VX = (float)n.VX,
                VY = (float)n.VY,
                Radius = (float)n.Radius,
                HalfLength = (float)n.HalfLength,
                MaxSpeed = (float)n.MaxSpeed,
            })
            .OrderBy(n => n.X)
            .ThenBy(n => n.Y)
            .ThenBy(n => n.Radius)
            .ToList();
        pred.SetNeighbors(neighbors);

        var state = OwnMoveState.FromContinuous((float)s.StartX, (float)s.StartY);
        pred.Step(ref state, new MoveIntent(s.Dx, s.Dy), s.DtMs / 1000.0, 0);
        return (state.X, state.Y);
    }

    /// <summary>
    /// 构造回放用的高度函数：
    ///  - <paramref name="productionSampler"/>=false 时直接求值语料声明的线性场；
    ///  - =true 时把同一个线性场落成字节角点网格（Go 侧 <c>byte(float)</c> 是**截断**，
    ///    这里也用 <see cref="Math.Truncate"/> 对齐），再走客户端生产采样
    ///    <see cref="TileMap.LogicalHeightAt"/>（与 <c>worldmap.HeightAt</c> 同构，含崖壁带）。
    /// 字段 <c>types</c> 全 0（陆地）：语料只声明硬墙格，不声明水角点。
    /// </summary>
    private static Func<float, float, float> BuildHeightAt(CorpusScenario s, bool productionSampler)
    {
        if (s.Heights is not { } h)
        {
            return static (_, _) => 0f;
        }

        if (!productionSampler)
        {
            return (x, y) => (float)(h.Base + h.GradX * x + h.GradY * y);
        }

        var grid = new byte[(WorldSize + 1) * (WorldSize + 1)];
        for (var cy = 0; cy <= WorldSize; cy++)
        {
            for (var cx = 0; cx <= WorldSize; cx++)
            {
                grid[cy * (WorldSize + 1) + cx] =
                    (byte)Math.Truncate(h.Base + h.GradX * cx + h.GradY * cy);
            }
        }

        var tileMap = new TileMap(WorldSize, WorldSize, grid, new byte[grid.Length]);
        return tileMap.LogicalHeightAt;
    }

    private static string Describe(CorpusScenario s, float gotX, float gotY)
    {
        var heights = s.Heights is null
            ? "无"
            : $"{s.Heights.Base:G6}+{s.Heights.GradX:G6}x+{s.Heights.GradY:G6}y";
        return $"[{s.Category}] {s.Name}: " +
               $"start=({s.StartX:G17},{s.StartY:G17}) dir=({s.Dx},{s.Dy}) speed={s.Speed:G17} " +
               $"dt={s.DtMs}ms radius={s.BodyRadius} shapes={s.Shapes.Count} neighbors={s.Neighbors.Count} " +
               $"blocked={s.Blocked.Length} heights={heights} | " +
               $"want=({s.WantX:G17},{s.WantY:G17}) got=({gotX:G17},{gotY:G17}) " +
               $"delta=({(double)gotX - s.WantX:G6},{(double)gotY - s.WantY:G6})";
    }

    /// <summary>逐行读 JSONL：跳过首行 meta，其余反序列化成场景（snake_case 字段）。</summary>
    private static (int? MetaCount, List<CorpusScenario> Scenarios) LoadScenarios(string path)
    {
        int? metaCount = null;
        var scenarios = new List<CorpusScenario>();

        foreach (var raw in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("meta", out var meta))
            {
                metaCount = meta.TryGetProperty("count", out var count) ? count.GetInt32() : null;
                continue;
            }

            var scenario = doc.RootElement.Deserialize<CorpusScenario>()
                           ?? throw new InvalidOperationException($"语料行解析失败：{raw}");
            scenarios.Add(scenario);
        }

        return (metaCount, scenarios);
    }

    private sealed class CorpusScenario
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("category")] public string Category { get; init; } = "";
        [JsonPropertyName("start_x")] public double StartX { get; init; }
        [JsonPropertyName("start_y")] public double StartY { get; init; }
        [JsonPropertyName("dx")] public int Dx { get; init; }
        [JsonPropertyName("dy")] public int Dy { get; init; }
        [JsonPropertyName("speed")] public double Speed { get; init; }
        [JsonPropertyName("dt_ms")] public int DtMs { get; init; }

        /// <summary>硬墙格（地形：水/悬崖），按左上角格判定可走性。</summary>
        [JsonPropertyName("blocked")] public int[][] Blocked { get; init; } = [];

        /// <summary>可选线性高度场；缺省表示平地。</summary>
        [JsonPropertyName("heights")] public CorpusHeights? Heights { get; init; }

        /// <summary>静态形状（只有 circle/box；胶囊只在 neighbors 里）。</summary>
        [JsonPropertyName("shapes")] public List<CorpusShape> Shapes { get; init; } = [];

        /// <summary>ORCA 动态邻居（胶囊）。</summary>
        [JsonPropertyName("neighbors")] public List<CorpusNeighbor> Neighbors { get; init; } = [];

        [JsonPropertyName("body_radius")] public double BodyRadius { get; init; }
        [JsonPropertyName("want_x")] public double WantX { get; init; }
        [JsonPropertyName("want_y")] public double WantY { get; init; }
    }

    private sealed class CorpusHeights
    {
        [JsonPropertyName("base")] public double Base { get; init; }
        [JsonPropertyName("grad_x")] public double GradX { get; init; }
        [JsonPropertyName("grad_y")] public double GradY { get; init; }
    }

    private sealed class CorpusShape
    {
        [JsonPropertyName("kind")] public string Kind { get; init; } = "circle";
        [JsonPropertyName("x")] public double X { get; init; }
        [JsonPropertyName("y")] public double Y { get; init; }
        [JsonPropertyName("r")] public double R { get; init; }
        [JsonPropertyName("w")] public double W { get; init; }
        [JsonPropertyName("h")] public double H { get; init; }
    }

    private sealed class CorpusNeighbor
    {
        [JsonPropertyName("x")] public double X { get; init; }
        [JsonPropertyName("y")] public double Y { get; init; }
        [JsonPropertyName("vx")] public double VX { get; init; }
        [JsonPropertyName("vy")] public double VY { get; init; }
        [JsonPropertyName("radius")] public double Radius { get; init; }
        [JsonPropertyName("half_length")] public double HalfLength { get; init; }
        [JsonPropertyName("max_speed")] public double MaxSpeed { get; init; }
    }
}
