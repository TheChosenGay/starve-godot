using Godot;

namespace GodotClient.Game;

/// <summary>
/// 全屏法线光照 pass：canvas_item screen shader 逐像素还原世界坐标 →
/// 采样法线图 → 环境光 + 太阳 + 最多 8 点光 + 深度雾，乘到屏幕颜色上。
/// 挂默认画布层（世界之上、UI CanvasLayer 之下），不影响 HUD。
/// </summary>
public partial class LightingPass : Control
{
    private readonly ShaderMaterial _mat = MakeMaterial();

    public LightingPass()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Material = _mat;
    }

    public void SetNormalMap(Texture2D normalMap) =>
        _mat.SetShaderParameter("uNormal", normalMap);

    public void SetMapSize(Vector2 size) =>
        _mat.SetShaderParameter("uMapSize", size);

    public void SetLights(
        Vector2 screenSize,
        float zoom,
        float ambient,
        Vector2 sunDir,
        Color sunColor,
        Color fogColor,
        float fogDensity,
        Vector2[] lightPos,
        Color[] lightColor,
        float[] lightRadius)
    {
        _mat.SetShaderParameter("uScreenSize", screenSize);
        _mat.SetShaderParameter("uZoom", zoom);
        _mat.SetShaderParameter("uAmbient", ambient);
        _mat.SetShaderParameter("uSunDir", sunDir);
        _mat.SetShaderParameter("uSunColor", sunColor);
        _mat.SetShaderParameter("uFogColor", fogColor);
        _mat.SetShaderParameter("uFogDensity", fogDensity);
        _mat.SetShaderParameter("uLightCount", lightPos.Length);
        _mat.SetShaderParameter("uLightPos", lightPos);
        _mat.SetShaderParameter("uLightColor", lightColor);
        _mat.SetShaderParameter("uLightRadius", lightRadius);
    }

    private static ShaderMaterial MakeMaterial()
    {
        var shader = new Shader
        {
            Code = @"shader_type canvas_item;
uniform sampler2D uScreenTexture : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D uNormal : filter_nearest;
uniform vec2 uScreenSize;
uniform vec2 uCam;
uniform float uZoom;
uniform vec2 uMapSize;
uniform float uAmbient;
uniform vec2 uSunDir;
uniform vec3 uSunColor;
uniform vec3 uFogColor;
uniform float uFogDensity;
uniform int uLightCount;
uniform vec2 uLightPos[8];
uniform vec3 uLightColor[8];
uniform float uLightRadius[8];

void fragment() {
    vec2 uv = SCREEN_UV;
    vec2 sp = uv * uScreenSize;
    float a = (sp.x - uScreenSize.x * 0.5) / (20.0 * uZoom);
    float b = (sp.y - uScreenSize.y * 0.5) / (10.0 * uZoom);
    vec2 wpos = uCam + vec2((a + b) * 0.5, (b - a) * 0.5);
    vec3 n = texture(uNormal, clamp(wpos / uMapSize, vec2(0.0), vec2(1.0))).rgb * 2.0 - 1.0;
    vec3 light = vec3(uAmbient);
    vec3 sunDir3 = normalize(vec3(uSunDir, 1.0));
    light += uSunColor * max(dot(n, sunDir3), 0.0);
    for (int i = 0; i < 8; i++) {
        if (i >= uLightCount) break;
        float d = distance(wpos, uLightPos[i]);
        float att = 1.0 - smoothstep(0.0, uLightRadius[i], d);
        if (att <= 0.002) continue;
        vec2 ld = normalize(uLightPos[i] - wpos);
        light += uLightColor[i] * att * (max(dot(n.xy, ld) * 0.5 + 0.5, 0.0));
    }
    vec4 screen = texture(uScreenTexture, uv);
    vec3 lit = screen.rgb * light;
    float fog = 1.0 - exp(-distance(wpos, uCam) * uFogDensity);
    COLOR = vec4(mix(lit, uFogColor, clamp(fog, 0.0, 0.85)), screen.a);
}",
        };
        var mat = new ShaderMaterial();
        mat.Shader = shader;
        mat.SetShaderParameter("uMapSize", new Vector2(128, 128));
        return mat;
    }
}
