// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// Translates a room's retail event script (the MPD script section) into shared-engine flows at install time.
/// The retail script runs up to four threads; each forked thread becomes its own flow, started and joined by commands
/// the field presentation carries out. Within a flow, each run of opcodes that does not wait becomes one automatic edge
/// carrying its presentation commands; an opcode that waits ends its batch with one awaited command, and the flow
/// continues when the presentation completes it. Opcode lengths come from the field engine's own length table, so every
/// opcode is stepped exactly. Commands keep retail units: map units with y down, 4096 angle units per turn, and frames
/// of the 30 Hz event clock.
/// </summary>
public sealed class EventConversion
{
    private const int LengthTableOffset = 0x81430;  // BATTLE.PRG: bytes per script opcode.
    private const int CameraPitches = 0x81400;    // BATTLE.PRG 0x800E9C00: halfword pitch presets for b7 0a.
    private const int FamilySounds = 0x80890;       // BATTLE.PRG 0x800E9090: the first resident sound of each animation set's family.

    /// <summary>Opcodes understood but not presented yet; the scene plays without their effect.</summary>
    private static readonly HashSet<int> Unpresented = new()
    {
        0x24,                                   // animation sound panning
        0xa8,                                   // reverb
        0xbb, 0xbc, 0xbe,                       // screen post-processing
        0xe3,                                   // feedback overlay rotation
        0xaa, 0xed, 0xba, 0xf3, 0x7d,           // weapon trails, lens warp, echo source, scanlines, group field
        0x98,                                   // music snapshot
        0x9c, 0xb9,                             // sound pan slides, the echo's draw depth
    };

    /// <summary>Opcodes that wait only for loading, which installed content never needs.</summary>
    private static readonly HashSet<int> LoadWaits = new() { 0x0a, 0x0c, 0x3a, 0x3b, 0x3c, 0x75, 0x86, 0x89, 0x99, 0x9d, 0x9e, 0xa4, 0xb8 };

    private readonly string _id;
    private readonly byte[] _map = Array.Empty<byte>();
    private readonly byte[] _lengths;
    private readonly int _script;
    private readonly int _end;
    private readonly JsonArray _events = new();
    private readonly JsonArray _untranslated = new();
    private readonly JsonArray _unpresented = new();
    private readonly Queue<(string Id, int Pc)> _pending = new();
    private readonly string[] _threads = new string[4];
    private readonly JsonArray _overlays = new();
    private readonly JsonArray _sounds = new();
    /// <summary>The event sound bank (an SE file) selected at each opcode, from a pass over the script in address order.</summary>
    private readonly Dictionary<int, int> _soundBank = new(), _selectedSlot = new();
    private readonly int[] _soundSlots = new int[4];
    private int _soundSelected;

    private readonly int[] _animationSlots = new int[4];
    private int _animationSelected = 1;

    /// <summary>
    /// The sound banks in each slot and the selected slot, and the event animation sets (OBJ/Ennn.ESQ ids) in each
    /// slot and the selected one, once the script has run, in address order.
    /// </summary>
    public (int[] Slots, int Selected, int[] Animations, int AnimationSelected) SoundBanks => ((int[])_soundSlots.Clone(), _soundSelected, (int[])_animationSlots.Clone(), _animationSelected);
    /// <summary>The event animation set (an OBJ/Exxx.ESQ file) selected at each opcode, in address order.</summary>
    private readonly Dictionary<int, int> _animationSet = new();
    private readonly SortedSet<int> _characters = new(), _animationSets = new(), _chains = new(), _music = new();
    private readonly JsonArray _transitions = new();

    // The flow being translated.
    private string _flow = "";
    private JsonObject _nodes = new();
    private readonly List<JsonObject> _batch = new();
    private string _node = "";

    /// <param name="data">The script's container: an MPD (whose script section offset is at byte 16) or an EVT file.</param>
    /// <param name="scriptOffset">Where the script starts in <paramref name="data"/>.</param>
    /// <param name="soundBanks">
    /// The event sound banks loaded into slots 1 to 3 and the slot selected when the script starts, and likewise the
    /// event animation sets: an event file or a room's own event keeps the banks and sets the event that started it
    /// left (EVT 0013 animates Callo from the cellar event's set without loading one).
    /// </param>
    public EventConversion(string id, byte[] data, int scriptOffset, byte[] battle, (int[] Slots, int Selected, int[] Animations, int AnimationSelected)? soundBanks = null)
    {
        _id = id;
        if (soundBanks is { } banks)
        {
            _soundSlots = (int[])banks.Slots.Clone(); _soundSelected = banks.Selected;
            _animationSlots = (int[])banks.Animations.Clone(); _animationSelected = banks.AnimationSelected;
        }
        _lengths = battle[LengthTableOffset..(LengthTableOffset + 256)];
        _ashleyFamily = battle[FamilySounds];
        for (int i = 0; i < _pitches.Length; i++) _pitches[i] = BitConverter.ToInt16(battle, CameraPitches + i * 2);
        _script = scriptOffset;
        // A room may have no script at all; its event then ends as it starts.
        _empty = scriptOffset < 0 || scriptOffset + 0x12 > data.Length;
        _end = _empty ? _script : _script + BitConverter.ToUInt16(data, _script + 2);
        _map = data;
        if (_empty) return;
        // b7 10 runs BATTLE.PRG's built-in scene reset (0x800E9B5C) and comes back after itself when the routine's
        // 48 returns. Each call gets its own copy after the script, its 48 a jump back.
        var calls = new List<int>();
        for (int pc = _script + 0x10; pc < _end && _lengths[data[pc]] != 0; pc += _lengths[data[pc]])
            if (data[pc] == 0xb7 && pc + 1 < data.Length && data[pc + 1] == 0x10) calls.Add(pc);
        if (calls.Count == 0) return;
        int routine = SceneReset, length = 0;
        while (battle[routine + length] != 0x48 && _lengths[battle[routine + length]] != 0) length += _lengths[battle[routine + length]];
        var extended = new List<byte>(data);
        _appended = data.Length;
        foreach (int call in calls)
        {
            _resets[call] = extended.Count;
            extended.AddRange(battle[routine..(routine + length)]);
            int back = call + _lengths[0xb7] - extended.Count;
            extended.AddRange(new[] { (byte)0x42, (byte)(back & 0xFF), (byte)((back >> 8) & 0xFF) });
        }
        _map = extended.ToArray();
    }

    /// <summary>
    /// The script's opcodes in address order: the script proper (up to its text table), then the scene-reset copies
    /// after the data.
    /// </summary>
    private IEnumerable<int> Code()
    {
        for (int pc = _script + 0x10; pc < _end && _lengths[_map[pc]] != 0; pc += _lengths[_map[pc]]) yield return pc;
        if (_resets.Count == 0) yield break;
        for (int pc = _appended; pc < _map.Length && _lengths[_map[pc]] != 0; pc += _lengths[_map[pc]]) yield return pc;
    }

    /// <summary>Whether an address lies in the script's code or in its scene-reset copies.</summary>
    private bool InCode(int pc) => pc >= _script + 0x10 && pc < _end || _resets.Count > 0 && pc >= _appended && pc < _map.Length;

    /// <summary>BATTLE.PRG 0x800E9B5C: the scene-reset routine b7 10 runs.</summary>
    private const int SceneReset = 0x8135C;
    /// <summary>Where each b7 10's copy of the scene reset sits after the script.</summary>
    private readonly Dictionary<int, int> _resets = new();
    private readonly int _appended;

    private readonly bool _empty;
    private readonly int _ashleyFamily;
    /// <summary>The gameplay camera's pitch presets b7 0a picks from.</summary>
    private readonly short[] _pitches = new short[8];
    private readonly JsonArray _characterSounds = new();

    private int Byte(int at) => _map[at];
    private int Signed(int at) => (sbyte)_map[at];
    private short Short(int at) => BitConverter.ToInt16(_map, at);
    private int Word(int at) => BitConverter.ToUInt16(_map, at);

    /// <summary>
    /// One string of the script's text table, which follows the opcodes: u16 offsets, counted in u16 units from the
    /// table, then the retail strings.
    /// </summary>
    private JsonObject Message(int index)
    {
        int table = _end, first = Word(table) * 2;
        if (index * 2 >= first) throw new InvalidDataException($"{_id} shows string {index}, past its text table.");
        var (text, tokens) = DialogueConversion.Decode(_map, table + Word(table + index * 2) * 2);
        return new JsonObject { ["text"] = text, ["tokens"] = tokens };
    }

    /// <summary>An animation bank: 1 is the event set selected at this opcode, 0 the character's base set.</summary>
    private string AnimationSet(int pc, int bank) => bank == 1 ? $"esq{_animationSet.GetValueOrDefault(pc):d3}" : bank == 0 ? "base" : $"bank{bank}";

