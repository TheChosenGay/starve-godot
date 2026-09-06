using Godot;

namespace GodotClient.Game;

/// <summary>共享的吉卜力植被 GLB 加载与风动播放逻辑。</summary>
public partial class GhibliPlantActor3D : Node3D
{
    private float _modelScale;
    private AnimationPlayer? _windPlayer;

    protected virtual string[] ModelPaths => [];
    protected virtual float DefaultModelScale => 1f;

    public ulong VariantSeed { get; set; }

    [Export(PropertyHint.Range, "0.1,3,0.01")]
    public float ModelScale
    {
        get => _modelScale > 0f ? _modelScale : DefaultModelScale;
        set
        {
            _modelScale = Mathf.Max(0.05f, value);
            var visual = GetNodeOrNull<Node3D>("Visual");
            if (visual is not null)
                visual.Scale = Vector3.One * _modelScale;
        }
    }

    public override void _Ready() => Rebuild();

    private void Rebuild()
    {
        _windPlayer = null;
        var old = GetNodeOrNull<Node>("Visual");
        if (old is not null)
        {
            RemoveChild(old);
            old.Free();
        }

        var models = ModelPaths;
        if (models.Length == 0)
        {
            AddChild(MakeErrorLabel("未配置植被模型"));
            return;
        }

        var path = models[(int)(VariantSeed % (ulong)models.Length)];
        if (!ResourceLoader.Exists(path))
        {
            AddChild(MakeErrorLabel("缺少 " + path));
            return;
        }

        var packed = GD.Load<PackedScene>(path);
        if (packed is null)
        {
            AddChild(MakeErrorLabel($"{path} 尚未导入，请等 Godot 导入完成"));
            return;
        }

        var visual = new Node3D
        {
            Name = "Visual",
            Scale = Vector3.One * ModelScale,
        };
        AddChild(visual);

        var model = packed.Instantiate<Node3D>();
        model.Rotation = new Vector3(0f, VariantYaw(), 0f);
        visual.AddChild(model);
        GhibliPlantShading.Apply(model);
        _windPlayer = FindAnimationPlayer(model);
        PlayWind();
    }

    private float VariantYaw()
    {
        var mixed = VariantSeed * 2654435761UL;
        return (mixed % 3600UL) / 3600f * Mathf.Tau;
    }

    private void PlayWind()
    {
        if (_windPlayer is null) return;
        var names = _windPlayer.GetAnimationList();
        if (names.Length == 0) return;
        var name = names[0];
        var animation = _windPlayer.GetAnimation(name);
        if (animation is not null && animation.LoopMode == Animation.LoopModeEnum.None)
            animation.LoopMode = Animation.LoopModeEnum.Linear;
        _windPlayer.Play(name);
        _windPlayer.SpeedScale = 0.65f;
    }

    private static AnimationPlayer? FindAnimationPlayer(Node root)
    {
        if (root is AnimationPlayer found) return found;
        foreach (var child in root.FindChildren("*", "AnimationPlayer", true, false))
        {
            if (child is AnimationPlayer player)
                return player;
        }
        return null;
    }

    private static Label3D MakeErrorLabel(string text) => new()
    {
        Name = "Visual",
        Text = text,
        Position = new Vector3(0, 0.8f, 0),
        PixelSize = 0.01f,
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
    };
}

/// <summary>
/// Godot 导入 glTF 时默认不用顶点色；叶子若只有 COLOR_0 会变成白/灰。
/// 有 albedo 贴图就用贴图；没有则打开顶点色。
/// </summary>
public static class GhibliPlantShading
{
    public static void Apply(Node root)
    {
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            var surfaces = mesh.Mesh?.GetSurfaceCount() ?? 0;
            for (var i = 0; i < surfaces; i++)
            {
                if (mesh.GetActiveMaterial(i) is not StandardMaterial3D src) continue;
                var mat = (StandardMaterial3D)src.Duplicate();
                mat.VertexColorUseAsAlbedo = mat.AlbedoTexture is null;
                mesh.SetSurfaceOverrideMaterial(i, mat);
            }
        }
    }
}
