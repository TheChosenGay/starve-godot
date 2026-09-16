using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 主场景性能采样：每秒写 jsonl，并挂本地网页。F3 面板读 <see cref="Latest"/>。
/// </summary>
public sealed class PerfMonitor : IDisposable
{
    public const int DefaultPort = 18765;
    public const string DirName = "perf";

    private readonly Process _proc = Process.GetCurrentProcess();
    private readonly CpuUsageMeter _cpu = new();
    private readonly List<PerfSnapshot> _history = [];
    private readonly object _gate = new();
    private readonly PerfSessionWriter _log;
    private readonly PerfServer? _server;
    // 逐帧耗时直方图：1 秒一条的采样看不出 frame pacing 问题（见 FrameTimeStats）。
    private readonly FrameTimeStats _frames = new();
    private double _accum;
    private bool _disposed;

    public string LogPath => _log.Path;
    public string LogDir { get; }
    public string? Url => _server?.Url;
    public PerfSnapshot Latest { get; private set; }

    /// <summary>当前窗口的帧时间统计（分位数/尖峰）。面板与网页读它。</summary>
    public FrameTimeReport FrameTime => _frames.Report();

    public PerfMonitor(string logDir, int port, bool startServer)
    {
        LogDir = logDir;
        Directory.CreateDirectory(logDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _log = new PerfSessionWriter(Path.Combine(logDir, $"starve-perf-{stamp}.jsonl"));
        if (startServer)
            _server = new PerfServer(port, this);
    }

    public static PerfMonitor? TryStart()
    {
        if (OS.GetCmdlineUserArgs().Contains("--smoke")) return null;
        if (System.Environment.GetEnvironmentVariable("STARVE_PERF") == "0") return null;
        var dir = Path.Combine(OS.GetUserDataDir(), DirName);
        var port = DefaultPort;
        if (int.TryParse(System.Environment.GetEnvironmentVariable("STARVE_PERF_PORT"), out var custom) &&
            custom is > 0 and < 65536)
            port = custom;
        return new PerfMonitor(dir, port, startServer: true);
    }

    public void Tick(double delta)
    {
        // 每帧都喂直方图（不是每秒一次）：抓尖峰必须用帧粒度。
        _frames.Add(delta * 1000.0);
        _accum += delta;
        if (_accum < 1.0) return;
        var wall = _accum;
        _accum = 0;
        var pacing = _frames.Report();
        // 位置插值/外推健康度：外推帧占比能直接反映"快照是否跟上"。
        // 若该比例持续偏高，说明服务端下发间隔不稳定或客户端渲染落后。
        var extrap = GodotClient.Game.GameRoot.SmootherExtrapolating;
        var total = GodotClient.Game.GameRoot.SmootherSamples;
        if (total > 0)
        {
            GD.Print($"SMOOTH 外推帧={extrap}/{total} ({(100.0 * extrap / total):F1}%)");
            GodotClient.Game.GameRoot.ResetSmootherStats();
        }
        var snap = Sample(wall) with
        {
            FrameMedianMs = pacing.MedianMs,
            FrameP95Ms = pacing.P95Ms,
            FrameWorstMs = pacing.WorstMs,
            FrameSpikeRatio = pacing.SpikeRatio,
        };
        Latest = snap;
        _log.Write(snap);
        lock (_gate)
        {
            _history.Add(snap);
            if (_history.Count > 600)
                _history.RemoveRange(0, _history.Count - 600);
        }
    }

    public PerfSnapshot[] CopyHistory()
    {
        lock (_gate)
            return _history.ToArray();
    }

    public string[] ListLogs()
    {
        if (!Directory.Exists(LogDir)) return [];
        var files = Directory.GetFiles(LogDir, "starve-perf-*.jsonl");
        Array.Sort(files, StringComparer.Ordinal);
        Array.Reverse(files);
        return files;
    }

    private PerfSnapshot Sample(double wallSeconds)
    {
        _proc.Refresh();
        var fps = (float)Engine.GetFramesPerSecond();
        if (fps < 0.01f)
            fps = (float)Performance.GetMonitor(Performance.Monitor.TimeFps);
        var processSec = (float)Performance.GetMonitor(Performance.Monitor.TimeProcess);
        var physicsSec = (float)Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess);
        return new PerfSnapshot(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            fps,
            fps > 0.01f ? 1000f / fps : processSec * 1000f,
            processSec * 1000f,
            physicsSec * 1000f,
            _cpu.Next(_proc.TotalProcessorTime, wallSeconds, System.Environment.ProcessorCount),
            _proc.WorkingSet64,
            GC.GetTotalMemory(false),
            (long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed),
            (long)Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed),
            (long)Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed),
            (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            (int)Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame),
            (int)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
            (int)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
            (int)Performance.GetMonitor(Performance.Monitor.ObjectCount))
        {
            // GC 计数为累计值：写进日志后用相邻样本求增量，即可判断
            // "每秒回收几次"以及是否与帧尖峰同时发生。
            GcGen0 = GC.CollectionCount(0),
            GcGen1 = GC.CollectionCount(1),
            GcGen2 = GC.CollectionCount(2),
            GcPauseMs = (long)GC.GetTotalPauseDuration().TotalMilliseconds,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server?.Dispose();
        _log.Dispose();
    }
}
