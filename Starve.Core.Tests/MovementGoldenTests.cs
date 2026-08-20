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
            var blocked = vector.Blocked.Select(cell => (X: cell[0], Y: cell[1])).ToHashSet();
            var sim = new OwnMovementSim((x, y) => !blocked.Contains((x, y)));
            sim.SnapTo(vector.StartX, vector.StartY);
            sim.SetSpeed(vector.Speed);
            sim.SetIntent(vector.DX, vector.DY);
            sim.Tick(vector.DTMS);

            Assert.True(
                MathF.Abs(sim.Position.X - vector.WantX) <= 1e-5f,
                $"{vector.Name}: x={sim.Position.X}, want={vector.WantX}");
            Assert.True(
                MathF.Abs(sim.Position.Y - vector.WantY) <= 1e-5f,
                $"{vector.Name}: y={sim.Position.Y}, want={vector.WantY}");
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
        [JsonPropertyName("blocked")] public int[][] Blocked { get; init; } = [];
        [JsonPropertyName("want_x")] public float WantX { get; init; }
        [JsonPropertyName("want_y")] public float WantY { get; init; }
    }
}
