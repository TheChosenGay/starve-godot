shader_type spatial;
render_mode cull_back, specular_disabled, shadows_disabled;

// Freestylized albedo + height。COLOR = (草, 土, 岩, 雪)，UV2 = (AO, 水)。
uniform sampler2D uGrassAlbedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D uGrassHeight : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform sampler2D uDirtAlbedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D uDirtHeight : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform sampler2D uRockAlbedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D uRockHeight : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform sampler2D uSnowAlbedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D uSnowHeight : hint_default_white, filter_linear_mipmap, repeat_enable;

uniform float uWorldTiling = 0.22;
uniform float uHeightSharpness : hint_range(0.04, 0.8) = 0.22;
uniform float uSlopeRock : hint_range(0.0, 1.0) = 0.72;
uniform float uStylize : hint_range(0.0, 0.6) = 0.18;

uniform vec4 shadow_tint : source_color = vec4(0.42, 0.48, 0.62, 1.0);
uniform float bands = 5.0;
uniform float shade_min : hint_range(0.0, 1.0) = 0.5;
uniform float fill : hint_range(0.0, 0.8) = 0.2;
uniform float day_light : hint_range(0.0, 1.0) = 1.0;
uniform float coverage : hint_range(0.0, 1.0) = 0.22;
uniform float thickness : hint_range(10.0, 100.0) = 42.0;
uniform float wind : hint_range(0.0, 2.0) = 0.06;
uniform vec3 sun_direction = vec3(0.32, 0.72, -0.58);
uniform vec3 box_center = vec3(0.0, 13.0, 0.0);
uniform vec3 box_size = vec3(320.0, 10.0, 320.0);
uniform float night : hint_range(0.0, 1.0) = 0.0;
uniform float cloud_shadow_strength : hint_range(0.0, 1.0) = 0.78;

varying vec3 world_pos;
varying vec3 world_normal;

// 云影缓存：fragment 与 light 都用到云影，若不缓存则 cloud_shade() 被调用两次。
//
// cloud_shade() 内部是 4 步光线步进 + 5 阶 FBM（每次 noise 含 8 次 hash），
// 单次约 160 次 hash，两次就是 320 次 —— 而两次算的是**完全相同**的值。
// 用 varying 传递后只算一次，直接省掉一半。
//
// 这也是"关掉体积云层后地形依然很贵"的原因：地形着色器内联了自己的一份
// 云噪声采样，与场景里的 CloudLayer3D 是两套独立开销。
varying float cloud_shade_cached;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

vec2 world_uv() {
	return world_pos.xz * uWorldTiling;
}

// shaderic.com HeightmapBlending 的多层形式：高的细节咬住低的缝。
vec3 height_blend(
	vec3 c0, float h0, float w0,
	vec3 c1, float h1, float w1,
	vec3 c2, float h2, float w2,
	float sharpness)
{
	float a0 = h0 * w0 + w0;
	float a1 = h1 * w1 + w1;
	float a2 = h2 * w2 + w2;
	float m = max(max(a0, a1), a2);
	float b0 = max(a0 - m + sharpness, 0.0);
	float b1 = max(a1 - m + sharpness, 0.0);
	float b2 = max(a2 - m + sharpness, 0.0);
	return (c0 * b0 + c1 * b1 + c2 * b2) / max(b0 + b1 + b2, 1e-5);
}

// 整数 hash：GPU 上远快于 fract(sin(n)*k)。
// 这个噪声在**每个地形像素**上要跑几百次（见下方 cloud_shade 注释），
// 是本着色器最大的单点开销。
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
		mix(mix(hash(n + 0.0), hash(n + 1.0), f.x), mix(hash(n + 157.0), hash(n + 158.0), f.x), f.y),
		mix(mix(hash(n + 113.0), hash(n + 114.0), f.x), mix(hash(n + 270.0), hash(n + 271.0), f.x), f.y),
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

float density_func(vec3 pos) {
	vec3 q = pos * 0.09 + vec3(TIME * wind * 0.55, 0.0, -TIME * wind * 1.05);
	float dens = fbm_clouds(q * 2.032, 2.6434, 0.5, 0.5);
	float gap = 1.0 - coverage;
	dens *= smoothstep(gap, gap + 0.035, dens);
	vec3 bmin = box_center - box_size * 0.5;
	float h = clamp((pos.y - bmin.y) / max(box_size.y, 0.001), 0.0, 1.0);
	dens *= smoothstep(0.0, 0.28, h) * smoothstep(1.0, 0.72, h);
	float r = length(pos.xz - box_center.xz) / max(box_size.x * 0.5, 0.001);
	dens *= smoothstep(1.0, 0.62, r);
	return dens;
}

