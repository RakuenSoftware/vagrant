// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Godot;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// The field HUD's art, from BATTLE/SYSTEM.DAT. The file opens with eight u32 section offsets. Section 0 is the HUD
/// page: an 8-byte VRAM rectangle (768, 0, 64 by 224 halfwords) and its 4-bit pixels, so 256 by 224 texels. Section 7
/// is three raw blocks uploaded to (832, 0) and (896, 0), 64 by 224 halfwords each, then (960, 66), 64 by 158; the
/// palettes the HUD draws with sit in the last row of the first two, at (832, 223) for digits, (864, 223) for the
/// labels and (896, 223) for the cursor. The page is written once through each palette.
/// Section 1 is a second 4-bit page at (448, 256), 64 by 240 halfwords; its "BATTLE MODE" (73 by 10 at (64, 0)) is
/// drawn through the palette at (976, 220), in section 7's third block.
/// The HUD's own lettering (BATTLE.PRG 0x800C6540) is set in the page's outlined capitals and punctuation: a character
/// less 32 indexes 4-bit widths at 0x800EB7B4 and columns at 0x800EB7D4 (the low bit picking row 86 for the
/// capitals, else row 76), 10 rows high; a pair of capitals closes by a 2-bit kerning at 0x800EB708 (26 by 26).
/// </summary>
public sealed class HudConversion
{
    // BATTLE.PRG loads at 0x80068800.
    private const int Widths = 0x82FB4, Columns = 0x82FD4, Kerning = 0x82F08;
    private const int Characters = 59;
    private readonly byte[] _system, _battle;

    public HudConversion(byte[] system, byte[] battle)
    {
        if (system.Length < 32) throw new InvalidDataException("SYSTEM.DAT is truncated.");
        if (battle.Length < Columns + Characters) throw new InvalidDataException("BATTLE.PRG is truncated.");
        _system = system;
        _battle = battle;
    }

    private ushort Word(int at) => BitConverter.ToUInt16(_system, at);

    /// <summary>
    /// A palette colour from section 7: VRAM x 832 to 1023 on row 223 (the first two blocks are 224 rows from y 0, the
    /// third 158 rows from y 66).
    /// </summary>
    private ushort Clut(int x, int index)
    {
        int fonts = (int)BitConverter.ToUInt32(_system, 28);
        int block = (x - 832) / 64, column = x - 832 - block * 64 + index;
        int row = block < 2 ? 223 : 223 - 66;
        return Word(fonts + block * 64 * 224 * 2 + (row * 64 + column) * 2);
    }

    /// <summary>The HUD page (256 by 224 texels) through the palette at (<paramref name="clut"/>, 223).</summary>
    public Image Page(int clut)
    {
        int page = (int)BitConverter.ToUInt32(_system, 0) + 8;
        if (page + 64 * 224 * 2 > _system.Length || clut < 832 || clut > 1008) throw new InvalidDataException("SYSTEM.DAT's HUD page is truncated.");
        var image = Image.CreateEmpty(256, 224, false, Image.Format.Rgba8);
        for (int y = 0; y < 224; y++)
            for (int u = 0; u < 256; u++)
            {
                int index = (Word(page + (y * 64 + u / 4) * 2) >> (u % 4 * 4)) & 15;
                ushort colour = Clut(clut, index);
                int r = colour & 31, g = (colour >> 5) & 31, b = (colour >> 10) & 31;
                image.SetPixel(u, y, Color.Color8((byte)((r << 3) | (r >> 2)), (byte)((g << 3) | (g >> 2)), (byte)((b << 3) | (b >> 2)), colour == 0 ? (byte)0 : (byte)255));
            }
        return image;
    }

    /// <summary>
    /// Section 1's page (VRAM (448, 256): 256 by 240 texels, 4-bit) through the palette at
    /// (<paramref name="clutX"/>, <paramref name="clutY"/>) in section 7's third block (x 960 to 1023, rows 66 to 223).
    /// The map screen's compass is drawn from it.
    /// </summary>
    public Image SecondPage(int clutX, int clutY)
    {
        int second = (int)BitConverter.ToUInt32(_system, 4) + 8;
        int fonts = (int)BitConverter.ToUInt32(_system, 28), third = fonts + 2 * 64 * 224 * 2;
        if (second + 64 * 240 * 2 > _system.Length || third + 64 * 158 * 2 > _system.Length) throw new InvalidDataException("SYSTEM.DAT's second HUD page is truncated.");
        if (clutX < 960 || clutX > 1008 || clutY < 66 || clutY > 223) throw new InvalidDataException("No palette of the second HUD page is there.");
        var image = Image.CreateEmpty(256, 240, false, Image.Format.Rgba8);
        for (int y = 0; y < 240; y++)
            for (int x = 0; x < 256; x++)
            {
                int index = (Word(second + (y * 64 + x / 4) * 2) >> (x % 4 * 4)) & 15;
                ushort colour = Word(third + ((clutY - 66) * 64 + clutX - 960 + index) * 2);
                int r = colour & 31, g = (colour >> 5) & 31, b = (colour >> 10) & 31;
                image.SetPixel(x, y, Color.Color8((byte)((r << 3) | (r >> 2)), (byte)((g << 3) | (g >> 2)), (byte)((b << 3) | (b >> 2)), colour == 0 ? (byte)0 : (byte)255));
            }
        return image;
    }

