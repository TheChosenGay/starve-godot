using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starve.Core;

/// <summary>一帧性能采样。写 jsonl、网页和面板共用同一份字段。</summary>
public readonly record struct PerfSnapshot(
    long UnixMs,
    float Fps,
    float FrameMs,
    float ProcessMs,
    float PhysicsMs,
    float CpuPercent,
    long WorkingSetBytes,
    long ManagedBytes,
    long VideoMemBytes,
    long TextureMemBytes,
    long BufferMemBytes,
    int DrawCalls,
    int RenderObjects,
    int Primitives,
    int NodeCount,
    int ObjectCount,
    // --- 帧节拍（frame pacing）：1 秒窗口内逐帧统计 ---
    // 平均 Fps 正常但不代表不卡；这组字段专门刻画"顿挫"。
    // 缺省 0 表示该次采样没带（旧日志/未启用），读取方需容忍。
    float FrameMedianMs = 0f,
    float FrameP95Ms = 0f,
    float FrameWorstMs = 0f,
    float FrameSpikeRatio = 0f);

public static class PerfSnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ToLine(in PerfSnapshot snap) =>
        JsonSerializer.Serialize(snap, Options);

    public static bool TryParse(string line, out PerfSnapshot snap)
    {
        snap = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<PerfSnapshot>(line, Options);
            if (parsed.UnixMs <= 0 && parsed.Fps <= 0) return false;
            snap = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        if (bytes < 1024L * 1024 * 1024)
            return (bytes / (1024f * 1024f)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (bytes / (1024f * 1024f * 1024f)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }
}

/// <summary>用进程 CPU 时间差算占用百分比（已除以逻辑核数，满载约 100）。</summary>
public sealed class CpuUsageMeter
{
    private TimeSpan _lastCpu;
    private bool _has;

    public float Next(TimeSpan totalProcessorTime, double wallSeconds, int processorCount)
    {
        if (!_has)
        {
            _lastCpu = totalProcessorTime;
            _has = true;
            return 0f;
        }

        var cpu = (totalProcessorTime - _lastCpu).TotalSeconds;
        _lastCpu = totalProcessorTime;
        var cores = Math.Max(processorCount, 1);
        var wall = Math.Max(wallSeconds, 1e-6);
        return (float)Math.Clamp(cpu / (wall * cores) * 100.0, 0, 100.0 * cores);
    }
}

/// <summary>把采样追加成 jsonl，方便直接打开或给网页读。</summary>
public sealed class PerfSessionWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private bool _disposed;

    public string Path { get; }

    public PerfSessionWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    public void Write(in PerfSnapshot snap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer.WriteLine(PerfSnapshotJson.ToLine(snap));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
    }
}
