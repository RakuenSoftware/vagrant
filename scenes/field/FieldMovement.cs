// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// How the enemies move, as BATTLE.PRG moves them: the map of the room their movement reads (0x800e6f9c), the tests of
/// a tile and a step (0x800d954c, 0x800d96c8, 0x800d9b6c, 0x800e0678), the movers by kind (0x800e50a0: walking,
/// flying, hopping), the obstacle steering and its detours (0x800e3600, 0x800e2f5c), the leaps it finds (0x800e49d8,
/// 0x800d8400, 0x800db93c), the platform ride (0x800df8a4), the scripted walks of an event (0x800e5308), and the
/// commands their bodies carry out (0x800deefc, and the actor side, 0x80075554).
/// </summary>
public sealed partial class FieldBattle
{
    public sealed partial class Combatant
    {
        /// <summary>Whether its body is busy with a jump, fall, hop, landing or rising, so its AI skips its tick (body +8 bits 16-18 and 28-30, the command under way).</summary>
        public bool BodyBusy => Mover is { Busy: true } || HopState != 0 || Mind is { Settling: not 0 } or { Settled: true } or { Jump: not 0 };
        /// <summary>Its body passes through other bodies (body +0x5ac bit 11, set by an event's walk, 0x800a0a1c; 0x800a8fd4 lets it by).</summary>
        public bool PassingThrough;
        /// <summary>
        /// Its body's flag 0x10 at +0x5b0 (Guildenstern's final form's swoop, 0x800e76d4): as its body is drawn
        /// (0x800b02d8) the animation's root x angle is replaced by a quarter turn, pitching the whole model; it touches
        /// nothing else.
        /// </summary>
        public bool Pitched;
    }

    /// <summary>
    /// What an enemy's body does this frame (the actor code a command posts, 0x80075554): stand (2), turn (3, 4, 7),
    /// walk or run (9, 10, after the probe that may nudge it, 13), leap to a tile (11, 14, 18), warp (23), fly (16,
    /// 17) or glide to a point (19). A flyer's landing and rising (21, 22) run their course apart.
    /// </summary>
    public enum Body { Stand, Turn, Walk, Leap, Warp, Fly, Glide, Land, Rise }

    public sealed partial class Mind
    {
        /// <summary>Its step's reach in tiles (+0xb2, AI word bits 10-11), the height a step may rise or drop (+0xb0, bits 8-9), the tile marks it shuns (+0x32, bits 30-31) and its girth for spacing (+0x94).</summary>
        public int StepReach = 1, StepHeight, Shuns, Girth = 63;
        /// <summary>This frame's movement flags (+0 to +7): it descends beside a target out of reach, crosses zones, minds drops, moves away, keeps to steps that near its goal, may enter marked tiles, and keeps its feet (no leaps).</summary>
        public bool Descends, CrossesZones, Wary, Away, Nearer, Wades, Grounded;
        /// <summary>Its zone (+0x132) and the zone its route makes for (+0x154).</summary>
        public int Zone, Passage;
        /// <summary>It closes on its target (+0x0c), and the chase's goal stood beside it (+0x0d).</summary>
        public bool Closing;
        /// <summary>
        /// Its detour (+0x190): bit 0 under way, 1-3 its direction, 4 its side, 5-11 its life, 12-15 the cardinals it
        /// took, 16 a new side next, 17 it turned, 18-21 the free cardinals, 22-26 and 27-31 the lines it must cross.
        /// </summary>
        public uint Detour;
        /// <summary>The tile its last detour began on and how many began there (+0x194, +0x198), its run as one began (+0x199), and the frames before it looks for a leap again (+0x156).</summary>
        public (int X, int Z) DetourTile = (-1, -1);
        public int DetourStarts, LeapHold;
        public bool DetourRun;
        /// <summary>The command its body carries out and the last one given (0x1f8003c8), the direction or angle it steps along (+0x188), the tile it leaps to (+0x170), and the flight vector (+0x17c, +0x180, +0x184).</summary>
        public Body Command;
        public int Direction;
        public (int X, int Z) LeapTile;
        public Vector3 Course;
        /// <summary>Where it is bound mid-leap (+0x174, +0x176, +0x178), which others' tile tests take for it.</summary>
        public Vector3 Bound;
        /// <summary>The tile it was last nudged off a wall on (+0x9c).</summary>
        public (int X, int Z)? Nudged;
        /// <summary>The platform it rides, two more (+0x19b, 0 none), and the dock it makes for (+0x19c).</summary>
        public int Riding;
        public (int X, int Z, int Platform)? Dock;
        /// <summary>A flyer: aloft (its body's 0x200000), its frames facing aloft (+0x228), the frames of a landing or rising, and where to.</summary>
        public bool Airborne;
        public int Aground, Pose = -1;
        /// <summary>
        /// A flyer's landing or rising (body +8 bits 28-30, 0x800a2cd4): 1 to start a landing, 2 descending, 3 waiting
        /// out the landing's animation, 4 to start rising, 5 rising, 0 none; its frames left and the way left (body
        /// +0x5ca, +0x34 to +0x38), the point it is bound for (+0x5ec, +0x5f0) and the facing it turns to (+0x5f2).
        /// </summary>
        public int Settling, SettleFrames, SettleFacing;
        public (int X, int Y, int Z) SettleLeft;
        public Vector3 SettleTo;
        /// <summary>The landing or rising ended this frame: its command is still under way to the AI until the next frame's actor update.</summary>
        public bool Settled;
        /// <summary>
        /// Its jump (body +8 bits 16-18, 0x800ac690): 2 crouching, 3 in the air, 4 recovering, 0 none; the crouch's
        /// frames and the recovery's wait (+0x11), the step a frame (+0x34 to +0x38), the frames before it looks for
        /// the floor (+0x1816) and how it does (+0x181a: 2 a jump, 0 after a bounce off the ceiling), the point it jumps
        /// for (+0x1814, +0x1818), the animation set (+0x5b0 bits 0-1) and its marks of a leaper (bit 6) and flyer (5)
        /// at body +0xa.
        /// </summary>
        public int Jump, JumpWait, JumpFrames, JumpMode, JumpX, JumpZ, JumpSet, JumpMarks;
        public (int X, int Y, int Z) JumpStep;
        /// <summary>
        /// The upper-body twist and its pitch (body +0x14, +0x16) and how they move each frame (+0x17f4 to +0x17fc,
        /// 0x8009fc60): mode -2 back to none, -3 toward the goal it turns to less its facing, -4 toward a value; the
        /// pitch's and twist's values and the most they move a frame.
        /// </summary>
        public int Twist, Pitch, TwistMode = -2, PitchTarget, TwistTarget, TwistRate = 30;
        /// <summary>The look a glance turns its upper body to this tick (command 16, 0x800e4180), null for none.</summary>
        public int? Glance;
        /// <summary>Whether its model twists at all (a bone of part group 2, 0x800a152c, AI +0x2a); without one the twist stays 0.</summary>
        public bool Twists;
        /// <summary>The body's turn under way (body +0x18 frames left, +0x3e angle left), stepped once a frame (0x800b002c).</summary>
        public int TurnFrames, TurnLeft;
        /// <summary>A body turn straight to an angle (AI command 3, 0x800df4c8) in place of the turn head first (command 4).</summary>
        public bool BodyTurn;
        /// <summary>The actor code posted for its body (node +9) with its value (+12), and whether it waits for the actor update (node +8 = 0x80).</summary>
        public int Code, CodeValue;
        public bool Posted;
        /// <summary>The turn in place set its body still this frame (motion kind 6, 0x800a249c), so it does not settle to idle.</summary>
        public bool TurningInPlace;
        /// <summary>The scripted walk that holds its AI (+0x18c): the walk's own record, and the point it walks to (+0x160).</summary>
        public Mind? Script;
        public (int X, int Z) ScriptPoint;

        /// <summary>The chase's goal was its own tile once drawn in (+0x0d), and the goal it had before (+0x48, null for none): a hopper's re-aim.</summary>
        public bool Beside;
        public (int X, int Z)? Aimed;
        /// <summary>Its body landed on a platform from the air (+0x1a, 0x800e6764), so its walls toward lower floors are opened as it walks (<see cref="RaisedEdges"/>).</summary>
        public bool Raised;
        /// <summary>This frame's walk is a plain walk (actor code 9, set by <see cref="RaisedEdges"/>): no run and no nudge off a wall.</summary>
        public bool Plain;
        /// <summary>The point it walks or hops by (+0x17c, +0x180): the same words as a flyer's course across.</summary>
        public (int X, int Z) GoalPoint { get => ((int)Course.X, (int)Course.Z); set => Course = new Vector3(value.X, Course.Y, value.Z); }

        /// <summary>Whether it routes between zones by the room's links (+0x29, AI word bit 14; a walk record's always).</summary>
        public bool Routes;

        /// <summary>
        /// A record for a scripted walk (BATTLE.PRG 0x800e51c8): the whole record copied, then a step height of 320 and a
        /// reach of 3, routing between zones, no detour; its scripted walk is itself.
        /// </summary>
        public Mind Walker()
        {
            var r = (Mind)MemberwiseClone();
            r.Relations = (uint[])Relations.Clone();
            r.Distances = (uint[])Distances.Clone();
            r.UnstickDirections = (int[])UnstickDirections.Clone();
            r.RingX = (byte[])RingX.Clone();
            r.RingZ = (byte[])RingZ.Clone();
            r.Looks = ((int, int)[])Looks.Clone();
            (r.StepHeight, r.StepReach, r.Routes, r.Detour) = (320, 3, true, 0);
            r.Script = r;
            return r;
        }
    }

    /// <summary>The tile attribute grid the movement reads (0x800f58b8, 32 a row): zone 0-3, 0x10, 0x20 pit, 0x40, 0x80 taken, traps 0x100 and 0x200, 0x800, 0x1000, 0x2000 seen everywhere, 0x8000 none.</summary>
    private ushort[]? _cells;
    /// <summary>The floors (0x800f58d0): a tile's floor over 64 in ten bits (bit 9 none), 0x2000 a pit given its edge's floor.</summary>
    private readonly ushort[] _floors = new ushort[1024];
    /// <summary>The walled steps out of each tile (the room's AI block at 0x498), null for a room without.</summary>
    private byte[]? _walls;
    /// <summary>The map's width and depth in tiles (the battle's +4), the bounds the enemies keep within (+0 to +3), the flyers' ceiling (+0x14, 0 none) and the platforms claimed (+0x0e).</summary>
    private int _width, _depth, _ceiling, _claimed;
    private (int MaxX, int MaxZ, int MinX, int MinZ) _bounds;
    /// <summary>The direction tests made this frame (0x1f8003d0: tested, free) and the actors found in each direction (0x1f8003d2, 0x1f8003d4), and whose.</summary>
    private int _tested, _free, _looked;
    private readonly int[] _found = new int[8];
    private Mind? _testing;
    /// <summary>The height a flyer cruises above the floor (+0xbe) and its clearance over a straight route (+0xbc).</summary>
    private const int Cruise = -128, Clearance = -256;

    /// <summary>
    /// Reads the room for the enemies' movement as the room loads (BATTLE.PRG 0x800e6f9c, by 0x80089888): each tile's
    /// attribute word folded into the grid (its zone, bit 27 as 0x10, the pit bit 17 as 0x20, 28 as 0x40, 31 as 0x800,
    /// 30 as 0x1000, 29 as 0x2000, the first trap on the tile at level 0, of a type under 16 and kind 0 or 1, as 0x100
    /// or 0x200, unless scenario variable 32 or 64 has cleared the traps, 0x8008d438); a tile off the room keeps the
    /// value worked out for the tile before it with 0x8000 added (retail's register, never reset) and floor 0; each
    /// tile's floor (<see cref="FieldView.TileFloor"/>) in 64-unit steps with its "no floor" flag as bit 9; then the
    /// pits are filled (0x800e6098); then the walled steps, bounds and ceiling.
    /// </summary>
    private void Survey()
    {
        _cells = new ushort[1024];
        Array.Clear(_floors);
        (_width, _depth) = _view.Tiles;
        var traps = _view.Document.GetValueOrDefault("traps", new Godot.Collections.Array()).AsGodotArray();
        int cell = 0;
        for (int i = 0; i < 1024; i++)
        {
            int x = i & 31, z = i >> 5;
            if ((uint)x >= (uint)_width || (uint)z >= (uint)_depth) { cell |= 0x8000; _cells[i] = (ushort)cell; continue; }
            long t = _view.TileAttributes(x, z);
            cell = (int)(t >> 23 & 15) | (int)(t >> 27 & 1) << 4 | (int)(t >> 17 & 1) << 5 | (int)(t >> 28 & 1) << 6
                | (int)(t >> 31 & 1) << 11 | (int)(t >> 30 & 1) << 12 | (int)(t >> 29 & 1) << 13;
            _floors[i] = (ushort)(_view.TileFloor(x, z) >> 6 & 0x3FF);
            if (!_view.TrapsCleared)
                foreach (var entry in traps)
                {
                    var trap = entry.AsGodotDictionary();
                    var at = trap["tile"].AsGodotArray();
                    if (at[0].AsInt32() != x || at[1].AsInt32() != z || trap["level"].AsInt32() != 0) continue;
                    int type = trap["type"].AsInt32() & 0xFFFF, kind = trap["value"].AsInt32() & 0xFFFF;
                    if (type < 16 && kind < 2) cell |= 1 << (8 + kind);
                    break;
                }
            _cells[i] = (ushort)cell;
        }
        // 0x800e6098: z rows outer, x inner; a pit not yet filled takes the floor of the first of its cardinal
        // neighbours (0, 2, 4, 6) on the grid not marked 0x10, 0x20 or 0x40: that tile's centre sample in 64-unit steps,
        // none (and no more looking) where it is below 0; and gives it to every pit it joins (0x800e5ec0).
        for (int z = 0; z < 32; z++)
            for (int x = 0; x < 32; x++)
            {
                if ((_cells[z * 32 + x] & 0x20) == 0 || (_floors[z * 32 + x] & 0x2000) != 0) continue;
                int level = -1;
                for (int d = 0; d < 8; d += 2)
                {
                    int nx = x + Directions[d].X, nz = z + Directions[d].Z;
                    if ((uint)nx >= 32 || (uint)nz >= 32 || (_cells[nz * 32 + nx] & 0x70) != 0) continue;
                    level = _view.Sample(nx * 128 | 64, nz * 128 | 64) >> 6;
                    break;
                }
                if (level >= 0) Fill(x, z, level);
            }
        var map = _view.Document.GetValueOrDefault("enemy_map", new Godot.Collections.Dictionary()).AsGodotDictionary();
        var bounds = map.GetValueOrDefault("bounds", new Godot.Collections.Array()).AsGodotArray();
        _bounds = bounds.Count == 4 ? (bounds[0].AsInt32(), bounds[1].AsInt32(), bounds[2].AsInt32(), bounds[3].AsInt32()) : (_width, _depth, 0, 0);
        var walls = map.GetValueOrDefault("walls", new Godot.Collections.Array()).AsGodotArray();
        _walls = null;
        if (bounds.Count == 4)
        {
            _walls = new byte[Math.Max(walls.Count, _width * _depth)];
            for (int i = 0; i < walls.Count; i++) _walls[i] = (byte)walls[i].AsInt32();
        }
        _ceiling = -map.GetValueOrDefault("ceiling", 0).AsInt32() * 64;
        _claimed = 0;
    }

    /// <summary>Fills a pit and the pits it joins with a floor (BATTLE.PRG 0x800e5ec0, depth first by the cardinals 0, 2, 4, 6).</summary>
    private void Fill(int x, int z, int level)
    {
        _floors[z * 32 + x] = (ushort)(_floors[z * 32 + x] & 0xFC00 | level & 0x3FF | 0x2000);
        for (int d = 0; d < 8; d += 2)
        {
            int nx = x + Directions[d].X, nz = z + Directions[d].Z;
            if ((uint)nx < 32 && (uint)nz < 32 && (_cells![nz * 32 + nx] & 0x20) != 0 && (_floors[nz * 32 + nx] & 0x2000) == 0) Fill(nx, nz, level);
        }
    }

