namespace Starve.Core;

/// <summary>
/// 本端移动的**链状态**（纯数据）。
///
/// 约定：凡是要跨步保留、且影响下一步结果的东西，全部在这里 ——
/// 尤其是 <see cref="VelX"/>/<see cref="VelY"/>（ORCA 的自身速度）。
/// 放在 <see cref="OwnMovePredictor"/> 的字段里就会让重放不可复现。
/// </summary>
public struct OwnMoveState
{
    /// <summary>整格锚点。</summary>
    public int AnchorX;
    public int AnchorY;

    /// <summary>子格偏移（[0,1)）。</summary>
    public float SubX;
    public float SubY;

    /// <summary>上一步的实际速度（格/秒）——ORCA 的"当前速度"输入，也是表现层依据。</summary>
    public float VelX;
    public float VelY;

    /// <summary>连续位置 X（= 锚点 + 子格）。</summary>
    public readonly float X => AnchorX + SubX;

    /// <summary>连续位置 Y（= 锚点 + 子格）。</summary>
    public readonly float Y => AnchorY + SubY;

    public static OwnMoveState FromContinuous(float x, float y)
    {
        var anchorX = (int)MathF.Floor(x);
        var anchorY = (int)MathF.Floor(y);
        var subX = x - anchorX;
        var subY = y - anchorY;
        if (subX < 0f)
        {
            anchorX--;
            subX += 1f;
        }

        if (subY < 0f)
        {
            anchorY--;
            subY += 1f;
        }

        return new OwnMoveState { AnchorX = anchorX, AnchorY = anchorY, SubX = subX, SubY = subY };
    }
}

/// <summary>移动意图（按住方向，−1/0/1）。</summary>
public readonly record struct MoveIntent(int Dx, int Dy)
{
    public static MoveIntent Stop => new(0, 0);
}
