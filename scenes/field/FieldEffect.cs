// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// A visual effect playing (BATTLE.PRG's effect manager, 0x800CE9B0): the installed effect's script runs one
/// instance per part and per child, each tick (30 Hz) until it yields; its timeline fires emitters and children; its
/// particles move by the effect's physics and are drawn as camera-facing quads of the effect's texture records,
/// blended as each record says. Retail positions are map units (y down); particles keep 20.12 fixed point.
/// </summary>
/// <remarks>
/// The PLG renderers are in FieldEffectRenderers.cs. Not yet followed: the common battle sounds and effect sequences, rumble, bystanders' colour shifts, camera moves,
/// and the exact spline control points.
/// </remarks>
public sealed partial class FieldEffect : Node3D
{
    /// <summary>What an effect is aimed from or at: an actor (at a bone, or its middle) or a point.</summary>
    public sealed class Entity
    {
        public EventActor? Actor;
        public Vector3 Point;
        public int Bone = -1;

        public Vector3 Position(int bone = -1)
        {
            if (Actor == null) return Point;
            int b = bone >= 0 ? bone : Bone;
            // An actor taken whole stands at its root bone (the hips), which goes with it when it lies down (the dying
            // soldier of E232, MAP010); one without a skeleton, 150 above its feet.
            return b >= 0 ? Actor.BonePosition(b) : Actor.Skeleton != null ? Actor.BonePosition(0) : Actor.Position + new Vector3(0, -150, 0);
        }
    }

    private sealed class Anchor
    {
        public Vector3 Translation, From, To, Control;
        // The anchor's turn: an actor's bone's (or its facing alone), which local particles are placed by.
        public Basis Basis = Basis.Identity;
        public bool Path, Spline, Started;
        public int Tick, Limit, Ease, Curve;
        /// <summary>The actor and bone it is placed on (the record's +52 and +53 in BATTLE.PRG), which a trail is drawn from.</summary>
        public EventActor? Actor;
        public int Bone = -1;
    }

    private sealed class Particle
    {
        public Vector3 Position, Velocity, Force, Acceleration, Homing;
        public int Mass = 4096, GravityScale = 4096, HomingStrength, Lifetime = -1, Age, Scale = 4, Flags, Anchor = -1;
        public int Program, Table, Pc, TimeLeft, Frame, Death, Every;
        /// <summary>Its layer mode: the firing event's depth, else its sprite program's frame's layer (0x800D6E44).</summary>
        public int Depth, Layer;
        public bool Stopped;
        public int[] Colours = new int[3];
        /// <summary>Where its colour curves start (emitter byte 0xC7; BATTLE.PRG keeps it at the particle's +0x77 and steps it each tick, wrapping at 256).</summary>
        public int ColourPhase;
    }

    private sealed class Track
    {
        public int Type, Cursor, Firing = -1, Fired;
        public List<int[]> Events = new();
        public bool Done => Cursor >= Events.Count && Firing < 0;
    }

    private sealed class Instance
    {
        public int Pc, Frame, Children;
        public readonly int[] Vars = new int[4];
        public Instance? Parent;
        public bool Root, Ended;
        public Entity Source = null!, Secondary = null!, Target = null!;
        public List<Track>? Timeline;
        public int TimelineFrame, ChildAt;
        public bool Paused;
        public readonly List<Particle> Particles = new();
        public readonly Anchor[] Anchors = new Anchor[16];
        public readonly List<(int Delay, Entity Target, int Timeline)> Pending = new();
        public readonly List<Renderer> Renderers = new();
    }

    private readonly GDictionary _data;
    private readonly byte[] _script;
    private readonly List<int[]> _emitters = new(), _curves = new();
    private readonly GArray _timelines, _anchors;
    // Read every tick for every particle, so kept as plain arrays: each frame table's frames (part count, first
    // record), each record's corners, atlas rectangle and blend, and each sprite program's words.
    private readonly int[][][] _frames;
    private readonly (int[] Corners, int[] Rect, bool Semi, int Blend)[] _records;
    private readonly uint[][] _programs;
    private readonly int[] _gravity = new int[6], _drag = new int[2], _remap = new int[4];
    private readonly int _gravityCurve, _dragCurve, _anchorCount;
    private readonly Vector2 _atlasSize;
    private readonly List<Instance> _instances = new();
    private readonly List<Entity> _targets;
    private readonly int[] _curveTable = new int[33];
    private readonly RandomNumberGenerator _random = new();
    /// <summary>The engine's field it draws in, under its own name there.</summary>
    public Wol.Engine.World.FieldScene? Scene;
    private readonly string _key = "effect" + System.Threading.Interlocked.Increment(ref _effectCount);
    private static int _effectCount;

    private readonly Texture2D? _atlas;
    private readonly List<Texture2D?> _pageTextures = new();
    private readonly Dictionary<string, int> _pages = new();
    private readonly List<string> _rendererKinds = new();
    private readonly List<int[]> _standardColours = new();
    private Vector3 _gravityNow;
    private int _dragNow, _ticks;

    public bool Finished { get; private set; }

    /// <summary>The actor an event's effect is requested on (a0's actor), whose colours a shift on the source changes while the effect itself comes from the point (0, 0, 0).</summary>
    public EventActor? Caster { get; init; }

    /// <summary>The character in an actor-table slot (0 is Ashley), for anchors on a fixed actor.</summary>
    public Func<int, EventActor?>? Slot { get; init; }

    /// <summary>
    /// The battle's casting mode (BATTLE.PRG's byte at 0x800F522C, set as an action is requested from an actor:
    /// 1 an item, 2 a spell, 3 a special attack or Break Art; 0 for events and once the casting is over). Script
    /// operation 8 branches when it is 0, 15 picks variable 3 by it (the casting sparkle's emitter: 43, 44, 45;
    /// BATTLE.PRG's bytes at 0x800EC32C) and 36 clears it. E000's entry 56 sparkles about the caster while it is set.
    /// </summary>
    public int Mode;

    private static readonly int[] ModeEmitters = { 0, 43, 44, 45 };

    /// <summary>
    /// The depth (retail units) the GTE's divide stops at: half the projection distance after the view's scale. A sprite
    /// corner nearer than it is placed on the screen as if it stood there, as the character renderer's are (see
    /// <see cref="EventActor.ProjectAsRetail"/>); 0 for none.
    /// </summary>
    public float NearLimit;

    /// <summary>The projection distance the GTE projects with, for <see cref="NearLimit"/>.</summary>
    public float Projection;

    /// <summary>The characters an effect's request lists besides its source and targets (timeline type 8 shifts them).</summary>
    public Func<IEnumerable<EventActor>>? Bystanders { get; init; }

    /// <summary>Operation 35: the script lets its source actor go on to its release (0x8009D854).</summary>
    public Action? Released;

    /// <summary>
    /// A palette transform (opcode 70's, BATTLE.PRG 0x8007B29C) on an actor (null: the room): its operation, its
    /// change in 5-bit steps and retail's fade value; operation 8 restores the original colours.
    /// </summary>
    public Action<EventActor?, int, Vector3, int>? ColourShift;

    private Instance? _focusInstance;
    private int _focusAnchor;

    /// <summary>Where the effect has the camera look (track 12 action 13), until it gives it back (14) or ends.</summary>
    public Vector3? Focus => _focusInstance != null && !Finished ? AnchorAt(_focusInstance, _focusAnchor) : null;

    /// <summary>Rumbles the pad by an envelope (its first value, 128 full).</summary>
    public Action<int, int>? Rumble;

    /// <summary>Plays an installed sound by its key, panned by where a retail point stands on the screen (none: centred).</summary>
    public Action<string, Vector3?>? Sound;

