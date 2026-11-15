// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Wol.Engine.Imaging;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// Converts the retail title overlay (TITLE/TITLE.PRG) into ordinary images and a layout description.
/// The overlay's offsets are only meaningful for the verified retail file, which the retail-disc catalog catalogues.
/// No PlayStation rendering runs in the game; the presentation draws these images natively.
/// </summary>
public sealed class TitleConversion
{
    // Fixed locations inside the verified retail US TITLE.PRG.
    private const int PublisherOffset = 0xA70C;     // 16-colour CLUT followed by a 256x48 4-bit image.
    private const int DeveloperOffset = 0xBF2C;     // 128x13 4-bit image followed by two CLUTs.
    private const int PaletteTableOffset = 0xC2B4;  // Four 16-entry CLUTs: menu text dark/lit, glow blue/gold.
    private const int TextUvOffset = 0xC334;        // Ten packed GPU UV/CLUT words for the menu words.
    private const int TextPageOffset = 0xC35C;      // Ten GPU texture-page commands for the menu words.
    private const int GlowUvOffset = 0xC384;
    private const int GlowPageOffset = 0xC3AC;
    private const int GlowSizeOffset = 0xC3D4;
    private const int GlowOriginOffset = 0xC3FC;
    private const int BackgroundOffset = 0xE2D4;    // Run-length coded 544x576 VRAM upload.
    private const int CopyrightOffset = 0x449A8;    // 180x24 direct-colour image.
    public const int MenuEntries = 10;
    public const int NewGameEntry = 0, SettingsEntry = 10;
    public const int MenuWordWidth = 128;
    public const int MenuWordHeight = 32;

    private readonly byte[] _overlay;
    private readonly ushort[] _vram = new ushort[1024 * 512];

    public TitleConversion(byte[] overlay)
    {
        if (overlay.Length < CopyrightOffset + 180 * 24 * 2) throw new InvalidDataException("The title overlay is truncated.");
        _overlay = overlay;
        // The retail title decodes one 544x576 block, uploading its first 512 rows at (480,0) and its last 64 at (0,448).
        int words = (CopyrightOffset - BackgroundOffset) / 4;
        var block = new ushort[544 * 576];
        int written = 0;
        for (int index = 0; index < words;)
        {
            uint control = BitConverter.ToUInt32(_overlay, BackgroundOffset + index++ * 4);
            written += (int)(control & 0xFFFF) * 2;
            for (uint literal = control >> 16; literal != 0; literal--)
            {
                if (index >= words || written + 2 > block.Length) throw new InvalidDataException("The title background stream is malformed.");
                uint pair = BitConverter.ToUInt32(_overlay, BackgroundOffset + index++ * 4);
                block[written++] = (ushort)pair;
                block[written++] = (ushort)(pair >> 16);
            }
        }
        if (written != block.Length) throw new InvalidDataException("The title background stream decoded to an unexpected size.");
        for (int y = 0; y < 512; y++) Array.Copy(block, y * 544, _vram, y * 1024 + 480, 544);
        for (int y = 0; y < 64; y++) Array.Copy(block, (512 + y) * 544, _vram, (448 + y) * 1024, 544);
    }


    /// <summary>Converts a 15-bit PlayStation colour. Colour 0x0000 is the hardware's transparent texel.</summary>
    public static Rgba32 ToColor(ushort value, bool transparentBlack)
    {
        int r = value & 31, g = (value >> 5) & 31, b = (value >> 10) & 31;
        byte alpha = transparentBlack && value == 0 ? (byte)0 : (byte)255;
        return new Rgba32((byte)((r << 3) | (r >> 2)), (byte)((g << 3) | (g >> 2)), (byte)((b << 3) | (b >> 2)), alpha);
    }

    /// <summary>
    /// Writes a PS1 colour (0 transparent) as four bytes of RGBA, scaled by <paramref name="gain"/>. On semi-transparent
    /// faces only texels with the mask bit (0x8000) blend, as on the rooms' polygons: <paramref name="mask"/> 1 gives those
    /// half alpha and leaves the rest opaque, 2 keeps only the unmasked texels and 3 only the masked ones (the two draws
    /// of an additive or subtractive face); 0 ignores the bit.
    /// </summary>
    public static void WriteTexel(byte[] data, int at, ushort colour, int mask, float gain)
    {
        bool masked = (colour & 0x8000) != 0;
        var shown = ToColor(colour, false);
        data[at] = (byte)(shown.Red * gain);
        data[at + 1] = (byte)(shown.Green * gain);
        data[at + 2] = (byte)(shown.Blue * gain);
        data[at + 3] = colour == 0 ? (byte)0 : mask switch { 1 => masked ? (byte)128 : (byte)255, 2 => masked ? (byte)0 : (byte)255, 3 => masked ? (byte)255 : (byte)0, _ => (byte)255 };
    }

