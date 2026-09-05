using Starve.Core;

namespace Starve.Core.Tests;

public sealed class PerfSnapshotTests
{
    [Fact]
    public void JsonLine_Roundtrips()
    {
        var snap = new PerfSnapshot(
            1_725_528_000_000,
            59.8f,
            16.7f,
            4.2f,
            1.1f,
            12.5f,
            400_000_000,
            80_000_000,
            120_000_000,
            90_000_000,
            8_000_000,
            210,
            80,
            12_000,
            340,
            1200);

        Assert.True(PerfSnapshotJson.TryParse(PerfSnapshotJson.ToLine(snap), out var parsed));
        Assert.Equal(snap.UnixMs, parsed.UnixMs);
        Assert.Equal(snap.Fps, parsed.Fps, 2);
        Assert.Equal(snap.CpuPercent, parsed.CpuPercent, 2);
        Assert.Equal(snap.DrawCalls, parsed.DrawCalls);
        Assert.Equal(snap.WorkingSetBytes, parsed.WorkingSetBytes);
    }

    [Fact]
    public void CpuUsageMeter_ReportsHalfCoreAsExpected()
    {
        var meter = new CpuUsageMeter();
        Assert.Equal(0f, meter.Next(TimeSpan.Zero, 1, 4));
        var next = meter.Next(TimeSpan.FromSeconds(0.5), 1, 4);
        Assert.InRange(next, 12.4f, 12.6f);
    }

    [Fact]
    public void SessionWriter_AppendsJsonl()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"starve-perf-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var writer = new PerfSessionWriter(path))
            {
                writer.Write(new PerfSnapshot(10, 60, 16, 3, 1, 8, 1, 1, 1, 1, 1, 2, 3, 4, 5, 6));
                writer.Write(new PerfSnapshot(11, 58, 17, 4, 1, 9, 1, 1, 1, 1, 1, 2, 3, 4, 5, 6));
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.True(PerfSnapshotJson.TryParse(lines[1], out var second));
            Assert.Equal(58, second.Fps, 1);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FormatBytes_UsesMb()
    {
        Assert.Equal("1.5 MB", PerfSnapshotJson.FormatBytes((long)(1.5 * 1024 * 1024)));
    }
}
