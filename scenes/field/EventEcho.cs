// SPDX-License-Identifier: AGPL-3.0-only
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The retail frame-buffer echo (opcodes e1 and e4-e7): the frame is drawn again over itself, scaled about the
/// screen centre, offset, tinted and blended with a PlayStation semi-transparency mode. The result is a zoom blur
/// or after-image. Scale, tint and offset ease on the 30 Hz event clock.
/// The retail echo reads the previous frame (the other display buffer), echo and all, so its echoes accumulate into
/// a trail: <c>e7 00</c> with <c>e1 01</c> alone is a motion blur. Each drawn frame is copied, so the next one reads it.
/// </summary>
public partial class EventEcho : ColorRect
{
    private const string Shader = @"
shader_type canvas_item;
uniform sampler2D screen : hint_screen_texture, filter_nearest;
uniform sampler2D previous : filter_nearest;
uniform bool trail = false;
uniform vec2 scale = vec2(1.0);
uniform vec2 offset = vec2(0.0);
uniform vec3 tint = vec3(1.0);
uniform int mode = 0;
void fragment() {
    vec3 back = texture(screen, SCREEN_UV).rgb;
    vec2 source = (SCREEN_UV - 0.5 - offset) / scale + 0.5;
    vec3 echo = vec3(0.0);
    bool inside = all(greaterThanEqual(source, vec2(0.0))) && all(lessThanEqual(source, vec2(1.0)));
    if (inside) echo = (trail ? texture(previous, source).rgb : texture(screen, source).rgb) * tint;
    vec3 result = !inside ? back : mode == 0 ? (back + echo) * 0.5 : mode == 1 ? back + echo : mode == 2 ? back - echo : back + echo * 0.25;
    COLOR = vec4(clamp(result, 0.0, 1.0), 1.0);
}";

    /// <summary>One eased value: scale (x, y), tint (r, g, b, 128 neutral) or offset (dx, dy in screen pixels).</summary>
    private sealed class Channel
    {
        public Vector3 Value, Start, End;
        public int Tick, Frames, Ease;

        public void Set(Vector3 value, int ease, int frames)
        {
            if (frames <= 0) { Value = value; Tick = Frames = 0; return; }
            Start = Value;
            End = value;
            Ease = ease;
            Tick = 0;
            Frames = frames;
        }

        public void Step()
        {
            if (Tick >= Frames) return;
            Tick++;
            Value = Start.Lerp(End, EventCamera.Ease(Ease, Tick / (float)Frames));
        }
    }

    private readonly Channel _scale = new() { Value = Vector3.One };
    private readonly Channel _tint = new() { Value = new Vector3(128, 128, 128) };
    private readonly Channel _offset = new();
    private ShaderMaterial _material = null!;
    private int _mode;
    private int _stopping = -1;
    // Whether the echo reads the previous frame (a trail) or this one (bf's presets: a soft, solid picture).
    private bool _reads = true;
    private Texture2Drd? _previous;
    private Rid _previousRd;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AnchorRight = 1;
        AnchorBottom = 1;
        _material = new ShaderMaterial { Shader = new Shader { Code = Shader } };
        Material = _material;
        Visible = false;
        RenderingServer.FramePostDraw += Keep;
    }

    public override void _ExitTree()
    {
        RenderingServer.FramePostDraw -= Keep;
        _material.SetShaderParameter("trail", false);
        _material.SetShaderParameter("previous", default);
        _previous = null;
        if (_previousRd.IsValid) RenderingServer.GetRenderingDevice().FreeRid(_previousRd);
        _previousRd = default;
    }

    /// <summary>Keeps the frame just drawn for the next frame's echo, while the echo is on.</summary>
    private void Keep()
    {
        var device = RenderingServer.GetRenderingDevice();
        if (!Visible || device == null || !IsInsideTree()) return;
        var frame = RenderingServer.TextureGetRdTexture(GetViewport().GetTexture().GetRid());
        if (!frame.IsValid) return;
        var format = device.TextureGetFormat(frame);
        if (_previous == null || _previous.GetWidth() != format.Width || _previous.GetHeight() != format.Height)
        {
            if (_previousRd.IsValid) device.FreeRid(_previousRd);
            var kept = new RDTextureFormat
            {
                Width = format.Width, Height = format.Height, Format = format.Format, TextureType = RenderingDevice.TextureType.Type2D,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
            };
            _previousRd = device.TextureCreate(kept, new RDTextureView());
            _previous = new Texture2Drd { TextureRdRid = _previousRd };
            _material.SetShaderParameter("previous", _previous);
            // The first frame has no predecessor: it echoes itself.
            _material.SetShaderParameter("trail", false);
        }
        else _material.SetShaderParameter("trail", _reads);
        device.TextureCopy(frame, _previousRd, Vector3.Zero, Vector3.Zero, new Vector3(format.Width, format.Height, 1), 0, 0, 0, 0);
    }

    /// <summary>e1: starts the echo, or stops it two frames later.</summary>
    public void Enable(bool on)
    {
        if (on && !Visible) _material?.SetShaderParameter("trail", false);
        if (on) { Visible = true; _stopping = -1; _reads = true; }
        else if (Visible) _stopping = 2;
    }

    public void Zoom(float x, float y, int ease, int frames) => _scale.Set(new Vector3(x, y, 0), ease, frames);
    public void Tint(int r, int g, int b, int ease, int frames) => _tint.Set(new Vector3(r, g, b), ease, frames);
    public void Offset(int dx, int dy, int ease, int frames) => _offset.Set(new Vector3(dx, dy, 0), ease, frames);
    public void Blend(int mode) => _mode = mode & 3;

    /// <summary>
    /// Retail bf: 0 resets the echo; otherwise this frame drawn again over itself, blended half and half, two pixels low
    /// and slightly bright. It leaves no trail: in Episode 3 (7:45, MAP011) a knight rising before the camera stays solid,
    /// where feeding the frames back left the room showing through him.
    /// </summary>
    public void Preset(int value)
    {
        Clear();
        if (value == 0) return;
        Enable(true);
        _reads = false;
        Offset(0, 2, 0, 0);
        Tint(144, 144, 144, 0, 0);
    }

    public void Clear()
    {
        Visible = false;
        _material?.SetShaderParameter("trail", false);
        _stopping = -1;
        _mode = 0;
        _scale.Set(Vector3.One, 0, 0);
        _tint.Set(new Vector3(128, 128, 128), 0, 0);
        _offset.Set(Vector3.Zero, 0, 0);
    }

    /// <summary>Advances the eased values by one event frame.</summary>
    public void Step()
    {
        _scale.Step();
        _tint.Step();
        _offset.Step();
        if (_stopping > 0 && --_stopping == 0) { Visible = false; _stopping = -1; }
        _material.SetShaderParameter("scale", new Vector2(_scale.Value.X, _scale.Value.Y));
        _material.SetShaderParameter("tint", _tint.Value / 128f);
        _material.SetShaderParameter("offset", new Vector2(_offset.Value.X / 320f, _offset.Value.Y / 240f));
        _material.SetShaderParameter("mode", _mode);
    }
}