    /// <summary>Starts the effect's sound sequence (its block 13); <see cref="SequenceStarted"/> then holds until the effect is let go.</summary>
    public Action<int>? Sequence;
    public bool SequenceStarted { get; private set; }

    /// <summary>Whether the effect has reached its hit (track 12 action 0 or 1), when its action's results land.</summary>
    public bool Struck { get; private set; }
    public Action? SequenceEnded;

    /// <summary>A sky dome colour transform (track 10, as opcode 70 mode 2): operation, change in 8-bit steps, fade.</summary>
    public Action<int, Vector3, int>? SkyShift;

    /// <summary>A weapon trail on an actor's bone, or every trail off (track 12 actions 3 and 4).</summary>
    public Action<EventActor?, int, int, bool>? Trail;

    /// <summary>Reveals the trap on the tile under a map position (track 12 action 2).</summary>
    public Action<Vector3>? RevealTrap;

    /// <summary>An actor's carried weapon drawn flat at a grey (0 back to its owner's light; track 12 action 10).</summary>
    public Action<EventActor?, int>? WeaponGlow;

    /// <summary>Holds the room's moving platforms for a number of frames (track 12 action 7).</summary>
    public Action<int>? HoldPlatforms;

    /// <summary>One of an actor's own sounds by number (track 12 action 11).</summary>
    public Action<EventActor?, int>? ActorSound;
    private int _lastActorSound = -1;

    /// <summary>
    /// The camera shake now (0x800d206c, each channel its wave times its envelope and gain over 256): across, up and
    /// toward the target in map units, and a roll in 4096ths of a turn.
    /// </summary>
    public Vector4 Shake { get; private set; }

    private sealed class ShakeChannel
    {
        public bool On, SpeedCurve, JitterCurve;
        public int Envelope, Gain, Speed, Jitter, Time, Phase;
    }
    private readonly ShakeChannel[] _shake = { new(), new(), new(), new() };
    private readonly List<int[]> _envelopes = new();
    /// <summary>Whom the effect's colour shifts changed, restored should it end before its own events restore them.</summary>
    private readonly HashSet<EventActor?> _shifted = new();

    /// <summary>The most quads drawn in one tick so far, and the ticks run, for tests.</summary>
    public int MostQuads { get; private set; }
    public int Ticks => _ticks;

    /// <param name="entry">The part's script address: 0, or for the resident file's small effects three times their number.</param>
    /// <param name="texture">Loads an installed texture by name (the grids' VRAM pages).</param>
    /// <summary>
    /// An event's effect (opcode a0, BATTLE.PRG 0x800CF0E8) is only loaded: it waits, ready, for the script to start
    /// its parts (a1), each once, and stays ready between them.
    /// </summary>
    public bool Armed { get; }

    /// <summary>An armed effect with nothing playing: none of its started parts is still running.</summary>
    public bool Idle => Armed && _instances.Count == 0;

    private readonly HashSet<int> _startedParts = new();
    private readonly Entity _source;

    public FieldEffect(GDictionary data, Texture2D? atlas, Entity source, List<Entity> targets, int entry = 0, Func<string, Texture2D?>? texture = null, bool armed = false)
    {
        Armed = armed;
        _source = source;
        _data = data;
        var script = data["script"].AsGodotArray();
        _script = new byte[script.Count];
        for (int i = 0; i < script.Count; i++) _script[i] = (byte)script[i].AsInt32();
        foreach (var e in data["emitters"].AsGodotArray()) _emitters.Add(Ints(e.AsGodotArray()));
        foreach (var c in data["curves"].AsGodotArray()) _curves.Add(Ints(c.AsGodotArray()));
        foreach (var envelope in data.GetValueOrDefault("envelopes", new GArray()).AsGodotArray()) _envelopes.Add(Ints(envelope.AsGodotArray()));
        var frames = data["frames"].AsGodotArray();
        _frames = new int[frames.Count][][];
        for (int t = 0; t < frames.Count; t++)
        {
            var table = frames[t].AsGodotArray();
            _frames[t] = new int[table.Count][];
            for (int f = 0; f < table.Count; f++) _frames[t][f] = Ints(table[f].AsGodotArray());
        }
        var records = data["records"].AsGodotArray();
        _records = new (int[], int[], bool, int)[records.Count];
        for (int r = 0; r < records.Count; r++)
        {
            var record = records[r].AsGodotDictionary();
            _records[r] = (Ints(record["corners"].AsGodotArray()), Ints(record["rect"].AsGodotArray()), record["semi"].AsBool(), record["blend"].AsInt32());
        }
        var programs = data["programs"].AsGodotArray();
        _programs = new uint[programs.Count][];
        for (int p = 0; p < programs.Count; p++)
        {
            var words = programs[p].AsGodotArray();
            _programs[p] = new uint[words.Count];
            for (int w = 0; w < words.Count; w++) _programs[p][w] = (uint)words[w].AsInt64();
        }
        _timelines = data["timelines"].AsGodotArray();
        _anchors = data["anchors"].AsGodotArray();
        if (data.TryGetValue("header", out var headerValue))
        {
            var header = headerValue.AsGodotDictionary();
            Array.Copy(Ints(header["gravity"].AsGodotArray()), _gravity, 6);
            Array.Copy(Ints(header["drag"].AsGodotArray()), _drag, 2);
            Array.Copy(Ints(header["remap"].AsGodotArray()), _remap, 4);
            _gravityCurve = header["gravity_curve"].AsInt32();
            _dragCurve = header["drag_curve"].AsInt32();
            _anchorCount = Math.Min(16, header["anchors"].AsInt32());
        }
        _targets = targets.Count > 0 ? targets : new List<Entity> { source };
        if (data.TryGetValue("atlas_size", out var size)) _atlasSize = new Vector2(size.AsGodotArray()[0].AsSingle(), size.AsGodotArray()[1].AsSingle());
        // PS1 semi-transparency: 0 half and half, 1 adds, 2 subtracts, 3 adds a quarter; 4 is opaque.
        _atlas = atlas;
        foreach (var value in data.GetValueOrDefault("renderers", new GArray()).AsGodotArray()) _rendererKinds.Add(value.AsString());
        if (data.GetValueOrDefault("colours").VariantType == Variant.Type.Array)
            foreach (var value in data["colours"].AsGodotArray()) _standardColours.Add(Ints(value.AsGodotArray()));
        if (data.GetValueOrDefault("pages").VariantType == Variant.Type.Dictionary && texture != null)
            foreach (var (key, asset) in data["pages"].AsGodotDictionary())
            {
                _pages[key.AsString()] = _pageTextures.Count + 1;
                _pageTextures.Add(texture(asset.AsString()));
            }
        // Part 0 starts at once (the cast); its program begins at script address 0.
        if (!armed) _instances.Add(new Instance { Pc = entry, Root = true, Source = source, Secondary = source, Target = _targets[0] });
    }

    /// <summary>Starts part <paramref name="part"/> (BATTLE.PRG 0x800CEF74): a root instance at script address 3 n.</summary>
    public void StartPart(int part)
    {
        if (Finished || !Armed && _instances.Count == 0 && part > 0) return;
        // A part's bit in the ready mask (0x800F521C) is cleared as it starts.
        if (Armed && !_startedParts.Add(part)) return;
        var first = _instances.Count > 0 ? _instances[0] : null;
        var source = first?.Source ?? (Armed ? _source : new Entity());
        var target = first?.Target ?? _targets[0];
        _instances.Add(new Instance { Pc = part * 3, Root = true, Source = source, Secondary = source, Target = target });
    }