    private RasterImage DirectImage(int x, int y, int width, int height)
    {
        var image = new RasterImage(width, height);
        for (int row = 0; row < height; row++)
            for (int column = 0; column < width; column++)
                image.SetPixel(column, row, ToColor(_vram[(y + row) * 1024 + x + column], false));
        return image;
    }

    private RasterImage IndexedImage(int offset, int width, int height, int paletteOffset)
    {
        var image = new RasterImage(width, height);
        for (int row = 0; row < height; row++)
            for (int column = 0; column < width; column++)
            {
                int packed = _overlay[offset + (row * width + column) / 2];
                int index = (column & 1) == 0 ? packed & 15 : packed >> 4;
                image.SetPixel(column, row, ToColor(BitConverter.ToUInt16(_overlay, paletteOffset + index * 2), true));
            }
        return image;
    }

    /// <summary>Reads one 4-bit texel from emulated VRAM for a texture page and UV origin.</summary>
    private int Texel(int pageX, int pageY, int u, int v)
    {
        ushort word = _vram[(pageY + v) * 1024 + pageX + u / 4];
        return (word >> ((u & 3) * 4)) & 15;
    }

    private static (int X, int Y) Page(uint command)
    {
        if ((command >> 24) != 0xE1 || ((command >> 7) & 3) != 0) throw new InvalidDataException("A title texture page is not a 4-bit page.");
        return ((int)(command & 15) * 64, (int)((command >> 4) & 1) * 256);
    }

    private static (int U, int V, int ClutX, int ClutY) Uv(uint word) =>
        ((int)(word & 0xFF), (int)((word >> 8) & 0xFF), (int)((word >> 16) & 0x3F) * 16, (int)((word >> 22) & 0x1FF));

