using Godot;

namespace GodotClient.Game;

/// <summary>
/// 2.5D 卡通材质：量化漫反射 + 描边。角色/道具共用，之后 glTF 只要换成同一套 ShaderMaterial。
/// </summary>
public static class ToonMaterials
{
    public const string AlbedoParam = "albedo";
    public const string FlashParam = "flash";

    public static ShaderMaterial Create(Color albedo, Texture2D? albedoTex = null)
    {
        var outline = new ShaderMaterial { Shader = MakeOutlineShader() };
        outline.SetShaderParameter("outline_color", new Color(0.08f, 0.06f, 0.1f));
        outline.SetShaderParameter("outline_width", 0.028f);

        var mat = new ShaderMaterial { Shader = MakeToonShader(), NextPass = outline };
        mat.SetShaderParameter(AlbedoParam, albedo);
        mat.SetShaderParameter(FlashParam, 0f);
        mat.SetShaderParameter("shadow_tint", new Color(0.42f, 0.4f, 0.62f));
        mat.SetShaderParameter("bands", 3f);
        mat.SetShaderParameter("rim", 0.35f);
        mat.SetShaderParameter("use_albedo_tex", albedoTex is not null);
        if (albedoTex is not null)
            mat.SetShaderParameter("albedo_tex", albedoTex);
        return mat;
    }

    public static void ApplyToMeshTree(Node root)
    {
        foreach (var child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh) continue;
            Texture2D? tex = null;
            Color albedo = Colors.White;
            var current = mesh.GetActiveMaterial(0);
            if (current is StandardMaterial3D std)
            {
                tex = std.AlbedoTexture;
                albedo = std.AlbedoColor;
            }
            mesh.MaterialOverride = Create(albedo, tex);
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

    private static Shader MakeToonShader() => new()
    {
        Code = """
shader_type spatial;
render_mode cull_back, specular_disabled, shadows_disabled;

uniform vec4 albedo : source_color = vec4(0.4, 0.75, 0.45, 1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white;
uniform bool use_albedo_tex = false;
uniform float flash : hint_range(0.0, 1.0) = 0.0;
uniform vec4 shadow_tint : source_color = vec4(0.42, 0.4, 0.62, 1.0);
uniform float bands = 3.0;
uniform float rim : hint_range(0.0, 1.0) = 0.35;

void fragment() {
	vec3 baseCol = use_albedo_tex ? texture(albedo_tex, UV).rgb : albedo.rgb;
	vec3 base = mix(baseCol, vec3(1.0), flash);
	ALBEDO = base;
	ROUGHNESS = 1.0;
}

void light() {
	float ndotl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0);
	float stepped = floor(ndotl * bands + 1e-4) / max(bands - 1.0, 1.0);
	stepped = mix(0.28, 1.0, clamp(stepped, 0.0, 1.0));
	vec3 shade = mix(ALBEDO * shadow_tint.rgb, ALBEDO, stepped);
	DIFFUSE_LIGHT += shade * LIGHT_COLOR * ATTENUATION;
	float rimAmt = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 3.0) * rim;
	DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * rimAmt;
}
""",
    };

    private static Shader MakeOutlineShader() => new()
    {
        Code = """
shader_type spatial;
render_mode unshaded, cull_front, shadows_disabled, depth_draw_opaque;

uniform vec4 outline_color : source_color = vec4(0.08, 0.06, 0.1, 1.0);
uniform float outline_width = 0.028;

void vertex() {
	VERTEX += NORMAL * outline_width;
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
