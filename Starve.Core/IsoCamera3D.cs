using System.Numerics;
using System;

namespace Starve.Core;

/// <summary>
/// 固定俯视正交相机的纯数学：世界格 → 3D、45° 俯视 2.5D 机位。
/// 渲染层用 Camera3D 套这些数即可，不在这里依赖 Godot。
/// </summary>
public static class IsoCamera3D
{
    /// <summary>世界 1 格 = 1 个 3D 单位。</summary>
    public const float WorldUnit = 1f;

    /// <summary>
    /// 俯仰角（度）。45° 是经典等距俯视，顶面偏多、容易显得「站得太高」；
    /// 28–35° 更接近侧面 2.5D。运行时可改，正交构图会跟着变。
    /// </summary>
    public static float PitchDegrees
    {
        get => _pitchDegrees;
        set => _pitchDegrees = Math.Clamp(value, 15f, 70f);
    }

    private static float _pitchDegrees = 45f;

    /// <summary>偏航角（度）：绕世界 Y 转 45°，让 XZ 轴在屏幕上成菱形。</summary>
    public const float YawDegrees = 45f;

    /// <summary>相机到注视点的距离。正交下几乎不影响构图，但太远近裁剪会切到角色。</summary>
    public const float Distance = 36f;

    /// <summary>
    /// 3D 正交视野相对「与 2D 同跨度」的倍率。越小角色越大、看得越近。
    /// </summary>
    public const float ViewScale = 0.5f;

    /// <summary>世界格坐标 → 3D（X=wx，Y=高度，Z=wy）。</summary>
    public static Vector3 WorldTo3D(float wx, float wy, float height = 0) =>
        new(wx * WorldUnit, height * WorldUnit, wy * WorldUnit);

    /// <summary>3D XZ → 世界格坐标（忽略高度）。</summary>
    public static Vector2 WorldFrom3D(float x, float z) =>
        new(x / WorldUnit, z / WorldUnit);

    /// <summary>把相机中心移到原点：世界根节点的位移。</summary>
    public static Vector3 WorldOffset(float camX, float camY, float height) =>
        -WorldTo3D(camX, camY, height);

    /// <summary>
    /// 正交相机垂直尺寸：默认比 2D 等距更近（ViewScale），角色在画面里更大。
    /// </summary>
    public static float OrthoSize(float viewHeightPx, float zoom)
    {
        var z = MathF.Max(zoom, 1e-5f);
        var pitch = PitchDegrees * (MathF.PI / 180f);
        // 地面上 Δ(wx+wy)=1 时，相机局部 Y 的增量 = sin(pitch)/√2
        var cameraYPerWxPlusWy = MathF.Sin(pitch) / MathF.Sqrt(2f);
        var visibleWxPlusWy = viewHeightPx / ((IsoMath.Step / 2f) * z);
        return cameraYPerWxPlusWy * visibleWxPlusWy * ViewScale;
    }

    /// <summary>
    /// 相对注视点的等距正交机位。viewYawRadians 叠在 45° 偏航上，用于 Q/E 绕玩家转。
    /// Position 是相对注视点的偏移（注视点 + Position = 相机世界坐标）。
    /// </summary>
    public static (Vector3 Position, Vector3 RotationDegrees) CameraPose(float viewYawRadians = 0)
    {
        var yaw = YawDegrees + viewYawRadians * (180f / MathF.PI);
        var rotation = new Vector3(-PitchDegrees, yaw, 0);
        var forward = ForwardYxz(rotation);
        return (-forward * Distance, rotation);
    }

    /// <summary>
    /// 相机挂在绕 Y 旋转的枢轴下时的本地偏移：+Z 后方、+Y 抬高，俯仰 PitchDegrees。
    /// 枢轴放在玩家身上并只转 Y，即为水平环绕，世界本身不转。
    /// </summary>
    public static Vector3 OrbitLocalOffset()
    {
        var elev = PitchDegrees * (MathF.PI / 180f);
        return new Vector3(0, Distance * MathF.Sin(elev), Distance * MathF.Cos(elev));
    }

    /// <summary>相机跟随玩家：世界不动，相机绕目标点摆在等距方位。</summary>
    public static Vector3 FollowPosition(float camX, float camY, float height, float viewYawRadians = 0)
    {
        var (rel, _) = CameraPose(viewYawRadians);
        return WorldTo3D(camX, camY, height) + rel;
    }

    /// <summary>
    /// 平面朝向：把模型局部 +Z 对准世界位移 (dx, dz)。Meshy/Mixamo 角色正面是 +Z。
    /// </summary>
    public static float FacingYaw(float dx, float dz) => MathF.Atan2(dx, dz);

    /// <summary>
    /// Godot 相机局部 -Z（视线）在世界空间的方向。欧拉顺序 YXZ，单位为度。
    /// </summary>
    public static Vector3 ForwardYxz(Vector3 rotationDegrees)
    {
        var pitch = rotationDegrees.X * (MathF.PI / 180f);
        var yaw = rotationDegrees.Y * (MathF.PI / 180f);
        var cp = MathF.Cos(pitch);
        var sp = MathF.Sin(pitch);
        var cy = MathF.Cos(yaw);
        var sy = MathF.Sin(yaw);
        // 未旋转视线 (0,0,-1) → Rx(pitch) → Ry(yaw)
        return new Vector3(-cp * sy, sp, -cp * cy);
    }
}