    /// <summary>An actor reference: "slot:n" for a direct slot, "id:n" for the actor created with character n, or "all".</summary>
    private string Actor(int at)
    {
        int reference = Word(at);
        return (reference >> 12) switch { 1 => $"slot:{reference & 0x1F}", 0 => $"id:{reference}", _ => reference == 0x2000 ? "none" : "all" };
    }

    /// <summary>The room's flows (the main event first) and the opcodes translation did not cover.</summary>
    public JsonObject Export()
    {
        // Bank loads and selections can sit in any thread; the bank in effect follows the script's address order.
        if (_empty)
        {
            _node = Node(_script + 0x10);
            _nodes = new JsonObject();
            Finish();
            _events.Add(new JsonObject { ["id"] = _id, ["start_action"] = "start_" + _id, ["entry"] = _node, ["nodes"] = _nodes });
        }
        int[] slots = _soundSlots, animationSlots = _animationSlots;
        foreach (int pc in Code())
        {
            int op = _map[pc];
            if (op == 0x85 && Byte(pc + 2) is >= 1 and <= 3) slots[Byte(pc + 2)] = Byte(pc + 1);
            if (op == 0x88) _soundSelected = Byte(pc + 1) & 3;
            // 3a loads an event SEQ (OBJ/Ennn.ESQ; kinds 0 and 1 queue the same file, kind 1 in another load mode)
            // into a slot; 3d selects the slot the 220+ animation bank uses.
            if (op == 0x3a && ((Byte(pc + 3) >> 2) & 3) <= 1) { animationSlots[Byte(pc + 3) & 3] = Word(pc + 1); _animationSets.Add(Word(pc + 1)); }
            if (op == 0x3d) _animationSelected = Byte(pc + 1) & 3;
            _soundBank[pc] = slots[_soundSelected];
            _selectedSlot[pc] = _soundSelected;
            _animationSet[pc] = animationSlots[_animationSelected];
        }
        FindLeaders();
        if (!_empty) _pending.Enqueue((_id, _script + 0x10));
        while (_pending.Count > 0)
        {
            var (flow, pc) = _pending.Dequeue();
            TranslateFlow(flow, pc);
        }
        var characters = new JsonArray();
        foreach (int character in _characters) characters.Add(character);
        var sets = new JsonArray();
        foreach (int set in _animationSets) sets.Add(set);
        var chains = new JsonArray();
        foreach (int chain in _chains) chains.Add(chain);
        var music = new JsonArray();
        foreach (int id in _music) music.Add(id);
        return new JsonObject
        {
            ["events"] = _events, ["overlays"] = _overlays, ["sounds"] = _sounds, ["characters"] = characters, ["animation_sets"] = sets,
            ["chains"] = chains, ["music"] = music, ["transitions"] = _transitions, ["untranslated"] = _untranslated, ["unpresented"] = _unpresented,
            ["character_sounds"] = _characterSounds, ["balloons"] = Balloons(),
        };
    }

    /// <summary>
    /// The script's own balloon shapes 9 to 15. The script header's third halfword locates a block whose halfwords 1
    /// to 7 give each shape's offset in it (0 for none); as a script loads, BATTLE.PRG 0x800BEFC8 puts them in the
    /// field engine's shape table after its nine (at 0x800EB588), so a box's shape 9 to 15 is the running script's.
    /// </summary>
    private JsonObject Balloons()
    {
        var shapes = new JsonObject();
        if (_empty) return shapes;
        int block = _script + BitConverter.ToUInt16(_map, _script + 4);
        if (block + 16 > _map.Length) return shapes;
        for (int shape = 9; shape <= 15; shape++)
        {
            int offset = BitConverter.ToUInt16(_map, block + (shape - 8) * 2);
            if (offset != 0 && block + offset + 4 <= _map.Length) shapes[shape.ToString()] = DialogueConversion.Balloon(_map, block + offset);
        }
        return shapes;
    }

    // Flow opcodes: 40/41 compare a flag, 42/45/4b/4c jump, 46/47 switch on a flag, 49/4a count a loop.
    private static readonly HashSet<int> Jumps = new() { 0x40, 0x41, 0x42, 0x45, 0x47, 0x4a, 0x4b, 0x4c };
    private readonly HashSet<int> _leaders = new();
    /// <summary>Start-skippable sections: the 0d, its 0e, the fix-up that follows it, and whether skipping stops the music.</summary>
    private readonly List<(int Open, int Close, int FixUp, bool StopMusic)> _skips = new();
    private readonly HashSet<string> _started = new();
    private readonly List<JsonObject> _effects = new();
    private readonly Queue<int> _blocks = new();
    private bool _blockDone;

    /// <summary>Every jump target in the script, so that translation starts a node at each one.</summary>
    private void FindLeaders()
    {
        foreach (int pc in Code())
        {
            int op = _map[pc];
            if (op is 0x40 or 0x41 or 0x4a) _leaders.Add(op == 0x4a ? pc - Short(pc + 2) : pc + Short(pc + 4));
            else if (op is 0x42 or 0x45 or 0x4b or 0x4c) _leaders.Add(pc + Short(pc + 1));
            else if (op == 0x47) _leaders.Add(pc + Short(pc + 2));
            else if (op == 0x0e) { _leaders.Add(pc + Word(pc + 1)); _leaders.Add(pc + 3); }
            if (op == 0x0d)
            {
                // A Start-skippable section runs to its 0e, whose fix-up (from 0e's end to 0f) runs only when skipped.
                int close = pc + _lengths[op];
                while (close < _end && _lengths[_map[close]] != 0 && _map[close] != 0x0e) close += _lengths[_map[close]];
                if (close < _end && _map[close] == 0x0e) _skips.Add((pc, close, close + 3, (Byte(pc + 1) & 1) == 0));
            }
            if (op is 0x40 or 0x41 or 0x47 or 0x4a) _leaders.Add(pc + _lengths[op]);
        }
    }

    private void TranslateFlow(string flow, int entry)
    {
        _flow = flow;
        _nodes = new JsonObject();
        _started.Clear();
        _blocks.Clear();
        _blocks.Enqueue(entry);
        // The main flow carries the fix-ups that skipping jumps to.
        if (flow == _id) foreach (var skip in _skips) _blocks.Enqueue(skip.FixUp);
        while (_blocks.Count > 0)
        {
            int start = _blocks.Dequeue();
            if (!_started.Add(Node(start))) continue;
            _node = Node(start);
            _batch.Clear();
            _effects.Clear();
            TranslateBlock(start);
        }
        _events.Add(new JsonObject { ["id"] = flow, ["start_action"] = "start_" + flow, ["entry"] = Node(entry), ["nodes"] = _nodes });
    }

    /// <summary>Translates from <paramref name="pc"/> until the flow ends, jumps or reaches another block.</summary>
    private void TranslateBlock(int pc)
    {
        int steps = 0;
        while (true)
        {
            if (!InCode(pc) || ++steps > 4096) throw new InvalidDataException($"The {_id} script leaves its code at {pc - _script:x4}.");
            int op = _map[pc], length = _lengths[op];
            if (length == 0) throw new InvalidDataException($"The {_id} script has an invalid opcode {op:x2} at {pc - _script:x4}.");
            if (op is 0xff or 0xfe)
            {
                // The end of the event closes every text box and returns the camera to the gameplay view, unless
                // the event already did (e9) or chains on to another event file.
                Command("text.close", new JsonObject { ["box"] = -1 });
                Command("camera.return", new JsonObject { ["automatic"] = true });
                Finish();
                return;
            }
            if (op == 0xf6) { Finish(); return; }
            if (Jumps.Contains(op)) { Flow(pc, op); return; }
            int next = Translate(pc, op) ?? pc + length;
            if (op == 0x50) FlagOperation(pc);
            if (_blockDone) { _blockDone = false; return; }
            // Waits end their node; a jump target starts a new one.
            if (_leaders.Contains(next) || _started.Contains(Node(next)) && Node(next) != _node)
            {
                Link(next);
                return;
            }
            pc = next;
        }
    }

    private static JsonArray Flag(int index) => new() { "flags", index.ToString() };

    /// <summary>Opcode 50: flag arithmetic (add, sub, mul, div, mod, and, or, set) on an unsigned byte flag.</summary>
    private void FlagOperation(int pc)
    {
        int operation = Byte(pc + 1) >> 4, flag = ((Byte(pc + 1) & 0xF) << 6) | (Byte(pc + 2) >> 2), value = Byte(pc + 3);
        if (operation == 7) { _effects.Add(new JsonObject { ["op"] = "set", ["path"] = Flag(flag), ["value"] = value }); return; }
        string? @operator = operation switch { 0 => "add", 1 => "sub", 2 => "mul", 3 => "div", 5 => "bit_and", 6 => "bit_or", _ => null };
        if (@operator == null) { _untranslated.Add($"{pc - _script:x4} {Convert.ToHexStringLower(_map, pc, _lengths[0x50])}"); return; }
        _effects.Add(new JsonObject { ["op"] = "math", ["path"] = Flag(flag), ["operator"] = @operator, ["value"] = value, ["bits"] = 8 });
    }