    /// <summary>
    /// The crates' count on each tile (bits 10 to 12 of the floors, BATTLE.PRG 0x800e6898 and 0x800e68a0): a crate
    /// adds one to its tile as it comes to rest there and takes it away as it is lifted, pushed, breaks or goes, so
    /// the count is the room's resting crates on the tile, and each raises the tile's floor by 128.
    /// </summary>
    private void CountCrates()
    {
        if (_cells == null) return;
        for (int i = 0; i < 1024; i++) _floors[i] &= 0xE3FF;
        foreach (var (_, _, x, z, _, resting) in _view.CrateObjects)
            if (resting && (uint)x < 32 && (uint)z < 32) _floors[z * 32 + x] = (ushort)(_floors[z * 32 + x] + 0x400);
    }

    /// <summary>A tile's attribute in the grid (0x1f8003c0), 0x8000 off it.</summary>
    private int Cell((int X, int Z) t) => (uint)t.X < 32 && (uint)t.Z < 32 ? _cells![t.Z * 32 + t.X] : 0x8000;

    /// <summary>A tile's floor before its sign is kept to 15 bits (BATTLE.PRG 0x800e4320): below 0 where it has none.</summary>
    private int RawFloor((int X, int Z) t)
    {
        int v = _floors[(t.Z & 31) * 32 + (t.X & 31)];
        return (v << 22 >> 16) - ((v & 0x1C00) >> 3);
    }

    /// <summary>A tile's floor (BATTLE.PRG 0x800e42e0 and 0x800e42ec), y down.</summary>
    internal int FloorOf((int X, int Z) t) => RawFloor(t) << 17 >> 17;

    /// <summary>The steps walled out of a tile (the room's bitmap), bit d the step in direction d.</summary>
    private int WallsOf((int X, int Z) t) => _walls == null || t.Z * _width + t.X is var i && (uint)i >= (uint)_walls.Length ? 0xFF : _walls[i];

    /// <summary>
    /// Whether a step out of a tile is walled (the bitmap; or, in a room without, 0x800e4690: where the next tile has
    /// no floor, or a floor 64 or more above or below).
    /// </summary>
    internal bool Walled((int X, int Z) t, int d)
    {
        if (_walls != null) return (WallsOf(t) >> d & 1) != 0;
        var n = Step(t, d);
        return RawFloor(n) < 0 || Math.Abs(FloorOf(t) - FloorOf(n)) >= 64;
    }

    /// <summary>The direction of a step of a tile (0x1f8003e0), -1 for none, -1 for one past a neighbour too.</summary>
    internal static int Toward(int dx, int dz) => dx is < -1 or > 1 || dz is < -1 or > 1 ? -1 : new[] { 1, 0, 7, 2, -1, 6, 3, 4, 5 }[(dz + 1) * 3 + dx + 1];

    /// <summary>Where another stands for the tile tests (BATTLE.PRG 0x800d954c, 0x800d98e8): bound for a tile mid-leap or warp, else where it is.</summary>
    private static Vector3 Placed(Combatant k) => k.Index != 0 && k.BodyBusy && k.Mind != null ? k.Mind.Bound : k.Actor.Position;

    /// <summary>Starts the frame's tests afresh for an actor (the scratch word cleared, 0x800db080).</summary>
    private void Afresh(Mind m)
    {
        _testing = m;
        _tested = _free = _looked = 0;
    }

    /// <summary>
    /// Whether an actor may stand on a tile (BATTLE.PRG 0x800d954c): 0 on the tile it remembers failing on, off the map
    /// or marked (0x80b0 and the marks it shuns); -1 where another, or where another is bound, stands within both
    /// girths and 128 of its floor; else 1.
    /// </summary>
    private int TileFree(Combatant c, (int X, int Z) t)
    {
        var m = c.Mind!;
        if (t == m.Memory || (uint)t.X >= (uint)_width || (uint)t.Z >= (uint)_depth || (Cell(t) & (m.Shuns << 8 | 0x80B0)) != 0) return 0;
        int cx = t.X * 128 + 64, cz = t.Z * 128 + 64, cy = FloorOf(t);
        foreach (var k in Combatants.Values)
        {
            if (k == c || k.Dead || k.Mind is not { } km || k.Index >= 16) continue;
            var at = Placed(k);
            int r = km.Girth + m.Girth;
            if (Math.Abs((int)at.X - cx) <= r && Math.Abs((int)at.Y - cy) <= 128 && Math.Abs((int)at.Z - cz) <= r) return -1;
        }
        return 1;
    }

    /// <summary>Whether it can stand on a tile (<see cref="TileFree"/> above 0).</summary>
    private bool Walkable(Combatant c, (int X, int Z) tile) => TileFree(c, tile) > 0;

    /// <summary>
    /// The actor in a direction (BATTLE.PRG 0x800d98e8), its index and one, 0 none: from a point its reach extension
    /// ahead (at the floor there, or its own height when the step is walled or stays on its tile), the first actor
    /// ahead of it on both axes within both girths and the other's reach extension, and half their heights.
    /// </summary>
    private int Ahead(Combatant c, int d)
    {
        var m = c.Mind!;
        if (_testing != m) Afresh(m);
        if ((_looked >> d & 1) != 0) return _found[d];
        var p = c.Actor.Position;
        var (dx, dz) = Directions[d];
        int px = (int)p.X + m.ReachBonus * dx, pz = (int)p.Z + m.ReachBonus * dz;
        int toward = Toward((px >> 7) - m.Tile.X, (pz >> 7) - m.Tile.Z);
        int y = toward < 0 || Walled(m.Tile, toward) ? (int)p.Y : FloorOf((px >> 7, pz >> 7));
        int found = 0;
        foreach (var k in Combatants.Values)
        {
            if (k == c || k.Dead || k.Mind is not { } km || k.Index >= 16) continue;
            var at = Placed(k);
            int ex = (int)at.X - px, ey = (int)at.Y - y, ez = (int)at.Z - pz;
            if (dx > 0 && ex < 0 || dx < 0 && ex > 0 || dz > 0 && ez < 0 || dz < 0 && ez > 0) continue;
            int r = km.Girth + km.ReachBonus + m.Girth;
            if (Math.Abs(ex) <= r && Math.Abs(ez) <= r && Math.Abs(ey) <= (m.Head + km.Head) >> 1) { found = k.Index + 1; break; }
        }
        _looked |= 1 << d;
        return _found[d] = found;
    }

    /// <summary>
    /// Whether a walker may step in a direction (BATTLE.PRG 0x800d9b6c), kept for the frame by direction (an odd one by
    /// the step it turns out to be, as retail keeps it): not with an actor ahead; free where the half-tile ahead is its
    /// own tile; else not walled, not the tile it remembers (in a room with walls), not marked 0x10 (unless it may),
    /// 0x20, 0x80 or shunned, not in another zone than its own and its route's while it keeps to zones, and no higher a
    /// class than its own.
    /// </summary>
    private bool WalkFree(Combatant c, int d)
    {
        var m = c.Mind!;
        if (_testing != m) Afresh(m);
        if ((_tested >> d & 1) != 0) return (_free >> d & 1) != 0;
        int key = d;
        bool blocked = Ahead(c, d) != 0;
        if (!blocked)
        {
            var p = c.Actor.Position;
            int hx = ((int)p.X & 0xFFFF) >> 6 & 0xFF, hz = ((int)p.Z & 0xFFFF) >> 6 & 0xFF;
            var n = ((hx + Directions[d].X) >> 1, (hz + Directions[d].Z) >> 1);
            if (n != m.Tile)
            {
                if ((d & 1) != 0) key = Toward(n.Item1 - m.Tile.X, n.Item2 - m.Tile.Z) & 7;
                if (_walls == null) blocked = Walled(m.Tile, key);
                else blocked = m.Memory == n || (WallsOf(m.Tile) >> key & 1) != 0;
                if (!blocked)
                {
                    int a = Cell(n);
                    blocked = (a & 0x10) != 0 && !m.Wades || (a & 0xA0) != 0 || (m.Shuns & a >> 8) != 0;
                    if (!blocked && m.Routes && !m.CrossesZones && (Cell(m.Tile) & 15) != (a & 15)) blocked = (m.Passage & 15) != (a & 15);
                    if (!blocked) blocked = (_floors[(m.Tile.Z & 31) * 32 + (m.Tile.X & 31)] & 0x1C00) < (_floors[(n.Item2 & 31) * 32 + (n.Item1 & 31)] & 0x1C00);
                }
            }
        }
        _tested |= 1 << key;
        _free = blocked ? _free & ~(1 << key) : _free | 1 << key;
        return !blocked;
    }

    /// <summary>
    /// Whether a flyer may move in a direction (BATTLE.PRG 0x800e0678), kept for the frame: nothing in its way
    /// (<see cref="Occupied"/>), the next tile within the bounds, not marked 0x40, not the tile it remembers, and (not
    /// kept) its cruising height there under the ceiling.
    /// </summary>
    private bool FlyFree(Combatant c, int d)
    {
        var m = c.Mind!;
        if (_testing != m) Afresh(m);
        if ((_tested >> d & 1) != 0) return (_free >> d & 1) != 0;
        var n = Step(m.Tile, d);
        bool blocked = Occupied(c, d, 1) != 0 || n.Z > _bounds.MaxZ || n.Z < _bounds.MinZ || n.X > _bounds.MaxX || n.X < _bounds.MinX
            || (Cell(n) & 0x40) != 0 || n == m.Memory;
        if (!blocked && _ceiling != 0 && FloorOf(n) + Cruise < _ceiling) return false;
        _tested |= 1 << d;
        _free = blocked ? _free & ~(1 << d) : _free | 1 << d;
        return !blocked;
    }

    /// <summary>
    /// What stands in a flyer's way in a direction (BATTLE.PRG 0x800e02b4), kept for the frame by direction alone (so
    /// a near probe answers a far one): from a point its reach extension ahead (none for a scale of 0), a platform
    /// within its girth and 96 and half its height and 32 (1), else an actor other than Ashley within both girths and
    /// the other's reach extension and half their heights (its index and one); 0 none. With a scale, only those ahead.
    /// </summary>
    private int Occupied(Combatant c, int d, int scale)
    {
        var m = c.Mind!;
        if (_testing != m) Afresh(m);
        if ((_looked >> d & 1) != 0) return _found[d];
        var p = c.Actor.Position;
        var (dx, dz) = Directions[d];
        int px = (int)p.X + dx * m.ReachBonus * scale, py = (int)p.Y, pz = (int)p.Z + dz * m.ReachBonus * scale;
        bool Behind(int ex, int ez) => scale != 0 && (dx > 0 && ex < 0 || dx < 0 && ex > 0 || dz > 0 && ez < 0 || dz < 0 && ez > 0);
        int found = 0;
        for (int i = _view.PlatformCount - 1; i >= 0 && found == 0; i--)
        {
            if (_view.PlatformState(i) is not { Off: false } platform) continue;
            int ex = (int)platform.Now.X - px, ez = (int)platform.Now.Z - pz;
            if (Behind(ex, ez)) continue;
            if (Math.Abs(ex) <= m.Radius + 96 && Math.Abs(ez) <= m.Radius + 96 && Math.Abs((int)platform.Now.Y - py) <= (m.Head + 32) >> 1) found = 1;
        }
        if (found == 0)
            foreach (var k in Combatants.Values)
            {
                if (k == c || k.Dead || k.Index is 0 or >= 16 || k.Mind is not { } km) continue;
                var at = k.Actor.Position;
                int ex = (int)at.X - px, ez = (int)at.Z - pz;
                if (Behind(ex, ez)) continue;
                int r = km.Radius + m.Radius + km.ReachBonus;
                if (Math.Abs(ex) <= r && Math.Abs(ez) <= r && Math.Abs((int)at.Y - py) <= (m.Head + km.Head) >> 1) { found = k.Index + 1; break; }
            }
        _looked |= 1 << d;
        return _found[d] = found;
    }

    /// <summary>
    /// Whether a step is lawful (BATTLE.PRG 0x800d96c8): 1 yes, 0 no, -1 too near the target it chases. Never for one
    /// keeping its feet, onto a tile off the map, marked 0x10 or 0x20, or in another zone unless it may cross; a step
    /// beside it not with an actor ahead, and for one minding drops (or a rise) within its reach and the height it may
    /// rise or drop (8192 for one that flies or hops). One keeping to steps that near its goal steps only nearer, and
    /// not within its stop distance less 96 of the target it chases. Retail keeps the x step signed and makes the z
    /// step positive twice; so it is here.
    /// </summary>
    private int Legal(Combatant c, (int X, int Z) from, (int X, int Z) to, int d)
    {
        var m = c.Mind!;
        if (m.Grounded || (uint)to.X >= (uint)_width || (uint)to.Z >= (uint)_depth) return 0;
        int a = Cell(to);
        if ((a & 0x30) != 0 || !m.CrossesZones && (a & 15) != m.Zone) return 0;
        int dx = to.X - from.X, dz = Math.Abs(to.Z - from.Z), dy = FloorOf(to) - FloorOf(from);
        bool reach = true;
        if (dx < 2 && dz < 2)
        {
            if (Ahead(c, d) != 0) return 0;
            if (!m.Wary && dy > 0) reach = false;
        }
        if (reach)
        {
            if (dx + dz > m.StepReach) return 0;
            int h = (m.Moves & 0xF8) != 0 ? 8192 : m.StepHeight;
            if (m.Wary && dy > h || dy < -h) return 0;
        }
        if (!m.Nearer) return 1;
        if (m.Mode == 1 && At(m.ChaseTarget) is { } t)
        {
            long ex = to.X * 128 + 64 - ((int)t.Actor.Position.X & 0xFFFF), ez = to.Z * 128 + 64 - ((int)t.Actor.Position.Z & 0xFFFF), stop = m.Stop - 96;
            if (ex * ex + ez * ez < stop * stop) return -1;
        }
        return Apart(to, m.Goal) < Apart(m.Tile, m.Goal) ? 1 : 0;
    }

    /// <summary>The squared distance between two tiles in tiles (BATTLE.PRG 0x800e45f4).</summary>
    private static int Apart((int X, int Z) a, (int X, int Z) b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);

    /// <summary>
    /// A leap across a drop ahead (BATTLE.PRG 0x800e49d8), in a room with walls: where the tile ahead is lower than its
    /// own (its 15-bit floor, as the comparisons take it), the first tile beyond within its reach less one (one at least:
    /// retail tests its constant 2 in place of the cap, so no cap applies) higher than that one, if
    /// it may step there and stand there.
    /// </summary>
    private (int X, int Z)? Across(Combatant c, (int X, int Z) ahead, int d, int n)
    {
        var m = c.Mind!;
        if (_walls == null) return null;
        int low = RawFloor(ahead);
        if (low < 0) return null;
        low = low << 17 >> 17;
        if (!(FloorOf(m.Tile) < low) || n == 0) return null;
        int last = Math.Max(n - 1, 1);
        for (int k = 1; k <= last; k++)
        {
            var q = (ahead.X + k * Directions[d].X, ahead.Z + k * Directions[d].Z);
            if (FloorOf(q) >= low) continue;
            return Legal(c, m.Tile, q, d) > 0 && TileFree(c, q) > 0 ? q : null;
        }
        return null;
    }

    /// <summary>
    /// A climb or leap in blocked directions (BATTLE.PRG 0x800d8400): for the direction given (or, of two, the cardinal
    /// nearest the heading and then the other), the next tile if it may step there (taken where it may stand, -1
    /// taken), else past a pit (<see cref="OverPit"/>). Kind 1 a tile to leap to, 2 one taken, 0 none.
    /// </summary>
    private ((int X, int Z) Tile, int Kind) Climb(Combatant c, int a, int b, int h)
    {
        var m = c.Mind!;
        if (m.Grounded) return (default, 0);
        int[] ways;
        if (a == b) ways = new[] { h };
        else
        {
            int q = (h & 0xFFF) >> 10;
            if ((h & 0x3FF) > 512) q = (q + 1) & 3;
            ways = new[] { 2 * q, a == 2 * q ? b : a };
        }
        foreach (int e in ways)
        {
            var n = Step(m.Tile, e & 7);
            int r = Legal(c, m.Tile, n, e & 7);
            if (r > 0)
            {
                int v = TileFree(c, n);
                return v > 0 ? (n, 1) : v < 0 ? (n, 2) : (default, 0);
            }
            if (r < 0) return (n, 2);
            var (over, kind) = OverPit(c, n, e & 7);
            if (kind != 0) return (over, kind);
        }
        return (default, 0);
    }

