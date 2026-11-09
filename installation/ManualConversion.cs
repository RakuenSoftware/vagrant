// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// The Quick Manual: one topic per SMALL/HELPnn.HF0 (01 to 14). A file opens with four u32s (its text's size, the
/// pictures' block size, whether its rules are drawn, and one unused), then a table of u16 line offsets in halfwords
/// from the table's start; the first entry is also the line count. Each line is a retail string. The first line
/// opens with a two-byte value and repeats the topic's opening sentence, which (run on to its full stop) stands as its
/// title. Its pictures (their cells in the matching HF1) and its rules follow the text.
/// </summary>
public static class ManualConversion
{
    public const int Topics = 14;
    private const int Table = 0x10;

    /// <summary>
    /// A topic with its pictures (MENU/MENUE.PRG, loaded at 0x80102800, draws them at 0x80103e6c). The HF0's second
    /// block (at 0x10 plus its text's length, rounded to four) is a count and halfword offsets to picture records; a
    /// record whose first halfword is 0 is a still picture: from byte 0x10 its x and y on the page, its width and
    /// height in pixels, a halfword, its colours (16 for four bits a pixel, otherwise eight), its palette, and the cells
    /// that tile it row by row (16 or 8 pixels wide, 16 high). The HF1 is a cell count and a palette count, the cells
    /// (128 bytes each: 4 halfwords by 16 rows) and the palettes (16 colours each); palette p starts at colour 16 p.
    /// Records with a nonzero first halfword blink (0x80104c30: a 16-bit mask at 2, stepped every halfword-4 frames);
    /// they are drawn still.
    /// </summary>
    public static JsonObject Export(byte[] help, byte[] pictures, string name, IDictionary<string, Image> images)
    {
        var topic = Export(help);
        var list = new JsonArray();
        int textLength = (int)BitConverter.ToUInt32(help, 0), block = 0x10 + (textLength + 3) / 4 * 4;
        if (pictures.Length >= 8 && block + 2 <= help.Length)
        {
            int cells = (int)BitConverter.ToUInt32(pictures, 0), palettes = (int)BitConverter.ToUInt32(pictures, 4);
            int paletteAt = 8 + cells * 128;
            int count = BitConverter.ToUInt16(help, block);
            for (int i = 0; i < count && block + 2 + i * 2 + 2 <= help.Length; i++)
            {
                int record = block + 2 * BitConverter.ToUInt16(help, block + 2 + i * 2);
                // A record whose first halfword is set blinks (a 16-bit mask at 2, stepped every halfword-4 frames); it
                // is drawn as a still here.
                if (record + 0x20 > help.Length) continue;
                int x = BitConverter.ToInt16(help, record + 0x10), y = BitConverter.ToInt16(help, record + 0x12);
                int width = BitConverter.ToInt16(help, record + 0x14), height = BitConverter.ToInt16(help, record + 0x16);
                bool deep = BitConverter.ToInt16(help, record + 0x1A) != 16;
                int palette = BitConverter.ToInt16(help, record + 0x1C), tile = deep ? 8 : 16;
                if (width <= 0 || height <= 0 || width > 512 || height > 512) continue;
                int columns = (width + tile - 1) / tile, rows = (height + 15) / 16;
                var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < columns; c++)
                    {
                        int at = record + 0x1E + (r * columns + c) * 2;
                        if (at + 2 > help.Length) break;
                        int cell = BitConverter.ToInt16(help, at);
                        if (cell < 0 || cell >= cells) continue;
                        for (int py = 0; py < 16 && r * 16 + py < height; py++)
                            for (int px = 0; px < tile && c * tile + px < width; px++)
                            {
                                int data = 8 + cell * 128 + py * 8;
                                int index = deep ? pictures[data + px] : (pictures[data + px / 2] >> ((px & 1) * 4)) & 15;
                                int colour = palette * 16 + index;
                                if (colour >= palettes * 16) continue;
                                image.SetPixel(c * tile + px, r * 16 + py, TitleConversion.ToColor(BitConverter.ToUInt16(pictures, paletteAt + colour * 2), true));
                            }
                    }
                string asset = $"{name}_picture{i}";
                images[asset] = image;
                list.Add(new JsonObject { ["image"] = asset, ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height });
            }
        }
        topic["pictures"] = list;
        // Then the page's rules (MENUE 0x80103d30, drawn while the third word is above 0): from the pictures' block
        // plus the second word (each rounded down to four), a count and 28-byte records of a blink state (eight
        // halfwords, as a picture's), the two ends (x, y on the page) and a colour.
        var rules = new JsonArray();
        int second = (int)BitConverter.ToUInt32(help, 4), shown = (int)BitConverter.ToUInt32(help, 8);
        int rulesAt = 0x10 + textLength / 4 * 4 + second / 4 * 4;
        if (shown > 0 && rulesAt + 2 <= help.Length)
        {
            int count = BitConverter.ToUInt16(help, rulesAt);
            for (int i = 0; i < count && rulesAt + 2 + (i + 1) * 28 <= help.Length; i++)
            {
                int r = rulesAt + 2 + i * 28;
                rules.Add(new JsonObject
                {
                    ["from"] = new JsonArray { (int)BitConverter.ToInt16(help, r + 16), (int)BitConverter.ToInt16(help, r + 18) },
                    ["to"] = new JsonArray { (int)BitConverter.ToInt16(help, r + 20), (int)BitConverter.ToInt16(help, r + 22) },
                    ["colour"] = new JsonArray { (int)help[r + 24], (int)help[r + 25], (int)help[r + 26] },
                });
            }
        }
        topic["rules"] = rules;
        return topic;
    }

    public static JsonObject Export(byte[] help)
    {
        if (help.Length < Table + 2) throw new InvalidDataException("A Quick Manual topic is truncated.");
        int count = BitConverter.ToUInt16(help, Table);
        if (Table + count * 2 > help.Length) throw new InvalidDataException("A Quick Manual topic's line table is malformed.");
        var lines = new JsonArray();
        for (int i = 1; i < count; i++)
            lines.Add(DialogueConversion.Decode(help, Table + 2 * BitConverter.ToUInt16(help, Table + i * 2)).Text.TrimEnd());
        // The title is the opening sentence, which may run on over the next lines.
        string title = DialogueConversion.Decode(help, Table + 2 * BitConverter.ToUInt16(help, Table) + 2).Text.Trim();
        for (int i = 1; !title.EndsWith('.') && i < lines.Count; i++)
            if (lines[i]!.GetValue<string>().Trim() is { Length: > 0 } more) title += " " + more;
            else break;
        return new JsonObject { ["title"] = title, ["lines"] = lines };
    }
}
