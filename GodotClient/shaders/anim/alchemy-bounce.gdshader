shader_type spatial;
render_mode cull_back, specular_schlick_ggx;

uniform float bounce_t = 0.0;
uniform float foot_y = -0.95;
uniform vec4 albedo : source_color = vec4(0.62, 0.48, 0.34, 1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white, filter_linear_mipmap;
uniform bool use_albedo_tex = false;
uniform sampler2D mr_tex : hint_default_white, filter_linear_mipmap;
uniform bool use_mr_tex = false;
uniform int metallic_channel = 2;
uniform int roughness_channel = 1;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float roughness : hint_range(0.0, 1.0) = 0.85;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap;
uniform bool use_normal_tex = false;
uniform float normal_scale = 1.0;

float tex_channel(vec4 t, int c) {
	if (c <= 0) return t.r;
	if (c == 1) return t.g;
	if (c == 2) return t.b;
	if (c == 3) return t.a;
	return (t.r + t.g + t.b) * 0.333333;
}

// 一次起跳：脚不离地，幅度全靠身上拉长/压扁。deform 逐跳衰减。
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

// x 恒为 0（脚钉在地面），y=纵向缩放，z=水平缩放。
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

void vertex() {
	vec3 pose = bounce_pose(bounce_t);
	float h = VERTEX.y - foot_y;
	VERTEX.y = foot_y + h * pose.y;
	VERTEX.x *= pose.z;
	VERTEX.z *= pose.z;
	NORMAL.y /= pose.y;
	NORMAL.xz /= pose.z;
	NORMAL = normalize(NORMAL);
	TANGENT.y *= pose.y;
	TANGENT.xz *= pose.z;
	TANGENT = normalize(TANGENT);
}

void fragment() {
	vec3 base = albedo.rgb;
	if (use_albedo_tex) {
		base *= texture(albedo_tex, UV).rgb;
	}
	ALBEDO = base;
	float m = metallic;
	float r = roughness;
	if (use_mr_tex) {
		vec4 packed = texture(mr_tex, UV);
		m *= tex_channel(packed, metallic_channel);
		r *= tex_channel(packed, roughness_channel);
	}
	METALLIC = m;
	ROUGHNESS = r;
	if (use_normal_tex) {
		NORMAL_MAP = texture(normal_tex, UV).rgb;
		NORMAL_MAP_DEPTH = normal_scale;
	}
}
