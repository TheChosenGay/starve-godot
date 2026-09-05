using Godot;

namespace GodotClient.Game;

/// <summary>
/// Meshy 炼金引擎。网格以原点为中心，落地时抬到脚底。
/// </summary>
public partial class AlchemyEngine3D : Node3D
{
    public const string ModelPath = "res://assets/models/alchemy-engine/alchemy-engine.glb";
    public const float GroundLift = 0.952f;

    private float _modelScale = 1f;

    [Export(PropertyHint.Range, "0.2,3,0.05")]
    public float ModelScale
    {
        get => _modelScale;
        set
        {
            _modelScale = Mathf.Max(0.05f, value);
            var visual = GetNodeOrNull<Node3D>("Visual");
            if (visual is not null) visual.Scale = Vector3.One * _modelScale;
        }
    }

    public override void _Ready() => Rebuild();

    private void Rebuild()
    {
        var old = GetNodeOrNull<Node>("Visual");
        if (old is not null)
        {
            RemoveChild(old);
            old.Free();
        }

        if (!ResourceLoader.Exists(ModelPath))
        {
            AddChild(new Label3D
            {
                Name = "Visual",
                Text = "缺少 " + ModelPath,
                Position = new Vector3(0, 1.2f, 0),
                PixelSize = 0.01f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            });
            return;
        }

        var packed = GD.Load<PackedScene>(ModelPath);
        if (packed is null)
        {
            AddChild(new Label3D
            {
                Name = "Visual",
                Text = "炼金引擎 glb 尚未导入，请等 Godot 导入完成",
                Position = new Vector3(0, 1.2f, 0),
                PixelSize = 0.01f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            });
            return;
        }

        var visual = new Node3D
        {
            Name = "Visual",
            Scale = Vector3.One * _modelScale,
        };
        AddChild(visual);
        var model = packed.Instantiate<Node3D>();
        model.Position = new Vector3(0, GroundLift, 0);
        visual.AddChild(model);
    }
}
