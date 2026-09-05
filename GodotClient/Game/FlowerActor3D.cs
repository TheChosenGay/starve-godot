namespace GodotClient.Game;

/// <summary>可采摘花朵外观；按实体 ID 稳定选择一种花色。</summary>
public partial class FlowerActor3D : GhibliPlantActor3D
{
    private static readonly string[] Models =
    [
        "res://assets/models/ghibli-flower/ghibli_flower_daisy_godot.glb",
        "res://assets/models/ghibli-flower/ghibli_flower_buttercup_godot.glb",
        "res://assets/models/ghibli-flower/ghibli_flower_pink_godot.glb",
        "res://assets/models/ghibli-flower/ghibli_flower_lavender_godot.glb",
        "res://assets/models/ghibli-flower/ghibli_flower_poppy_godot.glb",
    ];

    protected override string[] ModelPaths => Models;
    protected override float DefaultModelScale => 1.2f;
}
