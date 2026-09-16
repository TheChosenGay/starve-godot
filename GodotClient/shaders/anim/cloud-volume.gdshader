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

// 整数 hash：替代 fract(sin(n)*k) 的写法。
//
// 为什么必须换：sin() 在 GPU 上是多周期超越函数，而这段噪声每像素要调用
// 上千次（见下方 MARCH_STEPS 注释）。实测"体积云关掉帧率 39→100+"，
// 说明它就是这个场景最大的 GPU 开销，而开销的大头正是串行执行的 sin()。
// 整数位运算（乘法 + 异或 + 移位）吞吐远高于 sin，且噪声质量等价。
float hash(float n) {
	uint x = uint(int(n));
	x ^= x >> 16u;
	x *= 0x7feb352du;
	x ^= x >> 15u;
	x *= 0x846ca68bu;
	x ^= x >> 16u;
	return float(x & 0x00ffffffu) / float(0x01000000u);
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

	// 空区域提前退出：先用 3 个粗采样探一下这条光线有没有云。
	// 天空占了屏幕很大面积，其中大部分像素是纯空（alpha≈0，最终被 discard）——
	// 对它们跑满 16 步是纯浪费。这一步能省掉大量空像素的完整步进。
	float probe = 0.0;
	for (int k = 0; k < 3; k++) {
		vec3 pp = ro + ray_dir * (t_enter + (float(k) + 0.5) * (t_exit - t_enter) / 3.0);
		probe += density_func(pp, TIME);
	}
	if (probe * march_step < 0.0015) {
		discard;
	}

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
