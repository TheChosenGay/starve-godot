using System;

namespace Starve.Core;

/// <summary>
/// 占位物视觉钉点：Position 是锚点格，模型画在占格中心（圆按 1×1 处理）。
/// 占位不等于不可走——挡人的是服务端下发的形状（圆=格心圆，盒=占格矩形），
/// 本地预测用 Starve.Core.MovementSlide 复刻扫掠 + 沿面滑动。见 P1.4。
/// </summary>
public static class BlockVisual
{
    public static (float X, float Y) Center(float posX, float posY, int width, int height)
    {
        var w = Math.Max(1, width);
        var h = Math.Max(1, height);
        return (posX + w * 0.5f, posY + h * 0.5f);
    }
}
