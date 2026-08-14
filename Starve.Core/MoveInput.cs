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

    private static int Clamp(int v) => Math.Clamp(v, -1, 1);
}
