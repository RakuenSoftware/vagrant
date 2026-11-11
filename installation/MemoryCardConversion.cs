// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// The memory card screens' content: their text (MENU/MCMAN.BIN), DATA's play time label (MENU/MENU7.PRG), the save pictures (MENU/SPMCIMG.BIN in the palettes
/// of MENU/MCDATA.BIN), the rooms' location numbers (the executable's table of zone and map pairs) and the rooms that
/// count toward MAP% (BATTLE.PRG's mask).
/// </summary>
public static class MemoryCardConversion
{
    /// <summary>Where the executable's 512 zone and map pairs start (0x8004F0CC); a room's location number is its pair's index.</summary>
    private const int Locations = 0x3F0CC + 0x800;

    /// <summary>Where BATTLE.PRG's sixteen words of rooms that count toward MAP% start (0x800E8508); 361 bits are set.</summary>
    private const int Completion = unchecked((int)(0x800E8508u - 0x80068800u));

    private const int PlayTime = 0x21C, ContainerCheck = 0x81B0;

    /// <summary>
    /// The text table (as <see cref="InventoryConversion.MenuStrings"/>): 0 to 41 the screens' words and messages, then
    /// the save locations, a save's location number plus 41 (1 Breakroom; 64 and on the containers).
    /// </summary>
    public static JsonObject Export(byte[] mcman, byte[] executable, byte[] battle, byte[] menu7)
    {
        if (executable.Length < Locations + 1024 || battle.Length < Completion + 64) throw new InvalidDataException("The memory card tables are truncated.");
        var rooms = new JsonArray();
        for (int i = 0; i < 512; i++) rooms.Add(new JsonArray { executable[Locations + i * 2], executable[Locations + i * 2 + 1] });
        var counted = new JsonArray();
        for (int i = 0; i < 16; i++) counted.Add(BitConverter.ToUInt32(battle, Completion + i * 4));
        // DATA's play time label, plain ASCII the HUD lettering prints (MENU7.PRG 0x21C, "PLAY    TIME").
        int end = Array.IndexOf(menu7, (byte)0, PlayTime);
        string label = end > PlayTime ? System.Text.Encoding.ASCII.GetString(menu7, PlayTime, end - PlayTime) : "";
        return new JsonObject { ["strings"] = InventoryConversion.MenuStrings(mcman, 0), ["rooms"] = rooms, ["counted"] = counted, ["pictures"] = "memory_card_pictures", ["play_time"] = label,
            // MENU7's question as a container is opened: "Check the container?", YES, NO, CONTAINER and its warning.
            ["container_check"] = InventoryConversion.MenuStrings(menu7, ContainerCheck) };
    }

    /// <summary>
    /// The save pictures, 448 by 256: SPMCIMG.BIN's 8-bit texels, columns 1 to 4 (64 wide each) in MCDATA.BIN's first
    /// palette and the rest in its second. Column 0 holds NEW DATA, USED, the container's chest, ERROR and NO DATA;
    /// location n - 1 (0 to 47) is the 64 by 32 cell at column 1 + (n &amp; 8) / 8 + (n &amp; 0x30) / 8, row n &amp; 7.
    /// </summary>
    public static Godot.Image Pictures(byte[] pictures, byte[] palettes)
    {
        if (pictures.Length < 448 * 256 || palettes.Length < 1024) throw new InvalidDataException("The save pictures are truncated.");
        var rgba = new byte[448 * 256 * 4];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 448; x++)
            {
                int palette = x / 64 is >= 1 and <= 4 ? 0 : 512;
                int colour = BitConverter.ToUInt16(palettes, palette + pictures[y * 448 + x] * 2), at = (y * 448 + x) * 4;
                var shown = TitleConversion.ToColor((ushort)colour, false);
                (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = ((byte)shown.R8, (byte)shown.G8, (byte)shown.B8, 255);
            }
        return Godot.Image.CreateFromData(448, 256, false, Godot.Image.Format.Rgba8, rgba);
    }
}
