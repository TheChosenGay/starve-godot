using Godot;

namespace GodotClient.Game;

/// <summary>
/// 程序化火焰外观。强度不是噪音原值：先用水滴外形得到
/// <c>fire_intensity</c>，再用噪音雕边并乘出 <c>fire_intensity1</c>。
/// </summary>
public sealed class FireStyle
{
    public string Id = "campfire";
    public string Label = "篝火";
    public float DetailStrength = 3f;
    public float ScrollSpeed = 1.2f;
    public float FireHeight = 1f;
    public float FireShape = 1.5f;
    public float FireThickness = 0.55f;
    public float FireSharpness = 1f;
    public float Intensity = 1f;
    public int NoiseOctaves = 6;
    public float NoiseLacunarity = 3f;
    public float NoiseGain = 0.5f;
    public float NoiseAmplitude = 1f;
    public float NoiseFrequency = 1.5f;
    public Color ColorCore = new(1.55f, 1.32f, 0.62f);
    public Color ColorMid = new(1.35f, 0.42f, 0.07f);
    public Color ColorEdge = new(0.28f, 0.04f, 0.01f);
    public float ColorMix = 0f;
    public float MeshWidth = 0.95f;
    public float MeshHeight = 1.45f;
    public float LightEnergy = 2.4f;
    public float LightRange = 5.5f;
    public Color LightColor = new(1.55f, 0.68f, 0.22f);

    public FireStyle Clone() => new()
    {
        Id = Id,
        Label = Label,
        DetailStrength = DetailStrength,
        ScrollSpeed = ScrollSpeed,
        FireHeight = FireHeight,
        FireShape = FireShape,
        FireThickness = FireThickness,
        FireSharpness = FireSharpness,
        Intensity = Intensity,
        NoiseOctaves = NoiseOctaves,
        NoiseLacunarity = NoiseLacunarity,
        NoiseGain = NoiseGain,
        NoiseAmplitude = NoiseAmplitude,
        NoiseFrequency = NoiseFrequency,
        ColorCore = ColorCore,
        ColorMid = ColorMid,
        ColorEdge = ColorEdge,
        ColorMix = ColorMix,
        MeshWidth = MeshWidth,
        MeshHeight = MeshHeight,
        LightEnergy = LightEnergy,
        LightRange = LightRange,
        LightColor = LightColor,
    };

    public static FireStyle Campfire() => new();

    public static FireStyle Tall() => new()
    {
        Id = "tall",
        Label = "细高",
        FireHeight = 1.45f,
        FireShape = 2.35f,
        FireThickness = 0.32f,
        FireSharpness = 1.15f,
        DetailStrength = 3.4f,
        ScrollSpeed = 1.35f,
        MeshWidth = 0.62f,
        MeshHeight = 2.05f,
        LightEnergy = 1.8f,
        LightRange = 4.2f,
        LightColor = new(1.6f, 0.7f, 0.2f),
    };

    public static FireStyle Wide() => new()
    {
        Id = "wide",
        Label = "宽焰",
        FireHeight = 0.78f,
        FireShape = 0.72f,
        FireThickness = 0.95f,
        FireSharpness = 0.72f,
        DetailStrength = 2.4f,
        ScrollSpeed = 0.85f,
        Intensity = 1.15f,
        MeshWidth = 1.45f,
        MeshHeight = 1.15f,
        ColorCore = new(1.6f, 1.15f, 0.42f),
        ColorMid = new(1.25f, 0.38f, 0.05f),
        LightEnergy = 2.8f,
        LightRange = 6.2f,
    };

    public static FireStyle Blue() => new()
    {
        Id = "blue",
        Label = "蓝火",
        ColorMix = 1f,
        ColorCore = new(0.72f, 0.95f, 1.6f),
        ColorMid = new(0.12f, 0.42f, 1.15f),
        ColorEdge = new(0.02f, 0.04f, 0.28f),
        FireHeight = 1.08f,
        FireShape = 1.7f,
        FireThickness = 0.48f,
        ScrollSpeed = 1.05f,
        DetailStrength = 3.2f,
        MeshWidth = 0.88f,
        MeshHeight = 1.55f,
        LightEnergy = 2.1f,
        LightRange = 5f,
        LightColor = new(0.35f, 0.62f, 1.35f),
    };

