using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 把外来人形 clip 接到猪人骨上。
/// Mixamo（mixamorig:*）只改骨名、只拷旋转；UAL / Rigify 仍走另一张表。
/// </summary>
public static class HumanoidAnimRetarget
{
    public const string UalPath = "res://assets/models/ual/AnimationLibrary_Godot_Standard.gltf";
    public const string MixamoDir = "res://assets/models/pigman/mixamo/";
    public const string MixamoIdlePath = MixamoDir + "idle.glb";
    public static readonly string[] MixamoClipPaths =
    [
        MixamoDir + "punch.glb",
        MixamoDir + "pickup.glb",
        MixamoDir + "hit.glb",
        MixamoDir + "death.glb",
    ];

    private static readonly Dictionary<string, string> UalToPigman = new()
    {
        ["DEF-hips"] = "Hips",
        ["DEF-spine.001"] = "Spine02",
        ["DEF-spine.002"] = "Spine01",
        ["DEF-spine.003"] = "Spine",
        ["DEF-neck"] = "neck",
        ["DEF-head"] = "Head",
        ["DEF-shoulder.L"] = "LeftShoulder",
        ["DEF-upper_arm.L"] = "LeftArm",
        ["DEF-forearm.L"] = "LeftForeArm",
        ["DEF-hand.L"] = "LeftHand",
        ["DEF-shoulder.R"] = "RightShoulder",
        ["DEF-upper_arm.R"] = "RightArm",
        ["DEF-forearm.R"] = "RightForeArm",
        ["DEF-hand.R"] = "RightHand",
        ["DEF-thigh.L"] = "LeftUpLeg",
        ["DEF-shin.L"] = "LeftLeg",
        ["DEF-foot.L"] = "LeftFoot",
        ["DEF-toe.L"] = "LeftToeBase",
        ["DEF-thigh.R"] = "RightUpLeg",
        ["DEF-shin.R"] = "RightLeg",
        ["DEF-foot.R"] = "RightFoot",
        ["DEF-toe.R"] = "RightToeBase",
    };

    /// <summary>标准 Mixamo 名 → 猪人 Meshy 骨名（Spine 链顺序不同）。</summary>
    private static readonly Dictionary<string, string> MixamoToPigman = new()
    {
        ["Hips"] = "Hips",
        ["Spine"] = "Spine02",
        ["Spine1"] = "Spine01",
        ["Spine2"] = "Spine",
        ["Neck"] = "neck",
        ["Head"] = "Head",
        ["LeftShoulder"] = "LeftShoulder",
        ["LeftArm"] = "LeftArm",
        ["LeftForeArm"] = "LeftForeArm",
        ["LeftHand"] = "LeftHand",
        ["RightShoulder"] = "RightShoulder",
        ["RightArm"] = "RightArm",
        ["RightForeArm"] = "RightForeArm",
        ["RightHand"] = "RightHand",
        ["LeftUpLeg"] = "LeftUpLeg",
        ["LeftLeg"] = "LeftLeg",
        ["LeftFoot"] = "LeftFoot",
        ["LeftToeBase"] = "LeftToeBase",
        ["RightUpLeg"] = "RightUpLeg",
        ["RightLeg"] = "RightLeg",
        ["RightFoot"] = "RightFoot",
        ["RightToeBase"] = "RightToeBase",
    };

    public static int MergeInto(AnimationPlayer dest, Skeleton3D? destSkel, string sourcePath)
    {
        if (!ResourceLoader.Exists(sourcePath))
            return 0;
        var packed = GD.Load<PackedScene>(sourcePath);
        if (packed is null)
            return 0;

        var tmp = packed.Instantiate<Node>();
        var attached = false;
        if (dest.IsInsideTree() && tmp.GetParent() is null)
        {
            dest.AddChild(tmp);
            attached = true;
        }
        var srcPlayer = RiggedActor3D.FindAnimationPlayer(tmp);
        var srcSkel = RiggedActor3D.FindSkeleton(tmp);
        if (srcPlayer is null)
        {
            if (attached) dest.RemoveChild(tmp);
            tmp.Free();
            return 0;
        }

        var added = 0;
        if (srcSkel is not null && destSkel is not null && LooksLikeUal(srcSkel))
            added = MergeRetargeted(dest, destSkel, srcPlayer, srcSkel, UalToPigman, skipWalkRun: true, skipIdle: true, mixamoDelta: false);
        else if (srcSkel is not null && destSkel is not null && LooksLikeMixamo(srcSkel))
            added = MergeRetargeted(dest, destSkel, srcPlayer, srcSkel, MixamoToPigman, skipWalkRun: true, skipIdle: true, mixamoDelta: true);
        else
            added = RiggedActor3D.CopyClips(dest, srcPlayer);
        if (attached) dest.RemoveChild(tmp);
        tmp.Free();
        return added;
    }

    private static int MergeRetargeted(
        AnimationPlayer dest,
        Skeleton3D destSkel,
        AnimationPlayer srcPlayer,
        Skeleton3D srcSkel,
        Dictionary<string, string> boneMap,
        bool skipWalkRun,
        bool skipIdle,
        bool mixamoDelta)
    {
        var destLib = RiggedActor3D.EnsureLibrary(dest);
        var count = 0;
        foreach (var name in srcPlayer.GetAnimationList())
        {
            var src = srcPlayer.GetAnimation(name);
            if (src is null) continue;
            var leaf = LeafName(name);
            if (skipWalkRun && IsWalkRun(leaf)) continue;
            if (skipIdle && IsIdle(leaf)) continue;
            if (destLib.HasAnimation(leaf) || dest.HasAnimation(name)) continue;
            var mapped = Retarget(src, srcSkel, destSkel, dest, name, boneMap, mixamoDelta);
            if (mapped is null) continue;
            destLib.AddAnimation(leaf, mapped);
            count++;
        }
        return count;
    }