    /// <summary>Makes the HUD page through each palette, among the images, and returns the asset names by palette.</summary>
    public JsonObject Export(IDictionary<string, Image> images)
    {
        var assets = new JsonObject();
        foreach (var (name, x) in new[] { ("digits", 832), ("labels", 864), ("cursor", 896) })
        {
            images["hud_" + name] = Page(x);
            assets[name] = "hud_" + name;
        }
        // BATTLE MODE, from section 1's page through its palette in section 7's third block, (960, 66) 64 by 158.
        int second = (int)BitConverter.ToUInt32(_system, 4) + 8;
        int fonts = (int)BitConverter.ToUInt32(_system, 28), third = fonts + 2 * 64 * 224 * 2;
        if (second + 64 * 240 * 2 > _system.Length || third + 64 * 158 * 2 > _system.Length) throw new InvalidDataException("SYSTEM.DAT's second HUD page is truncated.");
        var banner = Image.CreateEmpty(73, 10, false, Image.Format.Rgba8);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 73; x++)
            {
                int u = 64 + x;
                int index = (Word(second + (y * 64 + u / 4) * 2) >> (u % 4 * 4)) & 15;
                ushort colour = Word(third + ((220 - 66) * 64 + 976 - 960 + index) * 2);
                int r = colour & 31, g = (colour >> 5) & 31, b = (colour >> 10) & 31;
                banner.SetPixel(x, y, Color.Color8((byte)((r << 3) | (r >> 2)), (byte)((g << 3) | (g >> 2)), (byte)((b << 3) | (b >> 2)), colour == 0 ? (byte)0 : (byte)255));
            }
        images["hud_battle_mode"] = banner;
        // The chain timing's "!" (BATTLE.PRG 0x800970BC): 22 by 32 at (0xC0, 0) of the same page, through the palette at
        // (960, 221).
        var cue = Image.CreateEmpty(22, 32, false, Image.Format.Rgba8);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 22; x++)
            {
                int u = 0xC0 + x;
                int index = (Word(second + (y * 64 + u / 4) * 2) >> (u % 4 * 4)) & 15;
                ushort colour = Word(third + ((221 - 66) * 64 + index) * 2);
                int r = colour & 31, g = (colour >> 5) & 31, b = (colour >> 10) & 31;
                cue.SetPixel(x, y, Color.Color8((byte)((r << 3) | (r >> 2)), (byte)((g << 3) | (g >> 2)), (byte)((b << 3) | (b >> 2)), colour == 0 ? (byte)0 : (byte)255));
            }
        images["hud_chain_cue"] = cue;
        // The enemies' balloons (BATTLE.PRG 0x80095C18): the page's top 64 rows through the palette at (992, 220), the
        // rounded and jagged balloons, the small one, their dots and marks.
        var emotes = Image.CreateEmpty(256, 64, false, Image.Format.Rgba8);
        for (int y = 0; y < 64; y++)
            for (int u = 0; u < 256; u++)
            {
                int index = (Word(second + (y * 64 + u / 4) * 2) >> (u % 4 * 4)) & 15;
                ushort colour = Word(third + ((220 - 66) * 64 + 992 - 960 + index) * 2);
                int r = colour & 31, g = (colour >> 5) & 31, b = (colour >> 10) & 31;
                emotes.SetPixel(u, y, Color.Color8((byte)((r << 3) | (r >> 2)), (byte)((g << 3) | (g >> 2)), (byte)((b << 3) | (b >> 2)), colour == 0 ? (byte)0 : (byte)255));
            }
        images["hud_emotes"] = emotes;
        assets["emotes"] = "hud_emotes";
        assets["battle_mode"] = "hud_battle_mode";
        assets["chain_cue"] = "hud_chain_cue";
        var widths = new JsonArray();
        var columns = new JsonArray();
        for (int c = 0; c < Characters; c++)
        {
            widths.Add((_battle[Widths + c / 2] >> (c % 2 * 4)) & 15);
            columns.Add((int)_battle[Columns + c]);
        }
        var kerning = new JsonArray();
        for (int pair = 0; pair < 26 * 26; pair++) kerning.Add((_battle[Kerning + pair / 4] >> (pair % 4 * 2)) & 3);
        assets["font"] = new JsonObject { ["widths"] = widths, ["columns"] = columns, ["kerning"] = kerning };
        return assets;
    }
}
