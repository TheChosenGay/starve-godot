using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 逐帧移动追踪（诊断用，默认关闭）。
///
/// 打开：STARVE_MOVE_TRACE=1
///   STARVE_MOVE_TRACE_OUT  输出 csv（默认 /tmp/starve-move-trace.csv）
///   STARVE_MOVE_TRACE_MS   采集时长后自动退出（默认 10000）
///   STARVE_MOVE_TRACE_N    额外追踪的动态实体数（默认 2，0 = 只看自己）
///
/// 为什么需要它：帧率/GC/外推比例这些**每秒聚合**指标既看不到"逐帧位移不匀"，
/// 也看不到"客户端预测的速度和服务端说的速度对不上"。这里把三条线并排落成序列：
///
///   ① <b>本地预测</b>：locX/locY 逐帧位置 → locVX/locVY/locSpeed（逐帧实测速度）；
///   ② <b>服务端权威</b>：srvX/srvY（Position+Sub 连续位置）、srvVelX/srvVelY（服务端**声明**的速度）、
///      srvDirX/srvDirY/srvPath/srvStopped（服务端认为你在做什么 = "行为"）；
///   ③ <b>和解决定</b>：err（本地 vs 权威）、impliedLeadMs（把误差折算成多少毫秒领先）、
///      driftX/driftY/driftSpeed（**同一 tick 跨度**内本地走了多少 vs 服务端走了多少）、
///      decision/k/soft/hard（这次到底做了什么）。
///
/// ⚠️ 读法要点：
///   - **"本地现在" vs "服务端过去"天然差一个网络领先量**（≈ v×往返延迟），那不是失配。
///     真正判据是 drift*（同跨度位移差）：恒为 0 才叫预测正确。
///   - srvStepSpeed（服务端**实际**这一步走了多少）与 srvVelSpeed（服务端**声明**的速度）
///     不一致时，说明权威速度字段本身在撒谎（贴岸/被挡时）。
///   - rendY/dRendY 是"上下卡"的直接指标：横向位置被拉回时，角色站在高度场上会连带
///     上下跳，dRendY 的尖峰就是它。
///
/// 用法（对着本地 gate 跑一段自动行走）：
///   STARVE_MOVE_TRACE=1 STARVE_DEMO_MOVE=1,0 make run
/// </summary>
public static class MoveTrace
{
    private static readonly bool On =
        System.Environment.GetEnvironmentVariable("STARVE_MOVE_TRACE") == "1";

    private static readonly string OutPath =
        System.Environment.GetEnvironmentVariable("STARVE_MOVE_TRACE_OUT") ?? "/tmp/starve-move-trace.csv";

    private static readonly long DurationMs =
        long.TryParse(System.Environment.GetEnvironmentVariable("STARVE_MOVE_TRACE_MS"), out var d) ? d : 10000;

    private static readonly int MaxTracks =
        int.TryParse(System.Environment.GetEnvironmentVariable("STARVE_MOVE_TRACE_N"), out var n) ? n : 2;

    // 自己的列：顺序 = header 顺序 = AppendOwn 的写入顺序。
    private static readonly string[] OwnCols =
    [
        "canPredict", "ticked", "pending", "acked",
        "locX", "locY", "locVX", "locVY", "locSpeed", "locDirX", "locDirY", "locSlope",
        "rendX", "rendY", "rendZ", "dRendY", "tileH",
        "srvX", "srvY", "srvVelX", "srvVelY", "srvVelSpeed", "srvEffSpeed",
        "srvDirX", "srvDirY", "srvPath", "srvStopped",
        "srvStepX", "srvStepY", "srvStepSpeed", "stepTicks",
        "errX", "errY", "err", "impliedLeadMs",
        "driftX", "driftY", "driftSpeed",
        "decision", "k", "soft", "hard", "stopSettled",
    ];

    private const int OwnFixedCols = 4;   // canPredict..acked
    private const int OwnPredCols = 8;    // locX..locSlope
    private const int OwnRenderCols = 5;  // rendX..tileH
    // 和解块列数（由 header 顺序推出，改上面数组时要同步）。
    private const int OwnReconCols = 26;

    private static readonly Dictionary<ulong, Entry> Prev = new();
    private static readonly Dictionary<ulong, Entry> Cur = new();
    private static readonly List<ulong> Tracked = new();
    private static readonly StringBuilder Body = new();

    private static ulong _ownId;
    private static long _startMs;
    private static long _frames;
    private static float _camX;
    private static float _camY;
    private static bool _written;
    private static bool _snapThisFrame;