    public static FireStyle WillOWisp() => new()
    {
        Id = "wisp",
        Label = "鬼火",
        ColorMix = 1f,
        ColorCore = new(0.75f, 1.55f, 0.62f),
        ColorMid = new(0.12f, 0.85f, 0.28f),
        ColorEdge = new(0.01f, 0.16f, 0.05f),
        FireHeight = 0.92f,
        FireShape = 1.85f,
        FireThickness = 0.4f,
        FireSharpness = 0.82f,
        Intensity = 0.82f,
        ScrollSpeed = 0.55f,
        DetailStrength = 2.6f,
        NoiseFrequency = 1.15f,
        MeshWidth = 0.72f,
        MeshHeight = 1.25f,
        LightEnergy = 1.5f,
        LightRange = 4f,
        LightColor = new(0.28f, 1.05f, 0.42f),
    };

    public static FireStyle Flicker() => new()
    {
        Id = "flicker",
        Label = "急闪",
        DetailStrength = 4.6f,
        ScrollSpeed = 2.7f,
        FireHeight = 1.12f,
        FireShape = 1.7f,
        FireThickness = 0.5f,
        FireSharpness = 1.25f,
        NoiseOctaves = 7,
        NoiseFrequency = 2.15f,
        NoiseLacunarity = 3.4f,
        ColorCore = new(1.7f, 1.45f, 0.85f),
        ColorMid = new(1.45f, 0.32f, 0.04f),
        ColorEdge = new(0.4f, 0.04f, 0.0f),
        MeshWidth = 0.9f,
        MeshHeight = 1.55f,
        LightEnergy = 2.6f,
        LightRange = 5.2f,
        LightColor = new(1.75f, 0.55f, 0.12f),
    };

    public static FireStyle FromId(string id) => id switch
    {
        "tall" => Tall(),
        "wide" => Wide(),
        "blue" => Blue(),
        "wisp" => WillOWisp(),
        "flicker" => Flicker(),
        _ => Campfire(),
    };
}

/// <summary>gameidea 火焰 shader：值噪音 fBm + 水滴塑形 + 时间滚动。</summary>
public static class FireMaterials
{
    private static Shader? _shader;

    public static Shader Shader => _shader ??= ShaderLibrary.Load(ShaderLibrary.Fire);

    public static ShaderMaterial Create(FireStyle style)
    {
        var mat = new ShaderMaterial { Shader = Shader };
        Apply(mat, style);
        return mat;
    }

    public static void Apply(ShaderMaterial mat, FireStyle style)
    {
        mat.SetShaderParameter("detail_strength", style.DetailStrength);
        mat.SetShaderParameter("scroll_speed", style.ScrollSpeed);
        mat.SetShaderParameter("fire_height", style.FireHeight);
        mat.SetShaderParameter("fire_shape", style.FireShape);
        mat.SetShaderParameter("fire_thickness", style.FireThickness);
        mat.SetShaderParameter("fire_sharpness", style.FireSharpness);
        mat.SetShaderParameter("intensity", style.Intensity);
        mat.SetShaderParameter("noise_octaves", style.NoiseOctaves);
        mat.SetShaderParameter("noise_lacunarity", style.NoiseLacunarity);
        mat.SetShaderParameter("noise_gain", style.NoiseGain);
        mat.SetShaderParameter("noise_amplitude", style.NoiseAmplitude);
        mat.SetShaderParameter("noise_frequency", style.NoiseFrequency);
        mat.SetShaderParameter("color_core", style.ColorCore);
        mat.SetShaderParameter("color_mid", style.ColorMid);
        mat.SetShaderParameter("color_edge", style.ColorEdge);
        mat.SetShaderParameter("color_mix", style.ColorMix);
    }
}
