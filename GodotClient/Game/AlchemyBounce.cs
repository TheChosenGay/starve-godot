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

    public static Shader Shader
    {
        get
        {
            _shader ??= ShaderLibrary.Load(ShaderLibrary.AlchemyBounce);
            return _shader;
        }
    }

    public static void BindTree(Node root, List<ShaderMaterial> into)
    {
        into.Clear();
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            if (child is MultiMeshInstance3D) continue;
            if (IsFxMesh(child, root)) continue;
            mesh.ExtraCullMargin = 0.75f;
            var footY = mesh.GetAabb().Position.Y;
            var surfaces = mesh.Mesh?.GetSurfaceCount() ?? 0;
            if (surfaces <= 0)
            {
                var mat = Wrap(SourceMaterial(mesh, 0), footY);
                mesh.MaterialOverride = mat;
                into.Add(mat);
                continue;
            }

            for (var i = 0; i < surfaces; i++)
            {
                var mat = Wrap(SourceMaterial(mesh, i), footY);
                mesh.SetSurfaceOverrideMaterial(i, mat);
                into.Add(mat);
            }
        }
    }

    /// <summary>不换 shader，只把现有材质收进抖动时间轴（Toon 套上后仍能抖）。</summary>
    public static void BindExisting(Node root, List<ShaderMaterial> into)
    {
        into.Clear();
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            if (child is MultiMeshInstance3D) continue;
            if (IsFxMesh(child, root)) continue;
            mesh.ExtraCullMargin = 0.75f;
            var footY = mesh.GetAabb().Position.Y;
            if (mesh.MaterialOverride is ShaderMaterial over)
            {
                BindTime(over, footY);
                into.Add(over);
                continue;
            }

            var surfaces = mesh.Mesh?.GetSurfaceCount() ?? 0;
            for (var i = 0; i < surfaces; i++)
            {
                if (mesh.GetSurfaceOverrideMaterial(i) is not ShaderMaterial surface) continue;
                BindTime(surface, footY);
                into.Add(surface);
            }
        }
    }

    public static void SetTime(IReadOnlyList<ShaderMaterial> mats, float bounceT)
    {
        for (var i = 0; i < mats.Count; i++)
        {
            mats[i].SetShaderParameter("bounce_t", bounceT);
            if (mats[i].NextPass is ShaderMaterial outline)
                outline.SetShaderParameter("bounce_t", bounceT);
        }
    }

    private static Material? SourceMaterial(MeshInstance3D mesh, int surface)
    {
        if (mesh.GetSurfaceOverrideMaterial(surface) is { } over)
            return over;
        if (mesh.Mesh is { } meshRes && surface < meshRes.GetSurfaceCount())
            return meshRes.SurfaceGetMaterial(surface);
        return mesh.GetActiveMaterial(surface);
    }

    private static ShaderMaterial Wrap(Material? src, float footY)
    {
        var mat = new ShaderMaterial { Shader = Shader };
        mat.SetShaderParameter("bounce_t", 0f);
        mat.SetShaderParameter("foot_y", footY);
        if (src is ShaderMaterial sm)
        {
            var srcAlbedo = sm.GetShaderParameter("albedo");
            mat.SetShaderParameter("albedo", srcAlbedo.VariantType == Variant.Type.Color
                ? srcAlbedo.AsColor()
                : new Color(0.62f, 0.48f, 0.34f));
            if (sm.GetShaderParameter("albedo_tex").AsGodotObject() is Texture2D srcTex)
            {
                mat.SetShaderParameter("use_albedo_tex", true);
                mat.SetShaderParameter("albedo_tex", srcTex);
            }
            return mat;
        }

        if (src is StandardMaterial3D std)
        {
            CopyLit(mat, std);
            return mat;
        }

        if (src is BaseMaterial3D baseMat)
        {
            CopyLit(mat, baseMat);
            return mat;
        }

        if (ToonMaterials.ExtractAlbedoTex(src) is { } tex)
        {
            mat.SetShaderParameter("albedo", ToonMaterials.ExtractAlbedoColor(src));
            mat.SetShaderParameter("use_albedo_tex", true);
            mat.SetShaderParameter("albedo_tex", tex);
            return mat;
        }

        mat.SetShaderParameter("albedo", new Color(0.62f, 0.48f, 0.34f));
        return mat;
    }

    private static void CopyLit(ShaderMaterial mat, BaseMaterial3D src)
    {
        mat.SetShaderParameter("albedo", src.AlbedoColor);
        if (src.AlbedoTexture is { } albedo)
        {
            mat.SetShaderParameter("use_albedo_tex", true);
            mat.SetShaderParameter("albedo_tex", albedo);
        }

        if (src is StandardMaterial3D std)
        {
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
        }
        else
        {
            mat.SetShaderParameter("metallic", src.Metallic);
            mat.SetShaderParameter("roughness", src.Roughness);
        }
    }

    private static void BindTime(ShaderMaterial mat, float footY)
    {
        mat.SetShaderParameter("foot_y", footY);
        mat.SetShaderParameter("bounce_t", 0f);
        if (mat.NextPass is ShaderMaterial outline)
        {
            outline.SetShaderParameter("foot_y", footY);
            outline.SetShaderParameter("bounce_t", 0f);
        }
    }

    private static bool IsFxMesh(Node node, Node root)
    {
        for (var p = node; p is not null && p != root; p = p.GetParent())
        {
            if (p is AlchemyBounceFx or LiquidRiseFx) return true;
        }
        return false;
    }
}