    // 自己的逐帧状态
    private static bool _canPredict;
    private static bool _ticked;
    private static ulong _pending;
    private static ulong _acked;
    private static int _locDirX;
    private static int _locDirY;
    private static float _locSlope = 1f;
    private static bool _hasOwnPrev;
    private static float _ownPrevX;
    private static float _ownPrevY;
    private static bool _hasReconcile;
    private static OwnMovementSim.ReconcileTrace _recon;

    /// <summary>ApplyWorld 被调用的次数（诊断）。</summary>
    public static long ApplyCount { get; private set; }

    /// <summary>其中"世界 tick 没推进"的次数（= 天气帧/配置帧等非位置消息触发的重放）。</summary>
    public static long ApplySameTick { get; private set; }

    private struct Entry
    {
        public float X;
        public float Y;
        public float Rx;
        public float Ry;
        public float Rz;
        public float TileH;
        public bool HasRendered;
        public bool Extrap;
        public float Blend;
        public double Dt;
        public long Tick;
        public int Samples;
    }

    public static bool Enabled => On;

    public static void FrameBegin(long nowMs, ulong ownId, float camX, float camY)
    {
        if (!On) return;
        if (_startMs == 0) _startMs = nowMs;
        _ownId = ownId;
        _camX = camX;
        _camY = camY;
        // 注意：不要在这里清 _snapThisFrame —— ApplyWorld 在本帧更早处已经置位了它。
        // 它在 FrameEnd 写完这一行之后清。
        Cur.Clear();
    }

    /// <summary>ApplyWorld 被调用（世界版本 +1）。tick 未推进 = 非位置消息触发的重放。</summary>
    public static void NoteApply(long tick, long prevTick)
    {
        if (!On) return;
        ApplyCount++;
        if (tick == prevTick) ApplySameTick++;
        _snapThisFrame = true;
    }

    /// <summary>自己的预测可用性与本地意图（每帧）。</summary>
    public static void OwnFrame(
        bool canPredict, bool ticked, ulong pending, ulong acked,
        int dirX, int dirY, float slope)
    {
        if (!On) return;
        _canPredict = canPredict;
        _ticked = ticked;
        _pending = pending;
        _acked = acked;
        _locDirX = dirX;
        _locDirY = dirY;
        _locSlope = slope;
    }

    /// <summary>本帧自己的和解快照（来自 <see cref="OwnMovementSim.LastReconcile"/>）。</summary>
    public static void OwnReconcile(in OwnMovementSim.ReconcileTrace trace)
    {
        if (!On) return;
        _recon = trace;
        _hasReconcile = true;
    }

    /// <summary>记录本帧某实体的**预测位置**（喂给 WorldTo3D 之前的世界格坐标）。</summary>
    public static void Sample(
        ulong id, float x, float y, bool extrap, float blend,
        double dt = 0, long tick = 0, int samples = 0)
    {
        if (!On) return;
        if (!Cur.TryGetValue(id, out var e)) e = default;
        e.X = x;
        e.Y = y;
        e.Extrap = extrap;
        e.Blend = blend;
        e.Dt = dt;
        e.Tick = tick;
        e.Samples = samples;
        Cur[id] = e;
    }

    /// <summary>记录本帧某实体**真正画到 3D 里的位置**与该点地形高度（每帧）。</summary>
    public static void SampleRendered(ulong id, float x, float y, float z, float tileH)
    {
        if (!On) return;
        if (!Cur.TryGetValue(id, out var e)) e = default;
        e.Rx = x;
        e.Ry = y;
        e.Rz = z;
        e.TileH = tileH;
        e.HasRendered = true;
        Cur[id] = e;
    }

    public static void FrameEnd(long nowMs, double deltaMs)
    {
        if (!On) return;
        _frames++;

        // 选追踪对象：自己 + 前 N 个"确实动过"的其他实体（只选一次，之后固定）。
        if (Tracked.Count < MaxTracks + 1)
        {
            foreach (var (id, e) in Cur)
            {
                if (Tracked.Contains(id)) continue;
                if (id == _ownId)
                {
                    Tracked.Insert(0, id);
                    continue;
                }
                if (Tracked.Count > MaxTracks) break;
                // 样本数 ≥2 说明插值器存过第二个位置 = 真的动过。
                if (e.Samples >= 2) Tracked.Add(id);
            }
        }

        var sb = new StringBuilder(384);
        sb.Append(_frames).Append(',')
          .Append(nowMs - _startMs).Append(',')
          .Append(Num(deltaMs)).Append(',')
          .Append(_snapThisFrame ? '1' : '0');

        AppendOwn(sb);
        foreach (var id in Tracked)
        {
            if (!Cur.TryGetValue(id, out var e))
            {
                sb.Append(",,,,,,,,");
                continue;
            }
            sb.Append(',')
              .Append(Num(e.X)).Append(',').Append(Num(e.Y)).Append(',')
              .Append(Num(e.X - _camX)).Append(',').Append(Num(e.Y - _camY)).Append(',')
              .Append(e.Extrap ? '1' : '0').Append(',')
              .Append(Num(e.Dt)).Append(',').Append(e.Tick).Append(',').Append(e.Samples);
        }
        Body.Append(sb).Append('\n');
        _snapThisFrame = false;

        Prev.Clear();
        foreach (var (id, e) in Cur) Prev[id] = e;
    }