    /// <summary>Ends the current node with an automatic edge to the node at <paramref name="pc"/>, translating it later.</summary>
    private void Link(int pc)
    {
        _nodes[_node] = new JsonObject { ["transitions"] = new JsonArray { Edge(Node(pc)) } };
        _blocks.Enqueue(pc);
    }

    private JsonObject Edge(string to)
    {
        var edge = new JsonObject { ["automatic"] = true, ["commands"] = Batch(), ["to"] = to };
        if (_effects.Count > 0)
        {
            var effects = new JsonArray();
            foreach (var effect in _effects) effects.Add(effect);
            _effects.Clear();
            edge["effects"] = effects;
        }
        return edge;
    }

    /// <summary>Flow opcodes end their node: a jump links on, a branch tests the world in a decision node.</summary>
    private void Flow(int pc, int op)
    {
        int length = _lengths[op];
        switch (op)
        {
            case 0x42: case 0x45: case 0x4b: case 0x4c:
                Link(pc + Short(pc + 1));
                return;
            case 0x40: case 0x41:
            {
                // Falls through when the comparison holds; jumps past the if-body when it fails.
                int test = Byte(pc + 1) >> 4, flag = ((Byte(pc + 1) & 0xF) << 6) | (Byte(pc + 2) >> 2);
                var condition = new JsonObject { ["path"] = Flag(flag) };
                if (op == 0x40) condition["value"] = Byte(pc + 3);
                else condition["value_path"] = Flag(((Byte(pc + 2) & 3) << 8) | Byte(pc + 3));
                string? comparison = test switch { 0 => "lte", 1 => "gte", 2 => "eq", 3 => "ne", 4 => "lt", 5 => "gt", 6 when op == 0x40 => "mask", _ => null };
                if (comparison == null) { Link(pc + Short(pc + 4)); return; }
                condition["op"] = comparison;
                Decide(pc, condition, pc + length, pc + Short(pc + 4));
                return;
            }
            case 0x47:
                // A case label: jumps when the switch byte (set by 46) equals the value.
                Decide(pc, new JsonObject { ["path"] = new JsonArray { "switch" }, ["op"] = "eq", ["value"] = Byte(pc + 1) }, pc + Short(pc + 2), pc + length);
                return;
            case 0x4a:
            {
                int counter = Byte(pc + 1), back = pc - Short(pc + 2);
                if (counter == 0xFF) { Link(back); return; }
                // Counts down, looping back while the counter is non-zero.
                var path = new JsonArray { "loops", counter.ToString() };
                _effects.Add(new JsonObject { ["op"] = "math", ["path"] = path, ["operator"] = "sub", ["value"] = 1, ["bits"] = 8 });
                Decide(pc, new JsonObject { ["path"] = path.DeepClone(), ["op"] = "ne", ["value"] = 0 }, back, pc + length);
                return;
            }
        }
    }

    /// <summary>Ends the node at a decision node: the first edge is taken when the condition holds, the second otherwise.</summary>
    private void Decide(int pc, JsonObject condition, int holds, int otherwise)
    {
        string decision = "b" + Node(pc)[1..];
        _nodes[_node] = new JsonObject { ["transitions"] = new JsonArray { Edge(decision) } };
        _nodes[decision] = new JsonObject
        {
            ["transitions"] = new JsonArray
            {
                new JsonObject { ["automatic"] = true, ["when"] = condition, ["to"] = Node(holds) },
                new JsonObject { ["automatic"] = true, ["to"] = Node(otherwise) },
            },
        };
        _blocks.Enqueue(holds);
        _blocks.Enqueue(otherwise);
    }

    private string Node(int pc) => $"p{pc - _script:x4}";

    private void Command(string type, JsonObject args) => _batch.Add(new JsonObject { ["type"] = type, ["args"] = args, ["await"] = false });

    /// <summary>Ends the current batch with an awaited command; the flow resumes at the next opcode when it completes.</summary>
    private void Await(int pc, string type, JsonObject args)
    {
        _batch.Add(new JsonObject { ["type"] = type, ["args"] = args, ["await"] = true });
        string waiting = "w" + Node(pc)[1..], next = Node(pc + _lengths[_map[pc]]);
        _nodes[_node] = new JsonObject { ["transitions"] = new JsonArray { Edge(waiting) } };
        var transitions = new JsonArray
        {
            new JsonObject
            {
                ["action"] = "event.complete",
                ["when"] = new JsonObject { ["scope"] = "facts", ["path"] = new JsonArray { "command_id" }, ["value_path"] = new JsonArray { "_event_pending", _flow } },
                ["to"] = next,
            },
        };
        // Inside a Start-skippable section, skipping drops the wait: the main flow goes on at the fix-up, any other
        // flow ends.
        foreach (var skip in _skips)
        {
            if (pc <= skip.Open || pc >= skip.Close) continue;
            var edge = new JsonObject { ["action"] = "event.skip", ["cancel_wait"] = true };
            if (_flow == _id)
            {
                edge["to"] = Node(skip.FixUp);
                edge["commands"] = new JsonArray { new JsonObject { ["type"] = "event.skipped", ["args"] = new JsonObject { ["stop_music"] = skip.StopMusic }, ["await"] = false } };
            }
            else edge["end"] = "complete";
            transitions.Add(edge);
            break;
        }
        _nodes[waiting] = new JsonObject { ["transitions"] = transitions };
        // The flow continues at the next opcode, unless another path has already translated it.
        if (!_started.Add(next)) _blockDone = true;
        _node = next;
    }

    private JsonArray Batch()
    {
        var commands = new JsonArray();
        foreach (var command in _batch) commands.Add(command);
        _batch.Clear();
        return commands;
    }

    private void Finish()
    {
        var edge = Edge("");
        edge.Remove("to");
        edge["end"] = "complete";
        _nodes[_node] = new JsonObject { ["transitions"] = new JsonArray { edge } };
    }

    private static JsonArray Point(int x, int y, int z) => new() { x, y, z };

