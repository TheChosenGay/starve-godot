using System.Collections.Generic;
using Godot;
using Starve.Core;

namespace GodotClient.Game;

public enum ToonShaderKind
{
    Bands,
    Cel,
}

/// <summary>运行时 toon 调参；角色与地形共用这一组滑条语义。</summary>
public sealed class ToonStyle
{
    public ToonShaderKind Kind = ToonShaderKind.Bands;
    public float Bands = 3f;
    public float Rim = 0.22f;
    public float ShadeMin = 0.22f;
    public float Fill = 0.18f;
    public Color ShadowTint = new(0.38f, 0.42f, 0.62f);
    public float OutlineWidth = 0.022f;
    public Color OutlineColor = new(0.07f, 0.05f, 0.09f);
    public float DiffuseThreshold = 0.5f;
    public float ShadowStrength = 0.5f;
    public float SpecularThreshold = 0.99f;
    public float SpecularStrength = 2f;
    public Color SpecularColor = Colors.White;
    public float RimWidth = 2f;
    public float RimPower = 4f;
    public float RimStrength = 1f;
    public Color RimColor = Colors.White;
    public bool RimLitOnly;

    public ToonStyle Clone() => new()
    {
        Kind = Kind,
        Bands = Bands,
        Rim = Rim,
        ShadeMin = ShadeMin,
        Fill = Fill,
        ShadowTint = ShadowTint,
        OutlineWidth = OutlineWidth,
        OutlineColor = OutlineColor,
        DiffuseThreshold = DiffuseThreshold,
        ShadowStrength = ShadowStrength,
        SpecularThreshold = SpecularThreshold,
        SpecularStrength = SpecularStrength,
        SpecularColor = SpecularColor,
        RimWidth = RimWidth,
        RimPower = RimPower,
        RimStrength = RimStrength,
        RimColor = RimColor,
        RimLitOnly = RimLitOnly,
    };

