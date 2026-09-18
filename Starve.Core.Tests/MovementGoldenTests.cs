using System.Text.Json;
using System.Text.Json.Serialization;
using Starve.Core;

namespace Starve.Core.Tests;

public sealed class MovementGoldenTests
{
    [Fact]
    public void MatchesServerMovementVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "movement_golden.json");
        var vectors = JsonSerializer.Deserialize<List<MovementGolden>>(File.ReadAllText(path))
                      ?? throw new InvalidOperationException("movement golden vectors are empty");

        foreach (var vector in vectors)
        {
            // blocked = 硬墙格（地形：水/悬崖）；占位物走 shapes，不影响可走网格
            var blocked = vector.Blocked.Select(cell => (X: cell[0], Y: cell[1])).ToHashSet();
            var sim = new OwnMovementSim((x, y) => !blocked.Contains((x, y)));
            sim.SetBlockers(vector.Shapes.Select(s => s.Kind == "box"
                ? BlockerShape.Box(s.X, s.Y, s.W, s.H)
                : BlockerShape.Circle(s.X, s.Y, s.R)).ToList());
            sim.SetBodyRadius(vector.BodyRadius > 0 ? vector.BodyRadius : OwnMovementSim.DefaultBodyRadius);
            sim.SnapTo(vector.StartX, vector.StartY);
            sim.SetSpeed(vector.Speed);
            sim.SetIntent(vector.DX, vector.DY);
            sim.Tick(vector.DTMS);

            var tol = vector.Tol > 0 ? vector.Tol : 1e-5f;
            Assert.True(
                MathF.Abs(sim.Position.X - vector.WantX) <= tol,
                $"{vector.Name}: x={sim.Position.X}, want={vector.WantX} (±{tol})");
            Assert.True(
                MathF.Abs(sim.Position.Y - vector.WantY) <= tol,
                $"{vector.Name}: y={sim.Position.Y}, want={vector.WantY} (±{tol})");
        }
    }

    private sealed class MovementGolden
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("start_x")] public float StartX { get; init; }
        [JsonPropertyName("start_y")] public float StartY { get; init; }
        [JsonPropertyName("dx")] public int DX { get; init; }
        [JsonPropertyName("dy")] public int DY { get; init; }
        [JsonPropertyName("speed")] public float Speed { get; init; }
        [JsonPropertyName("dt_ms")] public float DTMS { get; init; }

        /// <summary>硬墙格（地形：水/悬崖）：跨格不可走，贴边停在边界外侧。</summary>
        [JsonPropertyName("blocked")] public int[][] Blocked { get; init; } = [];

        /// <summary>占位物形状：circle（格心圆，x/y/r）或 box（占格盒，x/y/w/h）。</summary>
        [JsonPropertyName("shapes")] public List<GoldenShape> Shapes { get; init; } = [];

        /// <summary>移动体半径（格）；有 shapes 时必填。</summary>
        [JsonPropertyName("body_radius")] public float BodyRadius { get; init; }

        /// <summary>比较容差；缺省 1e-5，带接触回退（skin≈1e-3）的向量为 2e-3。</summary>
        [JsonPropertyName("tol")] public float Tol { get; init; }

        [JsonPropertyName("want_x")] public float WantX { get; init; }
        [JsonPropertyName("want_y")] public float WantY { get; init; }
    }

    private sealed class GoldenShape
    {
        [JsonPropertyName("kind")] public string Kind { get; init; } = "circle";
        [JsonPropertyName("x")] public float X { get; init; }
        [JsonPropertyName("y")] public float Y { get; init; }
        [JsonPropertyName("r")] public float R { get; init; }
        [JsonPropertyName("w")] public float W { get; init; }
        [JsonPropertyName("h")] public float H { get; init; }
    }
}