    /// <summary>
    /// A leap over pit tiles (BATTLE.PRG 0x800db93c): from a pit, on in the direction to the first tile that is no pit;
    /// within its reach (three at most) the tile if it may step there and stand there (1), or it taken (2); else none.
    /// </summary>
    private ((int X, int Z) Tile, int Kind) OverPit(Combatant c, (int X, int Z) n, int e)
    {
        var m = c.Mind!;
        if ((Cell(n) & 0x20) == 0) return (n, 0);
        int k = 0;
        var q = n;
        do { q = Step(q, e); k++; } while ((Cell(q) & 0x20) != 0);
        if (k >= Math.Min(m.StepReach, 3)) return (q, 0);
        int r = Legal(c, m.Tile, q, e);
        if (r != 1) return (q, r < 0 ? 2 : 0);
        int v = TileFree(c, q);
        return (q, v > 0 ? 1 : v < 0 ? 2 : 0);
    }

    /// <summary>
    /// Whether a tile lies behind it (BATTLE.PRG 0x800e47e4), as it faces its target (or itself without one); never
    /// while it closes on its target.
    /// </summary>
    private bool Behind(Combatant c, (int X, int Z) t)
    {
        var m = c.Mind!;
        if (m.Closing) return false;
        var p = c.Actor.Position;
        int a = At(m.FaceTarget) is { } face ? Angle(p.X - face.Actor.Position.X, p.Z - face.Actor.Position.Z) : (int)MathF.Round(c.Actor.Facing);
        int turn = (3072 - a) & 0xFFF;
        return (long)Cos(turn) * (t.X - m.Tile.X) + (long)Sin(turn) * (t.Z - m.Tile.Z) < 0;
    }

    /// <summary>The steering's direction test: a flyer's (modes 2 and 3) or a walker's.</summary>
    private bool Free(Combatant c, int d, bool flyer) => flyer ? FlyFree(c, d) : WalkFree(c, d);

    /// <summary>
    /// Steers round what is in the way (BATTLE.PRG 0x800e3600) for a heading, going to the goal (modes 0 and 2) or
    /// along it (1 and 3), a walker's (0, 1) or a flyer's (2, 3): following a detour under way; a leap across a drop
    /// ahead; on an exact cardinal, along it if free, else a climb or leap, else a detour (a walker moving along it,
    /// not away, faces instead); between two cardinals, along the heading if both and the diagonal are free, else the
    /// nearer free cardinal; both blocked, a climb or leap, else a detour; one blocked, a climb or leap that way, and a
    /// detour where the free one brings it no nearer (or, moving away, the heading lies within 15 degrees of the
    /// blocked one), else the free one.
    /// </summary>
    private void Steer(Combatant c, int h, int mode)
    {
        var m = c.Mind!;
        bool flyer = mode >= 2;
        if ((m.Detour & 1) != 0)
        {
            int follow = Follow(c, flyer, flyer ? 12 : 11);
            if (follow < 0) { EndDetour(m); Face(c); return; }
            m.Direction = (int)(m.Detour >> 1 & 7);
            Issue(c, follow);
            return;
        }
        int q = (h & 0xFFF) >> 10;
        if ((h & 0x3FF) > 512) q = (q + 1) & 3;
        int nearest = 2 * q;
        int span = Math.Abs((nearest & 2) != 0 ? m.Goal.X - m.Tile.X : m.Goal.Z - m.Tile.Z);
        if (Across(c, Step(m.Tile, nearest), nearest, Math.Min(m.StepReach, span)) is { } across)
        {
            m.LeapTile = across;
            Issue(c, 14);
            return;
        }
        bool held = m.LeapHold is >= 1 and <= 126;
        if ((h & 0x3FF) == 0)
        {
            int cardinal = (h >> 9) & 6;
            if (Free(c, cardinal, flyer)) { m.Direction = (short)h; Issue(c, 7); return; }
            if (!held && Probe(c, 0, 0, cardinal)) return;
            if (mode == 1 && !m.Away) { Face(c); return; }
            if (Start(c, mode, () => Exact(c, cardinal, flyer))) Detoured(c);
            else Face(c);
            return;
        }
        int q0 = h >> 10, c1 = (2 * q0 + 2) & 7, c0 = (2 * q0) & 7, diagonal = (2 * q0 + 1) & 7;
        bool f1 = Free(c, c1, flyer), f0 = Free(c, c0, flyer);
        if (f1 && f0)
        {
            if (Free(c, diagonal, flyer)) { m.Direction = (short)h; Issue(c, 7); }
            else { m.Direction = nearest == c1 ? c1 : c0; Issue(c, 5); }
            return;
        }
        if (!f1 && !f0)
        {
            if (!held && Probe(c, c1, c0, h)) return;
            if (Start(c, mode, () => Both(c, c1, c0, flyer))) Detoured(c);
            else Face(c);
            return;
        }
        m.Direction = f1 ? c1 : c0;
        int blocked = f1 ? c0 : c1;
        if (!held && Probe(c, 0, 0, blocked)) return;
        bool need = mode is 0 or 2 ? (m.Direction & 2) != 0 ? m.Tile.X == m.Goal.X : m.Tile.Z == m.Goal.Z
            : m.Away && Cos((blocked * 512 - h) & 0xFFF) >= 3959;
        if (!need) { Issue(c, 5); return; }
        int free = m.Direction, side = f1 ? 1 : -1;
        if (Start(c, mode, () => One(c, free, side, flyer))) Detoured(c);
        else Face(c);
    }

    /// <summary>A detour begun: it steps along its direction (command 5).</summary>
    private void Detoured(Combatant c)
    {
        c.Mind!.Direction = (int)(c.Mind.Detour >> 1 & 7);
        Issue(c, 5);
    }

    /// <summary>
    /// Begins a detour by a starter. Going along a heading (modes 1 and 3, 0x800e3c30 to 0x800e3c48) one begins only
    /// moving away within 448 of its goal, and its lines are then the goal's column and row, one past on its far side
    /// (0x800e3bc8).
    /// </summary>
    private static bool Start(Combatant c, int mode, Func<bool> starter)
    {
        if (mode is 0 or 2) return starter();
        var m = c.Mind!;
        long dx = m.Goal.X * 128 - m.Tile.X * 128, dz = m.Goal.Z * 128 - m.Tile.Z * 128;
        if (dx * dx + dz * dz >= 0x31000 || !m.Away) return false;
        bool begun = starter();
        int x = m.Tile.X != m.Goal.X ? m.Goal.X + (m.Tile.X < m.Goal.X ? 1 : 0) : 0, z = m.Tile.Z != m.Goal.Z ? m.Goal.Z + (m.Tile.Z < m.Goal.Z ? 1 : 0) : 0;
        m.Detour = m.Detour & 0x3FFFFF | (uint)x << 22 | (uint)z << 27;
        return begun;
    }

    /// <summary>
    /// A climb or leap for blocked directions (<see cref="Climb"/>), taken: one taken makes it face, and look again for
    /// one only 254 frames on; a leap to a tile behind it, as it glances back (a 24-frame look), waits; else it leaps.
    /// </summary>
    private bool Probe(Combatant c, int a, int b, int h)
    {
        var m = c.Mind!;
        var (tile, kind) = Climb(c, a, b, h);
        if (kind == 2)
        {
            if (m.LeapHold == 0) m.LeapHold = 254;
            Face(c);
            return true;
        }
        if (kind != 1) return false;
        if (Behind(c, tile) && Look(c, 128, 2)) return true;
        m.LeapTile = tile;
        Issue(c, 14);
        return true;
    }

    /// <summary>A look about (<see cref="LookAround"/>) by its movement mode's timer, its order kept.</summary>
    private bool Look(Combatant c, int chance, int kind)
    {
        var m = c.Mind!;
        var order = m.Order;
        bool looking = LookAround(c, chance, kind, m.Mode);
        m.Order = order;
        if (looking) m.Command = Body.Stand;
        return looking;
    }

    /// <summary>
    /// Begins a detour (BATTLE.PRG 0x800e3ddc): its run kept; on its first, after one that ended, or moving away, a side
    /// at random and a look about (8 in 128); the cardinals taken forgotten and the free ones noted.
    /// </summary>
    private uint Begin(Combatant c, bool flyer)
    {
        var m = c.Mind!;
        m.DetourRun = m.Run;
        uint w = m.Detour;
        if ((w >> 5 & 0x7F) == 0 || (w & 0x10000) != 0 || m.Away)
        {
            w &= ~0x10000u;
            w = w & ~0x10u | (uint)Roll(2) << 4;
            Look(c, 8, 1);
        }
        w &= 0xFFC30FFF;
        return w | (uint)Around(c, flyer) << 18;
    }

    /// <summary>The free cardinals (BATTLE.PRG 0x800e3d5c): 0 as bit 3, 2 as 2, 4 as 1, 6 as 0.</summary>
    private int Around(Combatant c, bool flyer) =>
        (Free(c, 0, flyer) ? 8 : 0) | (Free(c, 2, flyer) ? 4 : 0) | (Free(c, 4, flyer) ? 2 : 0) | (Free(c, 6, flyer) ? 1 : 0);

    /// <summary>
    /// Commits a detour's direction (BATTLE.PRG 0x800e3cdc): five begun on one tile in a row make it wait 120 frames;
    /// its leap search starts again; it lasts 126 frames.
    /// </summary>
    private static uint Commit(Mind m, uint w, int d)
    {
        m.DetourStarts = m.Tile == m.DetourTile ? m.DetourStarts + 1 : 0;
        if (m.DetourStarts >= 5) { m.Pause = 120; m.DetourStarts = 0; }
        m.DetourTile = m.Tile;
        m.LeapHold = 0;
        return w & ~0xFEEu | (uint)(d & 7) << 1 | 126u << 5;
    }

    /// <summary>The side a detour keeps (bit 4): +1 or -1.</summary>
    private static int Side(uint w) => (w & 0x10) != 0 ? 1 : -1;

    /// <summary>
    /// A detour where both cardinals about the heading are blocked (BATTLE.PRG 0x800e3eac): the cardinal outside them
    /// on its side, else its old direction turned that way (retail's stale direction, kept); the lines it must cross
    /// are its row and column, one past toward the goal.
    /// </summary>
    private bool Both(Combatant c, int c1, int c0, bool flyer)
    {
        var m = c.Mind!;
        uint w = Begin(c, flyer);
        int t = 2 * Side(w), d = ((c1 + t) & 7) == c0 ? c0 + t : c0 + 4;
        if (!Free(c, d & 7, flyer))
        {
            d = (int)(w >> 1 & 7) + t;
            if (!Free(c, d & 7, flyer)) { m.Detour = w; return false; }
        }
        w = Commit(m, w, d);
        int z = m.Tile.Z != m.Goal.Z ? m.Tile.Z + (m.Tile.Z < m.Goal.Z ? 1 : 0) : 0, x = m.Tile.X != m.Goal.X ? m.Tile.X + (m.Tile.X < m.Goal.X ? 1 : 0) : 0;
        m.Detour = w & 0x3FFFFF | (uint)z << 27 | (uint)x << 22 | 1;
        return true;
    }

    /// <summary>
    /// A detour where one cardinal is free (BATTLE.PRG 0x800e3ff4): along it when its side is the free one's, else the
    /// way back, else that turned toward its side; one line, across its way.
    /// </summary>
    private bool One(Combatant c, int d, int side, bool flyer)
    {
        var m = c.Mind!;
        uint w = Begin(c, flyer);
        int s = Side(w), way = d;
        if (s != side)
        {
            way = (d + 4) & 7;
            if (!Free(c, way, flyer))
            {
                way = (way - 2 * s) & 7;
                if (!Free(c, way, flyer)) { m.Detour = w; return false; }
            }
        }
        Lay(m, Commit(m, w, way), way);
        return true;
    }

    /// <summary>
    /// A detour where the heading is an exact cardinal, blocked (BATTLE.PRG 0x800e40f0): the cardinal against its
    /// side, else the one opposite that, else the way back; one line, across its way.
    /// </summary>
    private bool Exact(Combatant c, int cardinal, bool flyer)
    {
        var m = c.Mind!;
        uint w = Begin(c, flyer);
        int s = Side(w), way = (cardinal - 2 * s) & 7;
        if (!Free(c, way, flyer))
        {
            way = (way + 4) & 7;
            if (!Free(c, way, flyer))
            {
                way = (cardinal + 4) & 7;
                if (!Free(c, way, flyer)) { m.Detour = w; return false; }
            }
        }
        Lay(m, Commit(m, w, way), way);
        return true;
    }

    /// <summary>The line a one-way detour must cross: its row (moving in x) or column, one past toward the goal.</summary>
    private static void Lay(Mind m, uint w, int way)
    {
        int x = 0, z = 0;
        if ((way & 2) != 0) z = m.Tile.Z + (m.Tile.Z < m.Goal.Z ? 1 : 0);
        else x = m.Tile.X + (m.Tile.X < m.Goal.X ? 1 : 0);
        m.Detour = w & 0x3FFFFF | (uint)((x | z << 5) & 0x3FF) << 22 | 1;
    }

    /// <summary>Ends a detour (BATTLE.PRG 0x800e4bb8): not under way and its life gone, 30 frames kept so its side holds.</summary>
    private static void EndDetour(Mind m) => m.Detour = m.Detour & ~0x0FE1u | 0x3C0;

    /// <summary>
    /// Follows a detour (BATTLE.PRG 0x800e2f5c): 5 to step on along its direction, 14 to leap, -1 when it ends. An
    /// actor in the wall's side, idle, stands 4 to 7 frames; not moving away, a blocked way that cannot be stepped or
    /// stood on turns it back once, the other side; it ends where the wall's side is a wall marked K or past the
    /// bounds (its chase latches cleared), once across its lines, after 126 frames, where the wall ends (it stands 4
    /// to 7 frames), or once it has gone all four ways; else, the free ways changed, it turns to the first free of the
    /// way into the wall, its own and the way from it, or leaps where it may.
    /// </summary>
    private int Follow(Combatant c, bool flyer, int k)
    {
        var m = c.Mind!;
        uint w = m.Detour;
        m.Nearer = false;
        m.Run = m.DetourRun;
        if ((w >> 5 & 0x7F) == 0) { m.Detour = w; return -1; }
        int d = (int)(w >> 1 & 7), s = Side(w), wall = (d - 2 * s) & 7;
        int ahead = Ahead(c, wall);
        if (ahead != 0 && At(ahead - 1)?.Mind is { } other && (other.Moves & 0x30) == 0 && other.Pause == 0) other.Pause = Roll(4) + 4;
        if (!m.Away && (w & 0x20000) == 0 && !Free(c, d, flyer))
        {
            var n = Step(m.Tile, d);
            if (Legal(c, m.Tile, n, d) == 0 || TileFree(c, n) <= 0)
            {
                w |= 0x20000;
                w ^= 0x10;
                w &= 0xFFFF0FFF;
                d = (d + 4) & 7;
                w = w & ~0xEu | (uint)d << 1;
                s = Side(w);
            }
        }
        int lx = (int)(w >> 15 & 0xF80), lz = (int)(w >> 27) << 7;
        var p = c.Actor.Position;
        int x = (short)(int)p.X, z = (short)(int)p.Z, gx = m.Goal.X * 128 + 64, gz = m.Goal.Z * 128 + 64;
        if (!m.Away)
        {
            var side = Step(m.Tile, wall);
            if ((Cell(side) >> k & 1) != 0 || side.X > _bounds.MaxX || side.Z > _bounds.MaxZ || side.X < _bounds.MinX || side.Z < _bounds.MinZ)
            {
                m.FaceLatch = false;
                m.Searching = false;
                m.Detour = w | 0x10000;
                return -1;
            }
            if (lz > 0 && (gz < lz && z < lz || lz < gz && lz < z)) { m.Detour = w; return -1; }
            if (lx > 0 && (gx < lx && x < lx || lx < gx && lx < x)) { m.Detour = w; return -1; }
        }
        else
        {
            if (lz > 0 && (lz < gz && z < lz || gz < lz && lz < z)) { m.Detour = w; return -1; }
            if (lx > 0 && (lx < gx && x < lx || gx < lx && lx < x)) { m.Detour = w; return -1; }
        }
        int free = Around(c, flyer);
        if (free == (int)(w >> 18 & 15)) { m.Detour = w; return 5; }
        w = w & 0xFFC3FFFF | (uint)free << 18;
        s = Side(w);
        if (Free(c, (d - 3 * s) & 7, flyer) && Free(c, (d - 2 * s) & 7, flyer))
        {
            if ((m.Moves & 0x30) == 0 && m.Pause == 0) m.Pause = Roll(4) + 4;
            m.Detour = w | 0x10000;
            return -1;
        }
        uint taken = ((w >> 12) | 1u << (int)(w >> 2 & 3)) & 15;
        w = w & 0xFFFF0FFF | taken << 12;
        if (taken == 15) { m.Detour = w | 0x10000; return -1; }
        int way = (d - 2 * s) & 7;
        for (int i = 0; i < 4; i++, way = (way + 2 * s) & 7)
        {
            if (Free(c, way, flyer))
            {
                if (way != d) w |= 0x20000;
                m.Detour = w & ~0xEu | (uint)way << 1;
                return 5;
            }
            var n = Step(m.Tile, way);
            if (Legal(c, m.Tile, n, way) != 0 && TileFree(c, n) > 0)
            {
                m.LeapTile = n;
                if (way != d) w |= 0x20000;
                m.Detour = w & ~0xEu | (uint)way << 1;
                return 14;
            }
        }
        m.Detour = w;
        return -1;
    }

