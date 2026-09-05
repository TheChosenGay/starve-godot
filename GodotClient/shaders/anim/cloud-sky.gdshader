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
	} else {
		vec3 sky = render_sky_color(eye_dir);
		vec4 clouds = render_clouds(eye_dir, TIME);
		float fade = smoothstep(0.02, 0.16, eye_dir.y);
		clouds.a *= fade;
		COLOR = mix(sky, clouds.rgb, clouds.a);
	}
}
