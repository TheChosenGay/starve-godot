using System;
using System.Numerics;

namespace Starve.Core;

/// <summary>
/// 菱形等距投影的纯数学：世界坐标 → 世界容器本地坐标，以及容器变换。
/// 容器 transform：scale = zoom，position = 屏幕中心 - 相机中心投影偏移。
/// </summary>
public static class IsoMath
{
    /// <summary>世界 1 格对应的菱形边长（本地坐标像素）。</summary>
    public const float Step = 20;

    /// <summary>世界坐标 → 容器本地坐标（高度已包含，容器本身带 zoom 缩放）。</summary>
    public static Vector2 WorldToLocal(float wx, float wy, float height = 0) =>
        new((wx - wy) * Step, (wx + wy) * Step / 2 - height * Step);

    /// <summary>
    /// 容器本地坐标 → 世界坐标。高度场会影响屏幕 Y，因此迭代采样两次完成逆投影。
    /// 该函数接收 WorldContent.ToLocal() 的结果，天然包含相机旋转/缩放的逆变换。
    /// </summary>
    public static Vector2 LocalToWorld(float lx, float ly, Func<float, float, float>? heightAt = null)
    {
        var height = 0f;
        var result = Vector2.Zero;
        for (var i = 0; i < 2; i++)
        {
            var a = lx / Step;
            var b = (ly + height * Step) / (Step / 2);
            result = new Vector2((a + b) / 2, (b - a) / 2);
            if (heightAt is not null) height = heightAt(result.X, result.Y);
        }
        return result;
    }

    /// <summary>容器位置：把相机中心投影到屏幕中心。</summary>
    public static Vector2 ContainerPosition(
        float viewW,
        float viewH,
        float camX,
        float camY,
        float hCam,
        float zoom)
    {
        var fx = (camX - camY) * Step * zoom;
        var fy = ((camX + camY) * Step / 2 - hCam * Step) * zoom;
        return new Vector2(viewW / 2 - fx, viewH / 2 - fy);
    }
}