    /// <summary>
    /// Clears what is playing (opcode a5): the running parts and their particles end at once. An event's effect stays
    /// loaded and ready for the parts it has not started, as retail's does: the Dullahan's summoning (EVT0011)
    /// clears part 0 and starts part 1, its orbs, straight after.
    /// </summary>
    public void Clear()
    {
        _instances.Clear();
        _focusInstance = null;
        Shake = Vector4.Zero;
        Scene?.DrawTriangles(_key, ReadOnlySpan<Wol.Engine.World.FieldTriangle>.Empty, FieldView.EffectLayer);
        if (!Armed) Finish();
    }

    private static int[] Ints(GArray array)
    {
        var values = new int[array.Count];
        for (int i = 0; i < values.Length; i++) values[i] = array[i].AsInt32();
        return values;
    }

    private int Curve(int id, int t) => id <= 0 || id > _curves.Count || _curves[id - 1].Length == 0 ? 0 : _curves[id - 1][((t % _curves[id - 1].Length) + _curves[id - 1].Length) % _curves[id - 1].Length];

    private static int Lerp(int start, int end, int value) => start + ((end - start) * value >> 7);

    /// <summary>One 30 Hz tick: every live instance runs until it yields.</summary>
    public void Tick()
    {
        if (Finished || Idle) return;
        StepShake();
        // A runaway effect stops after half a minute.
        if (++_ticks > 900) { Finish(); return; }
        foreach (var instance in _instances.ToArray())
        {
            // An ended instance's particles live out their lives.
            // An ended instance's particles live out their lives; its renderers end with it (phase 4).
            if (instance.Ended) { instance.Renderers.Clear(); if (instance.Particles.Count > 0) TickParticles(instance); continue; }
            Run(instance);
            instance.Frame++;
        }
        _instances.RemoveAll(i => i.Ended && i.Particles.Count == 0 && i.Renderers.Count == 0);
        if (_instances.Count == 0)
        {
            // An armed effect waits for its next part with nothing drawn.
            if (Armed) { Scene?.DrawTriangles(_key, ReadOnlySpan<Wol.Engine.World.FieldTriangle>.Empty, FieldView.EffectLayer); return; }
            Finish();
            return;
        }
        Draw();
    }

    private void Finish()
    {
        Finished = true;
        Shake = Vector4.Zero;
        Scene?.DrawTriangles(_key, ReadOnlySpan<Wol.Engine.World.FieldTriangle>.Empty, FieldView.EffectLayer);
        foreach (var actor in _shifted) ColourShift?.Invoke(actor, 8, Vector3.Zero, 0);
        _shifted.Clear();
    }

    private void Run(Instance instance)
    {
        int Byte(int at) => at < _script.Length ? _script[at] : 3;
        int Word(int at) => Byte(at) | Byte(at + 1) << 8;
        for (int steps = 0; steps < 256; steps++)
        {
            int pc = instance.Pc, op = Byte(pc);
            switch (op)
            {
                case 0: instance.Pc = Word(pc + 1); return;
                case 1: instance.Pc = Word(pc + 1); continue;
                case 2:
                    _instances.Add(new Instance { Pc = Word(pc + 1), Source = instance.Source, Secondary = instance.Secondary, Target = instance.Target });
                    instance.Pc += 3; continue;
                case 3: instance.Ended = true; return;
                case 4: instance.Pc += 2; continue;
                case 5: instance.Pc += 6; continue;
                case 6: instance.Pc = instance.Renderers.Count == 0 ? Word(pc + 1) : pc + 3; continue;
                case 7: LoadTimeline(instance, Byte(pc + 1)); instance.Pc = 0xB7; continue;
                case 8: instance.Pc = Mode == 0 ? Word(pc + 1) : pc + 3; continue;
                case 15: if (Mode < 4) instance.Vars[3] = ModeEmitters[Mode]; instance.Pc += 1; continue;
                case 35: Released?.Invoke(); instance.Pc += 1; continue;
                case 36: Mode = 0; instance.Pc += 1; continue;
                case 11: TickRenderers(instance); instance.Pc += 1; continue;
                case 13: case 40: case 46: case 48: instance.Pc += 1; continue;
                case 12: UpdateAnchors(instance); instance.Pc += 1; continue;
                case 14: InitAnchors(instance); instance.Pc += 1; continue;
                case 16: instance.Vars[Byte(pc + 1) & 3] = Byte(pc + 2); instance.Pc += 4; continue;
                case 17: instance.Pc = instance.Vars[Byte(pc + 1) & 3] == Word(pc + 2) ? Word(pc + 4) : pc + 6; continue;
                case 22: instance.Pc = instance.Particles.Count == Byte(pc + 1) ? Word(pc + 2) : pc + 4; continue;
                case 32: Fire(instance, instance.Vars[Byte(pc + 1) & 3], 0, null, 0); instance.Pc += 2; continue;
                case 33: TickParticles(instance); instance.Pc += 1; continue;
                case 34:
                    _gravityNow = new Vector3(Lerp(_gravity[0], _gravity[1], _curveTable[Math.Min(32, _gravityCurve)]), Lerp(_gravity[2], _gravity[3], _curveTable[Math.Min(32, _gravityCurve)]), Lerp(_gravity[4], _gravity[5], _curveTable[Math.Min(32, _gravityCurve)]));
                    _dragNow = Lerp(_drag[0], _drag[1], _curveTable[Math.Min(32, _dragCurve)]);
                    instance.Pc += 1; continue;
                case 41:
                {
                    int timeline = instance.Vars[Byte(pc + 1) & 3];
                    if (instance.Root && timeline < 4 && _remap[timeline] != 0) timeline = _remap[timeline] - 1;
                    LoadTimeline(instance, timeline);
                    instance.Pc += 2; continue;
                }
                case 42: instance.ChildAt = Word(pc + 1); RunTimeline(instance); instance.Pc += 3; continue;
                case 43: instance.Pc = instance.Timeline == null || instance.Timeline.TrueForAll(t => t.Done) && instance.Pending.Count == 0 ? Word(pc + 1) : pc + 3; continue;
                case 44: instance.Timeline = null; instance.Pc += 1; continue;
                case 45:
                {
                    int packed = Byte(pc + 1) | Byte(pc + 2) << 8 | Byte(pc + 3) << 16, emitter = packed & 0x3F;
                    if (emitter < _emitters.Count) { _emitters[emitter][0xC4] = (packed >> 6) & 0x3F; _emitters[emitter][0xC5] = (packed >> 12) & 0x3F; _emitters[emitter][0xC6] = (packed >> 18) & 0x3F; }
                    instance.Pc += 4; continue;
                }
                case 47:
                    if (instance.Parent != null) instance.Parent.Children = Math.Max(0, instance.Parent.Children - 1);
                    instance.Pc += 1; continue;
                default: instance.Ended = true; return;
            }
        }
    }

    private void LoadTimeline(Instance instance, int index)
    {
        instance.Timeline = null;
        instance.TimelineFrame = 0;
        if (index < 0 || index >= _timelines.Count) return;
        instance.Timeline = new();
        foreach (var value in _timelines[index].AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            var track = new Track { Type = record["type"].AsInt32() };
            foreach (var e in record["events"].AsGodotArray()) track.Events.Add(Ints(e.AsGodotArray()));
            instance.Timeline.Add(track);
        }
    }

