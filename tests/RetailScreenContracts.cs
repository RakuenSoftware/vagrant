// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Checks GPU blending against a known background through the actual LDR and HDR canvas compositor.</summary>
public partial class RetailScreenContracts : RetailScreen
{
    public override async void _Ready()
    {
        try
        {
            if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("run on an isolated rendering display");
            GetNode<Node>("/root/DisplaySettings").ProcessMode = ProcessModeEnum.Disabled;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var background = new ColorRect { Size = GetViewportRect().Size, Material = new ShaderMaterial { Shader = new Shader { Code = @"shader_type canvas_item;
render_mode unshaded;
uniform bool linear = false;
void fragment() {
    vec3 c = vec3(41.0, 82.0, 123.0) / 255.0;
    if (linear) c = mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
    COLOR = vec4(c, 1.0);
}" } } };
            GetParent().AddChild(background);
            GetParent().MoveChild(background, GetIndex());
            var expected = new[] { (5, 10, 15), (0, 6, 13), (8, 10, 15), (21, 18, 19), (23, 27, 29), (8, 6, 6), (12, 13, 16), (8, 4, 2) };
            foreach (bool hdr in new[] { false, true })
            {
                GetViewport().UseHdr2D = hdr;
                ((ShaderMaterial)background.Material).SetShaderParameter("linear", hdr);
                BeginFrame(1, false, 0, false);
                Order(0, () =>
                {
                    DrawMode(2 << 5, false); Tile(40, 0, 40, 224, 64, 32, 16, true);
                    Tile(80, 0, 40, 224, 64, 32, 16, true);
                    DrawMode(1 << 5, false); Tile(80, 0, 40, 224, 64, 32, 16, true);
                    Tile(120, 0, 40, 224, 64, 32, 16, true); Tile(120, 0, 40, 224, 64, 32, 16, true);
                    Tile(160, 0, 40, 224, 248, 248, 248, true);
                    DrawMode(2 << 5, false); Tile(160, 0, 40, 224, 64, 32, 16, true);
                    // Odd five-bit foreground values distinguish rounding the sum from rounding each half separately.
                    DrawMode(0, false); Tile(200, 0, 40, 224, 72, 40, 24, true); Tile(200, 0, 40, 224, 72, 40, 24, true);
                    DrawMode(3 << 5, false); Tile(240, 0, 40, 224, 248, 120, 56, true);
                    Tile(280, 0, 40, 224, 64, 32, 16, false);
                });
                EndFrame();
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                var (scale, origin) = FieldView.Pin(GetViewportRect().Size, 1);
                for (int i = 0; i < expected.Length; i++)
                {
                    var point = (origin + new Vector2(i * 40 + 20, 100) * scale) * (Vector2)image.GetSize() / GetViewportRect().Size;
                    var color = image.GetPixel((int)point.X, (int)point.Y);
                    if (hdr) color = color.LinearToSrgb();
                    var actual = ((int)MathF.Round(color.R * 255), (int)MathF.Round(color.G * 255), (int)MathF.Round(color.B * 255));
                    var (r, g, b) = expected[i];
                    var bytes = (r * 8 + r / 4, g * 8 + g / 4, b * 8 + b / 4);
                    if (Math.Abs(actual.Item1 - bytes.Item1) > 1 || Math.Abs(actual.Item2 - bytes.Item2) > 1 || Math.Abs(actual.Item3 - bytes.Item3) > 1)
                        throw new InvalidOperationException($"HDR {hdr}, strip {i}: {actual}, expected {bytes}");
                }
            }
            GD.Print("RETAIL_SCREEN_CONTRACTS_OK: background, subtraction, subtract/add, saturation, half, quarter and opaque in LDR/HDR");
            GetTree().Quit(0);
        }
        catch (Exception error) { GD.PrintErr("RETAIL_SCREEN_CONTRACTS_FAILED: " + error.Message); GetTree().Quit(1); }
    }
}
