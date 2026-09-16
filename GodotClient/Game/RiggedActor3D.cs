using System;
using Godot;
using Starve.Game.V1;

namespace GodotClient.Game;

/// <summary>
/// 一份骨架 + AnimationPlayer。猪人合并 walk/run 并重定向 UAL；动物直接播包内 clip。
/// </summary>
[Tool]
public partial class RiggedActor3D : Node3D, IAnimatedActor3D
{
    private float _modelScale = 1f;
    private float _yawDegrees;
    private bool _applyToon;
    private bool _playing = true;
    private float _moveSpeedScale = 1f;
    private float _animSpeedMul = 1f;
    private bool _rebuildQueued;
    private bool _moving;
    private bool _actionActive;
    private bool _hitPlaying;
    private bool _dead;
    private AnimationPlayer? _player;
    private string _locoClip = "";
    private string _actionClip = "";
    private bool _needsPoseReset;

    public string ModelPath { get; set; } = "";
    public string[] ExtraAnimPaths { get; set; } = [];
    public bool MergeUal { get; set; }
    /// <summary>抵消 GLB 根节点残留缩放。猪人 Armature 是 Mixamo 的 0.01。</summary>
    public float ImportScale { get; set; } = 1f;
    /// <summary>头顶小字，方便确认动物已经生成。空则不显示。</summary>
    public string MarkerLabel { get; set; } = "";
    public float WalkTilesPerSec { get; set; } = 4.5f;
    public float RunSpeedThreshold { get; set; } = 13f;

    [Export(PropertyHint.Range, "0.2,3,0.05")]
    public float ModelScale
    {
        get => _modelScale;
        set
        {
            _modelScale = Mathf.Max(0.05f, value);
            var visual = GetNodeOrNull<Node3D>("Visual");
            if (visual is not null) visual.Scale = Vector3.One * VisualScale;
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
        set
        {
            if (_applyToon == value) return;
            _applyToon = value;
            if (IsInsideTree()) Rebuild();
            else RequestRebuild();
        }
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

    public float AnimSpeedMul
    {
        get => _animSpeedMul;
        set
        {
            _animSpeedMul = Mathf.Max(0.05f, value);
            if (IsInsideTree()) SyncPlayback();
        }
    }

    private float VisualScale => Mathf.Max(0.01f, ImportScale) * _modelScale;

    public override void _Ready() => Rebuild();

    public override void _EnterTree()
    {
        if (Engine.IsEditorHint()) RequestRebuild();
    }

    public void SetLocomotion(bool moving, float tilesPerSec = 10f)
    {
        _moving = moving;
        if (!Engine.IsEditorHint())
            _playing = true;
        _moveSpeedScale = moving
            ? Mathf.Clamp(tilesPerSec / Mathf.Max(0.5f, WalkTilesPerSec), 0.7f, 2.8f) * _animSpeedMul
            : _animSpeedMul;
        _locoClip = moving
            ? (tilesPerSec >= RunSpeedThreshold
                ? FirstClip("sprint_loop", "sprint", "gallop", "running", "run", "jog", "walk_loop", "walk")
                : FirstClip("walk_loop", "walk", "jog"))
            : FirstClip("idle_loop", "idle", "idle_rest");
        if (_actionActive || _hitPlaying || _dead) return;
        SyncPlayback();
    }

    public void PlayAction(ActionKind kind)
    {
        if (_dead) return;
        var clip = kind switch
        {
            ActionKind.Attack or ActionKind.Chop or ActionKind.Mine =>
                FirstClip("punch", "hook", "attack", "proc_attack"),
            ActionKind.Pick =>
                FirstClip("pickup", "picking", "proc_pick"),
            _ => FirstClip("idle_loop", "idle", "idle_rest"),
        };
        // 自动攻击会连续换 action_id；同一挥击没播完就从头切，看起来永远挥不完。
        if (_actionActive && _actionClip == clip && IsOneShotPlaying())
            return;
        _actionActive = true;
        _hitPlaying = false;
        _actionClip = clip;
        PlayNamed(_actionClip, loop: false, speed: 1f);
    }

    public void FinishAction()
    {
        if (_dead) return;
        // 权威完成：oneshot 播完再回 walk/idle。服务端常在命中帧就摘掉 ActionState。
        if (IsOneShotPlaying())
            return;
        _actionActive = false;
        if (_hitPlaying) return;
        _needsPoseReset = true;
        SyncPlayback();
    }

    public void CancelAction()
    {
        if (_dead) return;
        _actionActive = false;
        _hitPlaying = false;
        _actionClip = "";
        _needsPoseReset = true;
        SyncPlayback();
    }

    public void PlayHit()
    {
        if (_dead) return;
        _hitPlaying = true;
        PlayNamed(FirstClip("hitreact", "hit", "proc_hit"), loop: false, speed: 1.2f);
    }

    public void PlayDeath()
    {
        _dead = true;
        _actionActive = false;
        _hitPlaying = false;
        _playing = true;
        PlayNamed(FirstClip("death01", "death", "proc_death"), loop: false, speed: 1f);
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
                else if (mesh.GetSurfaceOverrideMaterial(i) is StandardMaterial3D overStd)
                {
                    overStd.EmissionEnabled = on;
                    overStd.Emission = on ? Colors.White : Colors.Black;
                    overStd.EmissionEnergyMultiplier = on ? 0.55f : 0f;
                }
            }
        }
    }

