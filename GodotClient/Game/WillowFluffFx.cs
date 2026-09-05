using System;
using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>
/// 柳絮 / 水网上浮：从网格表面按面积采样，公告板片沿法线剥离再向上漂。
/// 颜色先取落点表面色，再和统一 tint 按 <see cref="WillowFluffStyle.ColorMix"/> 混合。
/// </summary>
public sealed class WillowFluffStyle
{
    public float ColorMix = 0.22f;
    public Color Tint = new(0.95f, 0.90f, 0.78f);
    public float Lift = 1.55f;
    public float Sway = 0.28f;
    public float Size = 0.055f;
    public float Glow = 0.85f;
    public float Duration = 2.4f;
    public int Count = 96;

    public WillowFluffStyle Clone() => new()
    {
        ColorMix = ColorMix,
        Tint = Tint,
        Lift = Lift,
        Sway = Sway,
        Size = Size,
        Glow = Glow,
        Duration = Duration,
        Count = Count,
    };

    public static WillowFluffStyle Place() => new()
    {
        ColorMix = 0.18f,
        Tint = new Color(0.93f, 0.88f, 0.72f),
        Lift = 1.35f,
        Sway = 0.24f,
        Size = 0.05f,
        Glow = 0.9f,
        Duration = 2.2f,
        Count = 88,
    };
}

public partial class WillowFluffFx : Node3D
{
    public const string NodeName = "WillowFluffFx";

    public WillowFluffStyle Style { get; private set; } = new();
    public bool Loop { get; private set; }

    private ShaderMaterial _mat = null!;
    private MultiMeshInstance3D _mmi = null!;
    private float _playT;
    private static Shader? _shader;

    public static Shader Shader => _shader ??= ShaderLibrary.Load(ShaderLibrary.WillowFluff);

    public static WillowFluffFx? Find(Node3D host) =>
        host.GetNodeOrNull<WillowFluffFx>(NodeName);

    public static void Detach(Node3D host)
    {
        if (Find(host) is { } fx)
            fx.QueueFree();
    }

    public static WillowFluffFx PlayOn(Node3D host, WillowFluffStyle? style = null, bool loop = false)
    {
        Detach(host);
        var fx = new WillowFluffFx { Name = NodeName };
        host.AddChild(fx);
        fx.Rebuild(style ?? WillowFluffStyle.Place(), loop);
        return fx;
    }

    public void Rebuild(WillowFluffStyle style, bool loop)
    {
        Style = style.Clone();
        Loop = loop;
        _playT = 0f;
        EnsureMesh();
        var samples = SampleHost(GetParent() as Node3D ?? this, Mathf.Clamp(Style.Count, 8, 280));
        FillMultiMesh(samples);
        PushUniforms();
        SetProcess(!loop);
    }

    public void Replay()
    {
        _playT = 0f;
        PushUniforms();
        if (!Loop)
            SetProcess(true);
    }

    public void ApplyStyle(WillowFluffStyle style)
    {
        var countChanged = style.Count != Style.Count;
        Style = style.Clone();
        if (countChanged)
            Rebuild(Style, Loop);
        else
            PushUniforms();
    }

    public override void _Process(double delta)
    {
        if (Loop) return;
        _playT += (float)delta;
        _mat.SetShaderParameter("play_t", _playT);
        if (_playT > Style.Duration * 1.55f)
        {
            SetProcess(false);
            QueueFree();
        }
    }

    private void EnsureMesh()
    {
        if (_mmi is not null)
            return;
        _mat = new ShaderMaterial { Shader = Shader };
        _mmi = new MultiMeshInstance3D
        {
            Name = "Flakes",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 2.4f,
            MaterialOverride = _mat,
        };
        AddChild(_mmi);
    }

    private void PushUniforms()
    {
        _mat.SetShaderParameter("play_t", _playT);
        _mat.SetShaderParameter("duration", Style.Duration);
        _mat.SetShaderParameter("loop_mode", Loop ? 1f : 0f);
        _mat.SetShaderParameter("lift", Style.Lift);
        _mat.SetShaderParameter("sway", Style.Sway);
        _mat.SetShaderParameter("size", Style.Size);
        _mat.SetShaderParameter("glow", Style.Glow);
        _mat.SetShaderParameter("color_mix", Style.ColorMix);
        _mat.SetShaderParameter("tint", Style.Tint);
    }

