// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// The ending's packed pictures: ENDING/ILLUSTnn.BIN (06 to 16), each a compressed TIM, which ENDING.PRG unpacks
/// (0x8006AF64) as it shows them.
/// </summary>
/// <remarks>
/// The compressed stream is a run of commands: 0x00 to 0x7B copy that many plus one literal bytes; 0x7C repeats the
/// back-reference just made; 0x7D count value writes value count plus one times; 0x7E count a b writes the pair count
/// plus one times; 0x7F ends; 0x80 and up, with the byte after, copy (bits 4 to 6) plus three bytes from (the low
/// twelve bits) plus one back.
/// </remarks>
public static class EndingConversion
{
    public const int FirstIllustration = 6, LastIllustration = 16;

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
}
