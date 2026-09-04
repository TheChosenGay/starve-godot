using System.Collections.Generic;
using Godot;

namespace GodotClient.Game;

/// <summary>运行时 toon 调参；角色与地形共用这一组滑条语义。</summary>
public sealed class ToonStyle
{
    public float Bands = 3f;
    public float Rim = 0.22f;
    public float ShadeMin = 0.22f;
    public float Fill = 0.18f;
    public Color ShadowTint = new(0.38f, 0.42f, 0.62f);
    public float OutlineWidth = 0.022f;
    public Color OutlineColor = new(0.07f, 0.05f, 0.09f);

    public ToonStyle Clone() => new()
    {
        Bands = Bands,
        Rim = Rim,
        ShadeMin = ShadeMin,
        Fill = Fill,
        ShadowTint = ShadowTint,
        OutlineWidth = OutlineWidth,
        OutlineColor = OutlineColor,
    };
}

/// <summary>
/// 2.5D 卡通：半 Lambert 色阶 + 视空间描边。角色/地形共用 lighting，描边只给角色。
/// </summary>
public static class ToonMaterials
{
    public const string AlbedoParam = "albedo";
    public const string FlashParam = "flash";
    public const string KindMeta = "toon_kind";
    public const string KindActor = "actor";
    public const string KindOutline = "outline";
    public const string KindTerrain = "terrain";

    public static ToonStyle ActorDefaults { get; } = new();
    public static ToonStyle TerrainDefaults { get; } = new()
    {
        Bands = 3f,
        Rim = 0f,
        ShadeMin = 0.4f,
        Fill = 0.16f,
        ShadowTint = new Color(0.42f, 0.48f, 0.62f),
        OutlineWidth = 0f,
    };

    public static ShaderMaterial Create(Color albedo, Texture2D? albedoTex = null, bool outline = true)
    {
        var mat = new ShaderMaterial { Shader = MakeToonShader() };
        mat.SetMeta(KindMeta, KindActor);
        mat.SetShaderParameter(AlbedoParam, albedo);
        mat.SetShaderParameter(FlashParam, 0f);
        mat.SetShaderParameter("use_albedo_tex", albedoTex is not null);
        if (albedoTex is not null)
            mat.SetShaderParameter("albedo_tex", albedoTex);
        ApplyActor(mat, ActorDefaults);

        if (outline)
        {
            var hull = new ShaderMaterial { Shader = MakeOutlineShader() };
            hull.SetMeta(KindMeta, KindOutline);
            ApplyOutline(hull, ActorDefaults);
            mat.NextPass = hull;
        }
        return mat;
    }

    public static ShaderMaterial CreateTerrain(Texture2D atlas)
    {
        var mat = new ShaderMaterial { Shader = MakeTerrainShader() };
        mat.SetMeta(KindMeta, KindTerrain);
        mat.SetShaderParameter("uAtlas", atlas);
        ApplyTerrain(mat, TerrainDefaults);
        return mat;
    }

    public static void ApplyActor(ShaderMaterial mat, ToonStyle style)
    {
        mat.SetShaderParameter("shadow_tint", style.ShadowTint);
        mat.SetShaderParameter("bands", style.Bands);
        mat.SetShaderParameter("rim", style.Rim);
        mat.SetShaderParameter("shade_min", style.ShadeMin);
        mat.SetShaderParameter("fill", style.Fill);
        if (mat.NextPass is ShaderMaterial outline)
            ApplyOutline(outline, style);
    }

    public static void ApplyOutline(ShaderMaterial mat, ToonStyle style)
    {
        mat.SetShaderParameter("outline_color", style.OutlineColor);
        mat.SetShaderParameter("outline_width", style.OutlineWidth);
    }

    public static void ApplyTerrain(ShaderMaterial mat, ToonStyle style)
    {
        mat.SetShaderParameter("shadow_tint", style.ShadowTint);
        mat.SetShaderParameter("bands", style.Bands);
        mat.SetShaderParameter("shade_min", style.ShadeMin);
        mat.SetShaderParameter("fill", style.Fill);
    }

    public static void ApplyStyleToTree(Node root, ToonStyle style)
    {
        foreach (var mat in CollectActorMaterials(root))
            ApplyActor(mat, style);
    }