    /// <summary>
    /// Gives an enemy's body a command (BATTLE.PRG 0x800deefc, by its table at 0x800f16f8): 0 stand, 5 step along a
    /// direction, 7 along an angle, 8 to 12 a flyer's glide, flight, landing and rising, 13 warp, 14 leap to a tile.
    /// </summary>
    internal void Issue(Combatant c, int code)
    {
        var m = c.Mind!;
        m.Command = code switch
        {
            5 or 7 => Body.Walk, 8 => Body.Glide, 9 or 10 => Body.Fly, 13 => Body.Warp, 14 => Body.Leap, 11 => Body.Land, 12 => Body.Rise, _ => Body.Stand,
        };
        _issued = code;
        if (code == 5) m.Direction = (short)(m.Direction << 9);
        if (code is 11 or 12)
        {
            // 0x800e4b10 and 0x800e4b18: its tile is where it lands or rises from; a landing takes the tile (0x800df458).
            m.Aground = 0;
            m.LeapTile = m.Tile;
            if (code == 11) Reserve(m.LeapTile, true);
            m.Command = code == 11 ? Body.Land : Body.Rise;
        }
        if (code == 14) Aim14(c);
    }

    /// <summary>The last command given (0x1f8003c8).</summary>
    private int _issued;

    /// <summary>
    /// Moves an enemy by its kind (BATTLE.PRG 0x800e50a0, its table at 0x800f1740): to its goal tile (mode 0) or along
    /// a heading (mode 1), walking (0x800e4b2c, 0x800e4b70), flying (0x800e0a68, 0x800e1238) or hopping (0x800e153c,
    /// 0x800e1764).
    /// </summary>
    private void Move(Combatant c, int mode, int h)
    {
        switch (c.Gait)
        {
            case Gait.Fly:
                if (mode == 0) FlyTo(c);
                else FlyAlong(c, h);
                break;
            case Gait.Hop:
                if (mode == 0) Hop(c);
                else HopAlong(c, h);
                break;
            default:
                if (mode == 0) WalkTo(c);
                else WalkAlong(c, h);
                break;
        }
    }

    /// <summary>
    /// A walker for its goal tile (BATTLE.PRG 0x800e4b2c): riding a platform, the ride; on its goal it faces; else the
    /// goal's centre is the point it walks by, and it heads there by its waypoint (<see cref="Head"/>, 0x800e19fc).
    /// </summary>
    private void WalkTo(Combatant c)
    {
        var m = c.Mind!;
        if (m.Riding != 0) { Ride(c); return; }
        if (m.Goal == m.Tile) { Face(c); return; }
        m.GoalPoint = (m.Goal.X * 128 + 64, m.Goal.Z * 128 + 64);
        Head(c, Centre(m.Goal, c.Actor.Position.Y));
    }

    /// <summary>Walks for a point by the waypoint (0x800e19fc: not away, keeping to steps that near the goal; on a platform it landed on, by its raised edges), straight along the angle to it.</summary>
    private void Head(Combatant c, Vector3 point)
    {
        var m = c.Mind!;
        m.Away = false;
        m.Nearer = true;
        if (m.Raised) { RaisedEdges(c); return; }
        var way = Waypoint(c, point);
        if (m.Riding != 0) return;
        m.Direction = (int)c.Actor.FacingToward(way);
        Issue(c, 7);
    }

    /// <summary>A walker along a heading (BATTLE.PRG 0x800e4b70): on a platform it landed on, by its raised edges; riding a platform, the ride; else it crosses zones and steers (<see cref="Steer"/>, mode 1).</summary>
    private void WalkAlong(Combatant c, int h)
    {
        var m = c.Mind!;
        if (m.Raised) { RaisedEdges(c); return; }
        if (m.Riding != 0) { Ride(c); return; }
        m.CrossesZones = true;
        Afresh(m);
        Steer(c, h & 0xFFF, 1);
    }

    /// <summary>
    /// Walks off a platform it landed on (BATTLE.PRG 0x800e1908): its own tile's walled steps are opened for the
    /// frame toward each neighbour whose floor lies below it (0x800e1850); it steers for the point it walks by (mode 0);
    /// and a step or walk found (5 or 7) is a plain walk at that heading, never run.
    /// </summary>
    private void RaisedEdges(Combatant c)
    {
        var m = c.Mind!;
        int index = m.Tile.Z * _width + m.Tile.X;
        bool walled = _walls != null && (uint)index < (uint)_walls.Length;
        byte saved = walled ? _walls![index] : (byte)0;
        if (walled)
        {
            int bits = saved, y = (int)c.Actor.Position.Y;
            for (int d = 0; d < 8; d++)
                if ((bits >> d & 1) != 0 && y < FloorOf((m.Tile.X + Directions[d].X, m.Tile.Z + Directions[d].Z))) bits &= ~(1 << d);
            _walls![index] = (byte)bits;
        }
        var (gx, gz) = m.GoalPoint;
        Steer(c, Angle((short)(int)c.Actor.Position.X - gx, (short)(int)c.Actor.Position.Z - gz) & 0xFFF, 0);
        if (walled) _walls![index] = saved;
        if (_issued is 5 or 7) { m.Run = false; m.Plain = true; }
    }

    /// <summary>
    /// The speed and command a flyer moves by (BATTLE.PRG 0x800e4764): to run, its first profile that runs (0x20) then
    /// walks (0x10), each A before B; to walk, one that walks then runs. Asked to run, retail goes round run, walk, run
    /// until one is found, which would never end were neither flag set; but only flyers come here, and a flyer is one
    /// whose two profile flag bytes (fixed as its AI record is made, 0x800e6350) hold 0x30 between them (0x800e6828),
    /// so one of the four always answers and the null is never returned.
    /// </summary>
    private static (int Speed, int Code)? Pace(Mind m)
    {
        if (m.Run)
        {
            if ((m.FlagsA & 0x20) != 0) return (m.SpeedA, 10);
            if ((m.FlagsB & 0x20) != 0) return (m.SpeedB, 10);
            if ((m.FlagsA & 0x10) != 0) return (m.SpeedA, 9);
            if ((m.FlagsB & 0x10) != 0) return (m.SpeedB, 9);
            return null;
        }
        if ((m.FlagsA & 0x10) != 0) return (m.SpeedA, 9);
        if ((m.FlagsB & 0x10) != 0) return (m.SpeedB, 9);
        if ((m.FlagsA & 0x20) != 0) return (m.SpeedA, 10);
        if ((m.FlagsB & 0x20) != 0) return (m.SpeedB, 10);
        return null;
    }

    /// <summary>The highest floor of the free steps about a heading (BATTLE.PRG 0x800e0850): one on a cardinal, else three; 0 at the lowest.</summary>
    private int StepFloor(Combatant c, int h)
    {
        var m = c.Mind!;
        int d = (h >> 9) & 6, low = 0;
        for (int i = 0; i < ((h & 0x3FF) == 0 ? 1 : 3); i++)
            if (FlyFree(c, (d + i) & 7)) low = Math.Min(low, FloorOf(Step(m.Tile, (d + i) & 7)));
        return low;
    }

    /// <summary>The highest floor along a straight route, at sixteen points (BATTLE.PRG 0x800e6a6c); 0 at the lowest.</summary>
    private int RouteFloor(int x0, int z0, int x1, int z1)
    {
        int sx = (x1 - x0) >> 4, sz = (z1 - z0) >> 4, low = 0;
        for (int i = 0; i < 16; i++)
        {
            x0 += sx;
            z0 += sz;
            low = Math.Min(low, FloorOf((x0 >> 7, z0 >> 7)));
        }
        return low;
    }

    /// <summary>
    /// When a flyer that can walk goes on foot (BATTLE.PRG 0x800e0918): near its goal, grounded, it walks; where its
    /// cruising height would be over the ceiling, aloft it lands (on a tile it may stand on) and grounded it walks.
    /// </summary>
    private bool OnFoot(Combatant c, int h, bool toGoal)
    {
        var m = c.Mind!;
        if ((m.Moves & 6) == 0) return false;
        void Walker() { if (toGoal) WalkTo(c); else WalkAlong(c, h); }
        if (toGoal && Apart(m.Goal, m.Tile) < 16)
        {
            if (m.Airborne) return false;
            Walker();
            return true;
        }
        if (_ceiling == 0 || FloorOf(m.Tile) + Cruise >= _ceiling) return false;
        if (m.Airborne)
        {
            if (TileFree(c, m.Tile) <= 0) return false;
            Unclaim(c);
            Issue(c, 11);
            return true;
        }
        Walker();
        return true;
    }

    /// <summary>Lets go of the tile it was bound for and the platform it held (BATTLE.PRG 0x800e678c).</summary>
    private void Unclaim(Combatant c)
    {
        var m = c.Mind!;
        if (m.LeapTile.X <= _bounds.MaxX && m.LeapTile.X >= _bounds.MinX && m.LeapTile.Z <= _bounds.MaxZ && m.LeapTile.Z >= _bounds.MinZ) Reserve(m.LeapTile, false);
        if (m.Riding != 0) _claimed &= -(1 << (_claimed & 31));
        m.Raised = false;
    }

    /// <summary>
    /// Marks the tile of a trap an effect has shown (0x400, BATTLE.PRG 0x800e68ec from 0x8006cdd8). The grid's high
    /// byte is tested against the marks an enemy shuns (its AI +0x32: 4, 5, 5 or 7 by AI word bits 30 and 31, table
    /// 0x80069C18, left unset when its moves (+0x15e) have 0xC0; 0x800d954c and 0x800d9b6c), all four have 4, so a walker
    /// with them keeps off it.
    /// </summary>
    public void MarkTrap(int x, int z)
    {
        if (_cells == null) Survey();
        if ((uint)x >= 32 || (uint)z >= 32) return;
        _cells![z * 32 + x] |= 0x400;
    }

    /// <summary>Whether a tile carries a mark (its grid word, BATTLE.PRG [0x800F58B8]).</summary>
    public bool Marked((int X, int Z) tile, int mark)
    {
        if (_cells == null) Survey();
        return (Cell(tile) & mark) != 0;
    }

    /// <summary>Marks a tile taken (0x80, BATTLE.PRG 0x800e4c1c) or frees it (0x800e4c28).</summary>
    private void Reserve((int X, int Z) t, bool taken)
    {
        if (_cells == null || (uint)t.X >= 32 || (uint)t.Z >= 32) return;
        _cells[t.Z * 32 + t.X] = (ushort)(taken ? _cells[t.Z * 32 + t.X] | 0x80 : _cells[t.Z * 32 + t.X] & 0xFF7F);
    }

    /// <summary>
    /// A flyer along a heading (BATTLE.PRG 0x800e1238): on foot if it should be; with no pace it faces; else it keeps
    /// its feet and steers (mode 3), then flies the way found, straight up where the next step's cruising height is
    /// above it, taking off first if grounded.
    /// </summary>
    private void FlyAlong(Combatant c, int h)
    {
        var m = c.Mind!;
        if (OnFoot(c, h, false)) return;
        if (Pace(m) is not { } pace) { Face(c); return; }
        m.Grounded = true;
        Steer(c, h, 3);
        if (_issued == 5) h = (short)m.Direction;
        else if (_issued != 7) { Unclaim(c); Face(c); return; }
        Ascend(c, h, pace.Code);
    }

    /// <summary>Flies a way found (or straight up under a higher step), taking off first if grounded.</summary>
    private void Ascend(Combatant c, int h, int code)
    {
        var m = c.Mind!;
        int a = (3072 - h) & 0xFFF;
        m.Course = StepFloor(c, h) + Cruise < (int)c.Actor.Position.Y ? new Vector3(0, -4096, 0) : new Vector3(Cos(a), 0, Sin(a));
        if (!m.Airborne) { Issue(c, 12); return; }
        m.Aground = 0;
        Issue(c, code);
    }