    private void FillMultiMesh(IReadOnlyList<FluffSample> samples)
    {
        var count = Math.Max(1, samples.Count);
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = new Vector2(1f, 1f) },
            InstanceCount = count,
        };
        Texture2D? sharedTex = null;
        var texOk = true;
        for (var i = 0; i < count; i++)
        {
            var s = samples[i];
            mm.SetInstanceTransform(i, new Transform3D(BasisFromNormal(s.Normal), s.Local));
            mm.SetInstanceColor(i, s.Color);
            mm.SetInstanceCustomData(i, new Color(s.Uv.X, s.Uv.Y, s.Seed, 0f));
            if (s.Albedo is null)
                texOk = false;
            else if (sharedTex is null)
                sharedTex = s.Albedo;
            else if (sharedTex != s.Albedo)
                texOk = false;
        }

        _mmi.Multimesh = mm;
        if (texOk && sharedTex is not null)
        {
            _mat.SetShaderParameter("use_albedo_tex", true);
            _mat.SetShaderParameter("albedo_tex", sharedTex);
        }
        else
        {
            _mat.SetShaderParameter("use_albedo_tex", false);
        }
    }

    private readonly record struct FluffSample(
        Vector3 Local,
        Vector3 Normal,
        Vector2 Uv,
        Color Color,
        float Seed,
        Texture2D? Albedo);

    private static List<FluffSample> SampleHost(Node3D host, int count)
    {
        var tris = new List<Tri>();
        foreach (var child in host.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mi || mi.Mesh is null) continue;
            if (IsFxMesh(mi, host)) continue;
            CollectTris(host, mi, tris);
        }

        var rng = new Random(unchecked((int)host.GetInstanceId() ^ (int)Time.GetTicksMsec()));
        var samples = new List<FluffSample>(count);
        if (tris.Count == 0)
        {
            for (var i = 0; i < count; i++)
            {
                var p = new Vector3(
                    (rng.NextSingle() - 0.5f) * 0.5f,
                    rng.NextSingle() * 0.8f,
                    (rng.NextSingle() - 0.5f) * 0.5f);
                samples.Add(new FluffSample(p, Vector3.Up, Vector2.Zero, new Color(0.72f, 0.62f, 0.48f), rng.NextSingle(), null));
            }
            return samples;
        }

        var total = 0f;
        foreach (var t in tris)
            total += t.Area;
        if (total <= 1e-8f)
            total = tris.Count;

        for (var i = 0; i < count; i++)
        {
            var pick = rng.NextSingle() * total;
            var acc = 0f;
            var tri = tris[0];
            foreach (var t in tris)
            {
                acc += t.Area;
                if (pick <= acc)
                {
                    tri = t;
                    break;
                }
            }

            var r1 = MathF.Sqrt(rng.NextSingle());
            var r2 = rng.NextSingle();
            var w0 = 1f - r1;
            var w1 = r1 * (1f - r2);
            var w2 = r1 * r2;
            var local = tri.A * w0 + tri.B * w1 + tri.C * w2;
            var uv = tri.Ua * w0 + tri.Ub * w1 + tri.Uc * w2;
            var nrm = (tri.B - tri.A).Cross(tri.C - tri.A);
            if (nrm.LengthSquared() < 1e-10f) nrm = Vector3.Up;
            else nrm = nrm.Normalized();
            samples.Add(new FluffSample(local, nrm, uv, SampleColor(tri.Mat, uv), rng.NextSingle(), AlbedoTexOf(tri.Mat)));
        }

        return samples;
    }

    private readonly record struct Tri(
        Vector3 A,
        Vector3 B,
        Vector3 C,
        Vector2 Ua,
        Vector2 Ub,
        Vector2 Uc,
        float Area,
        Material? Mat);

    private static void CollectTris(Node3D host, MeshInstance3D mi, List<Tri> into)
    {
        var mesh = mi.Mesh;
        if (mesh is null) return;
        var toHost = host.GlobalTransform.AffineInverse() * mi.GlobalTransform;
        var surfaces = mesh.GetSurfaceCount();
        for (var s = 0; s < surfaces; s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            if (arrays.Count == 0) continue;
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            if (verts.Length < 3) continue;
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var mat = mi.GetActiveMaterial(s) ?? mi.MaterialOverride;
            if (indices.Length >= 3)
            {
                for (var i = 0; i + 2 < indices.Length; i += 3)
                    AddTri(into, toHost, verts, uvs, indices[i], indices[i + 1], indices[i + 2], mat);
            }
            else
            {
                for (var i = 0; i + 2 < verts.Length; i += 3)
                    AddTri(into, toHost, verts, uvs, i, i + 1, i + 2, mat);
            }
        }
    }

    private static void AddTri(
        List<Tri> into,
        Transform3D toHost,
        Vector3[] verts,
        Vector2[] uvs,
        int ia,
        int ib,
        int ic,
        Material? mat)
    {
        if (ia < 0 || ib < 0 || ic < 0 || ia >= verts.Length || ib >= verts.Length || ic >= verts.Length)
            return;
        var a = toHost * verts[ia];
        var b = toHost * verts[ib];
        var c = toHost * verts[ic];
        var area = (b - a).Cross(c - a).Length() * 0.5f;
        if (area < 1e-7f) return;
        var ua = ia < uvs.Length ? uvs[ia] : Vector2.Zero;
        var ub = ib < uvs.Length ? uvs[ib] : Vector2.Zero;
        var uc = ic < uvs.Length ? uvs[ic] : Vector2.Zero;
        into.Add(new Tri(a, b, c, ua, ub, uc, area, mat));
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

    private static Texture2D? AlbedoTexOf(Material? mat)
    {
        if (mat is StandardMaterial3D std)
            return std.AlbedoTexture;
        if (mat is ShaderMaterial sm && sm.GetShaderParameter("albedo_tex").AsGodotObject() is Texture2D tex)
            return tex;
        return null;
    }

    private static Color SampleTex(Texture2D tex, Vector2 uv)
    {
        try
        {
            var img = tex.GetImage();
            if (img is null) return Colors.White;
            if (img.IsCompressed())
                img.Decompress();
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
            if (p is WillowFluffFx or AlchemyBounceFx) return true;
            if (p.Name == "SnowCap") return true;
        }
        return false;
    }

    private static Basis BasisFromNormal(Vector3 n)
    {
        n = n.LengthSquared() < 1e-8f ? Vector3.Up : n.Normalized();
        var x = n.Cross(Vector3.Up);
        if (x.LengthSquared() < 1e-6f)
            x = n.Cross(Vector3.Forward);
        x = x.Normalized();
        var z = x.Cross(n).Normalized();
        return new Basis(x, n, z);
    }
}
