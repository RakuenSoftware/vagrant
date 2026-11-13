// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using Wol.Engine.Flow;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>An event command as the field presents it: its type, its arguments (read once at load) and whether its flow waits for it.</summary>
public readonly record struct EventCommand(string Type, GDictionary Args, bool Awaited);

/// <summary>
/// The state a room's scripts run against: the 1024 retail state flags (story commands change them, event conditions
/// test them, the field reads and writes them), opcode 46's switch byte that 47 tests, the loop counters 4a counts
/// down, and the skip a player has asked for. Converted scripts' conditions, effects and waits bind to these through
/// the engine's state schema ("flags"/n, "switch", "loops"/n, "skip").
/// </summary>
public sealed class EventWorld : FlowStateSchema<EventWorld, EventCommand>, IFlowPresentationBindings<EventWorld, EventCommand>
{
    public const int FlagCount = 1024;

    /// <summary>The retail state flags, by index.</summary>
    public readonly int[] Flags = new int[FlagCount];

    /// <summary>Opcode 46's switch byte, which 47's case labels compare.</summary>
    public int Switch;

    /// <summary>Opcode 4a's loop counters, by number.</summary>
    public readonly int[] Loops = new int[256];

    /// <summary>The skippable section the player asked to skip, or -1. Each flow's interrupt for it takes it there.</summary>
    public int Skip = -1;

    public EventWorld()
    {
        // TITLE.PRG 0x8007137c clears game data, then 0x80071254 initializes these two flags to one
        // (0x8007130c). Flag 1 selects normal-play room actors, including the first Minotaur.
        Flags[1] = Flags[0x11C] = 1;
        IntegerSequence("flags", _ => FlagCount, (world, index) => world.Flags[index], (world, index, value) => world.Flags[index] = (int)value);
        for (int index = 0; index < FlagCount; index++) IntegerElement($"flags/{index}", "flags", index);
        IntegerSequence("loops", _ => 256, (world, index) => world.Loops[index], (world, index, value) => world.Loops[index] = (int)value);
        for (int index = 0; index < 256; index++) IntegerElement($"loops/{index}", "loops", index);
        Integer("switch", world => world.Switch, (world, value) => world.Switch = (int)value);
        ReadOnlyInteger("skip", world => world.Skip);
    }

    /// <summary>Clears what a new script starts without: the switch, the loop counters and any skip. The flags carry on.</summary>
    public void BeginScript()
    {
        Switch = 0;
        Array.Clear(Loops);
        Skip = -1;
    }

    public Func<EventWorld, EventCommand> BindPresentation(JsonElement command)
    {
        var args = Json.ParseString(command.GetProperty("args").GetRawText()).AsGodotDictionary();
        var bound = new EventCommand(command.GetProperty("type").GetString()!, args, command.TryGetProperty("await", out var awaited) && awaited.GetBoolean());
        return _ => bound;
    }

    /// <summary>
    /// Compiles an installed event file's flows (the main flow first, then its threads) onto this world, with each
    /// flow's start action ("start_" and its id). Returns them in the file's order, or an error naming the flow.
    /// </summary>
    public (FlowProgram<EventWorld, EventCommand>[] Programs, string[] Ids, string Error) Compile(System.Text.Json.Nodes.JsonArray events)
    {
        var programs = new FlowProgram<EventWorld, EventCommand>[events.Count];
        var ids = new string[events.Count];
        for (int i = 0; i < events.Count; i++)
        {
            var flow = events[i]!;
            ids[i] = (string)flow["id"]!;
            try
            {
                using var document = JsonDocument.Parse(flow.ToJsonString());
                programs[i] = FlowProgram<EventWorld, EventCommand>.Compile(document.RootElement, this);
            }
            catch (Exception error) when (error is ArgumentException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                return (Array.Empty<FlowProgram<EventWorld, EventCommand>>(), Array.Empty<string>(), $"{ids[i]}: {error.Message}");
            }
        }
        return (programs, ids, "");
    }
}
