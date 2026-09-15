using System;
using System.Collections.Generic;

namespace Starve.Core;

/// <summary>ORCA 求解输入：一个智能体的当前速度与期望速度。</summary>
public struct OrcaAgent
{
    public float X, Z;          // 位置（格）
    public float VX, VY;        // 当前速度（格/秒）
    public float PrefVX, PrefVY;// 期望速度（格/秒）
    public float Radius;        // 截面半径（格）
    public float MaxSpeed;      // 速度上限（格/秒）
}

/// <summary>ORCA 邻居（别的智能体）。</summary>
public struct OrcaBody
{
    public float X, Z;
    public float VX, VY;
    public float Radius;
    public float MaxSpeed;
}

/// <summary>ORCA 求解参数（必须与服务端 <c>systems.ORCAOptions</c> 取值一致）。</summary>
public struct OrcaOptions
{
    public float TimeHorizon;
    public float CollabCoeff;
    public float SafetyMargin;

    /// <summary>
    /// 必须与服务端 systems.DefaultORCAOptions 逐项一致——两边只要有一项不同，
    /// 本地预测就会与服务端分叉（表现为被快照反复校正）。
    ///
    /// τ=0.5s 的取舍见服务端注释：τ 决定"提前多久开始避让"，
    /// 也决定邻居查询半径（≈2×速度×τ）。τ 越大越从容，但耗时同步放大。
    /// </summary>
    public static OrcaOptions Default => new()
    {
        TimeHorizon = 0.5f,
        CollabCoeff = 0.5f,
        SafetyMargin = 0.02f,
    };
}

/// <summary>
/// ORCA（Optimal Reciprocal Collision Avoidance）避障——**服务端 <c>systems/orca.go</c> 的逐行移植**。
///
/// 为什么客户端也要有一份：服务端每个 tick 用 ORCA 决定玩家实际怎么绕开动物；
/// 客户端本地预测若不做同一件事，就会出现"服务端绕开了、客户端直着走"，
/// 然后被快照一次次拉回——正是之前修掉的"停下抖动"那一类问题。
///
/// 两边必须**同参数、同顺序、同公式**：
///   - 参数来自 <see cref="OrcaOptions.Default"/>（与服务端 DefaultORCAOptions 对齐）；
///   - 邻居按 (X, Z, Radius) 排序（LP 是增量式的，顺序影响退化情形的解）；
///   - 对称打破用实体 id 的奇偶（见 <see cref="BreakSymmetry"/>）。
///
/// 半平面用 (point, direction) 表示，与 RVO2 同构：
/// 允许速度集合 = { v | det(direction, point - v) ≤ 0 }。
/// </summary>
public sealed class OrcaAvoidance
{
    private const float Epsilon = 1e-12f;

    private struct Line
    {
        public float PX, PY;   // point
        public float DX, DY;   // direction
    }

    public OrcaOptions Options { get; }
    /// <summary>对称打破：正面对撞时按 id 分侧，避免双方停死。</summary>
    public bool BreakSymmetry { get; }
    /// <summary>自己的稳定身份（实体 id），决定往哪一侧让。</summary>
    public ulong SymmetryKey { get; }

    public OrcaAvoidance(OrcaOptions options, bool breakSymmetry, ulong symmetryKey)
    {
        Options = options.TimeHorizon > 0 ? options : OrcaOptions.Default;
        if (Options.CollabCoeff <= 0) Options = new OrcaOptions
        {
            TimeHorizon = Options.TimeHorizon,
            CollabCoeff = OrcaOptions.Default.CollabCoeff,
            SafetyMargin = Options.SafetyMargin,
        };
        BreakSymmetry = breakSymmetry;
        SymmetryKey = symmetryKey;
    }

    private static float Det(float ax, float ay, float bx, float by) => ax * by - ay * bx;