    public void PlayPreview(string needle)
    {
        _locoClip = FirstClip(needle);
        _actionActive = false;
        _hitPlaying = false;
        _dead = false;
        _playing = true;
        SyncPlayback();
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
        if (_player is not null)
            _player.AnimationFinished -= OnAnimationFinished;
        _player = null;
        var old = GetNodeOrNull<Node>("Visual");
        if (old is not null)
        {
            RemoveChild(old);
            old.Free();
        }

        if (string.IsNullOrEmpty(ModelPath) || !ResourceLoader.Exists(ModelPath))
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
                Text = "模型尚未导入，请等 Godot 导入完成",
                Position = new Vector3(0, 1.2f, 0),
                PixelSize = 0.01f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            });
            return;
        }

        var visual = new Node3D
        {
            Name = "Visual",
            Scale = Vector3.One,
            RotationDegrees = new Vector3(0, _yawDegrees, 0),
        };
        AddChild(visual);
        var model = packed.Instantiate<Node3D>();
        visual.AddChild(model);
        HideImportJunk(model);
        UniqueMaterials(model);
        if (_applyToon) ToonMaterials.ApplyToMeshTree(model);
        _player = FindAnimationPlayer(model);
        var skel = FindSkeleton(model);
        visual.Scale = Vector3.One * VisualScale;
        LiftPlainMaterials(model);
        AttachMarker(visual);
        if (!string.IsNullOrEmpty(MarkerLabel))
            CallDeferred(MethodName.ValidateVisible);
        if (_player is not null)
        {
            foreach (var extra in ExtraAnimPaths)
            {
                if (string.IsNullOrEmpty(extra) || extra == ModelPath) continue;
                HumanoidAnimRetarget.MergeInto(_player, skel, extra);
            }
            // UAL 是 Rigify，不再往猪人身上合。
            if (skel is not null)
            {
                var names = _player.GetAnimationList();
                if (FindClip(names, "idle_loop", "idle") is null)
                    EnsureIdleFromRest(_player, skel);
                EnsureFallbackClips(_player, skel);
            }
            _player.AnimationFinished += OnAnimationFinished;
        }
        if (string.IsNullOrEmpty(_locoClip))
            _locoClip = FirstClip("idle_loop", "idle", "idle_rest", "walk_loop", "walk");
        SyncPlayback();
    }

    private void OnAnimationFinished(StringName name)
    {
        if (_dead)
        {
            if (_player is null) return;
            _player.Play(name);
            _player.Seek(_player.CurrentAnimationLength, true);
            _player.Pause();
            return;
        }
        if (_hitPlaying)
        {
            _hitPlaying = false;
            if (_actionActive && !string.IsNullOrEmpty(_actionClip))
            {
                PlayNamed(_actionClip, loop: false, speed: 1f);
                return;
            }
        }
        else if (_actionActive)
        {
            _actionActive = false;
        }
        _needsPoseReset = true;
        SyncPlayback();
    }

    private void SyncPlayback()
    {
        if (_player is null) return;
        if (_dead) return;
        if (_actionActive || _hitPlaying) return;
        if (_needsPoseReset)
        {
            FindSkeleton(this)?.ResetBonePoses();
            _needsPoseReset = false;
        }

        var name = string.IsNullOrEmpty(_locoClip)
            ? FirstClip("idle_loop", "idle", "idle_rest", "walk")
            : _locoClip;
        if (string.IsNullOrEmpty(name)) return;
        var loop = IsLoopClip(name);
        if (!_playing)
        {
            PlayNamed(name, loop, 0f);
            _player.Seek(0, true);
            return;
        }
        PlayNamed(name, loop, _moving ? _moveSpeedScale : _animSpeedMul);
    }

    private void PlayNamed(string name, bool loop, float speed)
    {
        if (_player is null || string.IsNullOrEmpty(name)) return;
        var anim = _player.GetAnimation(name);
        if (anim is not null)
            anim.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        if (_player.CurrentAnimation != name)
            _player.Play(name);
        else if (!_player.IsPlaying() && speed > 0.01f)
            _player.Play();
        _player.SpeedScale = speed <= 0.01f ? 0f : speed;
    }

    private string FirstClip(params string[] needles)
    {
        if (_player is null) return "";
        return FindClip(_player.GetAnimationList(), needles) ?? "";
    }

    private bool IsOneShotPlaying() =>
        _actionActive && _player is not null && _player.IsPlaying()
        && !string.IsNullOrEmpty(_actionClip) && !IsLoopClip(_actionClip);

    private static bool IsLoopClip(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("loop") || n.Contains("idle") || n.Contains("walk")
            || n.Contains("run") || n.Contains("gallop") || n.Contains("sprint")
            || n.Contains("jog");
    }

    internal static string? FindClip(string[] names, params string[] needles)
    {
        foreach (var needle in needles)
        {
            foreach (var name in names)
            {
                if (StoredName(name).Equals(needle, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            string? best = null;
            foreach (var name in names)
            {
                var searchable = name.Replace('|', '_').Replace('/', '_');
                if (!searchable.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (needle.Equals("idle", StringComparison.OrdinalIgnoreCase)
                    && searchable.Contains("hit", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (best is null || name.Length < best.Length)
                    best = name;
            }
            if (best is not null) return best;
        }
        return null;
    }

    internal static string StoredName(string name)
    {
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        return name.Replace('|', '_');
    }

    internal static AnimationLibrary EnsureLibrary(AnimationPlayer player)
    {
        var libs = player.GetAnimationLibraryList();
        var libName = libs.Count > 0 ? libs[0].ToString() : "";
        var lib = player.GetAnimationLibrary(libName);
        if (lib is not null) return lib;
        lib = new AnimationLibrary();
        player.AddAnimationLibrary("", lib);
        return lib;
    }

    internal static int CopyClips(AnimationPlayer dest, AnimationPlayer src)
    {
        var destLib = EnsureLibrary(dest);
        var count = 0;
        foreach (var name in src.GetAnimationList())
        {
            var anim = src.GetAnimation(name);
            if (anim is null) continue;
            var leaf = StoredName(name);
            if (destLib.HasAnimation(leaf) || dest.HasAnimation(name)) continue;
            var copy = (Animation)anim.Duplicate();
            PruneUnresolvableTracks(copy, dest);
            if (copy.GetTrackCount() == 0) continue; // 全被裁掉 → 这个 clip 没有可用数据
            destLib.AddAnimation(leaf, copy);
            count++;
        }
        return count;
    }

    /// <summary>
    /// 删掉"目标骨架里不存在"的轨道。
    ///
    /// 为什么必须删而不是留着：源 GLB 的轨道路径形如
    /// <c>../Armature/Skeleton3D:RightToeBase</c>，而目标模型的节点层级不同
    /// （Meshy 导出的骨架与 Mixamo 不一致），于是每个无法解析的 track 都会让
    /// Godot 打一条 <c>_update_caches: couldn't resolve track</c> 警告。
    ///
    /// 实测代价：**45 秒 134 万条警告、日志 300MB**——控制台被刷屏到无法看别的信息，
    /// 而且每个 actor 实例都会重刷一遍。裁掉之后这些警告消失，
    /// 动画表现不受影响（本来就解析不到 = 本来就不生效）。
    /// </summary>
    internal static void PruneUnresolvableTracks(Animation anim, AnimationPlayer dest)
    {
        var root = dest.GetParent();
        // 从后往前删，避免下标错位。
        for (var i = anim.GetTrackCount() - 1; i >= 0; i--)
        {
            var path = anim.TrackGetPath(i);
            // 只处理"节点:骨骼"形式的轨道；纯节点轨道（如属性动画）保留。
            var sub = path.GetSubNameCount();
            if (sub == 0) continue;
            var bone = path.GetSubName(sub - 1);
            var nodePath = path.GetConcatenatedSubNames();
            var node = root?.GetNodeOrNull(nodePath);
            if (node is Skeleton3D skel)
            {
                if (skel.FindBone(bone) >= 0) continue; // 骨骼存在 → 保留
            }
            else if (node is not null)
            {
                continue; // 节点存在且不是骨架（普通属性轨道）→ 保留
            }
            anim.RemoveTrack(i);
        }
    }

    internal static NodePath FirstBonePath(AnimationPlayer player)
    {
        foreach (var name in player.GetAnimationList())
        {
            var anim = player.GetAnimation(name);
            if (anim is null) continue;
            for (var i = 0; i < anim.GetTrackCount(); i++)
            {
                var path = anim.TrackGetPath(i);
                if (path.GetSubNameCount() > 0)
                    return path;
            }
        }
        return new NodePath("Armature:Hips");
    }

    internal static AnimationPlayer? FindAnimationPlayer(Node root)
    {
        if (root is AnimationPlayer found) return found;
        foreach (var child in root.FindChildren("*", "AnimationPlayer", true, false))
        {
            if (child is AnimationPlayer player) return player;
        }
        return null;
    }

    internal static Skeleton3D? FindSkeleton(Node root)
    {
        if (root is Skeleton3D skel) return skel;
        foreach (var child in root.FindChildren("*", "Skeleton3D", true, false))
        {
            if (child is Skeleton3D found) return found;
        }
        return null;
    }

    private static void EnsureFallbackClips(AnimationPlayer player, Skeleton3D skel)
    {
        var names = player.GetAnimationList();
        var lib = EnsureLibrary(player);
        var node = BoneNodePath(FirstBonePath(player), skel);
        var arm = FirstExisting(skel, "RightArm", "mixamorig:RightArm", "mixamorig_RightArm");
        if (FindClip(names, "attack", "sword", "punch") is null)
            AddBoneSwing(lib, skel, node, "proc_attack", arm, -100f, 0.55f);
        if (FindClip(names, "pickup", "picking", "proc_pick") is null)
            AddBoneSwing(lib, skel, node, "proc_pick", FirstExisting(skel, "Spine02", "Spine", "Hips"), 42f, 0.9f);
        if (FindClip(names, "hit") is null)
            AddBoneSwing(lib, skel, node, "proc_hit", FirstExisting(skel, "Spine02", "Spine", "Hips"), 18f, 0.28f);
        if (FindClip(names, "death") is null)
            AddBoneSwing(lib, skel, node, "proc_death", "Hips", 82f, 0.9f, holdEnd: true);
    }

    private static string FirstExisting(Skeleton3D skel, params string[] bones)
    {
        foreach (var bone in bones)
        {
            if (skel.FindBone(bone) >= 0) return bone;
        }
        return skel.GetBoneCount() > 0 ? skel.GetBoneName(0) : "Hips";
    }

    private static void AddBoneSwing(
        AnimationLibrary lib,
        Skeleton3D skel,
        string node,
        string clipName,
        string bone,
        float degrees,
        float length,
        bool holdEnd = false)
    {
        if (lib.HasAnimation(clipName)) return;
        var idx = skel.FindBone(bone);
        if (idx < 0) return;
        var restQ = skel.GetBoneRest(idx).Basis.GetRotationQuaternion();
        var swingQ = restQ * new Quaternion(Vector3.Right, Mathf.DegToRad(degrees));
        var anim = new Animation
        {
            Length = length,
            LoopMode = Animation.LoopModeEnum.None,
        };
        var track = anim.AddTrack(Animation.TrackType.Rotation3D);
        anim.TrackSetPath(track, new NodePath($"{node}:{bone}"));
        anim.RotationTrackInsertKey(track, 0, restQ);
        anim.RotationTrackInsertKey(track, length * 0.35f, swingQ);
        anim.RotationTrackInsertKey(track, length, holdEnd ? swingQ : restQ);
        lib.AddAnimation(clipName, anim);
    }

    private void ValidateVisible()
    {
        var visual = GetNodeOrNull<Node3D>("Visual");
        if (visual is null) return;
        var aabb = CollectGlobalMeshAabb(visual);
        var height = aabb.Size.Y;
        if (height is < 0.2f or > 8f)
            visual.Scale = Vector3.One * Mathf.Clamp(_modelScale, 0.35f, 0.7f);
        aabb = CollectGlobalMeshAabb(visual);
        if (aabb.Size.Y >= 0.2f && aabb.Size.Y <= 8f)
        {
            if (!string.IsNullOrEmpty(MarkerLabel))
                GD.Print($"{Name} {MarkerLabel} aabb={aabb.Size} scale={visual.Scale}");
            return;
        }
        if (visual.GetNodeOrNull<MeshInstance3D>("FallbackBody") is not null) return;
        visual.AddChild(new MeshInstance3D
        {
            Name = "FallbackBody",
            Position = new Vector3(0, 0.45f, 0),
            Mesh = new CapsuleMesh { Radius = 0.22f, Height = 0.9f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.62f, 0.58f, 0.52f),
                Roughness = 0.85f,
                Metallic = 0f,
            },
        });
        GD.PushWarning($"{Name} {MarkerLabel} mesh hidden, fallback capsule aabb={aabb.Size}");
    }

    private void AttachMarker(Node3D visual)
    {
        if (string.IsNullOrEmpty(MarkerLabel)) return;
        visual.AddChild(new Label3D
        {
            Name = "Marker",
            Text = MarkerLabel,
            Position = new Vector3(0, 1.65f, 0),
            PixelSize = 0.012f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            OutlineSize = 8,
            Modulate = Colors.White,
        });
    }

    private static void LiftPlainMaterials(Node model)
    {
        foreach (var child in model.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            var count = mesh.Mesh?.GetSurfaceCount() ?? 0;
            for (var i = 0; i < count; i++)
            {
                if (mesh.GetSurfaceOverrideMaterial(i) is not StandardMaterial3D std) continue;
                var c = std.AlbedoColor;
                if (c.R + c.G + c.B >= 0.85f) continue;
                std.AlbedoColor = new Color(
                    Mathf.Clamp(c.R * 2.1f + 0.16f, 0f, 1f),
                    Mathf.Clamp(c.G * 2.1f + 0.16f, 0f, 1f),
                    Mathf.Clamp(c.B * 2.1f + 0.14f, 0f, 1f));
                std.Metallic = Mathf.Min(std.Metallic, 0.06f);
                std.Roughness = Mathf.Max(std.Roughness, 0.78f);
            }
        }
    }

    private static Aabb CollectGlobalMeshAabb(Node root)
    {
        var acc = new Aabb();
        var has = false;
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh || !mesh.Visible) continue;
            if (mesh.Name.ToString().Contains("Ico", StringComparison.OrdinalIgnoreCase))
                continue;
            var local = mesh.GetAabb();
            for (var i = 0; i < 8; i++)
            {
                var p = mesh.GlobalTransform * local.GetEndpoint(i);
                if (!has)
                {
                    acc = new Aabb(p, Vector3.Zero);
                    has = true;
                }
                else
                    acc = acc.Expand(p);
            }
        }
        return acc;
    }

    private static void HideImportJunk(Node model)
    {
        foreach (var child in model.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is MeshInstance3D mesh &&
                mesh.Name.ToString().Contains("Ico", StringComparison.OrdinalIgnoreCase))
                mesh.Visible = false;
        }
    }

    private static void UniqueMaterials(Node model)
    {
        foreach (var child in model.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            var count = mesh.Mesh?.GetSurfaceCount() ?? 0;
            for (var i = 0; i < count; i++)
            {
                var mat = mesh.GetActiveMaterial(i);
                if (mat is null) continue;
                mesh.SetSurfaceOverrideMaterial(i, (Material)mat.Duplicate());
            }
        }
    }

    private static void EnsureIdleFromRest(AnimationPlayer player, Skeleton3D skel)
    {
        var lib = EnsureLibrary(player);
        if (lib.HasAnimation("idle_rest")) return;
        var node = BoneNodePath(FirstBonePath(player), skel);
        var anim = new Animation
        {
            Length = 1f,
            LoopMode = Animation.LoopModeEnum.Linear,
        };
        for (var i = 0; i < skel.GetBoneCount(); i++)
        {
            var bone = skel.GetBoneName(i);
            var rest = skel.GetBoneRest(i);
            var rot = anim.AddTrack(Animation.TrackType.Rotation3D);
            anim.TrackSetPath(rot, new NodePath($"{node}:{bone}"));
            anim.RotationTrackInsertKey(rot, 0, rest.Basis.GetRotationQuaternion());
            var pos = anim.AddTrack(Animation.TrackType.Position3D);
            anim.TrackSetPath(pos, new NodePath($"{node}:{bone}"));
            anim.PositionTrackInsertKey(pos, 0, rest.Origin);
        }
        lib.AddAnimation("idle_rest", anim);
    }

    private static string BoneNodePath(NodePath prefix, Skeleton3D skel)
    {
        if (prefix.GetNameCount() == 0)
            return skel.Name;
        var parts = new string[prefix.GetNameCount()];
        for (var i = 0; i < prefix.GetNameCount(); i++)
            parts[i] = prefix.GetName(i);
        return string.Join("/", parts);
    }
}
