using System.Text.Json.Serialization;
using Starve.Core;

namespace Starve.Core.Tests;

public sealed class SlopeSpeedTests
{
    [Fact]
    public void MatchesServerGoldenVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "slope_speed_golden.json");
        var cases = System.Text.Json.JsonSerializer.Deserialize<List<SlopeGolden>>(File.ReadAllText(path))
                    ?? throw new InvalidOperationException("slope golden vectors are empty");

        foreach (var tc in cases)
        {
            float HeightAt(float x, float y) =>
                x == tc.WX && y == tc.WY ? tc.H0 : tc.H1;
            var got = SlopeSpeed.Factor(tc.WX, tc.WY, tc.DX, tc.DY, HeightAt);
            Assert.True(
                MathF.Abs(got - tc.WantFactor) <= 1e-6f,
                $"{tc.Name}: factor={got}, want={tc.WantFactor}");
        }
    }

    [Fact]
    public void OwnMovementSim_DownhillIsSlowerThanFlat()
    {
        var tm = new TileMap(2, 2,
            [1, 1, 1, 0, 0, 0, 0, 0, 0],
            [3, 3, 3, 3, 3, 3, 3, 3, 3]);

        var flat = new OwnMovementSim((_, _) => true);
        flat.SnapTo(0.5f, 0.5f);
        flat.SetSpeed(10);
        flat.SetIntent(0, 1);
        flat.Tick(50);

        var slope = new OwnMovementSim((_, _) => true) { HeightAt = tm.HeightAt };
        slope.SnapTo(0.5f, 0.5f);
        slope.SetSpeed(10);
        slope.SetIntent(0, 1);
        slope.Tick(50);

        Assert.Equal(1f, flat.Position.Y, 3);
        Assert.True(slope.Position.Y < flat.Position.Y - 0.05f,
            $"downhill y={slope.Position.Y} should be slower than flat y={flat.Position.Y}");
    }

    private sealed class SlopeGolden
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("wx")] public float WX { get; init; }
        [JsonPropertyName("wy")] public float WY { get; init; }
        [JsonPropertyName("dx")] public int DX { get; init; }
        [JsonPropertyName("dy")] public int DY { get; init; }
        [JsonPropertyName("h0")] public float H0 { get; init; }
        [JsonPropertyName("h1")] public float H1 { get; init; }
        [JsonPropertyName("want_factor")] public float WantFactor { get; init; }
    }
}

