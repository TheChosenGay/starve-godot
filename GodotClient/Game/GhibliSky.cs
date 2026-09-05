using Godot;
using Starve.Core;

namespace GodotClient.Game;

/// <summary>
/// 吉卜力风体积云天空：光线步进 + FBM。
/// 云偏蓬松、亮、边缘软；颜色跟日夜周期走。
/// </summary>
public static class GhibliSky
{
    public static ShaderMaterial Create()
    {
        var mat = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        ApplyDefaults(mat);
        return mat;
    }

    public static ShaderMaterial CreateVolume()
    {
        var mat = new ShaderMaterial { Shader = new Shader { Code = VolumeShaderCode } };
        ApplyDefaults(mat);
        return mat;
    }

    public static ShaderMaterial CreateShadow()
    {
        var mat = new ShaderMaterial { Shader = new Shader { Code = ShadowShaderCode } };
        ApplyDefaults(mat);
        return mat;
    }

    public static Sky CreateSky(ShaderMaterial mat) => new()
    {
        SkyMaterial = mat,
        ProcessMode = Sky.ProcessModeEnum.Realtime,
        RadianceSize = Sky.RadianceSizeEnum.Size256,
    };

    public static void Apply(
        ShaderMaterial mat,
        DayCycleLook look,
        LightTune tune,
        Vector3 towardSun,
        float rain,
        Vector3 shadowRay = default)
    {
        if (towardSun.LengthSquared() < 1e-6f)
            towardSun = new Vector3(0.32f, 0.72f, -0.58f);
        else
            towardSun = towardSun.Normalized();
        if (shadowRay.LengthSquared() < 1e-6f)
            shadowRay = new Vector3(-0.5f, -0.707f, -0.5f);
        else
            shadowRay = shadowRay.Normalized();

        var cover = Mathf.Clamp(tune.CloudCoverage + rain * 0.26f, 0.04f, 0.95f);
        mat.SetShaderParameter("coverage", cover);
        mat.SetShaderParameter("thickness", tune.CloudThickness);
        mat.SetShaderParameter("wind", tune.CloudWind);
        mat.SetShaderParameter("absorption", 0.9f);
        mat.SetShaderParameter("sun_direction", towardSun);
        mat.SetShaderParameter("shadow_ray", shadowRay);
        mat.SetShaderParameter("sky_top", ToColor(look.SkyTop));
        mat.SetShaderParameter("sky_horizon", ToColor(look.SkyHorizon));
        mat.SetShaderParameter("ground_horizon", ToColor(look.GroundHorizon));
        mat.SetShaderParameter("ground_bottom", ToColor(look.GroundHorizon * 0.45f));
        mat.SetShaderParameter("sun_color", ToColor(look.SunColor));
        mat.SetShaderParameter("sun_energy", look.SunEnergy);
        mat.SetShaderParameter("ambient_color", ToColor(look.AmbientColor));
        mat.SetShaderParameter("night", look.NightWeight);
        mat.SetShaderParameter("cloud_lit", CloudLit(look));
        mat.SetShaderParameter("cloud_shadow", CloudShadow(look));
    }

    public static void ApplyDefaults(ShaderMaterial mat) =>
        Apply(mat, DayCyclePalette.Evaluate(0.5f), LightTune.Default, new Vector3(0.32f, 0.72f, -0.58f), 0f);

    private static Color CloudLit(DayCycleLook look)
    {
        var sun = ToColor(look.SunColor);
        var amb = ToColor(look.AmbientColor);
        var noon = sun.Lerp(new Color(0.96f, 0.97f, 0.98f), 0.22f * look.NoonWeight);
        var dusk = sun.Lerp(amb, 0.12f);
        var day = noon.Lerp(dusk, Mathf.Clamp(look.DuskWeight + look.MorningWeight * 0.55f, 0f, 1f));
        var night = new Color(amb.R * 0.45f, amb.G * 0.55f, amb.B * 0.85f);
        return day.Lerp(night, look.NightWeight);
    }

    private static Color CloudShadow(DayCycleLook look)
    {
        var sun = ToColor(look.SunColor);
        var amb = ToColor(look.AmbientColor);
        var day = amb.Lerp(sun, 0.18f) * new Color(0.52f, 0.58f, 0.66f);
        var night = new Color(amb.R * 0.18f, amb.G * 0.24f, amb.B * 0.42f);
        return day.Lerp(night, look.NightWeight);
    }