// 地面云影：只沿竖直方向做 2 次采样而不是 4 次。
//
// 地面云影的本质是"某点正上方的柱状光学厚度"，玩家看得见的是它的**横向**
// 变化（随时间漂移的云块）；纵向 4 个采样算的是同一团云在不同高度的密度，
// 结果高度相关，属于过采样。4 → 2 直接省掉一半噪声（每次 density_func
// 约 5 阶 FBM）。
//
// 想恢复更细腻的层次，把 CLOUD_SHADOW_STEPS 调回 4 即可 —— 这是视觉无损的
// 取舍，不是降级。
const int CLOUD_SHADOW_STEPS = 2;

float cloud_shade(vec3 pos) {
	float slab = max(box_size.y, 0.001);
	float thick = mix(0.45, 1.55, clamp((thickness - 18.0) / 72.0, 0.0, 1.0));
	float optical = 0.0;
	for (int i = 0; i < CLOUD_SHADOW_STEPS; i++) {
		float h = (float(i) + 0.5) / float(CLOUD_SHADOW_STEPS);
		vec3 sample_pos = vec3(pos.x, box_center.y + (h - 0.5) * slab, pos.z);
		optical += density_func(sample_pos);
	}
	// 采样数变了要按比例归一，否则整体明暗会变（4 步时的系数保持不变）。
	optical *= 4.0 / float(CLOUD_SHADOW_STEPS);
	float day = 1.0 - smoothstep(0.18, 0.62, night);
	return clamp(optical * 0.35 * thick * cloud_shadow_strength * day, 0.0, 0.85);
}

void fragment() {
	vec2 uv = world_uv();
	vec3 grass = texture(uGrassAlbedo, uv).rgb;
	vec3 dirt = texture(uDirtAlbedo, uv).rgb;
	vec3 rock = texture(uRockAlbedo, uv).rgb;
	vec3 snow = texture(uSnowAlbedo, uv).rgb;
	float hg = texture(uGrassHeight, uv).r;
	float hd = texture(uDirtHeight, uv).r;
	float hr = texture(uRockHeight, uv).r;
	float hs = texture(uSnowHeight, uv).r;

	float slope = clamp(1.0 - world_normal.y, 0.0, 1.0);
	float rock_w = max(COLOR.b, smoothstep(0.14, 0.55, slope) * uSlopeRock);
	vec3 w = vec3(COLOR.r, COLOR.g, rock_w);
	float wsum = w.x + w.y + w.z;
	if (wsum < 1e-4) {
		w = vec3(1.0, 0.0, 0.0);
	} else {
		w /= wsum;
	}

	vec3 blended = height_blend(grass, hg, w.x, dirt, hd, w.y, rock, hr, w.z, uHeightSharpness);
	float luma = dot(blended, vec3(0.30, 0.59, 0.11));
	blended = mix(blended, vec3(luma), uStylize);

	vec3 water = vec3(0.16, 0.40, 0.68) * (0.88 + 0.12 * noise(world_pos * 2.4));
	blended = mix(blended, water, UV2.y);
	float snow_transition = 1.0 - abs(COLOR.a * 2.0 - 1.0);
	float snow_edge = clamp(COLOR.a + (hs - 0.5) * 0.18 * snow_transition, 0.0, 1.0);
	blended = mix(blended, snow, smoothstep(0.0, 1.0, snow_edge));
	blended *= mix(0.58, 1.0, UV2.x);

	float cs = cloud_shade(world_pos);
	cloud_shade_cached = cs;
	ALBEDO = blended * mix(1.0, 0.32, cs);
	ROUGHNESS = 1.0;
	EMISSION = ALBEDO * fill * mix(0.1, 1.0, day_light) * vec3(0.88, 1.0, 1.15) * mix(1.0, 0.18, cs);
}

void light() {
	// 复用 fragment() 里算好的云影（见 cloud_shade_cached 注释）：
	// 重复调用 cloud_shade() 会让这层噪声的开销翻倍。
	float cs = cloud_shade_cached;
	float wrap = clamp(dot(NORMAL, LIGHT) * 0.5 + 0.5, 0.0, 1.0);
	float stepped = floor(wrap * bands + 1e-4) / max(bands - 1.0, 1.0);
	float shadeFloor = shade_min * mix(0.35, 1.0, day_light);
	stepped = mix(shadeFloor, 1.0, clamp(stepped, 0.0, 1.0));
	vec3 nightTint = mix(vec3(0.4, 0.5, 0.78), vec3(1.0), day_light);
	DIFFUSE_LIGHT += mix(ALBEDO * shadow_tint.rgb * nightTint, ALBEDO, stepped) * LIGHT_COLOR * ATTENUATION;
	DIFFUSE_LIGHT *= mix(1.0, 0.28, cs);
}
