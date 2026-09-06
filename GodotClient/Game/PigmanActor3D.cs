using Godot;

namespace GodotClient.Game;

/// <summary>
/// Meshy 猪人：走跑用自己的 GLB；idle/砍/捡/受击/死亡接 Mixamo clip。
/// </summary>
[Tool]
public partial class PigmanActor3D : RiggedActor3D
{
    public const string WalkPath = "res://assets/models/pigman/walk.glb";
    public const string RunPath = "res://assets/models/pigman/run.glb";

    public enum PigmanClip
    {
        Walk,
        Run,
    }

    private PigmanClip _clip = PigmanClip.Walk;

    public PigmanActor3D()
    {
        ModelPath = WalkPath;
        ExtraAnimPaths = [RunPath];
        MergeUal = false;
        WalkTilesPerSec = 4.5f;
        RunSpeedThreshold = 13f;
    }

    [Export]
    public PigmanClip Clip
    {
        get => _clip;
        set
        {
            _clip = value;
            if (IsInsideTree())
                PlayPreview(_clip == PigmanClip.Run ? "running" : "walk");
        }
    }

    public override void _Ready()
    {
        ExtraAnimPaths = [RunPath, ..HumanoidAnimRetarget.MixamoClipPaths];
        base._Ready();
        if (Engine.IsEditorHint())
            PlayPreview(_clip == PigmanClip.Run ? "running" : "walk");
    }
}
