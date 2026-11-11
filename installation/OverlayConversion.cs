// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Godot;
using Wol.Engine.Install;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// Converts a retail full-screen overlay (a GIM file: location cards, the distant view, staff credits) into one image
/// per layer. A GIM holds up to three layers, each a grid of 64x15-pixel tiles centred on the 320x240 screen, then
/// their palettes, then pixel strips the game uploads to VRAM 15 rows at a time. A tile entry is
/// (page column + 2) &lt;&lt; 8 | strip row &lt;&lt; 2 | quarter. Layer type 0 is 4-bit with its own 16 colours;
/// types 1 and 2 are 8-bit through the first or second 256-colour palette.
/// </summary>
public sealed class OverlayConversion
{
    public const int TileWidth = 64, TileHeight = 15;
    /// <summary>BATTLE.PRG: each overlay id's file, as (sector &lt;&lt; 8 | sector count).</summary>
    public const int FileTableOffset = 0x82E08;

    private readonly string _name;
    private readonly ushort[] _words;

    public OverlayConversion(string name, byte[] gim)
    {
        _name = name;
        _words = new ushort[gim.Length / 2];
        Buffer.BlockCopy(gim, 0, _words, 0, _words.Length * 2);
    }

    /// <summary>The disc path of overlay <paramref name="id"/>, from the field engine's file table.</summary>
    public static string PathFor(Func<long, string> pathAt, byte[] battle, int id) =>
        pathAt(BitConverter.ToUInt32(battle, FileTableOffset + id * 4) >> 8);

    /// <summary>Puts each layer's image into <paramref name="images"/> and returns their definitions.</summary>
    public JsonObject Export(IDictionary<string, Image> images)
    {
        var layers = new List<(int Columns, int Rows, int Type, int Tiles)>();
        int at = 0, extra = _words[3];
        for (int layer = 0; layer <= Math.Min(2, extra); layer++)
        {
            int columns = _words[at] & 0xFF, rows = _words[at] >> 8;
            layers.Add((columns, rows, _words[at + 2], at + 4));
            at += columns * rows + 4;
        }
        at = (at + 1) & ~1;
        int palettes = at, strips = _words[1] & 0xFF, pixels = at + (_words[1] >> 8) * 16;

        // The pixel strips as the game lays them out: 17 strips of 15 rows per 64-word VRAM column.
        int columnsOfStrips = (strips + 16) / 17;
        var vram = new ushort[255, columnsOfStrips * 64];
        for (int strip = 0, source = pixels; strip < strips; strip++)
        {
            int column = strip / 17, top = strip % 17 * TileHeight;
            for (int y = 0; y < TileHeight; y++)
                for (int x = 0; x < 64; x++)
                {
                    int word = source + (column * 0x3FC0) + ((strip % 17) * TileHeight + y) * 64 + x;
                    if (word < _words.Length) vram[top + y, column * 64 + x] = _words[word];
                }
        }

        var definitions = new JsonArray();
        for (int index = 0; index < layers.Count; index++)
        {
            var (columns, rows, type, tiles) = layers[index];
            int clut = type == 0 ? palettes + index * 16 : palettes + 64 + (type == 2 ? 256 : 0);
            int imageWidth = columns * TileWidth;
            var data = new byte[imageWidth * rows * TileHeight * 4];
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                {
                    int tile = _words[tiles + row * columns + column];
                    if (tile == 0) continue;
                    int pageColumn = ((tile >> 8) & 7) - 2, v = ((tile >> 2) & 0x1F) * TileHeight, u = (tile & 3) * TileWidth;
                    if (pageColumn < 0) throw new InvalidDataException($"{_name} has a tile outside its pixels.");
                    for (int y = 0; y < TileHeight; y++)
                        for (int x = 0; x < TileWidth; x++)
                        {
                            int texel = u + x, colour;
                            if (type == 0)
                            {
                                int wordX = pageColumn * 64 + texel / 4;
                                colour = wordX < vram.GetLength(1) && v + y < 255 ? (vram[v + y, wordX] >> ((texel & 3) * 4)) & 15 : 0;
                            }
                            else
                            {
                                int wordX = pageColumn * 64 + texel / 2;
                                colour = wordX < vram.GetLength(1) && v + y < 255 ? (vram[v + y, wordX] >> ((texel & 1) * 8)) & 255 : 0;
                            }
                            ushort value = clut + colour < _words.Length ? _words[clut + colour] : (ushort)0;
                            int pixel = ((row * TileHeight + y) * imageWidth + column * TileWidth + x) * 4;
                            var shown = TitleConversion.ToColor(value, true);
                            (data[pixel], data[pixel + 1], data[pixel + 2], data[pixel + 3]) = ((byte)shown.R8, (byte)shown.G8, (byte)shown.B8, (byte)shown.A8);
                        }
                }
            images[$"{_name}.{index}"] = Image.CreateFromData(imageWidth, rows * TileHeight, false, Image.Format.Rgba8, data);
            // Colour 0 of the layer's palette fills the cells without a tile in the credits' wipe (BATTLE.PRG 0x800C8060).
            ushort fill = clut < _words.Length ? _words[clut] : (ushort)0;
            definitions.Add(new JsonObject { ["image"] = $"{_name}.{index}", ["width"] = columns * TileWidth, ["height"] = rows * TileHeight, ["fill"] = fill });
        }
        return new JsonObject { ["layers"] = definitions };
    }
}