    /// <summary>
    /// One timeline tick: each track fires its events whose frame has come. 1 fires an emitter once a tick, repeat
    /// times, at its emission count; 3 starts a child per target (one each delay ticks), then one more; 12 action 5
    /// starts an anchor's path and 6 pauses the clock. The others are not followed.
    /// </summary>
    private void RunTimeline(Instance instance)
    {
        if (instance.Timeline == null) return;
        int frame = instance.TimelineFrame;
        foreach (var track in instance.Timeline)
        {
            if (track.Firing >= 0)
            {
                uint word = Word(track.Events[track.Firing]);
                int repeat = Math.Max(1, (int)(word >> 8) & 0xFF);
                Fire(instance, (int)(word >> 20) & 0x3F, (int)(word >> 26) & 0x3F, null, track.Fired, (int)(word >> 16) & 0xF);
                if (!instance.Paused && ++track.Fired >= repeat) track.Firing = -1;
                continue;
            }
            while (track.Cursor < track.Events.Count)
            {
                var e = track.Events[track.Cursor];
                int at = track.Type switch { 1 => e[0], 3 => e[7], 12 => (e[0] | e[1] << 8) & 0x1FF, 2 or 4 or 5 => (e[0] | e[1] << 8) & 0x1FF, _ => (short)(e[0] | e[1] << 8) };
                if (at > frame) break;
                track.Cursor++;
                if (track.Type == 1)
                {
                    uint word = Word(e);
                    track.Firing = track.Cursor - 1;
                    track.Fired = 0;
                    Fire(instance, (int)(word >> 20) & 0x3F, (int)(word >> 26) & 0x3F, null, 0, (int)(word >> 16) & 0xF);
                    if (++track.Fired >= Math.Max(1, (int)(word >> 8) & 0xFF)) track.Firing = -1;
                    break;
                }
                if (track.Type == 3)
                {
                    int delay = e[8];
                    for (int i = 0; i < _targets.Count; i++) instance.Pending.Add((i * delay, _targets[i], e[0]));
                    instance.Pending.Add((_targets.Count * delay, instance.Target, e[6]));
                }
                if (track.Type == 5 && Rumble != null)
                {
                    // Rumble (block 11's envelope n) when the caster, a target, or anyone is the player, or always (0).
                    int value = e[0] | e[1] << 8, who = value >> 14;
                    bool player = who switch { 1 => instance.Source.Actor?.Id == "slot:0", 2 => instance.Target.Actor?.Id == "slot:0", 3 => instance.Source.Actor?.Id == "slot:0" || _targets.Exists(t => t.Actor?.Id == "slot:0"), _ => true };
                    if (player) Rumble((value >> 9) & 0x1F, Envelope((value >> 9) & 0x1F, 0) is var strength && strength > 0 ? strength : 128);
                }
                if (track.Type == 4)
                {
                    // Starts PLG renderer n (0x800D5D74): its lifetime (0 for until stopped), its emitter record and a parameter.
                    uint word = Word(e);
                    StartRenderer(instance, (int)(word >> 18) & 7, (int)(word >> 9) & 0x1FF, (int)(word >> 21) & 0x3F, e[4] | e[5] << 8);
                }
                if (track.Type == 13)
                {
                    // Camera shake (0x800D1EF0): on each channel the mask names, an envelope, a gain, and a phase
                    // speed and jitter, each either a value or a curve read at the shake's age.
                    uint word = (uint)(e[4] | e[5] << 8 | e[6] << 16 | e[7] << 24);
                    for (int c = 0; c < 4; c++)
                    {
                        if (((word >> c) & 1) == 0) continue;
                        var channel = _shake[c];
                        channel.On = true;
                        channel.Envelope = (int)(word >> 4) & 0x1F;
                        channel.Gain = (int)(word >> 9) & 0x7F;
                        channel.SpeedCurve = ((word >> 16) & 1) != 0;
                        channel.Speed = (int)(word >> 17) & 0x7F;
                        channel.JitterCurve = ((word >> 24) & 1) != 0;
                        channel.Jitter = (int)(word >> 25) & 0x7F;
                        channel.Time = 0;
                        channel.Phase = JitterOf(channel);
                    }
                }
                if (track.Type == 2)
                {
                    // A sound (0x800d5904): bank 0 a resident one on the one-shot voices, 1 the effect's own (block
                    // 12), 2 starts the effect's sound sequence (block 13). Placed (bits 20 and 21), it pans by where
                    // its anchor (bits 22 to 27) stands on the screen; otherwise it is centred.
                    uint word = Word(e);
                    int id = (int)(word >> 9) & 0x1FF, bank = (int)(word >> 18) & 3;
                    Vector3? placed = ((word >> 20) & 3) != 0 ? AnchorAt(instance, (int)(word >> 22) & 0x3F) : null;
                    if (bank == 0) Sound?.Invoke($"effect_{id}_p128", placed);
                    if (bank == 1) Sound?.Invoke($"fx{_data["id"].AsInt32():d3}_{id}_p128", placed);
                    if (bank == 2 && Sequence != null) { Sequence(_data["id"].AsInt32()); SequenceStarted = true; }
                }
                if (track.Type == 10 && SkyShift != null)
                {
                    // The sky dome's colours (0x800d6310, opcode 70 mode 2's call): operation e[7], the fade (halfword
                    // 2, as it is) and r, g, b less 128 in 8-bit steps.
                    SkyShift(e[7], new Vector3(e[4] - 128, e[5] - 128, e[6] - 128) / 255f, e[2] | e[3] << 8);
                }
                if (track.Type is >= 6 and <= 9 && ColourShift != null)
                {
                    // A palette transform (BATTLE.PRG 0x800D5FF8, the call opcode 70 makes) on the source, each
                    // target or the room: operation e[7], the fade (halfword 2, passed to the routine as it is) and
                    // a change of r, g, b less 128 in 5-bit steps. Bystanders (8): every other character the request
                    // lists (0x800D61AC), so Ashley takes the drain's purple in MAP010 as the dying soldier does.
                    int fade = e[2] | e[3] << 8, operation = e[7];
                    var change = new Vector3(e[4] - 128, e[5] - 128, e[6] - 128) / 31f;
                    var shifted = new List<EventActor?>();
                    if (track.Type == 6) shifted.Add(instance.Source.Actor ?? Caster);
                    if (track.Type == 7) foreach (var target in _targets) shifted.Add(target.Actor);
                    if (track.Type == 9) shifted.Add(null);
                    if (track.Type == 8 && Bystanders != null) foreach (var other in Bystanders()) if (other != instance.Source.Actor && !_targets.Exists(t => t.Actor == other)) shifted.Add(other);
                    foreach (var actor in shifted)
                    {
                        if (track.Type != 9 && actor == null) continue;
                        ColourShift(actor, operation, change, fade);
                        _shifted.Add(actor);
                    }
                }
                if (track.Type == 12)
                {
                    int action = (e[0] | e[1] << 8) >> 9;
                    if (action == 5 && e[2] < 16 && instance.Anchors[e[2]] is { } anchor) { anchor.Started = true; anchor.Tick = 0; }
                    if (action == 6) instance.Paused = true;
                    // 0 and 1 are the hit's moment on the target or the source (0x8006cb04, 0x8006ca20): what the action
                    // does lands then.
                    if (action is 0 or 1) Struck = true;
                    // 9 stops an anchor's path (5's inverse).
                    if (action == 9 && e[2] < 16 && instance.Anchors[e[2]] is { } stopped) stopped.Started = false;
                    // 3 starts a weapon trail (0x800d6448) on the actor and bone of the anchor byte 2 names, in the style
                    // byte 3 gives; 4 ends every trail.
                    if (action == 3 && e[2] < 16 && instance.Anchors[e[2]] is { Actor: { } bearer } placed)
                        Trail?.Invoke(bearer, placed.Bone >= 0 ? placed.Bone : 0x40, e[3], true);
                    if (action == 4) Trail?.Invoke(null, 0, 0, false);
                    // 2 reveals the trap on the tile of the effect's point (0x8006cdd8; effects 40 and 188 use it).
                    if (action == 2) RevealTrap?.Invoke(instance.Target.Position());
                    // 7 holds the room's platforms for 180 frames (0x8008d5a0); 8 releases the source to its next move
                    // (0x8009d854, as operation 35 does).
                    if (action == 7) HoldPlatforms?.Invoke(180);
                    if (action == 8 && instance.Source.Actor != null) Released?.Invoke();
                    // 10 draws the second entity's carried weapon flat at the grey e[2] (0 gives it back its light).
                    if (action == 10) WeaponGlow?.Invoke(instance.Secondary.Actor, e[2]);
                    // 11 plays one of the second entity's own sounds: n + 4 below 4, else one of 4 to 6 not the last.
                    if (action == 11 && ActorSound != null)
                    {
                        int sound = e[2] < 4 ? e[2] + 4 : 4 + _random.RandiRange(0, 2);
                        if (e[2] >= 4 && sound == _lastActorSound) sound = 4 + (sound - 4 + 1 + _random.RandiRange(0, 1)) % 3;
                        _lastActorSound = sound;
                        ActorSound(instance.Secondary.Actor, sound);
                    }
                    // 13 turns the camera to look at anchor a (over b ticks), 14 gives it back.
                    if (action == 13) { _focusAnchor = e[2]; _focusInstance = instance; }
                    if (action == 14) _focusInstance = null;
                }
            }
        }
        for (int i = instance.Pending.Count - 1; i >= 0; i--)
        {
            var (delay, target, timeline) = instance.Pending[i];
            if (delay > 0) { instance.Pending[i] = (delay - 1, target, timeline); continue; }
            instance.Pending.RemoveAt(i);
            if (timeline >= _timelines.Count) continue;
            var child = new Instance { Pc = instance.ChildAt, Parent = instance, Source = instance.Source, Secondary = instance.Source, Target = target };
            child.Vars[1] = timeline;
            instance.Children++;
            _instances.Add(child);
        }
        if (!instance.Paused) instance.TimelineFrame++;
    }

