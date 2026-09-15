using System;
using System.Collections.Generic;

namespace Starve.Core;

/// <summary>形状种类（与服务端 components.CollideShape 对齐）。</summary>
public enum BlockerKind
{
    /// <summary>格心圆：树/岩。</summary>
    Circle = 0,
    /// <summary>占格盒：建筑/工作站。</summary>
    Box = 1,
    /// <summary>沿朝向铺开的胶囊：玩家/四足动物。</summary>
    Capsule = 2,
}

/// <summary>
/// 碰撞形状（与服务端 components.Collide 一一对应）：
/// 格心圆（树/岩）、占格盒（建筑/工作站）、胶囊（移动体）。
///
/// 注意：这里只放**静态**障碍（静态滑掠用）。动态体之间的避让走 ORCA
/// （见 <see cref="OrcaAvoidance"/>），不是硬碰撞——这正是服务端三阶段的设计。
/// </summary>
public readonly record struct BlockerShape(
    BlockerKind Kind, float X, float Y, float R, float W, float H,
    float HalfLength = 0f, float FaceX = 1f, float FaceZ = 0f, ulong Owner = 0)
{
    /// <summary>是否盒（决定扫掠分支）。</summary>
    public bool IsBox => Kind == BlockerKind.Box;
    /// <summary>是否胶囊。</summary>
    public bool IsCapsule => Kind == BlockerKind.Capsule;

    /// <summary>格心圆：中心为世界坐标（Position + 0.5），半径单位=格。</summary>
    public static BlockerShape Circle(float x, float y, float r) =>
        new(BlockerKind.Circle, x, y, r, 0f, 0f);

    /// <summary>占格盒：左上角锚点为 Position，尺寸单位=格。</summary>
    public static BlockerShape Box(float x, float y, float w, float h) =>
        new(BlockerKind.Box, x, y, 0f, w, h);

    /// <summary>胶囊：中心 (x,y)，半径 r，沿 (faceX,faceZ) 铺开半长 halfLength。</summary>
    public static BlockerShape Capsule(float x, float y, float r, float halfLength,
        float faceX, float faceZ, ulong owner) =>
        new(BlockerKind.Capsule, x, y, r, 0f, 0f, halfLength, faceX, faceZ, owner);
}

/// <summary>胶囊的两个端点（由中心、半长、朝向算出）。</summary>
public readonly record struct CapsuleSegment(float AX, float AY, float BX, float BY, float R)
{
    public static CapsuleSegment From(BlockerShape s)
    {
        var len = MathF.Sqrt(s.FaceX * s.FaceX + s.FaceZ * s.FaceZ);
        var fx = len > 1e-6f ? s.FaceX / len : 1f;
        var fz = len > 1e-6f ? s.FaceZ / len : 0f;
        return new CapsuleSegment(
            s.X - fx * s.HalfLength, s.Y - fz * s.HalfLength,
            s.X + fx * s.HalfLength, s.Y + fz * s.HalfLength, s.R);
    }
}

/// <summary>
/// 移动形状解算：圆沿位移连续扫掠（不穿墙），命中后把剩余位移投影到接触切面继续滑；
/// 完全被挡住时走墙角兜底（离散推进 + 盒的角区沿较近轴推到面外），
/// 避免 8 向输入正对角撞墙角被法向反向钉死。
///
/// 与服务端 <c>pkg/collide.SweepSlideSphere</c> 同几何：占位格本身可走（占位只影响寻路代价），
/// 挡人的是这里的形状。金标准向量 <c>testdata/movement_golden.json</c> 是唯一判据。
/// </summary>
public static class MovementSlide
{
    /// <summary>每次接触沿法向退出的距离（格），与服务端 pkg/collide.DefaultSkin 一致。</summary>
    public const float Skin = 1e-3f;

    /// <summary>最多消解几次接触，与服务端 DefaultSlideIterations 一致。</summary>
    public const int MaxSlides = 4;

    /// <summary>墙角兜底的探测步长（格），与服务端 CornerProbeStep 一致。</summary>
    public const float CornerProbeStep = 0.1f;

    private const float Eps = 1e-12f;

