shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;
uniform sampler2D atlas : source_color, filter_linear_mipmap, repeat_disable;
uniform vec4 region;
uniform vec4 tint : source_color = vec4(1.0);
uniform vec2 part_size;
uniform vec2 art_points[4];
uniform vec2 pose_points[4];
uniform float pose_depths[4];
uniform vec2 basis_x[3];
uniform vec2 basis_y[3];
uniform vec2 depth_gradients[3];
uniform int segment_count = 1;
uniform float body_depth_slope;
uniform float body_depth_intercept;
uniform vec3 layer_depths;
uniform bool grounded = false;
uniform bool joint_depth = false;
uniform vec2 alpha_window = vec2(0.0, 1.0);
uniform vec2 alpha_feather = vec2(0.0);
uniform vec2 hem_offset = vec2(0.0);
void vertex() {
 vec2 p = vec2(UV.x * part_size.x, -UV.y * part_size.y);
 vec3 weights = vec3(1.0, 0.0, 0.0);
 if (segment_count == 3) {
  float bend1 = smoothstep(-art_points[1].y / part_size.y - 0.065, -art_points[1].y / part_size.y + 0.065, UV.y);
  float bend2 = smoothstep(-art_points[2].y / part_size.y - 0.045, -art_points[2].y / part_size.y + 0.045, UV.y);
  weights = vec3(1.0-bend1, bend1*(1.0-bend2), bend1*bend2);
 }
 vec2 position = vec2(0.0);
 float depth = 0.0;
 for (int i=0; i<3; i++) {
  vec2 offset = p - art_points[i];
  vec2 point = pose_points[i] + basis_x[i]*offset.x + basis_y[i]*offset.y;
  float z = point.y*body_depth_slope + body_depth_intercept;
  if (joint_depth || (grounded && i == 2)) { z = pose_depths[i] + dot(depth_gradients[i], offset); }
  position += point*weights[i];
  depth += (z+layer_depths[i])*weights[i];
 }
 float free_hem = smoothstep(0.12, 1.0, UV.y);
 VERTEX = vec3(position + vec2(hem_offset.x, 0.0)*free_hem, depth + hem_offset.y*free_hem);
}
void fragment() {
 if (UV.y < alpha_window.x || UV.y > alpha_window.y) { discard; }
 vec4 pigment = texture(atlas, region.xy + UV*region.zw);
 if (alpha_feather.x > 0.0) pigment.a *= smoothstep(alpha_window.x, alpha_window.x + alpha_feather.x, UV.y);
 if (alpha_feather.y > 0.0) pigment.a *= 1.0 - smoothstep(alpha_window.y - alpha_feather.y, alpha_window.y, UV.y);
 ALBEDO = pigment.rgb*tint.rgb;
 ALPHA = pigment.a*tint.a;
 ALPHA_SCISSOR_THRESHOLD = 0.15;
}
