// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// The CONGRATULATIONS screen a boss's defeat opens (MENU/MENUF.PRG, submenu 0): its lettering (SMALL/RANK.DIS) and the
/// tables it reckons the score, the rank and the bonus by.
/// </summary>
public static class ResultsConversion
{
    // MENUF.PRG's tables, as file offsets: the lettering's 127 sprites, the sixteen ranks' words, the three bonus reels,
    // the ranks' title and score thresholds, and the points for a kill of each class and an attack with each weapon kind.
    private const int Sprites = 0x69D8, Ranks = 0x6DD0, Reels = 0x6E10, TitleThresholds = 0x6FEC, ScoreThresholds = 0x700C;
    private const int KillPoints = 0x398, AttackPoints = 0x3A4;
    // The prompt's two words, plain ASCII the HUD lettering prints.
    private const int Press = 0x388, Button = 0x390;
    private const int TimSize = 0x8220;

    /// <summary>
    /// The tables. A sprite is its rectangle on the lettering sheet (the three RANK.DIS pictures side by side, 256 wide
    /// each, by its texture page 29, 30 or 31): 0 to 9 the digits, 10 the comma, 11 BONUS, 12 RISKBREAKER RANK, 13 to 17
    /// STR, AGL, INT, HP and MP, 18 SCORE, 19 the percent sign, 20 PTS., 21 MAP COMPLETED, 22 and 23 CONGRATULATIONS'
    /// halves, 24 plus, 25 minus, 26 the colon, 27 to 68 the ranks' words. A rank is its words; a reel sixteen stops of
    /// a stat sprite and an amount.
    /// </summary>
    public static JsonObject Export(byte[] menu)
    {
        if (menu.Length < ScoreThresholds + 64) throw new InvalidDataException("MENUF.PRG is truncated.");
        var sprites = new JsonArray();
        for (int i = 0; i < 127; i++)
        {
            int at = Sprites + i * 8, page = BitConverter.ToUInt16(menu, at + 4) & 0xF;
            sprites.Add(new JsonArray { (page - 13) * 256 + menu[at], menu[at + 1], menu[at + 2], menu[at + 3] });
        }
        var ranks = new JsonArray();
        for (int i = 0; i < 16; i++)
        {
            var words = new JsonArray();
            for (int w = 0; w < menu[Ranks + i * 4] && w < 3; w++) words.Add(menu[Ranks + i * 4 + 1 + w]);
            ranks.Add(words);
        }
        var reels = new JsonArray();
        for (int r = 0; r < 3; r++)
        {
            var stops = new JsonArray();
            for (int i = 0; i < 16; i++)
            {
                int at = Reels + (r * 16 + i) * 4;
                stops.Add(new JsonArray { BitConverter.ToInt16(menu, at), BitConverter.ToInt16(menu, at + 2) });
            }
            reels.Add(stops);
        }
        JsonArray Shorts(int at, int count)
        {
            var list = new JsonArray();
            for (int i = 0; i < count; i++) list.Add(BitConverter.ToInt16(menu, at + i * 2));
            return list;
        }
        var scores = new JsonArray();
        for (int i = 0; i < 16; i++) scores.Add(BitConverter.ToUInt32(menu, ScoreThresholds + i * 4));
        return new JsonObject
        {
            ["sprites"] = sprites, ["ranks"] = ranks, ["reels"] = reels,
            ["title_thresholds"] = Shorts(TitleThresholds, 16), ["score_thresholds"] = scores,
            ["kill_points"] = Shorts(KillPoints, 6), ["attack_points"] = Shorts(AttackPoints, 10),
            ["press"] = Ascii(menu, Press), ["button"] = Ascii(menu, Button),
            ["sheet"] = "results_sheet", ["reel_sheet"] = "results_reel",
        };
    }

    private static string Ascii(byte[] data, int at)
    {
        int end = Array.IndexOf(data, (byte)0, at);
        return System.Text.Encoding.ASCII.GetString(data, at, (end < 0 ? data.Length : end) - at);
    }

    /// <summary>
    /// The lettering sheet, 768 by 256: RANK.DIS's three 4-bit pictures side by side through one of the first
    /// picture's palettes (0 the plain grey ramp the screen tints, 2 the gold ramp of the bonus reel), texel 0 clear.
    /// </summary>
    public static Godot.Image Sheet(byte[] rank, int palette)
    {
        if (rank.Length < TimSize * 3) throw new InvalidDataException("RANK.DIS is truncated.");
        var colours = new ushort[16];
        for (int i = 0; i < 16; i++) colours[i] = BitConverter.ToUInt16(rank, 20 + palette * 32 + i * 2);
        var rgba = new byte[768 * 256 * 4];
        for (int t = 0; t < 3; t++)
        {
            int image = t * TimSize + 8 + (int)BitConverter.ToUInt32(rank, t * TimSize + 8);
            int width = BitConverter.ToUInt16(rank, image + 8) * 4, height = BitConverter.ToUInt16(rank, image + 10);
            for (int y = 0; y < Math.Min(256, height); y++)
                for (int x = 0; x < Math.Min(256, width); x++)
                {
                    int texel = rank[image + 12 + (y * width + x) / 2] >> (x % 2 * 4) & 0xF;
                    if (texel == 0) continue;
                    int colour = colours[texel], at = (y * 768 + t * 256 + x) * 4;
                    var shown = TitleConversion.ToColor((ushort)colour, false);
                    (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = ((byte)shown.R8, (byte)shown.G8, (byte)shown.B8, 255);
                }
        }
        return Godot.Image.CreateFromData(768, 256, false, Godot.Image.Format.Rgba8, rgba);
    }
}
