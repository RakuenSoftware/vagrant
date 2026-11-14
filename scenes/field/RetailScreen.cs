// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// A screen drawn as retail draws it over the whole window (the HUD, the menus, the boxes): it fills the window, lets
/// the pointer through, and keeps its pictures' pixels sharp.
/// </summary>
/// <remarks>
/// A screen whose parts retail blends into one another (glows, shadows, fades) draws them as the PlayStation's GPU
/// does: it keeps the console's video memory (1024 by 512 words of 5-bit colour and a mask bit), with its pictures
/// where retail loads them, and each frame runs its primitives in ordering-table order into the 320 by 224 frame:
/// the ordering table's last entry first, and in an entry the primitive put in last first (libgpu's ClearOTagR,
/// AddPrim and DrawOTag). Sprites, tiles and flat or Gouraud-shaded, textured or plain triangles and quads are drawn
/// with the GPU's rules: a texel of 0 is not drawn; a texture is modulated by the primitive's colour (128 leaves it
/// as it is); a semi-transparent primitive blends where its texel's top bit is set (everywhere when untextured) by the
/// texture page's mode (half and half, added, subtracted, or a quarter added); polygons are dithered when the draw
/// mode asks, sprites never; a polygon more than 1023 pixels across or 511 down is dropped. What lies behind the
/// frame (the field, or black) is resolved through each pixel's 32-level channel transfer, so saturation between
/// primitives applies to the actual background.
/// </remarks>
public abstract partial class RetailScreen : Control
{
    /// <summary>The frame retail draws into: 320 pixels across, 224 lines.</summary>
    protected const int FrameWidth = 320, FrameHeight = 224;

    /// <summary>The PlayStation's video memory, 1024 by 512 16-bit words.</summary>
    protected readonly ushort[] Vram = new ushort[1024 * 512];

    // For each possible 5-bit background level, the GPU's resulting channel value. Keeping the transfer for all
    // 32 levels preserves saturation between primitives, including subtract-then-add over the live field.
    private byte[] _pixels = Array.Empty<byte>();
    private static byte[]? _identity;
    private int _levels;
    private Image? _image;
    private ImageTexture? _picture;
    private BackBufferCopy? _background;
    private List<Action>[] _order = Array.Empty<List<Action>>();

    // The GPU's draw mode: the texture page (its corner in video memory, blend and depth) and dithering.
    private int _pageX, _pageY, _blend, _depth;
    private bool _dither;
    // The draw area (DR_AREA): the pixels drawn to, from the left and top to the right and bottom (exclusive).
    private int _areaLeft, _areaTop, _areaRight = FrameWidth, _areaBottom = FrameHeight;

    /// <summary>The GPU's dither matrix, added to 8-bit colour before it is cut to five bits.</summary>
    private static readonly int[,] Dither = { { -4, 0, -3, 1 }, { 2, -2, 3, -1 }, { -3, 1, -4, 0 }, { 3, -1, 2, -2 } };

    /// <summary>A polygon's corner: its place on the frame, its texture coordinate and its colour (0 to 255 each).</summary>
    protected readonly record struct Corner(int X, int Y, int U, int V, int R, int G, int B);

    protected RetailScreen()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        AnchorRight = 1;
        AnchorBottom = 1;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    /// <summary>Copies a rectangle of 16-bit words to video memory (libgpu's LoadImage).</summary>
    protected void LoadImage(int x, int y, int width, int height, byte[] data, int at)
    {
        for (int row = 0; row < height; row++)
            for (int column = 0; column < width; column++)
                Vram[((y + row) & 511) * 1024 + ((x + column) & 1023)] = BitConverter.ToUInt16(data, at + (row * width + column) * 2);
    }

    /// <summary>Loads a TIM at <paramref name="at"/> of <paramref name="data"/> where its header places it: its palettes, then its picture.</summary>
    protected void LoadTim(byte[] data, int at)
    {
        int flags = BitConverter.ToInt32(data, at + 4), block = at + 8;
        if ((flags & 8) != 0)
        {
            LoadImage(BitConverter.ToUInt16(data, block + 4), BitConverter.ToUInt16(data, block + 6), BitConverter.ToUInt16(data, block + 8), BitConverter.ToUInt16(data, block + 10), data, block + 12);
            block += BitConverter.ToInt32(data, block);
        }
        LoadImage(BitConverter.ToUInt16(data, block + 4), BitConverter.ToUInt16(data, block + 6), BitConverter.ToUInt16(data, block + 8), BitConverter.ToUInt16(data, block + 10), data, block + 12);
    }

