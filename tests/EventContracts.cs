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
/// Reports how often each opcode was left unpresented; <c>--opcode=XX</c> lists one opcode's uses.
/// </summary>
public partial class EventContracts : Node
{
    public override void _Ready()
    {
        string disc = "", find = "", dump = "", opcode = "";
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--disc=", StringComparison.Ordinal)) disc = argument["--disc=".Length..];
            // --find=TEXT names the scripts whose messages hold TEXT, to tie a scene to its script.
            if (argument.StartsWith("--find=", StringComparison.Ordinal)) find = argument["--find=".Length..];
            // --dump=NAME writes that script's translation (map034, evt0004) to the output.
            if (argument.StartsWith("--dump=", StringComparison.Ordinal)) dump = argument["--dump=".Length..];
            // --opcode=XX names every unpresented use of that opcode (b7 ones as b7XX), with its bytes.
            if (argument.StartsWith("--opcode=", StringComparison.Ordinal)) opcode = argument["--opcode=".Length..];
        }
        try
        {
            if (disc.Length == 0) throw new InvalidOperationException("pass --disc=");
            using var image = PlayStationDisc.Open(disc, RetailDisc.Catalog);
            byte[] battle = image.ReadFile("BATTLE/BATTLE.PRG");
            // These distinct native selectors must not collapse: 0x2003 is side 4, not Ashley or allies.
            foreach (var (reference, expected) in new[] { (0x2003, "side:4"), (0x2001, "all"), (0x0000, "id:0") })
            {
                var selector = new byte[0x16];
                BitConverter.TryWriteBytes(selector.AsSpan(2), (ushort)selector.Length);
                selector[0x10] = 0x23;
                selector[0x11] = (byte)reference;
                selector[0x12] = (byte)(reference >> 8);
                selector[0x13] = 1;
                selector[0x14] = 10;
                selector[0x15] = 0xf6;
                var converted = new EventConversion("selector_contract", selector, 0, battle).Export();
                var selectorCommands = converted["events"]!.AsArray()[0]!["nodes"]!.AsObject()
                    .SelectMany(n => n.Value!["transitions"]!.AsArray())
                    .SelectMany(t => t!["commands"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray());
                if (!selectorCommands.Any(c => c!["type"]!.GetValue<string>() == "actor.flags" && c["args"]!["actor"]!.GetValue<string>() == expected))
                    throw new InvalidOperationException($"actor selector {reference:x4} did not retain {expected}");
            }
            // Each zone's rooms by number, as MAP script names, and the zone each room is in, from the zones' MPD lists.
            var rooms = new Dictionary<(int Zone, int Room), string>();
            var zoneOf = new Dictionary<string, int>();
            foreach (string path in RetailDisc.Catalog.FileDigests.Keys.Where(p => p.StartsWith("MAP/ZONE", StringComparison.Ordinal) && p.EndsWith(".ZND", StringComparison.Ordinal)))
            {
                int zone = int.Parse(path[8..11]);
                byte[] znd = image.ReadFile(path);
                int list = BitConverter.ToInt32(znd, 0), count = BitConverter.ToInt32(znd, 4) / 8;
                for (int room = 0; room < count; room++)
                {
                    uint sector = BitConverter.ToUInt32(znd, list + room * 8);
                    if (sector == 0) continue;
                    string map = image.PathAt(sector);
                    if (!map.StartsWith("MAP/MAP", StringComparison.Ordinal)) continue;
                    string script = map[4..^4].ToLowerInvariant();
                    rooms[(zone, room)] = script;
                    zoneOf.TryAdd(script, zone);
                }
            }
            var unpresented = new Dictionary<string, int>();
            int scripts = 0;
            var unusable = new List<string>();
            // The sound banks and animation sets a script starts with, as the field hands them on: an event's chains
            // and a room's local events start with what it left. The first pass gathers them; the second checks with them.
            var parents = new Dictionary<string, (int[], int, int[], int)>();
            bool gathering = true;
            void Check(string name, byte[] data, int offset)
            {
                System.Text.Json.Nodes.JsonObject result;
                // Some files no room reaches are not scripts the translation can follow; they are listed, not failed.
                EventConversion conversion;
                try
                {
                    string parent = name.Contains("_l", StringComparison.Ordinal) ? name[..name.IndexOf("_l", StringComparison.Ordinal)] : name;
                    conversion = new EventConversion(name, data, offset, battle, parents.TryGetValue(name, out var banks) ? banks : parents.TryGetValue(parent + "_room", out var room) ? room : null);
                    result = conversion.Export();
                }
                catch (System.IO.InvalidDataException error) { unusable.Add($"{name} ({error.Message})"); return; }
                catch (Exception error) when (error is not InvalidOperationException) { throw new InvalidOperationException($"{name}: {error}"); }
                if (gathering)
                {
                    foreach (var value in result["chains"]!.AsArray()) parents.TryAdd($"evt{(int)value!:d4}", conversion.SoundBanks);
                    if (!name.Contains("_l", StringComparison.Ordinal) && name.StartsWith("map", StringComparison.Ordinal)) parents[name + "_room"] = conversion.SoundBanks;
                    // A room an event changes to starts its script with the banks the event left (zone 0: this room's zone).
                    string here = name.Contains("_l", StringComparison.Ordinal) ? name[..name.IndexOf("_l", StringComparison.Ordinal)] : name;
                    foreach (var value in result["transitions"]!.AsArray())
                    {
                        int zone = (int)value!["zone"]!;
                        if (zone == 0 && !zoneOf.TryGetValue(here, out zone)) continue;
                        if (rooms.TryGetValue((zone, (int)value["map"]!), out var room)) parents.TryAdd(room, conversion.SoundBanks);
                    }
                    return;
                }
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
                    if (key == opcode) GD.Print($"EVENT: {opcode} in {name} at {entry}");
                }
                scripts++;
            }
            // Banks pass on along chains and room changes, and on from a room's script to its local events: two
            // gathering passes carry them through, then the check.
            for (int pass = 0; pass < 3; pass++)
            {
                gathering = pass < 2;
                unusable.Clear();
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
