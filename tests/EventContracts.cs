// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Wol.Engine.Install;
using Wol.Games.Vagrant.Installation;

namespace Wol.Games.Vagrant.Tests;

/// <summary>
/// Translates every event script on the player's disc (the EVENT files and each room's own script) and checks that
/// the shared flow runtime accepts the result, without a full installation. Argument: <c>--disc=BIN_OR_CUE</c>;
/// Rooms' local events (their door sections) are checked too. <c>--find=TEXT</c> also names the scripts whose messages hold TEXT, and <c>--dump=NAME</c> prints one's translation.
/// Reports how often each opcode was left unpresented.
/// </summary>
public partial class EventContracts : Node
{
    public override void _Ready()
    {
        string disc = "", find = "", dump = "";
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--disc=", StringComparison.Ordinal)) disc = argument["--disc=".Length..];
            // --find=TEXT names the scripts whose messages hold TEXT, to tie a scene to its script.
            if (argument.StartsWith("--find=", StringComparison.Ordinal)) find = argument["--find=".Length..];
            // --dump=NAME writes that script's translation (map034, evt0004) to the output.
            if (argument.StartsWith("--dump=", StringComparison.Ordinal)) dump = argument["--dump=".Length..];
        }
        try
        {
            if (disc.Length == 0) throw new InvalidOperationException("pass --disc=");
            using var image = PlayStationDisc.Open(disc, RetailDisc.Catalog);
            byte[] battle = image.ReadFile("BATTLE/BATTLE.PRG");
            var unpresented = new Dictionary<string, int>();
            int scripts = 0;
            var unusable = new List<string>();
            void Check(string name, byte[] data, int offset)
            {
                System.Text.Json.Nodes.JsonObject result;
                // Some files no room reaches are not scripts the translation can follow; they are listed, not failed.
                try { result = new EventConversion(name, data, offset, battle).Export(); }
                catch (System.IO.InvalidDataException error) { unusable.Add($"{name} ({error.Message})"); return; }
                catch (Exception error) when (error is not InvalidOperationException) { throw new InvalidOperationException($"{name}: {error}"); }
                if (name == dump) GD.Print("EVENT_DUMP " + result.ToJsonString());
                if (find.Length > 0 && System.Text.RegularExpressions.Regex.Unescape(result.ToJsonString()).Contains(find, StringComparison.OrdinalIgnoreCase)) GD.Print($"EVENT: \"{find}\" is in {name}");
                // Every flow compiles onto the field's event world, as the field compiles it when the event starts.
                var (_, _, invalid) = new Wol.Games.Vagrant.Scenes.Field.EventWorld().Compile(result["events"]!.AsArray());
                if (invalid.Length != 0) throw new InvalidOperationException($"{name}: {invalid}");
                if (result["untranslated"]!.AsArray().Count > 0) unusable.Add($"{name} (untranslated {result["untranslated"]![0]})");
                foreach (var entry in result["unpresented"]!.AsArray())
                {
                    string bytes = ((string)entry!).Split(' ')[1];
                    string key = bytes.StartsWith("b7", StringComparison.Ordinal) ? bytes[..4] : bytes[..2];
                    unpresented[key] = unpresented.GetValueOrDefault(key) + 1;
                }
                scripts++;
            }
            foreach (string path in RetailDisc.Catalog.FileDigests.Keys.Where(p => p.StartsWith("EVENT/", StringComparison.Ordinal) && p.EndsWith(".EVT", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal))
                Check("evt" + path[6..10], image.ReadFile(path), 0);
            foreach (string path in RetailDisc.Catalog.FileDigests.Keys.Where(p => p.StartsWith("MAP/MAP", StringComparison.Ordinal) && p.EndsWith(".MPD", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal))
            {
                byte[] mpd = image.ReadFile(path);
                if (mpd.Length < 24 || BitConverter.ToUInt32(mpd, 20) < 0x12) continue;
                Check(path[4..^4].ToLowerInvariant(), mpd, (int)BitConverter.ToUInt32(mpd, 16));
            }
            // The rooms' local events, which their conditions start: the MPD section at bytes 24 and 28 opens with a
            // u16 offset table (up to its first event), each entry an event.
            foreach (string path in RetailDisc.Catalog.FileDigests.Keys.Where(p => p.StartsWith("MAP/MAP", StringComparison.Ordinal) && p.EndsWith(".MPD", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal))
            {
                byte[] mpd = image.ReadFile(path);
                if (mpd.Length < 32) continue;
                int at = (int)BitConverter.ToUInt32(mpd, 24), length = (int)BitConverter.ToUInt32(mpd, 28);
                if (length < 2) continue;
                int first = BitConverter.ToUInt16(mpd, at);
                for (int i = 0; i * 2 < Math.Min(first, length); i++)
                {
                    int offset = BitConverter.ToUInt16(mpd, at + i * 2);
                    if (offset != 0 && offset < length) Check($"{path[4..^4].ToLowerInvariant()}_l{i}", mpd, at + offset);
                }
            }
            if (unusable.Count > 0) GD.Print($"EVENT: {unusable.Count} scripts not followed: {string.Join("; ", unusable.Take(5))}{(unusable.Count > 5 ? "; …" : "")}");
            GD.Print($"EVENT_CONTRACTS_OK: {scripts} scripts; unpresented {string.Join(", ", unpresented.OrderByDescending(e => e.Value).Select(e => $"{e.Key}×{e.Value}"))}");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr("EVENT_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