    private int JitterOf(ShakeChannel channel)
    {
        int jitter = channel.JitterCurve ? Curve(channel.Jitter, channel.Time) : channel.Jitter;
        return jitter == 0 ? 0 : _random.RandiRange(0, jitter - 1);
    }

    /// <summary>An envelope at a time: straight between its points' values, -1 after its end.</summary>
    private int Envelope(int index, int time)
    {
        if (index >= _envelopes.Count) return 0;
        var points = _envelopes[index];
        int at = 0;
        while (at + 1 < points.Length && points[at] != 0 && time - points[at] >= 0) { time -= points[at]; at += 2; }
        if (at + 1 >= points.Length) return -1;
        if (points[at] != 0) return at + 3 < points.Length ? (points[at + 3] - points[at + 1]) * time / points[at] + points[at + 1] : points[at + 1];
        return time == 0 ? points[at + 1] : -1;
    }

    private void StepShake()
    {
        var shake = Vector4.Zero;
        for (int c = 0; c < 4; c++)
        {
            var channel = _shake[c];
            if (!channel.On) continue;
            int v = Envelope(channel.Envelope, channel.Time);
            if (v < 0) { channel.On = false; continue; }
            float value = Mathf.Sin(channel.Phase * 32 * Mathf.Tau / 4096f) * v * channel.Gain / 256f;
            shake[c] = value;
            channel.Phase += (channel.SpeedCurve ? Curve(channel.Speed, channel.Time) : channel.Speed) + JitterOf(channel);
            channel.Time++;
        }
        Shake = shake;
        MostShake = Math.Max(MostShake, Math.Max(Math.Abs(shake.X), Math.Abs(shake.Y)));
    }

    /// <summary>The largest shake across or up so far, for tests.</summary>
    public float MostShake { get; private set; }

    private static uint Word(int[] e) => (uint)(e[0] | e[1] << 8 | e[2] << 16 | e[3] << 24);

    /// <summary>
    /// Anchors (block 10): each a frame on an entity (bits 1 and 3 to 4: the fixed actor, the instance's secondary or
    /// its target; bit 2 the entity's bone, else the fixed one) with its translation, and a path toward a target
    /// (bits 12 and 13) that a timeline starts. The fixed actor is the actor-table slot in the record's byte 2
    /// (BATTLE.PRG 0x800D004C), whoever the effect was played by: the Dullahan's summoning anchors its orbs on slot 2,
    /// him.
    /// </summary>
    private void InitAnchors(Instance instance)
    {
        for (int i = 0; i < _anchorCount && i < _anchors.Count; i++)
        {
            instance.Anchors[i] = new Anchor();
            PlaceAnchor(instance, i);
        }
    }

    private Entity Pick(Instance instance, int choice) => choice switch { 1 => instance.Secondary, 2 => instance.Target, _ => instance.Source };

    private void PlaceAnchor(Instance instance, int i)
    {
        var definition = _anchors[i].AsGodotDictionary();
        var anchor = instance.Anchors[i];
        int flags = definition["flags"].AsInt32(), bone = definition["bone"].AsInt32();
        var t = Ints(definition["transform"].AsGodotArray());
        int choice = (flags >> 3) & 3;
        var entity = choice == 0 && Slot?.Invoke(definition["actor"].AsInt32()) is { } fixedActor ? new Entity { Actor = fixedActor } : Pick(instance, choice);
        var at = (flags & 2) != 0 ? entity.Position((flags & 4) != 0 ? -1 : bone) : instance.Source.Position();
        // The anchor's own frame (flag 1; BATTLE.PRG 0x800D0B30): its translation runs from its start to its end
        // values (record 0x20, pairs) by curve 0's value over 128, and is composed with the entity's frame (0x80011ADC),
        // so on an actor's bone it turns and scales with the bone.
        // On an actor, the anchor turns with its bone; with 0x4000 by the actor's facing alone (0x800D0D08).
        int boneFrame = (flags & 4) != 0 ? entity.Bone : bone;
        (anchor.Actor, anchor.Bone) = (entity.Actor, boneFrame);
        bool yawOnly = (flags & 0xC000) == 0x4000;
        anchor.Basis = (flags & 2) != 0 && entity.Actor != null
            ? yawOnly || boneFrame < 0 ? new Basis(Vector3.Up, -entity.Actor.Facing * Mathf.Tau / 4096f) : entity.Actor.BoneBasis(boneFrame)
            : Basis.Identity;
        if ((flags & 1) != 0)
        {
            int[] curves = Ints(definition["curves"].AsGodotArray());
            int along = curves[0] > 0 ? Curve(curves[0], instance.Frame) : 0;
            var offset = new Vector3(t[6] + ((t[7] - t[6]) * along >> 7), t[8] + ((t[9] - t[8]) * along >> 7), t[10] + ((t[11] - t[10]) * along >> 7));
            at = (flags & 2) != 0 && entity.Actor != null && boneFrame >= 0 && !yawOnly ? entity.Actor.BonePoint(boneFrame, offset) : at + anchor.Basis * offset;
        }
        anchor.From = at;
        anchor.Translation = at;
        if ((flags & 0x80) != 0) anchor.To = Pick(instance, (flags >> 10) & 3).Position((flags & 0x200) != 0 ? -1 : definition["bone"].AsInt32());
        anchor.Path = ((flags >> 12) & 3) != 0;
        int packed = definition["packed"].AsInt32(), mode = definition["mode"].AsInt32();
        anchor.Limit = (packed & 0xFF) + 1;
        anchor.Ease = (mode >> 5) & 3;
        anchor.Curve = Ints(definition["curves"].AsGodotArray())[1];
        // A spline bends through a control point off the line's middle by the control radius (32 a step, give or take
        // its jitter), chosen once.
        anchor.Spline = ((flags >> 12) & 3) == 2;
        if (anchor.Spline && !anchor.Started)
        {
            float radius = ((packed >> 8) & 15) * 32 + Rand(-((packed >> 12) & 15) * 32, ((packed >> 12) & 15) * 32);
            var side = new Vector3(Rand(-1, 1), -1, Rand(-1, 1)).Normalized();
            anchor.Control = (anchor.From + anchor.To) / 2 + side * radius;
        }
        if ((definition["mode"].AsInt32() & 0x80) != 0) (anchor.From, anchor.To) = (anchor.To, anchor.From);
    }

