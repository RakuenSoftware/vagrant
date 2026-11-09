// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Wol.Engine.Install;
using Wol.Games.Vagrant.Installation;

namespace Wol.Games.Vagrant.Tests;

/// <summary>
/// Converts every visual effect on the player's disc into a scratch directory and checks each: blocks found, its
/// script present, every frame naming records that exist, every emitter naming a program and frame table that
/// exist. Arguments: <c>--disc=BIN_OR_CUE</c> and <c>--scratch=DIR</c> (receives the converted effects).
/// </summary>
public partial class EffectContracts : Node
{
    public override void _Ready()
    {
        string disc = "", scratch = "";
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--disc=", StringComparison.Ordinal)) disc = argument["--disc=".Length..];
            if (argument.StartsWith("--scratch=", StringComparison.Ordinal)) scratch = argument["--scratch=".Length..];
        }
        try
        {
            if (disc.Length == 0 || scratch.Length == 0) throw new InvalidOperationException("pass --disc= and --scratch=");
            Directory.CreateDirectory(scratch);
            using var image = PlayStationDisc.Open(disc, RetailDisc.Catalog);
            var images = new Dictionary<string, Image>();
            int converted = 0, textured = 0, records = 0;
            for (int id = 0; id < 320; id++)
            {
                var effect = EffectConversion.Export(id, path => RetailDisc.Catalog.FileDigests.ContainsKey(path) ? image.ReadFile(path) : null, $"effect_{id:d3}", images);
                if (effect == null) continue;
                File.WriteAllText(Path.Combine(scratch, $"effect_{id:d3}.json"), effect.ToJsonString());
                converted++;
                if (effect["script"]!.AsArray().Count == 0) throw new InvalidOperationException($"effect {id} has no script");
                int recordCount = effect["records"]!.AsArray().Count, programCount = effect["programs"]!.AsArray().Count, tableCount = effect["frames"]!.AsArray().Count;
                if (recordCount > 0) textured++;
                records += recordCount;
                foreach (var table in effect["frames"]!.AsArray())
                    foreach (var frame in table!.AsArray())
                        if ((int)frame![0]! > 0 && (int)frame[1]! + (int)frame[0]! > recordCount) throw new InvalidOperationException($"effect {id} has a frame past its records");
                foreach (var emitter in effect["emitters"]!.AsArray())
                    if (recordCount > 0 && ((int)emitter![0]! >= programCount || (int)emitter[1]! >= tableCount)) throw new InvalidOperationException($"effect {id} has an emitter past its programs or frames");
            }
            GD.Print($"EFFECT_CONTRACTS_OK: {converted} effects, {textured} textured, {records} texture records");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr("EFFECT_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
