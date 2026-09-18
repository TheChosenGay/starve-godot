using System;
using System.Globalization;
using System.IO;
using System.Text;
using Starve.Netcode;

namespace GodotClient.Game;

/// <summary>
/// 把组件（<c>ClientSmoother</c>）的指标落到**自己的 jsonl**：每次和解一行，时钟重同步一行。
///
/// 为什么要独立一份日志：性能日志（perf jsonl）是 1 秒一条的采样，而"这一下为什么卡"
/// 需要**逐次和解**的因果链（快照 tick / 已消费 seq / 误差 / 重放多少 tick / 走了哪种校正）。
/// 排查时把它和 `MoveTrace` 的逐帧 csv 对着看即可。
///
/// 只在 <c>STARVE_NETCODE_LOG=1</c> 或 perf 已启用时写（默认跟 perf 同目录）。
/// </summary>
public sealed class NetcodeMetrics : INetcodeMetrics, IDisposable
{
    private readonly StreamWriter _writer;
    /// <summary>本帧客户端"最近一步走了多少 / 当时的坡度因子 / 服务端有效速度"（诊断用）。</summary>
    private float _stepDistance;
    private float _slope = 1f;
    private float _effSpeed;

    /// <summary>喂入客户端最近一次整 tick 步进（位移 + 坡度因子 + 服务端有效速度）。</summary>
    public void NoteStep(float distance, float slope, float effSpeed)
    {
        _stepDistance = distance;
        _slope = slope;
        _effSpeed = effSpeed;
    }
    private readonly StringBuilder _line = new(256);
    private bool _disposed;

    public string Path { get; }

    public NetcodeMetrics(string dir)
    {
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        Path = System.IO.Path.Combine(dir, $"starve-netcode-{stamp}.jsonl");
        _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    public void OnCorrection(in CorrectionReport r)
    {
        _line.Clear();
        _line.Append("{\"t\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _line.Append(",\"k\":\"").Append(r.Kind).Append('"');
        _line.Append(",\"snapTick\":").Append(r.SnapshotTick);
        _line.Append(",\"stateTick\":").Append(r.StateTick);
        _line.Append(",\"ver\":").Append(r.StateVersion);
        _line.Append(",\"seq\":").Append(r.AppliedSeq);
        _line.Append(",\"err\":").Append(F(r.Err));
        _line.Append(",\"replay\":").Append(r.ReplayedTicks);
        _line.Append(",\"clamped\":").Append(r.ReplayClamped ? "true" : "false");
        _line.Append(",\"vmismatch\":").Append(r.VersionMismatch ? "true" : "false");
        _line.Append(",\"predSteps\":").Append(r.PredictedSteps);
        _line.Append(",\"predSpeed\":").Append(F(r.PredictedStepSpeed));
        _line.Append(",\"authSpeed\":").Append(F(r.AuthoritativeStepSpeed));
        _line.Append(",\"starved\":").Append(r.Starved ? "true" : "false");
        _line.Append(",\"blendTicks\":").Append(r.BlendTicks);
        // 同跨度位移对比：我走了多少 vs 服务端走了多少（判"速度差"还是"方向差"）
        _line.Append(",\"cmv\":").Append(F(r.ClientMove));
        _line.Append(",\"smv\":").Append(F(r.ServerMove));
        _line.Append(",\"dops\":").Append(r.DriftOps);
        _line.Append(",\"csteps\":").Append(r.ClientSteps);
        _line.Append(",\"step\":").Append(F(_stepDistance));
        // 客户端自己的坡度因子 vs 服务端的有效速度：判"两边坡度模型是否一致"
        _line.Append(",\"slope\":").Append(F(_slope));
        _line.Append(",\"eff\":").Append(F(_effSpeed));
        _line.Append('}');
        Write(_line);
    }

    /// <summary>别人（远端实体）的采样量太大，默认不逐条记：需要时按需打开。</summary>
    public void OnRemoteSample(ulong id, in RemoteSampleReport r)
    {
        if (Environment.GetEnvironmentVariable("STARVE_NETCODE_REMOTE_LOG") != "1") return;
        _line.Clear();
        _line.Append("{\"t\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _line.Append(",\"k\":\"remote\",\"id\":").Append(id);
        _line.Append(",\"playout\":").Append(F((float)r.PlayoutTick));
        _line.Append(",\"kind\":\"").Append(r.Kind).Append('"');
        _line.Append(",\"buffered\":").Append(r.Buffered);
        _line.Append(",\"starved\":").Append(r.Starved ? "true" : "false");
        _line.Append('}');
        Write(_line);
    }

    public void OnClockResync(long serverTick, long nowMs, double offsetMs)
    {
        _line.Clear();
        _line.Append("{\"t\":").Append(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _line.Append(",\"k\":\"clock_resync\"");
        _line.Append(",\"snapTick\":").Append(serverTick);
        _line.Append(",\"nowMs\":").Append(nowMs);
        _line.Append(",\"offsetMs\":").Append(F((float)offsetMs));
        _line.Append('}');
        Write(_line);
    }

    private void Write(StringBuilder line)
    {
        if (_disposed) return;
        // 极端情况：进程退出竞态 —— 宁可丢一行日志，也不要在这里抛进游戏逻辑
        try
        {
            _writer.WriteLine(line.ToString());
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
    }
}
