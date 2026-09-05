using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 飘液：贴表面的分阶离散覆膜，膜散时块面摇曳上飘。
/// <see cref="LiquidRiseStyle.BakeStatic"/> 用引擎快照把蒙皮姿势烤成静态网格，升空不再跟骨骼走。
/// </summary>
public sealed class LiquidRiseStyle
{
    public float ColorMix = 0.82f;
    public Color Tint = new(0.32f, 0.12f, 0.48f, 0.78f);
    public float Thickness = 0.01f;
    public float Rise = 0.96f;
    public float Swirl = 1.4f;
    public float Ripple = 2.4f;
    public float Glow = 1.49f;
    public float Dissolve = 8.6f;
    public float Duration = 1.75f;
    public bool BakeStatic;

    public LiquidRiseStyle Clone() => new()
    {
        ColorMix = ColorMix,
        Tint = Tint,
        Thickness = Thickness,
        Rise = Rise,
        Swirl = Swirl,
        Ripple = Ripple,
        Glow = Glow,
        Dissolve = Dissolve,
        Duration = Duration,
        BakeStatic = BakeStatic,
    };

    public static LiquidRiseStyle Place() => new();
}

public partial class LiquidRiseFx : Node3D
{
    public const string NodeName = "LiquidRiseFx";

    public LiquidRiseStyle Style { get; private set; } = new();
    public bool Loop { get; private set; }

    private ShaderMaterial _mat = null!;
    private readonly List<MeshInstance3D> _films = [];
    private float _playT;
    private static Shader? _shader;
    private static readonly Dictionary<ulong, Image> _imgCache = [];

    public static Shader Shader => _shader ??= ShaderLibrary.Load(ShaderLibrary.LiquidRise);

    public static LiquidRiseFx? Find(Node3D host) =>
        host.GetNodeOrNull<LiquidRiseFx>(NodeName);

    public static void Detach(Node3D host)
    {
        if (Find(host) is { } fx)
            fx.QueueFree();
    }

    public static LiquidRiseFx PlayOn(Node3D host, LiquidRiseStyle? style = null, bool loop = false)
    {
        Detach(host);
        var fx = new LiquidRiseFx { Name = NodeName };
        host.AddChild(fx);
        fx.Rebuild(style ?? LiquidRiseStyle.Place(), loop);
        return fx;
    }

    public void Rebuild(LiquidRiseStyle style, bool loop)
    {
        Style = style.Clone();
        Loop = loop;
        _playT = 0f;
        EnsureMaterial();
        ClearVisuals();
        BuildFilm();
        PushUniforms();
        SetProcess(true);
    }

    public void Replay()
    {
        _playT = 0f;
        PushUniforms();
        SetProcess(true);
    }

    public void SetLoop(bool loop)
    {
        Loop = loop;
        _mat.SetShaderParameter("loop_mode", loop ? 1f : 0f);
        if (!loop)
            _playT = 0f;
        PushUniforms();
        SetProcess(true);
    }

    public void ApplyStyle(LiquidRiseStyle style)
    {
        var bakeChanged = style.BakeStatic != Style.BakeStatic;
        Style = style.Clone();
        if (bakeChanged)
        {
            Rebuild(Style, Loop);
            return;
        }

        PushUniforms();
    }

    public override void _Process(double delta)
    {
        _playT += (float)delta;
        var life = MathF.Max(Style.Duration, 0.08f);
        if (Loop)
        {
            if (_playT >= life)
                _playT -= life;
        }
        else if (_playT >= life)
        {
            SetProcess(false);
            QueueFree();
            return;
        }

        _mat.SetShaderParameter("play_t", _playT);
    }

    private void EnsureMaterial()
    {
        _mat ??= new ShaderMaterial { Shader = Shader, RenderPriority = 1 };
    }

    public override void _ExitTree()
    {
        ClearVisuals();
    }

    private void ClearVisuals()
    {
        foreach (var film in _films)
        {
            if (GodotObject.IsInstanceValid(film))
                film.QueueFree();
        }

        _films.Clear();
        foreach (var child in GetChildren())
            child.QueueFree();
    }

    private void PushUniforms()
    {
        _mat.SetShaderParameter("play_t", _playT);
        _mat.SetShaderParameter("duration", Style.Duration);
        _mat.SetShaderParameter("loop_mode", Loop ? 1f : 0f);
        _mat.SetShaderParameter("thickness", Style.Thickness);
        _mat.SetShaderParameter("rise", Style.Rise);
        _mat.SetShaderParameter("swirl", Style.Swirl);
        _mat.SetShaderParameter("ripple", Style.Ripple);
        _mat.SetShaderParameter("glow", Style.Glow);
        _mat.SetShaderParameter("dissolve_scale", Style.Dissolve);
        _mat.SetShaderParameter("color_mix", Style.ColorMix);
        _mat.SetShaderParameter("tint", Style.Tint);
    }