    public static void ApplyStyleToTerrain(Node root, ToonStyle style)
    {
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial sm)
                ApplyTerrain(sm, style);
        }
    }

    public static IEnumerable<ShaderMaterial> CollectActorMaterials(Node root)
    {
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            if (IsActor(mesh.MaterialOverride))
                yield return (ShaderMaterial)mesh.MaterialOverride;
            var count = mesh.Mesh?.GetSurfaceCount() ?? 0;
            for (var i = 0; i < count; i++)
            {
                if (IsActor(mesh.GetSurfaceOverrideMaterial(i)))
                    yield return (ShaderMaterial)mesh.GetSurfaceOverrideMaterial(i)!;
            }
        }
    }

    private static bool IsActor(Material? mat) =>
        mat is ShaderMaterial sm && sm.HasMeta(KindMeta) && sm.GetMeta(KindMeta).AsString() == KindActor;

    public static void ApplyToMeshTree(Node root)
    {
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            if (mesh.MaterialOverride is not null)
            {
                if (!IsActor(mesh.MaterialOverride))
                    mesh.MaterialOverride = FromExisting(mesh.MaterialOverride);
                continue;
            }
            var surfaceCount = mesh.Mesh?.GetSurfaceCount() ?? 0;
            if (surfaceCount <= 0)
            {
                mesh.MaterialOverride = FromExisting(mesh.GetActiveMaterial(0));
                continue;
            }
            for (var i = 0; i < surfaceCount; i++)
            {
                if (IsActor(mesh.GetSurfaceOverrideMaterial(i))) continue;
                mesh.SetSurfaceOverrideMaterial(i, FromExisting(mesh.GetActiveMaterial(i)));
            }
        }
    }

    public static bool HasActorToon(Node root)
    {
        foreach (var _ in CollectActorMaterials(root))
            return true;
        return false;
    }

    public static void EnableOn(Node root)
    {
        if (root is PigmanActor3D pig)
        {
            pig.ApplyToon = true;
            return;
        }
        if (HasActorToon(root)) return;
        ApplyToMeshTree(root);
    }

    public static void DisableOn(Node root)
    {
        if (root is PigmanActor3D pig)
        {
            pig.ApplyToon = false;
            return;
        }
        RestoreMeshTree(root);
    }

    public static void RestoreMeshTree(Node root)
    {
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            if (IsActor(mesh.MaterialOverride))
                mesh.MaterialOverride = ToLit((ShaderMaterial)mesh.MaterialOverride);
            var count = mesh.Mesh?.GetSurfaceCount() ?? 0;
            for (var i = 0; i < count; i++)
            {
                if (IsActor(mesh.GetSurfaceOverrideMaterial(i)))
                    mesh.SetSurfaceOverrideMaterial(i, ToLit((ShaderMaterial)mesh.GetSurfaceOverrideMaterial(i)!));
            }
        }
    }

    public static ShaderMaterial CreateGround()
    {
        var mat = new ShaderMaterial { Shader = MakeGroundShader() };
        mat.SetShaderParameter("albedo", new Color(0.45f, 0.62f, 0.38f));
        mat.SetShaderParameter("grid", new Color(0.38f, 0.52f, 0.32f));
        return mat;
    }

    public static void SetAlbedo(ShaderMaterial mat, Color albedo) =>
        mat.SetShaderParameter(AlbedoParam, albedo);

    public static void SetFlash(ShaderMaterial mat, bool on) =>
        mat.SetShaderParameter(FlashParam, on ? 1f : 0f);

    private static StandardMaterial3D ToLit(ShaderMaterial sm)
    {
        var albedo = sm.GetShaderParameter(AlbedoParam).AsColor();
        var useTex = sm.GetShaderParameter("use_albedo_tex").AsBool();
        Texture2D? tex = null;
        if (useTex)
            tex = sm.GetShaderParameter("albedo_tex").AsGodotObject() as Texture2D;
        return new StandardMaterial3D
        {
            AlbedoColor = albedo,
            AlbedoTexture = tex,
            Roughness = 0.85f,
        };
    }

    private static ShaderMaterial FromExisting(Material? current)
    {
        Texture2D? tex = null;
        var albedo = Colors.White;
        if (current is StandardMaterial3D std)
        {
            tex = std.AlbedoTexture;
            albedo = std.AlbedoColor;
        }
        else if (current is BaseMaterial3D baseMat)
        {
            tex = baseMat.AlbedoTexture;
            albedo = baseMat.AlbedoColor;
        }
        return Create(albedo, tex);
    }

    private static Shader MakeToonShader() => new()
    {
        Code = """
shader_type spatial;
render_mode cull_back, specular_disabled, shadows_disabled, ambient_light_disabled;

uniform vec4 albedo : source_color = vec4(0.4, 0.75, 0.45, 1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white;
uniform bool use_albedo_tex = false;
uniform float flash : hint_range(0.0, 1.0) = 0.0;
uniform vec4 shadow_tint : source_color = vec4(0.38, 0.42, 0.62, 1.0);
uniform float bands = 3.0;
uniform float rim : hint_range(0.0, 1.0) = 0.22;
uniform float shade_min : hint_range(0.0, 1.0) = 0.22;
uniform float fill : hint_range(0.0, 0.8) = 0.18;

void fragment() {
	vec3 baseCol = use_albedo_tex ? texture(albedo_tex, UV).rgb : albedo.rgb;
	ALBEDO = mix(baseCol, vec3(1.0), flash);
	EMISSION = ALBEDO * fill * vec3(0.88, 1.0, 1.22);
	ROUGHNESS = 1.0;
}

void light() {
	float wrap = clamp(dot(NORMAL, LIGHT) * 0.5 + 0.5, 0.0, 1.0);
	float stepped = floor(wrap * bands + 1e-4) / max(bands - 1.0, 1.0);
	stepped = mix(shade_min, 1.0, clamp(stepped, 0.0, 1.0));
	vec3 shade = mix(ALBEDO * shadow_tint.rgb, ALBEDO, stepped);
	DIFFUSE_LIGHT += shade * LIGHT_COLOR * ATTENUATION;
	float rimAmt = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 2.5) * rim * stepped;
	DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * rimAmt * 0.35;
}
""",
    };

    private static Shader MakeOutlineShader() => new()
    {
        Code = """
shader_type spatial;
render_mode unshaded, cull_front, shadows_disabled, depth_draw_opaque;

uniform vec4 outline_color : source_color = vec4(0.07, 0.05, 0.09, 1.0);
uniform float outline_width = 0.022;

void vertex() {
	vec3 n = normalize((MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	view.xyz += n * outline_width;
	POSITION = PROJECTION_MATRIX * view;
}

void fragment() {
	ALBEDO = outline_color.rgb;
}
""",
    };

    private static Shader MakeTerrainShader() => new()
    {
        Code = """
shader_type spatial;
render_mode cull_back, specular_disabled, shadows_disabled, ambient_light_disabled;

uniform sampler2D uAtlas : source_color, filter_linear_mipmap;
uniform vec4 shadow_tint : source_color = vec4(0.42, 0.48, 0.62, 1.0);
uniform float bands = 3.0;
uniform float shade_min : hint_range(0.0, 1.0) = 0.4;
uniform float fill : hint_range(0.0, 0.8) = 0.16;

void fragment() {
	ALBEDO = texture(uAtlas, UV).rgb * COLOR.rgb;
	ROUGHNESS = 1.0;
	EMISSION = ALBEDO * fill * vec3(0.88, 1.0, 1.15);
}

void light() {
	float wrap = clamp(dot(NORMAL, LIGHT) * 0.5 + 0.5, 0.0, 1.0);
	float stepped = floor(wrap * bands + 1e-4) / max(bands - 1.0, 1.0);
	stepped = mix(shade_min, 1.0, clamp(stepped, 0.0, 1.0));
	DIFFUSE_LIGHT += mix(ALBEDO * shadow_tint.rgb, ALBEDO, stepped) * LIGHT_COLOR * ATTENUATION;
}
""",
    };

    private static Shader MakeGroundShader() => new()
    {
        Code = """
shader_type spatial;
render_mode unshaded, shadows_disabled;

uniform vec4 albedo : source_color = vec4(0.45, 0.62, 0.38, 1.0);
uniform vec4 grid : source_color = vec4(0.38, 0.52, 0.32, 1.0);
varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 cell = abs(fract(world_pos.xz) - 0.5);
	float line = 1.0 - step(0.03, min(cell.x, cell.y));
	ALBEDO = mix(albedo.rgb, grid.rgb, line * 0.55);
}
""",
    };
}