    /// <summary>Translates one opcode. Returns the next opcode's address when control does not simply fall through.</summary>
    private int? Translate(int pc, int op)
    {
        switch (op)
        {
            // Threads and flow.
            case 0xf5:
            {
                int thread = Byte(pc + 1) & 3;
                string child = $"{_id}_t{thread}_{pc - _script:x4}";
                _threads[thread] = child;
                _pending.Enqueue((child, pc + 4));
                Command("thread.start", new JsonObject { ["event"] = child });
                return pc + Word(pc + 2);
            }
            case 0xf4:
            {
                string? thread = _threads[Byte(pc + 1) & 3];
                if (thread == null) return null;
                if ((Byte(pc + 1) & 0x80) != 0) Command("thread.stop", new JsonObject { ["event"] = thread });
                else Await(pc, "thread.join", new JsonObject { ["event"] = thread });
                return null;
            }
            case 0x0d:
                // A Start-skippable section opens: Start (or b7 2) skips to its fix-up.
                if (_flow == _id && _skips.Exists(skip => skip.Open == pc)) Command("skip.section", new JsonObject { ["open"] = true });
                return null;
            case 0x9a:
                // Waits until the music reaches a bar (BATTLE.PRG 0x800BA7BC: the executable's 0x80011F90, the bar
                // at +124 of the active music configuration, against the signed halfword).
                Await(pc, "music.bar", new JsonObject { ["bar"] = Short(pc + 1) });
                return null;
            case 0x0f:
                return null;    // The fix-up's end.
            case 0x0e:
                // Not skipping: the section closes and the flow jumps over the fix-up.
                if (_flow == _id) Command("skip.section", new JsonObject { ["open"] = false });
                return pc + Word(pc + 1);
            case 0xf0:
                Await(pc, "wait", new JsonObject { ["ticks"] = Math.Max(1, Byte(pc + 1)) });
                return null;

            // Text.
            case 0x10:  // Text box: (parent << 4 | box), flags (type, open speed, flip, balloon shape, tail), x, y, columns, lines.
                Command("text.box", new JsonObject
                {
                    ["box"] = Byte(pc + 1) & 15, ["flags"] = Word(pc + 2), ["x"] = Byte(pc + 4) * 2 - 32, ["y"] = Byte(pc + 5) - 32,
                    ["width"] = Byte(pc + 6) * 12 + 10, ["height"] = Byte(pc + 7) * DialogueConversion.LineHeight + 4, ["speaker"] = Actor(pc + 8),
                });
                return null;
            case 0x11:
                if (Byte(pc + 3) == 0) Await(pc, "text.show", new JsonObject { ["box"] = Byte(pc + 1), ["message"] = Message(Byte(pc + 2)) });
                else Command("text.show", new JsonObject { ["box"] = Byte(pc + 1), ["message"] = Message(Byte(pc + 2)) });
                return null;
            case 0x12: case 0x14:
                Command("text.close", new JsonObject { ["box"] = Byte(pc + 1) });
                return null;

            // Full-screen overlays: three layers positioned in pixels about the screen centre, scale 0x20 = 1.
            case 0x16:
            {
                int overlay = Word(pc + 1);
                bool listed = false;
                foreach (var known in _overlays) listed |= (int)known! == overlay;
                if (!listed) _overlays.Add(overlay);
                Command("overlay.load", new JsonObject { ["overlay"] = overlay });
                return null;
            }
            case 0x17:
                Command("overlay.set", new JsonObject
                {
                    // The scale byte's low seven bits are the scale (32 is 1; the drawer, 0x800CBBCC, masks the stored
                    // halfword to 14 bits); its top bit draws the layer at the back of the room's ordering table
                    // (0x800CCB48), behind the room, as the town seen through MAP062's window.
                    ["layer"] = Byte(pc + 1) >> 6, ["mode"] = Byte(pc + 1) & 3, ["x"] = Signed(pc + 2) * 2, ["y"] = Signed(pc + 3) * 2,
                    ["scale"] = (Byte(pc + 4) & 0x7F) / 32f, ["behind"] = (Byte(pc + 4) & 0x80) != 0, ["brightness"] = Byte(pc + 5),
                });
                return null;
            case 0x18:
                Command("overlay.move", new JsonObject
                {
                    ["layer"] = Byte(pc + 5) >> 6, ["ease"] = Byte(pc + 5) & 7, ["x"] = Signed(pc + 1) * 2, ["y"] = Signed(pc + 2) * 2,
                    ["scale"] = (Byte(pc + 3) & 0x7F) / 32f, ["brightness"] = Byte(pc + 4), ["frames"] = Math.Max(1, Byte(pc + 6)),
                });
                return null;
            case 0x1a:
                Await(pc, "overlay.wait", new JsonObject());
                return null;
            case 0x19:
                Command("overlay.free", new JsonObject());
                return null;

            // Actors.
            case 0x20:
                _characters.Add(Word(pc + 3));
                Command("actor.spawn", new JsonObject { ["actor"] = Actor(pc + 1), ["character"] = Word(pc + 3), ["tile"] = new JsonArray { Byte(pc + 5), Byte(pc + 7) } });
                return null;
            case 0x23:
                Command("actor.flags", new JsonObject { ["actor"] = Actor(pc + 1), ["flags"] = Byte(pc + 3), ["weapon"] = Byte(pc + 4) });
                return null;
            case 0x1b:
            {
                // A number for a later message to print: a flag as u8, s8, or (with the next) u16 or s16.
                int flag = Word(pc + 2) & 0x3FF;
                Command("text.integer", new JsonObject { ["slot"] = Byte(pc + 1), ["flag"] = flag, ["mode"] = Byte(pc + 3) >> 6 });
                return null;
            }
            case 0xb2:
                // The distance between two actors over a unit, into a flag (255 at most), and the bearing from the
                // first to the second, 256 a turn, into the next.
                Await(pc, "actor.measure", new JsonObject { ["actor"] = Actor(pc + 1), ["target"] = Actor(pc + 3), ["unit"] = Math.Max(1, Byte(pc + 5)), ["flag"] = Word(pc + 6), ["bearing"] = true });
                return null;
            case 0xb6:
                // The distance between two actors in whole tiles, into a flag.
                Await(pc, "actor.measure", new JsonObject { ["actor"] = Actor(pc + 1), ["target"] = Actor(pc + 3), ["unit"] = 128, ["flag"] = Word(pc + 5), ["bearing"] = false });
                return null;
            case 0x51:
            {
                // Flag arithmetic between two flags: add, sub, mul, div, mod, and, or, copy, difference, swap.
                int a = ((Byte(pc + 1) & 0xF) << 6) | (Byte(pc + 2) >> 2), b = ((Byte(pc + 2) & 3) << 8) | Byte(pc + 3);
                Await(pc, "flag.combine", new JsonObject { ["flag"] = a, ["other"] = b, ["operation"] = Byte(pc + 1) >> 4 });
                return null;
            }
            case 0x52:
                // A flag (a full 16-bit index) holds whether an item is carried, or how many of a stackable one.
                Await(pc, "flag.item", new JsonObject { ["item"] = Word(pc + 1), ["flag"] = Word(pc + 3) });
                return null;
            case 0x55:
                // 0 takes every character but Ashley out of the battle while the event runs; anything else returns them.
                Command("battle.suspend", new JsonObject { ["suspended"] = Byte(pc + 1) == 0 });
                return null;
            case 0x13:
                // A text box anchored above an actor: box, actor, flags as in 10, characters a line, lines.
                Command("text.box", new JsonObject
                {
                    ["box"] = Byte(pc + 1) & 15, ["anchor"] = Actor(pc + 2), ["speaker"] = Actor(pc + 2), ["flags"] = Word(pc + 4),
                    ["width"] = Byte(pc + 6) * 12 + 10, ["height"] = Byte(pc + 7) * DialogueConversion.LineHeight + 4, ["x"] = 0, ["y"] = 0,
                });
                return null;
            case 0x1c:
                // The player picks one of the boxes in the mask: flag 16 gets its place from the top, from 1.
                Await(pc, "text.choose", new JsonObject { ["mask"] = Byte(pc + 1) });
                return null;
            case 0x2c:
                Command("actor.face_actor", new JsonObject { ["actor"] = Actor(pc + 1), ["target"] = Actor(pc + 3), ["speed"] = Byte(pc + 5) == 0xff ? -1 : Byte(pc + 5) });
                return null;
            case 0x66:
                Command("room.swap_groups", new JsonObject { ["first"] = Byte(pc + 1), ["second"] = Byte(pc + 2) });
                return null;
            case 0x67:
                Command("tile.block", new JsonObject { ["tile"] = new JsonArray { Byte(pc + 1), Byte(pc + 3) }, ["blocked"] = Byte(pc + 4) != 0 });
                return null;
            case 0x6c:
                // The fade the next room change uses: 0, 4 and 5 black, 1 white, 2 a wipe, 3 none; levels a frame.
                Command("screen.transition", new JsonObject { ["effect"] = Byte(pc + 1), ["speed"] = Byte(pc + 2) });
                return null;
            case 0x71:
                // 1 makes the actor vanish, 2 reappear (0 is an alternate drawing mode, not shown).
                Command("actor.materialise", new JsonObject { ["actor"] = Actor(pc + 1), ["state"] = Byte(pc + 3) });
                return null;
            case 0x72:
                // If Ashley's weapon was out as the event began, she draws (1) or sheathes (0) it with animation set b2.
                Await(pc, "actor.redraw", new JsonObject { ["drawn"] = Byte(pc + 1) != 0, ["set"] = Byte(pc + 2) });
                return null;
            case 0xb7:
                switch (Byte(pc + 1))
                {
                    case 0x0a:
                    {
                        // Sets the gameplay camera's pitch from BATTLE.PRG's presets (0x8007ce74 keeps only -0x300 and
                        // -0x80; anything else is the usual -0x180).
                        int n = Byte(pc + 2), pitch = n < _pitches.Length ? _pitches[n] : 0;
                        Command("camera.pitch", new JsonObject { ["pitch"] = pitch is -0x300 or -0x80 ? pitch : -0x180 });
                        return null;
                    }
                    case 0x10:
                        // The built-in scene reset: Ashley's state, sound and music slots and the camera put back.
                        if (_resets.TryGetValue(pc, out int reset)) return reset;
                        break;
                    case 2:
                        // Skips the open Start-skippable section as Start would (the request at 0x800f4c68).
                        Command("skip.request", new JsonObject());
                        return null;
                    case 0:
                        // Loads a character's model into a slot ahead of use (0x8007c8f8): installed content needs no wait.
                        return null;
                    case 9:
                        // Hands the camera straight back to the gameplay view, with the yaw and distance saved as the event began.
                        Command("camera.return", new JsonObject { ["immediate"] = true });
                        return null;
                    case 0x11:
                        // Keeps this room off the map: the visited mark (0x800f19a0, which sets the room's bit in the
                        // visited mask at 0x8005ffd8) is cleared.
                        Command("map.unvisited", new JsonObject());
                        return null;
                    case 5:
                        // Sets (n) or clears bit 0 of every actor's flags but Ashley's (BATTLE.PRG 0x800baf6c), which
                        // with bit 1 draws it.
                        Command("actors.shown", new JsonObject { ["shown"] = Byte(pc + 2) != 0 });
                        return null;
                    case 0x0c: case 0x0e:
                        // The room's platforms: 0e n stops them all while n is set (0x800f1d6e), 0c n holds them for
                        // n frames (the count at 0x800f1d68).
                        Command("room.platforms", new JsonObject { [Byte(pc + 1) == 0x0e ? "frozen" : "hold"] = Byte(pc + 2) });
                        return null;
                    case 1:
                        // The game's end: the ending (ENDING.PRG) follows.
                        Command("game.ending", new JsonObject());
                        return null;
                    case 7:
                        // Holds the puzzle timer (its state at 0x80050468 kept aside) or lets it run on again (0).
                        Command("timer.hold", new JsonObject { ["held"] = Byte(pc + 2) != 0 });
                        return null;
                    case 3: case 4:
                        // Unlocks (3) or asks after (4) an action; flag 0xb1 reports whether it was newly learned, or is known.
                        Await(pc, "ability.learn", new JsonObject { ["action"] = Byte(pc + 2), ["learn"] = Byte(pc + 1) == 3 });
                        return null;
                }
                _unpresented.Add($"{pc - _script:x4} {Convert.ToHexStringLower(_map, pc, _lengths[op])}");
                return null;
            case 0x1e:
                // The actor collapses where it stands, playing its fall (70) unless the byte is 0.
                Command("actor.collapse", new JsonObject { ["actor"] = Actor(pc + 1), ["animate"] = Byte(pc + 3) != 0 });
                return null;
            case 0x5f:
                // Defines user easing curve 4 + slot: a base shape, after squaring the fraction (or its complement,
                // mirrored, for a negative power) that many times.
                Command("ease.define", new JsonObject { ["slot"] = Byte(pc + 1) >> 6, ["shape"] = (Byte(pc + 1) >> 4) & 3, ["power"] = (Byte(pc + 1) & 15) is var power && power > 7 ? power - 16 : power });
                return null;
            case 0xe8:
                // Anchors the camera to an actor, whose position then carries both camera points; 0x2000 releases it.
                Command("camera.anchor", new JsonObject { ["actor"] = Actor(pc + 1) });
                return null;
            case 0xdf:
                // Camera momentum (0x800BB35C): bit 0 the look-at's, bit 1 the eye's.
                Command("camera.momentum", new JsonObject { ["look_at"] = (Byte(pc + 1) & 1) != 0, ["eye"] = (Byte(pc + 1) & 2) != 0 });
                return null;
            case 0xde:
                Command("camera.stop", new JsonObject());
                return null;
            case 0x96:
                Command("music.halt", new JsonObject());
                return null;
            case 0x8f:
                // Fades the current music out and starts another slot's at a volume.
                Command("music.crossfade", new JsonObject { ["slot"] = Byte(pc + 1), ["volume"] = Byte(pc + 2) & 0x7f, ["fade_in"] = Byte(pc + 3), ["fade_out"] = Byte(pc + 4) });
                return null;
            case 0x6e:
                Await(pc, "screen.wait", new JsonObject());
                return null;
            case 0xbf:
                // An echo preset: 0 resets the echo; otherwise a soft, slightly bright after-image two pixels low.
                Command("screen.echo", new JsonObject { ["preset"] = Word(pc + 1) });
                return null;
            case 0x58:
                // The HUD: 0 hides it while the scene plays, 1 brings it back.
                Command("hud.show", new JsonObject { ["visible"] = Byte(pc + 1) != 0 });
                return null;
            case 0x5c:
            {
                // Sets the actor's weapon model (OBJ/<n>.WEP, in material 0; 0 takes it away, 0xfe gives back its own
                // weapon and shield, 0xff leaves it) and its shield model (byte 5), which is not drawn.
                int model = Byte(pc + 3);
                if (model == 0xff) return null;
                var args = new JsonObject { ["actor"] = Actor(pc + 1), ["own"] = model == 0xfe };
                if (model != 0xfe) args["model"] = model;
                Command("actor.weapon_model", args);
                return null;
            }
            case 0x5a:
                // The puzzle timer. A first byte of 0 stops it; otherwise mode byte 0 counts up from zero and 1 counts
                // down from hours, minutes and seconds (bytes 4 to 6). Byte 2 is the display's style, not read.
                Command("timer", new JsonObject
                {
                    ["running"] = Byte(pc + 1) != 0, ["count_up"] = Byte(pc + 3) == 0,
                    ["time"] = new JsonArray { Byte(pc + 4), Byte(pc + 5), Byte(pc + 6) },
                });
                return null;
            case 0x1f:
                // Writes the actor's tile (x, level, z) into three flags, which the script then tests.
                Await(pc, "actor.tile_flags", new JsonObject { ["actor"] = Actor(pc + 1), ["flag"] = Word(pc + 3) });
                return null;
            case 0x2a:
                // Walks the actor along a route to a map point (x, z); a wait on mask 1 waits for it to arrive.
                Command("actor.path", new JsonObject { ["actor"] = Actor(pc + 1), ["position"] = new JsonArray { Short(pc + 3), Short(pc + 5) } });
                return null;
            case 0x2d:
                // Turns the actor to face a tile's centre: a speed in frames a turn, 0 at once, 0xff the default.
                Command("actor.face_tile", new JsonObject { ["actor"] = Actor(pc + 1), ["tile"] = new JsonArray { Byte(pc + 3), Byte(pc + 5) }, ["speed"] = Byte(pc + 6) == 0xff ? -1 : Byte(pc + 6) });
                return null;
            case 0x3f:
                // Draws (1) or sheathes (0) the actor's weapon, or toggles it (2). For other characters this also
                // makes them hostile or passive.
                Command("actor.weapon", new JsonObject { ["actor"] = Actor(pc + 1), ["mode"] = Byte(pc + 3) });
                return null;
            case 0x57:
                // Restores the actor's hit and magic points in full, and clears RISK and status.
                Command("actor.restore", new JsonObject { ["actor"] = Actor(pc + 1) });
                return null;
            case 0xb3:
                // 0 marks the actor as defeated (out of the battle and untargetable); anything else undoes it.
                Command("actor.defeated", new JsonObject { ["actor"] = Actor(pc + 1), ["defeated"] = Byte(pc + 3) == 0 });
                return null;
            case 0x26: case 0x28:
            {
                int x = Short(pc + 3);
                var args = new JsonObject { ["actor"] = Actor(pc + 1), ["position"] = Point(x >> 1, Short(pc + 5), Short(pc + 7)), ["relative"] = (x & 1) != 0 };
                if (op == 0x26)
                {
                    args["heading"] = Byte(pc + 9) == 0xff ? -1 : Byte(pc + 9) * 16;
                    args["speed"] = Byte(pc + 10) == 0xff ? -1 : Byte(pc + 10);
                    args["turn_speed"] = Byte(pc + 11) == 0xff ? -1 : Byte(pc + 11);
                    Command("actor.walk", args);
                }
                else
                {
                    args["speed"] = Byte(pc + 9) == 0xff ? -1 : Byte(pc + 9);
                    Command("actor.glide", args);
                }
                return null;
            }
            case 0x2b:
                Command("actor.turn", new JsonObject { ["actor"] = Actor(pc + 1), ["heading"] = Byte(pc + 3) * 16, ["direction"] = Signed(pc + 4), ["speed"] = Byte(pc + 5) == 0xff ? -1 : Byte(pc + 5) });
                return null;
            case 0x33:
                Command("actor.rotate", new JsonObject { ["actor"] = Actor(pc + 1), ["relative"] = Byte(pc + 3) != 0, ["angles"] = Point(Short(pc + 4), Short(pc + 6), Short(pc + 8)), ["speed"] = Byte(pc + 10) == 0xff ? -1 : Byte(pc + 10) });
                return null;
            case 0x22:
                Command("actor.animate", new JsonObject { ["actor"] = Actor(pc + 1), ["set"] = AnimationSet(pc, Byte(pc + 5) >> 6), ["number"] = Byte(pc + 3), ["blend"] = Byte(pc + 5) & 0x3f });
                return null;
            case 0x32:  // Queue an animation to follow the current one: actor, number, unused, bank << 6 | blend.
                Command("actor.queue", new JsonObject { ["actor"] = Actor(pc + 1), ["set"] = AnimationSet(pc, Byte(pc + 5) >> 6), ["number"] = Byte(pc + 3), ["blend"] = Byte(pc + 5) & 0x3f });
                return null;
            case 0x35:  // Play an animation from part-way in: actor, number, start frame, unused, bank << 6 | blend.
                Command("actor.animate", new JsonObject
                {
                    ["actor"] = Actor(pc + 1), ["set"] = AnimationSet(pc, Byte(pc + 6) >> 6), ["number"] = Byte(pc + 3), ["blend"] = Byte(pc + 6) & 0x3f,
                    ["skip"] = Math.Max(0, Byte(pc + 4) - 1),
                });
                return null;
            case 0x25: case 0x27:
            {
                // The tile forms of 26 and 28: the target is the tile centre on its floor (a height of 0x8000).
                var args = new JsonObject { ["actor"] = Actor(pc + 1), ["position"] = Point(Byte(pc + 3) * 128 + 64, -32768, Byte(pc + 5) * 128 + 64), ["relative"] = false };
                if (op == 0x25)
                {
                    args["heading"] = Byte(pc + 6) == 0xff ? -1 : Byte(pc + 6) * 16;
                    args["speed"] = Byte(pc + 7) == 0xff ? -1 : Byte(pc + 7);
                    args["turn_speed"] = Byte(pc + 8) == 0xff ? -1 : Byte(pc + 8);
                    Command("actor.walk", args);
                }
                else
                {
                    args["speed"] = Byte(pc + 6) == 0xff ? -1 : Byte(pc + 6);
                    Command("actor.glide", args);
                }
                return null;
            }
            // The frame-buffer echo: e1 on/off, e4 scale (0x20 = 1), e5 tint (0x80 neutral), e6 offset in pixels,
            // e7 blend mode. Each of e4-e6 sets at once with 0 frames, otherwise eases over the frames.
            case 0xe1:
                Command("screen.echo", new JsonObject { ["enabled"] = Byte(pc + 1) != 0 });
                return null;
            case 0xe4:
                Command("screen.echo", new JsonObject { ["scale"] = new JsonArray { Byte(pc + 1) / 32f, Byte(pc + 2) / 32f }, ["ease"] = Byte(pc + 3), ["frames"] = Byte(pc + 4) });
                return null;
            case 0xe5:
                Command("screen.echo", new JsonObject { ["tint"] = Point(Byte(pc + 1), Byte(pc + 2), Byte(pc + 3)), ["ease"] = Byte(pc + 4), ["frames"] = Byte(pc + 5) });
                return null;
            case 0xe6:
                Command("screen.echo", new JsonObject { ["offset"] = new JsonArray { Signed(pc + 1), Signed(pc + 2) }, ["ease"] = Byte(pc + 3), ["frames"] = Byte(pc + 4) });
                return null;
            case 0xe7:
                Command("screen.echo", new JsonObject { ["mode"] = Byte(pc + 1) & 3 });
                return null;
            case 0x2f:
                Command("actor.delete", new JsonObject { ["actor"] = Actor(pc + 1) });
                return null;
            case 0x1d:  // Text box open time: box << 5 | frames.
                Command("text.duration", new JsonObject { ["box"] = Byte(pc + 1) >> 5, ["frames"] = Byte(pc + 1) & 0x1f });
                return null;
            case 0x5e:  // Wait for a time or a newly pressed button: pad mask, frames (0 waits only for a button).
                Await(pc, "wait.button", new JsonObject { ["mask"] = Byte(pc + 1), ["ticks"] = Byte(pc + 2) });
                return null;

            // Music slots (1-4) hold MUSIC/MUSICnnn.DAT sequences; 99, 9d and 9e only wait for loading.
            case 0x90:
                _music.Add(Byte(pc + 1));
                Command("music.load", new JsonObject { ["slot"] = Byte(pc + 2), ["music"] = $"music_{Byte(pc + 1):d3}" });
                return null;
            case 0x9f:
                Command("music.play", new JsonObject { ["slot"] = Byte(pc + 1), ["volume"] = 0x7f, ["fade"] = 0 });
                return null;
            case 0x93:
                Command("music.volume", new JsonObject { ["volume"] = Byte(pc + 1), ["frames"] = Byte(pc + 2) });
                return null;
            case 0x97:
                Await(pc, "music.stop", new JsonObject { ["slot"] = Byte(pc + 1) });
                return null;
            case 0x83:
                // Every sound stops (SLUS 0x80045DC0, driver command 0xf1).
                Command("sound.stop", new JsonObject { ["all"] = true });
                return null;
            case 0x84:
            {
                // SLUS 0x80045D64: id 0 stops the group's voices; otherwise the one sound, a group 3 or 4 id by its
                // program in the selected bank, a group 1 or 2 id by itself.
                int group = Byte(pc + 1), id = Byte(pc + 2);
                if (id == 0) { Command("sound.stop", new JsonObject { ["group"] = group }); return null; }
                string sound = group is 3 or 4 ? $"se{_soundBank.GetValueOrDefault(pc)}_{id}" : group == 2 ? $"resident_{id}" : group == 1 ? $"effect_{id}" : "-";
                if (sound == "-") { _unpresented.Add($"{pc - _script:x4} {Convert.ToHexStringLower(_map, pc, _lengths[op])}"); return null; }
                Command("sound.stop", new JsonObject { ["sound"] = sound });
                return null;
            }
            case 0x30:
                Await(pc, "actor.wait", new JsonObject { ["actor"] = Actor(pc + 1), ["mask"] = Word(pc + 3) });
                return null;
            case 0x29:
                Command("actor.attach", new JsonObject { ["actor"] = Actor(pc + 1), ["target"] = Actor(pc + 3), ["bone"] = Byte(pc + 5) });
                return null;
            case 0x2e:
                Command("actor.scale", new JsonObject { ["actor"] = Actor(pc + 1), ["scale"] = Point(Short(pc + 3), Short(pc + 5), Short(pc + 7)), ["frames"] = Byte(pc + 9) });
                return null;
            // Room geometry groups: 7c switches the camera-direction cut-away (0 draws every group, 1 restores it),
            // 62 hides the groups marked for first-person view, 65 and 64 hide and show one group.
            case 0x7c:
                Command("room.cutaway", new JsonObject { ["enabled"] = Byte(pc + 1) != 0 });
                return null;
            case 0x62:
                Command("room.first_person", new JsonObject { ["enabled"] = Byte(pc + 1) != 0 });
                return null;
            case 0x64: case 0x65:
                Command("room.group", new JsonObject { ["group"] = Byte(pc + 1), ["hidden"] = op == 0x65 });
                return null;
            case 0x31:
                Command("actor.tint", new JsonObject { ["actor"] = Actor(pc + 1), ["color"] = Point(Byte(pc + 3), Byte(pc + 4), Byte(pc + 5)) });
                return null;
            case 0x3d:
                return null;    // Resolved per animation command by the pre-scan.
            case 0x50:
                return null;    // Flag arithmetic becomes an effect on the node's edge.
            case 0x46:  // Switch: copies a flag (full 16-bit index) into the byte the 47 case labels test.
                _effects.Add(new JsonObject { ["op"] = "copy", ["path"] = new JsonArray { "switch" }, ["value_path"] = Flag(Word(pc + 1)) });
                return null;
            case 0x49:  // Loop counter n = c, for 4a.
                if (Byte(pc + 1) != 0xFF) _effects.Add(new JsonObject { ["op"] = "set", ["path"] = new JsonArray { "loops", Byte(pc + 1).ToString() }, ["value"] = Byte(pc + 2) });
                return null;
            case 0x43: case 0x44:
                return null;    // Labels marking the ends of branch bodies.
            case 0xf7:  // Chain an event file: it runs in this room once this event's main thread ends.
                _chains.Add(Word(pc + 1));
                Command("event.chain", new JsonObject { ["event"] = $"evt{Word(pc + 1):d4}" });
                return null;

            // Screen.
            case 0x69:
                Await(pc, "screen.fade", new JsonObject { ["direction"] = "out", ["effect"] = Byte(pc + 1), ["speed"] = Math.Max(1, Byte(pc + 2)) });
                return null;
            case 0x6a:
                Command("screen.fade", new JsonObject { ["direction"] = "in", ["effect"] = Byte(pc + 1), ["speed"] = Math.Max(1, Byte(pc + 2)) });
                return null;
            case 0x6d:
                Command("screen.display", new JsonObject { ["switches"] = Byte(pc + 1) });
                return null;

            // Camera: c0-cb move the look-at point, d0-db the eye.
            case >= 0xc0 and <= 0xdb when (op & 0x0f) <= 0x0b:
                return Camera(pc, op);
            case 0xe0:
                Await(pc, "camera.wait", new JsonObject { ["mask"] = Byte(pc + 1) });
                return null;
            case 0xe9:
            {
                // Hands the camera back to the gameplay view around Ashley now, instead of when the event ends: an
                // octant (the byte + 4, in eighths of a turn) or 0xff to keep the yaw, and 1 near, 2 far or 0 to keep
                // the distance.
                int octant = Byte(pc + 1), distance = Byte(pc + 2);
                Command("camera.return", new JsonObject { ["yaw"] = octant == 0xff ? -1 : ((octant + 4) & 7) * 512, ["distance"] = distance == 1 ? 0x600 : distance == 2 ? 0x900 : 0 });
                return null;
            }
            case 0xe2:
                Command("camera.roll", new JsonObject { ["angle"] = Short(pc + 1), ["direction"] = Byte(pc + 3) >> 4, ["ease"] = Byte(pc + 3) & 15, ["frames"] = Byte(pc + 4) });
                return null;
            case 0xea: case 0xeb: case 0xec:
                Command("camera.lens", new JsonObject { ["property"] = op == 0xea ? "projection" : op == 0xeb ? "near" : "far", ["value"] = Byte(pc + 1), ["ease"] = Byte(pc + 2), ["frames"] = Byte(pc + 3) });
                return null;
            case 0xef:
                Command("camera.shake", new JsonObject { ["cycles"] = Byte(pc + 1), ["amplitude"] = Byte(pc + 2), ["axes"] = Byte(pc + 3), ["envelope"] = Byte(pc + 4), ["frames"] = Byte(pc + 5) });
                return null;
            case 0x77:  // Weather: type and intensity, wind direction, fall strength, frames to reach them.
                Command("weather.set", new JsonObject
                {
                    ["type"] = Byte(pc + 1) >> 4, ["intensity"] = Byte(pc + 1) & 15, ["direction"] = Byte(pc + 2) * 16,
                    ["strength"] = Byte(pc + 3) * 4 + 512, ["frames"] = Byte(pc + 4),
                });
                return null;
            // Sound effects. Group 1 is a resident one-shot, group 2 the resident ambience, and group 3 the event bank in
            // the selected slot (an SE file). The group is the driver's voice set (BATTLE.PRG's masks at 0x800E9B34).
            case 0x85: case 0x88:
                return null;
            case 0x80: case 0x82:
            {
                int group = Byte(pc + 1);
                // A bank sound with no bank loaded has nothing to play.
                // Group 1 plays a resident sound once, on a voice the driver rotates through voices 1 to 6.
                // Groups 3 and 4 play from the selected bank, on voices 12 to 19 and 20 to 23.
                string sound = group is 3 or 4 && _soundBank.GetValueOrDefault(pc) != 0 ? $"se{_soundBank[pc]}_{Byte(pc + 2)}" : group == 2 ? $"resident_{Byte(pc + 2)}" : group == 1 ? $"effect_{Byte(pc + 2)}" : "";
                if (sound.Length == 0) { _unpresented.Add($"{pc - _script:x4} {Convert.ToHexStringLower(_map, pc, _lengths[op])}"); return null; }
                if (op == 0x80)
                {
                    // Each sound plays at the pan its play command gives (0x80 is centre).
                    sound += $"_p{Byte(pc + 3)}";
                    bool listed = false;
                    foreach (var known in _sounds) listed |= (string)known! == sound;
                    if (!listed) _sounds.Add(sound);
                    Command("sound.play", new JsonObject { ["sound"] = sound, ["group"] = group, ["pan"] = Byte(pc + 3), ["volume"] = Byte(pc + 4) });
                }
                else Command("sound.volume", new JsonObject { ["sound"] = sound, ["volume"] = Byte(pc + 3), ["frames"] = Byte(pc + 4) });  // by sound, whatever its pan
                return null;
            }
            case 0x70:
            {
                // Palette transforms: the high nibble of byte 1 picks actors' palettes (0, with the actor at 2) or the
                // room's (1); 2, a single colour set elsewhere, is not presented. The low nibble is the operation, byte
                // 4 gives the fade (BATTLE.PRG 0x800B9B5C passes 1 << n, kept to five bits, so from 5 it is at once)
                // and bytes 5 to 7 a signed change in 5-bit units.
                int mode = Byte(pc + 1) >> 4;
                if (mode > 1) { _unpresented.Add($"{pc - _script:x4} {Convert.ToHexStringLower(_map, pc, _lengths[op])}"); return null; }
                var args = new JsonObject
                {
                    ["scope"] = mode == 0 ? "actor" : "room", ["op"] = Byte(pc + 1) & 0xF, ["fade"] = (1 << Byte(pc + 4)) & 0x1F,
                    ["delta"] = Point(Signed(pc + 5), Signed(pc + 6), Signed(pc + 7)),
                };
                if (mode == 0) args["actor"] = Actor(pc + 2);
                Command("palette.transform", args);
                return null;
            }
            case 0x59:
                // MENUF.PRG's screens and the end of a battle (BATTLE.PRG 0x800B92F4), by the second byte: 10 to 12 a
                // cube puzzle's (state flag 0xB6 to 6, 4 or 5), 13 a time attack's start (0xB6 to 1), 14 the screen
                // 0xB6 names (0 CONGRATULATIONS, 1 becoming 2 a time attack's end), then the loot and what was
                // learned; 15 the loot and what was learned alone. The flow waits for them.
                Await(pc, "menu.results", new JsonObject { ["op"] = Byte(pc + 1) });
                return null;
            case 0x79:
                // Starts (0) or pauses and resumes (1) one of the room's animations: n below 0x40 counts sub-section
                // 11's (texture scrolls, image frames, swinging groups), 0x40 and up sub-section 12's scripts.
                Command("room.animation", new JsonObject { ["index"] = Byte(pc + 2), ["start"] = Byte(pc + 1) == 0 });
                return null;
            case 0x60:
                // Switches one of the room's platforms on or off (bit 9 of its flags, the second byte less one); a
                // platform switched off is not drawn.
                Command("room.platform", new JsonObject { ["platform"] = Byte(pc + 1), ["on"] = ((Byte(pc + 2) - 1) & 1) == 0 });
                return null;
            case 0x54:
            {
                // A sound from the actor's family: the resident sound its animation set's family starts at, plus an
                // offset. Only Ashley's scripts use it, and her animation set is 0.
                if (Actor(pc + 1) != "id:0") { _unpresented.Add($"{pc - _script:x4} {Convert.ToHexStringLower(_map, pc, _lengths[op])}"); return null; }
                string sound = $"effect_{_ashleyFamily + Byte(pc + 3)}_p128";
                bool listed = false;
                foreach (var known in _sounds) listed |= (string)known! == sound;
                if (!listed) _sounds.Add(sound);
                Command("sound.play", new JsonObject { ["sound"] = sound, ["pan"] = 128, ["volume"] = 127 });
                return null;
            }
            case 0x81:
            {
                // A sound from the actor's own bank (its model's sound section): 4 picks one of 4 to 7 at random, and 8
                // takes another path that is not followed here.
                int number = Byte(pc + 3);
                if (number == 8) { _unpresented.Add($"{pc - _script:x4} {Convert.ToHexStringLower(_map, pc, _lengths[op])}"); return null; }
                var sounds = new JsonArray();
                for (int candidate = number; candidate < (number == 4 ? 8 : number + 1); candidate++)
                {
                    sounds.Add(candidate);
                    _characterSounds.Add(new JsonObject { ["actor"] = Actor(pc + 1), ["sound"] = candidate });
                }
                Command("actor.sound", new JsonObject { ["actor"] = Actor(pc + 1), ["sounds"] = sounds });
                return null;
            }
            case 0x3e:
                // BATTLE.PRG 0x8009f940: a mode (+0x17fd), a draw offset (+0x6f4 to 0x6f6, signed, added to the actor's
                // position while it is drawn, and 0x6f7 a depth bias) and a colour (+0x6f8). The colour is used only by
                // the actor renderer's second pass (0x800b36f8); the actor's own draw keeps the room's light (or 31's
                // colour), so the opcode does not tint it. Neither the pass nor the offset is presented.
                _unpresented.Add($"{pc - _script:x4} {Convert.ToHexStringLower(_map, pc, _lengths[op])}");
                return null;
            case 0x34: case 0x39:
                // Head tracking towards a point (34: x and z; 39: x, y and z), at a speed (0 at once).
                Command("actor.look", new JsonObject
                {
                    ["actor"] = Actor(pc + 1), ["point"] = op == 0x34 ? Point(Short(pc + 3), 0, Short(pc + 5)) : Point(Short(pc + 3), Short(pc + 5), Short(pc + 7)),
                    ["speed"] = Byte(pc + (op == 0x34 ? 7 : 9)),
                });
                return null;
            case 0x38:
                // Head tracking towards another actor (0x2000 ends it), at a speed (0 at once).
                Command("actor.look", new JsonObject { ["actor"] = Actor(pc + 1), ["target"] = Actor(pc + 3), ["speed"] = Byte(pc + 5) });
                return null;
            case 0xf1:
                // Sets the frame step (0x8005e24c; 2 or 4): at 4 the game draws at 15 Hz and moves twice as far each
                // frame, so its pace is the same. The field keeps its own rate.
                return null;
            case 0x61:
                // Sets the depth (bit 15 on, low 11 bits; 0x200 by default) past which room polygons sample the
                // half-resolution copy of their textures (BATTLE.PRG patches it into 0x80097590 and 0x800978f8): a
                // level of detail. The field's mipmapped textures choose their own detail, so nothing changes here.
                return null;
            case 0xa9:
                // Shakes the controller: 0xFF stops it, other values pick a pattern (the table at 0x800f4bc0, not
                // read; here a short shake).
                Command("pad.vibrate", new JsonObject { ["pattern"] = Byte(pc + 1) == 0xFF ? -1 : Byte(pc + 1) });
                return null;
            case 0x74: case 0x76:
                // Loads the room's sky image (the variant from room sub-section 15; the disc has one a room) or
                // releases it: the sky is drawn while loaded.
                Command("sky.show", new JsonObject { ["visible"] = op == 0x74 });
                return null;
            case 0x7a:
                // Holds the ambient sound (the weather's: SLUS 0x800913bc with -1 keeps the playing one aside) or
                // brings it back (0).
                Command("sound.ambient", new JsonObject { ["held"] = Byte(pc + 1) != 0 });
                return null;
            case 0x5d:
                // The body's idle sway, over a period of twice the byte (0 stops it).
                Command("actor.sway", new JsonObject { ["actor"] = Actor(pc + 1), ["period"] = Byte(pc + 3) * 2 });
                return null;
            case 0x87:
                // Releases an event sound bank slot; releasing the selected one stops the event sounds (SLUS
                // 0x800460c0: StopSfx of -2, then of group 3's voices).
                if (_selectedSlot.GetValueOrDefault(pc) == Byte(pc + 1)) Command("sound.stop", new JsonObject { ["group"] = 3, ["release"] = true });
                return null;
            case 0x78:
                // The weather's drift (0x80091314): 0 lets its direction, intensity and angle wander within the room's
                // ranges, anything else holds them.
                Command("weather.drift", new JsonObject { ["on"] = Byte(pc + 1) == 0 });
                return null;
            case 0xa1:
                // Starts part n of the effect playing (BATTLE.PRG 0x800cef74: its script at address 3 n).
                Command("effect.part", new JsonObject { ["part"] = Byte(pc + 1) });
                return null;
            case 0xa2:
                // Waits for the effect manager to reach a state (0x800cf48c): 0 once every effect has ended; 5, ready to
                // play, which installed effects always are.
                if (Byte(pc + 1) == 0) Await(pc, "effect.wait", new JsonObject());
                return null;
            case 0xa3:
                return null;    // Nothing outside a skip (0x8007bf04 is empty).
            case 0xa5:
                // Clears every effect (0x800cf1a8) and goes on once the manager is idle.
                Command("effect.stop", new JsonObject());
                return null;
            case 0xa0:
                // Starts visual effect n at an actor (BATTLE.PRG 0x8007be20: from the point (0, 0, 0), at the actor).
                Command("effect.play", new JsonObject { ["effect"] = Word(pc + 1), ["actor"] = Actor(pc + 3) });
                return null;
            case 0xb1:
                // Marks a door marker on the room's area map as the one Ashley came in by (BATTLE.PRG 0x8008d948),
                // which the map blinks.
                Command("map.entered", new JsonObject { ["marker"] = Byte(pc + 1) });
                return null;
            case 0x53:
                // Forgets a room's characters (BATTLE.PRG 0x800b91c4: the zone enemy cache entry for zone b1, room b2
                // is cleared), so they come back whole when Ashley next enters it.
                Command("enemies.forget", new JsonObject { ["zone"] = Byte(pc + 1), ["map"] = Byte(pc + 2) });
                return null;
            case 0x91:
                // Releases a music slot, which stops its music at once (SLUS 0x80045440 by way of 0x80044df4).
                Command("music.release", new JsonObject { ["slot"] = Byte(pc + 1) });
                return null;
            case 0x92:  // Play the music loaded in a slot: slot, volume, fade time.
                Command("music.play", new JsonObject { ["slot"] = Byte(pc + 1), ["volume"] = Byte(pc + 2), ["fade"] = Byte(pc + 3) });
                return null;
            case 0x73:
                Command("sky.show", new JsonObject { ["visible"] = Byte(pc + 1) != 0 });
                return null;
            case 0x7b:
                Command("sky.offset", new JsonObject { ["x"] = Short(pc + 1), ["y"] = Short(pc + 3) });
                return null;
            case 0x7e:
                // The sky's parallax (BATTLE.PRG 0x8008EAE4): how far it scrolls for the camera's yaw and pitch, as
                // signed bytes of 128 (4096 is a screen pixel for each unit of angle).
                Command("sky.parallax", new JsonObject { ["x"] = (sbyte)Byte(pc + 1) * 128, ["y"] = (sbyte)Byte(pc + 2) * 128 });
                return null;

            case 0x68:  // Room change: zone (0 for the current zone), map index, spawn tile, floor level, facing quadrant, flags.
            {
                var transition = new JsonObject
                {
                    ["zone"] = Byte(pc + 1), ["map"] = Byte(pc + 2), ["tile"] = new JsonArray { Byte(pc + 3), Byte(pc + 5) }, ["level"] = Byte(pc + 4) & 7,
                    ["facing"] = Byte(pc + 6) * 1024, ["flags"] = Byte(pc + 7), ["effect"] = Byte(pc + 8), ["speed"] = Byte(pc + 9),
                };
                _transitions.Add(transition.DeepClone());
                Await(pc, "field.transition", transition);
                return null;
            }
        }
        if (LoadWaits.Contains(op)) return null;
        string bytes = $"{pc - _script:x4} {Convert.ToHexStringLower(_map, pc, _lengths[op])}";
        (Unpresented.Contains(op) ? _unpresented : _untranslated).Add(bytes);
        return null;
    }

