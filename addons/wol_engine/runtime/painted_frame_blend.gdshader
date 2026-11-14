shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;
uniform sampler2D frame_a : source_color, filter_linear_mipmap;
uniform sampler2D frame_b : source_color, filter_linear_mipmap;
uniform vec4 rect_a;
uniform vec4 rect_b;
uniform vec4 bounds;
uniform float frame_blend : hint_range(0.0, 1.0);
void fragment() {
 vec2 pixel = bounds.xy + UV * bounds.zw;
 vec2 a_uv = (pixel - rect_a.xy) / rect_a.zw;
 vec2 b_uv = (pixel - rect_b.xy) / rect_b.zw;
 vec4 a = texture(frame_a, a_uv);
 vec4 b = texture(frame_b, b_uv);
 if (any(lessThan(a_uv, vec2(0.0))) || any(greaterThan(a_uv, vec2(1.0)))) a = vec4(0.0);
 if (any(lessThan(b_uv, vec2(0.0))) || any(greaterThan(b_uv, vec2(1.0)))) b = vec4(0.0);
 float alpha = mix(a.a, b.a, frame_blend);
 if (alpha < 0.15) discard;
 ALBEDO = mix(a.rgb * a.a, b.rgb * b.a, frame_blend) / alpha;
 ALPHA = alpha;
}
