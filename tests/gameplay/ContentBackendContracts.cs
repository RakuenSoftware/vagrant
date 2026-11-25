// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Text;
using Wol.Engine.Imaging;
using Wol.Engine.Models;
using Wol.Games.Vagrant.Installation;
using Wol.Games.Vagrant.Runtime;

internal static class ContentBackendContracts
{
    // A deliberately renderer-free implementation. Its cache format is raw planes and a diagnostic
    // model marker, distinct from the Godot backend's formats. It is never part of the player.
    private sealed class DiagnosticBackend(string identity) : IAssetBackend
    {
        public string CacheIdentity => identity;
        public int Models, Encodes, Decodes;

        public byte[] CompileModel(RiggedModel model)
        {
            if (model.Bones.Count == 0 || model.Surfaces.Count == 0 || model.Animations.Count == 0)
                throw new InvalidOperationException("The game supplied an incomplete decoded rig.");
            Models++;
            return Encoding.UTF8.GetBytes(identity + ":decoded-model");
        }

        public byte[] EncodeImage(RasterImage image)
        {
            Encodes++;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(identity);
            writer.Write(image.Width); writer.Write(image.Height); writer.Write((int)image.Format);
            writer.Write(image.Pixels);
            return stream.ToArray();
        }

        public RasterImage DecodeImage(byte[] encoded)
        {
            Decodes++;
            using var stream = new MemoryStream(encoded, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadString() != identity) throw new InvalidDataException("Another backend's cache artifact was read.");
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            var format = (RasterFormat)reader.ReadInt32();
            return new RasterImage(width, height, format, reader.ReadBytes(checked((int)(stream.Length - stream.Position))));
        }
    }

    public static void Check(string[] arguments)
    {
        if (typeof(FieldContent).Assembly != typeof(VibrationDriver).Assembly || typeof(ActionRecord).Assembly != typeof(FieldContent).Assembly)
            throw new InvalidOperationException("Game content or action data still lives in the host assembly.");
        var backend = new DiagnosticBackend("raw-planes-a");
        var image = new RasterImage(1, 1, pixels: new byte[] { 7, 31, 127, 253 });
        if (backend.DecodeImage(backend.EncodeImage(image)).Pixel(0, 0) != new Rgba32(7, 31, 127, 253))
            throw new InvalidOperationException("The independent image backend lost the defined pixel.");
        string missing = Path.Combine(Path.GetTempPath(), "vagrant-missing-" + Guid.NewGuid().ToString("N"));
        try { _ = new FieldContent(missing, backend); throw new InvalidOperationException("Content started without a verified installation."); }
        catch (FileNotFoundException) { }
        if (backend.Models != 0) throw new InvalidOperationException("A backend bypassed installation verification.");
        byte[] bank = new byte[16];
        BitConverter.TryWriteBytes(bank.AsSpan(), 1);
        BitConverter.TryWriteBytes(bank.AsSpan(4), 12);
        if (!FieldContent.HasSound(bank, 0) || FieldContent.HasSound(bank, -1) || FieldContent.HasSound(bank, 1) ||
            FieldContent.HasSound(bank[..15], 0) || FieldContent.HasSound(Array.Empty<byte>(), 0))
            throw new InvalidOperationException("Portable sound-bank presence rules changed.");
        Console.WriteLine("CONTENT_BACKEND_CONTRACTS_OK: portable content/action ownership, independent codec, required installation and bank presence");

        if (Array.Find(arguments, a => a.StartsWith("--installation=", StringComparison.Ordinal)) is { } installed)
            CheckInstallation(installed["--installation=".Length..]);
        else if (Array.Find(arguments, a => a.StartsWith("--disc=", StringComparison.Ordinal)) is { } disc)
        {
            string root = Path.Combine(Path.GetTempPath(), "vagrant-content-backend-" + Guid.NewGuid().ToString("N"));
            try
            {
                new RetailInstallation(root).Install(disc["--disc=".Length..], _ => { });
                CheckInstallation(root);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }

    private static void CheckInstallation(string root)
    {
        var first = new DiagnosticBackend("raw-planes-a");
        var content = new FieldContent(root, first);
        var character = content.Character("character_00") ?? throw new InvalidOperationException("Ashley was not decoded.");
        if (first.Models != 1 || Encoding.UTF8.GetString(character.Model) != "raw-planes-a:decoded-model")
            throw new InvalidOperationException("The model did not pass through the selected backend.");
        var room = content.Data("field_z001_m00") ?? throw new InvalidOperationException("The first room was not decoded.");
        string texture = room["textures"]!.AsObject().First().Value!.GetValue<string>();
        var pixels = content.Image(texture) ?? throw new InvalidOperationException("The first room has no texture.");
        if (first.Encodes == 0) throw new InvalidOperationException("Room image caching bypassed the selected backend.");

        var warm = new FieldContent(root, first);
        var cached = warm.Character("character_00")!.Value;
        warm.Data("field_z001_m00");
        var decoded = warm.Image(texture)!;
        if (first.Models != 1 || first.Decodes == 0 || !decoded.Pixels.SequenceEqual(pixels.Pixels) ||
            !cached.Model.SequenceEqual(character.Model))
            throw new InvalidOperationException("Warm assets were recompiled or did not use the matching image codec.");

        string sequence = Directory.GetFiles(Path.Combine(root, "disc", "OBJ"), "00_BT?.SEQ").Order(StringComparer.Ordinal).First();
        string set = "bt" + Path.GetFileNameWithoutExtension(sequence)[5..];
        var clips = warm.AnimationSet(0, set);
        if (clips.Count == 0 || clips.Any(c => c.Animation.Length <= 0) || !ReferenceEquals(clips, warm.AnimationSet(0, set)) ||
            !ReferenceEquals(cached.Definition, warm.Character("character_00")!.Value.Definition))
            throw new InvalidOperationException("Late animation decoding lost typed clips, identity or game metadata.");

        var second = new DiagnosticBackend("raw-planes-b");
        var other = new FieldContent(root, second);
        var alternate = other.Character("character_00")!.Value;
        other.Data("field_z001_m00"); other.Image(texture);
        if (second.Models != 1 || second.Encodes == 0 || Encoding.UTF8.GetString(alternate.Model) != "raw-planes-b:decoded-model")
            throw new InvalidOperationException("Changing the backend reused incompatible cache artifacts.");
        var returning = new DiagnosticBackend("raw-planes-a");
        var original = new FieldContent(root, returning);
        original.Character("character_00"); original.Data("field_z001_m00"); original.Image(texture);
        if (returning.Models != 0 || returning.Decodes == 0)
            throw new InvalidOperationException("Switching backends discarded the other backend's compatible cache.");
        Console.WriteLine("RETAIL_CONTENT_BACKEND_CONTRACTS_OK: verified disc, portable room/character/clip decoding, cold/warm caching and backend isolation");
    }
}
