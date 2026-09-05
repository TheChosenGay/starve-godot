shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

uniform float detail_strength = 3.0;
uniform float scroll_speed = 1.2;
uniform float fire_height = 1.0;
uniform float fire_shape = 1.5;
uniform float fire_thickness = 0.55;
uniform float fire_sharpness = 1.0;
uniform float intensity = 1.0;

uniform int noise_octaves = 6;
uniform float noise_lacunarity = 3.0;
uniform float noise_gain = 0.5;
uniform float noise_amplitude = 1.0;
uniform float noise_frequency = 1.5;

uniform vec4 color_core : source_color = vec4(1.55, 1.32, 0.62, 1.0);
uniform vec4 color_mid : source_color = vec4(1.35, 0.42, 0.07, 1.0);
uniform vec4 color_edge : source_color = vec4(0.28, 0.04, 0.01, 1.0);
uniform float color_mix : hint_range(0.0, 1.0) = 1.0;

float hash(vec2 p) {
	p = fract(p * 0.3183099 + vec2(0.1, 0.1));
	p *= 17.0;
	return fract(p.x * p.y * (p.x + p.y));
}

float value_noise(vec2 x) {
	vec2 p = floor(x);
	vec2 f = fract(x);
	return
		hash(p) * (1.0 - f.x) * (1.0 - f.y) +
		hash(p + vec2(1.0, 0.0)) * f.x * (1.0 - f.y) +
		hash(p + vec2(0.0, 1.0)) * (1.0 - f.x) * f.y +
		hash(p + vec2(1.0, 1.0)) * f.x * f.y;
}

float fbm(vec2 p) {
	int octaves = clamp(noise_octaves, 1, 8);
	float lacunarity = noise_lacunarity;
	float gain = noise_gain;
	float amplitude = noise_amplitude;
	float frequency = noise_frequency;
	float total = 0.0;
	for (int i = 0; i < 8; i++) {
		if (i >= octaves) break;
		total += value_noise(p * frequency) * amplitude;
		frequency *= lacunarity;
		amplitude *= gain;
	}
	return total * 0.5;
}

void fragment() {
	// QuadMesh 的 UV.y=0 在网格顶。翻成 0=脚、1=尖，火从下往上窜。
	vec2 uv = vec2(UV.x, 1.0 - UV.y);
	vec2 modified_uv = vec2(uv.x - 0.5, uv.y);

	float scroll = scroll_speed * detail_strength * TIME;
	float fire_noise = fbm(detail_strength * modified_uv - vec2(0.0, scroll));

	// fire_intensity：水滴 SDF（底宽顶尖），再减去噪音咬出火舌。
	float fire_intensity = intensity - 16.0 * fire_sharpness * pow(
		max(
			0.0,
			length(
				modified_uv * vec2(
					(1.0 / fire_thickness) + modified_uv.y * fire_shape,
					1.0 / fire_height
				)
			) - fire_noise * max(0.0, modified_uv.y + 0.05)
		),
		1.2
	);

	// fire_intensity1：再乘回噪音（内部纹理）并压住顶端。
	float fire_intensity1 = fire_noise * fire_intensity * (1.5 - pow(uv.y, 14.0));
	fire_intensity1 = clamp(fire_intensity1, 0.0, 1.0);

	vec3 heat = vec3(
		1.5 * fire_intensity1,
		1.5 * pow(fire_intensity1, 3.0),
		pow(fire_intensity1, 6.0)
	);
	vec3 graded = mix(color_edge.rgb, color_mid.rgb, smoothstep(0.02, 0.45, fire_intensity1));
	graded = mix(graded, color_core.rgb, smoothstep(0.38, 0.95, fire_intensity1));
	vec3 fire_color = mix(heat, graded, color_mix);

	float alpha = clamp(fire_intensity * (1.0 - pow(uv.y, 3.0)), 0.0, 1.0);
	if (alpha < 0.04) discard;

	ALBEDO = mix(vec3(0.0), fire_color, alpha);
	ALPHA = alpha;
}
