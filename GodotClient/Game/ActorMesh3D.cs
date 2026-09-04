using System;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 3D 角色/道具占位 mesh。没有 glTF 时用胶囊+头；导入模型后把子节点换成 PackedScene 即可。
/// </summary>
public static class ActorMesh3D
{
    public static Node3D Create(EntityStyle style)
    {
        var root = new Node3D();
        var mat = ToonMaterials.Create(style.Color);
        root.SetMeta("toon", mat);

        if (style.IsTree)
            BuildTree(root, mat);
        else if (style.Radius >= 9f)
            BuildActor(root, mat, style.Radius);
        else
            BuildProp(root, mat, style);

        return root;
    }

    public static ShaderMaterial? MaterialOf(Node3D root)
    {
        if (!root.HasMeta("toon")) return null;
        return root.GetMeta("toon").AsGodotObject() as ShaderMaterial;
    }

    public static void ApplyStyle(Node3D root, EntityStyle style)
    {
        var mat = MaterialOf(root);
        if (mat is not null) ToonMaterials.SetAlbedo(mat, style.Color);
        root.Scale = Vector3.One * ScaleOf(style);
    }

    private static float ScaleOf(EntityStyle style)
    {
        if (style.IsTree) return MathF.Max(0.7f, style.Radius / 14f);
        if (style.Radius >= 9f) return MathF.Max(0.75f, style.Radius / 10f);
        return MathF.Max(0.4f, style.Radius / 12f);
    }

    private static void BuildActor(Node3D root, Material mat, float radius)
    {
        var tall = radius >= 10f;
        var bodyH = tall ? 0.78f : 0.62f;
        var bodyR = tall ? 0.2f : 0.18f;
        root.AddChild(new MeshInstance3D
        {
            Name = "Body",
            Position = new Vector3(0, bodyH * 0.5f, 0),
            Mesh = new CapsuleMesh { Radius = bodyR, Height = bodyH },
            MaterialOverride = mat,
        });
        root.AddChild(new MeshInstance3D
        {
            Name = "Head",
            Position = new Vector3(0, bodyH + (tall ? 0.16f : 0.14f), 0),
            Mesh = new SphereMesh { Radius = tall ? 0.17f : 0.15f, Height = tall ? 0.34f : 0.3f },
            MaterialOverride = mat,
        });
    }

    private static void BuildTree(Node3D root, Material mat)
    {
        root.AddChild(new MeshInstance3D
        {
            Name = "Trunk",
            Position = new Vector3(0, 0.35f, 0),
            Mesh = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.14f, Height = 0.7f },
            MaterialOverride = mat,
        });
        root.AddChild(new MeshInstance3D
        {
            Name = "Crown",
            Position = new Vector3(0, 0.95f, 0),
            Mesh = new SphereMesh { Radius = 0.42f, Height = 0.84f },
            MaterialOverride = mat,
        });
    }

    private static void BuildProp(Node3D root, Material mat, EntityStyle style)
    {
        var h = style.IsFire ? 0.55f : 0.32f;
        var w = style.IsFire ? 0.28f : 0.34f;
        root.AddChild(new MeshInstance3D
        {
            Name = "Prop",
            Position = new Vector3(0, h * 0.5f, 0),
            Mesh = new BoxMesh { Size = new Vector3(w, h, w) },
            MaterialOverride = mat,
        });
    }
}