    /// <summary>返回滑动后的实际终点（方向可能已经变了）；无形状时原样返回请求位移。</summary>
    public static (float X, float Y) Slide(
        float x, float y, float dx, float dy, float bodyRadius, IReadOnlyList<BlockerShape> shapes)
    {
        if (shapes.Count == 0 || bodyRadius <= 0f) return (x + dx, y + dy);

        // posX/posY 是已经走到的位置，dx/dy 是还没走完的位移（与服务端逐行对齐）
        var posX = x;
        var posY = y;
        for (var iter = 0; iter < MaxSlides; iter++)
        {
            var lenSq = dx * dx + dy * dy;
            if (lenSq <= Eps) break;
            if (!EarliestHit(posX, posY, dx, dy, bodyRadius, shapes, out var hitIndex, out var t, out var nx, out var ny))
            {
                posX += dx; // 一路畅通
                posY += dy;
                dx = 0f;
                dy = 0f;
                break;
            }

            var residualX = dx * (1f - t); // 还没走完的部分（未投影）
            var residualY = dy * (1f - t);
            posX += dx * t; // 推进到接触位置
            posY += dy * t;
            if (t <= 0f)
            {
                // 起点就重叠（读档/传送落在障碍里）：按接触法向推出去，能自愈
                if (TryPushOut(posX, posY, bodyRadius, shapes[hitIndex], out var px, out var py))
                {
                    posX = px;
                    posY = py;
                }
                else
                {
                    posX += nx * Skin;
                    posY += ny * Skin;
                }
            }
            else if (MathF.Sqrt(lenSq) * t > Skin)
            {
                posX += nx * Skin; // 沿法向（指向移动体）退出 skin，避免贴面卡死
                posY += ny * Skin;
            }

            // 剩余位移投影到接触切面：去掉法向分量，只保留沿表面滑走的部分
            var restX = residualX;
            var restY = residualY;
            var intoSurface = restX * nx + restY * ny;
            if (intoSurface < 0f)
            {
                restX -= nx * intoSurface;
                restY -= ny * intoSurface;
            }
            if (restX * restX + restY * restY > Eps)
            {
                dx = restX;
                dy = restY;
                continue;
            }

            // 完全被挡住（8 向输入正对角撞墙角时法向恰好与位移反向）：
            // 离散推进 + 沿较近轴推出 → 顺着墙面滑开，而不是钉死在角上。
            CornerAssist(ref posX, ref posY, residualX, residualY, bodyRadius, shapes);
            dx = 0f;
            dy = 0f;
        }
        return (posX, posY);
    }

    /// <summary>最早接触：命中返回 true，并给出接触比例 t、指向移动体的单位法向、形状下标。</summary>
    private static bool EarliestHit(
        float x, float y, float dx, float dy, float r, IReadOnlyList<BlockerShape> shapes,
        out int hitIndex, out float hitT, out float nx, out float ny)
    {
        hitIndex = -1;
        hitT = float.PositiveInfinity;
        nx = 0f;
        ny = 0f;
        var lenSq = dx * dx + dy * dy;
        if (lenSq <= Eps) return false;

        for (var i = 0; i < shapes.Count; i++)
        {
            var shape = shapes[i];
            float t;
            float cx;
            float cy;
            if (shape.IsBox)
            {
                if (!SweepCircleBox(x, y, dx, dy, r, shape, out t, out cx, out cy)) continue;
            }
            else if (shape.IsCapsule)
            {
                // 胶囊（移动体）：圆扫掠 vs 线段，等价于"球沿运动方向撞胶囊段"。
                // 与服务端 SweepSlideCapsule 的"沿轴采样球"是同一几何。
                if (!SweepCircleSegment(x, y, dx, dy, r, shape, out t, out cx, out cy)) continue;
            }
            else
            {
                var rSum = r + shape.R;
                var mx = x - shape.X;
                var my = y - shape.Y;
                var c = mx * mx + my * my - rSum * rSum;
                if (c <= 0f)
                {
                    t = 0f; // 已重叠
                    cx = mx;
                    cy = my;
                }
                else
                {
                    var bq = mx * dx + my * dy;
                    if (bq >= 0f) continue; // 背离
                    var disc = bq * bq - lenSq * c;
                    if (disc < 0f) continue; // 够不着
                    t = (-bq - MathF.Sqrt(disc)) / lenSq;
                    if (t < 0f || t > 1f) continue;
                    cx = mx + dx * t;
                    cy = my + dy * t;
                }
            }

            if (t >= hitT) continue;
            hitT = t;
            hitIndex = i;
            var nl = MathF.Sqrt(cx * cx + cy * cy);
            if (nl > 1e-6f)
            {
                nx = cx / nl;
                ny = cy / nl;
            }
            else
            {
                nx = 1f;
                ny = 0f;
            }
        }
        return hitIndex >= 0;
    }