    private void UpdateAnchors(Instance instance)
    {
        for (int i = 0; i < _anchorCount && i < _anchors.Count; i++)
        {
            if (instance.Anchors[i] is not { } anchor) continue;
            if (!anchor.Started) { PlaceAnchor(instance, i); continue; }
            if (!anchor.Path) continue;
            float t = Math.Min(1f, (float)anchor.Tick / anchor.Limit);
            // Linear, eased, or along the anchor's path curve.
            if (anchor.Ease == 1) t = t * t * (3 - 2 * t);
            else if (anchor.Ease == 2 && anchor.Curve > 0) t = Curve(anchor.Curve, anchor.Tick) / 128f;
            anchor.Translation = anchor.Spline
                ? anchor.From * (1 - t) * (1 - t) + anchor.Control * 2 * (1 - t) * t + anchor.To * t * t
                : anchor.From.Lerp(anchor.To, t);
            anchor.Tick++;
        }
    }

    /// <summary>An anchor's frame: its turn and place, which a local particle's position is in.</summary>
    private Transform3D AnchorFrame(Instance instance, int index) =>
        index >= 0 && index < 16 && instance.Anchors[index] is { } anchor ? new Transform3D(anchor.Basis, anchor.Translation) : new Transform3D(Basis.Identity, instance.Source.Position());

    private Vector3 AnchorAt(Instance instance, int index) => index >= 0 && index < 16 && instance.Anchors[index] is { } anchor ? anchor.Translation : instance.Source.Position();

    /// <summary>
    /// Fires an emitter (the generic particle emitter, 0x800D2ADC): its count of particles at its origin plus a
    /// shape offset, each moving off at a speed in its direction, with its force, acceleration, homing, mass,
    /// gravity scale, lifetime, program, frames and colours; its ranges are read through the curves sampled at the
    /// firing's emission count.
    /// </summary>
    private void Fire(Instance instance, int index, int scale, Particle? parent, int counter, int depth = 0)
    {
        if (index < 0 || index >= _emitters.Count) return;
        for (int c = 1; c <= 32; c++) _curveTable[c] = Curve(c, counter);
        var e = _emitters[index];
        int S16(int at) => (short)(e[at] | e[at + 1] << 8);
        int Sel(int field) => Math.Min(32, e[8 + field]);
        int Range(int at, int field) => Lerp(S16(at), S16(at + 2), _curveTable[Sel(field)]);
        Vector3 Triple(int at, int field) => new(Range(at, field), Range(at + 4, field), Range(at + 8, field));
        int Between(int at, int field)
        {
            int a = Range(at, field), b = Range(at + 4, field);
            return a == b ? a : _random.RandiRange(Math.Min(a, b), Math.Max(a, b));
        }
        Vector3 Random3(int at, int field)
        {
            Vector3 low = Triple(at, field), high = Triple(at + 12, field);
            return new Vector3(Rand(low.X, high.X), Rand(low.Y, high.Y), Rand(low.Z, high.Z));
        }
        int flags = e[4] | e[5] << 8 | e[6] << 16 | e[7] << 24;
        int interval = Range(0xC0, 14);
        if (interval > 1 && instance.Frame % interval != 0) return;
        int count = Math.Clamp(Range(0xBC, 13), 0, 64);
        Vector3 origin = (flags & 7) switch
        {
            1 => instance.Target.Position(),
            2 => AnchorAt(instance, e[0xC8]),
            // A parent kept relative to its anchor (a local emitter's) stands at that anchor plus its position.
            4 => parent != null ? (parent.Anchor >= 0 ? AnchorFrame(instance, parent.Anchor) * (parent.Position / 4096f) : parent.Position / 4096f) : instance.Source.Position(),
            _ => Vector3.Zero,
        };
        bool local = (flags & (1 << 17)) != 0;
        if (local && (flags & 7) == 2) origin = Vector3.Zero;
        // A local child of a parent particle is kept relative to its own anchor too.
        else if (local && (flags & 7) == 4 && parent != null) origin = AnchorFrame(instance, e[0xC8]).AffineInverse() * origin;
        origin += Triple(0x18, 0);
        Vector3 extent = Triple(0x24, 1);
        float inner = Range(0x30, 15) / 4096f;
        bool surface = ((flags >> 11) & 3) != 0;
        for (int n = 0; n < count && instance.Particles.Count < 255; n++)
        {
            Vector3 offset = Shape((flags >> 3) & 7, extent, inner, surface, e[0xCA], e[0xCB]);
            Vector3 at = origin + offset;
            // The direction: a cone about the base angles, radial from the origin, or aimed at an entity.
            Vector3 angles = Triple(0x34, 2), spread = Triple(0x40, 3);
            Vector3 turn = angles + new Vector3(Rand(-spread.X, spread.X), Rand(-spread.Y, spread.Y), Rand(-spread.Z, spread.Z));
            // A cone about the base angles turns the z axis (BATTLE.PRG 0x800D3E60: the vector (0, 0, 4096)).
            Vector3 direction = Rotate(new Vector3(0, 0, 1), turn);
            int mode = (flags >> 13) & 7;
            if (mode == 1) direction = offset.LengthSquared() > 0 ? offset.Normalized() : new Vector3(0, -1, 0);
            else if (mode is 2 or 3)
            {
                var aim = (mode == 2 ? instance.Secondary : instance.Target).Position() - (local ? AnchorAt(instance, e[0xC8]) + at : at);
                if (aim.LengthSquared() > 0) direction = Rotate(aim.Normalized(), turn - angles);
            }
            float speed = Between(0xA4, 4) * 32;
            var particle = new Particle
            {
                Position = at * 4096, Velocity = direction * speed, Force = Random3(0x4C, 5), Acceleration = Random3(0x64, 6),
                Homing = Triple(0x7C, 7) + Random3Symmetric(Triple(0x88, 8)), HomingStrength = Between(0xAC, 9),
                Mass = Math.Max(1, Between(0x94, 10)), GravityScale = Between(0x9C, 11), Lifetime = Between(0xB4, 12),
                Program = e[0], Table = e[1], Death = e[2], Every = e[3], Flags = flags,
                // The scale (4 is the record's size): the firing event's, else the emitter's own (flags 26 to 31),
                // else its parent's, else 4.
                Scale = scale != 0 ? scale : (flags >> 26) != 0 ? (flags >> 26) & 0x3F : parent?.Scale ?? 4,
                Anchor = local ? e[0xC8] : -1, Depth = depth != 0 ? depth : parent?.Depth ?? 0,
            };
            for (int k = 0; k < 3; k++) particle.Colours[k] = e[0xC4 + k];
            particle.ColourPhase = e[0xC7];
            instance.Particles.Add(particle);
        }
    }

    private float Rand(float a, float b) => a == b ? a : _random.RandfRange(Math.Min(a, b), Math.Max(a, b));
    private Vector3 Random3Symmetric(Vector3 jitter) => new(Rand(-jitter.X, jitter.X), Rand(-jitter.Y, jitter.Y), Rand(-jitter.Z, jitter.Z));