    private static void AppendOwn(StringBuilder sb)
    {
        // 逐帧预测速度：用相邻两帧的预测位置差分（这才是"看起来的速度"）。
        var hasOwn = Cur.TryGetValue(_ownId, out var own);
        float vx = 0f, vy = 0f, speed = 0f;
        float dRendY = 0f;
        if (hasOwn)
        {
            if (_hasOwnPrev)
            {
                vx = own.X - _ownPrevX;
                vy = own.Y - _ownPrevY;
                speed = MathF.Sqrt(vx * vx + vy * vy);
            }
            if (own.HasRendered && Prev.TryGetValue(_ownId, out var po) && po.HasRendered)
                dRendY = own.Ry - po.Ry;
            _ownPrevX = own.X;
            _ownPrevY = own.Y;
            _hasOwnPrev = true;
        }

        void F(double v) => sb.Append(',').Append(Num(v));

        sb.Append(',').Append(_canPredict ? '1' : '0')
          .Append(',').Append(_ticked ? '1' : '0')
          .Append(',').Append(_pending)
          .Append(',').Append(_acked);

        if (hasOwn)
        {
            F(own.X); F(own.Y); F(vx); F(vy); F(speed);
            sb.Append(',').Append(_locDirX).Append(',').Append(_locDirY);
            F(_locSlope);
            if (own.HasRendered)
            {
                F(own.Rx); F(own.Ry); F(own.Rz); F(dRendY); F(own.TileH);
            }
            else
            {
                sb.Append(new string(',', OwnRenderCols));
            }
        }
        else
        {
            sb.Append(new string(',', OwnPredCols + OwnRenderCols));
        }

        if (!_hasReconcile)
        {
            sb.Append(new string(',', OwnReconCols));
            return;
        }

        var r = _recon;
        F(r.ServerX); F(r.ServerY); F(r.ServerVelX); F(r.ServerVelY); F(r.ServerVelSpeed);
        F(r.ServerEffectiveSpeed);
        sb.Append(',').Append(r.ServerDirX).Append(',').Append(r.ServerDirY)
          .Append(',').Append(r.ServerPathLen)
          .Append(',').Append(r.ServerStopped ? '1' : '0');
        F(r.ServerStepX); F(r.ServerStepY); F(r.ServerStepSpeed);
        sb.Append(',').Append(r.StepTicks);
        F(r.ErrX); F(r.ErrY); F(r.Err); F(r.ImpliedLeadMs);
        F(r.DriftX); F(r.DriftY); F(r.DriftSpeed);
        sb.Append(',').Append(r.Decision.ToString());
        F(r.K);
        sb.Append(',').Append(r.SoftCorrections).Append(',').Append(r.HardSnaps)
          .Append(',').Append(r.StopSettled ? '1' : '0');
    }

    private static string Num(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    public static bool ShouldQuit(long nowMs) => On && _startMs != 0 && nowMs - _startMs >= DurationMs;

    public static void Finish()
    {
        if (!On || _written) return;
        _written = true;
        var head = new StringBuilder("frame,tMs,deltaMs,snap");
        foreach (var c in OwnCols) head.Append(',').Append(c);
        foreach (var id in Tracked)
            head.Append($",e{id}_x,e{id}_y,e{id}_sx,e{id}_sy,e{id}_ex,e{id}_dt,e{id}_tick,e{id}_n");
        head.Append('\n').Append("own=").Append(_ownId).Append('\n');
        try
        {
            File.WriteAllText(OutPath, head + Body.ToString());
            GD.Print($"MOVE_TRACE 写入 {OutPath}（{_frames} 帧，追踪 {Tracked.Count} 个实体，" +
                     $"ApplyWorld={ApplyCount} 其中 tick 未推进={ApplySameTick}）");
        }
        catch (Exception ex)
        {
            GD.PushError($"MOVE_TRACE 写文件失败：{ex.Message}");
        }
    }
}
