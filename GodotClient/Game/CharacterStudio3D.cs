using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 角色工作室：在 Godot 里打开本场景即可看到 45° 俯视下的鱼人/蜥蜴/蜘蛛。
/// 选中角色节点，在右侧检查器改 Pixel Size、Display、Toon 等。
/// 运行当前场景（F6）也能单独预览，不必连服务器。
/// </summary>
[Tool]
public partial class CharacterStudio3D : Node3D
{
    public override void _Ready()
    {
        var cam = GetNodeOrNull<Camera3D>("IsoCamera");
        if (cam is not null)
        {
            var (pos, rot) = IsoCamera3D.CameraPose();
            var p = new Vector3(pos.X, pos.Y, pos.Z);
            cam.Position = p.Normalized() * 16f;
            cam.RotationDegrees = new Vector3(rot.X, rot.Y, rot.Z);
            cam.Projection = Camera3D.ProjectionType.Orthogonal;
            cam.Size = 8f;
            cam.Current = !Engine.IsEditorHint();
        }

        var ground = GetNodeOrNull<MeshInstance3D>("Ground");
        if (ground is not null && ground.Mesh is PlaneMesh plane)
            plane.Material = ToonMaterials.CreateGround();
    }
}
