shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;
uniform sampler2D pigment : source_color, filter_linear_mipmap, repeat_disable;
uniform vec3 cloth_points[117];
uniform vec3 cloth_origin = vec3(0.0);
void vertex() {
 vec2 address = clamp(UV, vec2(0.0), vec2(0.99999))*vec2(8.0,12.0);
 ivec2 cell = ivec2(floor(address));
 vec2 blend = fract(address);
 int i = cell.y*9+cell.x;
 VERTEX = mix(mix(cloth_points[i],cloth_points[i+1],blend.x),mix(cloth_points[i+9],cloth_points[i+10],blend.x),blend.y)-cloth_origin;
}
void fragment() {
 vec4 paint = texture(pigment,UV);
 ALBEDO = paint.rgb;
 ALPHA = paint.a;
 ALPHA_SCISSOR_THRESHOLD = 0.15;
}
