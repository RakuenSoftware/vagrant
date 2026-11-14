shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;
uniform float strength = 0.16;
void fragment() {
 float radius = length((UV-vec2(0.5))*2.0);
 ALBEDO = vec3(0.02,0.015,0.01);
 ALPHA = (1.0-smoothstep(0.2,1.0,radius))*strength;
}