    /// <summary>
    /// 圆 × 轴对齐盒扫掠：膨胀盒（Minkowski）逐轴 slab 求进入点，落在角区时改解角圆，
    /// 起点已重叠时按最近点/最浅面给出 t=0 的推出法向。
    /// </summary>
    private static bool SweepCircleBox(
        float x, float y, float dx, float dy, float r, BlockerShape box,
        out float t, out float cx, out float cy)
    {
        t = 0f;
        cx = 0f;
        cy = 0f;
        var maxX = box.X + box.W;
        var maxY = box.Y + box.H;

        // 起点在膨胀盒内：已重叠，法向指向盒上最近点（心在盒内时取最浅面）
        if (x > box.X - r && x < maxX + r && y > box.Y - r && y < maxY + r)
        {
            var qx = Math.Clamp(x, box.X, maxX);
            var qy = Math.Clamp(y, box.Y, maxY);
            cx = x - qx;
            cy = y - qy;
            if (cx == 0f && cy == 0f) ShallowestFace(x, y, box, out cx, out cy);
            return true;
        }

        var invX = dx != 0f ? 1f / dx : float.PositiveInfinity;
        var invY = dy != 0f ? 1f / dy : float.PositiveInfinity;
        var t0X = (box.X - r - x) * invX;
        var t1X = (maxX + r - x) * invX;
        if (t0X > t1X) (t0X, t1X) = (t1X, t0X);
        var t0Y = (box.Y - r - y) * invY;
        var t1Y = (maxY + r - y) * invY;
        if (t0Y > t1Y) (t0Y, t1Y) = (t1Y, t0Y);

        var tEnter = MathF.Max(t0X, t0Y);
        var tExit = MathF.Min(t1X, t1Y);
        if (tEnter > tExit || tExit < 0f || tEnter > 1f) return false;
        if (tEnter < 0f) tEnter = 0f;

        var enterX = x + dx * tEnter;
        var enterY = y + dy * tEnter;
        var outsideX = enterX < box.X || enterX > maxX;
        var outsideY = enterY < box.Y || enterY > maxY;
        if (outsideX && outsideY)
        {
            // 角区：真实接触在角圆上 → 解圆-圆
            var cornerX = enterX < box.X ? box.X : maxX;
            var cornerY = enterY < box.Y ? box.Y : maxY;
            var mx = x - cornerX;
            var my = y - cornerY;
            var lenSq = dx * dx + dy * dy;
            var bq = mx * dx + my * dy;
            var disc = bq * bq - lenSq * (mx * mx + my * my - r * r);
            if (disc < 0f) return false;
            var tc = (-bq - MathF.Sqrt(disc)) / lenSq;
            if (tc < 0f || tc > 1f) return false;
            t = tc;
            cx = mx + dx * tc;
            cy = my + dy * tc;
            return true;
        }

        // 平面接触：法向是进入面的外法线
        t = tEnter;
        if (t0X > t0Y)
        {
            cx = dx > 0f ? -1f : 1f;
            cy = 0f;
        }
        else
        {
            cx = 0f;
            cy = dy > 0f ? -1f : 1f;
        }
        return true;
    }

    /// <summary>
    /// 圆 × 胶囊段扫掠：把段当成"半径合"的膨胀线段，求圆沿位移与它的最早接触。
    /// 退化成圆×圆（halfLength=0）时与静态圆分支等价。
    /// </summary>
    private static bool SweepCircleSegment(
        float x, float y, float dx, float dy, float r, BlockerShape shape,
        out float t, out float cx, out float cy)
    {
        t = 0f;
        cx = 0f;
        cy = 0f;
        var seg = CapsuleSegment.From(shape);
        var rSum = r + seg.R;

        // 起点到线段的最短向量（用于"已重叠"判定与法向）
        ClosestOnSegment(x, y, seg.AX, seg.AY, seg.BX, seg.BY, out var qx, out var qy);
        var mx = x - qx;
        var my = y - qy;
        var distSq = mx * mx + my * my;
        if (distSq <= rSum * rSum)
        {
            // 已重叠：t=0 + 指向移动体的法向
            t = 0f;
            cx = mx;
            cy = my;
            if (MathF.Abs(cx) < 1e-6f && MathF.Abs(cy) < 1e-6f) { cx = 1f; cy = 0f; }
            return true;
        }

        // 圆沿位移扫掠膨胀线段：等价于对段的两个端点各做一次圆×圆扫掠，
        // 再对"边的法向"做一次平面扫掠，取最早。这里用与静态圆一致的保守做法：
        // 只考虑端点圆（段很短时足够；长段由调用方的长度限制保证）。
        var bestT = float.PositiveInfinity;
        var bestCx = 0f;
        var bestCy = 0f;
        foreach (var (px, py) in new[] { (seg.AX, seg.AY), (seg.BX, seg.BY) })
        {
            var ex = x - px;
            var ey = y - py;
            var c = ex * ex + ey * ey - rSum * rSum;
            var lenSq = dx * dx + dy * dy;
            if (lenSq <= Eps) continue;
            if (c <= 0f)
            {
                if (0f < bestT) { bestT = 0f; bestCx = ex; bestCy = ey; }
                continue;
            }
            var bq = ex * dx + ey * dy;
            if (bq >= 0f) continue;
            var disc = bq * bq - lenSq * c;
            if (disc < 0f) continue;
            var tc = (-bq - MathF.Sqrt(disc)) / lenSq;
            if (tc < 0f || tc > 1f) continue;
            if (tc < bestT)
            {
                bestT = tc;
                bestCx = ex + dx * tc;
                bestCy = ey + dy * tc;
            }
        }
        if (float.IsPositiveInfinity(bestT)) return false;
        t = bestT;
        cx = bestCx;
        cy = bestCy;
        return true;
    }

