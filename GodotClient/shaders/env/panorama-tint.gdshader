shader_type sky;

uniform sampler2D panorama : source_color, filter_linear, hint_default_black;
uniform vec3 sky_tint : source_color = vec3(1.0);
uniform float energy : hint_range(0.0, 2.0) = 1.0;

void sky() {
	COLOR = texture(panorama, SKY_COORDS).rgb * sky_tint * energy;
}
