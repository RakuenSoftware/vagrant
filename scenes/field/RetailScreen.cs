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
/// frame (the field, or black) stays a weight of each pixel, so the frame is laid over it as the GPU's blending would
/// leave it.
/// </remarks>
public abstract partial class RetailScreen : Control
{
    /// <summary>The frame retail draws into: 320 pixels across, 224 lines.</summary>
    protected const int FrameWidth = 320, FrameHeight = 224;

    /// <summary>The PlayStation's video memory, 1024 by 512 16-bit words.</summary>
    protected readonly ushort[] Vram = new ushort[1024 * 512];

    // The frame's 5-bit channels, and how much (of 255) of what lies behind the frame still shows at each pixel.
    private readonly byte[] _red = new byte[FrameWidth * FrameHeight], _green = new byte[FrameWidth * FrameHeight], _blue = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _behind = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _rgba = new byte[FrameWidth * FrameHeight * 4];
    private Image? _image;
    private ImageTexture? _picture;
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
        Array.Clear(_red);
        Array.Clear(_green);
        Array.Clear(_blue);
        Array.Fill(_behind, black ? (byte)0 : (byte)255);
    }

    /// <summary>Puts a primitive at the head of an ordering-table entry (AddPrim): it is drawn before those already there.</summary>
    protected void Order(int entry, Action primitive) => _order[entry].Insert(0, primitive);

    /// <summary>Draws the ordering table (DrawOTag), its last entry first, and shows the frame.</summary>
    protected void EndFrame()
    {
        for (int entry = _order.Length - 1; entry >= 0; entry--)
            foreach (var primitive in _order[entry]) primitive();
        for (int i = 0; i < _behind.Length; i++)
        {
            _rgba[i * 4] = (byte)(_red[i] << 3 | _red[i] >> 2);
            _rgba[i * 4 + 1] = (byte)(_green[i] << 3 | _green[i] >> 2);
            _rgba[i * 4 + 2] = (byte)(_blue[i] << 3 | _blue[i] >> 2);
            _rgba[i * 4 + 3] = (byte)(255 - _behind[i]);
        }
        if (_image == null)
        {
            _image = Image.CreateFromData(FrameWidth, FrameHeight, false, Image.Format.Rgba8, _rgba);
            _picture = ImageTexture.CreateFromImage(_image);
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.PremultAlpha };
        }
        else
        {
            _image.SetData(FrameWidth, FrameHeight, false, Image.Format.Rgba8, _rgba);
            _picture!.Update(_image);
        }
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
        int i = y * FrameWidth + x;
        if (!semi)
        {
            (_red[i], _green[i], _blue[i], _behind[i]) = ((byte)r, (byte)g, (byte)b, 0);
            return;
        }
        switch (_blend)
        {
            case 0:
                (_red[i], _green[i], _blue[i]) = ((byte)((_red[i] + r) >> 1), (byte)((_green[i] + g) >> 1), (byte)((_blue[i] + b) >> 1));
                _behind[i] >>= 1;
                break;
            case 1:
                (_red[i], _green[i], _blue[i]) = ((byte)Math.Min(31, _red[i] + r), (byte)Math.Min(31, _green[i] + g), (byte)Math.Min(31, _blue[i] + b));
                break;
            case 2:
                (_red[i], _green[i], _blue[i]) = ((byte)Math.Max(0, _red[i] - r), (byte)Math.Max(0, _green[i] - g), (byte)Math.Max(0, _blue[i] - b));
                break;
            default:
                (_red[i], _green[i], _blue[i]) = ((byte)Math.Min(31, _red[i] + (r >> 2)), (byte)Math.Min(31, _green[i] + (g >> 2)), (byte)Math.Min(31, _blue[i] + (b >> 2)));
                break;
        }
    }
}