/// <summary>沙盘用：把抖动套到任意网格，离开时还原材质。</summary>
public partial class AlchemyBounceFx : Node
{
    public const string NodeName = "AlchemyBounceFx";

    public bool Loop { get; set; }

    private readonly List<ShaderMaterial> _mats = [];
    private readonly List<(MeshInstance3D Mesh, int Surface, Material? Prev)> _prev = [];
    private float _t;
    private float _gap;
    private bool _engineOnly;

    public static void Attach(Node3D host, bool loop)
    {
        Detach(host);
        var fx = new AlchemyBounceFx { Name = NodeName, Loop = loop };
        host.AddChild(fx);
        if (host is AlchemyEngine3D engine)
        {
            fx._engineOnly = true;
            engine.PlayBounce();
            fx.SetProcess(loop);
            return;
        }

        fx.Capture(host);
        AlchemyBounce.BindTree(host, fx._mats);
        fx.SetProcess(true);
    }

    public static void Detach(Node3D host)
    {
        if (host.GetNodeOrNull<AlchemyBounceFx>(NodeName) is { } fx)
            fx.RestoreAndFree();
    }

    public override void _Process(double delta)
    {
        if (_engineOnly)
        {
            if (!Loop) return;
            _gap -= (float)delta;
            if (_gap > 0f) return;
            if (GetParent() is AlchemyEngine3D engine && !engine.IsBouncing)
            {
                engine.PlayBounce();
                _gap = 2.2f;
            }
            return;
        }

        _t += (float)delta;
        if (_t >= AlchemyBounce.Duration)
        {
            _t = 0f;
            AlchemyBounce.SetTime(_mats, 0f);
            if (!Loop)
            {
                SetProcess(false);
                return;
            }
        }

        AlchemyBounce.SetTime(_mats, _t);
    }

    private void Capture(Node host)
    {
        foreach (var child in host.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh || child is MultiMeshInstance3D) continue;
            var surfaces = mesh.Mesh?.GetSurfaceCount() ?? 0;
            if (surfaces <= 0)
            {
                _prev.Add((mesh, -1, mesh.MaterialOverride));
                continue;
            }

            for (var i = 0; i < surfaces; i++)
                _prev.Add((mesh, i, mesh.GetSurfaceOverrideMaterial(i)));
        }
    }

    private void RestoreAndFree()
    {
        foreach (var (mesh, surface, prev) in _prev)
        {
            if (!GodotObject.IsInstanceValid(mesh)) continue;
            if (surface < 0)
                mesh.MaterialOverride = prev;
            else
                mesh.SetSurfaceOverrideMaterial(surface, prev);
        }

        QueueFree();
    }
}