    /// <summary>
    /// A flyer for its goal tile (BATTLE.PRG 0x800e0a68): on foot if it should be; grounded, it takes off; with no pace
    /// it faces. Beside a target it cannot reach (its goal's 0x40), it comes down to it, or up under the ceiling. On its
    /// goal it closes on the centre, at the target's middle if the target stands there. Else it steers (mode 2): a
    /// cardinal it flies; a free straight line it follows in an arc over the route's highest floor, cutting across
    /// within a step.
    /// </summary>
    private void FlyTo(Combatant c)
    {
        var m = c.Mind!;
        if (OnFoot(c, 0, true)) return;
        if (!m.Airborne) { Issue(c, 12); return; }
        m.Grounded = true;
        if (Pace(m) is not { } pace) { Face(c); return; }
        int spd = pace.Speed;
        var p = c.Actor.Position;
        int x = (int)p.X, y = (int)p.Y, z = (int)p.Z, gx = m.Goal.X * 128 + 64, gz = m.Goal.Z * 128 + 64;
        int toward = Angle(x - gx, z - gz) & 0xFFF;
        if (m.Descends && Apart(m.Tile, m.Goal) < 2 && (Cell(m.Goal) & 0x40) != 0 && At(m.ChaseTarget) is { } target)
        {
            int ty = (int)target.Actor.Position.Y, fl = FloorOf(m.Tile);
            int t = ty >= fl ? fl + Cruise : _ceiling != 0 && _ceiling >= ty ? _ceiling : ty;
            if (Math.Abs(t - y) < spd)
            {
                int ox = m.Tile.X * 128 + 64, oz = m.Tile.Z * 128 + 64, b = (3072 - Angle(x - ox, z - oz)) & 0xFFF;
                m.Course = new Vector3(Math.Abs(x - ox) < spd ? ox : ((x << 12) + spd * Cos(b)) / 4096, t, Math.Abs(z - oz) < spd ? oz : ((z << 12) + spd * Sin(b)) / 4096);
                Issue(c, 8);
                return;
            }
            m.Course = new Vector3(0, t - y, 0);
            if (t - y > 0 && Occupied(c, 0, 0) != 0) m.Course = Vector3.Zero;
            Issue(c, pace.Code);
            return;
        }
        if (m.Goal == m.Tile)
        {
            if (!m.Descends || Occupied(c, 0, 0) != 0) { Face(c); return; }
            int s3 = Cruise + ((Cell(m.Goal) & 0x20) != 0 ? 0 : FloorOf(m.Goal));
            float ey = s3 - y;
            if (At(m.FaceTarget) is { } on && TileOf(on) == m.Goal) ey = on.Actor.Position.Y - (on.Mind?.Head ?? 128) / 2 - y;
            if (ey > 0 && s3 < y + spd) ey = 0;
            m.Course = new Vector3(Math.Abs(gx - x) < spd ? 0 : gx - x, ey, Math.Abs(gz - z) < spd ? 0 : gz - z);
            Issue(c, pace.Code);
            return;
        }
        Steer(c, toward, 2);
        if (_issued == 5)
        {
            int h = (short)m.Direction;
            int a = (3072 - h) & 0xFFF;
            m.Course = StepFloor(c, h) + Cruise < y ? new Vector3(0, -4096, 0) : new Vector3(Cos(a), 0, Sin(a));
            Issue(c, pace.Code);
            return;
        }
        if (_issued != 7) return;
        int end = FloorOf(m.Goal) + Cruise, top = RouteFloor(x, z, gx, gz) + Clearance;
        if (_ceiling != 0) top = Math.Max(top, _ceiling);
        int step = StepFloor(c, toward) + Cruise;
        top = Math.Min(top, step);
        int dist = Root((long)(gx - x) * (gx - x) + (long)(gz - z) * (gz - z));
        if (y + spd < top)
        {
            m.Course = new Vector3(gx - x, spd < end - y ? end - y : 0, gz - z);
            m.Aground = 0;
            Issue(c, pace.Code);
            return;
        }
        if (spd >= dist) { FlyAlong(c, toward); return; }
        long k = end + top == 0 ? 0 : Root(((long)end << 12) / (end + top) << 12);
        if (k + 4096 == 0) { FlyAlong(c, toward); return; }
        long reach = ((long)dist << 12) / (k + 4096);
        if (reach <= 0) { FlyAlong(c, toward); return; }
        long ahead = spd - reach;
        int arc = (int)(Math.Abs(y - top) * ahead * ahead / (reach * reach)) + top;
        int bearing = (3072 - toward) & 0xFFF;
        int ax = (x << 12) + spd * Cos(bearing), az = (z << 12) + spd * Sin(bearing);
        if (arc < top) arc = top;
        else if (step < arc)
        {
            if (top < y) { m.Course = new Vector3(0, -4096, 0); Issue(c, pace.Code); return; }
            arc = step;
        }
        m.Aground = 0;
        int dy = arc - y;
        if (spd < Math.Abs(dy)) { m.Course = new Vector3((ax >> 12) - x, dy, (az >> 12) - z); Issue(c, pace.Code); }
        else { m.Course = new Vector3(ax / 4096, arc, az / 4096); Issue(c, 8); }
    }

    /// <summary>
    /// A flyer facing aloft (BATTLE.PRG 0x800e2ccc's first step): it counts the frames, and after 120 one that can walk
    /// lands on a tile it may stand on instead of facing.
    /// </summary>
    private bool Hover(Combatant c)
    {
        var m = c.Mind!;
        if (!m.Airborne || m.Aground++ < 121 || (m.Moves & 6) == 0 || (m.Moves & 0x30) == 0 || TileFree(c, m.Tile) <= 0) return false;
        Unclaim(c);
        Issue(c, 11);
        return true;
    }

    /// <summary>
    /// A hopper along a heading (BATTLE.PRG 0x800e1764): of the points its reach along the heading and the other way,
    /// the one farther from the point it moves by (<see cref="Mind.GoalPoint"/>; the second on a tie), then for it.
    /// </summary>
    private void HopAlong(Combatant c, int h)
    {
        var m = c.Mind!;
        PointAt(c, m.Tile, h, m.HopReach);
        var ahead = m.Goal;
        PointAt(c, m.Tile, (h + 2048) & 0xFFF, m.HopReach);
        var (px, pz) = m.GoalPoint;
        static uint Apart2(int dx, int dz) { long a = (short)dx, b = (short)dz; return (uint)(a * a + b * b); }
        if (Apart2(m.Goal.X * 128 + 64 - px, m.Goal.Z * 128 + 64 - pz) < Apart2(ahead.X * 128 + 64 - px, ahead.Z * 128 + 64 - pz)) m.Goal = ahead;
        Hop(c);
    }

    /// <summary>
    /// Its goal a distance along a heading from a tile (BATTLE.PRG 0x800e1388): of eight tries, each an eighth of the
    /// distance (taken unsigned) nearer, the first tile it may stand on, else the last; each try's tile taken as a
    /// byte, so one past the low edge is 255; then kept within the bounds, low first.
    /// </summary>
    private void PointAt(Combatant c, (int X, int Z) from, int heading, int reach)
    {
        var m = c.Mind!;
        int a = (3072 - heading) & 0xFFF, step = (int)((uint)reach >> 3);
        (int X, int Z) tile = from;
        for (int i = 0; i < 8; i++, reach -= step)
        {
            tile = (((from.X * 128 + 64 + (reach * Cos(a) >> 12)) >> 7) & 0xFF, ((from.Z * 128 + 64 + (reach * Sin(a) >> 12)) >> 7) & 0xFF);
            if (TileFree(c, tile) > 0) break;
        }
        int x = tile.X < _bounds.MinX ? _bounds.MinX : _bounds.MaxX < tile.X ? _bounds.MaxX : tile.X;
        int z = tile.Z < _bounds.MinZ ? _bounds.MinZ : _bounds.MaxZ < tile.Z ? _bounds.MaxZ : tile.Z;
        m.Goal = (x, z);
    }

    /// <summary>
    /// A hopper for its goal tile (BATTLE.PRG 0x800e153c): on it, it faces; beyond its reach the goal comes in to its
    /// reach (<see cref="PointAt"/>); one that also walks walks (for the goal it had before a chase drew it in, if
    /// any) when the chase drew it onto its own tile or the goal is within four tiles; else it counts the frames, and
    /// from the sixth (until it hops) warps (command 13) to the goal, or the first cardinal tile beside it it may stand
    /// on, else faces.
    /// </summary>
    private void Hop(Combatant c)
    {
        var m = c.Mind!;
        var p = c.Actor.Position;
        if (m.Goal == m.Tile) { Face(c); return; }
        int x = (int)p.X, z = (int)p.Z, a = Angle(x - m.Goal.X * 128 - 64, z - m.Goal.Z * 128 - 64);
        long dx = (short)(m.Goal.X * 128 + 64 - x), dz = (short)(m.Goal.Z * 128 + 64 - z);
        if ((uint)(m.HopReach * m.HopReach) < (uint)(dx * dx + dz * dz)) PointAt(c, m.Tile, a, m.HopReach);
        if ((m.Moves & 6) != 0 && (m.Beside || (uint)Apart(m.Goal, m.Tile) < 16))
        {
            if (m.Aimed is { } aimed) m.Goal = aimed;
            WalkTo(c);
            return;
        }
        c.HopCount = (ushort)(c.HopCount + 1);
        if (c.HopCount < 6) { Face(c); return; }
        var goal = m.Goal;
        if (TileFree(c, goal) <= 0)
        {
            (int X, int Z)? free = null;
            for (int d = 0; d < 8 && free == null; d += 2)
            {
                var n = ((goal.X + Directions[d].X) & 0xFF, (goal.Z + Directions[d].Z) & 0xFF);
                if (TileFree(c, n) > 0) free = n;
            }
            if (free is not { } beside) { Face(c); return; }
            m.Goal = beside;
        }
        m.LeapTile = m.Goal;
        c.HopCount = 0;
        Issue(c, 13);
    }

    /// <summary>
    /// The ride on a platform (BATTLE.PRG 0x800df8a4): on the platform it gives it up and stands; else it waits for the
    /// platform to stand at its dock, then leaps for the tile the dock serves, if it may stand there.
    /// </summary>
    private void Ride(Combatant c)
    {
        var m = c.Mind!;
        if (_view.PlatformUnder(c.Actor.Position) >= 0)
        {
            m.Dock = null;
            _claimed &= ~(1 << m.Riding);
            m.Riding = 0;
            m.Command = Body.Stand;
            return;
        }
        int i = m.Dock is { } dock ? Docked((dock.X, dock.Z), false) : -1;
        if (i < 0) { Face(c); return; }
        var n = Docks()[i].Access;
        if (TileFree(c, n) <= 0) { Face(c); return; }
        m.Dock = null;
        _claimed &= ~(1 << ((i >> 1) + 2));
        m.Riding = 0;
        m.LeapTile = n;
        Issue(c, 14);
    }

    /// <summary>The room's platform docks (room sub-section 5 at 0x454): each an access tile and where its platform stands for it.</summary>
    private List<((int X, int Z) Access, (int X, int Z) Dock)> Docks()
    {
        var docks = new List<((int, int), (int, int))>();
        foreach (var entry in _view.Document.GetValueOrDefault("platform_docks", new Godot.Collections.Array()).AsGodotArray())
        {
            var e = entry.AsGodotArray();
            docks.Add(((e[0].AsInt32(), e[1].AsInt32()), (e[2].AsInt32(), e[3].AsInt32())));
        }
        return docks;
    }

    /// <summary>
    /// The dock entry for a tile (BATTLE.PRG 0x800dfaf8), by its access tile or its dock, whose platform stands at its
    /// dock now; -1 none.
    /// </summary>
    private int Docked((int X, int Z) tile, bool access)
    {
        var docks = Docks();
        for (int i = 0; i < docks.Count; i++)
        {
            if ((access ? docks[i].Access : docks[i].Dock) != tile || _view.PlatformState(i >> 1) is not { Off: false } platform) continue;
            if (((int)platform.Now.X >> 7, (int)platform.Now.Z >> 7) == docks[i].Dock) return i;
        }
        return -1;
    }

    /// <summary>
    /// Where a target on a platform is reached from (BATTLE.PRG 0x8008e2d4 and 0x800dfa54): the access tile of the dock
    /// its platform makes for, or its first.
    /// </summary>
    private (int X, int Z) Aim(Combatant c, Combatant t)
    {
        var m = c.Mind!;
        int p = _view.PlatformUnder(t.Actor.Position);
        if (p < 0 || !(m.Routes || (m.Moves & 0x38) != 0) || _view.PlatformState(p) is not { } platform) return TileOf(t);
        var docks = Docks();
        var bound = ((int)platform.Bound.X >> 7, (int)platform.Bound.Z >> 7);
        if (2 * p + 1 >= docks.Count) return TileOf(t);
        return docks[2 * p].Dock == bound ? docks[2 * p].Access : docks[2 * p + 1].Dock == bound ? docks[2 * p + 1].Access : docks[2 * p].Access;
    }

    /// <summary>
    /// Where an enemy heads for to reach a point (BATTLE.PRG 0x800e19fc and the link handler 0x800dfbcc): in the point's
    /// zone, or not routing, straight at it; else for the nearer to it of the two connector tiles the room links its
    /// zone to the point's by, along z first then x (0x800def30); and on the connector, through it into the next zone:
    /// its steps (bits 13 and 14, less one) in its direction (bits 11 and 12: octants 2, 6, 4 and 0; 0x800f175c). A
    /// connector with bit 15 is not used. One with bit 10 boards a platform: when one stands at the dock it serves and
    /// is waiting there unclaimed, the enemy claims it (Ashley does not) and makes for the dock at its other end
    /// (<see cref="Ride"/>); otherwise it waits on the connector.
    /// </summary>
    private Vector3 Waypoint(Combatant enemy, Vector3 point)
    {
        var m = enemy.Mind!;
        var position = enemy.Actor.Position;
        int x = (int)Mathf.Floor(position.X / 128), z = (int)Mathf.Floor(position.Z / 128);
        int tx = (int)Mathf.Floor(point.X / 128), tz = (int)Mathf.Floor(point.Z / 128);
        int from = _view.TileZone(x, z), to = _view.TileZone(tx, tz);
        if (!m.Routes || from == to) return point;
        uint link = _view.ZoneLink(to, from);
        if ((link & 0x1be0) == 0) return point;
        m.Passage = to;
        int best = -1, bestDistance = int.MaxValue;
        foreach (int half in (link & 0x1be00000) != 0 ? new[] { (int)(link & 0xFFFF), (int)(link >> 16) } : new[] { (int)(link & 0xFFFF) })
        {
            if ((half & 0x8000) != 0) continue;
            int cx = half >> 5 & 31, cz = half & 31, distance = (cx - tx) * (cx - tx) + (cz - tz) * (cz - tz);
            if (distance < bestDistance) (best, bestDistance) = (half, distance);
        }
        if (best < 0) return point;
        int connectorX = best >> 5 & 31, connectorZ = best & 31;
        if (x == connectorX && z == connectorZ && (best & 0x400) != 0)
        {
            int k = Docked((x, z), true);
            if (k >= 0 && _view.PlatformState(k >> 1) is { Waited: >= 0 } && (_claimed >> ((k >> 1) + 2) & 1) == 0)
            {
                m.Riding = (k >> 1) + 2;
                if (enemy.Index != 0) _claimed |= 1 << m.Riding;
                m.Dock = Partner((x, z));
                Face(enemy);
                return position;
            }
            return new Vector3(connectorX * 128 + 64, position.Y, connectorZ * 128 + 64);
        }
        if (x == connectorX && z == connectorZ)
        {
            int octant = new[] { 2, 6, 4, 0 }[best >> 11 & 3], steps = (best >> 13 & 3) + 1;
            var (dx, dz) = Directions[octant];
            return new Vector3((x + dx * steps) * 128 + 64, position.Y, (z + dz * steps) * 128 + 64);
        }
        return z != connectorZ ? new Vector3(position.X, position.Y, connectorZ * 128 + 64) : new Vector3(connectorX * 128 + 64, position.Y, position.Z);
    }

    /// <summary>The other end of a platform's crossing (BATTLE.PRG 0x800df9a8): the dock of the entry paired with the one this tile accesses, and its platform; the tile itself without one.</summary>
    private (int X, int Z, int Platform) Partner((int X, int Z) tile)
    {
        var docks = Docks();
        for (int i = 0; i < docks.Count; i++)
        {
            if (docks[i].Access != tile) continue;
            int j = (i & 1) == 0 ? i + 1 : i - 1;
            if (j < docks.Count) return (docks[j].Dock.X, docks[j].Dock.Z, j >> 1);
        }
        return (tile.X, tile.Z, 0);
    }

    /// <summary>
    /// The AI's command for an enemy's body (BATTLE.PRG 0x800deefc, by the AI tick at the frame's end): the order
    /// becomes a command (stand, turn, go for its goal (mode 0) or along a heading (mode 1) by its kind,
    /// <see cref="Move"/>) and is posted (node +8 = 0x80) for the next frame's actor update (<see cref="Embody"/>).
    /// A body busy landing, rising, leaping, falling or hopping takes no command.
    /// </summary>
    private void Drive(Combatant enemy)
    {
        var m = enemy.Mind!;
        if (enemy.BodyBusy) return;
        if (enemy.Suspended || m.Held || enemy.Has(Paralysis) || _phase != Phase.Free && _phase != Phase.Drawing && _phase != Phase.Sheathing) return;
        Afresh(m);
        m.Zone = m.Passage = Cell(m.Tile) & 15;
        _issued = -1;
        m.BodyTurn = m.Plain = false;
        switch (m.Order)
        {
            case Order.Stand:
                m.Command = Body.Stand;
                break;
            case Order.Turn:
                m.Command = Body.Turn;
                break;
            case Order.Path:
                m.Command = Body.Stand;
                Move(enemy, 0, 0);
                break;
            case Order.Away:
                m.Command = Body.Stand;
                Move(enemy, 1, (int)m.Heading);
                break;
            case Order.Fly:
                // Guildenstern's final form's command 8: the point it flies to, the whole way in the frame.
                m.Course = new Vector3(m.FlyTo.X, m.FlyTo.Y, m.FlyTo.Z);
                m.Command = Body.Glide;
                break;
        }
        Post(enemy);
    }