    public static ToonStyle CelDefaults() => new()
    {
        Kind = ToonShaderKind.Cel,
        OutlineWidth = 0.018f,
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
    public const string VariantMeta = "toon_variant";
    public const string VariantBands = "bands";
    public const string VariantCel = "cel";
    public const string DayLightParam = "day_light";
    public const string AlbedoTexMeta = "albedo_tex_keep";

    /// <summary>新建角色 Toon 时用哪套 shader。切换面板选项会改这个。</summary>
    public static ToonShaderKind CreateKind { get; set; } = ToonShaderKind.Bands;

    public static ToonStyle ActorDefaults { get; } = new();
    public static ToonStyle TerrainDefaults { get; } = new()
    {
        Bands = 5f,
        Rim = 0f,
        ShadeMin = 0.5f,
        Fill = 0.2f,
        ShadowTint = new Color(0.42f, 0.48f, 0.62f),
        OutlineWidth = 0f,
    };

    public static ShaderMaterial Create(Color albedo, Texture2D? albedoTex = null, bool outline = true)
    {
        var kind = CreateKind;
        var mat = new ShaderMaterial { Shader = ShaderOf(kind) };
        mat.SetMeta(KindMeta, KindActor);
        mat.SetMeta(VariantMeta, VariantName(kind));
        mat.SetShaderParameter(AlbedoParam, albedo);
        mat.SetShaderParameter(FlashParam, 0f);
        KeepAlbedo(mat, albedoTex);
        var style = ActorDefaults.Clone();
        style.Kind = kind;
        ApplyActor(mat, style);
        SetDayLight(mat, 1f);

        if (outline)
        {
            var hull = new ShaderMaterial { Shader = MakeOutlineShader() };
            hull.SetMeta(KindMeta, KindOutline);
            ApplyOutline(hull, ActorDefaults);
            mat.NextPass = hull;
        }
        return mat;
    }

    public static ShaderMaterial CreateTerrain()
    {
        var mat = new ShaderMaterial { Shader = ShaderLibrary.Load(ShaderLibrary.TerrainHeightBlend) };
        mat.SetMeta(KindMeta, KindTerrain);
        mat.SetShaderParameter("uGrassAlbedo", TerrainHaven.Load(TerrainHaven.GrassAlbedo, new Color(0.42f, 0.52f, 0.28f)));
        mat.SetShaderParameter("uGrassHeight", TerrainHaven.Load(TerrainHaven.GrassHeight, new Color(0.55f, 0.55f, 0.55f)));
        mat.SetShaderParameter("uDirtAlbedo", TerrainHaven.Load(TerrainHaven.DirtAlbedo, new Color(0.55f, 0.42f, 0.28f)));
        mat.SetShaderParameter("uDirtHeight", TerrainHaven.Load(TerrainHaven.DirtHeight, new Color(0.45f, 0.45f, 0.45f)));
        mat.SetShaderParameter("uRockAlbedo", TerrainHaven.Load(TerrainHaven.RockAlbedo, new Color(0.62f, 0.58f, 0.52f)));
        mat.SetShaderParameter("uRockHeight", TerrainHaven.Load(TerrainHaven.RockHeight, new Color(0.5f, 0.5f, 0.5f)));
        mat.SetShaderParameter("uWorldTiling", MapMeshBuilder.WorldTiling);
        mat.SetShaderParameter("uHeightSharpness", TerrainHaven.DefaultHeightSharpness);
        mat.SetShaderParameter("uSlopeRock", TerrainHaven.DefaultSlopeRock);
        ApplyTerrain(mat, TerrainDefaults);
        SetDayLight(mat, 1f);
        return mat;
    }

    public static Texture2D? ExtractAlbedoTex(Material? mat)
    {
        switch (mat)
        {
            case StandardMaterial3D std when std.AlbedoTexture is not null:
                return std.AlbedoTexture;
            case BaseMaterial3D baseMat when baseMat.AlbedoTexture is not null:
                return baseMat.AlbedoTexture;
            case ShaderMaterial sm:
                foreach (var key in new[] { "albedo_tex", "texture_albedo", "albedo_texture" })
                {
                    if (sm.GetShaderParameter(key).AsGodotObject() is Texture2D tex)
                        return tex;
                }
                if (sm.HasMeta(AlbedoTexMeta) && sm.GetMeta(AlbedoTexMeta).AsGodotObject() is Texture2D kept)
                    return kept;
                break;
        }
        return null;
    }

    public static Color ExtractAlbedoColor(Material? mat) =>
        mat switch
        {
            BaseMaterial3D b => b.AlbedoColor,
            ShaderMaterial sm when sm.GetShaderParameter(AlbedoParam).VariantType == Variant.Type.Color
                => sm.GetShaderParameter(AlbedoParam).AsColor(),
            _ => Colors.White,
        };

    public static void KeepAlbedo(ShaderMaterial mat, Texture2D? tex)
    {
        if (tex is null) return;
        mat.SetMeta(AlbedoTexMeta, tex);
        mat.SetShaderParameter("use_albedo_tex", true);
        mat.SetShaderParameter("albedo_tex", tex);
    }

    public static void ApplyActor(ShaderMaterial mat, ToonStyle style)
    {
        var tex = ExtractAlbedoTex(mat);
        EnsureKind(mat, style.Kind);
        if (style.Kind == ToonShaderKind.Cel)
        {
            mat.SetShaderParameter("diffuse_threshold", style.DiffuseThreshold);
            mat.SetShaderParameter("shadow_strength", style.ShadowStrength);
            mat.SetShaderParameter("specular_threshold", style.SpecularThreshold);
            mat.SetShaderParameter("specular_strength", style.SpecularStrength);
            mat.SetShaderParameter("specular_color", style.SpecularColor);
            mat.SetShaderParameter("rim_width", style.RimWidth);
            mat.SetShaderParameter("rim_power", style.RimPower);
            mat.SetShaderParameter("rim_strength", style.RimStrength);
            mat.SetShaderParameter("rim_color", style.RimColor);
            mat.SetShaderParameter("lit_part_fresnel_only", style.RimLitOnly);
        }
        else
        {
            mat.SetShaderParameter("shadow_tint", style.ShadowTint);
            mat.SetShaderParameter("bands", style.Bands);
            mat.SetShaderParameter("rim", style.Rim);
            mat.SetShaderParameter("shade_min", style.ShadeMin);
            mat.SetShaderParameter("fill", style.Fill);
        }
        if (mat.NextPass is ShaderMaterial outline)
            ApplyOutline(outline, style);
        KeepAlbedo(mat, tex);
    }

    public static ToonShaderKind ReadKind(ShaderMaterial mat)
    {
        if (mat.HasMeta(VariantMeta) && mat.GetMeta(VariantMeta).AsString() == VariantCel)
            return ToonShaderKind.Cel;
        return ToonShaderKind.Bands;
    }

    public static void EnsureKind(ShaderMaterial mat, ToonShaderKind kind)
    {
        if (ReadKind(mat) == kind && mat.Shader == ShaderOf(kind)) return;
        mat.Shader = ShaderOf(kind);
        mat.SetMeta(VariantMeta, VariantName(kind));
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

    public static bool IsLive(Node? root) =>
        root is not null && GodotObject.IsInstanceValid(root) && !root.IsQueuedForDeletion();

    public static void ApplyStyleToTree(Node root, ToonStyle style)
    {
        if (!IsLive(root)) return;
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
        if (!IsLive(root)) yield break;
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
        if (!IsLive(root)) return;
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            if (mesh.MaterialOverride is not null)
            {
                if (!IsActor(mesh.MaterialOverride))
                    mesh.MaterialOverride = FromExisting(SourceMaterial(mesh, 0) ?? mesh.MaterialOverride);
                continue;
            }
            var surfaceCount = mesh.Mesh?.GetSurfaceCount() ?? 0;
            if (surfaceCount <= 0)
            {
                mesh.MaterialOverride = FromExisting(SourceMaterial(mesh, 0));
                continue;
            }
            for (var i = 0; i < surfaceCount; i++)
            {
                if (IsActor(mesh.GetSurfaceOverrideMaterial(i))) continue;
                mesh.SetSurfaceOverrideMaterial(i, FromExisting(SourceMaterial(mesh, i)));
            }
        }
    }

    public static Material? SourceMaterial(MeshInstance3D mesh, int surface)
    {
        if (mesh.Mesh is { } meshRes && surface < meshRes.GetSurfaceCount()
            && meshRes.SurfaceGetMaterial(surface) is { } fromMesh)
            return fromMesh;
        return mesh.GetActiveMaterial(surface);
    }

    public static bool HasActorToon(Node root)
    {
        if (!IsLive(root) || root is TreeActor3D)
            return false;
        foreach (var _ in CollectActorMaterials(root))
            return true;
        return false;
    }

    public static void EnableOn(Node? root)
    {
        if (!IsLive(root) || root is TreeActor3D)
            return;
        if (root is PigmanActor3D pig)
        {
            pig.ApplyToon = true;
            return;
        }
        if (root is AlchemyEngine3D engine)
        {
            engine.SetApplyToon(true);
            return;
        }
        if (HasActorToon(root)) return;
        ApplyToMeshTree(root);
    }

    public static void DisableOn(Node? root)
    {
        if (!IsLive(root) || root is TreeActor3D)
            return;
        if (root is PigmanActor3D pig)
        {
            pig.ApplyToon = false;
            return;
        }
        if (root is AlchemyEngine3D engine)
        {
            engine.SetApplyToon(false);
            return;
        }
        RestoreMeshTree(root);
    }

    public static void RestoreMeshTree(Node root)
    {
        if (!IsLive(root)) return;
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

    public static void SetDayLight(ShaderMaterial mat, float sunT) =>
        mat.SetShaderParameter(DayLightParam, Mathf.Clamp(sunT, 0f, 1f));

    public static void ApplyCloudShadow(
        ShaderMaterial mat,
        DayCycleLook look,
        LightTune tune,
        Vector3 towardSun,
        Vector3 focus,
        float cloudHeight,
        float rain = 0f)
    {
        if (towardSun.LengthSquared() < 1e-6f)
            towardSun = new Vector3(0.32f, 0.72f, -0.58f);
        mat.SetShaderParameter("coverage", Mathf.Clamp(tune.CloudCoverage + rain * 0.26f, 0.04f, 0.95f));
        mat.SetShaderParameter("thickness", tune.CloudThickness);
        mat.SetShaderParameter("wind", tune.CloudWind);
        mat.SetShaderParameter("sun_direction", towardSun.Normalized());
        mat.SetShaderParameter("box_center", new Vector3(focus.X, cloudHeight, focus.Z));
        mat.SetShaderParameter("box_size", CloudLayer3D.DefaultBoxSize);
        mat.SetShaderParameter("night", look.NightWeight);
        mat.SetShaderParameter("cloud_shadow_strength", 0.78f);
    }

    public static void ApplyDayLightToTree(Node root, float sunT)
    {
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            if (mesh.MaterialOverride is ShaderMaterial over)
                TrySetDayLight(over, sunT);
            var count = mesh.Mesh?.GetSurfaceCount() ?? 0;
            for (var i = 0; i < count; i++)
            {
                if (mesh.GetSurfaceOverrideMaterial(i) is ShaderMaterial surface)
                    TrySetDayLight(surface, sunT);
            }
        }
    }

    private static void TrySetDayLight(ShaderMaterial mat, float sunT)
    {
        if (!mat.HasMeta(KindMeta)) return;
        var kind = mat.GetMeta(KindMeta).AsString();
        if (kind is KindActor or KindTerrain)
            SetDayLight(mat, sunT);
    }

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

    private static ShaderMaterial FromExisting(Material? current) =>
        Create(ExtractAlbedoColor(current), ExtractAlbedoTex(current));

    private static Shader? _bandsShader;
    private static Shader? _celShader;

    private static Shader ShaderOf(ToonShaderKind kind) =>
        kind == ToonShaderKind.Cel ? CelShader : BandsShader;

    private static string VariantName(ToonShaderKind kind) =>
        kind == ToonShaderKind.Cel ? VariantCel : VariantBands;

    private static Shader BandsShader => _bandsShader ??= MakeToonShader();
    private static Shader CelShader => _celShader ??= MakeCelShader();

    // 内联进 Toon：运行时 new Shader 的 Code 解析不了 #include。
    // Godot 要求所有 uniform 写在函数前面，所以 uniforms / 函数拆开拼。
    private const string BounceUniforms = """
uniform float bounce_t = 0.0;
uniform float foot_y = -0.95;
""";

    private const string BounceDeform = """
vec3 hop_pose(float u, float peak, float from_crouch, float deform) {
	u = clamp(u, 0.0, 1.0);
	float release = smoothstep(0.0, 0.20, u);
	float lift = 4.0 * u * (1.0 - u);
	float land = smoothstep(0.76, 1.0, u);
	float takeoff = 1.0 - smoothstep(0.0, 0.42, u);
	float fall = smoothstep(0.48, 0.80, u) * (1.0 - land);
	float start_sy = from_crouch > 0.5 ? mix(0.62, 1.0, release) : 1.0;
	float sy = clamp(
		start_sy + peak * 1.15 * lift + 0.10 * deform * takeoff + 0.10 * deform * fall - 0.32 * deform * land,
		0.55,
		1.70
	);
	return vec3(0.0, sy, inversesqrt(sy));
}

vec3 bounce_pose(float t) {
	const float t_crouch = 0.20;
	const float t_hop1 = 0.42;
	const float t_hop2 = 0.28;
	const float t_hop3 = 0.22;
	const float t_settle = 0.12;
	if (t <= 0.001) {
		return vec3(0.0, 1.0, 1.0);
	}
	if (t < t_crouch) {
		float u = t / t_crouch;
		float e = smoothstep(0.0, 0.78, u);
		e *= e;
		float sy = mix(1.0, 0.62, e);
		return vec3(0.0, sy, inversesqrt(sy));
	}
	t -= t_crouch;
	if (t < t_hop1) {
		return hop_pose(t / t_hop1, 0.38, 1.0, 1.0);
	}
	t -= t_hop1;
	if (t < t_hop2) {
		return hop_pose(t / t_hop2, 0.16, 0.0, 0.55);
	}
	t -= t_hop2;
	if (t < t_hop3) {
		return hop_pose(t / t_hop3, 0.06, 0.0, 0.28);
	}
	t -= t_hop3;
	float u = clamp(t / t_settle, 0.0, 1.0);
	float e = u * u * (3.0 - 2.0 * u);
	float sy = mix(0.84, 1.0, e);
	return vec3(0.0, sy, inversesqrt(sy));
}
""";

    // VERTEX/NORMAL 只能写在 vertex() 里，不能放进辅助函数。
    private const string BounceVertex = """
	vec3 pose = bounce_pose(bounce_t);
	float h = VERTEX.y - foot_y;
	VERTEX.y = foot_y + h * pose.y;
	VERTEX.x *= pose.z;
	VERTEX.z *= pose.z;
	NORMAL.y /= pose.y;
	NORMAL.xz /= pose.z;
	NORMAL = normalize(NORMAL);
""";

    private static Shader MakeToonShader() => new()
    {
        Code = """
shader_type spatial;
render_mode cull_back, specular_disabled, shadows_disabled, ambient_light_disabled;
""" + BounceUniforms + """
uniform vec4 albedo : source_color = vec4(0.4, 0.75, 0.45, 1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white;
uniform bool use_albedo_tex = false;
uniform float flash : hint_range(0.0, 1.0) = 0.0;
uniform vec4 shadow_tint : source_color = vec4(0.38, 0.42, 0.62, 1.0);
uniform float bands = 3.0;
uniform float rim : hint_range(0.0, 1.0) = 0.22;
uniform float shade_min : hint_range(0.0, 1.0) = 0.22;
uniform float fill : hint_range(0.0, 0.8) = 0.18;
uniform float day_light : hint_range(0.0, 1.0) = 1.0;

""" + BounceDeform + """
void vertex() {
""" + BounceVertex + """
}

void fragment() {
	vec3 baseCol = use_albedo_tex ? texture(albedo_tex, UV).rgb : albedo.rgb;
	ALBEDO = mix(baseCol, vec3(1.0), flash);
	float fillAmt = fill * mix(0.12, 1.0, day_light);
	EMISSION = ALBEDO * fillAmt * vec3(0.88, 1.0, 1.22);
	ROUGHNESS = 1.0;
}

void light() {
	float wrap = clamp(dot(NORMAL, LIGHT) * 0.5 + 0.5, 0.0, 1.0);
	float stepped = floor(wrap * bands + 1e-4) / max(bands - 1.0, 1.0);
	float shadeFloor = shade_min * mix(0.38, 1.0, day_light);
	stepped = mix(shadeFloor, 1.0, clamp(stepped, 0.0, 1.0));
	vec3 nightTint = mix(vec3(0.42, 0.52, 0.82), vec3(1.0), day_light);
	vec3 shade = mix(ALBEDO * shadow_tint.rgb * nightTint, ALBEDO, stepped);
	DIFFUSE_LIGHT += shade * LIGHT_COLOR * ATTENUATION;
	float rimAmt = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 2.5) * rim * stepped * mix(0.4, 1.0, day_light);
	DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * rimAmt * 0.35;
}
""",
    };

    private static Shader MakeCelShader() => new()
    {
        Code = """
shader_type spatial;
render_mode cull_back, ambient_light_disabled;
""" + BounceUniforms + """
uniform vec4 albedo : source_color = vec4(0.4, 0.75, 0.45, 1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white;
uniform bool use_albedo_tex = false;
uniform float flash : hint_range(0.0, 1.0) = 0.0;
uniform float day_light : hint_range(0.0, 1.0) = 1.0;
uniform float diffuse_threshold : hint_range(0.0, 1.0) = 0.5;
uniform float shadow_strength : hint_range(0.0, 1.0) = 0.5;
uniform float specular_threshold : hint_range(0.9, 1.0) = 0.99;
uniform float specular_strength : hint_range(0.0, 4.0) = 2.0;
uniform vec3 specular_color : source_color = vec3(1.0, 1.0, 1.0);
uniform float rim_width = 2.0;
uniform float rim_power = 4.0;
uniform float rim_strength : hint_range(0.0, 2.0) = 1.0;
uniform vec3 rim_color : source_color = vec3(1.0, 1.0, 1.0);
uniform bool lit_part_fresnel_only = false;

""" + BounceDeform + """
void vertex() {
""" + BounceVertex + """
}

void fragment() {
	vec3 baseCol = use_albedo_tex ? texture(albedo_tex, UV).rgb : albedo.rgb;
	ALBEDO = mix(baseCol, vec3(1.0), flash);
	ROUGHNESS = 1.0;
}

void light() {
	vec3 albedo_color = ALBEDO;
	float ndotl = dot(NORMAL, LIGHT);
	float lit_intensity = ndotl * ATTENUATION;
	float diffuse_step = lit_intensity > diffuse_threshold ? 1.0 : 0.0;
	vec3 diffuse_col = diffuse_step == 0.0
		? albedo_color * shadow_strength
		: albedo_color;
	DIFFUSE_LIGHT += diffuse_col * LIGHT_COLOR;

	vec3 H = normalize(LIGHT + VIEW);
	float spec = dot(NORMAL, H);
	float spec_step = spec > specular_threshold ? 1.0 : 0.0;
	SPECULAR_LIGHT += specular_color * specular_strength * spec_step * ATTENUATION * LIGHT_COLOR;

	float rim_dot = 1.0 - dot(VIEW, NORMAL);
	float rim = smoothstep(0.0, max(rim_width, 1e-4), rim_dot) * rim_width;
	rim = pow(max(rim, 0.0), rim_power) * rim_strength;
	rim *= ATTENUATION;
	if (lit_part_fresnel_only) {
		float rim_mask = smoothstep(0.0, 1.0, clamp(lit_intensity, 0.0, 1.0));
		rim *= rim_mask;
	}
	DIFFUSE_LIGHT += rim * rim_color * LIGHT_COLOR;
}
""",
    };

    private static Shader MakeOutlineShader() => new()
    {
        Code = """
shader_type spatial;
render_mode unshaded, cull_front, shadows_disabled, depth_draw_opaque;
""" + BounceUniforms + """
uniform vec4 outline_color : source_color = vec4(0.07, 0.05, 0.09, 1.0);
uniform float outline_width = 0.022;

""" + BounceDeform + """
void vertex() {
""" + BounceVertex + """
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