    /// <summary>Angles 4096 a turn, applied x, then y, then z.</summary>
    /// <summary>Turns a vector by angles (4096 a turn) as the GTE's RotMatrix builds them (0x80041200): Rx·Ry·Rz.</summary>
    private static Vector3 Rotate(Vector3 v, Vector3 angles)
    {
        float Unit(float a) => a * Mathf.Tau / 4096f;
        return new Basis(Vector3.Right, Unit(angles.X)) * (new Basis(Vector3.Up, Unit(angles.Y)) * (new Basis(Vector3.Back, Unit(angles.Z)) * v));
    }

    /// <summary>The spawn shapes (flags bits 3 to 5): an ellipsoid, a box, a cone along +y, a cylinder, a disc, a square, a polygon.</summary>
    private Vector3 Shape(int shape, Vector3 extent, float inner, bool surface, int sides, int points)
    {
        float Radius() => surface ? 1 : Rand(inner, 1);
        switch (shape)
        {
            case 0:
            {
                var d = new Vector3(_random.Randfn(), _random.Randfn(), _random.Randfn());
                d = d.LengthSquared() > 0 ? d.Normalized() : Vector3.Up;
                return d * extent * Radius();
            }
            case 1:
            {
                var p = new Vector3(Rand(-1, 1), Rand(-1, 1), Rand(-1, 1));
                if (surface) { int axis = _random.RandiRange(0, 2); p[axis] = _random.Randf() < 0.5f ? -1 : 1; }
                return p * extent;
            }
            case 2:
            {
                float y = Mathf.Sqrt(_random.Randf()), angle = _random.Randf() * Mathf.Tau, r = y * Radius();
                return new Vector3(Mathf.Cos(angle) * r * extent.X, y * extent.Y, Mathf.Sin(angle) * r * extent.Z);
            }
            case 3:
            {
                float angle = _random.Randf() * Mathf.Tau, r = Radius();
                return new Vector3(Mathf.Cos(angle) * r * extent.X, _random.Randf() * extent.Y, Mathf.Sin(angle) * r * extent.Z);
            }
            case 5:
                return new Vector3(Rand(-extent.X, extent.X), 0, Rand(-extent.Z, extent.Z));
            default:
            {
                float angle = shape == 6 && sides > 0 ? _random.RandiRange(0, sides - 1) * Mathf.Tau / sides : _random.Randf() * Mathf.Tau, r = Radius();
                return new Vector3(Mathf.Cos(angle) * r * extent.X, 0, Mathf.Sin(angle) * r * extent.Z);
            }
        }
    }

    /// <summary>
    /// The particle tick (0x800D4C18): physics (velocity damped by drag over mass, plus force and gravity; force grows
    /// by its acceleration and toward its homing point), the sprite program, then ageing, tick and death events.
    /// </summary>
    private void TickParticles(Instance instance)
    {
        foreach (var p in instance.Particles.ToArray())
        {
            float mass = p.Mass;
            p.Velocity = (p.Velocity * (mass - _dragNow) + p.Force * 4096) / mass + _gravityNow * p.GravityScale / 4096f;
            p.Position += p.Velocity;
            p.Force += p.Acceleration;
            if (p.HomingStrength != 0)
            {
                var toward = p.Homing * 4096 - p.Position;
                if (toward.LengthSquared() > 0) p.Force += toward.Normalized() * p.HomingStrength;
            }
            StepProgram(p);
            p.Age++;
            if (p.Every > 0) Fire(instance, p.Every - 1, 0, p, p.Age);
            bool dies = p.Lifetime == -1 ? p.Stopped : --p.Lifetime <= 0;
            if (!dies) continue;
            if (p.Death > 0) Fire(instance, p.Death - 1, 0, p, 0);
            instance.Particles.Remove(p);
        }
    }

    private void StepProgram(Particle p)
    {
        if (p.Program >= _programs.Length) { p.Stopped = true; return; }
        var words = _programs[p.Program];
        if (p.TimeLeft <= 0 && !p.Stopped)
            for (int guard = 0; guard < 64 && p.Pc < words.Length; guard++)
            {
                uint word = words[p.Pc];
                int command = (int)(word >> 28);
                if (command == 0) { p.Frame = (int)(word & 0xFFFF); p.TimeLeft = (int)(word >> 16) & 0xFF; p.Layer = (int)(word >> 24) & 0x3F; p.Pc++; break; }
                if (command == 2) { p.Pc = 0; continue; }
                if (command == 3) { p.Stopped = true; break; }
                p.Pc++;
            }
        p.TimeLeft -= 2;
    }

    /// <summary>A triangle to draw: its texture (0 the atlas, else a VRAM page), blend, corners, texture coordinates and colours.</summary>
    /// <summary>
    /// A triangle to draw. <paramref name="Pull"/> brings its depth toward the camera without moving it on screen: a
    /// sprite's radius, as the PSX sorts a sprite by its middle against each room or model polygon's mean depth.
    /// </summary>
    /// <paramref name="Layer"/> is 1 for the slots behind the whole room; 0 sorts it by depth.
    private readonly record struct Triangle(int Texture, int Mode, Vector3 A, Vector3 B, Vector3 C, Vector2 Ua, Vector2 Ub, Vector2 Uc, Color Ca, Color Cb, Color Cc, float Pull = 0, int Layer = 0);

    private readonly List<Triangle> _triangles = new();

    /// <summary>A quad's two triangles (corners top left, top right, bottom left, bottom right).</summary>
    // The light the added sprites cast on the characters (FieldLighting, enhanced), and the atlas read to colour it.
    private OmniLight3D? _light;
    private Image? _atlasImage;

    /// <summary>
    /// Lights the characters with the added sprites drawn this frame: their middle, and their colour, each sprite's
    /// texel at its middle times its tint.
    /// </summary>
    private void CastLight()
    {
        if (!Runtime.FieldLighting.Enhanced) { if (_light != null) _light.Visible = false; return; }
        if (_light == null)
        {
            _light = new OmniLight3D { Name = "Glow", OmniRange = 1024 / Installation.MapConversion.UnitsPerMetre, OmniAttenuation = 1.5f, ShadowEnabled = false, LightSpecular = 0 };
            AddChild(_light);
            _light.TopLevel = true;
        }
        if (_atlasImage == null && _atlas != null) { _atlasImage = _atlas.GetImage(); if (_atlasImage?.IsCompressed() == true) _atlasImage.Decompress(); }
        var middle = Vector3.Zero;
        var sum = new Color(0, 0, 0, 0);
        int sprites = 0;
        foreach (var t in _triangles)
        {
            if (t.Mode is not (1 or 3)) continue;
            var texel = Colors.White;
            if (t.Texture == 0 && _atlasImage is { } image)
            {
                var uv = (t.Ua + t.Ub + t.Uc) / 3;
                texel = image.GetPixel(Math.Clamp((int)(uv.X * image.GetWidth()), 0, image.GetWidth() - 1), Math.Clamp((int)(uv.Y * image.GetHeight()), 0, image.GetHeight() - 1));
            }
            var colour = texel * t.Ca;
            sum += new Color(colour.R, colour.G, colour.B, 0);
            middle += (t.A + t.B + t.C) / 3;
            sprites++;
        }
        if (sprites > 0) middle /= sprites;
        Runtime.FieldLighting.Cast(_light, middle, sum, sprites);
    }