    /// <summary>点到线段最近点。</summary>
    private static void ClosestOnSegment(
        float px, float py, float ax, float ay, float bx, float by,
        out float qx, out float qy)
    {
        var abx = bx - ax;
        var aby = by - ay;
        var lenSq = abx * abx + aby * aby;
        if (lenSq <= Eps) { qx = ax; qy = ay; return; }
        var tt = ((px - ax) * abx + (py - ay) * aby) / lenSq;
        tt = Math.Clamp(tt, 0f, 1f);
        qx = ax + abx * tt;
        qy = ay + aby * tt;
    }

    /// <summary>圆心落在盒内时取穿透最浅的那个面作为推出方向。</summary>
    private static void ShallowestFace(float x, float y, BlockerShape box, out float nx, out float ny)
    {
        var left = x - box.X;
        var right = box.X + box.W - x;
        var bottom = y - box.Y;
        var top = box.Y + box.H - y;
        var best = MathF.Min(MathF.Min(left, right), MathF.Min(bottom, top));
        nx = 0f;
        ny = 0f;
        if (best == left) nx = -1f;
        else if (best == right) nx = 1f;
        else if (best == bottom) ny = -1f;
        else ny = 1f;
    }

    /// <summary>
    /// 墙角兜底：按 <see cref="CornerProbeStep"/> 把剩余位移离散推进，
    /// 遇到第一个重叠位置就解掉穿透。步长固定且很小，所以不会一步跨过薄障碍。
    /// </summary>
    private static bool CornerAssist(
        ref float x, ref float y, float dx, float dy, float bodyRadius, IReadOnlyList<BlockerShape> shapes)
    {
        var total = MathF.Sqrt(dx * dx + dy * dy);
        if (total <= 1e-6f) return false;
        var steps = Math.Max(1, (int)MathF.Ceiling(total / CornerProbeStep));
        for (var i = 1; i <= steps; i++)
        {
            var px = x + dx * i / steps;
            var py = y + dy * i / steps;
            var moved = false;
            foreach (var shape in shapes)
            {
                if (!TryPushOut(px, py, bodyRadius, shape, out var ox, out var oy)) continue;
                px = ox;
                py = oy;
                moved = true;
            }
            if (!moved) continue;
            x = px;
            y = py;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 把圆心从形状里推出去：圆按心连线，盒的角区沿较近轴推到面外（于是沿面滑），
    /// 圆心在盒内时沿最浅面推出。不重叠返回 false。
    /// </summary>
    private static bool TryPushOut(float x, float y, float r, BlockerShape shape, out float ox, out float oy)
    {
        ox = x;
        oy = y;
        if (!shape.IsBox)
        {
            var mx = x - shape.X;
            var my = y - shape.Y;
            var rSum = r + shape.R;
            var d2 = mx * mx + my * my;
            if (d2 >= rSum * rSum) return false;
            var d = MathF.Sqrt(d2);
            if (d > 1e-6f)
            {
                ox = shape.X + mx / d * rSum;
                oy = shape.Y + my / d * rSum;
            }
            else
            {
                ox = shape.X + rSum;
                oy = shape.Y;
            }
            return true;
        }

        var maxX = shape.X + shape.W;
        var maxY = shape.Y + shape.H;
        var qx = Math.Clamp(x, shape.X, maxX);
        var qy = Math.Clamp(y, shape.Y, maxY);
        var dxq = x - qx;
        var dyq = y - qy;
        var dist2 = dxq * dxq + dyq * dyq;
        if (dist2 > 0f)
        {
            if (dist2 >= r * r) return false;
            var outX = x < shape.X ? shape.X - x : x - maxX;
            var outY = y < shape.Y ? shape.Y - y : y - maxY;
            if (outX > 0f && outY > 0f)
            {
                // 角区：沿较近的轴推到面外，另一轴不动 → 沿面滑
                if (outX <= outY) ox = x < shape.X ? shape.X - r : maxX + r;
                else oy = y < shape.Y ? shape.Y - r : maxY + r;
                return true;
            }
            var d = MathF.Sqrt(dist2);
            ox = qx + dxq / d * r;
            oy = qy + dyq / d * r;
            return true;
        }

        ShallowestFace(x, y, shape, out var nx, out var ny);
        if (nx < 0f) ox = shape.X - r;
        else if (nx > 0f) ox = maxX + r;
        else if (ny < 0f) oy = shape.Y - r;
        else oy = maxY + r;
        return true;
    }
}
