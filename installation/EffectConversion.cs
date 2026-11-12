// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Godot;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// A visual effect: EFFECT/Ennn.P with its textures (Ennn_k.FBT) and palette (Ennn_1.FBC). The .P file is a chain of
/// blocks, each opening with a word whose low halfword is its length (with the word) and high halfword its type; type
/// 0 ends it. Offsets inside a block count from after its word.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>1 textures: a frame-table count, the records' offset, the frame tables' offsets; a frame is a run of parts
/// (a count and a first record), a record 24 bytes: a CLUT word (bit 15 draws it semi-transparent), a texture-page
/// word (blend in bits 5 and 6, depth in 7 and 8), u, v, w, h and four corner offsets (top left, top right, bottom
/// left, bottom right) in world units.</item>
/// <item>2 sprite programs: words whose top nibble is 0 show a frame (bits 0 to 15) for a time (16 to 23, sixtieths) in
/// a layer (24 to 29), 1 offset, 2 loop, 3 stop.</item>
/// <item>3 timelines of tracks (a type, an event count and the events' offset).</item>
/// <item>4 curves: bytes, 128 a whole.</item>
/// <item>5 emitters, 204 bytes each; 8 the script; 9 the header; 10 anchors, 56 bytes each; 11 shake envelopes.</item>
/// </list>
/// Textures are read through an emulated VRAM, as the game loads them: Ennn_k.FBT at x 512 + 64 k, y 256 (E000_0
/// resident at 512), the palettes in rows 240 (E000) and up from 241 at x 768. Each texture record is cut out into one
/// atlas; a colour of 0 is transparent. E000 also gives the weapon trails their strips (BATTLE.PRG 0x800cdcbc): style
/// s's is 32 by 8 at u 0x68 + 40 (s / 3), v 0x48 + 10 (s mod 3) of the resident page, 4-bit, through palette row 240,
/// its texels across the trail from base to tip; they follow the file's records, listed as "trails".
/// </remarks>
public static class EffectConversion
{
    /// <summary>The trail styles a strip is cut for: the tens of an effect's style byte, 0 to 8 staying inside the page.</summary>
    private const int TrailStyles = 9;
    private const int VramX = 512, VramY = 256, VramWidth = 512, VramHeight = 256, ClutX = 768, ClutY = 240;

    /// <summary>
    /// The effect's own sound bank (block 12; SLUS 0x80046168 registers it), laid out as an event bank: a count, then
    /// offsets to AKAO sound programs. Timeline sounds of bank 1 play from it.
    /// </summary>
    public static byte[] SoundBank(byte[] p) => Block(p, 12);

    /// <summary>The effect's sound sequence (block 13, an AKAO block; 0x800d7aec registers it in sound slot 3), which timeline sounds of bank 2 start.</summary>
    public static byte[] SoundSequence(byte[] p) => Block(p, 13);

    /// <summary>One of an effect file's blocks by type (each a u16 length, a u16 type, then its data); empty when it has none.</summary>
    private static byte[] Block(byte[] p, int wanted)
    {
        for (int at = 0; at + 4 <= p.Length;)
        {
            int length = BitConverter.ToUInt16(p, at), type = BitConverter.ToUInt16(p, at + 2);
            if (type == 0 || length < 4) break;
            if (type == wanted) return p[(at + 4)..Math.Min(p.Length, at + length)];
            at += length;
        }
        return Array.Empty<byte>();
    }

    private const uint Overlay = 0x800F9800;            // where EFFPURGE.BIN and the PLG files load
    private const uint RendererIndices = 0x800FCE3C;    // EFFPURGE: each effect's first renderer, u16
    private const uint RendererEntries = 0x800FD15C;    // EFFPURGE: the renderers' entry points, u32

