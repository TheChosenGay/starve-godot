namespace Starve.Core;

/// <summary>
/// 移动键 → 世界方向映射（菱形投影屏幕轴换算）。
/// 屏幕右 = 世界 (1,-1)、左 = (-1,1)、上 = (-1,-1)、下 = (1,1)。
/// 纯逻辑：渲染层只喂按键，这里产出 (dx,dy)。
/// </summary>
public static class MoveInput
{
    private static readonly Dictionary<string, (int Dx, int Dy)> Map = new()
    {
        ["ArrowUp"] = (-1, -1),
        ["ArrowDown"] = (1, 1),
        ["ArrowLeft"] = (-1, 1),
        ["ArrowRight"] = (1, -1),
        ["w"] = (-1, -1),
        ["W"] = (-1, -1),
        ["s"] = (1, 1),
        ["S"] = (1, 1),
        ["a"] = (-1, 1),
        ["A"] = (-1, 1),
        ["d"] = (1, -1),
        ["D"] = (1, -1),
    };

    public static (int Dx, int Dy)? TryMap(string key) =>
        Map.TryGetValue(key, out var v) ? v : null;

    /// <summary>多键合成并 clamp 到 [-1,1]。</summary>
    public static (int Dx, int Dy) Combine(IEnumerable<(int Dx, int Dy)> dirs)
    {
        var dx = 0;
        var dy = 0;
        foreach (var (x, y) in dirs)
        {
            dx += x;
            dy += y;
        }
        return (Clamp(dx), Clamp(dy));
    }

    /// <summary>
    /// 把默认等距世界方向绕竖直轴转 viewYaw，得到连续世界向量（未 8 向吸附）。
    /// </summary>
    public static (float X, float Y) RotateViewYaw(int dx, int dy, float viewYawRadians)
    {
        if (dx == 0 && dy == 0) return (0f, 0f);
        var c = MathF.Cos(viewYawRadians);
        var s = MathF.Sin(viewYawRadians);
        return (dx * c + dy * s, -dx * s + dy * c);
    }

    /// <summary>
    /// 把默认等距世界方向绕竖直轴转 viewYaw，使 WASD 始终对应当前画面上/下/左/右。
    /// </summary>
    public static (int Dx, int Dy) WithViewYaw(int dx, int dy, float viewYawRadians)
    {
        var (wx, wy) = RotateViewYaw(dx, dy, viewYawRadians);
        return (SnapAxis(wx), SnapAxis(wy));
    }

    /// <summary>
    /// 8 向带角度滞回。饥荒 Q/E 是 45° 一格，不会在分界线上抖；
    /// 平滑环绕时要转过上一格中心约 30° 才换格，避免预测来回抽。
    /// </summary>
    public static (int Dx, int Dy) WithViewYawSticky(
        int dx, int dy, float viewYawRadians, (int Dx, int Dy) previous)
    {
        if (dx == 0 && dy == 0) return (0, 0);
        var (wx, wy) = RotateViewYaw(dx, dy, viewYawRadians);
        var next = (SnapAxis(wx), SnapAxis(wy));
        if (next == previous || previous == (0, 0)) return next;
        var delta = WrapPi(MathF.Atan2(wx, wy) - MathF.Atan2(previous.Dx, previous.Dy));
        return MathF.Abs(delta) >= StickRadians ? next : previous;
    }

    private const float SnapThreshold = 0.38f;
    private const float StickRadians = 30f * MathF.PI / 180f;

    private static int SnapAxis(float v) =>
        v > SnapThreshold ? 1 : v < -SnapThreshold ? -1 : 0;

    private static float WrapPi(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    private static int Clamp(int v) => Math.Clamp(v, -1, 1);
}