    /// <summary>求避让后的安全速度（方向可任意）。邻居顺序不影响结果。</summary>
    public void Solve(in OrcaAgent self, IReadOnlyList<OrcaBody> neighbors, out float vx, out float vy)
    {
        var maxSpeed = self.MaxSpeed > 0 ? self.MaxSpeed : MathF.Sqrt(self.PrefVX * self.PrefVX + self.PrefVY * self.PrefVY);
        if (maxSpeed <= 0f) { vx = 0f; vy = 0f; return; }

        ClampLen(self.PrefVX, self.PrefVY, maxSpeed, out var prefX, out var prefY);
        if (neighbors.Count == 0) { vx = prefX; vy = prefY; return; }

        // 邻居排序：与服务端一致的确定性顺序。
        var sorted = new List<OrcaBody>(neighbors);
        sorted.Sort((a, b) =>
        {
            if (a.X != b.X) return a.X.CompareTo(b.X);
            if (a.Z != b.Z) return a.Z.CompareTo(b.Z);
            return a.Radius.CompareTo(b.Radius);
        });

        var lines = new List<Line>(sorted.Count);
        foreach (var n in sorted)
        {
            if (AgentLine(self, n, out var line)) lines.Add(line);
        }
        if (lines.Count == 0) { vx = prefX; vy = prefY; return; }

        LinearProgram2(lines, maxSpeed, prefX, prefY, out vx, out vy);
    }

    /// <summary>为一个邻居构造 ORCA 半平面（对齐 RVO2 的 Agent::computeNewVelocity）。</summary>
    private bool AgentLine(in OrcaAgent self, in OrcaBody n, out Line line)
    {
        line = default;
        var invTau = 1f / Options.TimeHorizon;
        var relX = n.X - self.X;
        var relZ = n.Z - self.Z;
        var relVX = self.VX - n.VX;
        var relVZ = self.VY - n.VY;
        var combined = self.Radius + n.Radius + Options.SafetyMargin;
        var combinedSq = combined * combined;
        var distSq = relX * relX + relZ * relZ;

        float ux, uz, dirX, dirY;

        if (distSq > combinedSq)
        {
            var wX = relVX - invTau * relX;
            var wZ = relVZ - invTau * relZ;
            var wLenSq = wX * wX + wZ * wZ;
            var dot1 = wX * relX + wZ * relZ;

            if (dot1 < 0 && dot1 * dot1 > combinedSq * wLenSq)
            {
                var wLen = MathF.Sqrt(wLenSq);
                if (wLen <= Epsilon) return false;
                var unitWX = wX / wLen;
                var unitWZ = wZ / wLen;
                dirX = unitWZ; dirY = -unitWX;
                ux = (combined * invTau - wLen) * unitWX;
                uz = (combined * invTau - wLen) * unitWZ;
            }
            else
            {
                var leg = MathF.Sqrt(distSq - combinedSq);
                // 对称打破：正面对撞时 det 恰为 0（完全共线），两人会选同一条 leg
                // → 往同一侧让 → 仍撞上/停死。按实体 id 奇偶加极小偏置，
                // 让一方选左腿、另一方选右腿。偏置远小于正常 det，不影响非对称情形。
                // 与服务端 systems/orca.go 的 side 偏置逐位对应。
                var side = Det(relX, relZ, wX, wZ);
                if (BreakSymmetry)
                {
                    side += (SymmetryKey % 2 == 0) ? -1e-9f : 1e-9f;
                }
                if (side > 0)
                {
                    dirX = (relX * leg - relZ * combined) / distSq;
                    dirY = (relX * combined + relZ * leg) / distSq;
                }
                else
                {
                    dirX = -(relX * leg + relZ * combined) / distSq;
                    dirY = -(-relX * combined + relZ * leg) / distSq;
                }
                var dot2 = relVX * dirX + relVZ * dirY;
                ux = dot2 * dirX - relVX;
                uz = dot2 * dirY - relVZ;
            }
        }
        else
        {
            var d = MathF.Sqrt(distSq);
            if (d <= Epsilon)
            {
                line = new Line { PX = self.VX + combined * invTau * 0.5f, PY = self.VY, DX = 0f, DY = 1f };
                return true;
            }
            ux = relX / d; uz = relZ / d;
            dirX = uz; dirY = -ux;
            var shortfall = combined * invTau - (-(relVX * ux + relVZ * uz));
            ux *= shortfall; uz *= shortfall;
        }

        var dirLen = MathF.Sqrt(dirX * dirX + dirY * dirY);
        if (dirLen <= Epsilon) return false;
        dirX /= dirLen; dirY /= dirLen;

        var k = Options.CollabCoeff;
        line = new Line
        {
            PX = self.VX + k * ux,
            PY = self.VY + k * uz,
            DX = dirX,
            DY = dirY,
        };
        return true;
    }

