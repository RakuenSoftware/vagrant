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
/// The state a room's scripts run against, and their compilation onto it. The 1024 retail state flags live here (story
/// commands change them, event conditions test them, the field reads and writes them); with them the switch byte
/// opcode 46 sets and 47 tests, the loop counters 4a counts down, and the skip a player has asked for.
/// <para>
/// A translated script's conditions, effects, waits and commands are bound once as it loads (BindCondition,
/// BindEffect, BindDelay, BindPresentation): each reads and writes these fields directly and its JSON is not kept.
/// </para>
/// </summary>
public sealed class EventWorld : IFlowPresentationBindings<EventWorld, EventCommand>, IFlowDelayBindings<EventWorld>
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

    /// <summary>Clears what a new script starts without: the switch, the loop counters and any skip. The flags carry on.</summary>
    public void BeginScript()
    {
        Switch = 0;
        Array.Clear(Loops);
        Skip = -1;
    }

    /// <summary>A whole number from an installed document, which may carry it as 3 or 3.0.</summary>
    private static int Whole(JsonElement value) => value.TryGetInt32(out int whole) ? whole : checked((int)value.GetDouble());

    /// <summary>A path's field: ["flags", n], ["switch"], ["loops", n] or ["skip"].</summary>
    private (Func<EventWorld, int> Read, Action<EventWorld, int>? Write) Field(JsonElement path)
    {
        string root = path[0].GetString() ?? throw new ArgumentException("An event path has no root.");
        switch (root)
        {
            case "flags" when path.GetArrayLength() == 2:
            {
                int index = int.Parse(path[1].GetString()!);
                if ((uint)index >= FlagCount) throw new ArgumentException("An event flag is out of range: " + index);
                return (world => world.Flags[index], (world, value) => world.Flags[index] = value);
            }
            case "loops" when path.GetArrayLength() == 2:
            {
                int index = int.Parse(path[1].GetString()!);
                if ((uint)index >= 256) throw new ArgumentException("An event loop counter is out of range: " + index);
                return (world => world.Loops[index], (world, value) => world.Loops[index] = value);
            }
            case "switch" when path.GetArrayLength() == 1:
                return (world => world.Switch, (world, value) => world.Switch = value);
            case "skip" when path.GetArrayLength() == 1:
                return (world => world.Skip, null);
        }
        throw new ArgumentException("An event refers to an unknown field: " + path.GetRawText());
    }

    public Predicate<EventWorld> BindCondition(JsonElement condition)
    {
        var read = Field(condition.GetProperty("path")).Read;
        string op = condition.TryGetProperty("op", out var given) ? given.GetString()! : "eq";
        Func<EventWorld, int> other;
        if (condition.TryGetProperty("value_path", out var valuePath)) other = Field(valuePath).Read;
        else { int value = Whole(condition.GetProperty("value")); other = _ => value; }
        return op switch
        {
            "eq" => world => read(world) == other(world),
            "ne" => world => read(world) != other(world),
            "lt" => world => read(world) < other(world),
            "lte" => world => read(world) <= other(world),
            "gt" => world => read(world) > other(world),
            "gte" => world => read(world) >= other(world),
            // Any bit in common (retail's test 6).
            "mask" => world => (read(world) & other(world)) != 0,
            _ => throw new ArgumentException("An event condition has an unknown comparison: " + op),
        };
    }

    public Action<EventWorld> BindEffect(JsonElement effect)
    {
        var (read, write) = Field(effect.GetProperty("path"));
        if (write == null) throw new ArgumentException("An event effect writes a read-only field.");
        string op = effect.GetProperty("op").GetString()!;
        switch (op)
        {
            case "set":
            {
                int value = Whole(effect.GetProperty("value"));
                return world => write(world, value);
            }
            case "copy":
            {
                var source = Field(effect.GetProperty("value_path")).Read;
                return world => write(world, source(world));
            }
            case "math":
            {
                int value = Whole(effect.GetProperty("value"));
                int mask = effect.TryGetProperty("bits", out var bits) ? (int)((1L << Whole(bits)) - 1) : 0xFFFF;
                string @operator = effect.GetProperty("operator").GetString()!;
                Func<int, int> apply = @operator switch
                {
                    "add" => left => left + value,
                    "sub" => left => left - value,
                    "mul" => left => left * value,
                    "div" => value == 0 ? throw new ArgumentException("An event divides by zero.") : left => left / value,
                    "bit_and" => left => left & value,
                    "bit_or" => left => left | value,
                    _ => throw new ArgumentException("An event effect has an unknown operator: " + @operator),
                };
                // The result keeps the operation's width, as an unsigned byte for the flags.
                return world => write(world, apply(read(world)) & mask);
            }
        }
        throw new ArgumentException("An event effect has an unknown operation: " + op);
    }

    public Func<EventWorld, long> BindDelay(JsonElement command)
    {
        long ticks = Whole(command.GetProperty("args").GetProperty("ticks"));
        return _ => ticks;
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
    public (FlowProgram<EventWorld, EventCommand>[] Programs, string[] Ids, string Error) Compile(Godot.Collections.Array events)
    {
        var programs = new FlowProgram<EventWorld, EventCommand>[events.Count];
        var ids = new string[events.Count];
        for (int i = 0; i < events.Count; i++)
        {
            var flow = events[i].AsGodotDictionary();
            ids[i] = flow["id"].AsString();
            try
            {
                using var document = JsonDocument.Parse(Json.Stringify(flow));
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