    /// <summary>
    /// The PLG renderer templates, by the shape of their entry function's code: its words with every jump target and
    /// immediate masked (so the same template compiled with other constants matches), from the entry to its return,
    /// MD5, first eight hex digits. The template names the renderer the player draws; a suffix gives its texture
    /// columns or layout.
    /// </summary>
    private static readonly Dictionary<string, string> Templates = new()
    {
        ["ebb11563"] = "grid",
        ["8e1960da"] = "closed_ring", ["6d1c5cc7"] = "closed_ring:4", ["1f897c1f"] = "closed_ring:4",
        ["a7fa7525"] = "closed_ring:tiled", ["494c0eb8"] = "closed_ring:transposed",
        ["1ffea28a"] = "ring", ["64dca2ae"] = "ring:transposed",
        ["8c70356f"] = "surface", ["4e132a9f"] = "surface:4",
        ["41ea6d8d"] = "mirrored_surface", ["44f320d5"] = "mirrored_surface:4",
        ["50ed887d"] = "rotating_surface", ["838ca626"] = "rotating_surface:4",
        ["21e59bb5"] = "mesh",
        ["c8f16fde"] = "trail:9", ["3842947f"] = "trail:5",
        ["839c4166"] = "strands", ["d46b7540"] = "strands:lines",
    };

    /// <summary>The renderers effect <paramref name="id"/>'s timeline can start, in order: a template name, or "" for none known.</summary>
    public static JsonArray Renderers(int id, byte[] purge, byte[]? plg)
    {
        var list = new JsonArray();
        int At(uint address) => (int)(address - Overlay);
        if (plg == null || plg.Length == 0 || At(RendererIndices) + (id + 2) * 2 > purge.Length) return list;
        int first = BitConverter.ToUInt16(purge, At(RendererIndices) + id * 2), last = BitConverter.ToUInt16(purge, At(RendererIndices) + (id + 1) * 2);
        for (int r = first; r < last; r++)
        {
            uint entry = BitConverter.ToUInt32(purge, At(RendererEntries) + r * 4);
            int at = entry == 0 ? -1 : At(entry);
            if (at < 0 || at + 4 > plg.Length) { list.Add(""); continue; }
            var words = new List<byte>();
            for (int i = at; i + 4 <= plg.Length; i += 4)
            {
                uint word = BitConverter.ToUInt32(plg, i), op = word >> 26;
                uint masked = op is 2 or 3 ? word & 0xFC000000 : op != 0 && op != 0x12 ? word & 0xFFFF0000 : word;
                words.AddRange(BitConverter.GetBytes(masked));
                if (word == 0x03E00008)
                {
                    if (i + 8 <= plg.Length) words.AddRange(BitConverter.GetBytes(Mask(BitConverter.ToUInt32(plg, i + 4))));
                    break;
                }
            }
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(words.ToArray()))[..8];
            list.Add(Templates.GetValueOrDefault(hash, ""));
        }
        return list;