    private void Quad(int texture, int mode, Vector3[] p, Vector2[] uv, Color[] c, float pull = 0, int layer = 0)
    {
        _triangles.Add(new Triangle(texture, mode, p[0], p[1], p[2], uv[0], uv[1], uv[2], c[0], c[1], c[2], pull, layer));
        _triangles.Add(new Triangle(texture, mode, p[1], p[3], p[2], uv[1], uv[3], uv[2], c[1], c[3], c[2], pull, layer));
    }

    /// <summary>A PS1 blend's tint: half and half draws at half alpha, a quarter add at a quarter of the colour.</summary>
    private static Color Tint(int mode, Color colour) => mode switch { 0 => colour with { A = 0.5f }, 3 => colour * 0.25f, _ => colour };

    /// <summary>Builds the frame's triangles: each particle's frame parts as camera-facing quads, then what the renderers built.</summary>
    private void Draw()
    {
        Scene?.DrawTriangles(_key, ReadOnlySpan<Wol.Engine.World.FieldTriangle>.Empty, FieldView.EffectLayer);
        _triangles.Clear();
        var camera = GetViewport()?.GetCamera3D();
        if (camera == null) return;
        Vector3 right = camera.GlobalBasis.X, up = camera.GlobalBasis.Y;
        float unit = 1f / Installation.MapConversion.UnitsPerMetre;
        var eye = camera.GlobalTransform.AffineInverse();
        float front = Math.Max(camera.Near * 1.05f, unit);
        // A corner nearer than the GTE's limit lands where retail's projection puts it (one behind the eye at the near
        // plane).
        Vector3 Placed(Vector3 world)
        {
            if (NearLimit <= 0) return world;
            var local = eye * world / unit;
            if (-local.Z >= NearLimit) return world;
            return camera.GlobalTransform * (EventActor.GteProjection(local, Projection, NearLimit, front / unit).Drawn * unit);
        }
        int quads = 0;
        foreach (var instance in _instances)
            foreach (var p in instance.Particles)
            {
                if (p.Table >= _frames.Length || _atlasSize.X <= 0) continue;
                var table = _frames[p.Table];
                if (p.Frame >= table.Length) continue;
                int parts = table[p.Frame][0], first = table[p.Frame][1];
                var retail = p.Anchor >= 0 ? AnchorFrame(instance, p.Anchor) * (p.Position / 4096f) : p.Position / 4096f;
                var centre = EventActor.RetailSpace * retail;
                var colour = new Color(Channel(p, 0), Channel(p, 1), Channel(p, 2));
                float size = p.Scale / 4f * unit;
                // Flag 16 turns the sprite on screen to follow its motion (toward where it will be 128 ticks on).
                Vector3 across = right, down = -up;
                if ((p.Flags & (1 << 16)) != 0 && p.Velocity.LengthSquared() > 0)
                {
                    var ahead = camera.UnprojectPosition(EventActor.RetailSpace * (retail + p.Velocity / 4096f * 128)) - camera.UnprojectPosition(centre);
                    if (ahead.LengthSquared() > 0)
                    {
                        float angle = Mathf.Atan2(ahead.Y, ahead.X);
                        across = right * Mathf.Cos(angle) - up * Mathf.Sin(angle);
                        down = right * -Mathf.Sin(angle) - up * Mathf.Cos(angle);
                    }
                }
                for (int k = 0; k < parts && first + k < _records.Length; k++)
                {
                    var record = _records[first + k];
                    var c = record.Corners;
                    var corners = new Vector3[4];
                    for (int q = 0; q < 4; q++) corners[q] = Placed(centre + (across * c[q * 2] + down * c[q * 2 + 1]) * size);
                    int mode = record.Semi ? record.Blend : 4;
                    var tint = Tint(mode, colour);
                    // Sorted by its middle, brought 128 units toward the eye (a sparkle's own radius, a few units, left the
                    // dying soldier's in MAP010 inside his armour, where retail shows them over him), a sprite comes before a model it overlaps; a far one stays behind the room. Its
                    // layer mode (0x800D6E44: the firing's depth, else its frame's layer) moves its ordering-table slot,
                    // a quarter of its depth, 8 or 16 nearer (1, 2) or farther (3, 4); 5 puts it in the table's last
                    // slot (0x7FE), and 6 to 8 in slots before the table's first (-15, -13, -11), which the footage
                    // shows behind the room too (the courtyard's lightning glows behind the manor).
                    int layerMode = p.Depth != 0 ? p.Depth : p.Layer;
                    float bias = layerMode switch { 1 => 32, 2 => 64, 3 => -32, 4 => -64, _ => 0 } * unit;
                    Quad(0, mode, corners, RecordUv(first + k, 0, 0, 1, 1), new[] { tint, tint, tint, tint }, 128 * unit + bias, layerMode is >= 5 and <= 8 ? 1 : 0);
                    quads++;
                }
            }
        foreach (var instance in _instances)
            foreach (var renderer in instance.Renderers) _triangles.AddRange(renderer.Triangles);
        CastLight();
        MostQuads = Math.Max(MostQuads, quads + _triangles.Count / 2 - quads);
        // The engine draws them: a PS1 blend's add (1, and a quarter add, 3, its colour already a quarter) adds, 2
        // subtracts, half and half (0, at half alpha) and opaque (4) mix; enhanced lighting draws added sprites
        // brighter than white, to bloom (FieldLighting).
        var drawn = new Wol.Engine.World.FieldTriangle[_triangles.Count];
        for (int i = 0; i < drawn.Length; i++)
        {
            var t = _triangles[i];
            var texture = t.Texture == 0 ? _atlas : t.Texture > 0 ? _pageTextures[t.Texture - 1] : null;
            var blend = t.Mode is 1 or 3 ? Wol.Engine.World.FieldBlend.Add : t.Mode == 2 ? Wol.Engine.World.FieldBlend.Subtract : Wol.Engine.World.FieldBlend.Mix;
            drawn[i] = new(texture, blend, t.A, t.B, t.C, t.Ua, t.Ub, t.Uc, t.Ca, t.Cb, t.Cc, t.Pull, t.Layer > 0);
        }
        Scene?.DrawTriangles(_key, drawn, FieldView.EffectLayer, Runtime.FieldLighting.Enhanced ? Runtime.FieldLighting.EffectBoost : 1f);
    }

    public override void _ExitTree()
    {
        Scene?.RemoveTriangles(_key);
        // Letting the effect go releases its sound slot, which stops its sequence (0x80046634).
        if (SequenceStarted) SequenceEnded?.Invoke();
    }

    /// <summary>Texture coordinates in the atlas of a part of record <paramref name="index"/> (fractions of its width and height).</summary>
    private Vector2[] RecordUv(int index, float u0, float v0, float u1, float v1)
    {
        if (index < 0 || index >= _records.Length || _atlasSize.X <= 0) return new Vector2[4];
        var rect = _records[index].Rect;
        Vector2 At(float u, float v) => new((rect[0] + rect[2] * u) / _atlasSize.X, (rect[1] + rect[3] * v) / _atlasSize.Y);
        return new[] { At(u0, v0), At(u1, v0), At(u0, v1), At(u1, v1) };
    }


    /// <summary>
    /// A colour channel (BATTLE.PRG 0x800D74B0): its curve at the particle's colour phase plus one, the phase starting
    /// from the emitter's byte 0xC7 and stepping each tick as a byte, the value doubled less one, 128 a whole; no curve
    /// is a whole.
    /// </summary>
    private float Channel(Particle p, int channel)
    {
        int curve = p.Colours[channel];
        if (curve == 0) return 1;
        int value = Curve(curve, ((p.ColourPhase + p.Age) & 255) + 1);
        return value == 0 ? 0 : (value * 2 - 1) / 128f;
    }
}
