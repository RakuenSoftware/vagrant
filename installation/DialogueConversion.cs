// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// Converts the retail dialogue font and decodes retail strings to Unicode for the shared engine's dialogue.
/// The font is the first table of BATTLE/SYSTEM.DAT's font section: 189 glyphs in 12x12 cells, 21 per row, at 4 bits
/// through a grey ramp. A glyph's code is its index; advances come from the field engine's width table.
/// </summary>
public sealed class DialogueConversion
{
    public const int Glyphs = 189, Cell = 12, Columns = 21;
    private const int LetteredRow = (Glyphs + Columns - 1) / Columns * Cell;
    /// <summary>Vertical distance between dialogue lines, in screen pixels.</summary>
    public const int LineHeight = 13;
    // BATTLE.PRG's per-glyph advances: the fixed-width set (all 6) and, after it, the lettered set's.
    private const int WidthTableOffset = 0x83010 + Glyphs;
    private const int BalloonTableOffset = 0x82D88; // BATTLE.PRG, pointers to the speech-balloon shapes.
    private const int Battle = unchecked((int)0x80068800);   // BATTLE.PRG's load address.
    private const int BalloonShapes = 9;

    /// <summary>
    /// Unicode for each glyph code; '\0' marks codes with no glyph. Private-use code points stand in for the
    /// "Lv." and continuation-arrow glyphs, which have no Unicode equivalent.
    /// </summary>
    private static readonly string Characters =
        "0123456789ABCDEFGHIJK" +
        "LMNOPQRSTUVWXYZabcdef" +
        "ghijklmnopqrstuvwxyzŒ" +
        "ÀÁÂÄÇÈÉÊËÌÍÎÏÒÓÔÖÙÚÛÜ" +
        "ßœàáâäçèéêëìíîïòóôöùú" +
        "ûü\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0" +
        "\0\0\0\0\0\0\0\0„‼≠≦≧÷·–… !\"#" +
        "$%&'()=@[];:,./\\<>?_-" +
        "+*`{}♪△□○×←→↑↓★■~";

    private readonly byte[] _font;
    private readonly byte[] _palette;
    private readonly byte[] _widths;
    private readonly byte[] _system;
    private readonly byte[] _battle;

    public DialogueConversion(byte[] system, byte[] battle)
    {
        if (Characters.Length != Glyphs) throw new InvalidOperationException("The glyph map is incomplete.");
        int fontOffset = (int)BitConverter.ToUInt32(system, 28);
        if (fontOffset + 0xE000 + 158 * 128 > system.Length) throw new InvalidDataException("The system data is truncated.");
        _system = system;
        _battle = battle;
        _font = system[fontOffset..(fontOffset + 0x7000)];
        // Field speech balloons print in font colour 0: a dark ink ramp in the font's first table at VRAM (832, 222),
        // which the game uploads with the font to (832, 0).
        int palette = fontOffset + (222 * 64) * 2;
        _palette = system[palette..(palette + 32)];
        _widths = battle[WidthTableOffset..(WidthTableOffset + Glyphs)];
    }

    /// <summary>Writes the font image and returns its definition, in the shape the field presentation builds a bitmap font from.</summary>
    public JsonObject Export(IDictionary<string, Image> images) =>
        // The sheet holds two sets of the glyphs, 9 rows of 12 apart: an upright one, and below it the hand-lettered
        // italic capitals the field's balloons and its HUD print in.
        Sheet(images, "dialogue_font", LetteredRow, _palette, _widths);

    /// <summary>
    /// Writes the upright set the field's system box prints in (box type 4: the messages, such as "No target in
    /// range", and the attack readout at the foot of the screen) white: the glyphs' indices run from a faint edge (1)
    /// to their core (14 and 15), as the second ramp of the font table's next row (VRAM (848, 223)) runs from dark to
    /// white. The advances are the fixed-width set's (all 6; BATTLE.PRG 0x83010). Returns its definition.
    /// </summary>
    public JsonObject ExportPlain(IDictionary<string, Image> images)
    {
        int fontOffset = (int)BitConverter.ToUInt32(_system, 28), palette = fontOffset + (223 * 64 + 16) * 2;
        return Sheet(images, "system_font", 0, _system[palette..(palette + 32)], _battle[(WidthTableOffset - Glyphs)..WidthTableOffset]);
    }

