shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

uniform float play_t = 0.0;
uniform float duration = 2.4;
uniform float loop_mode = 0.0;
uniform float lift = 1.55;
uniform float sway = 0.28;
uniform float size = 0.055;
uniform float glow = 0.85;
uniform float color_mix : hint_range(0.0, 1.0) = 0.22;
uniform vec4 tint : source_color = vec4(0.95, 0.90, 0.78, 1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap;
uniform bool use_albedo_tex = false;

varying float v_age;
varying vec3 v_col;
varying float v_seed;

float hash11(float x) {
	return fract(sin(x * 127.1) * 43758.5453);
}

void vertex() {
	float seed = INSTANCE_CUSTOM.z + float(INSTANCE_ID) * 0.0173;
	v_seed = seed;

	float age;
	if (loop_mode > 0.5) {
		float cycle = 0.55 + 0.45 * hash11(seed + 2.1);
		age = fract(TIME * cycle * 0.38 + seed);
	} else {
		float start = hash11(seed) * 0.42 * duration;
		float life = duration * (0.62 + 0.38 * hash11(seed + 1.7));
		age = clamp((play_t - start) / max(life, 0.08), 0.0, 1.0);
	}
	v_age = age;

	vec3 baked = COLOR.rgb;
	vec3 sampled = baked;
	if (use_albedo_tex) {
		vec4 tex = texture(albedo_tex, INSTANCE_CUSTOM.xy);
		sampled = mix(baked, tex.rgb, tex.a);
	}
	v_col = mix(sampled, tint.rgb, color_mix);

	vec3 origin = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
	vec3 nrm = normalize(MODEL_MATRIX[1].xyz);
	float peel = 1.0 - age * 0.35;
	origin += nrm * (0.035 + 0.06 * peel);
	origin.y += lift * pow(age, 0.70);

	float w = age * 6.4 + seed * 14.0;
	origin.x += sin(w) * sway * age;
	origin.z += cos(w * 0.81 + seed) * sway * age * 0.72;

	vec3 cam_x = normalize(INV_VIEW_MATRIX[0].xyz);
	vec3 cam_y = normalize(INV_VIEW_MATRIX[1].xyz);
	vec2 q = UV * 2.0 - 1.0;
	float ang = seed * 6.28318;
	float ca = cos(ang);
	float sa = sin(ang);
	vec2 r = vec2(q.x * ca - q.y * sa, q.x * sa + q.y * ca);

	float fade = sin(clamp(age, 0.0, 1.0) * 3.14159265);
	float s = size * (0.55 + 0.7 * hash11(seed + 4.4)) * mix(0.35, 1.2, fade);
	vec3 world = origin + cam_x * r.x * s * 0.42 + cam_y * r.y * s;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world, 1.0);
}

void fragment() {
	vec2 p = UV * 2.0 - 1.0;
	float rx = p.x / 0.42;
	float ry = p.y / 1.0;
	float flake = exp(-(rx * rx + ry * ry) * 2.6);
	float spark = 0.72 + 0.28 * sin(TIME * 7.5 + v_seed * 21.0);
	float fade = sin(clamp(v_age, 0.0, 1.0) * 3.14159265);
	float a = flake * fade * glow * spark;
	ALBEDO = v_col * (0.55 + 0.75 * glow);
	ALPHA = a;
}
