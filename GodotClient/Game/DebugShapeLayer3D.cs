using System.Collections.Generic;
using Godot;
using Starve.Game.V1;
using Starve.Protocol.World;

namespace GodotClient.Game;

/// <summary>
/// 调试层：把服务端下发的简化碰撞体（DebugShape 组件）画成半透明体，
/// 用来肉眼核对"碰撞体是不是刚好包住渲染模型"。
///
/// 数据来源：服务端开 GATE_DEBUG_COLLISION=1 时，每个有碰撞体的实体会多一个 DebugShape
/// 组件随快照下发。坐标是**实体节点局部空间**，所以这里只跟随实体节点、不再自己施加变换：
///   - 节点位置由 <see cref="EntityLayer3D"/> 算好（占位物 = 占格中心 BlockVisual.Center，
///     移动体 = 连续位置）；朝向取实体节点的 Y 旋转，也就是"模型局部 +Z 指向"的方向；
///   - 胶囊段 a→b 直接按局部空间摆放，由节点旋转把它带到朝向（服务端不预先旋转，
///     自己再转到世界朝向会被转两遍，方向误差恰好等于朝向角）；
///   - 盒以节点为中心，**不要**再叠加 (w/2,h/2,d/2)（节点已经在占格中心）。
/// </summary>
public partial class DebugShapeLayer3D : Node3D
{
    private readonly Dictionary<ulong, (Node3D Node, string Signature)> _shapes = new();

    /// <summary>按快照同步形状（签名变了就重建；实体没了就摘掉）。</summary>
    public void Sync(IReadOnlyDictionary<ulong, EntityView> entities)
    {
        var seen = new HashSet<ulong>();
        foreach (var (id, view) in entities)
        {
            var shape = view.Get("DebugShape", DebugShape.Parser);
            if (shape is null) continue;
            seen.Add(id);
            var signature = SignatureOf(shape);
            if (_shapes.TryGetValue(id, out var entry))
            {
                if (entry.Signature == signature) continue;
                entry.Node.QueueFree();
            }
            var node = Build(shape);
            AddChild(node);
            _shapes[id] = (node, signature);
        }
        var stale = new List<ulong>();
        foreach (var id in _shapes.Keys)
        {
            if (!seen.Contains(id)) stale.Add(id);
        }
        foreach (var id in stale)
        {
            _shapes[id].Node.QueueFree();
            _shapes.Remove(id);
        }
    }

    /// <summary>跟随实体节点的位置与朝向（实体层已经算好等距投影与朝向）。</summary>
    public void UpdatePositions(EntityLayer3D? entityLayer)
    {
        if (entityLayer is null) return;
        foreach (var (id, entry) in _shapes)
        {
            if (!entityLayer.TryGetEntityNode(id, out var actor)) continue;
            entry.Node.Position = actor.Position;
            entry.Node.Rotation = new Vector3(0f, actor.Rotation.Y, 0f);
        }
    }

    private static Node3D Build(DebugShape shape)
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = shape.Kind == DebugShape.Types.Kind.DebugShapeKindBox
                ? new Color(1f, 0.62f, 0.2f, 0.25f)
                : new Color(0.3f, 0.9f, 1f, 0.25f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var node = new Node3D();
        if (shape.Kind == DebugShape.Types.Kind.DebugShapeKindBox)
        {
            var width = Mathf.Max(0.05f, (float)shape.Width);
            var depth = Mathf.Max(0.05f, (float)shape.Depth);
            var boxHeight = Mathf.Max(0.05f, (float)shape.Height);
            // 盒以**节点**为中心：实体节点已经由 EntityLayer3D 摆到占格中心
            // （BlockVisual.Center = Position + 占格/2），这里不能再叠加
            // (w/2,h/2,d/2)——那会把盒推走半个足迹（1×1 差对角半格、2×2 差 2 格）。
            node.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(width, boxHeight, depth) },
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
            return node;
        }

        var a = new Vector3((float)shape.AX, (float)shape.AY, (float)shape.AZ);
        var b = new Vector3((float)shape.BX, (float)shape.BY, (float)shape.BZ);
        var radius = Mathf.Max(0.02f, (float)shape.Radius);
        var segment = b - a;
        var length = segment.Length();
        var height = Mathf.Max(0.05f, length > 0.001f ? length + 2f * radius : (float)shape.Height);
        var mesh = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = radius, Height = height },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        // Godot 的胶囊沿 Y 轴、以自身中心为原点：摆到段中点，再把 Y 轴转到段方向
        if (length > 0.001f)
        {
            var center = (a + b) * 0.5f;
            var axis = segment / length;
            mesh.Transform = new Transform3D(new Basis(Vector3.Up.Cross(axis).Normalized(), Vector3.Up.AngleTo(axis)), center);
        }
        else
        {
            // 退化段（服务端没给段）：按高度立在节点上方；以原点为中心会半截埋进地面
            mesh.Position = new Vector3(a.X, (float)shape.Height * 0.5f, a.Z);
        }
        node.AddChild(mesh);
        return node;
    }

    private static string SignatureOf(DebugShape shape) =>
        $"{shape.Kind}|{shape.Radius:F4}|{shape.AX:F4},{shape.AY:F4},{shape.AZ:F4}|" +
        $"{shape.BX:F4},{shape.BY:F4},{shape.BZ:F4}|{shape.Width:F4}x{shape.Depth:F4}x{shape.Height:F4}";
}
