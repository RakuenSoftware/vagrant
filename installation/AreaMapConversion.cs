// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// The map screen's data for one area: SMALL/SCENnnn.ARM, by the scene id a room's MPD gives (section B, 0x50).
/// The file holds a room count; a 12-byte record per room (a visited word, its data size, the zone and the map
/// index counting from 1); the rooms' map data one after another; then a 36-byte name record per room (a 32-byte name
/// and the previous and next room for browsing). A room's map data is six counted lists: vertices (four s16: x, y, z),
/// floor triangles and quads (vertex indices, a byte each), thick outline edges and thin lines (two vertices each),
/// and markers (a vertex, a label, flags: 0 a door, 1 the room's anchor, 2 a save point, 4 an exit to another area,
/// 8 a workshop, 0x10 a container; and a lock).
/// The area names are MENU5.PRG's string table at 0x5bfc: u16 offsets in halfwords, by scene id.
/// </summary>
public static class AreaMapConversion
{
    private const int AreaNames = 0x5BFC;

    public static JsonObject Export(byte[] arm)
    {
        if (arm.Length < 4) throw new InvalidDataException("An area map is truncated.");
        int count = BitConverter.ToInt32(arm, 0);
        var rooms = new JsonArray();
        int at = 4 + count * 12;
        if (count < 0 || at > arm.Length) throw new InvalidDataException("An area map's room list is malformed.");
        for (int i = 0; i < count; i++)
        {
            int record = 4 + i * 12, size = BitConverter.ToInt32(arm, record + 4);
            if (at + size > arm.Length) throw new InvalidDataException("An area map's room data is truncated.");
            int p = at;
            JsonArray List(int width)
            {
                int n = BitConverter.ToInt32(arm, p);
                var list = new JsonArray();
                for (int k = 0; k < n; k++)
                {
                    var entry = new JsonArray();
                    for (int b = 0; b < width; b++) entry.Add((int)arm[p + 4 + k * 4 + b]);
                    list.Add(entry);
                }
                p += 4 + n * 4;
                return list;
            }
            int vertexCount = BitConverter.ToInt32(arm, p);
            var vertices = new JsonArray();
            for (int k = 0; k < vertexCount; k++)
                vertices.Add(new JsonArray { (int)BitConverter.ToInt16(arm, p + 4 + k * 8), (int)BitConverter.ToInt16(arm, p + 6 + k * 8), (int)BitConverter.ToInt16(arm, p + 8 + k * 8) });
            p += 4 + vertexCount * 8;
            var triangles = List(3);
            var quads = List(4);
            var edges = List(2);
            var lines = List(2);
            var markers = List(4);
            rooms.Add(new JsonObject
            {
                ["zone"] = (int)BitConverter.ToInt16(arm, record + 8), ["map"] = BitConverter.ToInt16(arm, record + 10) - 1,
                ["vertices"] = vertices, ["triangles"] = triangles, ["quads"] = quads, ["edges"] = edges, ["lines"] = lines, ["markers"] = markers,
            });
            at += size;
        }
        for (int i = 0; i < count && at + (i + 1) * 36 <= arm.Length; i++)
        {
            rooms[i]!["name"] = DialogueConversion.Decode(arm, at + i * 36).Text.Trim();
            // The rooms L1 and R1 step to on the map screen.
            rooms[i]!["prev"] = (int)BitConverter.ToUInt16(arm, at + i * 36 + 32);
            rooms[i]!["next"] = (int)BitConverter.ToUInt16(arm, at + i * 36 + 34);
        }
        return new JsonObject { ["rooms"] = rooms };
    }

    /// <summary>
    /// The map screen's words (MENU/MENU5.PRG, loaded at 0x80102800): its legend in ASCII (BACK, NEXT, ROTATION,
    /// ZOOM and MENU from 0x478, JAMMING at 0x4A4), the doors' names (the string table at 0x60B0) and the connecting
    /// areas' (0x5E30, its last "not visited").
    /// </summary>
    public static JsonObject Menu(byte[] menu5)
    {
        string Ascii(int at)
        {
            int end = Array.IndexOf(menu5, (byte)0, at);
            return System.Text.Encoding.ASCII.GetString(menu5, at, (end < 0 ? menu5.Length : end) - at);
        }
        var legend = new JsonArray();
        foreach (int at in new[] { 0x478, 0x480, 0x488, 0x494, 0x49C, 0x4A4 }) legend.Add(Ascii(at));
        return new JsonObject
        {
            ["legend"] = legend, ["doors"] = InventoryConversion.MenuStrings(menu5, 0x60B0), ["connecting"] = InventoryConversion.MenuStrings(menu5, 0x5E30),
            ["backdrop"] = "map_backdrop",
        };
    }

    /// <summary>The map screen's backdrop, MENU/MAPBG.BIN: a 256-colour palette (15-bit, 512 bytes), then the 8-bit picture, 320 by 240.</summary>
    public static Godot.Image Backdrop(byte[] file)
    {
        if (file.Length < 0x200 + 320 * 240) throw new InvalidDataException("MAPBG.BIN is truncated.");
        var image = Godot.Image.CreateEmpty(320, 240, false, Godot.Image.Format.Rgba8);
        for (int i = 0; i < 320 * 240; i++)
        {
            ushort colour = BitConverter.ToUInt16(file, file[0x200 + i] * 2);
            int r = colour & 31, g = (colour >> 5) & 31, b = (colour >> 10) & 31;
            image.SetPixel(i % 320, i / 320, Godot.Color.Color8((byte)((r << 3) | (r >> 2)), (byte)((g << 3) | (g >> 2)), (byte)((b << 3) | (b >> 2))));
        }
        return image;
    }

    /// <summary>The area names, by scene id.</summary>
    public static JsonArray Names(byte[] menu)
    {
        var names = new JsonArray();
        int count = BitConverter.ToUInt16(menu, AreaNames);
        for (int i = 0; i < count; i++) names.Add(DialogueConversion.Decode(menu, AreaNames + 2 * BitConverter.ToUInt16(menu, AreaNames + i * 2)).Text.Trim());
        return names;
    }
}