    /// <summary>
    /// Posts the command (node +8 = 0x80, +9 the actor code, +12 its value): a stand (2), or with a glance asked for
    /// the glance (6, its angle); a turn straight to an angle (AI command 3: actor code 3, the angle); the turn head
    /// first (AI command 4, 0x800df720), worked out from the facing and twist now: within a step of its goal (or on it)
    /// the last step (4, the goal), else a step of the twist (7, the twist a step on) while one with a twist has it
    /// short of 800 that way, else a step of the body (3, its facing a step on); the rest as they are.
    /// </summary>
    internal void Post(Combatant enemy)
    {
        var m = enemy.Mind!;
        int yaw = (int)MathF.Round(enemy.Actor.Facing) & 0xFFF;
        switch (m.Command)
        {
            case Body.Stand when m.Glance is { } glance:
                (m.Code, m.CodeValue) = (6, glance);
                break;
            case Body.Stand:
                m.Code = 2;
                break;
            case Body.Turn when m.BodyTurn:
                (m.Code, m.CodeValue) = (3, (int)m.TurnTo & 0xFFF);
                break;
            case Body.Turn:
            {
                int facing = (short)yaw + (short)m.Twist, goal = (int)m.TurnTo & 0xFFF, spd = (int)m.TurnRate & 0xFFFF;
                int diff = (goal - facing) & 0xFFF, dir = diff < 2048 ? 1 : -1;
                bool can = dir > 0 ? m.Twist < 800 : m.Twist > -800;
                if (diff < 1 || diff < spd || 4096 - diff < spd) (m.Code, m.CodeValue) = (4, goal);
                else if (m.Twists && can) (m.Code, m.CodeValue) = (7, (dir * spd + m.Twist) & 0xFFFF);
                else (m.Code, m.CodeValue) = (3, (dir * spd + yaw) & 0xFFFF);
                break;
            }
            case Body.Leap:
                break;
            default:
                m.Code = _issued;
                break;
        }
        m.Glance = null;
        m.Posted = true;
    }

    /// <summary>
    /// An enemy's body for a frame, before the AI's tick (BATTLE.PRG's frame: the actor update 0x80075554 carries out a
    /// posted command, then the motion pass 0x800a4d8c, then the animation pass 0x800aef94 with the turn under way and
    /// the twist, 0x800b002c): one falling or leaping carries on; a hop runs its course (vanished, it stands on its tile
    /// facing its target and reappears, 21 then 22, then stands); a flyer's landing or rising runs its course; else a
    /// posted command is carried out (<see cref="Dispatch"/>) and, with no walk this frame and no turn in place, a body
    /// coming out of a turn or a walk settles to idle (0x800a6aa0 at speed 0). Then the turn steps and the twist moves.
    /// </summary>
    internal void Embody(Combatant enemy)
    {
        var actor = enemy.Actor;
        var m = enemy.Mind!;
        bool walked = false;
        m.TurningInPlace = false;
        m.Settled = false;
        if (m.Raised && m.Jump == 0 && _view.PlatformUnder(actor.Position) < 0) m.Raised = false;
        if (m.Jump != 0) StepJump(enemy);
        else if (enemy.Mover is { Busy: true } falling)
        {
            falling.Step(null, walk: true);
            if (!falling.Busy) { Reserve(m.LeapTile, false); Play(enemy, 1, 6); }
        }
        else if (enemy.HopState != 0)
        {
            if (enemy.HopState == 1 && !_view.Materialising(actor))
            {
                actor.Position = enemy.HopTo;
                if (At(m.FaceTarget) is { } faced) actor.Facing = actor.FacingToward(faced.Actor.Position);
                _view.Materialise(actor, appear: true);
                if (Play(enemy, 21, 8, once: true) is { } set) actor.Queue($"{set}_22", 8);
                enemy.HopState = 2;
            }
            if (enemy.HopState == 2 && !_view.Materialising(actor)) { Play(enemy, 1, 6); enemy.HopState = 0; Reserve(TileOf(enemy), false); }
        }
        else if (!Settle(enemy) && m.Posted)
        {
            m.Posted = false;
            Gaze(enemy);
            walked = Dispatch(enemy);
        }
        if (!walked && _walking.Contains(enemy) && enemy.Mover is { Busy: false } && !m.Airborne && m.Command is Body.Walk or Body.Stand or Body.Turn)
        {
            _walking.Remove(enemy);
            enemy.Mover?.Halt();
        }
        if (!walked && !m.TurningInPlace && !m.Airborne && !enemy.BodyBusy)
        {
            // 0x800a6aa0 at speed 0: out of a walk or run (13 to 16) to 6, then idle, each blend 10; out of a turn (17,
            // 18) to idle 1, blend 10. (Its other animations, an action's or a reaction's, are the battle's to end.)
            int now = Pose(enemy);
            if (now is >= 13 and <= 16) { Pose(enemy, 6, 10); Queue(enemy, 1, 10); }
            else if (now is 17 or 18) Pose(enemy, 1, 10);
        }
        StepTurn(enemy);
        StepTwist(enemy);
    }