    /// <summary>
    /// Starts a frame with an ordering table of <paramref name="entries"/> and the draw environment's mode (its
    /// texture page and dithering): the frame cleared to black (the environment's background, <paramref name="black"/>)
    /// or left clear over what lies behind it.
    /// </summary>
    protected void BeginFrame(int entries, bool black, int page, bool dither)
    {
        DrawMode(page, dither);
        DrawArea(0, 0, FrameWidth, FrameHeight);
        if (_order.Length != entries)
        {
            _order = new List<Action>[entries];
            for (int i = 0; i < entries; i++) _order[i] = new List<Action>();
        }
        foreach (var entry in _order) entry.Clear();
        _levels = black ? 1 : 32;
        int size = FrameWidth * FrameHeight * _levels * 4;
        if (_pixels.Length != size) _pixels = new byte[size];
        if (black) Array.Clear(_pixels);
        else
        {
            if (_identity == null)
            {
                _identity = new byte[FrameWidth * FrameHeight * 32 * 4];
                for (int level = 0; level < 32; level++)
                    for (int pixel = 0; pixel < FrameWidth * FrameHeight; pixel++)
                    {
                        int at = (level * FrameWidth * FrameHeight + pixel) * 4;
                        _identity[at] = _identity[at + 1] = _identity[at + 2] = (byte)level;
                    }
            }
            Buffer.BlockCopy(_identity, 0, _pixels, 0, size);
        }
    }

    /// <summary>Puts a primitive at the head of an ordering-table entry (AddPrim): it is drawn before those already there.</summary>
    protected void Order(int entry, Action primitive) => _order[entry].Insert(0, primitive);