    private JsonObject Sheet(IDictionary<string, Image> images, string name, int row, byte[] palette, byte[] widths)
    {
        int rows = (Glyphs + Columns - 1) / Columns;
        var image = Image.CreateEmpty(Columns * Cell, rows * Cell, false, Image.Format.Rgba8);
        for (int y = 0; y < rows * Cell; y++)
            for (int x = 0; x < Columns * Cell; x++)
            {
                int packed = _font[(y + row) * 128 + x / 2], index = (x & 1) == 0 ? packed & 15 : packed >> 4;
                int colour = BitConverter.ToUInt16(palette, index * 2), level = colour & 31;
                byte value = (byte)((level << 3) | (level >> 2));
                image.SetPixel(x, y, Color.Color8(value, value, value, index == 0 ? (byte)0 : (byte)255));
            }
        images[name + "_image"] = image;
        var glyphs = new JsonObject();
        for (int code = 0; code < Glyphs; code++)
        {
            if (Characters[code] == '\0') continue;
            glyphs[((int)Characters[code]).ToString()] = new JsonObject
            {
                ["rect"] = new JsonArray { code % Columns * Cell, code / Columns * Cell, Cell, Cell },
                ["advance"] = (int)widths[code],
            };
        }
        return new JsonObject { ["schema"] = 1, ["texture"] = name + "_image", ["size"] = Cell, ["ascent"] = Cell, ["descent"] = 0, ["line_height"] = LineHeight, ["glyphs"] = glyphs };
    }

    // BATTLE.PRG's field text tables: one at 0x800EA868 of halfword offsets in halfwords from itself, and the field
    // messages' at 0x800EBDDC (0x800CB40C picks from it).
    private const int TextTable = unchecked((int)0x800EA868) - Battle, MessageTable = unchecked((int)0x800EBDDC) - Battle;
    // The name a crate's fighter record takes as a target ("Cube"), which BATTLE.PRG 0x8007647C copies from the
    // executable (loaded at 0x8000F800 past its 0x800-byte header).
    private const int CubeName = unchecked((int)0x8004FDD0) - unchecked((int)0x8000F800);
    // The field menu's table (0x800EB9DC): its commands (0 to 9: MAGIC, BREAK ARTS, BATTLE ABILITIES, ITEMS, STATUS,
    // MAP, DATA, OPTIONS, SCORE, QUICK MANUAL), then "This command cannot be used" (11) and its reasons (13 to 21).
    private const int MenuTable = unchecked((int)0x800EB9DC) - Battle;

