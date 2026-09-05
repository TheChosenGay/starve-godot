namespace GodotClient.Game;

/// <summary>无交互、无阻挡的纯装饰灌木外观。</summary>
public partial class ShrubActor3D : GhibliPlantActor3D
{
    private static readonly string[] Models =
    [
        "res://assets/models/ghibli-bush/ghibli_bush_godot.glb",
    ];

    protected override string[] ModelPaths => Models;
    protected override float DefaultModelScale => 0.56f;
}