    /// <summary>Draws the ordering table (DrawOTag), its last entry first, and shows the frame.</summary>
    protected void EndFrame()
    {
        for (int entry = _order.Length - 1; entry >= 0; entry--)
            foreach (var primitive in _order[entry]) primitive();
        int height = FrameHeight * _levels;
        if (_image == null || _image.GetHeight() != height)
        {
            _image?.Dispose();
            _picture?.Dispose();
            _image = Image.CreateFromData(FrameWidth, height, false, Image.Format.Rgba8, _pixels);
            _picture = ImageTexture.CreateFromImage(_image);
        }
        else
        {
            _image.SetData(FrameWidth, height, false, Image.Format.Rgba8, _pixels);
            _picture!.Update(_image);
        }
        if (Material is not ShaderMaterial)
        {
            Material = new ShaderMaterial { Shader = new Shader { Code = CompositeShader } };
            _background = new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Disabled, ShowBehindParent = true };
            AddChild(_background);
        }
        var material = (ShaderMaterial)Material;
        material.SetShaderParameter("transfer", _picture);
        material.SetShaderParameter("levels", _levels);
        material.SetShaderParameter("linear", GetViewport().UseHdr2D);
        _background!.CopyMode = _levels == 1 ? BackBufferCopy.CopyModeEnum.Disabled : BackBufferCopy.CopyModeEnum.Viewport;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_picture == null) return;
        var (unit, offset) = FieldView.Pin(Size, 1);
        DrawTextureRect(_picture, new Rect2(offset, new Vector2(FrameWidth, FrameHeight) * unit), false);
    }

    /// <summary>Sets the draw mode (a DR_TPAGE primitive): the texture page and whether polygons are dithered.</summary>
    protected void DrawMode(int page, bool dither)
    {
        Page(page);
        _dither = dither;
    }

    /// <summary>Sets the draw area (a DR_AREA primitive): only pixels within the rectangle are drawn.</summary>
    protected void DrawArea(int x, int y, int width, int height)
    {
        (_areaLeft, _areaTop) = (Math.Max(0, x), Math.Max(0, y));
        (_areaRight, _areaBottom) = (Math.Min(FrameWidth, x + width), Math.Min(FrameHeight, y + height));
    }

    private void Page(int page)
    {
        _pageX = (page & 15) * 64;
        _pageY = (page & 16) * 16;
        _blend = (page >> 5) & 3;
        _depth = (page >> 7) & 3;
    }

    /// <summary>A sprite (SPRT) of the current texture page: its texels from (u, v) on, modulated by (r, g, b).</summary>
    protected void Sprite(int x, int y, int width, int height, int u, int v, int clut, int r, int g, int b, bool semi)
    {
        for (int row = 0; row < height; row++)
        {
            int py = y + row;
            if (py < 0 || py >= FrameHeight) continue;
            for (int column = 0; column < width; column++)
            {
                int px = x + column;
                if (px < 0 || px >= FrameWidth) continue;
                ushort texel = Texel(u + column, v + row, clut);
                if (texel == 0) continue;
                Plot(px, py, Math.Min(31, (texel & 31) * r >> 7), Math.Min(31, (texel >> 5 & 31) * g >> 7), Math.Min(31, (texel >> 10 & 31) * b >> 7), semi && (texel & 0x8000) != 0);
            }
        }
    }

    /// <summary>A plain rectangle (TILE) of one colour.</summary>
    protected void Tile(int x, int y, int width, int height, int r, int g, int b, bool semi)
    {
        for (int py = Math.Max(0, y); py < Math.Min(FrameHeight, y + height); py++)
            for (int px = Math.Max(0, x); px < Math.Min(FrameWidth, x + width); px++)
                Plot(px, py, r >> 3, g >> 3, b >> 3, semi);
    }

    /// <summary>
    /// A triangle or quad (its corners 0, 1, 2 and then 1, 2, 3): textured from the texture page <paramref name="page"/>
    /// (which becomes the draw mode's) through <paramref name="clut"/>, or plain (a page below 0); Gouraud-shaded by its
    /// corners' colours or flat in the first's.
    /// </summary>
    protected void Polygon(ReadOnlySpan<Corner> corners, int page, int clut, bool gouraud, bool semi)
    {
        if (page >= 0) Page(page);
        Triangle(corners[0], corners[1], corners[2], page >= 0, clut, gouraud, semi, corners[0]);
        if (corners.Length == 4) Triangle(corners[1], corners[2], corners[3], page >= 0, clut, gouraud, semi, corners[0]);
    }

    private void Triangle(Corner a, Corner b, Corner c, bool textured, int clut, bool gouraud, bool semi, Corner flat)
    {
        int left = Math.Min(a.X, Math.Min(b.X, c.X)), right = Math.Max(a.X, Math.Max(b.X, c.X));
        int top = Math.Min(a.Y, Math.Min(b.Y, c.Y)), bottom = Math.Max(a.Y, Math.Max(b.Y, c.Y));
        if (right - left > 1023 || bottom - top > 511) return;
        long area = (long)(b.X - a.X) * (c.Y - a.Y) - (long)(c.X - a.X) * (b.Y - a.Y);
        if (area == 0) return;
        if (area < 0) { (b, c) = (c, b); area = -area; }
        bool dither = _dither && (gouraud || textured);
        for (int y = Math.Max(0, top); y <= Math.Min(FrameHeight - 1, bottom); y++)
            for (int x = Math.Max(0, left); x <= Math.Min(FrameWidth - 1, right); x++)
            {
                // Each corner's weight is its opposite edge's; a pixel on an edge is the triangle's only on its top or left edges.
                long wa = Edge(b, c, x, y), wb = Edge(c, a, x, y), wc = Edge(a, b, x, y);
                if (!Inside(wa, b, c) || !Inside(wb, c, a) || !Inside(wc, a, b)) continue;
                int r = flat.R, g = flat.G, bl = flat.B;
                if (gouraud)
                {
                    r = (int)((a.R * wa + b.R * wb + c.R * wc) / area);
                    g = (int)((a.G * wa + b.G * wb + c.G * wc) / area);
                    bl = (int)((a.B * wa + b.B * wb + c.B * wc) / area);
                }
                int d = dither ? Dither[y & 3, x & 3] : 0;
                if (!textured)
                {
                    Plot(x, y, Cut(r + d), Cut(g + d), Cut(bl + d), semi);
                    continue;
                }
                ushort texel = Texel((int)((a.U * wa + b.U * wb + c.U * wc) / area), (int)((a.V * wa + b.V * wb + c.V * wc) / area), clut);
                if (texel == 0) continue;
                if (dither) Plot(x, y, Cut(((texel & 31) * r >> 4) + d), Cut(((texel >> 5 & 31) * g >> 4) + d), Cut(((texel >> 10 & 31) * bl >> 4) + d), semi && (texel & 0x8000) != 0);
                else Plot(x, y, Math.Min(31, (texel & 31) * r >> 7), Math.Min(31, (texel >> 5 & 31) * g >> 7), Math.Min(31, (texel >> 10 & 31) * bl >> 7), semi && (texel & 0x8000) != 0);
            }

        static long Edge(Corner p, Corner q, int x, int y) => (long)(q.X - p.X) * (y - p.Y) - (long)(q.Y - p.Y) * (x - p.X);
        static bool Inside(long weight, Corner p, Corner q) => weight > 0 || weight == 0 && (q.Y < p.Y || q.Y == p.Y && q.X > p.X);
        static int Cut(int value) => Math.Clamp(value, 0, 255) >> 3;
    }

    /// <summary>The texel at (u, v) of the current texture page, through the palette at <paramref name="clut"/> (x / 16 in its low six bits, y above).</summary>
    private ushort Texel(int u, int v, int clut)
    {
        u &= 255;
        int row = ((_pageY + (v & 255)) & 511) * 1024, palette = (clut >> 6 & 511) * 1024 + (clut & 63) * 16;
        return _depth switch
        {
            0 => Vram[palette + (Vram[row + ((_pageX + (u >> 2)) & 1023)] >> ((u & 3) * 4) & 15)],
            1 => Vram[palette + (Vram[row + ((_pageX + (u >> 1)) & 1023)] >> ((u & 1) * 8) & 255)],
            _ => Vram[row + ((_pageX + u) & 1023)],
        };
    }

    /// <summary>Writes a pixel of 5-bit colour, blended by the draw mode when <paramref name="semi"/>.</summary>
    private void Plot(int x, int y, int r, int g, int b, bool semi)
    {
        if (x < _areaLeft || x >= _areaRight || y < _areaTop || y >= _areaBottom) return;
        for (int level = 0; level < _levels; level++)
        {
            int i = ((level * FrameHeight + y) * FrameWidth + x) * 4;
            if (!semi)
            {
                (_pixels[i], _pixels[i + 1], _pixels[i + 2]) = ((byte)r, (byte)g, (byte)b);
                continue;
            }
            switch (_blend)
            {
                case 0:
                    (_pixels[i], _pixels[i + 1], _pixels[i + 2]) = ((byte)((_pixels[i] + r) >> 1), (byte)((_pixels[i + 1] + g) >> 1), (byte)((_pixels[i + 2] + b) >> 1));
                    break;
                case 1:
                    (_pixels[i], _pixels[i + 1], _pixels[i + 2]) = ((byte)Math.Min(31, _pixels[i] + r), (byte)Math.Min(31, _pixels[i + 1] + g), (byte)Math.Min(31, _pixels[i + 2] + b));
                    break;
                case 2:
                    (_pixels[i], _pixels[i + 1], _pixels[i + 2]) = ((byte)Math.Max(0, _pixels[i] - r), (byte)Math.Max(0, _pixels[i + 1] - g), (byte)Math.Max(0, _pixels[i + 2] - b));
                    break;
                default:
                    (_pixels[i], _pixels[i + 1], _pixels[i + 2]) = ((byte)Math.Min(31, _pixels[i] + (r >> 2)), (byte)Math.Min(31, _pixels[i + 1] + (g >> 2)), (byte)Math.Min(31, _pixels[i + 2] + (b >> 2)));
                    break;
            }
        }
    }

    private const string CompositeShader = @"shader_type canvas_item;
