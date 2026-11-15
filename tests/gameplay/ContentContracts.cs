// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Wol.Engine.Imaging;
using Wol.Games.Vagrant.Installation;

internal static class ContentContracts
{
    public static void Check()
    {
        if (typeof(TitleConversion).Assembly != typeof(Wol.Games.Vagrant.Runtime.VibrationDriver).Assembly)
            throw new InvalidOperationException("The decoder was compiled into a host assembly.");
        for (int word = 0; word <= ushort.MaxValue; word++)
        {
            // Independent expectations: five-bit channels expand across the full eight-bit range.
            byte r = (byte)((word & 31) * 8 + (word & 31) / 4);
            byte g = (byte)(((word >> 5) & 31) * 8 + ((word >> 5) & 31) / 4);
            byte b = (byte)(((word >> 10) & 31) * 8 + ((word >> 10) & 31) / 4);
            if (TitleConversion.ToColor((ushort)word, true) != new Rgba32(r, g, b, word == 0 ? (byte)0 : (byte)255))
                throw new InvalidOperationException("A retail palette channel or transparency key changed.");
        }
        foreach (int mask in new[] { 0, 1, 2, 3 })
        {
            var bytes = new byte[12];
            TitleConversion.WriteTexel(bytes, 0, 0, mask, 1);
            TitleConversion.WriteTexel(bytes, 4, 0x001f, mask, 1);
            TitleConversion.WriteTexel(bytes, 8, 0x801f, mask, 1);
            byte plain = mask == 3 ? (byte)0 : (byte)255;
            byte masked = mask switch { 1 => 128, 2 => 0, _ => 255 };
            if (!bytes.AsSpan().SequenceEqual(new byte[] { 0, 0, 0, 0, 255, 0, 0, plain, 255, 0, 0, masked }))
                throw new InvalidOperationException("A split transparency pass changed.");
        }
        var loading = new byte[0x39954 + 8];
        BinaryPrimitives.WriteUInt16LittleEndian(loading.AsSpan(0x39950), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(loading.AsSpan(0x39952), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(loading.AsSpan(0x39954), 0x001f);
        BinaryPrimitives.WriteUInt16LittleEndian(loading.AsSpan(0x39956), 0x03e0);
        BinaryPrimitives.WriteUInt16LittleEndian(loading.AsSpan(0x39958), 0x7c00);
        var picture = TitleConversion.Loading(loading);
        if (picture.Width != 2 || picture.Height != 2 || !picture.Pixels.AsSpan().SequenceEqual(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 0 }))
            throw new InvalidOperationException("Direct-color loading art changed.");
        var backdrop = new byte[512 + 320 * 240];
        BinaryPrimitives.WriteUInt16LittleEndian(backdrop.AsSpan(2), 0x03e0);
        backdrop[512] = 1; backdrop[^1] = 1;
        var map = AreaMapConversion.Backdrop(backdrop);
        if (map.Width != 320 || map.Height != 240 || map.Pixel(0, 0) != new Rgba32(0, 255, 0) || map.Pixel(319, 239) != new Rgba32(0, 255, 0) || map.Pixel(1, 0) != new Rgba32(0, 0, 0))
            throw new InvalidOperationException("Indexed map art changed.");
        // Distinct palettes on either side of the save-picture column boundaries.
        var palettes = new byte[1024];
        BinaryPrimitives.WriteUInt16LittleEndian(palettes, 0x001f);
        BinaryPrimitives.WriteUInt16LittleEndian(palettes.AsSpan(512), 0x7c00);
        var saves = MemoryCardConversion.Pictures(new byte[448 * 256], palettes);
        foreach (int x in new[] { 0, 63, 64, 319, 320, 447 })
            if (saves.Pixel(x, 255) != (x >= 64 && x < 320 ? new Rgba32(255, 0, 0) : new Rgba32(0, 0, 255)))
                throw new InvalidOperationException("Save-picture palette columns changed.");
        // Four-bit font cells retain index-zero transparency and independent glyph advances.
        var system = new byte[0xe000 + 158 * 128];
        var battle = new byte[0x83010 + DialogueConversion.Glyphs * 2];
        system[108 * 128] = 0x10;
        BinaryPrimitives.WriteUInt16LittleEndian(system.AsSpan(222 * 128 + 2), 31);
        battle[0x83010 + DialogueConversion.Glyphs] = 7;
        var images = new Dictionary<string, RasterImage>();
        var definition = new DialogueConversion(system, battle).Export(images);
        if (images["dialogue_font_image"].Pixel(0, 0).Alpha != 0 || images["dialogue_font_image"].Pixel(1, 0) != new Rgba32(255, 255, 255)
            || (int)definition["glyphs"]!["48"]!["advance"]! != 7)
            throw new InvalidOperationException("Font indices, transparency or layout metrics changed.");
        foreach (byte[] truncated in new[] { Array.Empty<byte>(), new byte[512] })
        {
            bool rejected = false;
            try { _ = AreaMapConversion.Backdrop(truncated); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Truncated indexed art was accepted.");
        }
        var eventTables = new byte[0x81530];
        eventTables[0x81430 + 0x23] = 5;
        eventTables[0x81430 + 0xf6] = 1;
        foreach (var (reference, expected) in new[] { (0x2003, "side:4"), (0x2001, "all"), (0, "id:0") })
        {
            var script = new byte[0x16];
            BinaryPrimitives.WriteUInt16LittleEndian(script.AsSpan(2), (ushort)script.Length);
            script[0x10] = 0x23;
            BinaryPrimitives.WriteUInt16LittleEndian(script.AsSpan(0x11), (ushort)reference);
            script[0x13] = 1; script[0x14] = 10; script[0x15] = 0xf6;
            var converted = new EventConversion("portable_selector", script, 0, eventTables).Export();
            var commands = converted["events"]!.AsArray()[0]!["nodes"]!.AsObject()
                .SelectMany(n => n.Value!["transitions"]!.AsArray())
                .SelectMany(t => t!["commands"]?.AsArray() ?? new JsonArray());
            if (!commands.Any(c => (string)c!["type"]! == "actor.flags" && (string)c["args"]!["actor"]! == expected))
                throw new InvalidOperationException("A retail event actor selector changed.");
        }
        if (EventConversion.FramesPerSecond != 30 || EventConversion.TicksPerFrame != 1 || Math.Abs(EventConversion.Heading(1024) + Math.PI / 2) > 0.000001)
            throw new InvalidOperationException("The event clock or heading units changed.");
        Console.WriteLine("CONTENT_CONTRACTS_OK: full palette space, transparency passes, direct/indexed art, save-picture columns, font metrics and event selectors without a renderer");
    }
}