    private static Animation? Retarget(
        Animation src,
        Skeleton3D srcSkel,
        Skeleton3D destSkel,
        AnimationPlayer dest,
        string clipName,
        Dictionary<string, string> boneMap,
        bool mixamoDelta)
    {
        var destNode = dest.IsInsideTree()
            ? dest.GetPathTo(destSkel).ToString()
            : destSkel.Name.ToString();
        if (string.IsNullOrEmpty(destNode) || destNode == ".")
            destNode = destSkel.Name;
        var loop = GuessLoop(src, clipName);
        var srcLen = (float)src.Length;
        var timeScale = 1f;
        if (mixamoDelta && loop == Animation.LoopModeEnum.None && srcLen > 1.6f)
            timeScale = TargetOneShotLength(clipName) / srcLen;
        var mapped = 0;
        var anim = new Animation
        {
            Length = srcLen * timeScale,
            LoopMode = loop,
            Step = (float)src.Step * timeScale,
        };

        for (var i = 0; i < src.GetTrackCount(); i++)
        {
            var srcBoneName = NormalizeBone(BoneOf(src.TrackGetPath(i)));
            if (!boneMap.TryGetValue(srcBoneName, out var destBone))
                continue;
            if (destSkel.FindBone(destBone) < 0)
                continue;
            if (src.TrackGetType(i) is not Animation.TrackType.Rotation3D)
                continue;

            var destIdx = destSkel.FindBone(destBone);
            if (destIdx < 0) continue;
            if (!TryFirstRotation(src, i, out var bind))
                continue;

            var destRest = destSkel.GetBoneRest(destIdx).Basis.GetRotationQuaternion();
            var track = anim.AddTrack(Animation.TrackType.Rotation3D);
            anim.TrackSetPath(track, new NodePath($"{destNode}:{destBone}"));

            var keys = src.TrackGetKeyCount(i);
            for (var k = 0; k < keys; k++)
            {
                var time = (float)src.TrackGetKeyTime(i, k);
                if (!TryRotation(src.TrackGetKeyValue(i, k), out var qSrc))
                    continue;
                // Mixamo 第一帧带着 Blender -90°。只把相对这一帧的变化叠到猪人 rest 上。
                var qOut = mixamoDelta
                    ? destRest * bind.Inverse() * qSrc
                    : destRest
                      * srcSkel.GetBoneRest(srcSkel.FindBone(BoneOf(src.TrackGetPath(i)))).Basis
                          .GetRotationQuaternion().Inverse()
                      * qSrc;
                anim.RotationTrackInsertKey(track, time * timeScale, qOut.Normalized());
                mapped++;
            }
        }

        return mapped > 0 ? anim : null;
    }

    private static float TargetOneShotLength(string clipName)
    {
        var n = clipName.ToLowerInvariant();
        if (n.Contains("death")) return 1.8f;
        if (n.Contains("hit")) return 0.45f;
        if (n.Contains("punch") || n.Contains("attack")) return 0.7f;
        return 1.2f;
    }

    private static bool TryFirstRotation(Animation src, int track, out Quaternion q)
    {
        q = Quaternion.Identity;
        var keys = src.TrackGetKeyCount(track);
        for (var k = 0; k < keys; k++)
        {
            if (TryRotation(src.TrackGetKeyValue(track, k), out q))
                return true;
        }
        return false;
    }

    private static bool TryRotation(Variant value, out Quaternion q)
    {
        q = Quaternion.Identity;
        if (value.VariantType == Variant.Type.Quaternion)
        {
            q = value.AsQuaternion();
            return true;
        }
        if (value.VariantType == Variant.Type.Basis)
        {
            q = value.AsBasis().GetRotationQuaternion();
            return true;
        }
        return false;
    }

    private static Animation.LoopModeEnum GuessLoop(Animation src, string clipName)
    {
        if (src.LoopMode != Animation.LoopModeEnum.None)
            return src.LoopMode;
        var n = clipName.ToLowerInvariant();
        return n.Contains("loop") || n.Contains("idle") || n.Contains("walk") || n.Contains("run")
            ? Animation.LoopModeEnum.Linear
            : Animation.LoopModeEnum.None;
    }

    private static bool LooksLikeUal(Skeleton3D skel) => skel.FindBone("DEF-hips") >= 0;

    private static bool LooksLikeMixamo(Skeleton3D skel)
    {
        for (var i = 0; i < skel.GetBoneCount(); i++)
        {
            var name = skel.GetBoneName(i);
            if (name.StartsWith("mixamorig", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return skel.FindBone("Spine1") >= 0 && skel.FindBone("Hips") >= 0;
    }

    private static bool IsWalkRun(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("walk") || n.Contains("run") || n.Contains("jog")
            || n.Contains("sprint") || n.Contains("gallop");
    }

    private static bool IsIdle(string name) =>
        name.Contains("idle", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBone(string name)
    {
        if (name.StartsWith("mixamorig:", StringComparison.OrdinalIgnoreCase))
            return name[10..];
        if (name.StartsWith("mixamorig_", StringComparison.OrdinalIgnoreCase))
            return name[10..];
        return name;
    }

    private static string BoneOf(NodePath path)
    {
        if (path.GetSubNameCount() > 0)
            return path.GetSubName(path.GetSubNameCount() - 1);
        return path.GetNameCount() > 0 ? path.GetName(path.GetNameCount() - 1) : "";
    }

    private static string LeafName(string name)
    {
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        var bar = name.LastIndexOf('|');
        if (bar >= 0) name = name[(bar + 1)..];
        return name;
    }
}
