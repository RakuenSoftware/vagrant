// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Wol.Engine.Models;
using Wol.Games.Vagrant.Installation;

internal static class CharacterContracts
{
    public static void Check()
    {
        // A deliberately authored SHP: one bone, one group, one triangle and a two-colour 2x2 image.
        var model = new byte[0x190];
        model[0] = (byte)'H'; model[4] = 1; model[5] = 1; model[6] = 1;
        BitConverter.TryWriteBytes(model.AsSpan(0x12C), (uint)(0x148 - 0xF8));
        BitConverter.TryWriteBytes(model.AsSpan(0x130), (uint)(0x14C - 0xF8));
        BitConverter.TryWriteBytes(model.AsSpan(0x134), (uint)(0x164 - 0xF8));
        BitConverter.TryWriteBytes(model.AsSpan(0xF4), (uint)(0x174 - 0xF8));
        model[0x138] = 128; model[0x13C] = 255;
        model[0x14A] = 3;
        model[0x154] = 128; model[0x15E] = 128;
        model[0x164] = 0x24; model[0x165] = 16;
        model[0x16A] = 4; model[0x16C] = 8;
        model[0x16E] = 1; model[0x171] = 1;
        model[0x181] = 1; model[0x182] = 1; model[0x183] = 2;
        BitConverter.TryWriteBytes(model.AsSpan(0x186), (ushort)0xFFFF);
        Array.Fill(model, (byte)1, 0x18C, 4);
        var decoder = new CharacterConversion("synthetic", model, Array.Empty<(string, byte[], int)>());
        var definition = decoder.Export();
        var rig = decoder.Model;
        if (rig.Bones.Count != 1 || rig.Bones[0].Parent != -1 || rig.Bones[0].RestPosition != Vector3.Zero ||
            Math.Abs(rig.Scale - CharacterConversion.ModelScale / MapConversion.UnitsPerMetre) > 1e-8 ||
            Vector3.Distance(Vector3.Transform(Vector3.UnitY, rig.Rotation), -Vector3.UnitY) > 1e-6)
            throw new InvalidOperationException("The decoded rig changed its root, units or coordinate space.");
        var surface = rig.Surfaces.Single();
        if (surface.Blend != RigBlend.Opaque || surface.TwoSided || surface.VertexColours || surface.Vertices.Count != 3 ||
            surface.Vertices[1].Position != new Vector3(128, 0, 0) || surface.Vertices[2].Position != new Vector3(0, 128, 0) ||
            surface.Vertices.Any(v => v.Bone != 0) || surface.Texture?.Pixel(0, 0) is not { Red: 255, Green: 255, Blue: 255, Alpha: 255 })
            throw new InvalidOperationException("The plain model lost geometry, rigid weights or texture decoding.");
        if (rig.Animations.Single() is not { Name: "rest", Animation.Loop: true } || (int)definition["weapon_bone"]! != -1)
            throw new InvalidOperationException("An unanimated prop lost its rest clip or acquired a mount.");
        model[0x166] = 0x70; // Two passes: unmasked opaque texels, then masked quarter-additive texels.
        decoder = new CharacterConversion("synthetic", model, Array.Empty<(string, byte[], int)>());
        decoder.Export();
        if (decoder.Model.Surfaces.Count != 2 || decoder.Model.Surfaces[0].Blend != RigBlend.Opaque ||
            decoder.Model.Surfaces[1].Blend != RigBlend.Add || decoder.Model.Surfaces[0].Texture!.Pixel(0, 0).Alpha != 0 ||
            decoder.Model.Surfaces[1].Texture!.Pixel(0, 0) is not { Red: 63, Green: 63, Blue: 63, Alpha: 255 })
            throw new InvalidOperationException("The model's ordered opaque/quarter-additive texture passes changed.");

        // Two authored 31-frame SEQ half-strides, with a three-tick root translation and final-frame handoff.
        const int slots = 15, stride = 14, start = 16 + 2 * stride, streams = start + slots;
        var seq = new byte[streams + 24];
        BitConverter.TryWriteBytes(seq.AsSpan(), (ushort)slots); seq[2] = 1;
        BitConverter.TryWriteBytes(seq.AsSpan(12), (uint)(start - 8));
        Array.Fill(seq, (byte)255, start, slots); seq[start + 13] = 0; seq[start + 14] = 1;
        for (int i = 0; i < 2; i++)
        {
            int h = 16 + i * stride;
            BitConverter.TryWriteBytes(seq.AsSpan(h), (ushort)31); seq[h + 2] = 255;
            BitConverter.TryWriteBytes(seq.AsSpan(h + 4), (ushort)(16 + i * 4));
            BitConverter.TryWriteBytes(seq.AsSpan(h + 10), (ushort)9);
            seq[streams + 16 + i * 4] = 31; seq[streams + 17 + i * 4] = (byte)(i == 0 ? 24 : 23);
        }
        seq[streams + 6] = 0x82; seq[streams + 7] = 2;
        var lengths = new byte[65]; lengths[23] = lengths[24] = 1;
        decoder = new CharacterConversion("synthetic", model, Array.Empty<(string, byte[], int)>()) { ScriptLengths = lengths };
        decoder.Prepare();
        var events = new JsonObject();
        var clips = decoder.Clips("base", seq, 0, events);
        if (clips.Count != 2 || clips.Any(c => c.Animation.Loop || Math.Abs(c.Animation.Length - 31 / 30.0) > 1e-6 ||
            c.Animation.Tracks[0].Positions[^1].Value != new Vector3(3, 0, 0)) ||
            (int)events["base_13"]![0]![1]! != 24 || (int)events["base_14"]![0]![1]! != 23)
            throw new InvalidOperationException("Portable animation decoding lost root motion, duration or phase handoff commands.");
        // Big-endian half-angle bases: Rx(45), Ry(90), Rz(135), composed in that order.
        seq[streams + 9] = 2; seq[streams + 11] = 4; seq[streams + 13] = 6;
        var rotated = decoder.Clips("rotated", seq, 0, new())[0].Animation.Tracks[1].Rotations[0].Value;
        if (Vector3.Distance(Vector3.Transform(Vector3.UnitX, rotated), -Vector3.UnitZ) > 1e-6 ||
            Vector3.Distance(Vector3.Transform(Vector3.UnitY, rotated), -Vector3.UnitX) > 1e-6)
            throw new InvalidOperationException("SEQ rotations changed axis order or angular units.");
        seq[2] = 2;
        if (decoder.Clips("wrong_body", seq, 0, new()).Count != 0)
            throw new InvalidOperationException("An animation set for another skeleton was accepted.");
        Console.WriteLine("CHARACTER_CONTRACTS_OK: portable geometry, weights, texture passes, coordinate space, rest clip and SEQ timing/scripts");
    }
}