        static uint Mask(uint word) { uint op = word >> 26; return op is 2 or 3 ? word & 0xFC000000 : op != 0 && op != 0x12 ? word & 0xFFFF0000 : word; }
    }

    /// <summary>The eight standard colours the ring and surface templates take a row's brightness from, as the PLG code keeps them.</summary>
    public static JsonArray? StandardColours(byte[]? plg)
    {
        if (plg == null) return null;
        ReadOnlySpan<byte> start = stackalloc byte[] { 255, 255, 255, 0, 128, 128, 128, 0, 255, 128, 0, 0 };
        int at = plg.AsSpan().IndexOf(start);
        if (at < 0 || at + 32 > plg.Length) return null;
        var colours = new JsonArray();
        for (int i = 0; i < 8; i++) colours.Add(new JsonArray { (int)plg[at + i * 4], (int)plg[at + i * 4 + 1], (int)plg[at + i * 4 + 2] });
        return colours;
    }

    /// <summary>
    /// Writes a PS1 colour as four bytes of RGBA: 0 is transparent; otherwise the alpha keeps the texel's STP bit (255
    /// set, 254 clear), since a semi-transparent sprite blends only the texels with it set and draws the rest opaque.
    /// </summary>
    private static void Put(byte[] data, int at, ushort colour)
    {
        var shown = TitleConversion.ToColor(colour, false);
        (data[at], data[at + 1], data[at + 2]) = ((byte)shown.R8, (byte)shown.G8, (byte)shown.B8);
        data[at + 3] = colour == 0 ? (byte)0 : (colour & 0x8000) != 0 ? (byte)255 : (byte)254;
    }

    /// <summary>Converts effect <paramref name="id"/>; the files are read through <paramref name="read"/> (null if absent).</summary>
    public static JsonObject? Export(int id, Func<string, byte[]?> read, string name, IDictionary<string, Image> output)
    {
        byte[]? p = read($"EFFECT/E{id:d3}.P");
        if (p == null) return null;
        // The VRAM the effect sees: the resident page and palette, then its own.
        var vram = new ushort[VramWidth * VramHeight];
        var clut = new ushort[256 * 8];
        void Page(byte[]? data, int slot)
        {
            if (data == null) return;
            for (int i = 0; i + 1 < data.Length && i / 128 < VramHeight; i += 2)
            {
                int row = i / 128, column = slot * 64 + (i % 128) / 2;
                if (column < VramWidth) vram[row * VramWidth + column] = BitConverter.ToUInt16(data, i);
            }
        }
        void Palette(byte[]? data, int row)
        {
            if (data == null) return;
            for (int i = 0; i + 1 < data.Length && row * 256 + i / 2 < clut.Length; i += 2) clut[row * 256 + i / 2] = BitConverter.ToUInt16(data, i);
        }
        Page(read("EFFECT/E000_0.FBT"), 0);
        Palette(read("EFFECT/E000_0.FBC"), 0);
        if (id != 0)
        {
            for (int k = 1; k < 8; k++) Page(read($"EFFECT/E{id:d3}_{k}.FBT"), k);
            Palette(read($"EFFECT/E{id:d3}_1.FBC"), 1);
        }

        var blocks = new Dictionary<int, (int At, int Length)>();
        for (int at = 0; at + 4 <= p.Length;)
        {
            int length = BitConverter.ToUInt16(p, at), type = BitConverter.ToUInt16(p, at + 2);
            if (type == 0 || length < 4) break;
            blocks[type] = (at + 4, length - 4);
            at += length;
        }
        int S16(int at) => BitConverter.ToInt16(p, at);
        int U16(int at) => BitConverter.ToUInt16(p, at);
        int S32(int at) => BitConverter.ToInt32(p, at);
        var effect = new JsonObject { ["id"] = id };

        // Textures: every record cut out into the atlas.
        var frames = new JsonArray();
        var records = new JsonArray();
        var images = new List<(byte[] Pixels, int Width, int Height)>();
        // Each record's CLUT and page words, u, v, width, height and the offset of its corners (-1 for none).
        var cuts = new List<(int ClutWord, int Page, int U, int V, int W, int H, int Corners)>();
        if (blocks.TryGetValue(1, out var textures))
        {
            int b = textures.At, tables = S16(b), recordsAt = U16(b + 2);
            int recordCount = Math.Max(0, (textures.Length - recordsAt) / 24);
            var offsets = new List<int>();
            for (int i = 0; i < tables; i++) offsets.Add(S32(b + 4 + i * 4));
            for (int i = 0; i < tables; i++)
            {
                int end = i + 1 < tables ? offsets[i + 1] : recordsAt;
                var table = new JsonArray();
                for (int at = offsets[i]; at + 4 <= end && at + 4 <= textures.Length; at += 4)
                    table.Add(new JsonArray { U16(b + at), U16(b + at + 2) });
                frames.Add(table);
            }
            for (int i = 0; i < recordCount; i++)
            {
                int r = b + recordsAt + i * 24;
                cuts.Add((U16(r), U16(r + 2), p[r + 4], p[r + 5], p[r + 6], p[r + 7], r + 8));
            }
        }
        if (id == 0)
        {
            var trails = new JsonArray();
            for (int style = 0; style < TrailStyles; style++)
            {
                trails.Add(cuts.Count);
                cuts.Add((0x3C30, 0x38, 0x68 + 40 * (style / 3), 0x48 + 10 * (style % 3), 32, 8, -1));
            }
            effect["trails"] = trails;
        }
        foreach (var (clutWord, page, u, v, w, h, cornersAt) in cuts)
        {
            int pageX = (page & 15) * 64 - VramX, pageY = ((page >> 4) & 1) * 256 - VramY, depth = (page >> 7) & 3;
            int clutColumn = (clutWord & 0x3F) * 16 - ClutX, clutRow = ((clutWord >> 6) & 0x1FF) - ClutY;
            int iw = Math.Max(1, w), ih = Math.Max(1, h);
            var image = new byte[iw * ih * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int tu = u + x, tv = v + y, row = pageY + tv;
                    if (row < 0 || row >= VramHeight) continue;
                    int index;
                    if (depth == 1)
                    {
                        int column = pageX + tu / 2;
                        if (column < 0 || column >= VramWidth) continue;
                        index = (vram[row * VramWidth + column] >> ((tu & 1) * 8)) & 0xFF;
                    }
                    else if (depth == 0)
                    {
                        int column = pageX + tu / 4;
                        if (column < 0 || column >= VramWidth) continue;
                        index = (vram[row * VramWidth + column] >> ((tu & 3) * 4)) & 0xF;
                    }
                    else
                    {
                        int column = pageX + tu;
                        if (column < 0 || column >= VramWidth) continue;
                        Put(image, (y * iw + x) * 4, vram[row * VramWidth + column]);
                        continue;
                    }
                    int entry = clutRow * 256 + clutColumn + index;
                    if (clutRow < 0 || entry < 0 || entry >= clut.Length) continue;
                    Put(image, (y * iw + x) * 4, clut[entry]);
                }
            images.Add((image, iw, ih));
            var corners = new JsonArray();
            for (int c = 0; c < 8; c++) corners.Add(cornersAt < 0 ? 0 : S16(cornersAt + c * 2));
            records.Add(new JsonObject { ["blend"] = (page >> 5) & 3, ["semi"] = (clutWord & 0x8000) != 0, ["corners"] = corners });
        }
        // Shelf-packs the records into one atlas.
        if (images.Count > 0)
        {
            const int width = 1024;
            int x = 0, y = 0, shelf = 0;
            var places = new List<(int X, int Y)>();
            foreach (var image in images)
            {
                if (x + image.Width > width) { x = 0; y += shelf + 1; shelf = 0; }
                places.Add((x, y));
                x += image.Width + 1;
                shelf = Math.Max(shelf, image.Height);
            }
            int height = Math.Max(1, y + shelf);
            var atlas = new byte[width * height * 4];
            for (int i = 0; i < images.Count; i++)
            {
                var (pixels, iw, ih) = images[i];
                for (int row = 0; row < ih; row++) Array.Copy(pixels, row * iw * 4, atlas, ((places[i].Y + row) * width + places[i].X) * 4, iw * 4);
                records[i]!["rect"] = new JsonArray { places[i].X, places[i].Y, iw, ih };
            }
            output[name + "_atlas"] = Image.CreateFromData(width, height, false, Image.Format.Rgba8, atlas);
            effect["atlas"] = name + "_atlas";
            effect["atlas_size"] = new JsonArray { width, height };
        }
        effect["frames"] = frames;
        effect["records"] = records;

        // Sprite programs: their words.
        var programs = new JsonArray();
        if (blocks.TryGetValue(2, out var programBlock))
        {
            int b = programBlock.At, count = S32(b);
            for (int i = 0; i < count; i++)
            {
                var words = new JsonArray();
                for (int at = U16(b + 4 + i * 2); at + 4 <= programBlock.Length; at += 4)
                {
                    uint word = BitConverter.ToUInt32(p, b + at);
                    words.Add((long)word);
                    if (word >> 28 is 2 or 3) break;
                }
                programs.Add(words);
            }
        }
        effect["programs"] = programs;

        // Curves.
        var curves = new JsonArray();
        if (blocks.TryGetValue(4, out var curveBlock))
        {
            int b = curveBlock.At, count = U16(b), data = b + U16(b + 2);
            for (int i = 0; i < count; i++)
            {
                int length = p[b + 4 + i];
                var curve = new JsonArray();
                for (int k = 0; k < length && data + k < b + curveBlock.Length; k++) curve.Add((int)p[data + k]);
                curves.Add(curve);
                data += length;
            }
        }
        effect["curves"] = curves;

        // Timelines: their tracks' events, each as its bytes.
        var timelines = new JsonArray();
        if (blocks.TryGetValue(3, out var timelineBlock))
        {
            int b = timelineBlock.At, count = S32(b);
            for (int i = 0; i < count; i++)
            {
                int t = b + U16(b + 4 + i * 2), trackCount = S32(t);
                var tracks = new JsonArray();
                for (int k = 0; k < trackCount; k++)
                {
                    int type = p[t + 4 + k * 4], events = p[t + 5 + k * 4], at = b + U16(t + 6 + k * 4);
                    int size = type switch { 1 or 2 or 12 => 4, 3 => 9, 5 => 2, 4 or 6 or 7 or 8 or 9 or 10 or 13 => 8, _ => 0 };
                    var list = new JsonArray();
                    for (int e = 0; size > 0 && e < events && at + (e + 1) * size <= p.Length; e++)
                    {
                        var bytes = new JsonArray();
                        for (int c = 0; c < size; c++) bytes.Add((int)p[at + e * size + c]);
                        list.Add(bytes);
                    }
                    tracks.Add(new JsonObject { ["type"] = type, ["events"] = list });
                }
                timelines.Add(tracks);
            }
        }
        effect["timelines"] = timelines;

        // Emitters: their 204 bytes, read by the player by the documented offsets.
        var emitters = new JsonArray();
        if (blocks.TryGetValue(5, out var emitterBlock))
        {
            int b = emitterBlock.At, count = S32(b);
            for (int i = 0; i < count && 4 + (i + 1) * 204 <= emitterBlock.Length; i++)
            {
                var bytes = new JsonArray();
                for (int c = 0; c < 204; c++) bytes.Add((int)p[b + 4 + i * 204 + c]);
                emitters.Add(bytes);
            }
        }
        effect["emitters"] = emitters;

        // The script, the header and the anchors.
        var script = new JsonArray();
        if (blocks.TryGetValue(8, out var scriptBlock)) for (int c = 0; c < scriptBlock.Length; c++) script.Add((int)p[scriptBlock.At + c]);
        effect["script"] = script;
        if (blocks.TryGetValue(9, out var header) && header.Length >= 0x1A)
        {
            int b = header.At;
            effect["header"] = new JsonObject
            {
                ["gravity"] = new JsonArray { S16(b), S16(b + 2), S16(b + 4), S16(b + 6), S16(b + 8), S16(b + 10) },
                ["drag"] = new JsonArray { S16(b + 12), S16(b + 14) },
                ["gravity_curve"] = (int)p[b + 0x12], ["drag_curve"] = (int)p[b + 0x13], ["parts"] = (int)p[b + 0x14], ["anchors"] = (int)p[b + 0x15],
                ["remap"] = new JsonArray { (int)p[b + 0x16], (int)p[b + 0x17], (int)p[b + 0x18], (int)p[b + 0x19] },
            };
        }
        var anchors = new JsonArray();
        if (blocks.TryGetValue(10, out var anchorBlock))
            for (int i = 0; (i + 1) * 56 <= anchorBlock.Length; i++)
            {
                int a = anchorBlock.At + i * 56;
                var triples = new JsonArray();
                for (int c = 0; c < 18; c++) triples.Add(S16(a + 0x14 + c * 2));
                anchors.Add(new JsonObject
                {
                    ["flags"] = U16(a), ["actor"] = (int)p[a + 2], ["bone"] = (int)p[a + 3], ["curves"] = new JsonArray { (int)p[a + 4], (int)p[a + 5], (int)p[a + 6], (int)p[a + 7] },
                    ["rotation"] = new JsonArray { S16(a + 8), S16(a + 0xC), S16(a + 0x10) }, ["mode"] = U16(a + 0xA), ["packed"] = U16(a + 0xE), ["shape"] = U16(a + 0x12),
                    ["transform"] = triples,
                });
            }
        effect["anchors"] = anchors;
        // Envelopes (block 11, for camera shake): a count, halfword offsets; each entry's first byte is the skip to its
        // points, (duration, value) byte pairs ending with a duration of 0 and the last value.
        var envelopes = new JsonArray();
        if (blocks.TryGetValue(11, out var envelopeBlock))
        {
            int b = envelopeBlock.At, count = S32(b);
            for (int i = 0; i < count; i++)
            {
                int at = U16(b + 4 + i * 2);
                at += p[b + at];
                var points = new JsonArray();
                while (at + 1 < envelopeBlock.Length)
                {
                    points.Add((int)p[b + at]);
                    points.Add((int)p[b + at + 1]);
                    if (p[b + at] == 0) break;
                    at += 2;
                }
                envelopes.Add(points);
            }
        }
        effect["envelopes"] = envelopes;

        // The PLG renderers (track 4 starts renderer n with an emitter record), the standard colours, and for grids
        // (which texture straight from VRAM) each page they read: page n of the effect's slots at x 512 + 64 n,
        // y 256, in four or eight bits, through palette row 240 + c.
        byte[]? plg = read($"EFFECT/PLG{id:d3}.BIN");
        byte[]? purge = read("EFFECT/EFFPURGE.BIN");
        var renderers = purge == null ? new JsonArray() : Renderers(id, purge, plg);
        effect["renderers"] = renderers;
        if (StandardColours(plg) is { } colours) effect["colours"] = colours;
        var pages = new JsonObject();
        foreach (var timeline in timelines)
            foreach (var track in timeline!.AsArray())
            {
                if ((int)track!["type"]! != 4) continue;
                foreach (var e in track["events"]!.AsArray())
                {
                    uint word = (uint)((int)e![0]! | (int)e[1]! << 8 | (int)e[2]! << 16 | (int)e[3]! << 24);
                    int renderer = (int)(word >> 18) & 7, emitter = (int)(word >> 21) & 0x3F;
                    if (renderer >= renderers.Count || (string?)renderers[renderer] != "grid" || emitter >= emitters.Count) continue;
                    var record = emitters[emitter]!.AsArray();
                    int page = (int)record[0xCA]! & 7, row = (int)record[0xCB]! & 7, deep = ((int)record[0xC7]! >> 2) & 1;
                    string key = $"{page}_{row}_{deep}";
                    if (pages.ContainsKey(key)) continue;
                    int width = deep == 1 ? 128 : 256;
                    var image = new byte[width * 256 * 4];
                    for (int y = 0; y < 256; y++)
                        for (int x = 0; x < width; x++)
                        {
                            ushort half = vram[y * VramWidth + page * 64 + (deep == 1 ? x / 2 : x / 4)];
                            int index = deep == 1 ? (half >> ((x & 1) * 8)) & 0xFF : (half >> ((x & 3) * 4)) & 0xF;
                            Put(image, (y * width + x) * 4, clut[Math.Min(clut.Length - 1, row * 256 + index)]);
                        }
                    string asset = $"{name}_page_{key}";
                    output[asset] = Image.CreateFromData(width, 256, false, Image.Format.Rgba8, image);
                    pages[key] = asset;
                }
            }
        effect["pages"] = pages;
        return effect;
    }
}
