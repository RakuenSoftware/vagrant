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
    /// <summary>
    /// What an enemy's body does this frame (the actor code a command posts, 0x80075554): stand (2), turn (3, 4, 7),
    /// walk or run (9, 10, after the probe that may nudge it, 13), leap to a tile (11, 14, 18), warp (23), fly (16,
    /// 17) or glide to a point (19). A flyer's landing and rising (21, 22) run their course apart.
    /// </summary>
    public enum Body { Stand, Turn, Walk, Leap, Warp, Fly, Glide }

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
        public int Aground, Settling, Pose = -1;
        public Vector3 SettleTo;
        /// <summary>The upper-body twist (body +0x14) and the look a glance turns its head to (command 16).</summary>
        public float Twist;
        public float? Glance;
        /// <summary>The scripted walk that holds its AI (+0x18c): the walk's own record, and the point it walks to (+0x160).</summary>
        public Mind? Script;
        public (int X, int Z) ScriptPoint;

        /// <summary>A record for a scripted walk (0x800e51c8): its movement taken from this one.</summary>
        public Mind Walker() => new()
        {
            Height = Height, Head = Head, Radius = Radius, Girth = Girth, Moves = Moves, ReachBonus = ReachBonus, DefaultTurn = DefaultTurn, TurnSpeed = TurnSpeed,
            FlagsA = FlagsA, FlagsB = FlagsB, SpeedA = SpeedA, SpeedB = SpeedB, HopReach = HopReach, StepReach = StepReach, StepHeight = StepHeight,
            Shuns = Shuns, Airborne = Airborne, Tile = Tile, Goal = Tile,
        };
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
            if ((uint)nx < 32 && (uint)nz < 32 && (_cells[nz * 32 + nx] & 0x20) != 0 && (_floors[nz * 32 + nx] & 0x2000) == 0) Fill(nx, nz, level);
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
    private int FloorOf((int X, int Z) t) => RawFloor(t) << 17 >> 17;

    /// <summary>The steps walled out of a tile (the room's bitmap), bit d the step in direction d.</summary>
    private int WallsOf((int X, int Z) t) => _walls == null || t.Z * _width + t.X is var i && (uint)i >= (uint)_walls.Length ? 0xFF : _walls[i];

    /// <summary>
    /// Whether a step out of a tile is walled (the bitmap; or, in a room without, 0x800e4690: where the next tile has
    /// no floor, or a floor 64 or more above or below).
    /// </summary>
    private bool Walled((int X, int Z) t, int d)
    {
        if (_walls != null) return (WallsOf(t) >> d & 1) != 0;
        var n = Step(t, d);
        return RawFloor(n) < 0 || Math.Abs(FloorOf(t) - FloorOf(n)) >= 64;
    }

    /// <summary>The direction of a step of a tile (0x1f8003e0), -1 for none, -1 for one past a neighbour too.</summary>
    private static int Toward(int dx, int dz) => dx is < -1 or > 1 || dz is < -1 or > 1 ? -1 : new[] { 1, 0, 7, 2, -1, 6, 3, 4, 5 }[(dz + 1) * 3 + dx + 1];

    /// <summary>Where another stands for the tile tests (BATTLE.PRG 0x800d954c, 0x800d98e8): bound for a tile mid-leap or warp, else where it is.</summary>
    private static Vector3 Placed(Combatant k) => k.Index != 0 && (k.Mover is { Busy: true } || k.HopState != 0) && k.Mind != null ? k.Mind.Bound : k.Actor.Position;

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
                    if (!blocked && c.Routes && !m.CrossesZones && (Cell(m.Tile) & 15) != (a & 15)) blocked = (m.Passage & 15) != (a & 15);
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
        int a = At(m.FaceTarget) is { } face ? Angle(p.X - face.Actor.Position.X, p.Z - face.Actor.Position.Z) : (int)c.Actor.Facing;
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
    private void Issue(Combatant c, int code)
    {
        var m = c.Mind!;
        m.Command = code switch
        {
            5 or 7 => Body.Walk, 8 => Body.Glide, 9 or 10 => Body.Fly, 13 => Body.Warp, 14 => Body.Leap, _ => Body.Stand,
        };
        _issued = code;
        if (code == 5) m.Direction = (short)(m.Direction << 9);
        if (code is 11 or 12)
        {
            m.Aground = 0;
            m.LeapTile = m.Tile;
            StartSettling(c, code == 11);
            m.Command = Body.Stand;
        }
        // 0x800df5f0: a leap within the height it may step is a step (11); beyond it a hopper warps.
        if (code == 14 && Math.Abs(FloorOf(m.LeapTile) - FloorOf(m.Tile)) >= m.StepHeight && (m.Moves & 8) != 0) m.Command = Body.Warp;
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
                if (mode == 0) Hop(c, c.Mind!.Goal);
                else HopAlong(c, h);
                break;
            default:
                if (mode == 0) WalkTo(c);
                else WalkAlong(c, h);
                break;
        }
    }

    /// <summary>
    /// A walker for its goal tile (BATTLE.PRG 0x800e4b2c): riding a platform, the ride; on its goal it faces; else by
    /// its waypoint (<see cref="Waypoint"/>, 0x800e19fc) for the goal's centre.
    /// </summary>
    private void WalkTo(Combatant c)
    {
        var m = c.Mind!;
        if (m.Riding != 0) { Ride(c); return; }
        if (m.Goal == m.Tile) { Face(c); return; }
        Head(c, Centre(m.Goal, c.Actor.Position.Y));
    }

    /// <summary>Walks for a point by the waypoint (0x800e19fc: not away, keeping to steps that near the goal), straight along the angle to it.</summary>
    private void Head(Combatant c, Vector3 point)
    {
        var m = c.Mind!;
        m.Away = false;
        m.Nearer = true;
        var way = Waypoint(c, point);
        if (m.Riding != 0) return;
        m.Direction = (int)c.Actor.FacingToward(way);
        Issue(c, 7);
    }

    /// <summary>A walker along a heading (BATTLE.PRG 0x800e4b70): riding a platform, the ride; else it crosses zones and steers (<see cref="Steer"/>, mode 1).</summary>
    private void WalkAlong(Combatant c, int h)
    {
        var m = c.Mind!;
        if (m.Riding != 0) { Ride(c); return; }
        m.CrossesZones = true;
        Afresh(m);
        Steer(c, h & 0xFFF, 1);
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
                m.Course = new Vector3(Math.Abs(x - ox) < spd ? ox : x + spd * Cos(b) / 4096f, t, Math.Abs(z - oz) < spd ? oz : z + spd * Sin(b) / 4096f);
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
        float ax = x + spd * Cos(bearing) / 4096f, az = z + spd * Sin(bearing) / 4096f;
        if (arc < top) arc = top;
        else if (step < arc)
        {
            if (top < y) { m.Course = new Vector3(0, -4096, 0); Issue(c, pace.Code); return; }
            arc = step;
        }
        m.Aground = 0;
        int dy = arc - y;
        if (spd < Math.Abs(dy)) { m.Course = new Vector3(ax - x, dy, az - z); Issue(c, pace.Code); }
        else { m.Course = new Vector3(ax, arc, az); Issue(c, 8); }
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
    /// the one farther from what it moves by.
    /// </summary>
    private void HopAlong(Combatant c, int h)
    {
        var m = c.Mind!;
        var ahead = PointAt(c, h, m.HopReach);
        var behind = PointAt(c, h + 2048, m.HopReach);
        var origin = m.FacePoint ?? (At(m.FaceTarget) is { } t ? t.Actor.Position : c.Actor.Position);
        m.Goal = ((Centre(behind, 0) - origin) with { Y = 0 }).LengthSquared() < ((Centre(ahead, 0) - origin) with { Y = 0 }).LengthSquared() ? ahead : behind;
        Hop(c, m.Goal);
    }

    /// <summary>
    /// A point its reach along a heading from its tile (BATTLE.PRG 0x800e1388): the first standing tile of eight tries,
    /// an eighth of the reach nearer each, else the last; kept within the bounds.
    /// </summary>
    private (int X, int Z) PointAt(Combatant c, int heading, int reach)
    {
        var m = c.Mind!;
        int a = (3072 - heading) & 0xFFF, step = reach >> 3;
        (int X, int Z) tile = m.Tile;
        for (int i = 0; i < 8; i++, reach -= step)
        {
            tile = ((m.Tile.X * 128 + (reach * Cos(a) >> 12) + 64) >> 7, (m.Tile.Z * 128 + (reach * Sin(a) >> 12) + 64) >> 7);
            if (Walkable(c, tile)) break;
        }
        return (Math.Clamp(tile.X, _bounds.MinX, Math.Max(_bounds.MinX, _bounds.MaxX)), Math.Clamp(tile.Z, _bounds.MinZ, Math.Max(_bounds.MinZ, _bounds.MaxZ)));
    }

    /// <summary>
    /// A hopper for its goal tile (BATTLE.PRG 0x800e153c): on it, it faces; beyond its reach the goal comes in to its
    /// reach (<see cref="PointAt"/>); one that also walks walks once within four tiles; else at most every 6 calls it
    /// warps (command 13) to the goal or the first free cardinal tile beside it.
    /// </summary>
    private void Hop(Combatant c, (int X, int Z) goal)
    {
        var m = c.Mind!;
        var p = c.Actor.Position;
        if (goal == m.Tile) { Face(c); return; }
        var centre = Centre(goal, p.Y);
        if ((long)m.HopReach * m.HopReach < (long)((centre - p) with { Y = 0 }).LengthSquared())
            goal = PointAt(c, Angle(p.X - centre.X, p.Z - centre.Z), m.HopReach);
        m.Goal = goal;
        if ((m.Moves & 6) != 0 && Apart(goal, m.Tile) < 16) { WalkTo(c); return; }
        if (++c.HopCount < 6) { Face(c); return; }
        if (!Walkable(c, goal))
        {
            (int X, int Z)? free = null;
            for (int d = 0; d < 8 && free == null; d += 2) if (Walkable(c, Step(goal, d))) free = Step(goal, d);
            if (free is not { } beside) { Face(c); return; }
            m.Goal = goal = beside;
        }
        c.HopCount = 0;
        m.LeapTile = goal;
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
        if (p < 0 || !(c.Routes || (m.Moves & 0x38) != 0) || _view.PlatformState(p) is not { } platform) return TileOf(t);
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
        if (!enemy.Routes || from == to) return point;
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
    /// Carries out an enemy's order with its body: one falling or leaping carries on, a hop runs its course (vanished,
    /// it stands on its tile facing its target and reappears, 21 then 22, then stands), a flyer's landing or rising
    /// runs its course; then the AI's order becomes a command: stand, turn, go for its goal (mode 0) or along a heading
    /// (mode 1) by its kind (<see cref="Move"/>), and its body carries it out (<see cref="Perform"/>).
    /// </summary>
    private void Drive(Combatant enemy)
    {
        var actor = enemy.Actor;
        var m = enemy.Mind!;
        if (enemy.Mover is { Busy: true } falling)
        {
            falling.Step(null, walk: true);
            if (!falling.Busy) { Reserve(m.LeapTile, false); Play(enemy, 1, 6); }
            return;
        }
        if (enemy.HopState == 1 && !_view.Materialising(actor))
        {
            actor.Position = enemy.HopTo;
            if (At(m.FaceTarget) is { } faced) actor.Facing = actor.FacingToward(faced.Actor.Position);
            _view.Materialise(actor, appear: true);
            if (Play(enemy, 21, 8, once: true) is { } set) actor.Queue($"{set}_22", 8);
            enemy.HopState = 2;
        }
        if (enemy.HopState == 2 && !_view.Materialising(actor)) { Play(enemy, 1, 6); enemy.HopState = 0; Reserve(TileOf(enemy), false); }
        if (Settle(enemy)) return;
        if (enemy.HopState != 0 || enemy.Suspended || m.Held || enemy.Has(Paralysis) || _phase != Phase.Free && _phase != Phase.Drawing && _phase != Phase.Sheathing) return;
        Afresh(m);
        m.Zone = m.Passage = Cell(m.Tile) & 15;
        _issued = -1;
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
        Perform(enemy);
    }

    /// <summary>
    /// Carries out a command for the frame (the actor side, 0x80075554): stands (idle), turns (<see cref="Turn(Combatant)"/>),
    /// walks or runs a frame (<see cref="Stride"/>), leaps to a tile, warps, or flies.
    /// </summary>
    private void Perform(Combatant enemy)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        if (m.Command != Body.Turn && m.Glance is not { } && m.Twist != 0) m.Twist = Mathf.MoveToward(m.Twist, 0, 30);
        switch (m.Command)
        {
            case Body.Stand:
                if (_walking.Remove(enemy)) { enemy.Mover?.Halt(); if (!m.Airborne) Play(enemy, 1, 6); }
                if (m.Glance is { } glance && actor.Head != null) m.Twist = Mathf.MoveToward(m.Twist, glance, 64);
                break;
            case Body.Turn:
                if (_walking.Remove(enemy)) { enemy.Mover?.Halt(); if (!m.Airborne) Play(enemy, 1, 6); }
                Turn(enemy);
                break;
            case Body.Walk:
                Stride(enemy, m.Direction);
                break;
            case Body.Leap:
                Leap(enemy);
                break;
            case Body.Warp:
                Warp(enemy);
                break;
            case Body.Fly:
                Fly(enemy);
                break;
            case Body.Glide:
            {
                // Actor code 19 (0x800b6530 with 0x800a291c): there in the frame, with no change of animation, turning a
                // twelfth of the way a frame toward whom it faces, else along its flight.
                float? want = At(m.FaceTarget) is { } faced ? actor.FacingToward(faced.Actor.Position)
                    : ((m.Course - actor.Position) with { Y = 0 }).LengthSquared() > 0 ? actor.FacingToward(m.Course) : null;
                actor.Position = m.Course;
                if (want is { } turn) actor.Facing = Mathf.PosMod(actor.Facing + Mathf.Wrap(turn - actor.Facing, -2048, 2048) / 12, 4096);
                break;
            }
        }
        m.Glance = null;
        if (actor.Head != null) actor.HeldYaw = m.Twist == 0 ? null : m.Twist;
    }

    /// <summary>
    /// Turns (BATTLE.PRG's command 4, 0x800df720), its view the body's facing and its upper body's twist: within a turn
    /// step of its facing, the whole body to it (the twist easing back); else one with an upper body twists it a step,
    /// to 800 either way, then turns the body a step.
    /// </summary>
    private void Turn(Combatant enemy)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        float view = actor.Facing + m.Twist;
        int diff = (int)Mathf.PosMod(m.TurnTo - view, 4096), dir = diff < 2048 ? 1 : -1;
        float rate = m.TurnRate;
        if (diff < 1 || diff < rate || 4096 - diff < rate)
        {
            float turn = Mathf.Wrap(m.TurnTo - actor.Facing, -2048, 2048);
            actor.Facing = Mathf.PosMod(Math.Abs(turn) <= rate ? m.TurnTo : actor.Facing + Math.Sign(turn) * rate, 4096);
            m.Twist = Mathf.MoveToward(m.Twist, 0, rate);
            return;
        }
        if (actor.Head != null && (dir > 0 ? m.Twist < 800 : m.Twist > -800)) m.Twist = Math.Clamp(m.Twist + dir * rate, -800, 800);
        else actor.Facing = Mathf.PosMod(actor.Facing + dir * rate, 4096);
    }

    /// <summary>
    /// Walks or runs a frame along a heading (BATTLE.PRG 0x800def8c, carried out by the movement Ashley's is, 0x800a5280):
    /// asked to run, the first profile that runs (flags 0x84), else the first that walks (0x42; retail tests the first
    /// profile's speed byte for the second, kept); with neither it stands. Its corners a probe's step ahead (at that
    /// profile's speed) are tested for walls and rises; one walled nudges it off the wall once a tile, to its girth
    /// within the tile, at most 8 a frame. It moves at its record's speed, the second's where that profile's flags are
    /// exactly walking (2, 64) or running (4, 128), facing its target as it goes, else its heading.
    /// </summary>
    private void Stride(Combatant enemy, int heading)
    {
        var actor = enemy.Actor;
        var m = enemy.Mind!;
        if (enemy.Mover is not { } mover) return;
        bool run;
        int probe;
        if (m.Run && (m.FlagsA & 0x84) != 0) (run, probe) = (true, m.SpeedA);
        else if (m.Run && (m.FlagsB & 0x84) != 0) (run, probe) = (true, m.SpeedB);
        else if ((m.FlagsA & 0x42) != 0) (run, probe) = (false, m.SpeedA);
        else if ((m.SpeedA & 0x42) != 0) (run, probe) = (false, m.SpeedB);
        else
        {
            if (_walking.Remove(enemy)) { mover.Halt(); Play(enemy, 1, 6); }
            return;
        }
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
        if (walled)
        {
            int cx = tx * 128 + 64, cz = tz * 128 + 64;
            int nx = (px < cx ? cx - 64 + r : cx + 63 - r) - x, nz = (pz < cz ? cz - 64 + r : cz + 63 - r) - z;
            if ((nx | nz) != 0 && m.Nudged != m.Tile)
            {
                m.Nudged = m.Tile;
                Nudge(enemy, Math.Clamp(nx, -8, 8), Math.Clamp(nz, -8, 8));
                return;
            }
        }
        int speed = run ? (m.FlagsB is 4 or 128 ? m.SpeedB : m.SpeedA) : (m.FlagsB is 2 or 64 ? m.SpeedB : m.SpeedA);
        if (speed <= 0)
        {
            if (_walking.Remove(enemy)) { mover.Halt(); Play(enemy, 1, 6); }
            return;
        }
        mover.WalkSpeed = speed;
        float facing = At(m.FaceTarget) is { } t ? actor.FacingToward(t.Actor.Position) : heading & 0xFFF;
        mover.Step(heading & 0xFFF, walk: true, facing: facing);
        if (!_walking.Contains(enemy) || enemy.Runs != run)
        {
            if (!run || Play(enemy, FieldPlayer.Run, 4) == null) Play(enemy, FieldPlayer.Walk, 4);
            enemy.Runs = run;
            _walking.Add(enemy);
        }
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
    /// Leaps to a tile (BATTLE.PRG's command 14, 0x800df5f0, by the jump 0x800a208c): within the height it may step it
    /// steps (11), else a hopper warps (23), a flyer flies (18), else it leaps (14); its goal's centre on the floor is
    /// where it is bound (+0x174), that tile taken until it lands. The leap's speed is its profile's (run, then walk).
    /// </summary>
    private void Leap(Combatant enemy)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        if (enemy.Mover is not { } mover) return;
        int speed = m.Run && (m.FlagsA & 0x84) != 0 ? m.SpeedA : m.Run && (m.FlagsB & 0x84) != 0 ? m.SpeedB
            : (m.FlagsA & 0x42) != 0 ? m.SpeedA : (m.FlagsB & 0x42) != 0 ? m.SpeedB : 0;
        if (speed <= 0)
        {
            if (_walking.Remove(enemy)) { mover.Halt(); Play(enemy, 1, 6); }
            return;
        }
        var centre = Centre(m.LeapTile, 0);
        float floor = _view.FloorAt(centre) ?? FloorOf(m.LeapTile);
        m.Bound = centre with { Y = floor };
        Reserve(m.LeapTile, true);
        _walking.Remove(enemy);
        float facing = At(m.FaceTarget) is { } t ? actor.FacingToward(t.Actor.Position) : actor.FacingToward(centre);
        mover.Leap(m.Bound, speed, facing);
        Play(enemy, 47, 4, once: true);
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
    /// Flies a frame (actor codes 16 and 17, motion kind 3, 0x800a2790): along the flight vector at its record's speed
    /// (the second's where that profile's flags are exactly 16 walking or 32 running), through anything; it faces its
    /// target, else its way; rising (38), level (37) or sinking (40).
    /// </summary>
    private void Fly(Combatant enemy)
    {
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        bool run = _issued == 10;
        int speed = run ? (m.FlagsB == 32 ? m.SpeedB : m.SpeedA) : (m.FlagsB == 16 ? m.SpeedB : m.SpeedA);
        var v = m.Course.LengthSquared() > 0 ? m.Course.Normalized() * speed : Vector3.Zero;
        actor.Position += v;
        m.Airborne = true;
        if (At(m.FaceTarget) is { } t) TurnToward(actor, actor.FacingToward(t.Actor.Position));
        else if (v.X != 0 || v.Z != 0) TurnToward(actor, actor.FacingToward(actor.Position + v));
        int pose = speed <= 0 ? 1 : v.Y < -2 ? 38 : v.Y < 3 ? 37 : 40;
        if (pose != m.Pose && Play(enemy, pose, 4) != null) m.Pose = pose;
        _walking.Add(enemy);
    }

    /// <summary>A body's turn while it flies: the shortest way, a twelfth of what is left a frame.</summary>
    private static void TurnToward(EventActor actor, float facing) =>
        actor.Facing = Mathf.PosMod(actor.Facing + Mathf.Wrap(facing - actor.Facing, -2048, 2048) / 12, 4096);

    /// <summary>
    /// Starts a flyer's landing (actor code 21: 9 to the floor at its tile's centre, then 10) or rising (22: 7, then 37,
    /// to its cruising height there), at its flying speed.
    /// </summary>
    private void StartSettling(Combatant enemy, bool landing)
    {
        var m = enemy.Mind!;
        if (m.Settling != 0) return;
        var centre = Centre(m.LeapTile, 0);
        float ground = _view.FloorAt(centre) ?? FloorOf(m.LeapTile);
        if (landing) Reserve(m.LeapTile, true);
        m.SettleTo = centre with { Y = landing ? ground : FloorOf(m.LeapTile) + Cruise };
        int speed = Math.Max(1, Pace(m)?.Speed ?? 8);
        m.Settling = Math.Max(1, (int)Math.Ceiling((m.SettleTo - enemy.Actor.Position).Length() / speed)) * (landing ? 1 : -1);
        if (landing) Play(enemy, 9, 8, once: true);
        else { m.Airborne = true; if (Play(enemy, 7, 8, once: true) is { } set) enemy.Actor.Queue($"{set}_37", 8); }
    }

    /// <summary>A landing or rising under way: the body moves evenly to where it settles; a landing ends grounded (10, then idle) with its tile freed.</summary>
    private bool Settle(Combatant enemy)
    {
        var m = enemy.Mind!;
        if (m.Settling == 0) return false;
        int left = Math.Abs(m.Settling);
        var actor = enemy.Actor;
        actor.Position += (m.SettleTo - actor.Position) / left;
        m.Settling -= Math.Sign(m.Settling);
        if (m.Settling == 0 && m.SettleTo.Y >= (_view.FloorAt(m.SettleTo) ?? m.SettleTo.Y))
        {
            m.Airborne = false;
            Reserve(TileOf(enemy), false);
            if (Play(enemy, 10, 8, once: true) is { } set) actor.Queue($"{set}_1", 8);
            m.Pose = -1;
        }
        return m.Settling != 0;
    }

    /// <summary>
    /// Sets an enemy walking to a map point for an event (BATTLE.PRG 0x800e527c, by event opcodes 21 and 2a through
    /// 0x8007cff8): it lets its token and icons go, and its AI is held by a walk of its own (<see cref="StepScripted"/>);
    /// false for one without an AI (it walks as the event's own route).
    /// </summary>
    public bool ScriptedWalk(string id, int x, int z)
    {
        if (!Combatants.TryGetValue(id, out var c) || c == Ashley || c.Mind is not { } m || c.Mover == null || c.Dead) return false;
        ReleaseToken(c);
        Show(c, 0);
        m.Script ??= m.Walker();
        m.Script.ScriptPoint = (x, z);
        m.Held = true;
        return true;
    }

    /// <summary>Whether an actor still walks for an event (its record at +0x18c).</summary>
    public bool Scripted(string id) => Combatants.TryGetValue(id, out var c) && c.Mind?.Script != null;

    /// <summary>
    /// The event's frame of the scripted walks (BATTLE.PRG 0x800e5308, battle mode 3): each held enemy walks (never
    /// runs, facing no one, its marks waded) by its own record from where it stands: a walker by its waypoint for the
    /// point until within 32 of it, a flyer or hopper for the point's tile until on it; then it stands and its walk
    /// ends (0x800e5240).
    /// </summary>
    public void StepScripted()
    {
        foreach (var c in Combatants.Values)
        {
            if (c.Dead || c.Mind is not { Script: { } walk } own) continue;
            c.Mind = walk;
            try
            {
                walk.Pause = 0;
                if (walk.LeapHold != 0) walk.LeapHold--;
                if ((walk.Detour >> 5 & 0x7F) != 0) walk.Detour -= 1u << 5;
                walk.Wades = true;
                walk.Run = walk.CrossesZones = walk.Wary = false;
                walk.FaceTarget = 16;
                walk.Tile = TileOf(c);
                walk.Zone = walk.Passage = Cell(walk.Tile) & 15;
                Afresh(walk);
                _issued = -1;
                if (Settle(c) || c.Mover is { Busy: true } || c.HopState != 0)
                {
                    if (c.Mover is { Busy: true } leaping) leaping.Step(null, walk: true);
                    continue;
                }
                var (gx, gz) = walk.ScriptPoint;
                var p = c.Actor.Position;
                bool done;
                walk.Command = Body.Stand;
                if (c.Gait == Gait.Walk)
                {
                    done = (gx - p.X) * (gx - p.X) + (gz - p.Z) * (gz - p.Z) < 1024;
                    if (!done && walk.Riding != 0) Ride(c);
                    else if (!done)
                    {
                        walk.Goal = (gx >> 7, gz >> 7);
                        Head(c, new Vector3(gx, p.Y, gz));
                    }
                }
                else
                {
                    done = walk.Goal == walk.Tile && walk.Goal == (gx >> 7, gz >> 7);
                    if (!done)
                    {
                        walk.Goal = (gx >> 7, gz >> 7);
                        Move(c, 0, 0);
                    }
                }
                Perform(c);
                if (done)
                {
                    if (_walking.Remove(c)) { c.Mover?.Halt(); Play(c, 1, 6); }
                    own.Script = null;
                    own.Held = false;
                }
            }
            finally { c.Mind = own; }
        }
    }
}