    /// <summary>
    /// Composes the added Settings word in the title's small-capital lettering. The capital S, and the small
    /// E, T, I and N, are the disc's own glyphs. The title has no small S or G, so these two are the disc's
    /// capital S and G reduced to small-capital height. Columns are those of the verified retail words.
    /// </summary>
    private static RasterImage ComposeSettings(List<RasterImage> words)
    {
        RasterImage newGame = words[0], continueWord = words[1], sound = words[3];
        // Small capitals in CONTINUE: T, I, N, E. Capitals: S in SOUND, G in NEW GAME.
        (RasterImage Source, int Start, int End) capitalS = (sound, 38, 50), capitalG = (newGame, 60, 72);
        (RasterImage Source, int Start, int End) t = (continueWord, 60, 68), i = (continueWord, 68, 73), n = (continueWord, 73, 83), e = (continueWord, 93, 103);
        (int Top, int Bottom) Rows(RasterImage source, int start, int end)
        {
            int top = MenuWordHeight, bottom = -1;
            for (int y = 0; y < MenuWordHeight; y++)
                for (int x = start; x < end; x++)
                    if (source.Pixel(x, y).Red > 0) { top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            return (top, bottom);
        }
        int[,] Crop((RasterImage Source, int Start, int End) glyph)
        {
            var result = new int[glyph.End - glyph.Start, MenuWordHeight];
            for (int x = glyph.Start; x < glyph.End; x++)
                for (int y = 0; y < MenuWordHeight; y++)
                    result[x - glyph.Start, y] = glyph.Source.Pixel(x, y).Red;
            return result;
        }
        var small = Rows(continueWord, e.Start, e.End);
        int[,] Reduce((RasterImage Source, int Start, int End) glyph)
        {
            // Area-average the capital's palette indices (which rise with brightness) onto small-capital height,
            // bottom-aligned to the small capitals' baseline.
            var (top, bottom) = Rows(glyph.Source, glyph.Start, glyph.End);
            double scale = (small.Bottom - small.Top + 1) / (double)(bottom - top + 1);
            int width = Math.Max(1, (int)Math.Round((glyph.End - glyph.Start) * scale));
            var result = new int[width, MenuWordHeight];
            for (int x = 0; x < width; x++)
                for (int y = small.Top; y <= small.Bottom; y++)
                {
                    double x0 = glyph.Start + x / scale, x1 = glyph.Start + (x + 1) / scale;
                    double y0 = top + (y - small.Top) / scale, y1 = top + (y - small.Top + 1) / scale;
                    double sum = 0, area = 0;
                    for (int sx = (int)Math.Floor(x0); sx < Math.Ceiling(x1); sx++)
                        for (int sy = (int)Math.Floor(y0); sy < Math.Ceiling(y1); sy++)
                        {
                            double overlap = (Math.Min(sx + 1, x1) - Math.Max(sx, x0)) * (Math.Min(sy + 1, y1) - Math.Max(sy, y0));
                            if (overlap <= 0 || sx >= glyph.End || sy >= MenuWordHeight) continue;
                            sum += glyph.Source.Pixel(sx, sy).Red * overlap;
                            area += overlap;
                        }
                    result[x, y] = area > 0 ? Math.Clamp((int)Math.Round(sum / area), 0, 15) : 0;
                }
            return result;
        }
        // The reduced letters lose their own spacing, so each is set one column apart from its neighbour.
        var letters = new[] { (Crop(capitalS), 0), (Crop(e), 0), (Crop(t), 0), (Crop(t), 0), (Crop(i), 0), (Crop(n), 0), (Reduce(capitalG), 1), (Reduce(capitalS), 1) };
        int total = 0;
        foreach (var (letter, spacing) in letters) total += letter.GetLength(0) + spacing;
        var word = new RasterImage(MenuWordWidth, MenuWordHeight, RasterFormat.Luminance8);
        int column = (MenuWordWidth - total) / 2;
        foreach (var (letter, spacing) in letters)
        {
            column += spacing;
            for (int x = 0; x < letter.GetLength(0); x++)
                for (int y = 0; y < MenuWordHeight; y++)
                {
                    if (letter[x, y] == 0) continue;
                    int existing = word.Pixel(column + x, y).Red;
                    word.SetPixel(column + x, y, new Rgba32((byte)Math.Max(existing, letter[x, y]), 0, 0));
                }
            column += letter.GetLength(0);
        }
        return word;
    }

    // The executable's "NOW LOADING..." picture (SLUS_010.40): its width and height, then its direct-colour texels.
    private const int LoadingOffset = 0x39950;

    /// <summary>
    /// The picture retail shows while it loads, "NOW LOADING..." in glowing blue on black, from the verified
    /// executable (SLUS_010.40 at 0x39950: 126 by 20); it stands in the middle of the 320 by 240 screen.
    /// </summary>
    public static RasterImage Loading(byte[] executable)
    {
        int width = BitConverter.ToUInt16(executable, LoadingOffset), height = BitConverter.ToUInt16(executable, LoadingOffset + 2);
        if (width == 0 || height == 0 || LoadingOffset + 4 + width * height * 2 > executable.Length) throw new InvalidDataException("The loading picture is missing from the executable.");
        var image = new RasterImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image.SetPixel(x, y, ToColor(BitConverter.ToUInt16(executable, LoadingOffset + 4 + (y * width + x) * 2), true));
        return image;
    }

    /// <summary>Makes every title image, among <paramref name="images"/>, and returns the layout document.</summary>
    public JsonObject Export(IDictionary<string, RasterImage> images)
    {
        void Save(RasterImage image, string name) => images[name.Replace(".png", "")] = image;

        Save(IndexedImage(PublisherOffset + 32, 256, 48, PublisherOffset), "publisher.png");
        Save(IndexedImage(DeveloperOffset, 128, 13, DeveloperOffset + 128 * 13 / 2), "developer.png");
        // The visible title is the interlaced 512x448 display area starting at VRAM (512,16).
        Save(DirectImage(512, 16, 512, 448), "title_background.png");
        // The menu panel is two 160x64 direct-colour pages that the title redraws behind the words.
        var panel = new RasterImage(160, 128);
        panel.CopyFrom(DirectImage(192, 448, 160, 64), 0, 0, 160, 64);
        panel.CopyFrom(DirectImage(352, 448, 160, 64), 0, 0, 160, 64, 0, 64);
        Save(panel, "title_panel.png");
        var copyright = new RasterImage(180, 24);
        for (int i = 0; i < 180 * 24; i++) copyright.SetPixel(i % 180, i / 180, ToColor(BitConverter.ToUInt16(_overlay, CopyrightOffset + i * 2), false));
        Save(copyright, "title_copyright.png");

        var palettes = new JsonArray();
        for (int palette = 0; palette < 4; palette++)
        {
            var colours = new JsonArray();
            for (int entry = 0; entry < 16; entry++) colours.Add((int)BitConverter.ToUInt16(_overlay, PaletteTableOffset + (palette * 16 + entry) * 2));
            palettes.Add(colours);
        }

        var words = new JsonArray();
        var glows = new JsonArray();
        var wordImages = new List<RasterImage>();
        for (int entry = 0; entry < MenuEntries; entry++)
        {
            var (pageX, pageY) = Page(BitConverter.ToUInt32(_overlay, TextPageOffset + entry * 4));
            var (u, v, clutX, clutY) = Uv(BitConverter.ToUInt32(_overlay, TextUvOffset + entry * 4));
            if (clutX != 480 || clutY != 384 + entry) throw new InvalidDataException("A menu word does not use its own CLUT row.");
            // Menu words keep their 4-bit indices; the presentation recolours them as the retail CLUT blends do.
            var indices = new RasterImage(MenuWordWidth, MenuWordHeight, RasterFormat.Luminance8);
            for (int row = 0; row < MenuWordHeight; row++)
                for (int column = 0; column < MenuWordWidth; column++)
                    indices.SetPixel(column, row, new Rgba32((byte)Texel(pageX, pageY, u + column, v + row), 0, 0));
            Save(indices, $"title_word_{entry}.png");
            wordImages.Add(indices);
            words.Add(new JsonObject { ["file"] = $"title_word_{entry}.png" });

            (pageX, pageY) = Page(BitConverter.ToUInt32(_overlay, GlowPageOffset + entry * 4));
            (u, v, clutX, clutY) = Uv(BitConverter.ToUInt32(_overlay, GlowUvOffset + entry * 4));
            if (clutX != 480 || clutY != 394) throw new InvalidDataException("A menu glow does not use the glow CLUT row.");
            uint size = BitConverter.ToUInt32(_overlay, GlowSizeOffset + entry * 4), origin = BitConverter.ToUInt32(_overlay, GlowOriginOffset + entry * 4);
            int width = (int)(size & 0xFFFF), height = (int)(size >> 16);
            var glow = new RasterImage(width, height);
            for (int row = 0; row < height; row++)
                for (int column = 0; column < width; column++)
                    glow.SetPixel(column, row, ToColor(BitConverter.ToUInt16(_overlay, PaletteTableOffset + 32 * 2 + Texel(pageX, pageY, u + column, v + row) * 2), true));
            Save(glow, $"title_glow_{entry}.png");
            if (entry == NewGameEntry) Save(glow, $"title_glow_{SettingsEntry}.png");
            glows.Add(new JsonObject
            {
                ["file"] = $"title_glow_{entry}.png",
                ["x"] = (int)(origin & 0xFFFF),
                ["y"] = (int)(origin >> 16),
            });
        }
        // Settings is added by this game. It shares New Game's glow, which is centred on the same slot.
        Save(ComposeSettings(wordImages), $"title_word_{SettingsEntry}.png");
        words.Add(new JsonObject { ["file"] = $"title_word_{SettingsEntry}.png", ["authored"] = "Settings, composed from the disc's menu lettering" });
        var newGameGlow = glows[NewGameEntry]!;
        glows.Add(new JsonObject { ["file"] = $"title_glow_{SettingsEntry}.png", ["x"] = (int)newGameGlow["x"]!, ["y"] = (int)newGameGlow["y"]! });
        return new JsonObject
        {
            ["display_origin"] = new JsonArray(512, 16),
            ["display_size"] = new JsonArray(512, 448),
            ["panel_origin"] = new JsonArray(176, 256),
            ["copyright_origin"] = new JsonArray(678 - 512, 376),
            ["publisher_origin"] = new JsonArray(32, 88),
            ["developer_origin"] = new JsonArray(96, 104),
            ["palettes"] = palettes,
            ["words"] = words,
            ["glows"] = glows,
        };
    }
}
