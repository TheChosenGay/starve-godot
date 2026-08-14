using Godot;

namespace GodotClient.Game;

/// <summary>LUT 调色 pass：按昼夜权重混合白天/黄昏/夜晚三套表（挂在光照 pass 之后）。</summary>
public partial class LutPass : Control
{
    private readonly ShaderMaterial _mat = MakeMaterial();

    public LutPass()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Material = _mat;
    }

    public void SetAtlas(Texture2D atlas) => _mat.SetShaderParameter("uLut", atlas);

    public void SetWeights(float day, float dusk, float night) =>
        _mat.SetShaderParameter("uWeights", new Vector3(day, dusk, night));

    private static ShaderMaterial MakeMaterial()
    {
        var shader = new Shader
        {
            Code = @"shader_type canvas_item;
uniform sampler2D uScreenTexture : hint_screen_texture;
uniform sampler2D uLut;
uniform vec3 uWeights;
const float N = 32.0;
const float STYLES = 6.0;
vec3 lutSample(float strip, vec3 c) {
    float b = c.b * (N - 1.0);
    float b0 = floor(b);
    float b1 = min(b0 + 1.0, N - 1.0);
    float fb = b - b0;
    float yBase = (strip * N + 0.5) / (STYLES * N);
    vec2 uv0 = vec2((c.r * (N - 1.0) + b0 * N + 0.5) / (N * N), yBase + c.g * (N - 1.0) / (STYLES * N));
    vec2 uv1 = vec2((c.r * (N - 1.0) + b1 * N + 0.5) / (N * N), yBase + c.g * (N - 1.0) / (STYLES * N));
    return mix(texture(uLut, uv0).rgb, texture(uLut, uv1).rgb, fb);
}
void fragment() {
    vec4 screen = texture(uScreenTexture, SCREEN_UV);
    vec3 graded = lutSample(0.0, screen.rgb) * uWeights.x
                + lutSample(1.0, screen.rgb) * uWeights.y
                + lutSample(2.0, screen.rgb) * uWeights.z;
    COLOR = vec4(graded, screen.a);
}",
        };
        var mat = new ShaderMaterial();
        mat.Shader = shader;
        return mat;
    }
}