    private int? Camera(int pc, int op)
    {
        string channel = (op & 0x10) == 0 ? "look_at" : "eye";
        var args = new JsonObject { ["channel"] = channel };
        switch (op & 0x0f)
        {
            case 0x0:
                args["position"] = Point(Short(pc + 1), Short(pc + 3), Short(pc + 5));
                Command("camera.stage", args);
                break;
            case 0x1:
                Command("camera.snap", args);
                break;
            case 0x2: case 0x3:
                args["mode"] = (op & 0x0f) == 2 ? "line" : "radius";
                args["ease"] = Byte(pc + 1);
                args["frames"] = Math.Max(1, Byte(pc + 2));
                Command("camera.move", args);
                break;
            case 0x4:
                args["mode"] = "orbit";
                args["direction"] = Byte(pc + 1) & 15;
                args["yaw_ease"] = Byte(pc + 1) >> 4;
                args["radius_ease"] = Byte(pc + 2) & 15;
                args["height_ease"] = Byte(pc + 2) >> 4;
                args["frames"] = Math.Max(1, Byte(pc + 3));
                Command("camera.move", args);
                break;
            case 0x5:
                args["mode"] = "turn";
                args["yaw"] = Short(pc + 1);
                args["height"] = Short(pc + 3);
                args["radius_scale"] = Byte(pc + 5);
                args["yaw_ease"] = Byte(pc + 6) & 15;
                args["radius_ease"] = Byte(pc + 6) >> 4;
                args["height_ease"] = Byte(pc + 7) & 15;
                args["frames"] = Math.Max(1, Byte(pc + 8));
                Command("camera.move", args);
                break;
            case 0x7: case 0xa:
                args["actor"] = Actor(pc + 1);
                args["bone"] = Byte(pc + 3);
                args["offset"] = Point(Short(pc + 4), Short(pc + 6), Short(pc + 8));
                args["local"] = (op & 0x0f) == 0xa;
                Command("camera.stage_bone", args);
                break;
            case 0x8:
                args["mode"] = "track";
                args["actor"] = Actor(pc + 1);
                args["bone"] = Byte(pc + 3);
                args["offset"] = Point(Short(pc + 4), Short(pc + 6), Short(pc + 8));
                args["frames"] = Math.Max(1, Byte(pc + 10));
                Command("camera.move", args);
                break;
            case 0x9:
                args["mode"] = "follow";
                args["frames"] = Math.Max(1, Byte(pc + 1));
                Command("camera.move", args);
                break;
            case 0xb:
                args["mode"] = "line";
                args["ease"] = 0;
                args["frames"] = Math.Max(1, Byte(pc + 2));
                Command("camera.move", args);
                break;
            default:
                _untranslated.Add($"{pc - _script:x4} {Convert.ToHexStringLower(_map, pc, _lengths[op])}");
                break;
        }
        return null;
    }
}
