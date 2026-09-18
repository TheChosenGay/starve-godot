using System;
using System.Collections.Generic;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 本地预测投掷物（ghost）的表现层。
///
/// 只画**本地预告**那一颗：玩家按下确认后 600ms 起手期间服务端还没有可渲染的实体，
/// 全靠这一层给即时反馈；起手结束后按 <see cref="ThrowPhysics"/> 的抛物线飞，
/// 权威实体一出现就交接（见 <see cref="ThrowFlightTracker"/>），ghost 随即消失。
///
/// **权威飞行体不在这里画**：快照里带 <c>Thrown</c> 的实体由
/// <see cref="EntityLayer3D"/> 负责（它本来就有实体节点，复用同一套采样）。
/// 这样职责清楚：本层只管"服务端还没确认的那一段"。
///
/// 视觉照搬 ThrowAimLayer3D 的小球风格（同一个炸弹外观），
/// 世界→3D 用 <see cref="IsoCamera3D.WorldTo3D"/>，抛物线高度叠加到世界 Y 上
/// （游戏世界 Y 向上，与瞄准预览的弧线一致）。
/// </summary>
public partial class ThrowFlightLayer3D : Node3D
{
    /// <summary>ghost 用的小球（比瞄准点略大，作为"炸弹"更显眼）。</summary>
    private const float BombRadius = 0.13f;

    private MeshInstance3D? _bomb;
    private StandardMaterial3D? _bombMat;
    private SphereMesh? _bombMesh;
    private bool _built;

    public override void _Ready() => BuildResources();

    /// <summary>
    /// 把当前跟踪器的快照画出来。没有 ghost 时隐藏（不销毁节点，避免每帧建/删）。
    /// </summary>
    /// <param name="samples">
    /// <see cref="ThrowFlightTracker.Samples"/> 的返回值。本层只取 <c>IsGhost</c> 的那条；
    /// 该列表由跟踪器复用，所以必须当帧消费。
    /// </param>
    /// <param name="heightAt">地形高度查询：抛物线是"离地高度"，要叠在脚下地形上。</param>
    public void Show(IReadOnlyList<ThrowFlightSample> samples, Func<float, float, float>? heightAt)
    {
        BuildResources();

        ThrowFlightSample? ghost = null;
        for (var i = 0; i < samples.Count; i++)
        {
            if (!samples[i].IsGhost) continue;
            ghost = samples[i];
            break;
        }

        if (ghost is not { } g)
        {
            if (_bomb is not null) _bomb.Visible = false;
            return;
        }

        if (_bomb is null)
        {
            _bomb = new MeshInstance3D { Mesh = _bombMesh, MaterialOverride = _bombMat };
            AddChild(_bomb);
        }

        var ground = heightAt?.Invoke(g.Position.X, g.Position.Y) ?? 0f;
        var world = IsoCamera3D.WorldTo3D(g.Position.X, g.Position.Y, ground);
        // 高度是**离地**格数，直接加到世界 Y 上（与 ThrowAimLayer3D 的弧线同一口径；
        // 注意不能走 VisualY，那会乘地形 HeightScale 把弧线压扁）。
        world.Y += (float)g.Height * IsoCamera3D.WorldUnit;
        _bomb.Position = new Godot.Vector3(world.X, world.Y, world.Z);
        _bomb.Visible = true;
    }

    /// <summary>立刻隐藏 ghost（例如投掷被拒、退出世界）。</summary>
    public void Clear()
    {
        if (_bomb is not null) _bomb.Visible = false;
    }

    private void BuildResources()
    {
        if (_built) return;
        _built = true;

        _bombMesh = new SphereMesh { Radius = BombRadius, Height = BombRadius * 2f, RadialSegments = 10, Rings = 6 };
        _bombMat = new StandardMaterial3D
        {
            // 深色炸弹 + 一点自发光，白天黑夜都看得见，也和绿色的瞄准弧线区分开。
            AlbedoColor = new Color(0.16f, 0.15f, 0.17f),
            EmissionEnabled = true,
            Emission = new Color(0.9f, 0.25f, 0.12f),
            EmissionEnergyMultiplier = 0.6f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
    }
}