    /// <summary>增量式二维线性规划：求最接近期望速度的可行点（对齐 RVO2 linearProgram2）。</summary>
    private void LinearProgram2(List<Line> lines, float maxSpeed, float prefX, float prefY,
        out float vx, out float vy)
    {
        ClampLen(prefX, prefY, maxSpeed, out var bestX, out var bestY);
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (Det(l.DX, l.DY, l.PX - bestX, l.PY - bestY) <= 0f) continue; // 满足约束
            if (!LinearProgram1(lines, i, maxSpeed, prefX, prefY, out var nx, out var ny))
            {
                ProjectOntoLine(l, bestX, bestY, out nx, out ny);
                ClampLen(nx, ny, maxSpeed, out nx, out ny);
            }
            bestX = nx; bestY = ny;
        }
        vx = bestX; vy = bestY;
    }

    /// <summary>在第 lineNo 条约束边界上求最优点（对齐 RVO2 linearProgram1）。</summary>
    private static bool LinearProgram1(List<Line> lines, int lineNo, float radius,
        float optX, float optY, out float rx, out float ry)
    {
        rx = 0f; ry = 0f;
        var l = lines[lineNo];
        var dotProduct = l.PX * l.DX + l.PY * l.DY;
        var disc = dotProduct * dotProduct + radius * radius - (l.PX * l.PX + l.PY * l.PY);
        if (disc < 0f) return false;

        var sqrtDisc = MathF.Sqrt(disc);
        var tLeft = -dotProduct - sqrtDisc;
        var tRight = -dotProduct + sqrtDisc;

        for (var i = 0; i < lineNo; i++)
        {
            var li = lines[i];
            var denom = Det(l.DX, l.DY, li.DX, li.DY);
            var num = Det(li.DX, li.DY, l.PX - li.PX, l.PY - li.PY);
            if (MathF.Abs(denom) <= Epsilon)
            {
                if (num < 0f) return false;
                continue;
            }
            var t = num / denom;
            if (denom >= 0f) { if (t < tRight) tRight = t; }
            else { if (t > tLeft) tLeft = t; }
            if (tLeft > tRight) return false;
        }

        var tc = l.DX * (optX - l.PX) + l.DY * (optY - l.PY);
        if (tc < tLeft) tc = tLeft;
        else if (tc > tRight) tc = tRight;
        rx = l.PX + tc * l.DX;
        ry = l.PY + tc * l.DY;
        return true;
    }

    /// <summary>把点投到约束边界上（无可行解时的兜底）。</summary>
    private static void ProjectOntoLine(in Line l, float x, float y, out float rx, out float ry)
    {
        var d2 = l.DX * l.DX + l.DY * l.DY;
        if (d2 <= Epsilon) { rx = x; ry = y; return; }
        var px = x - l.PX;
        var py = y - l.PY;
        var proj = (px * l.DX + py * l.DY) / d2;
        rx = l.PX + proj * l.DX;
        ry = l.PY + proj * l.DY;
    }

    private static void ClampLen(float x, float y, float maxLen, out float rx, out float ry)
    {
        var l = MathF.Sqrt(x * x + y * y);
        if (l <= maxLen || l <= Epsilon) { rx = x; ry = y; return; }
        rx = x / l * maxLen;
        ry = y / l * maxLen;
    }
}

/// <summary>
/// ORCA 邻居在客户端的形态（位置 + 速度 + 半径 + 胶囊半长）。
/// 由快照刷新：只有**动态**实体（玩家/动物）进这个列表；
/// 静态障碍走硬碰撞（MovementSlide），不参与 ORCA。
/// </summary>
public struct OrcaNeighbor
{
    public float X, Y;        // 位置（格）
    public float VX, VY;      // 当前速度（格/秒）
    public float Radius;      // 截面半径（格）
    public float HalfLength;  // 胶囊半长（0 = 圆柱）
    public float MaxSpeed;    // 速度上限
}
