// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// The ending: ENDING/ILLUSTnn.BIN (06 to 16) are illustrations, each a compressed TIM, and ENDING/ENDING.PRG carries
/// the credits as a script of positioning bytes and text.
/// </summary>
/// <remarks>
/// The compressed stream is a run of commands: 0x00 to 0x7B copy that many plus one literal bytes; 0x7C repeats the
/// back-reference just made; 0x7D count value writes value count plus one times; 0x7E count a b writes the pair count
/// plus one times; 0x7F ends; 0x80 and up, with the byte after, copy (bits 4 to 6) plus three bytes from (the low
/// twelve bits) plus one back.
/// In the credits script, a heading follows a 0x03 byte and a name a 0x02; 0x01 n moves the next line n pixels down
/// (the second such value before a line is its step), and 0x1F inside text is a spacing control.
/// </remarks>
public static class EndingConversion
{
    public const int FirstIllustration = 6, LastIllustration = 16;
    private const int CreditsStart = 0x5C00, CreditsEnd = 0x7C00;

    public static byte[] Decompress(byte[] data)
    {
        var output = new List<byte>(data.Length * 2);
        int p = 0, lastLength = 0, lastDistance = 0;
        bool afterReference = false;
        while (p < data.Length)
        {
            int op = data[p++];
            if (op == 0x7F) break;
            if (op >= 0x80)
            {
                if (p >= data.Length) throw new InvalidDataException("An ending illustration is truncated.");
                lastLength = ((op >> 4) & 7) + 3;
                lastDistance = (((op & 15) << 8) | data[p++]) + 1;
                Copy(lastLength, lastDistance);
                afterReference = true;
                continue;
            }
            if (op == 0x7C && afterReference) { Copy(lastLength, lastDistance); continue; }
            afterReference = false;
            if (op == 0x7D)
            {
                int count = data[p] + 1;
                byte value = data[p + 1];
                p += 2;
                for (int i = 0; i < count; i++) output.Add(value);
                continue;
            }
            if (op == 0x7E)
            {
                int count = data[p] + 1;
                byte a = data[p + 1], b = data[p + 2];
                p += 3;
                for (int i = 0; i < count; i++) { output.Add(a); output.Add(b); }
                continue;
            }
            int literal = op + 1;
            if (p + literal > data.Length) throw new InvalidDataException("An ending illustration is truncated.");
            for (int i = 0; i < literal; i++) output.Add(data[p + i]);
            p += literal;
        }
        return output.ToArray();

        void Copy(int length, int distance)
        {
            if (distance > output.Count) throw new InvalidDataException("An ending illustration refers before its start.");
            for (int i = 0; i < length; i++) output.Add(output[output.Count - distance]);
        }
    }

    /// <summary>A TIM image (4, 8 or 16 bits a pixel; the first palette row for indexed ones). Colour 0 is transparent.</summary>
    public static Image Tim(byte[] tim)
    {
        if (tim.Length < 8 || BitConverter.ToUInt32(tim, 0) != 0x10) throw new InvalidDataException("An ending illustration is not a TIM.");
        uint flags = BitConverter.ToUInt32(tim, 4);
        int depth = (int)(flags & 3), at = 8;
        var palette = new ushort[256];
        if ((flags & 8) != 0)
        {
            int clutLength = BitConverter.ToInt32(tim, at);
            int colours = Math.Min(256, (int)BitConverter.ToUInt16(tim, at + 8));
            for (int i = 0; i < colours; i++) palette[i] = BitConverter.ToUInt16(tim, at + 12 + i * 2);
            at += clutLength;
        }
        int words = BitConverter.ToUInt16(tim, at + 8), height = BitConverter.ToUInt16(tim, at + 10), pixels = at + 12;
        int width = depth switch { 0 => words * 4, 1 => words * 2, _ => words };
        var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                ushort colour = depth switch
                {
                    0 => palette[(tim[pixels + y * words * 2 + x / 2] >> ((x & 1) * 4)) & 15],
                    1 => palette[tim[pixels + y * words * 2 + x]],
                    _ => BitConverter.ToUInt16(tim, pixels + (y * words + x) * 2),
                };
                image.SetPixel(x, y, TitleConversion.ToColor(colour, true));
            }
        return image;
    }

    /// <summary>The credits in order: each line's text, whether it is a heading, and the pixels it moves down first.</summary>
    public static JsonArray Credits(byte[] program)
    {
        var lines = new JsonArray();
        int step = 0, value = 0, moves = 0;
        for (int p = CreditsStart; p < Math.Min(CreditsEnd, program.Length);)
        {
            int b = program[p];
            if (b == 0x01 && p + 1 < program.Length) { value = program[p + 1]; if (++moves >= 2) step = value; p += 2; continue; }
            if (b is 0x02 or 0x03 && p + 1 < program.Length && program[p + 1] >= 0x20 && program[p + 1] < 0x7F)
            {
                var text = new StringBuilder();
                int q = p + 1;
                while (q < program.Length && (program[q] >= 0x20 && program[q] < 0x7F || program[q] == 0x1F))
                {
                    if (program[q] != 0x1F) text.Append((char)program[q]);
                    q++;
                }
                if (text.Length >= 2) lines.Add(new JsonObject { ["text"] = text.ToString(), ["heading"] = b == 0x03, ["step"] = step });
                // The copyright lines close the credits.
                if (text.ToString() == "ALL RIGHTS RESERVED") break;
                moves = 0;
                step = 0;
                p = q;
                continue;
            }
            p++;
        }
        return lines;
    }
}