    private void BuildFilm()
    {
        var host = GetParent() as Node3D ?? this;
        foreach (var mi in SourceMeshes(host))
        {
            var posed = Style.BakeStatic ? BakePosed(mi) : null;
            var copy = new MeshInstance3D
            {
                Name = "Film",
                Mesh = posed ?? mi.Mesh,
                Transform = Transform3D.Identity,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                ExtraCullMargin = Style.Rise + 3.2f,
                CustomAabb = new Aabb(new Vector3(-3.2f, -1.4f, -3.2f), new Vector3(6.4f, 7.2f, 6.4f)),
                MaterialOverride = _mat,
            };
            if (posed is null)
                copy.Skin = mi.Skin;
            mi.AddChild(copy);
            if (posed is null && FindSkeleton(mi) is { } skel)
                copy.Skeleton = copy.GetPathTo(skel);
            copy.SetInstanceShaderParameter("seed", (float)(mi.GetInstanceId() % 997) * 0.001f);
            copy.SetInstanceShaderParameter("surface_color", AverageColor(mi));
            _films.Add(copy);
        }
    }

    /// <summary>
    /// 用引擎正在渲染的骨骼矩阵烤姿势。手算再乘世界矩阵会和猪人 Armature 的 0.01 对不齐，塌到脚下。
    /// </summary>
    private static ArrayMesh? BakePosed(MeshInstance3D mi)
    {
        if (mi.Mesh is null || mi.Skin is null)
            return null;

        try
        {
            var baked = mi.BakeMeshFromCurrentSkeletonPose();
            if (baked is null || baked.GetSurfaceCount() <= 0)
                return null;

            var srcSize = mi.GetAabb().Size.Length();
            var dstSize = baked.GetAabb().Size.Length();
            if (srcSize > 1e-4f && dstSize < srcSize * 0.08f)
                return null;

            return baked;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Skeleton3D? FindSkeleton(MeshInstance3D mi)
    {
        if (mi.GetParent() is Skeleton3D parent)
            return parent;
        var path = mi.Skeleton;
        if (path.IsEmpty) return null;
        return mi.GetNodeOrNull<Skeleton3D>(path);
    }

    private static IEnumerable<MeshInstance3D> SourceMeshes(Node3D host)
    {
        if (host is MeshInstance3D self && self.Mesh is not null)
            yield return self;
        foreach (var child in host.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mi || mi.Mesh is null) continue;
            if (IsFxMesh(mi, host)) continue;
            yield return mi;
        }
    }

    private static Color AverageColor(MeshInstance3D mi)
    {
        var mat = mi.GetActiveMaterial(0) ?? mi.MaterialOverride;
        return SampleColor(mat, new Vector2(0.5f, 0.5f));
    }

    private static Color SampleColor(Material? mat, Vector2 uv)
    {
        if (mat is StandardMaterial3D std)
        {
            var c = std.AlbedoColor;
            if (std.AlbedoTexture is { } tex)
                return c * SampleTex(tex, uv);
            return c;
        }

        if (mat is ShaderMaterial sm)
        {
            try
            {
                var albedo = sm.GetShaderParameter("albedo");
                var c = albedo.VariantType == Variant.Type.Color ? albedo.AsColor() : Colors.White;
                if (sm.GetShaderParameter("albedo_tex").AsGodotObject() is Texture2D tex)
                    return c * SampleTex(tex, uv);
                var mid = sm.GetShaderParameter("color_mid");
                if (mid.VariantType == Variant.Type.Color)
                    return mid.AsColor();
                return c;
            }
            catch
            {
                return new Color(0.72f, 0.62f, 0.48f);
            }
        }

        return new Color(0.72f, 0.62f, 0.48f);
    }

    private static Color SampleTex(Texture2D tex, Vector2 uv)
    {
        try
        {
            var id = tex.GetInstanceId();
            if (!_imgCache.TryGetValue(id, out var img))
            {
                img = tex.GetImage();
                if (img is null) return Colors.White;
                if (img.IsCompressed())
                    img.Decompress();
                _imgCache[id] = img;
            }

            var x = Mathf.Clamp(Mathf.FloorToInt(Mathf.PosMod(uv.X, 1f) * img.GetWidth()), 0, img.GetWidth() - 1);
            var y = Mathf.Clamp(Mathf.FloorToInt(Mathf.PosMod(uv.Y, 1f) * img.GetHeight()), 0, img.GetHeight() - 1);
            return img.GetPixel(x, y);
        }
        catch
        {
            return Colors.White;
        }
    }

    private static bool IsFxMesh(Node node, Node host)
    {
        if (node is MultiMeshInstance3D) return true;
        for (var p = node; p is not null && p != host; p = p.GetParent())
        {
            if (p is AlchemyBounceFx or LiquidRiseFx) return true;
            var name = p.Name.ToString();
            if (name is "SnowCap" or "Veil" or "Tentacle" or "Film" or "Wisp" or "PickMark") return true;
        }

        return false;
    }
}
