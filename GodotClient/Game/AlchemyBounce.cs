using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 炼金引擎靠近抖动：顶点 squash / stretch。时间轴在 shader 里，C# 只推进 <c>bounce_t</c>。
/// </summary>
public static class AlchemyBounce
{
    public const float Duration = 1.24f;

    private static Shader? _shader;

    public static Shader Shader => _shader ??= ShaderLibrary.Load(ShaderLibrary.AlchemyBounce);

    public static void BindTree(Node root, List<ShaderMaterial> into)
    {
        into.Clear();
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            mesh.ExtraCullMargin = 0.75f;
            var footY = mesh.GetAabb().Position.Y;
            var surfaces = mesh.Mesh?.GetSurfaceCount() ?? 0;
            if (surfaces <= 0)
            {
                var mat = Wrap(mesh.GetActiveMaterial(0), footY);
                mesh.MaterialOverride = mat;
                into.Add(mat);
                continue;
            }

            for (var i = 0; i < surfaces; i++)
            {
                var mat = Wrap(mesh.GetActiveMaterial(i), footY);
                mesh.SetSurfaceOverrideMaterial(i, mat);
                into.Add(mat);
            }
        }
    }

    public static void SetTime(IReadOnlyList<ShaderMaterial> mats, float bounceT)
    {
        for (var i = 0; i < mats.Count; i++)
            mats[i].SetShaderParameter("bounce_t", bounceT);
    }

    private static ShaderMaterial Wrap(Material? src, float footY)
    {
        var mat = new ShaderMaterial { Shader = Shader };
        mat.SetShaderParameter("bounce_t", 0f);
        mat.SetShaderParameter("foot_y", footY);
        if (src is not StandardMaterial3D std)
        {
            mat.SetShaderParameter("albedo", new Color(0.62f, 0.48f, 0.34f));
            return mat;
        }

        mat.SetShaderParameter("albedo", std.AlbedoColor);
        if (std.AlbedoTexture is { } albedo)
        {
            mat.SetShaderParameter("use_albedo_tex", true);
            mat.SetShaderParameter("albedo_tex", albedo);
        }

        var mr = std.MetallicTexture ?? std.RoughnessTexture;
        if (mr is not null)
        {
            mat.SetShaderParameter("use_mr_tex", true);
            mat.SetShaderParameter("mr_tex", mr);
            mat.SetShaderParameter("metallic_channel", (int)std.MetallicTextureChannel);
            mat.SetShaderParameter("roughness_channel", (int)std.RoughnessTextureChannel);
        }

        mat.SetShaderParameter("metallic", std.Metallic);
        mat.SetShaderParameter("roughness", std.Roughness);
        if (std.NormalEnabled && std.NormalTexture is { } normal)
        {
            mat.SetShaderParameter("use_normal_tex", true);
            mat.SetShaderParameter("normal_tex", normal);
            mat.SetShaderParameter("normal_scale", std.NormalScale);
        }

        return mat;
    }
}