    /// <summary>
    /// Carries out a posted command (the actor update, BATTLE.PRG 0x80075554, table 0x80068918): stand (2: idle 1, or
    /// 6 with no one to face, blend 16, and any turn under way stops, 0x8009e700); a body step (3: the twist held, a
    /// turn to the value at R frames a turn, R 32 for a body under 128 wide, else 128, 0x800b6634); the last step (4:
    /// the twist toward the goal, a turn to it at the turn speed a frame, 0x800b668c); a glance (6: still, the twist
    /// toward the angle at 64 a frame); a step of the twist (7: still, the twist to the value at once); a walk or run
    /// (the twist back to none, <see cref="Stride"/>); a leap, warp, flight or glide. Returns whether it walked.
    /// </summary>
    private bool Dispatch(Combatant enemy)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        int easing = enemy.Girth > 128 ? 20 : 30;
        switch (m.Command)
        {
            case Body.Stand or Body.Turn when m.Code == 2:
                if (_walking.Remove(enemy)) enemy.Mover?.Halt();
                if (Pose(enemy) is not (99 or 199)) Pose(enemy, m.FaceTarget < 16 ? 1 : 6, 16);
                m.TurnFrames = 0;
                return false;
            case Body.Stand when m.Code == 6:
                Twist(m, -4, 0, m.CodeValue, 64);
                return false;
            case Body.Turn when m.Code == 7:
                Twist(m, -4, 0, (short)m.CodeValue, 4096);
                return false;
            case Body.Turn when m.Code == 3:
                if (m.Twists) { m.TwistMode = -4; m.PitchTarget = m.Pitch; m.TwistTarget = m.Twist; m.TwistRate = 0; }
                AimTurn(enemy, Wrap((short)m.CodeValue - (int)MathF.Round(actor.Facing)), -1, enemy.Girth < 128 ? 32 : 128);
                TurnPose(enemy, (short)m.CodeValue);
                return false;
            case Body.Turn when m.Code == 4:
            {
                Twist(m, -3, 0, m.CodeValue, easing);
                int d = Wrap(m.CodeValue - (int)MathF.Round(actor.Facing)), spd = Math.Max(1, (int)m.TurnRate);
                AimTurn(enemy, d, Math.Abs(d) / spd + (Math.Abs(d) % spd != 0 ? 1 : 0), 0);
                TurnPose(enemy, m.CodeValue);
                return false;
            }
            case Body.Walk:
                Twist(m, -2, 0, 0, easing);
                return Stride(enemy, m.Direction);
            case Body.Leap:
                Leap(enemy);
                return false;
            case Body.Warp:
                Warp(enemy);
                return false;
            case Body.Fly:
                Fly(enemy);
                return false;
            case Body.Land or Body.Rise:
                StartSettling(enemy, m.Command == Body.Land);
                return false;
            case Body.Glide:
            {
                // Actor code 19 (0x80075ca0 with 0x800b6530 and motion kind 4, 0x800a291c): there in the frame by the
                // whole of the step to the point, with no change of animation; facing its target, else along the step
                // (2048 with none across), its turn aimed afresh over 12 frames.
                int x = (int)MathF.Round(actor.Position.X), y = (int)MathF.Round(actor.Position.Y), z = (int)MathF.Round(actor.Position.Z);
                int vx = (short)((int)m.Course.X - x), vy = (short)((int)m.Course.Y - y), vz = (short)((int)m.Course.Z - z);
                int facing = At(m.FaceTarget) is { } faced ? Angle((int)faced.Actor.Position.X - x, (int)faced.Actor.Position.Z - z) + 2048 : Angle(vx, vz) + 2048;
                actor.Position = new Vector3((short)(x + vx), (short)(y + vy), (short)(z + vz));
                AimTurn(enemy, Wrap((facing & 0xFFF) - ((int)MathF.Round(actor.Facing) & 0xFFF)), 12, 0);
                return false;
            }
        }
        return false;
    }

    /// <summary>The upper body's mode, values and rate set (BATTLE.PRG 0x8009fc60), which does nothing for a model without a twist; a rate of 0 or below keeps 30 (20 for one over 128 wide) only where retail passes -1.</summary>
    private static void Twist(Mind m, int mode, int pitch, int twist, int rate)
    {
        if (!m.Twists) return;
        (m.TwistMode, m.PitchTarget, m.TwistTarget, m.TwistRate) = (mode, pitch, twist, rate);
    }

    /// <summary>An angle difference wrapped as BATTLE.PRG's turns take it (0x800a9eb4, 0x800aa850): from -2048 to 2048.</summary>
    private static int Wrap(int d)
    {
        d = (short)d;
        if (d > 2048) d -= 4096;
        if (d < -2048) d += 4096;
        return d;
    }

    /// <summary>
    /// Arms the body's turn (BATTLE.PRG 0x800aa984): the angle left, and over the frames given, or with -1 over
    /// ceil(|angle| * rate / 4096) frames (the rate R of 0x800a9eb4); 0 frames turns it at once.
    /// </summary>
    private static void AimTurn(Combatant enemy, int delta, int frames, int rate)
    {
        var m = enemy.Mind!;
        m.TurnLeft = delta;
        if (frames == -1)
        {
            long p = (long)Math.Abs(delta) * rate;
            frames = (int)(p >> 12) + ((p & 0xFFF) != 0 ? 1 : 0);
        }
        m.TurnFrames = frames;
        if (frames != 0) return;
        int v = (short)((int)MathF.Round(enemy.Actor.Facing) + delta);
        enemy.Actor.Facing = Mathf.PosMod(v - v / 4096 * 4096, 4096);
    }

    /// <summary>A frame of the body's turn (BATTLE.PRG 0x800b002c): a share of the angle left, truncated, so the last frame takes the rest.</summary>
    private static void StepTurn(Combatant enemy)
    {
        var m = enemy.Mind!;
        if (m.TurnFrames == 0) return;
        int step = (short)m.TurnLeft / m.TurnFrames;
        enemy.Actor.Facing = ((int)MathF.Round(enemy.Actor.Facing) + step) & 0xFFF;
        m.TurnLeft -= step;
        m.TurnFrames--;
    }

    /// <summary>
    /// The turn in place's animation (BATTLE.PRG 0x800a249c), not aloft and not when already facing the angle: the
    /// body is held still this frame, and unless it plays 117 or 118, 17 turning one way or 18 the other (blend 10).
    /// </summary>
    private void TurnPose(Combatant enemy, int angle)
    {
        var m = enemy.Mind!;
        if (m.Airborne) return;
        int d = Wrap(angle - (short)(int)MathF.Round(enemy.Actor.Facing));
        if (d == 0) return;
        m.TurningInPlace = true;
        if (Pose(enemy) is 117 or 118) return;
        Pose(enemy, d >= 0 ? 17 : 18, 10);
    }

    /// <summary>
    /// The twist and pitch for a frame (BATTLE.PRG 0x800b002c from 0x800b04a4, after the turn): back toward none in
    /// mode -2; otherwise toward the values (mode -4) or the goal less the facing (mode -3), wrapped and kept within
    /// 800 either way, while the body plays an animation the twist is drawn with (idle 0, 1, 6, walking 13, 14, turning
    /// 17, 18; 8 to 34 aloft), else toward none; never more than the rate a frame. It is drawn on the upper body.
    /// </summary>
    private void StepTwist(Combatant enemy)
    {
        var m = enemy.Mind!;
        if (!m.Twists) return;
        int ty = 0, tp = 0;
        if (m.TwistMode == -2 && m.Twist == 0 && m.Pitch == 0) { enemy.Actor.HeldYaw = null; return; }
        if (m.TwistMode != -2)
        {
            int a = Pose(enemy);
            if (a >= 100) a -= 100;
            bool shown = m.Airborne ? a is >= 8 and <= 34 : a is 0 or 1 or 6 or 13 or 14 or 17 or 18;
            if (shown)
            {
                (tp, ty) = m.TwistMode == -3 ? (m.PitchTarget, m.TwistTarget - ((int)MathF.Round(enemy.Actor.Facing) & 0xFFF)) : (m.PitchTarget, m.TwistTarget);
                static int Within(int v) { v = (short)v; v = v >= 2048 ? v - 4096 : v < -2048 ? v + 4096 : v; return Math.Clamp(v, -800, 800); }
                ty = Within(ty);
                tp = Within(tp);
            }
        }
        int r = m.TwistRate;
        m.Twist += Math.Clamp(ty - m.Twist, -r, r);
        m.Pitch += Math.Clamp(tp - m.Pitch, -r, r);
        enemy.Actor.HeldYaw = m.Twist == 0 ? null : m.Twist;
    }

    /// <summary>The animation an enemy's body plays now (body +0x5b8), -1 for none.</summary>
    internal static int Pose(Combatant enemy)
    {
        string clip = enemy.Actor.Player?.CurrentAnimation ?? "";
        int split = clip.LastIndexOf('_');
        return split >= 0 && int.TryParse(clip[(split + 1)..], out int number) ? number : -1;
    }

    /// <summary>
    /// Sets an enemy body's animation (BATTLE.PRG 0x800a0204): aloft, idle 1 or 6 is the airborne idle 8; one already
    /// playing is not started again; the blend doubles for a body 128 or more wide. Returns whether it plays.
    /// </summary>
    private static bool Pose(Combatant enemy, int number, int blend)
    {
        if (enemy.Mind is { Airborne: true } && number is 1 or 6) number = 8;
        if (Pose(enemy) == number) return true;
        return Play(enemy, number, enemy.Girth >= 128 ? blend * 2 : blend) != null;
    }

    /// <summary>Queues an enemy body's next animation for when this one ends (BATTLE.PRG 0x800a01c8).</summary>
    private static void Queue(Combatant enemy, int number, int blend)
    {
        foreach (string set in enemy.Set != "base" ? new[] { enemy.Set, "base" } : new[] { "base" })
            if (enemy.Actor.HasClip($"{set}_{number}")) { enemy.Actor.Queue($"{set}_{number}", blend); return; }
    }

    /// <summary>
    /// Walks or runs a frame along a heading (BATTLE.PRG 0x800def8c, carried out by the movement Ashley's is, 0x800a5280):
    /// asked to run, the first profile that runs (flags 0x84), else the first that walks (0x42; retail tests the first
    /// profile's speed byte for the second, kept); with neither it stands. Its corners a probe's step ahead (at that
    /// profile's speed) are tested for walls and rises; one walled nudges it off the wall once a tile, to its girth
    /// within the tile, at most 8 a frame. It moves at its record's speed, the second's where that profile's flags are
    /// exactly walking (2, 64) or running (4, 128); walking it faces its target as it goes, else (and running) its
    /// heading, its turn aimed afresh each frame. Returns whether it moved.
    /// </summary>
    private bool Stride(Combatant enemy, int heading)
    {
        var actor = enemy.Actor;
        var m = enemy.Mind!;
        if (enemy.Mover is not { } mover) return false;
        bool run;
        int probe;
        if (m.Run && (m.FlagsA & 0x84) != 0) (run, probe) = (true, m.SpeedA);
        else if (m.Run && (m.FlagsB & 0x84) != 0) (run, probe) = (true, m.SpeedB);
        else if ((m.FlagsA & 0x42) != 0) (run, probe) = (false, m.SpeedA);
        else if ((m.SpeedA & 0x42) != 0) (run, probe) = (false, m.SpeedB);
        else return false;
        var p = actor.Position;
        int a = (3072 - heading) & 0xFFF, x = (int)p.X, z = (int)p.Z;
        int px = x + (Cos(a) * probe >> 12), pz = z + (Sin(a) * probe >> 12), r = m.Radius, tx = m.Tile.X, tz = m.Tile.Z;
        int c0 = ((px - r) >> 7) - tx, c1 = ((px + r) >> 7) - tx, r0 = ((pz - r) >> 7) - tz, r1 = ((pz + r) >> 7) - tz;
        bool walled = false;
        foreach (var (row, col) in new[] { (r0, c0), (r1, c0), (r0, c1), (r1, c1) })
        {
            int d = Toward(col, row);
            if (d < 0) continue;
            if (_walls != null) walled |= (WallsOf(m.Tile) >> d & 1) != 0 || (_floors[((tz + row) & 31) * 32 + ((tx + col) & 31)] >> 10 & 7) > (_floors[(tz & 31) * 32 + (tx & 31)] >> 10 & 7);
            else walled |= Walled(m.Tile, d);
        }
        if (walled && !m.Plain)
        {
            int cx = tx * 128 + 64, cz = tz * 128 + 64;
            int nx = (px < cx ? cx - 64 + r : cx + 63 - r) - x, nz = (pz < cz ? cz - 64 + r : cz + 63 - r) - z;
            if ((nx | nz) != 0 && m.Nudged != m.Tile)
            {
                m.Nudged = m.Tile;
                Nudge(enemy, Math.Clamp(nx, -8, 8), Math.Clamp(nz, -8, 8));
                return true;
            }
        }
        int speed = run ? (m.FlagsB is 4 or 128 ? m.SpeedB : m.SpeedA) : (m.FlagsB is 2 or 64 ? m.SpeedB : m.SpeedA);
        if (speed <= 0) return false;
        mover.WalkSpeed = speed;
        // Walking (9) faces whom it faces, else its way; running (10) always its way (0x8007589c, 0x80075944).
        int facing = !run && At(m.FaceTarget) is { } t ? (int)actor.FacingToward(t.Actor.Position) & 0xFFF : heading & 0xFFF;
        // 0x800a6660, before the step: the turn is aimed afresh every frame it walks, the angle left to its facing over
        // 12 frames (or one frame a 32 of it for a body over 128 wide, if more), so it closes a twelfth a frame.
        int yaw = (int)MathF.Round(actor.Facing) & 0xFFF;
        if (facing != yaw)
        {
            if (m.TurnFrames == 0) AimTurn(enemy, Wrap(facing - yaw), 12, 0);
            else
            {
                int left = (short)(facing - yaw);
                m.TurnLeft = left >= 2048 ? left - 4096 : left < -2048 ? left + 4096 : left;
                m.TurnFrames = 12;
            }
            if (enemy.Girth >= 129 && Math.Abs((short)m.TurnLeft / 32) is var n && m.TurnFrames < n) m.TurnFrames = n;
        }
        mover.Step(heading & 0xFFF, walk: true);
        if (!_walking.Contains(enemy) || enemy.Runs != run)
        {
            if (!run || Play(enemy, FieldPlayer.Run, 4) == null) Play(enemy, FieldPlayer.Walk, 4);
            enemy.Runs = run;
            _walking.Add(enemy);
        }
        return true;
    }

    /// <summary>
    /// A nudge (actor code 13, its motion kind 2, 0x800a25ec): the body moves by the step where there is floor and no
    /// wall, onto the floor, or from 64 or more above it falls.
    /// </summary>
    private void Nudge(Combatant enemy, int dx, int dz)
    {
        var actor = enemy.Actor;
        var next = actor.Position + new Vector3(dx, 0, dz);
        if (_view.FloorAt(next) is not { } floor || _view.Solid(next)) return;
        actor.Position = floor - actor.Position.Y >= FieldPlayer.FallHeight ? next : next with { Y = floor };
    }

    /// <summary>
    /// A leap to a tile asked of the body (BATTLE.PRG's command 14, 0x800df5f0): within the height it may step it is
    /// the short jump (actor code 11); beyond it a hopper warps (23), a flyer leaps as one (18), a leaper (0xc0) as one
    /// (14), else the short jump. Its speed is its profile's (running, the first that runs, 0x84, else the first that
    /// walks, 0x42); with none it stands. Where it is bound (+0x174, +0x178) is the tile's centre at the floors grid's
    /// floor, and that tile is taken (by any but Ashley).
    /// </summary>
    private void Aim14(Combatant c)
    {
        var m = c.Mind!;
        int code = Math.Abs(FloorOf(m.LeapTile) - FloorOf(m.Tile)) < m.StepHeight ? 11 : (m.Moves & 8) != 0 ? 23 : (m.Moves & 0x30) != 0 ? 18 : (m.Moves & 0xC0) != 0 ? 14 : 11;
        if (code == 23) { m.Command = Body.Warp; return; }
        int speed = m.Run && (m.FlagsA & 0x84) != 0 ? m.SpeedA : m.Run && (m.FlagsB & 0x84) != 0 ? m.SpeedB
            : (m.FlagsA & 0x42) != 0 ? m.SpeedA : (m.FlagsB & 0x42) != 0 ? m.SpeedB : -1;
        if (speed < 0) { m.Command = Body.Stand; return; }
        m.Bound = new Vector3(m.LeapTile.X * 128 + 64, FloorOf(m.LeapTile), m.LeapTile.Z * 128 + 64);
        if (c.Index != 0) Reserve(m.LeapTile, true);
        m.Code = code;
        m.CodeValue = speed;
    }

    /// <summary>
    /// The body takes up a leap (actor codes 11, 14 and 18, 0x800759cc): the twist back to none, facing the tile's
    /// centre always; a short jump by a leaper or a flyer goes as theirs; a leaper is marked one, a flyer marked and
    /// shown aloft (31) first; a short jump takes the first animation set. Then the jump (<see cref="Launch"/>).
    /// </summary>
    private void Leap(Combatant enemy)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        _walking.Remove(enemy);
        Twist(m, -2, 0, 0, enemy.Girth > 128 ? 20 : 30);
        int x = (int)MathF.Round(actor.Position.X), z = (int)MathF.Round(actor.Position.Z), cx = m.LeapTile.X * 128 + 64, cz = m.LeapTile.Z * 128 + 64;
        int facing = Angle(cx - x, cz - z) + 2048;
        int code = m.Code;
        if (code == 11) code = (m.JumpMarks & 0x40) != 0 ? 14 : (m.JumpMarks & 0x20) != 0 ? 18 : 11;
        if (code == 11) m.JumpSet = 1;
        else if (code == 14) m.JumpMarks = m.JumpMarks & 0x9F | 0x40;
        else { m.JumpMarks |= 0x60; Pose(enemy, 31, 8); }
        Launch(enemy, m.CodeValue, facing);
        StepJump(enemy);
    }

    /// <summary>The floor a jump lands on at a point (BATTLE.PRG 0x800a3500): the collision floor, or a crate's top on the tile where higher.</summary>
    internal int JumpFloor(int x, int z)
    {
        int h = _view.Sample(x, z) << 17 >> 17;
        foreach (var (_, _, tx, tz, bottom, resting) in _view.CrateObjects)
            if (resting && tx == x >> 7 && tz == z >> 7 && (int)bottom - 128 < h) h = (int)bottom - 128;
        return h;
    }

    /// <summary>
    /// The floor under a point in the air for a jump (BATTLE.PRG 0x800a6ee8, its first way): the collision floor
    /// (-3000 off it), a crate's top on the tile, 3000 over a pit, or a platform's top within 64 across and no more
    /// than 256 above the body, whichever is highest.
    /// </summary>
    private int AirFloor(int x, int y, int z)
    {
        short sample = _view.Sample(x, z);
        if (sample < 0) return -3000;
        int h = JumpFloor(x, z);
        if ((_view.TileAttributes(x >> 7, z >> 7) >> 17 & 1) != 0 && h >= 0) h = 3000;
        for (int i = _view.PlatformCount - 1; i >= 0; i--)
        {
            if (_view.PlatformState(i) is not { Off: false } platform) continue;
            int ex = (int)platform.Now.X, ez = (int)platform.Now.Z, top = (int)platform.Now.Y;
            if (ex - 64 <= x && x < ex + 64 && ez - 64 <= z && z < ez + 64 && top >= y - 256) { if (top < h) h = top; break; }
        }
        return h;
    }

    // BATTLE.PRG's jump tables (0x800e90ac, 0x800e90b4): frames held at the top and the lift over the higher end, by
    // the kind of jump (a drop 2, a small rise or level 1, a big rise 0; a floating body's 5, 4, 3); and the jump's
    // animations by its set (0x800e92e8 the crouch's end, 0x800e92f0 near the top, 0x800e92f8 falling, 0x800e9300 the
    // landing).
    private static readonly int[] HoldFrames = { 5, 5, 5, 10, 12, 12 }, Lifts = { 63, 63, 63, 30, 42, 42 };
    private static readonly int[] CrouchEnds = { 44, 48, 53, 0, 35, 32, 92 }, Tops = { 49, 49, 54, 0, 36, 33, 92 }, Falls = { 45, 50, 55, 0, 38, 34, 92 }, Landings = { 46, 51, 56, 0, 31, 36, 93 };

    /// <summary>
    /// Launches a jump to the tile it is bound for (BATTLE.PRG 0x800a208c): it crouches 2 frames when its speed is its
    /// run's (the second profile's when faster than the first) or more, else 6; its speed is at least 18; the point is
    /// the tile's centre at its floor (<see cref="JumpFloor"/>). Dropping, the arc's frames are those gravity (6 a frame,
    /// halved into the fall) takes to cover the drop and the lift, plus those held, no fewer than its speed across
    /// needs (the hold growing by half the lack); rising or level, those of the climb to the lift above the higher end,
    /// plus those held, growing the hold until the step across is within its speed (and it cries out, its sound 12).
    /// Each frame's step across is the distance over the frames, truncated. It plays 47 (blend 4) and turns to face the
    /// centre at 8 frames a turn.
    /// </summary>
    private void Launch(Combatant enemy, int speed, int facing)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        if (m.Jump != 0) return;
        int quick = m.SpeedB > m.SpeedA ? m.SpeedB : 255;
        m.JumpWait = speed >= quick ? 2 : 6;
        if (speed < 18) speed = 18;
        int x = (short)(int)MathF.Round(actor.Position.X), y = (short)(int)MathF.Round(actor.Position.Y), z = (short)(int)MathF.Round(actor.Position.Z);
        int tx = m.LeapTile.X * 128 + 64, tz = m.LeapTile.Z * 128 + 64, ty = JumpFloor(tx, tz);
        (m.JumpX, m.JumpZ) = (tx, tz);
        int dx = tx - x, dz = tz - z, dy = ty - y, t0 = Math.Max(Math.Abs(dx), Math.Abs(dz)) / speed;
        const int g = 6;
        int frames, vx, vy, vz;
        if (dy > 0)
        {
            int hold = HoldFrames[2];
            dy += Lifts[2];
            int vel = 0, fall = 0;
            frames = 0;
            if (dy > 0) do { vel += g; fall += vel >> 1; frames++; } while (fall < dy);
            frames += hold;
            if (frames < t0) { hold = hold + 1 + ((t0 - frames) >> 1); frames = t0; }
            (vx, vz, vy) = (dx / frames, dz / frames, -(hold + 1) * g / 2);
        }
        else
        {
            if (enemy.Index != 0 && actor.Position.Y < 3000) _view.PlayActorSound(actor, $"c{actor.Character:X2}_12_p128");
            int k = dy >= -64 ? 1 : 0, hold = HoldFrames[k], h = dy - Lifts[k];
            while (true)
            {
                int vel = 0, rise = 0;
                frames = 0;
                if (h < 0) do { vel += g; rise -= vel >> 1; frames++; } while (h < rise);
                frames += hold;
                (vx, vz, vy) = (dx / frames, dz / frames, -vel / 2);
                if (Math.Abs(vx) <= speed && Math.Abs(vz) <= speed) break;
                hold++;
                h -= (hold + 1) * g / 2;
            }
        }
        m.JumpStep = (vx, vy, vz);
        m.JumpFrames = frames;
        m.JumpMode = 2;
        Pose(enemy, 47, 4);
        m.Jump = 2;
        AimTurn(enemy, Wrap((facing & 0xFFF) - ((int)MathF.Round(actor.Facing) & 0xFFF)), -1, 8);
    }

    /// <summary>
    /// A frame of a jump (BATTLE.PRG 0x800ac690). Crouching, its frames run down and at the last but one it plays the
    /// crouch's end (blend 8); the next frame it is in the air. In the air, gravity adds 3 to its fall first; rising
    /// into the ceiling (192 above it) it bounces down, and stops across and lets its tile go; it plays the falling
    /// animation (blend 8) once falling faster than 9, or the top's (blend 16) between 9 either way; it does not look
    /// for the floor while rising or before its frames are out, and then each frame keeps short of the centre across
    /// and lands where the next step would meet the floor (<see cref="AirFloor"/>): on it there, as far across as it
    /// came, playing the landing (blend 8), grounded. Recovering, it waits out the landing's blend (7 frames, 15 for a
    /// body 128 or more wide), then idles (blend 8), lets the tile it stands on go and is done.
    /// </summary>
    private void StepJump(Combatant enemy)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        // The animation set (0x800ac690): 5 aloft (a flyer's leap), else the jump's own.
        int set = m.Airborne ? 5 : m.JumpSet;
        switch (m.Jump)
        {
            case 2:
                if (m.JumpWait > 0)
                {
                    m.JumpWait--;
                    if (m.JumpWait < 2) { Pose(enemy, CrouchEnds[set], 8); m.JumpWait = 0; }
                    return;
                }
                m.Jump = 3;
                return;
            case 3:
            {
                int x = (short)(int)MathF.Round(actor.Position.X), y = (short)(int)MathF.Round(actor.Position.Y), z = (short)(int)MathF.Round(actor.Position.Z);
                var (vx, vy, vz) = m.JumpStep;
                vy += 3;
                if (vy < 0 && y + vy - 192 < _view.CeilingAt(x, z))
                {
                    vy = -vy;
                    if (enemy.Index != 0) { vx = vz = 0; m.JumpMode = 0; Reserve(m.LeapTile, false); }
                }
                if (vy > 9) Pose(enemy, Falls[set], 8);
                else if (vy >= -9) Pose(enemy, Tops[set], 16);
                m.JumpStep = (vx, vy, vz);
                int? onto = null;
                if (m.JumpMode == 2)
                {
                    --m.JumpFrames;
                    // Coming down onto a platform it is not on (0x800a9378's second way, 128 below to just under it):
                    // it lands on its top, and is marked as landed there (0x800e6764), its tile let go.
                    int nx = x + vx, ny = y + vy, nz = z + vz, under = _view.PlatformUnder(actor.Position);
                    for (int i = _view.PlatformCount - 1; i >= 0 && vy >= 0 && onto == null; i--)
                    {
                        if (i == under || _view.PlatformState(i) is not { Off: false } platform) continue;
                        int ex = (int)platform.Now.X, ez = (int)platform.Now.Z, top = (int)platform.Now.Y;
                        if (ex - 64 <= nx && nx < ex + 64 && ez - 64 <= nz && nz < ez + 64 && ny - 128 <= top && top < ny) onto = top;
                    }
                    if (onto != null) { m.Raised = true; Reserve(m.LeapTile, false); }
                }
                bool look = onto != null || (m.JumpMode switch { 2 => m.JumpFrames <= 0 && vy >= 0, _ => vy >= 0 });
                if (!look) { actor.Position = new Vector3((short)(x + vx), (short)(y + vy), (short)(z + vz)); return; }
                if (m.JumpMode == 2)
                {
                    if (vx > 0 && m.JumpX < x + vx || vx < 0 && x + vx < m.JumpX) vx = 0;
                    if (vz > 0 && m.JumpZ < z + vz || vz < 0 && z + vz < m.JumpZ) vz = 0;
                    m.JumpStep = (vx, vy, vz);
                }
                int floor = onto ?? AirFloor(x + vx, y, z + vz);
                if (y + vy < floor) { actor.Position = new Vector3((short)(x + vx), (short)(y + vy), (short)(z + vz)); return; }
                actor.Position = new Vector3((short)(x + vx), floor, (short)(z + vz));
                m.JumpMode = 0;
                Pose(enemy, Landings[set], 8);
                m.Airborne = false;
                m.JumpWait = (enemy.Girth >= 128 ? 16 : 8) - 1;
                m.Jump = 4;
                return;
            }
            case 4:
                if (--m.JumpWait > 0) return;
                Pose(enemy, 1, 8);
                Reserve(TileOf(enemy), false);
                m.Jump = 0;
                m.Settled = true;
                return;
        }
    }

    /// <summary>A warp (command 13, actor code 23, 0x800df4ec and 0x800a29e8): it fades out (19 then 20) to reappear on the tile, taking it until it lands.</summary>
    private void Warp(Combatant enemy)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        var spot = Centre(m.LeapTile, 0);
        if (_view.FloorAt(spot) is not { } landing) return;
        Reserve(m.LeapTile, true);
        enemy.HopTo = m.Bound = spot with { Y = landing };
        enemy.HopState = 1;
        _view.Materialise(actor, appear: false);
        if (Play(enemy, 19, 8, once: true) is { } set) actor.Queue($"{set}_20", 8);
    }

    /// <summary>
    /// Flies a frame (AI commands 9 and 10: actor codes 16 and 17, 0x80075b70 and 0x80075c2c, then motion kind 3,
    /// 0x800a2790): the twist back to none; the flight vector made 4096 long in all three axes (the GTE's normalise),
    /// moved along at its record's speed (the second profile's where its flags are exactly 16 walking or 32 running),
    /// each axis's step truncated, through anything, and aloft; it faces its target, else its way (2048 straight up or
    /// down), its turn aimed afresh over 12 frames; it plays its airborne idle with no speed, else rising (38), level
    /// (37) or sinking (40) by its step down, blend 4, set again whenever another has taken over.
    /// </summary>
    private void Fly(Combatant enemy)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        Twist(m, -2, 0, 0, enemy.Girth > 128 ? 20 : 30);
        int speed = m.Code == 10 ? (m.FlagsB == 32 ? m.SpeedB : m.SpeedA) : (m.FlagsB == 16 ? m.SpeedB : m.SpeedA);
        var (nx, ny, nz) = Normalise((int)m.Course.X, (int)m.Course.Y, (int)m.Course.Z);
        int x = (int)MathF.Round(actor.Position.X), y = (int)MathF.Round(actor.Position.Y), z = (int)MathF.Round(actor.Position.Z);
        int facing = At(m.FaceTarget) is { } t ? Angle((int)t.Actor.Position.X - x, (int)t.Actor.Position.Z - z) + 2048 : Angle(nx, nz) + 2048;
        long s = (long)speed << 12;
        int dx = (int)(nx * s / (1 << 24)), dy = (int)(ny * s / (1 << 24)), dz = (int)(nz * s / (1 << 24));
        actor.Position = new Vector3((short)(x + dx), (short)(y + dy), (short)(z + dz));
        m.Airborne = true;
        m.JumpMarks |= 0x20;
        AimTurn(enemy, Wrap((facing & 0xFFF) - ((int)MathF.Round(actor.Facing) & 0xFFF)), 12, 0);
        int pose = s <= 0 ? 8 : dy < -2 ? 38 : dy < 3 ? 37 : 40;
        if (Pose(enemy) != pose) Pose(enemy, pose, 4);
        _walking.Add(enemy);
    }

    /// <summary>
    /// Starts a flyer's landing (actor code 21, 0x800b65e8: state 1) or rising (22, 0x800b65f4: state 4) and runs its
    /// first frame (0x800a2cd4). It turns to face its target, else (retail's slip, the point being divided away) the
    /// map's corner, at 8 frames a turn (0x800a9eb4). Landing it plays 9 (blend 8) and is bound for its tile's centre
    /// at the ground there; rising it plays 7 (blend 8) with 37 queued, is aloft, and is bound for the centre at its
    /// cruising height. The way is covered at 8 frames per 128 units (body +0x5c2, 8 for every body: 0x8009bab0), the
    /// frames ceil(root of the way squared * 8 / 128) (0x800aa290, 0x800aa698); with none it is there at once and done.
    /// </summary>
    private void StartSettling(Combatant enemy, bool landing)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        int x = (short)(int)MathF.Round(actor.Position.X), y = (short)(int)MathF.Round(actor.Position.Y), z = (short)(int)MathF.Round(actor.Position.Z);
        int cx = m.LeapTile.X * 128 + 64, cz = m.LeapTile.Z * 128 + 64;
        int ty = landing ? Ground(cx, cz) : FloorOf(m.LeapTile) + Cruise;
        m.SettleTo = new Vector3(cx, ty, cz);
        m.SettleFacing = At(m.FaceTarget) is { } t ? Angle((int)t.Actor.Position.X - x, (int)t.Actor.Position.Z - z) + 2048
            : landing ? Angle(-m.LeapTile.X, -m.LeapTile.Z) + 2048 : Angle(-x, -z) + 2048;
        if (landing) Pose(enemy, 9, 8);
        else { Pose(enemy, 7, 8); Queue(enemy, 37, 8); }
        int dx = (short)(cx - x), dy = (short)(ty - y), dz = (short)(cz - z);
        int frames = 0;
        {
            long p = (long)Root((long)Math.Abs((long)dx * dx) + Math.Abs((long)dz * dz) + Math.Abs((long)dy * dy)) * 8;
            frames = (int)(p >> 7) + ((p & 0x7F) != 0 ? 1 : 0);
        }
        AimTurn(enemy, Wrap((m.SettleFacing & 0xFFF) - ((int)MathF.Round(actor.Facing) & 0xFFF)), -1, 8);
        m.SettleLeft = (dx, dy, dz);
        m.SettleFrames = frames;
        if (!landing) m.Airborne = true;
        if (frames == 0)
        {
            actor.Position = new Vector3((short)(x + dx), (short)(y + dy), (short)(z + dz));
            if (landing) Landed(enemy);
            else Pose(enemy, 1, 8);
            m.Settled = true;
            return;
        }
        m.Settling = landing ? 2 : 5;
    }

    /// <summary>
    /// The ground under a tile's centre as a landing takes it (BATTLE.PRG 0x800a6ee8, its fourth way): the collision
    /// floor there, a crate on the tile its top instead, 3000 over a pit, -3000 off the map.
    /// </summary>
    private int Ground(int x, int z)
    {
        short sample = _view.Sample(x, z);
        if (sample == -32768) return -3000;
        int h = (sample & 0x7FFF) << 17 >> 17;
        foreach (var (_, _, tx, tz, bottom, resting) in _view.CrateObjects)
            if (resting && tx == x >> 7 && tz == z >> 7 && (int)bottom - 128 < h) h = (int)bottom - 128;
        if ((_view.TileAttributes(x >> 7, z >> 7) >> 17 & 1) != 0 && h >= 0) h = 3000;
        return h;
    }

    /// <summary>A landing's end (state 3's finish): grounded, its ground bookkeeping, the tile it stands on freed, idle (blend 8).</summary>
    private void Landed(Combatant enemy)
    {
        var m = enemy.Mind!;
        m.Airborne = false;
        Reserve(TileOf(enemy), false);
        Pose(enemy, 1, 8);
        m.Settling = 0;
    }

    /// <summary>
    /// A landing or rising under way (BATTLE.PRG 0x800a2cd4 states 2, 3 and 5): each frame it moves by its way left over
    /// its frames left, truncated (0x800a2fbc), so the last frame takes the rest. Descending, a platform's top just
    /// under it (within 128, and 64 across) stops the landing where it is, aloft, playing its airborne idle, its tile
    /// left taken (0x800a9378); at the end it plays 10 (blend 8), is grounded, and waits out that animation, then lands
    /// (<see cref="Landed"/>). Rising, at the end it plays its airborne idle (blend 8), which drops the queued 37.
    /// Returns whether it was under way this frame.
    /// </summary>
    private bool Settle(Combatant enemy)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        switch (m.Settling)
        {
            case 2 or 5:
            {
                int f = m.SettleFrames;
                var (lx, ly, lz) = m.SettleLeft;
                int sx = (short)lx / f, sy = (short)ly / f, sz = (short)lz / f;
                m.SettleFrames = f - 1;
                m.SettleLeft = ((short)(lx - sx), (short)(ly - sy), (short)(lz - sz));
                actor.Position = new Vector3((short)((int)MathF.Round(actor.Position.X) + sx), (short)((int)MathF.Round(actor.Position.Y) + sy), (short)((int)MathF.Round(actor.Position.Z) + sz));
                if (m.Settling == 2)
                {
                    int x = (int)MathF.Round(actor.Position.X), y = (int)MathF.Round(actor.Position.Y), z = (int)MathF.Round(actor.Position.Z);
                    for (int i = _view.PlatformCount - 1; i >= 0; i--)
                    {
                        if (_view.PlatformState(i) is not { Off: false } platform) continue;
                        int ex = (int)platform.Now.X, ez = (int)platform.Now.Z, top = (int)platform.Now.Y;
                        if (ex - 64 <= x && x < ex + 64 && ez - 64 <= z && z < ez + 64 && y - 128 <= top && top < y)
                        {
                            m.Airborne = true;
                            Pose(enemy, 8, 8);
                            m.Settling = 0;
                            m.Settled = true;
                            return true;
                        }
                    }
                    if (m.SettleFrames == 0) { Pose(enemy, 10, 8); m.Airborne = false; m.Settling = 3; }
                }
                else if (m.SettleFrames == 0)
                {
                    Pose(enemy, 1, 8);
                    m.Settling = 0;
                    m.Settled = true;
                }
                return true;
            }
            case 3:
                // The finish comes the frame after the animation pass that first found 10 at its end.
                if (!Ended(enemy, 10)) return true;
                Landed(enemy);
                m.Settled = true;
                return true;
        }
        return false;
    }

    /// <summary>Whether a body's animation has reached its last frame and holds there (body +0x119c bit 0x20000).</summary>
    private static bool Ended(Combatant enemy, int number)
    {
        if (Pose(enemy) != number || enemy.Actor.Player is not { } player) return Pose(enemy) != number;
        return player.CurrentAnimationPosition >= player.CurrentAnimationLength;
    }

    /// <summary>
    /// Sets an actor walking to a map point for an event (event opcodes 21 and 2a through 0x8007cff8 and
    /// BATTLE.PRG 0x800e527c): only an actor with an AI record walks so (Ashley, and those of side 4); any other does
    /// not move at all, and false is returned. It lets its token and icons go (0x800d82cc, 0x800d821c, 0x800dbf00) and
    /// its AI is held by a walk record of its own (<see cref="Mind.Walker"/>, <see cref="StepScripted"/>). A second
    /// walk while one runs does not turn it (retail's delay slot writes the new record's settings and point into the
    /// AI's own record instead).
    /// </summary>
    public bool ScriptedWalk(string id, int x, int z)
    {
        if (!Combatants.TryGetValue(id, out var c)) return false;
        // 0x800a0a1c(idx, 2): it passes through other bodies until a wait sees it on its walk's goal tile.
        c.PassingThrough = true;
        if (c.Mind is not { } m) return false;
        ReleaseToken(c);
        Show(c, 0);
        Show(c, 0x40);
        if (m.Script == null)
        {
            if (c.Mover == null) c.Mover = new FieldPlayer(c.Actor, _view.FloorAt, _view.Stairs) { Crowded = Crowded, Climbs = false, Animates = false, Turns = false };
            m.Script = m.Walker();
            m.Script.ScriptPoint = (x, z);
            m.Held = true;
        }
        else
        {
            (m.StepHeight, m.StepReach, m.Routes, m.Detour) = (320, 3, true, 0);
            m.ScriptPoint = (x, z);
        }
        return true;
    }

    /// <summary>
    /// Whether an event's wait on an actor's walk holds (BATTLE.PRG 0x8007d03c by 0x800e5154): while its walk record
    /// has it on another tile than the record's goal; with no walk record (or no AI record) it is done.
    /// </summary>
    public bool Scripted(string id)
    {
        if (!Combatants.TryGetValue(id, out var c) || c.Mind?.Script is not { } walk) return false;
        if (TileOf(c) != walk.Goal) return true;
        c.PassingThrough = false;
        return false;
    }

    /// <summary>
    /// The event's frame of the scripted walks (BATTLE.PRG battle mode 3: the actor update, then 0x800e5308): each
    /// actor's body first carries out what was posted (<see cref="Embody"/>); then each with a walk record and no
    /// command waiting or under way walks by it, from where its body stands, never running and facing no one: a walker
    /// within 32 of its point stands and its walk ends (0x800e5240), else heads for it by its waypoint; a flyer or
    /// hopper on the tile it made for last frame stands and its walk ends, else makes for the point's tile.
    /// </summary>
    public void StepScripted()
    {
        foreach (var c in Fighters(includeHidden: true))
        {
            if (c.Dead || c.Mind is not { } own) continue;
            if (own.Script is { } walk) c.Mind = walk;
            try { if (c.Mind.Posted || c.BodyBusy || own.Script != null) Embody(c); }
            finally { c.Mind = own; }
        }
        foreach (var c in Fighters(includeHidden: true))
        {
            if (c.Dead || c.Mind is not { Script: { } walk } own) continue;
            if (walk.Posted || c.BodyBusy) continue;
            c.Mind = walk;
            try
            {
                if (walk.LeapHold != 0) walk.LeapHold--;
                if ((walk.Detour >> 5 & 0x7F) != 0) walk.Detour -= 1u << 5;
                walk.Pause = 0;
                walk.Wades = true;
                walk.Run = walk.CrossesZones = walk.Wary = false;
                walk.FaceTarget = 16;
                walk.Tile = TileOf(c);
                walk.Zone = walk.Passage = Cell(walk.Tile) & 15;
                Afresh(walk);
                var (gx, gz) = walk.ScriptPoint;
                int x = (ushort)(int)c.Actor.Position.X, z = (ushort)(int)c.Actor.Position.Z;
                bool done;
                walk.Command = Body.Stand;
                walk.BodyTurn = false;
                if (c.Gait == Gait.Walk || c == Ashley)
                {
                    long dx = (short)(gx - x), dz = (short)(gz - z);
                    done = dx * dx + dz * dz < 1024;
                    if (!done && walk.Riding != 0) Ride(c);
                    else if (!done)
                    {
                        walk.Goal = (gx >> 7, gz >> 7);
                        Head(c, new Vector3(gx, c.Actor.Position.Y, gz));
                    }
                }
                else
                {
                    done = walk.Goal == walk.Tile;
                    if (!done)
                    {
                        walk.Goal = (gx >> 7, gz >> 7);
                        Move(c, 0, 0);
                    }
                }
                Post(c);
                if (done)
                {
                    own.Script = null;
                    own.Held = false;
                    (own.Airborne, own.Aground, own.Settling, own.SettleFrames, own.SettleLeft, own.SettleFacing, own.SettleTo, own.Settled) = (walk.Airborne, walk.Aground, walk.Settling, walk.SettleFrames, walk.SettleLeft, walk.SettleFacing, walk.SettleTo, walk.Settled);
                    (own.Twist, own.Pitch, own.TwistMode, own.PitchTarget, own.TwistTarget, own.TwistRate) = (walk.Twist, walk.Pitch, walk.TwistMode, walk.PitchTarget, walk.TwistTarget, walk.TwistRate);
                    (own.TurnFrames, own.TurnLeft, own.Code, own.CodeValue, own.Posted, own.Command, own.Direction) = (walk.TurnFrames, walk.TurnLeft, walk.Code, walk.CodeValue, walk.Posted, walk.Command, walk.Direction);
                    (own.LeapTile, own.Bound, own.Course) = (walk.LeapTile, walk.Bound, walk.Course);
                }
            }
            finally { c.Mind = own; }
        }
    }


}
