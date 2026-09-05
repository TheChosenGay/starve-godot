using Godot;

namespace GodotClient.Game;

/// <summary>
/// 游戏世界中的可砍伐树木外观。当前使用带形态键风动的吉卜力灌木 GLB。
/// </summary>
public partial class TreeActor3D : Node3D
{
    public const string ModelPath = "res://assets/models/ghibli-bush/ghibli_bush_godot.glb";
    public const float DefaultModelScale = 0.28f;

    private float _modelScale = DefaultModelScale;
    private AnimationPlayer? _windPlayer;

    [Export(PropertyHint.Range, "0.1,1,0.01")]
    public float ModelScale
    {
        get => _modelScale;
        set
        {
            _modelScale = Mathf.Max(0.05f, value);
            var visual = GetNodeOrNull<Node3D>("Visual");
            if (visual is not null)
                visual.Scale = Vector3.One * _modelScale;
        }
    }

    public override void _Ready() => Rebuild();

    public void SetFlash(bool on)
    {
        var visual = GetNodeOrNull<Node3D>("Visual");
        if (visual is null) return;
        foreach (var child in visual.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            if (mesh.MaterialOverride is ShaderMaterial over)
                ToonMaterials.SetFlash(over, on);
            var surfaces = mesh.Mesh?.GetSurfaceCount() ?? 0;
            for (var i = 0; i < surfaces; i++)
            {
                if (mesh.GetSurfaceOverrideMaterial(i) is ShaderMaterial surface)
                    ToonMaterials.SetFlash(surface, on);
            }
        }
    }

    private void Rebuild()
    {
        _windPlayer = null;
        var old = GetNodeOrNull<Node>("Visual");
        if (old is not null)
        {
            RemoveChild(old);
            old.Free();
        }

        if (!ResourceLoader.Exists(ModelPath))
        {
            AddChild(MakeErrorLabel("缺少 " + ModelPath));
            return;
        }

        var packed = GD.Load<PackedScene>(ModelPath);
        if (packed is null)
        {
            AddChild(MakeErrorLabel("灌木 GLB 尚未导入，请等 Godot 导入完成"));
            return;
        }

        var visual = new Node3D
        {
            Name = "Visual",
            Scale = Vector3.One * _modelScale,
        };
        AddChild(visual);

        var model = packed.Instantiate<Node3D>();
        visual.AddChild(model);
        _windPlayer = FindAnimationPlayer(model);
        PlayWind();
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
