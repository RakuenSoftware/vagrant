// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// The CONGRATULATIONS screen a boss's defeat opens (MENU/MENUF.PRG, submenu 0): the sprites of its lettering
/// (SMALL/RANK.DIS), its colours, and the tables it reckons the score, the rank and the bonus by.
/// </summary>
public static class ResultsConversion
{
    /// <summary>Where a program keeps the screen's tables, as file offsets (-1 where it has none).</summary>
    private readonly record struct Layout(int Sprites, int SpriteCount, int Ranks, int Reels, int TitleThresholds, int ScoreThresholds, int KillPoints, int AttackPoints, int Colours, int ColourCount, int Press, int Button);

    // MENUF.PRG's tables (loaded at 0x80102800): the lettering's 127 sprites, the sixteen ranks' words, the three bonus
    // reels, the ranks' title and score thresholds, the points for a kill of each class and an attack with each weapon
    // kind, the colour pairs the parts are shaded by (0x80109738, four bytes each), and the prompt's two words.
    private static readonly Layout Menu = new(0x69D8, 127, 0x6DD0, 0x6E10, 0x6FEC, 0x700C, 0x398, 0x3A4, 0x6F38, 22, 0x388, 0x390);
    // ENDING.PRG's copy of the screen (loaded at 0x80068800, after the credits; the sprites at 0x800DB814, the ranks at
    // 0x800DBA3C, the colours at 0x800DBA7C, the thresholds at 0x800DBAB8 and 0x800DBAD8, the points at 0x800688B4
    // and 0x800688C0): the ranks' 69 sprites, no reel and no prompt.
    private static readonly Layout Ending = new(0x73014, 69, 0x7323C, -1, 0x732B8, 0x732D8, 0xB4, 0xC0, 0x7327C, 15, -1, -1);

    /// <summary>
    /// The tables. A sprite is its corner and size on its texture page, the page and its palette (RANK.DIS's pictures
    /// as MENUF loads them, pages 29 to 31): 0 to 9 the digits, 10 the comma, 11 BONUS, 12 RISKBREAKER RANK, 13 to 17
    /// STR, AGL, INT, HP and MP, 18 SCORE, 19 the percent sign, 20 PTS., 21 MAP COMPLETED, 22 and 23 CONGRATULATIONS'
    /// halves, 24 plus, 25 minus, 26 the colon, 27 to 68 the ranks' words. A rank is its words; a reel sixteen stops of
    /// a stat sprite and an amount. A colour is red, green and blue. From MENUF.PRG, or from ENDING.PRG's copy
    /// (<paramref name="ending"/>), whose lettering is END.DIS.
    /// </summary>
    public static JsonObject Export(byte[] menu, bool ending = false)
    {
        var (Sprites, SpriteCount, Ranks, Reels, TitleThresholds, ScoreThresholds, KillPoints, AttackPoints, Colours, ColourCount, Press, Button) = ending ? Ending : Menu;
        if (menu.Length < Math.Max(Sprites + SpriteCount * 8, ScoreThresholds + 64)) throw new InvalidDataException("The results' program is truncated.");
        var sprites = new JsonArray();
        for (int i = 0; i < SpriteCount; i++)
        {
            int at = Sprites + i * 8;
            sprites.Add(new JsonArray { menu[at], menu[at + 1], menu[at + 2], menu[at + 3], BitConverter.ToUInt16(menu, at + 4), BitConverter.ToUInt16(menu, at + 6) });
        }
        var ranks = new JsonArray();
        for (int i = 0; i < 16; i++)
        {
            var words = new JsonArray();
            for (int w = 0; w < menu[Ranks + i * 4] && w < 3; w++) words.Add(menu[Ranks + i * 4 + 1 + w]);
            ranks.Add(words);
        }
        var reels = new JsonArray();
        for (int r = 0; r < 3 && Reels >= 0; r++)
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
        var colours = new JsonArray();
        for (int i = 0; i < ColourCount; i++) colours.Add(new JsonArray { menu[Colours + i * 4], menu[Colours + i * 4 + 1], menu[Colours + i * 4 + 2] });
        var scores = new JsonArray();
        for (int i = 0; i < 16; i++) scores.Add(BitConverter.ToUInt32(menu, ScoreThresholds + i * 4));
        var tables = new JsonObject
        {
            ["sprites"] = sprites, ["colours"] = colours, ["ranks"] = ranks, ["reels"] = reels,
            ["title_thresholds"] = Shorts(TitleThresholds, 16), ["score_thresholds"] = scores,
            ["kill_points"] = Shorts(KillPoints, 6), ["attack_points"] = Shorts(AttackPoints, 10),
        };
        if (Press >= 0) (tables["press"], tables["button"]) = (Ascii(menu, Press), Ascii(menu, Button));
        return tables;
    }

    private static string Ascii(byte[] data, int at)
    {
        int end = Array.IndexOf(data, (byte)0, at);
        return System.Text.Encoding.ASCII.GetString(data, at, (end < 0 ? data.Length : end) - at);
    }
}
