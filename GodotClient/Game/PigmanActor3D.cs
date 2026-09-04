using Godot;

namespace GodotClient.Game;

/// <summary>
/// Meshy 猪人：walk / run 两套带皮 glb。可在工作室预览，也可作为 3D 模式玩家占位。
/// </summary>
[Tool]
public partial class PigmanActor3D : Node3D
{
    public const string WalkPath = "res://assets/models/pigman/walk.glb";
    public const string RunPath = "res://assets/models/pigman/run.glb";

    public enum PigmanClip
    {
        Walk,
        Run,
    }

    private PigmanClip _clip = PigmanClip.Walk;
    private float _modelScale = 1f;
    private float _yawDegrees;
    private bool _applyToon;
    private bool _playing = true;
    private bool _rebuildQueued;
    private AnimationPlayer? _player;

    [Export]
    public PigmanClip Clip
    {
        get => _clip;
        set { _clip = value; RequestRebuild(); }
    }

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

    [Export(PropertyHint.Range, "0,360,1")]
    public float YawDegrees
    {
        get => _yawDegrees;
        set
        {
            _yawDegrees = value;
            var visual = GetNodeOrNull<Node3D>("Visual");
            if (visual is not null) visual.RotationDegrees = new Vector3(0, _yawDegrees, 0);
        }
    }

    [Export]
    public bool ApplyToon
    {
        get => _applyToon;
        set { _applyToon = value; RequestRebuild(); }
    }

    [Export]
    public bool Playing
    {
        get => _playing;
        set
        {
            _playing = value;
            SyncPlayback();
        }
    }

    public override void _Ready() => Rebuild();

    public override void _EnterTree()
    {
        if (Engine.IsEditorHint()) RequestRebuild();
    }

    public void SetLocomotion(bool moving)
    {
        _playing = moving;
        if (moving && _clip != PigmanClip.Walk)
        {
            _clip = PigmanClip.Walk;
            RequestRebuild();
            return;
        }
        SyncPlayback();
    }

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

    private void RequestRebuild()
    {
        if (!IsInsideTree() || _rebuildQueued) return;
        _rebuildQueued = true;
        CallDeferred(MethodName.Rebuild);
    }

    private void Rebuild()
    {
        _rebuildQueued = false;
        _player = null;
        var old = GetNodeOrNull<Node>("Visual");
        if (old is not null)
        {
            RemoveChild(old);
            old.Free();
        }

        var path = _clip == PigmanClip.Run ? RunPath : WalkPath;
        if (!ResourceLoader.Exists(path))
        {
            AddChild(new Label3D
            {
                Name = "Visual",
                Text = "缺少 " + path,
                Position = new Vector3(0, 1.2f, 0),
                PixelSize = 0.01f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            });
            return;
        }

        var packed = GD.Load<PackedScene>(path);
        if (packed is null)
        {
            AddChild(new Label3D
            {
                Name = "Visual",
                Text = "猪人 glb 尚未导入，请等 Godot 导入完成",
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
            RotationDegrees = new Vector3(0, _yawDegrees, 0),
        };
        AddChild(visual);
        var model = packed.Instantiate<Node3D>();
        visual.AddChild(model);
        if (_applyToon) ToonMaterials.ApplyToMeshTree(model);
        _player = FindAnimationPlayer(model);
        SyncPlayback();
    }

    private void SyncPlayback()
    {
        if (_player is null || _player.GetAnimationList().Length == 0) return;
        var name = PickClip(_player.GetAnimationList());
        if (_playing)
        {
            if (_player.CurrentAnimation != name) _player.Play(name);
            _player.SpeedScale = 1f;
        }
        else
        {
            _player.Play(name);
            _player.Seek(0, true);
            _player.SpeedScale = 0f;
        }
    }

    private string PickClip(string[] names)
    {
        var needle = _clip == PigmanClip.Run ? "run" : "walk";
        foreach (var name in names)
        {
            if (name.Contains(needle, System.StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return names[0];
    }

    private static AnimationPlayer? FindAnimationPlayer(Node root)
    {
        if (root is AnimationPlayer found) return found;
        foreach (var child in root.FindChildren("*", "AnimationPlayer", true, false))
        {
            if (child is AnimationPlayer player) return player;
        }
        return null;
    }
}