render_mode unshaded, blend_disabled;
uniform sampler2D screen : hint_screen_texture, repeat_disable, filter_nearest;
uniform sampler2D transfer : repeat_disable, filter_nearest;
uniform int levels = 1;
uniform bool linear = false;
void fragment() {
    ivec2 pixel = ivec2(clamp(floor(UV * vec2(320.0, 224.0)), vec2(0.0), vec2(319.0, 223.0)));
    ivec3 background = ivec3(0);
    if (levels != 1) {
        vec3 c = textureLod(screen, SCREEN_UV, 0.0).rgb;
        if (linear) c = mix(c * 12.92, 1.055 * pow(max(c, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
        background = ivec3(floor(clamp(c, vec3(0.0), vec3(1.0)) * 255.0 / 8.0));
    }
    vec3 result = vec3(texelFetch(transfer, pixel + ivec2(0, background.r * 224), 0).r,
                       texelFetch(transfer, pixel + ivec2(0, background.g * 224), 0).g,
                       texelFetch(transfer, pixel + ivec2(0, background.b * 224), 0).b);
    result = floor(result * 255.0 + 0.5);
    result = (result * 8.0 + floor(result / 4.0)) / 255.0;
    if (linear) result = mix(result / 12.92, pow((result + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), result));
    COLOR = vec4(result, 1.0);
}";
}