    /// <summary>
    /// The field's battle text, as BATTLE.PRG holds it: the body parts the attack readout names (entries 1 to 15 of
    /// the text table: HEAD, R.ARM, L.ARM, LEGS, TAIL, BODY, CHEST, ABDOMEN, R.LEG, L.LEG, MOUTH, NECK, WINGS, R.ARM,
    /// L.ARM), its "TARGET:" line (20), its hit-points part (21: HP, a number, a slash, a number, a percent sign), the
    /// target kinds (25 to 39), the damage types (41 to 43: BLN/, EGD/, PRC/), the affinities (44 to 50: PHY, AIR, FIR,
    /// ERT, WAT, LIT, DRK) and the classes (51 to 56: /Human to /Evil), and the field messages (0: "No target in range!",
    /// 1 to 10 the chests' and doors': bound by magic, locked with a key, locked, unlocked with a key), and the menu
/// table's announcements (23 to 30: Spell, Special Attack, Break Art, Item Effect and Trap, each with its name in
/// quotes, and "Mastered the spell"). A string
    /// the game fills in (ff n) is a string token at its place. The executable gives the name a crate is aimed at by.
    /// </summary>
    public JsonObject ExportBattleText(byte[] executable)
    {
        string Ascii(int at)
        {
            int end = at;
            while (end < _battle.Length && _battle[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(_battle, at, end - at);
        }
        JsonObject Entry(int table, int index)
        {
            int offset = BitConverter.ToUInt16(_battle, table + index * 2) * 2;
            var (text, tokens) = Decode(_battle, table + offset);
            return new JsonObject { ["text"] = text, ["tokens"] = tokens };
        }
        JsonArray Table(int table, int first, int last)
        {
            var list = new JsonArray();
            for (int i = first; i <= last; i++) list.Add(Entry(table, i));
            return list;
        }
        JsonArray Range(int first, int last) => Table(TextTable, first, last);
        // The message table's offsets are in halfwords from the table too; the first one, where the first message
        // begins, is the table's length.
        var messages = new JsonArray();
        int count = BitConverter.ToUInt16(_battle, MessageTable);
        for (int i = 0; i < count; i++) messages.Add(Entry(MessageTable, i));
        return new JsonObject
        {
            ["parts"] = Range(1, 15), ["target"] = Entry(TextTable, 20), ["hit_points"] = Entry(TextTable, 21),
            // An item's chance on its target, with no hit points ("Ashley Riot 100%"), and the parts list's word for a
            // target taken whole, "#TARGET" in the HUD's ASCII strings (BATTLE.PRG 0x8006987D; "#" right-aligns it).
            ["item_chance"] = Entry(TextTable, 23), ["whole_target"] = Ascii(unchecked((int)0x8006987D) - Battle),
            ["kinds"] = Range(25, 39), ["damage"] = Range(41, 43), ["affinities"] = Range(44, 50), ["classes"] = Range(51, 56),
            ["messages"] = messages,
            ["menu"] = Table(MenuTable, 0, 9), ["menu_refusal"] = Entry(MenuTable, 11), ["menu_reasons"] = Table(MenuTable, 13, 21),
            ["announcements"] = Table(MenuTable, 23, 30),
            ["cube"] = Decode(executable, CubeName).Text,
        };
    }

    /// <summary>
    /// Writes the speech balloons: the 64x64 paper tile they are filled with (the top-left of SYSTEM.DAT's UI page at
    /// VRAM (448, 256), through the palette the game uploads with the font's third table to (992, 220)), and the
    /// balloon shapes from the field engine's table. A shape is a header (total vertices, tail parts, outline
    /// vertices, body polygons), one entry per tail part (attach start, attach end, its vertices, its polygons),
    /// vertices as x, y bytes across the box (tail vertices are signed offsets from the attach start), then
    /// polygons as four vertex indices (0xFF for a triangle's fourth).
    /// </summary>
    /// <summary>
    /// A balloon shape at <paramref name="at"/>: its header (total vertices, tail parts, outline vertices, body
    /// polygons) as part 0, one entry per tail part, the vertices, then each part's polygons. Shapes 0 to 8 are the
    /// field engine's; a script may carry shapes 9 to 15 of its own (see <see cref="EventConversion"/>).
    /// </summary>
    public static JsonObject Balloon(byte[] data, int at)
    {
        int total = data[at], parts = data[at + 1];
        var partList = new JsonArray();
        for (int part = 0; part <= parts; part++)
            partList.Add(new JsonArray { data[at + part * 4], data[at + part * 4 + 1], data[at + part * 4 + 2], data[at + part * 4 + 3] });
        int vertices = at + (parts + 1) * 4;
        var vertexList = new JsonArray();
        for (int v = 0; v < total; v++) vertexList.Add(new JsonArray { data[vertices + v * 2], data[vertices + v * 2 + 1] });
        var polygons = new JsonArray();
        int polygon = vertices + total * 2;
        for (int part = 0; part <= parts; part++)
        {
            var list = new JsonArray();
            for (int p = 0; p < data[at + part * 4 + 3]; p++, polygon += 4)
                list.Add(new JsonArray { data[polygon], data[polygon + 1], data[polygon + 2], data[polygon + 3] });
            polygons.Add(list);
        }
        return new JsonObject { ["parts"] = partList, ["vertices"] = vertexList, ["polygons"] = polygons };
    }

    /// <summary>The speech balloons' shapes, and the paper they are drawn on among the images.</summary>
    public JsonObject Balloons(IDictionary<string, Image> images)
    {
        int page = (int)BitConverter.ToUInt32(_system, 4), fontOffset = (int)BitConverter.ToUInt32(_system, 28);
        int pageWidth = BitConverter.ToInt16(_system, page + 4) * 4;
        int clut = fontOffset + 0xE000 + ((220 - 66) * 64 + (992 - 960)) * 2;
        var tile = Image.CreateEmpty(64, 64, false, Image.Format.Rgba8);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int packed = _system[page + 8 + (y * pageWidth + x) / 2], index = (x & 1) == 0 ? packed & 15 : packed >> 4;
                int colour = BitConverter.ToUInt16(_system, clut + index * 2);
                int r = colour & 31, g = (colour >> 5) & 31, b = (colour >> 10) & 31;
                tile.SetPixel(x, y, Color.Color8((byte)((r << 3) | (r >> 2)), (byte)((g << 3) | (g >> 2)), (byte)((b << 3) | (b >> 2))));
            }
        images["dialogue_paper"] = tile;

        var shapes = new JsonArray();
        for (int index = 0; index < BalloonShapes; index++)
        {
            int at = (int)BitConverter.ToUInt32(_battle, BalloonTableOffset + index * 4) - Battle;
            if (at <= 0 || at >= _battle.Length) throw new InvalidDataException("A speech balloon shape is missing.");
            shapes.Add(Balloon(_battle, at));
        }
        return new JsonObject { ["paper"] = "dialogue_paper", ["shapes"] = shapes };
    }

    /// <summary>The character a pixel of horizontal offset is written as; the dialogue font gives it a one-pixel advance.</summary>
    public const char HairSpace = '\u200A';

    /// <summary>
    /// Decodes one retail string starting at <paramref name="at"/>. Line breaks become newlines and new pages become
    /// page tokens; a horizontal offset (fa n, n pixels) becomes spaces of the font's six-pixel advance and, for what
    /// is left, hair spaces of one pixel (<see cref="HairSpace"/>); fb 04 becomes a centre token, and ff n a string token for the game's n-th filled-in string.
    /// Vertical placement, colour and print-speed controls only affect the retail layout and are dropped.
    /// </summary>
    public static (string Text, JsonArray Tokens) Decode(byte[] data, int at)
    {
        var text = new StringBuilder();
        var tokens = new JsonArray();
        while (at < data.Length)
        {
            int code = data[at++];
            if (code < Glyphs)
            {
                if (Characters[code] != '\0') text.Append(Characters[code]);
                continue;
            }
            switch (code)
            {
                case 0xE5:
                    tokens.Add(new JsonObject { ["type"] = "page", ["offset"] = text.Length });
                    continue;
                case 0xE7:
                    return (text.ToString(), tokens);
                case 0xE8:
                    text.Append('\n');
                    continue;
                case 0xFD: case 0xFE:
                    // A number the script set (1b): its slot is the parameter's last digit, in hex (0xfd) or decimal.
                    tokens.Add(new JsonObject { ["type"] = "number", ["offset"] = text.Length, ["slot"] = data[at++] % 10, ["hex"] = code == 0xFD });
                    continue;
                case 0xFF:
                    // A string the game fills in (an item's name, a target's part): its slot.
                    tokens.Add(new JsonObject { ["type"] = "string", ["offset"] = text.Length, ["slot"] = data[at++] });
                    continue;
                case 0xFA:
                {
                    int offset = data[at++];
                    if (offset < 240) text.Append(' ', offset / 6).Append(HairSpace, offset % 6);
                    continue;
                }
                case 0xFB:
                    // fb 04 centres the message's lines across its box (the shouts and the credits); other values pace
                    // the printing.
                    if (data[at++] == 4) tokens.Add(new JsonObject { ["type"] = "centre", ["offset"] = text.Length });
                    continue;
                case >= 0xEC:
                    at++;   // Two-byte controls: colour, placement, speed and the printer's sound (f9, which no retail text uses).
                    continue;
                default:
                    continue;   // Single-byte controls such as the continuation arrow.
            }
        }
        throw new InvalidDataException("A retail string is not terminated.");
    }
}
