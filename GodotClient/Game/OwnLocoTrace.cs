using System;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 自己角色的**走路表现**逐帧追踪（诊断用，默认关闭）：STARVE_LOCO_TRACE=1
///
///   STARVE_LOCO_TRACE_OUT   输出 csv（默认 /tmp/starve-loco-trace.csv）
///
/// 为什么单独一条：角色动画的"走/停"和速度**不是**来自输入，而是来自
/// <see cref="LocomotionPresentation.FromDisplacement"/>：它拿**这一帧渲染位置的位移**算速度，
/// 低于 <see cref="LocomotionPresentation.StopTilesPerSec"/>(0.8 格/秒) 就判"没在走" ⇒
/// <c>SetLocomotion(moving:false)</c> ⇒ 片段切 "idle"；下一帧位移恢复 ⇒ 再切回 "walk" ⇒
/// <c>Play("walk")</c> **从第 0 帧重播**（同名片不重播，跨片才重播）。
///
/// 所以只要渲染位移出现**单帧凹坑**（典型来源：和解的残差平滑正在把位移吃掉、
/// 或整 tick 步进与帧余量外推在某一帧对不齐），走路片段就会被反复打断 —— 视觉上就是
/// "一卡一卡"，而**位置本身可能完全正常**。这条日志就是用来区分这两件事的：
/// 把 locoMoving / 本帧位移 / 动画速度 与 netcode jsonl 的校正时间戳对齐即可。
///
/// 列：t, dx, dy, dtMs, speed, moving, animSpeed, clip, intentX, intentY, posX, posY
/// </summary>
public static class OwnLocoTrace
{
    private static readonly bool On =
        System.Environment.GetEnvironmentVariable("STARVE_LOCO_TRACE") == "1";

    private static readonly string OutPath =
        System.Environment.GetEnvironmentVariable("STARVE_LOCO_TRACE_OUT") ?? "/tmp/starve-loco-trace.csv";

    /// <summary>
    /// 采集多久后自动退出（毫秒，0 = 不退出）。**必须给个有限值**：
    /// 曾经因为只设了 MOVE_TRACE_MS 而漏了 STARVE_MOVE_TRACE=1，自动退出没生效，
    /// 两个"自动行走"客户端一直挂在 gate 上抢同一个玩家实体，把真人操作挤成走不动。
    /// </summary>
    private static readonly long DurationMs =
        long.TryParse(System.Environment.GetEnvironmentVariable("STARVE_LOCO_TRACE_MS"), out var d) ? d : 0;

    private static long _startedAt;

    /// <summary>采集到位了就该退出（调用方负责 GetTree().Quit()）。</summary>
    public static bool Expired(long nowMs)
    {
        if (!On || DurationMs <= 0) return false;
        if (_startedAt == 0) { _startedAt = nowMs; return false; }
        return nowMs - _startedAt >= DurationMs;
    }

    private static StreamWriter? _writer;
    private static double _tickTarget;
    private static float _offsetDist;
    private static float _blendWeight;
    private static readonly StringBuilder Line = new(160);

    /// <summary>本帧是否把角色从"走"判成"停"（= 动画被打断的那一下）。统计用。</summary>
    public static int Flickers { get; private set; }
    public static int Frames { get; private set; }

    /// <summary>本帧从组件里取到的"渲染时基"（在 GameRoot 里每帧调一次）。</summary>
    public static void NoteClock(double tickTarget, float offsetDist, float blendWeight)
    {
        if (!On) return;
        _tickTarget = tickTarget;
        _offsetDist = offsetDist;
        _blendWeight = blendWeight;
    }

    public static void Sample(
        long nowMs, float dx, float dy, float deltaMs,
        in LocomotionSample loco, int intentX, int intentY, float posX, float posY)
    {
        if (!On) return;
        EnsureOpen();

        var speed = deltaMs > 1f ? MathF.Sqrt(dx * dx + dy * dy) * 1000f / deltaMs : 0f;
        // "本帧被判停、但意图还在动" = 动画被打断（玩家按着键却播了 idle）
        var interrupted = !loco.Moving && (intentX != 0 || intentY != 0);
        if (interrupted) Flickers++;
        Frames++;

        Line.Clear();
        Line.Append(nowMs).Append(',')
            .Append(F(dx)).Append(',').Append(F(dy)).Append(',')
            .Append(F(deltaMs)).Append(',')
            .Append(F(speed)).Append(',')
            .Append(loco.Moving ? '1' : '0').Append(',')
            .Append(F(loco.TilesPerSec)).Append(',')
            .Append(loco.Moving ? "walk" : "idle").Append(',')
            .Append(intentX).Append(',').Append(intentY).Append(',')
            .Append(F(posX)).Append(',').Append(F(posY)).Append(',')
            .Append(_tickTarget.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
            .Append(F(_offsetDist)).Append(',').Append(F(_blendWeight));
        _writer!.WriteLine(Line.ToString());
    }

    public static void Finish()
    {
        if (_writer is null) return;
        try
        {
            _writer.Flush();
            _writer.Dispose();
        }
        catch (Exception)
        {
            // 退出竞态：日志丢了就丢了，绝不在这里抛进游戏逻辑
        }
        _writer = null;
        GD.Print($"LOCO 逐帧追踪：{Frames} 帧，其中\"按着键却被判停\"={Flickers} 帧（{OutPath}）");
    }

    private static void EnsureOpen()
    {
        if (_writer is not null) return;
        _writer = new StreamWriter(new FileStream(OutPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        _writer.WriteLine("t,dx,dy,dtMs,speed,moving,animSpeed,clip,intentX,intentY,posX,posY,tickTarget,offsetDist,blendWeight");
    }

    private static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
}
