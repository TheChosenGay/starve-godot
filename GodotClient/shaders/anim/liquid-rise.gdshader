shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, world_vertex_coords;

uniform float play_t = 0.0;
uniform float duration = 1.75;
uniform float loop_mode = 0.0;
uniform float thickness = 0.01;
uniform float rise = 0.96;
uniform float swirl = 1.4;
uniform float ripple = 2.4;
uniform float glow = 1.49;
uniform float dissolve_scale = 8.6;
uniform float color_mix : hint_range(0.0, 1.0) = 0.82;
uniform vec4 tint : source_color = vec4(0.32, 0.12, 0.48, 0.78);

instance uniform float seed = 0.0;
instance uniform vec4 surface_color : source_color = vec4(0.72, 0.62, 0.48, 1.0);

varying float v_age;
varying float v_along;
varying float v_leave;
varying vec3 v_world;
varying vec3 v_col;

float hash21(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float value_noise(vec2 x) {
	vec2 p = floor(x);
	vec2 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(
		mix(hash21(p), hash21(p + vec2(1.0, 0.0)), f.x),
		mix(hash21(p + vec2(0.0, 1.0)), hash21(p + vec2(1.0, 1.0)), f.x),
		f.y
	);
}

float fbm(vec2 p) {
	float a = 0.5;
	float s = 0.0;
	for (int i = 0; i < 5; i++) {
		s += value_noise(p) * a;
		p = p * 2.13 + vec2(17.2, 9.1);
		a *= 0.5;
	}
	return s;
}

float cycle_age() {
	float d = max(duration, 0.08);
	if (loop_mode > 0.5) {
		return fract(play_t / d);
	}
	return clamp(play_t / d, 0.0, 1.0);
}

float patch_id(vec3 p) {
	float raw = fbm(p.xz * dissolve_scale * 0.42 + vec2(p.y * dissolve_scale * 0.28, seed * 2.4));
	return floor(raw * 5.0);
}

void vertex() {
	float age = cycle_age();
	v_age = age;
	v_col = mix(surface_color.rgb, tint.rgb, color_mix);

	float layer = patch_id(VERTEX);
	float appear_at = layer * 0.07;
	float on = step(appear_at, age) * step(1.0, layer);
	float leave_at = 0.40 + layer * 0.055;
	float leave = smoothstep(leave_at, leave_at + 0.30, age) * on;
	vec3 world = VERTEX;
	world += NORMAL * thickness * on * (0.55 + 0.20 * layer);
	float sway = leave * swirl;
	float flick = 7.0 + ripple * 2.4;
	world.y += leave * rise * (0.28 + 0.12 * layer);
	world.x += sin(VERTEX.y * 7.2 + age * flick + layer * 1.7 + seed * 5.0) * sway * 0.11;
	world.z += cos(VERTEX.x * 6.4 + age * (flick * 0.86) + layer * 1.3 + seed * 4.0) * sway * 0.09;
	world.x += sin(VERTEX.y * 3.1 + age * 3.6 * ripple + seed) * sway * 0.07 * leave;
	VERTEX = world;
	v_along = layer;
	v_leave = leave;
	v_world = world;
}

void fragment() {
	float age = v_age;
	float layer = v_along;
	float appear_at = layer * 0.07;
	float on = step(appear_at, age) * step(1.0, layer);
	float n = fbm(v_world.xz * dissolve_scale + vec2(v_world.y * 0.55 - age * ripple, seed));
	float keep = on * step(0.22, n);
	keep *= 1.0 - smoothstep(0.82, 1.0, v_leave);
	float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 1.6);
	float fade = 1.0 - v_leave * 0.72;
	float a = (0.26 + 0.40 * fres) * keep * fade * glow * tint.a;
	if (a < 0.02) {
		discard;
	}

	vec3 col = mix(v_col, tint.rgb, fres * 0.2 + v_leave * 0.25);
	ALBEDO = col * (0.72 + 0.38 * glow);
	ALPHA = a;
}
