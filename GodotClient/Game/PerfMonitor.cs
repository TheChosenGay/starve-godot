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
    private double _accum;
    private bool _disposed;

    public string LogPath => _log.Path;
    public string LogDir { get; }
    public string? Url => _server?.Url;
    public PerfSnapshot Latest { get; private set; }

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
        _accum += delta;
        if (_accum < 1.0) return;
        var wall = _accum;
        _accum = 0;
        var snap = Sample(wall);
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
            (int)Performance.GetMonitor(Performance.Monitor.ObjectCount));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server?.Dispose();
        _log.Dispose();
    }
}
