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