    private static Color ToColor(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    private const string ShaderCode = """
shader_type sky;

uniform float coverage : hint_range(0.0, 1.0) = 0.34;
uniform float thickness : hint_range(10.0, 100.0) = 48.0;
uniform float absorption : hint_range(0.1, 2.0) = 0.42;
uniform float softness : hint_range(0.02, 0.25) = 0.12;
uniform float wind : hint_range(0.0, 1.0) = 0.22;
uniform float scale : hint_range(0.4, 2.5) = 1.18;
uniform vec3 sun_direction = vec3(0.32, 0.72, -0.58);
uniform vec3 sky_top : source_color = vec3(0.42, 0.68, 0.92);
uniform vec3 sky_horizon : source_color = vec3(0.86, 0.90, 0.94);
uniform vec3 ground_horizon : source_color = vec3(0.55, 0.52, 0.42);
uniform vec3 ground_bottom : source_color = vec3(0.22, 0.24, 0.16);
uniform vec3 sun_color : source_color = vec3(1.0, 0.92, 0.78);
uniform vec3 ambient_color : source_color = vec3(0.78, 0.82, 0.88);
uniform vec3 cloud_lit : source_color = vec3(1.0, 0.98, 0.94);
uniform vec3 cloud_shadow : source_color = vec3(0.62, 0.72, 0.84);
uniform float sun_energy : hint_range(0.0, 3.0) = 0.65;
uniform float night : hint_range(0.0, 1.0) = 0.0;

const int MARCH_STEPS = 8;

float hash(float n) {
	return fract(sin(n) * 753.5453123);
}

float noise(vec3 x) {
	vec3 p = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	float n = p.x + p.y * 157.0 + 113.0 * p.z;
	return mix(
		mix(
			mix(hash(n + 0.0), hash(n + 1.0), f.x),
			mix(hash(n + 157.0), hash(n + 158.0), f.x),
			f.y
		),
		mix(
			mix(hash(n + 113.0), hash(n + 114.0), f.x),
			mix(hash(n + 270.0), hash(n + 271.0), f.x),
			f.y
		),
		f.z
	);
}

float fbm_clouds(vec3 pos, float lacunarity, float init_gain, float gain) {
	vec3 p = pos;
	float h = init_gain;
	float t = 0.0;
	for (int i = 0; i < 5; i++) {
		t += abs(noise(p)) * h;
		p *= lacunarity;
		h *= gain;
	}
	return t;
}

vec3 sun_dir() {
	return normalize(sun_direction);
}

vec3 render_sky_color(vec3 eye_dir) {
	float h = clamp(eye_dir.y, 0.0, 1.0);
	vec3 sky = mix(sky_horizon, sky_top, pow(h, 0.55));
	float sun_amount = max(dot(eye_dir, sun_dir()), 0.0);
	float glow = sun_energy * (1.0 - night * 0.55);
	sky += sun_color * glow * smoothstep(0.996, 0.9995, sun_amount) * 1.35;
	sky += sun_color * glow * pow(sun_amount, 22.0) * 0.26;
	sky += sun_color * glow * pow(sun_amount, 6.0) * 0.08;
	return sky;
}

float density_func(vec3 pos, float time) {
	vec3 drift = vec3(time * wind * 0.11, 0.0, time * wind * 0.07);
	vec3 p = pos * (0.00068 / max(scale, 0.2)) + drift;
	float dens = fbm_clouds(p * 1.58, 2.12, 0.55, 0.48);
	float gap = 1.0 - coverage;
	dens = smoothstep(gap, gap + softness, dens);
	return pow(dens, 0.72);
}

vec4 render_clouds(vec3 eye_dir, float time) {
	float y = max(eye_dir.y, 0.08);
	float march_step = thickness / float(MARCH_STEPS);
	vec3 projection = eye_dir / y;
	vec3 iter = projection * march_step;
	vec3 origin = vec3(0.0, 2.0, 0.0);
	vec3 cloud_pos = origin + projection * 90.0;
	float alpha = 0.0;
	vec3 cloud_color = vec3(0.0);
	float t = 1.0;
	vec3 sun = sun_dir();
	float sun_t = max(dot(normalize(eye_dir), sun), 0.0);

	for (int i = 0; i < MARCH_STEPS; i++) {
		float height = clamp((cloud_pos.y - origin.y) / thickness, 0.0, 1.0);
		float density = density_func(cloud_pos, time);
		float ti = exp(-absorption * density * march_step);
		t *= ti;
		vec3 scatter = mix(cloud_shadow, cloud_lit, smoothstep(0.12, 0.82, height));
		scatter = mix(scatter, sun_color, pow(sun_t, 7.0) * 0.2 * (1.0 - night));
		cloud_color += t * scatter * density * march_step * (0.82 + 0.5 * height);
		alpha += (1.0 - ti) * (1.0 - alpha);
		cloud_pos += iter;
		if (alpha > 0.99) {
			break;
		}
	}

	vec3 p = cloud_pos * (0.00068 / max(scale, 0.2)) + vec3(time * wind * 0.11, 0.0, time * wind * 0.07);
	float dens2 = fbm_clouds(p * 0.22, 2.0, 0.5, 0.52);
	float dens3 = fbm_clouds(p * 0.18, 2.0, 0.52, 0.5);
	vec3 tint = mix(ambient_color, sun_color, 0.42 * (1.0 - night));
	tint.g *= mix(1.0, 0.93, dens2);
	tint.b *= mix(1.0, 0.88, dens3);
	vec3 painted = pow(cloud_color * mix(tint, vec3(1.0), 0.08 * (1.0 - night)), vec3(0.90, 0.94, 1.0));
	painted *= mix(vec3(0.10, 0.12, 0.20), vec3(1.0), 1.0 - night);
	return vec4(painted, alpha * mix(0.55, 0.96, 1.0 - night * 0.7));
}

void sky() {
	vec3 eye_dir = normalize(EYEDIR);
	if (eye_dir.y < 0.0) {
		float g = clamp(-eye_dir.y, 0.0, 1.0);
		COLOR = mix(ground_horizon, ground_bottom, g);
		return;
	}

	vec3 sky = render_sky_color(eye_dir);
	vec4 clouds = render_clouds(eye_dir, TIME);
	float fade = smoothstep(0.02, 0.16, eye_dir.y);
	clouds.a *= fade;
	COLOR = mix(sky, clouds.rgb, clouds.a);
}
""";

    private const string VolumeShaderCode = """
shader_type spatial;
render_mode unshaded, blend_mix, cull_back, depth_draw_never, shadows_disabled, fog_disabled, ambient_light_disabled;

// 原文：hash + 3D value noise + abs-FBM + 光线步进
// https://gameidea.org/2025/01/22/making-a-stylized-sky-shader-with-volumetric-clouds/

uniform float coverage : hint_range(0.0, 1.0) = 0.23;
uniform float thickness : hint_range(10.0, 100.0) = 55.0;
uniform float absorption : hint_range(0.1, 2.0) = 0.9;
uniform float wind : hint_range(0.0, 2.0) = 0.06;
uniform vec3 sun_direction = vec3(0.0, 0.4, -1.0);
uniform vec3 sun_color : source_color = vec3(1.0, 0.92, 0.78);
uniform vec3 ambient_color : source_color = vec3(0.78, 0.82, 0.88);
uniform vec3 cloud_lit : source_color = vec3(1.0, 0.98, 0.94);
uniform vec3 cloud_shadow : source_color = vec3(0.62, 0.72, 0.84);
uniform float sun_energy : hint_range(0.0, 3.0) = 0.65;
uniform float night : hint_range(0.0, 1.0) = 0.0;
uniform vec3 box_center = vec3(0.0, 22.0, 0.0);
uniform vec3 box_size = vec3(320.0, 10.0, 320.0);

const int MARCH_STEPS = 16;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

float hash(float n) {
	return fract(sin(n) * 753.5453123);
}

float noise(vec3 x) {
	vec3 p = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	float n = p.x + p.y * 157.0 + 113.0 * p.z;
	return mix(
		mix(
			mix(hash(n + 0.0), hash(n + 1.0), f.x),
			mix(hash(n + 157.0), hash(n + 158.0), f.x),
			f.y
		),
		mix(
			mix(hash(n + 113.0), hash(n + 114.0), f.x),
			mix(hash(n + 270.0), hash(n + 271.0), f.x),
			f.y
		),
		f.z
	);
}

float fbm_clouds(vec3 pos, float lacunarity, float init_gain, float gain) {
	vec3 p = pos;
	float H = init_gain;
	float t = 0.0;
	for (int i = 0; i < 5; i++) {
		t += abs(noise(p)) * H;
		p *= lacunarity;
		H *= gain;
	}
	return t;
}

float density_func(vec3 pos, float time) {
	vec3 p = pos * 0.09 + vec3(time * wind * 0.55, 0.0, -time * wind * 1.05);
	float dens = fbm_clouds(p * 2.032, 2.6434, 0.5, 0.5);
	float gap = 1.0 - coverage;
	dens *= smoothstep(gap, gap + 0.035, dens);
	vec3 bmin = box_center - box_size * 0.5;
	float h = clamp((pos.y - bmin.y) / max(box_size.y, 0.001), 0.0, 1.0);
	dens *= smoothstep(0.0, 0.28, h) * smoothstep(1.0, 0.72, h);
	float r = length(pos.xz - box_center.xz) / max(box_size.x * 0.5, 0.001);
	dens *= smoothstep(1.0, 0.62, r);
	return dens;
}

void fragment() {
	vec3 cam_fwd = normalize(-INV_VIEW_MATRIX[2].xyz);
	vec3 cam_pos = INV_VIEW_MATRIX[3].xyz;
	bool is_ortho = PROJECTION_MATRIX[3][3] != 0.0;
	vec3 ray_dir = is_ortho ? cam_fwd : normalize(world_pos - cam_pos);
	vec3 ro = world_pos - ray_dir * 80.0;

	vec3 bmin = box_center - box_size * 0.5;
	vec3 bmax = box_center + box_size * 0.5;
	vec3 inv = 1.0 / ray_dir;
	vec3 t0 = (bmin - ro) * inv;
	vec3 t1 = (bmax - ro) * inv;
	vec3 tmin3 = min(t0, t1);
	vec3 tmax3 = max(t0, t1);
	float t_enter = max(max(tmin3.x, tmin3.y), tmin3.z);
	float t_exit = min(min(tmax3.x, tmax3.y), tmax3.z);
	if (t_exit <= 0.0 || t_enter > t_exit) {
		discard;
	}
	t_enter = max(t_enter, 0.0);

	float march_step = (t_exit - t_enter) / float(MARCH_STEPS);
	float alpha = 0.0;
	vec3 cloud_color = vec3(0.0);
	float T = 1.0;
	vec3 cloud_pos = ro;

	for (int i = 0; i < MARCH_STEPS; i++) {
		cloud_pos = ro + ray_dir * (t_enter + (float(i) + 0.5) * march_step);
		float height = clamp((cloud_pos.y - bmin.y) / max(box_size.y, 0.001), 0.0, 1.0);
		float density = density_func(cloud_pos, TIME) * mix(0.45, 1.55, clamp((thickness - 18.0) / 72.0, 0.0, 1.0));
		float Ti = exp(-absorption * density * march_step);
		T *= Ti;
		cloud_color += T * exp(height) * density * march_step;
		alpha += (1.0 - Ti) * (1.0 - alpha);
		if (alpha > 0.99) {
			break;
		}
	}

	vec3 p = cloud_pos * 0.09 + vec3(TIME * wind * 0.55, 0.0, -TIME * wind * 1.05);
	float dens2 = fbm_clouds(p * 0.2, 2.0, 0.5, 0.52);
	float dens3 = fbm_clouds(p * 0.2, 2.0, 0.52, 0.5);
	float lum = clamp(dot(max(cloud_color, vec3(0.0)), vec3(0.33)), 0.0, 1.4);
	float top = clamp((cloud_pos.y - bmin.y) / max(box_size.y, 0.001), 0.0, 1.0);
	vec3 shade = mix(cloud_shadow, cloud_lit, smoothstep(0.08, 0.78, top * 0.5 + lum * 0.5));
	shade.g *= mix(1.0, 0.92, dens2);
	shade.b *= mix(1.0, 0.86, dens3);
	vec3 light_tint = mix(ambient_color, sun_color, clamp(sun_energy * 0.38, 0.0, 0.8) * (1.0 - night));
	shade *= mix(light_tint, sun_color, 0.28 * (1.0 - night));
	shade *= mix(0.10, 1.0, 1.0 - night);
	shade += sun_color * sun_energy * 0.05 * (1.0 - night) * lum;

	if (alpha < 0.01) {
		discard;
	}
	ALBEDO = shade;
	ALPHA = clamp(alpha * mix(0.38, 0.92, 1.0 - night * 0.6), 0.0, 0.88);
}
""";

    private const string ShadowShaderCode = """
shader_type spatial;
render_mode unshaded, blend_mul, cull_back, shadows_disabled, fog_disabled, depth_draw_opaque;

uniform float coverage : hint_range(0.0, 1.0) = 0.23;
uniform float thickness : hint_range(10.0, 100.0) = 55.0;
uniform float absorption : hint_range(0.1, 2.0) = 0.9;
uniform float wind : hint_range(0.0, 2.0) = 0.06;
uniform vec3 sun_direction = vec3(0.32, 0.72, -0.58);
uniform vec3 shadow_ray = vec3(-0.5, -0.707, -0.5);
uniform vec3 box_center = vec3(0.0, 22.0, 0.0);
uniform vec3 box_size = vec3(320.0, 10.0, 320.0);
uniform float shadow_strength : hint_range(0.0, 1.0) = 0.5;
uniform float night : hint_range(0.0, 1.0) = 0.0;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

float hash(float n) {
	return fract(sin(n) * 753.5453123);
}

float noise(vec3 x) {
	vec3 p = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	float n = p.x + p.y * 157.0 + 113.0 * p.z;
	return mix(
		mix(
			mix(hash(n + 0.0), hash(n + 1.0), f.x),
			mix(hash(n + 157.0), hash(n + 158.0), f.x),
			f.y
		),
		mix(
			mix(hash(n + 113.0), hash(n + 114.0), f.x),
			mix(hash(n + 270.0), hash(n + 271.0), f.x),
			f.y
		),
		f.z
	);
}

float fbm_clouds(vec3 pos, float lacunarity, float init_gain, float gain) {
	vec3 p = pos;
	float H = init_gain;
	float t = 0.0;
	for (int i = 0; i < 5; i++) {
		t += abs(noise(p)) * H;
		p *= lacunarity;
		H *= gain;
	}
	return t;
}

float density_func(vec3 pos, float time) {
	vec3 p = pos * 0.09 + vec3(time * wind * 0.55, 0.0, -time * wind * 1.05);
	float dens = fbm_clouds(p * 2.032, 2.6434, 0.5, 0.5);
	float gap = 1.0 - coverage;
	dens *= smoothstep(gap, gap + 0.035, dens);
	vec3 bmin = box_center - box_size * 0.5;
	float h = clamp((pos.y - bmin.y) / max(box_size.y, 0.001), 0.0, 1.0);
	dens *= smoothstep(0.0, 0.28, h) * smoothstep(1.0, 0.72, h);
	float r = length(pos.xz - box_center.xz) / max(box_size.x * 0.5, 0.001);
	dens *= smoothstep(1.0, 0.62, r);
	return dens;
}

void fragment() {
	vec3 mid = vec3(world_pos.x, box_center.y, world_pos.z);
	float slab = max(box_size.y, 0.001);
	float thick = mix(0.45, 1.55, clamp((thickness - 18.0) / 72.0, 0.0, 1.0));
	float step_size = slab / 4.0;
	float optical = 0.0;
	for (int i = 0; i < 4; i++) {
		float h = (float(i) + 0.5) / 4.0;
		vec3 sample_pos = mid + vec3(0.0, (h - 0.5) * slab, 0.0);
		optical += density_func(sample_pos, TIME) * step_size * thick;
	}
	float transmit = exp(-absorption * optical);
	float day_shadow = 1.0 - smoothstep(0.28, 0.72, night);
	float shade = clamp((1.0 - transmit) * shadow_strength * day_shadow, 0.0, 0.72);
	ALBEDO = vec3(1.0 - shade);
}
""";
}
