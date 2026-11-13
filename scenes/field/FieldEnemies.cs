// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The enemies' AI, as BATTLE.PRG runs it each battle frame: the tick (0x800d8ef4: timers, perception, plan upkeep),
/// then the decision pass (0x800daed0: launch, unstick, behaviour routine, wander). Moves come from the planner
/// (<see cref="PlanMoves"/>); the body carries out the orders the AI gives it (<see cref="Drive"/>).
/// </summary>
public sealed partial class FieldBattle
{
    public sealed partial class Combatant
    {
        /// <summary>Its AI record (BATTLE.PRG's 0x484 bytes, 0x800f5878 by actor index); null for a crate.</summary>
        public Mind? Mind;
        /// <summary>Its actor index (the node's +4): 0 for Ashley, 16 for none.</summary>
        public int Index = 16;
        /// <summary>How long it has held a token (its AI's +0x8e), 0 with none: the frames of its telegraph.</summary>
        public int Telegraph => Mind?.Token ?? 0;
        /// <summary>The frames left after it let a token go (its AI's +0x9a), while its jagged balloon stands.</summary>
        public int Alert => Mind?.AfterRelease ?? 0;
        /// <summary>
        /// Where its body stands as the AI measures it (its object's +28 to +32): a crate's base, 64 under its middle
        /// (the crate's stand-in, which Ashley aims at, stands 150 under it).
        /// </summary>
        public Vector3 Origin => Cube ? Actor.Position + new Vector3(0, 64 - 150, 0) : Actor.Position;
    }

    /// <summary>
    /// The behaviour routines of BATTLE.PRG's table at 0x800F1790, by a room actor record's bits 4 to 8; and
    /// Guildenstern's final form's own (0x800e7f8c), which the setup gives characters 172 and 173 in place of the
    /// record's (0x800e5e3c).
    /// </summary>
    public enum Behaviour { Fighter, KeepsDistance, FollowsAlly, Escort, PackHunter, Bodyguard, Patroller, ScaredEscort, Berserk, None, FinalForm }

    /// <summary>
    /// What the AI has an enemy's body do this frame (the actor command it writes, 0x800deefc): stand (0), turn to a
    /// facing (4), walk for its goal (6, by way of 0x800e50a0), step along a heading while facing elsewhere (5/7), or
    /// fly to a point (8, actor code 19).
    /// </summary>
    public enum Order { Stand, Turn, Path, Away, Fly }

    /// <summary>An action made: who made it, which, and on whom, with the target's hit points as it began.</summary>
    /// <remarks>The action block BATTLE.PRG hands its replan (0x800dec88); its results' amounts (+4) are told here by the
    /// hit points the target has lost since.</remarks>
    public sealed record Acted(Combatant Caster, int Action, Combatant Target)
    {
        public int TargetHp { get; } = Target.Hp;
    }

    /// <summary>
    /// An actor's AI record (BATTLE.PRG, 0x484 bytes): what it perceives of every actor, how it stands to them, its
    /// token, state, plan phase, chase, memory and the order it gives its body. Offsets are the record's.
    /// </summary>
    public sealed partial class Mind
    {
        /// <summary>Its word for each actor index (+0x1a4): bit 0 sees it, 1 just lost it, 2 low line of sight, 3 in its view cone, 5 aware of it, 6-7 the relation (3 foe, 2 ally, 1 neither), 8-14 frames a foe went unseen, 15-19 frames it is seen regardless, 20-26 frames a relation is overridden, 27 another stands in the line of fire, 28-29 the relation restored, 30 high line of sight, 31 perceived for targeting.</summary>
        public uint[] Relations = new uint[16];
        /// <summary>The squared distance to each actor index (+0x1e4).</summary>
        public uint[] Distances = new uint[16];
        /// <summary>Its reference height (+0x82, the centre, negative above the feet), eye height (+0xb8) and girth (+0x92).</summary>
        public int Height, Head, Radius = 63;
        /// <summary>How far it sees and hears, squared (+0xac, +0xa4), and the cosine (4096 a whole) its view cone opens to (+0xa8).</summary>
        public uint SightSquared, HearingSquared;
        public int ConeCosine = -4096;
        /// <summary>Calls of the line-of-sight test (+0x12b): odd ones test at eye height, even ones at the centre.</summary>
        public int Sightings;
        /// <summary>AI word bits: sees without a line of sight (15, +0x25), searches where it lost a foe (13, +0x24), keeps facing the point it backs off from (7, +0x33), forgets a foe unseen 120 frames (23, +0x22), defends allies (24, +0x21), turns on whoever harms it (25, +0x30) and how (26, +0x20), and may be won over by Ashley's help (27, +0x27).</summary>
        public bool BlindSight, SearchesLost, KeepsFacing, Forgets, Defends, Retaliates, Avenges, Convertible;
        /// <summary>When it retreats (AI word bits 28 and 29, +0x2f), and whether it does this frame (+0x0e).</summary>
        public int FleeRule;
        public bool Retreating;
        /// <summary>Won over to Ashley's side (+0x13), the help that counted toward it (+0x126), and the routine it had before (+0x6c).</summary>
        public bool Converted;
        public int Conversions;
        public Behaviour Behaviour = Behaviour.Fighter;
        public Behaviour? Calmed;
        /// <summary>Its group and the group of the leader it keeps by (the record's +0x30 bits 0-3 and 9-13), and that leader's index (+0x16, 16 none).</summary>
        public int Group, LeaderGroup = -1, Leader = 16;
        /// <summary>Its state for its first planned move (+0x12e: 1 approach, 2 special spot, 7 nothing planned) and its plan phase (+0x131).</summary>
        public int State = 7, Phase = 3;
        /// <summary>Its token's age (+0x8e), the refractory frames before it may take one again (+0x98), the frames after a release (+0x9a).</summary>
        public int Token, Refractory, AfterRelease;
        /// <summary>The icon shown over it (the byte at 0x800f2260), and an icon held for a time (+0x138, +0x12a).</summary>
        public int Shown, Icon, IconTimer;
        /// <summary>Frames before it may voice another reaction (+0x155), and the reaction its body is to cry out (its node's byte +0x0b: 1 alert, 2 calm, 0 none).</summary>
        public int VoiceLockout, Cue;
        /// <summary>The command its body had at its last tick (the node's +9 kept at +0x124; a change voices the alert cry).</summary>
        public Body LastCommand;
        /// <summary>Its movement profiles' flags (+0x15e), its reach extension in 32-unit steps (+0xb3), its turn speed in range (+0xba) and its usual turn speed (+0x13c).</summary>
        public int Moves, ReachBonus, TurnSpeed, DefaultTurn = 128;
        /// <summary>Its two movement profiles, flags and speed in units a frame (+0x224 to +0x227: data +0x30, +0x32, +0x31, +0x33), and its hop's reach (+0xb4).</summary>
        public int FlagsA, FlagsB, SpeedA, SpeedB, HopReach;
        /// <summary>The chase (0x800e24ec): its target (+0xe5), stop distance (+0xdc), range band (+0xe9: 0 far, 1 in range, 2 too close), the last stop, target and flag (+0xe0, +0xeb, +0xea), the frames all stayed (+0xf8), the face latch (+0xe8), the search (+0xe4, +0xe6) for the last known tile (+0xec), and the target's half-tile (+0xf4).</summary>
        public int ChaseTarget = 16, Stop, Band, LastStop, LastChaseTarget = 16, Unchanged, SearchTimer;
        public bool LastFlag, FaceLatch, Searching;
        public (int X, int Z) LastKnown, TargetHalf;
        /// <summary>Its tile last frame (+0x34), frames it stood on it (+0x46, from 2700), and whether it is stuck (+0x1c) with the 40-frame cycle (+0x45).</summary>
        public (int X, int Z) Tile = (-1, -1);
        public int SameTile = 2700, Cycle;
        public bool Stuck, Still;
        /// <summary>The tile it remembers failing on (+0x38, null none), whom it failed on and where they stood (+0x3b, +0x3c), and for how long (+0x40).</summary>
        public (int X, int Z)? Memory;
        public int MemoryTarget = 16, MemoryTimer;
        public (int X, int Z) MemoryTargetTile;
        /// <summary>The unstick step (+0xfa), its four directions (+0x118) and whom it faces as it steps (+0xfb).</summary>
        public int Unstick, UnstickFace = 16;
        public int[] UnstickDirections = new int[4];
        /// <summary>The flee memory (+0x114) and the threats' tile (+0x110).</summary>
        public int FleeTimer;
        public (int X, int Z) Threat;
        /// <summary>The ring of its last eight positions' low bytes (+0x70, +0x78), its index and fill (+0x80, +0x81), and the pause an oscillation earns (+0x19a).</summary>
        public byte[] RingX = new byte[8], RingZ = new byte[8];
        public int RingIndex, RingFill, Pause;
        /// <summary>Its movement mode (+0x8a: 1 chase, 2 go to, 3 wander, 4 flee, 5 patrol, 6 hold in range).</summary>
        public int Mode;
        /// <summary>The mode-6 turn: the tile it turned for (+0x108), whether it is turning (+0x10c) and to what facing (+0x18a).</summary>
        public (int X, int Z) TurnGoal = (-1, -1);
        public bool TurnLatch;
        public float TurnAngle;
        /// <summary>The look-around timers by mode (+0x474): frames left and kind.</summary>
        public (int Ticks, int Kind)[] Looks = new (int, int)[8];
        /// <summary>The bodyguard's latches: its protectee was an ally (+0x0f), the intercept timer (+0x44), and its protectee lost (+0x18).</summary>
        public bool Protecting, Lost;
        public int Intercept;
        /// <summary>Its AI is held (+0x18c): it neither perceives nor decides.</summary>
        public bool Held;
        /// <summary>This frame: skips its decision (+0x14), and perceived although it skips (+0x1d).</summary>
        public bool Skip, Refreshed;
        /// <summary>Whom it faces (+0x15c, 16 none), the point it keeps facing (+0x158), its goal tile (+0x168), whether to run (+1), the heading it steps along and the order.</summary>
        public int FaceTarget = 16;
        public Vector3? FacePoint;
        public (int X, int Z) Goal;
        public bool Run;
        public float Heading, TurnTo, TurnRate = 128;
        public Order Order;
        /// <summary>The point it flies to with command 8 (+0x17c, +0x184, +0x180: 20.12 fixed, here whole units).</summary>
        public (int X, int Y, int Z) FlyTo;
        /// <summary>It never remembers failing (+0x19, set for Guildenstern's final form; 0x800e4c8c then does nothing).</summary>
        public bool NeverRemembers;
        /// <summary>The move it set out to make (+0x22c).</summary>
        public PlanEntry? Executing;
        /// <summary>How it stood to Ashley when last in this room (+0x42, 0x800e6694's halfword), -1 none.</summary>
        public int Saved = -1;
        /// <summary>Dormant (+0x12d): 0 awake, 1 lying still, 2 stirring.</summary>
        public int Dormant;
        /// <summary>Its home tile and how far it wanders about it (+0xcc, +0xc8: 63 a free wander), and its pace: frames moving and idling and its odds of a new heading, one in two to the power (+0xd6, +0xd4, +0xd8).</summary>
        public (int X, int Z) Home;
        public int HomeRadius, MoveFrames, IdleFrames, Chance;
        /// <summary>The wander (0x800e1eac): its frame count (+0xc2), sub-state (+0xc0), heading or last step (+0xd0, -1 none), stall timer (+0xd2), and the random walk's tile and steps (+0x11c, +0x15d).</summary>
        public int WanderCount, WanderState, WanderHeading = -1, Stall, WalkSteps;
        public (int X, int Z) WalkTile;
        /// <summary>Whether it goes to the room's nearest special spot before a long-reaching harmful move (+0x2d: AI word bit 12, in a room with a spot), and the spot chosen (+0x134).</summary>
        public bool UsesSpots;
        public (int X, int Z) Spot;
        /// <summary>The patrol (0x800e23ac): its route (+0x101), its odds of turning back, one in two to this power (+0x102), the point it walks for (+0x100), its way along the route (+0x103: 1 or -1) and the frames before it may turn back (+0x104).</summary>
        public int Route, PatrolTurns, PatrolPoint, PatrolStep = 1, PatrolTimer;
        /// <summary>The move it falls back on against the room's breakable crates (+0x140, built at every replan by 0x800dd604): the usable action able to hit objects with the longest reach; null with none.</summary>
        public PlanEntry? Fallback;
    }

    /// <summary>The actors holding a token, newest first (BATTLE.PRG's list at +0x1c of the battle AI block, and its count at +0x12).</summary>
    private readonly List<Combatant> _holders = new();
    /// <summary>Whether an actor launched an action this frame (0x800f58e0), and how many engaged this frame (the block's +0x24).</summary>
    private bool _launched;
    private int _engagers;
    /// <summary>The actor index perceived in turn after Ashley (0x800f58d4).</summary>
    private int _rotating;
    /// <summary>The room's crates as actors (BATTLE.PRG's objects at 0x800f45e0, each in an actor slot of its own), and the four breakable ones the enemies may strike (the battle AI block's +0x28 to +0x34).</summary>
    private readonly List<Combatant> _objects = new();
    private readonly Combatant?[] _breakables = new Combatant?[4];
    /// <summary>The room's special spots ([0x800f58c8]) and patrol routes ([0x800f58d8]), as halfwords (see <see cref="Installation.MapConversion"/>).</summary>
    private int[]? _spots;
    private int[][]? _routes;
    /// <summary>The action under way and who made it, heeded as it ends.</summary>
    private Acted? _acted;

    /// <summary>How many may hold a token at once less one (the battle AI block's +0x0d: the room's word at 0x450 of sub-section 5, bits 29 to 31).</summary>
    private int TokenLimit => Math.Max(0, _view.Document.GetValueOrDefault("telegraphs", 1).AsInt32() - 1);

    /// <summary>
    /// The sine of an angle, 4096 a turn and 4096 a whole (SLUS_010.40 0x80040f28): from the executable's table of the
    /// first eighth, the angle reflected into it by its bits (a quarter turn exactly is 4096; past it the angle is taken
    /// from a half turn; its second eighth reads the cosine column), negative when the angle's bit 0x800 is set.
    /// </summary>
    private int Sin(int angle)
    {
        var table = _view.Content.Field;
        if ((angle & 0x400) != 0)
        {
            if ((angle & 0x3FF) == 0) return (angle & 0x800) != 0 ? -4096 : 4096;
            angle = 2048 - angle;
        }
        int t = angle & 0x1FF, v = (angle & 0x200) != 0 ? table.Sine(512 - t, cosine: true) : table.Sine(t, cosine: false);
        return (angle & 0x800) != 0 ? -v : v;
    }

    /// <summary>The cosine of an angle (SLUS_010.40 0x80040f24): the sine a quarter turn on.</summary>
    private int Cos(int angle) => Sin(angle + 1024);

    /// <summary>
    /// The integer square root (SLUS_010.40 0x80041b94): the value's leading zeros (the GTE's count) rounded down to
    /// even pick a mantissa of 64 to 255 to look up in the executable's root table, shifted back by half the exponent
    /// and down 12; 0 for 0.
    /// </summary>
    private int Root(long value)
    {
        int n = (int)value, zeros = n < 0 ? System.Numerics.BitOperations.LeadingZeroCount((uint)~n) : System.Numerics.BitOperations.LeadingZeroCount((uint)n);
        if (zeros == 32) return 0;
        int even = zeros & 0x1E, mantissa = even >= 24 ? n << (even - 24) : n >> (24 - even);
        return (int)((uint)(_view.Content.Field.SquareRoot(mantissa - 64) << ((31 - even) >> 1)) >> 12);
    }

    /// <summary>
    /// The angle of a ground offset (SLUS_010.40 ratan2 0x8004137c, called with x then z): 0 along +z, 1024 along +x,
    /// -2048 to 2048; the executable's arctangent table by the smaller over the larger in 512ths, mirrored into its
    /// octant. The direction from a point toward another is the angle of the first less the second, as a facing
    /// (ground direction (-sin h, -cos h)).
    /// </summary>
    private int Angle(float dx, float dz)
    {
        int y = (int)dx, x = (int)dz;
        if (x == 0 && y == 0) return 0;
        int ax = Math.Abs(x), ay = Math.Abs(y), table = ax < ay ? 512 - _view.Content.Field.Arctangent((ax << 9) / ay) : _view.Content.Field.Arctangent((ay << 9) / ax);
        if (x < 0) table = 1024 - table;
        table <<= 1;
        return y < 0 ? -table : table;
    }

    /// <summary>The facing from an actor toward a point (its place less the point's, by <see cref="Angle"/>), 0 to 4095.</summary>
    private int Toward(Combatant c, Vector3 point) => Angle((int)c.Actor.Position.X - (int)point.X, (int)c.Actor.Position.Z - (int)point.Z) & 0xFFF;

    /// <summary>
    /// A ground offset scaled to 4096 long (SLUS_010.40 0x80041af4, with no height): its squared length (each part a
    /// 16-bit value, as the GTE squares it) shifted to an even exponent picks a mantissa for the executable's inverse
    /// root table; each part times that (the GTE's GPF, unshifted), down half the exponent.
    /// </summary>
    /// <summary>A vector made 4096 long by the GTE's normalise (SLUS 0x80041abc, its inverse-root table): each axis a 16-bit value.</summary>
    private (int X, int Y, int Z) Normalise(int dx, int dy, int dz)
    {
        dx = (short)dx;
        dy = (short)dy;
        dz = (short)dz;
        int length = dx * dx + dy * dy + dz * dz;
        int zeros = length < 0 ? System.Numerics.BitOperations.LeadingZeroCount((uint)~length) : System.Numerics.BitOperations.LeadingZeroCount((uint)length);
        int even = zeros & ~1, mantissa = even >= 24 ? length << (even - 24) : length >> (24 - even);
        int inverse = _view.Content.Field.InverseSquareRoot(mantissa - 64), shift = ((31 - even) >> 1) & 31;
        return (dx * inverse >> shift, dy * inverse >> shift, dz * inverse >> shift);
    }
    private int Roll(int range) => (int)(_random.Randi() & (uint)(range - 1));
    internal static (int X, int Z) TileOf(Combatant c) => ((int)Mathf.Floor(c.Actor.Position.X / 128), (int)Mathf.Floor(c.Actor.Position.Z / 128));
    private static Vector3 Centre((int X, int Z) tile, float y) => new(tile.X * 128 + 64, y, tile.Z * 128 + 64);
    private Combatant? At(int index) => index is >= 0 and < 16 ? Fighters().Find(c => c.Index == index) ?? _objects.Find(o => o.Index == index) : null;

    /// <summary>
    /// Gives an actor its AI record as it joins the battle (BATTLE.PRG 0x800e6178 with the AI word's decoding,
    /// 0x800e5a9c): its senses, faction, temper, retreat rule and movement from the AI word; its behaviour routine,
    /// group and leader's group from its room record; its heights and girth from its model. Then its relations are set
    /// (<see cref="Relate"/>) and leaders found (0x800e5998).
    /// </summary>
    private void Awaken(Combatant c, uint ai, GDictionary stats)
    {
        if (c.Cube || c.Index >= 16) return;
        var definition = _view.Content.Character(c.Actor.Character);
        int girth = definition.GetValueOrDefault("girth", 63).AsInt32();
        var (sight, cone, rate) = _view.Content.Field.Senses(ai);
        var m = new Mind
        {
            Height = (int)c.Centre, Head = definition.GetValueOrDefault("height", 0).AsInt32(),
            Radius = c.Index == 0 ? 40 : Math.Min(girth, 62),
            SightSquared = sight, HearingSquared = Math.Min(sight, 0x240000u),
            ConeCosine = Cos(cone / 2 * 4096 / 360),
            DefaultTurn = girth > 128 ? 32 : 128,
            KeepsFacing = (ai >> 7 & 1) != 0, SearchesLost = (ai >> 13 & 1) != 0, BlindSight = (ai >> 15 & 1) != 0,
            Forgets = (ai >> 23 & 1) != 0, Defends = (ai >> 24 & 1) != 0, Retaliates = (ai >> 25 & 1) != 0, Avenges = (ai >> 26 & 1) != 0,
            Convertible = (ai >> 27 & 1) != 0, FleeRule = (int)(ai >> 28 & 3),
            Behaviour = (Behaviour)Math.Min(stats.GetValueOrDefault("behaviour", 0).AsInt32(), (int)Behaviour.None),
            Group = stats.GetValueOrDefault("group", 0).AsInt32(), LeaderGroup = stats.GetValueOrDefault("leader", 0).AsInt32(),
            HomeRadius = stats.GetValueOrDefault("home_radius", 0).AsInt32(), Dormant = stats.GetValueOrDefault("dormant", false).AsBool() ? 1 : 0,
            UsesSpots = (ai >> 12 & 1) != 0 && Array.Exists(Spots, spot => (spot & 0x8000) != 0),
            Route = stats.GetValueOrDefault("route", 0).AsInt32() & 3, Routes = c.Routes,
            PatrolTurns = _view.Content.Field.PatrolTurns(stats.GetValueOrDefault("patrol_turns", 0).AsInt32()),
        };
        if (stats.GetValueOrDefault("home", new Godot.Collections.Array()).AsGodotArray() is { Count: 2 } home) m.Home = (home[0].AsInt32(), home[1].AsInt32());
        (m.MoveFrames, m.IdleFrames, m.Chance) = _view.Content.Field.WanderPace(ai);
        m.TurnSpeed = m.DefaultTurn * rate >> 4;
        // AI +0x2a (0x800e633c): whether the model has a bone of part group 2, which its twist needs (0x800a152c).
        m.Twists = definition.GetValueOrDefault("twists", false).AsBool();
        c.Faction = (int)(ai >> 21 & 3);
        // Its movement profiles: their flags (+0x15e) and the larger of their speeds, its reach extension (+0xb3, 0x800e685c).
        var profiles = stats.GetValueOrDefault("moves", new Godot.Collections.Array()).AsGodotArray();
        if (profiles.Count == 2)
        {
            // Its record keeps nine tenths of each speed, truncated (0x800769b0, 0x80076a04).
            (m.FlagsA, m.SpeedA) = (profiles[0].AsGodotArray()[0].AsInt32(), profiles[0].AsGodotArray()[1].AsInt32() * 9 / 10);
            (m.FlagsB, m.SpeedB) = (profiles[1].AsGodotArray()[0].AsInt32(), profiles[1].AsGodotArray()[1].AsInt32() * 9 / 10);
        }
        m.Moves = m.FlagsA | m.FlagsB;
        // Its steps (0x800e5a9c): their reach in tiles (bits 10-11), the height one may rise or drop (bits 8-9), the
        // trap marks it shuns (bits 30-31, none for a profile with 0xc0), and its girth for spacing (+0x94).
        m.StepReach = (int)(ai >> 10 & 3) + 1;
        m.StepHeight = new[] { 0, 130, 258, 514 }[ai >> 8 & 3];
        m.Shuns = (m.Moves & 0xC0) != 0 ? 0 : new[] { 4, 5, 5, 7 }[ai >> 30];
        m.Girth = girth > 128 ? girth : m.Radius;
        m.Airborne = c.Gait == Gait.Fly && c.Altitude > 16;
        m.ReachBonus = Math.Max(m.SpeedA, m.SpeedB);
        // Its hop's reach (0x800e6178): the hopping profile's speed, 128 units a point, kept within its sight.
        m.HopReach = (m.FlagsA & 8) != 0 ? m.SpeedA << 7 : (m.FlagsB & 8) != 0 ? m.SpeedB << 7 : 0;
        if ((long)(m.HopReach - 64) * (m.HopReach - 64) > m.SightSquared) m.HopReach = Root(m.SightSquared) - 64;
        c.Mind = m;
        // Guildenstern's final form (characters 172 and 173, 0x800e5e3c): its own routine, no memory of failing, and the
        // fight's block for its orbit and swoop (0x800e7660).
        if (stats.GetValueOrDefault("id", 0).AsInt32() is 172 or 173)
        {
            m.Behaviour = Behaviour.FinalForm;
            m.NeverRemembers = true;
            _finalForm = new FinalFormBlock { Owner = c, Path = _view.Content.Field.SwoopPath() };
        }
        Relate(c);
        // Leaders (0x800e5998): one without finds the first actor, Ashley first, whose group is its leader's group.
        foreach (var follower in Fighters())
        {
            if (follower.Mind is not { Leader: 16 } f) continue;
            foreach (var leader in Fighters()) if (leader.Mind is { } l && l.Group == f.LeaderGroup) { f.Leader = leader.Index; break; }
        }
    }

    /// <summary>
    /// Sets how an actor stands to every other (BATTLE.PRG 0x800e7370): 2 to itself, else by its faction and whether
    /// the other is another of factions 0 to 2 (<see cref="Runtime.FieldContent.Relation"/>); one that has no relation to it
    /// yet is set in turn.
    /// </summary>
    private void Relate(Combatant s)
    {
        var m = s.Mind!;
        foreach (var k in Fighters())
        {
            if (k.Mind is not { } other) continue;
            int r = k == s ? 2 : _view.Content.Field.Relation(s.Faction, !(k != Ashley && k.Faction < 3));
            m.Relations[k.Index] = m.Relations[k.Index] & ~0xC0u | (uint)r << 6;
            if ((other.Relations[s.Index] >> 6 & 3) == 0) Relate(k);
        }
    }

    /// <summary>
    /// How a room character stood to Ashley as she leaves (BATTLE.PRG 0x800e6694): bit 0 won over, bits 1-2 its
    /// relation, 3-4 the relation it goes back to, 5 not dormant, 6-12 the frames left of an overridden relation; -1
    /// without an AI.
    /// </summary>
    public int Bearing(Combatant c)
    {
        if (c.Mind is not { } m) return -1;
        uint word = m.Relations[0];
        return (m.Converted ? 1 : 0) | (int)(word >> 6 & 3) << 1 | (int)(word >> 28 & 3) << 3 | (m.Dormant == 0 ? 1 : 0) << 5 | (int)(word >> 20 & 0x7F) << 6;
    }

    /// <summary>
    /// Puts back how a room character stood to Ashley (BATTLE.PRG 0x800e72d0, run for every actor as each is made): from
    /// its kept halfword, its relation, the one it goes back to and the frames left of an override; one won over is won
    /// over again (0x800dc210). One already awake there does not lie dormant again (0x800e6538).
    /// </summary>
    public void Restore(Combatant c, int standing)
    {
        if (c.Mind != null) c.Mind.Saved = standing;
        if (c.Mind != null && standing >= 0 && (standing & 0x20) != 0) c.Mind.Dormant = 0;
        foreach (var a in Fighters())
        {
            if (a.Mind is not { Saved: >= 0 } m) continue;
            int h = m.Saved;
            m.Relations[0] = m.Relations[0] & 0xC80FFF30u | (uint)(h >> 3 & 3) << 28 | (uint)(h >> 1 & 3) << 6 | (uint)(h >> 6 & 0x7F) << 20;
            if ((h & 1) != 0) Convert(a, true);
        }
    }

    /// <summary>
    /// One battle frame of the enemies' AI (BATTLE.PRG's callbacks at 0x800f16bc): the next actor perceived in turn
    /// (0x800e50c8), every actor's tick (<see cref="Tick"/>), then every enemy's decision (<see cref="Decide"/>), then
    /// their bodies carry out the orders (<see cref="Drive"/>).
    /// </summary>
    private void StepEnemies()
    {
        if (Ashley == null || Ashley.Dead) return;
        _launched = false;
        _engagers = 0;
        if (_planClock < 0xFFFE) _planClock++;
        foreach (var c in Combatants.Values) c.Recovery = c.Recovery >= 36 ? c.Recovery - 35 : 0;
        PlaceObjects();
        var fighters = Fighters();
        // The crates are perceived in turn too (their nodes' +0x1c has 0x10, which puts them in the mask at
        // 0x800f590c), and stand in lines of fire.
        var actors = new List<Combatant>(fighters);
        foreach (var crate in _objects) if (crate.Index < 16) actors.Add(crate);
        for (int i = 0; i < 16; i++)
        {
            _rotating = (_rotating + 1) & 15;
            if (actors.Exists(f => f.Index == _rotating && (f.Mind != null || f.Cube))) break;
        }
        // One that has left the battle lets its token go and its icons (0x800d82cc, 0x800d821c).
        foreach (var c in Combatants.Values)
            if (c.Mind != null && c != Ashley && (c.Dead || !c.Hostile || !c.Actor.Visible))
            {
                ReleaseToken(c);
                Show(c, 0);
                Show(c, 0x40);
            }
        // The tick's first steps for every actor (0x800d8ef4 steps 2 and 3): its voiced cue is wiped (one made by a
        // replan before this tick is never heard), and its icon and voice timers run.
        foreach (var c in fighters)
        {
            if (c.Mind is not { } m || c == Ashley) continue;
            m.Cue = 0;
            if (m.IconTimer != 0) Show(c, --m.IconTimer == 0 ? 0 : m.Icon);
            if (m.VoiceLockout > 0) m.VoiceLockout--;
        }
        foreach (var c in fighters) if (Acts(c)) Tick(c, actors);
        foreach (var c in fighters) if (Acts(c)) Decide(c, fighters);
        foreach (var c in fighters) if (Acts(c)) Drive(c);
    }

    /// <summary>Whether an enemy fights by the AI: a hostile one with its weapon drawn.</summary>
    private bool Acts(Combatant c) => c != Ashley && c.Mind != null && c.Hostile && c.Drawn && !c.Dead && c.Hp > 0;

    /// <summary>
    /// An enemy's AI tick (BATTLE.PRG 0x800d8ef4): its icon, cooldown and token timers; held, taken out, paralysed or
    /// mid-hop, it does no more; its oscillation pause; its stuck count (2700 frames on one tile); an alert voiced when
    /// its body's code changed (<see cref="Voice"/>); the ring of its positions that catches it going back and forth (a
    /// 30-frame pause); its perception of Ashley and of the actor in turn (<see cref="Perceive"/>); its plan's upkeep (<see cref="Recheck"/>); whether it retreats; and its memory.
    /// </summary>
    private void Tick(Combatant c, List<Combatant> fighters)
    {
        var m = c.Mind!;
        m.Refreshed = false;
        if (c.Cooldown > 0) c.Cooldown--;
        StepToken(c);
        m.Skip = true;
        if (m.Held || c.Suspended || c.Has(Paralysis) || c.Mover is { Busy: true } || c.HopState != 0 || m.Settling != 0 || m.Settled) return;
        if (m.Lost)
        {
            if (m.IconTimer != 0 && m.Icon == 6) { KeepChase(c); return; }
            m.Lost = false;
        }
        if (m.Pause != 0)
        {
            m.Pause--;
            Face(c);
            Perceive(c, Ashley!, fighters);
            m.Refreshed = true;
            if (m.SameTile >= 2) m.SameTile--;
            KeepChase(c);
            return;
        }
        m.Skip = false;
        var tile = TileOf(c);
        if (tile == m.Tile)
        {
            if (m.SameTile != 0 && --m.SameTile == 0)
            {
                m.UnstickFace = 16;
                m.Stuck = true;
                m.Cycle = 40;
                m.Memory = tile;
            }
            if (m.Cycle != 0 && --m.Cycle == 0)
            {
                if (++m.Unstick >= 5) m.Unstick = 1;
                m.Cycle = 40;
            }
            m.Still = true;
        }
        else
        {
            m.SameTile = 2700;
            m.Still = false;
            if (m.Stuck) m.Memory = null;
            m.Stuck = false;
        }
        // A change in what its body does (but for a stop, code 8, which it never gives) is voiced as an alert.
        if (m.LastCommand != m.Command) Voice(c, 1, 64);
        m.LastCommand = m.Command;
        m.Tile = tile;
        // Walking, its last eight positions repeating every four frames is an oscillation: it pauses 30 frames.
        if (m.Order is Order.Path or Order.Away)
        {
            if (m.RingFill == 0xFF && Same(m.RingX) && Same(m.RingZ))
            {
                m.RingFill = 0;
                m.Pause = 30;
                KeepChase(c);
                m.Skip = true;
                return;
            }
            m.RingX[m.RingIndex] = (byte)(int)c.Actor.Position.X;
            m.RingZ[m.RingIndex] = (byte)(int)c.Actor.Position.Z;
            m.RingFill |= 1 << m.RingIndex;
            m.RingIndex = (m.RingIndex + 1) & 7;
        }
        else m.RingFill = 0;
        if ((m.Detour >> 5 & 0x7F) != 0) m.Detour -= 1u << 5;
        Perceive(c, Ashley!, fighters);
        if (_rotating != 0 && fighters.Find(f => f.Index == _rotating && (f.Mind != null || f.Cube)) is { } other) Perceive(c, other, fighters);
        if (Recheck(c)) { KeepChase(c); m.Skip = true; return; }
        if (m.LeapHold != 0) m.LeapHold--;
        // Whether it retreats (0x800d93ac): never; under a quarter or half its hit points; or while it has fled lately
        // or knows of a foe; any rule also with no plan and foes about.
        bool retreat = m.FleeRule switch
        {
            1 => c.Hp * 4 < c.MaxHp,
            2 => c.Hp * 2 < c.MaxHp,
            3 => m.FleeTimer != 0 || fighters.Exists(k => k.Index < 16 && (m.Relations[k.Index] & 0x20) != 0 && (m.Relations[k.Index] >> 6 & 3) == 3),
            _ => false,
        };
        m.Retreating = m.FleeRule != 0 && (retreat || m.Phase == 2);
        // Its memory of a failure goes once its 900 frames are up, or once the one it failed on leaves that tile.
        if (!m.Stuck && m.Memory != null)
        {
            if (m.MemoryTimer == 0 || --m.MemoryTimer == 0) m.Memory = null;
            else if (At(m.MemoryTarget) is not { } target || TileOf(target) != m.MemoryTargetTile) m.Memory = null;
        }
    }

    private static bool Same(byte[] ring) => ring[0] == ring[4] && ring[1] == ring[5] && ring[2] == ring[6] && ring[3] == ring[7];

    /// <summary>
    /// How one actor perceives another (BATTLE.PRG 0x800d87e8), into its relation word and distance for it: the squared
    /// distance between their reference heights; the line of sight, every other call at eye height (bit 30) or at the
    /// centre (bit 2, which gives bit 30 too), clear on one tile; inside its view cone (bit 3); seen (bit 0) within sight
    /// in the cone with a line of sight (unless it needs none), or while its memory of being struck runs (bits 15-19);
    /// just lost (bit 1); aware (bit 5) seen or within hearing with a line of sight; perceived for targeting (bit 31)
    /// seen or on a tile everyone is perceived on; another actor no farther standing within the target's girth of the
    /// line (bit 27); the overridden relation's timer (bits 20-26); and a foe unseen over 120 frames forgotten.
    /// </summary>
    private void Perceive(Combatant self, Combatant other, List<Combatant> fighters)
    {
        var m = self.Mind!;
        var om = other.Mind;
        int o = other.Index;
        var me = self.Actor.Position;
        var them = other.Origin;
        uint d2 = Distance(me, m.Height, them, om?.Height ?? -64);
        m.Distances[o] = d2;
        uint rel = m.Relations[o];
        var theirs = TileOf(other);
        // A crate (one of the objects at 0x800f45e0) or one on its tile is always in its line of sight.
        if (theirs == m.Tile || other.Cube) rel |= 0x40000004;
        else
        {
            m.Sightings = (m.Sightings + 1) & 0xFF;
            if ((m.Sightings & 1) != 0) rel = Clear(them, om?.Head ?? 128, me, m.Head) ? rel | 1u << 30 : rel & ~(1u << 30);
            else rel = Clear(them, om != null ? Math.Abs(om.Height) : 64, me, Math.Abs(m.Height)) ? rel | 4 : rel & ~4u;
            if ((rel & 4) != 0) rel |= 1u << 30;
        }
        rel = Cone(self, them, d2) >= m.ConeCosine ? rel | 8 : rel & ~8u;
        if (other == self) rel = (rel | 0x21) & ~2u;
        else
        {
            bool sighted = d2 <= m.SightSquared && (rel & 8) != 0 && (m.BlindSight || (rel >> 30 & 1) != 0);
            uint memory = rel >> 15 & 0x1F;
            if (memory != 0) { rel = rel & ~(0x1Fu << 15) | (memory - 1) << 15; sighted = true; }
            bool was = (m.Relations[o] & 1) != 0;
            rel = sighted ? rel | 1 : rel & ~1u;
            rel = !sighted && was ? rel | 2 : rel & ~2u;
            rel = sighted || d2 < m.HearingSquared && (rel >> 30 & 1) != 0 ? rel | 0x20 : rel & ~0x20u;
        }
        rel = _view.Perceived(theirs.X, theirs.Z) || (rel & 1) != 0 ? rel | 1u << 31 : rel & ~(1u << 31);
        // Another actor no farther than the target (by what it last measured, 0 for one never perceived) in front of
        // the line to it and within the target's girth of it stands in the line of fire.
        int toward = Angle(them.X - me.X, them.Z - me.Z), girth = om?.Radius ?? 63;
        bool occluded = false;
        foreach (var k in fighters)
        {
            if (k == self || k == other || k.Index >= 16 || d2 < m.Distances[k.Index]) continue;
            var at = k.Actor.Position;
            int off = Angle(at.X - me.X, at.Z - me.Z) - toward;
            if (Cos(off) < 0) continue;
            if (Math.Abs((long)Root(m.Distances[k.Index]) * Sin(off) >> 12) < girth) { occluded = true; break; }
        }
        rel = occluded ? rel | 1u << 27 : rel & ~(1u << 27);
        m.Relations[o] = rel;
        if (other == self || om == null) return;
        uint timer = rel >> 20 & 0x7F;
        if (timer != 0)
        {
            rel = rel & ~(0x7Fu << 20) | --timer << 20;
            if (timer == 0) rel = rel & ~0xC0u | (rel >> 28 & 3) << 6;
        }
        if (m.Forgets && (rel >> 6 & 3) == 3)
        {
            if ((rel & 1) != 0) rel &= ~(0x7Fu << 8);
            else
            {
                uint unseen = ((rel >> 8) + 1) & 0x7F;
                rel = rel & ~(0x7Fu << 8) | unseen << 8;
                if (unseen > 120) rel = rel & ~0xC0u | 1u << 6;
            }
        }
        m.Relations[o] = rel;
    }

    /// <summary>
    /// How squarely an actor looks at a point (BATTLE.PRG 0x800e437c), 4096 dead ahead to -4096 behind: the cosine
    /// between the way it looks and the ground offset to the point (normalised, 0x80041af4), 4096 at no distance. The
    /// way it looks is its body's facing (body +0x26, the port's <see cref="EventActor.Facing"/>: both run 4096 a turn
    /// with the ground direction (-sin h, -cos h)) turned by its upper body's twist (body +0x14, <see cref="Mind.Twist"/>); the
    /// executable takes the cosine and sine of 3072 less it, which are that direction.
    /// </summary>
    private int Cone(Combatant self, Vector3 point, uint d2)
    {
        if (d2 == 0) return 4096;
        int a = (3072 - (short)((int)self.Actor.Facing + self.Mind!.Twist)) & 0xFFF, along = Cos(a), across = Sin(a);
        var me = self.Actor.Position;
        var (nx, _, nz) = Normalise((short)(int)point.X - (short)(int)me.X, 0, (short)(int)point.Z - (short)(int)me.Z);
        return Math.Clamp((nx * along + nz * across) >> 12, -4096, 4096);
    }


    /// <summary>The squared distance between two points at heights raised by their offsets (BATTLE.PRG 0x800e4624), each difference as a 16-bit value.</summary>
    private static uint Distance(Vector3 a, int ha, Vector3 b, int hb)
    {
        long dx = (short)((int)a.X - (int)b.X), dy = (short)((int)a.Y + ha - ((int)b.Y + hb)), dz = (short)((int)a.Z - (int)b.Z);
        return (uint)(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Whether the line from one point (raised by <paramref name="hp"/>) to another (raised by <paramref name="hq"/>)
    /// stays above the floor at each of its sixteen steps (BATTLE.PRG 0x800e44e8 and 0x800e4478): x is taken unsigned,
    /// and each step's tile is read from the enemies' floors grid (<see cref="Survey"/>), a filled pit as floor 0 and
    /// any other tile as its floor with its crates. The two points stand in the room, so every step's tile is on the grid.
    /// </summary>
    private bool Clear(Vector3 p, int hp, Vector3 q, int hq)
    {
        int px = (int)p.X & 0xFFFF, py = (int)p.Y - hp, pz = (short)(int)p.Z;
        int sx = (((int)q.X & 0xFFFF) - px) >> 4, sy = ((int)q.Y - hq - py) >> 4, sz = ((short)(int)q.Z - pz) >> 4;
        for (int i = 0; i < 16; i++)
        {
            px += sx; py += sy; pz += sz;
            int v = _floors[((pz >> 7) * 32 + (px >> 7)) & 1023];
            int floor = (v & 0x2000) != 0 ? 0 : ((v << 22 >> 16) - ((v & 0x1C00) >> 3)) << 17 >> 17;
            if (py >= floor) return false;
        }
        return true;
    }

    /// <summary>
    /// The decision pass for an enemy (BATTLE.PRG 0x800daed0): one that skipped its tick decides nothing, unless it
    /// perceived anyway, when it only measures Ashley and tries to launch. Otherwise its orders are cleared and it acts
    /// by priority (0x800db820): launch a move (<see cref="Launch"/>), step off a tile it is stuck or failed on
    /// (<see cref="Unstick"/>), its behaviour routine (<see cref="Behave"/>), else wander.
    /// </summary>
    private void Decide(Combatant c, List<Combatant> fighters)
    {
        var m = c.Mind!;
        if (m.Skip && !m.Refreshed) return;
        if (m.Skip)
        {
            m.Distances[0] = Distance(c.Actor.Position, m.Height, Ashley!.Actor.Position, Ashley.Mind?.Height ?? -64);
            Launch(c);
            return;
        }
        m.FaceTarget = 16;
        m.FacePoint = null;
        m.Run = false;
        m.Descends = m.CrossesZones = m.Wary = m.Away = m.Nearer = m.Wades = m.Grounded = m.Closing = m.Beside = false;
        m.Aimed = null;
        m.Order = Order.Stand;
        if (!(Launch(c) || Unstick(c) || Behave(c, fighters))) Wander(c);
        // Its stance (0x800db7b4): one dormant holds still; one stirring rises and is awake.
        if (m.Dormant == 1) m.Order = Order.Stand;
        else if (m.Dormant == 2) m.Dormant = 0;
    }

    /// <summary>
    /// Tries to launch a planned move (BATTLE.PRG 0x800dad9c): each in turn (<see cref="TryLaunch"/>); then its
    /// fallback move (<see cref="BuildFallback"/>) on each of the room's four breakable crates in turn, one Ashley is
    /// not carrying that rests (its target stays the last crate tried).
    /// </summary>
    private bool Launch(Combatant c)
    {
        foreach (var entry in c.Plans.ToArray()) if (TryLaunch(c, entry)) return true;
        if (c.Mind!.Fallback is not { } fallback) return false;
        foreach (var crate in _breakables)
        {
            if (crate == null) continue;
            fallback.Target = crate;
            if (Resting(crate) && TryLaunch(c, fallback)) return true;
        }
        return false;
    }

    /// <summary>
    /// Builds an enemy's fallback move (BATTLE.PRG 0x800dd604, at every replan): of its usable actions (0x800e4df8) able
    /// to hit objects (0x800e5710's bit 6: the room lets its enemies strike crates, the action costs neither magic nor
    /// hit points, and its target kind is 14), the one with the longest reach, the first of equals; at weight 128, due
    /// now, with no margin, minding the line of fire, and needing a line of sight unless its action does not.
    /// </summary>
    private void BuildFallback(Combatant c)
    {
        var m = c.Mind!;
        m.Fallback = null;
        if (!_view.Document.GetValueOrDefault("crate_attacks", false).AsBool()) return;
        uint longest = 0;
        for (int slot = 0; slot < Math.Min(6, c.Limbs.Count); slot++)
            for (int i = 0; i < 4; i++)
            {
                int action = c.Limbs[slot].Actions[i];
                if (action == 0 || !Usable(c, slot, action)) continue;
                var record = _view.Content.Field.Action(action);
                if (record.CostKind is 1 or 3 || record.TargetKind != 14) continue;
                uint reach2 = Reach2(c, action);
                if (reach2 <= longest) continue;
                longest = reach2;
                m.Fallback = new PlanEntry { Action = action & 0xFF, Slot = slot, Index = i, Limb = -1, Weight = 128, Target = c };
            }
    }

    /// <summary>
    /// Keeps the room's crates as actors (BATTLE.PRG's objects at 0x800f45e0): each the room holds takes the first actor
    /// index from 2 that no character holds, as it is first seen (0x800765b0; retail's setup makes them after the room's
    /// characters, 0x8008911c), and the breakable ones (kinds 2 and 4) the first free of
    /// four places the enemies strike them from (0x800e6178); one gone gives both up (0x800e7454). Each stands for the
    /// AI where the crate stands, its stand-in 150 under the crate's middle.
    /// </summary>
    private void PlaceObjects()
    {
        if (_view.Field == null) return;
        var present = new Dictionary<string, (int Kind, int X, int Z, float Bottom)>();
        foreach (var (id, kind, x, z, bottom, _) in _view.CrateObjects) present[id] = (kind, x, z, bottom);
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            if (present.ContainsKey(_objects[i].Actor.Id)) continue;
            int slot = Array.IndexOf(_breakables, _objects[i]);
            if (slot >= 0) _breakables[slot] = null;
            _objects.RemoveAt(i);
        }
        foreach (var (id, (kind, x, z, bottom)) in present)
        {
            var crate = _objects.Find(o => o.Actor.Id == id);
            if (crate == null)
            {
                if (!_view.Field.Actors.TryGetValue(id, out var node)) continue;
                crate = new Combatant
                {
                    Actor = new EventActor(id, -1, node),
                    Name = _view.Content.Data("battle_text").GetValueOrDefault("cube", "").AsString(), Hp = 1, MaxHp = 1, Cube = true, CubeKind = kind,
                };
                var taken = new HashSet<int>();
                foreach (var other in Combatants.Values) taken.Add(other.Index);
                foreach (var other in _objects) taken.Add(other.Index);
                for (crate.Index = 2; crate.Index < 16 && taken.Contains(crate.Index); crate.Index++) { }
                _objects.Add(crate);
                int free = Array.IndexOf(_breakables, null);
                if (kind is 2 or 4 && crate.Index < 16 && free >= 0) _breakables[free] = crate;
            }
            crate.Actor.Position = new Vector3(x * 128 + 64, bottom - 64 + 150, z * 128 + 64);
        }
        CountCrates();
    }

    /// <summary>Whether a crate rests where it stands: not carried, breaking or falling (its object's +18 not Ashley and +26 at 0).</summary>
    private bool Resting(Combatant crate)
    {
        foreach (var (id, _, _, _, _, resting) in _view.CrateObjects) if (id == crate.Actor.Id) return resting;
        return false;
    }

    /// <summary>Whether no foe of an enemy stands beside a crate (BATTLE.PRG 0x800dbcec): none within a squared tile distance under 3.</summary>
    private bool Unguarded(Combatant c, Combatant crate)
    {
        var tile = TileOf(crate);
        foreach (var k in Fighters())
        {
            var at = TileOf(k);
            if (Relation(c, k) == 3 && (at.X - tile.X) * (at.X - tile.X) + (at.Z - tile.Z) * (at.Z - tile.Z) < 3) return false;
        }
        return true;
    }

    /// <summary>
    /// Launches one planned move if it may be made now (BATTLE.PRG 0x800dac80): it passes the gate
    /// (<see cref="Gate"/>), takes a token (<see cref="AcquireToken"/>) and has held it 32 frames; then the move is
    /// made, and one that cools starts its cooldown (the room's, or 50 won over).
    /// </summary>
    private bool TryLaunch(Combatant c, PlanEntry e)
    {
        var m = c.Mind!;
        if (!Gate(c, e)) return false;
        AcquireToken(c);
        if (m.Token < 32) return false;
        m.Executing = e;
        _launched = true;
        m.Order = Order.Stand;
        Execute(c, e);
        if (Cooled(c, e)) c.Cooldown = m.Converted ? 50 : _view.Document.GetValueOrDefault("cooldown", 150).AsInt32();
        return true;
    }

    /// <summary>
    /// The battle core starting a launched move (command 24): its recovery and its user's, 256 a point, for actions
    /// outside 22 to 53 (0x800859b4; an action of 255 takes its user's weapon's, 0 in every zone's templates), then the
    /// spell or special attack (<see cref="EnemySpell"/>), else the attack (<see cref="Strike(Combatant, Combatant, int)"/>).
    /// </summary>
    private void Execute(Combatant c, PlanEntry e)
    {
        var record = _view.Content.Field.Action(e.Action);
        if (e.Action is < 22 or > 53) c.Recovery += ((record.Recovery == 255 ? 0 : record.Recovery) + c.RecoveryBonus) * 256;
        if (_walking.Remove(c)) c.Mover?.Halt();
        _acted = new Acted(c, e.Action, e.Target);
        // It is resolved, and its cost paid, as it sets out (0x80072ec4 then 0x80070f28 with state+12 its index), on
        // what its kind takes: its target, or every fighter its area holds.
        c.Actor.Facing = c.Actor.FacingToward(e.Target.Actor.Position);
        _links = 0;
        _exchanges = 1;
        var blow = Resolve(e.Action, c, Targets(record, c, e.Target, e.Limb), live: true);
        if (EnemySpell(c, e.Action, e.Target, blow)) return;
        _blow = blow;
        Strike(c, e.Target, blow.Targets.Count > 0 ? blow.Targets[0].Limb : e.Limb);
    }

    /// <summary>
    /// A planned move's reach and flags (BATTLE.PRG 0x800e5710, copied into the entry by 0x800dd918): its reach squared
    /// (0x800e5698: the action's range, or its user's weapon's, 32 less past 32), the margin it keeps out of
    /// (0x800e5600), and its flags: 2 keeps that margin (action flags 0x2080), 4 needs no line of sight (0x1000), 8 needs
    /// no clear line of fire (its target kinds 2, 4, 6 and 8), 16 harm (it cools), 32 help, and 1 a harmful move reaching
    /// past 0x51000 squared without action flag 0x2000 (0x800dd918), which sends one that uses special spots to one
    /// first (<see cref="Choose"/>). The fallback move (<see cref="BuildFallback"/>) keeps no margin and has flag 4 at
    /// most.
    /// </summary>
    private (uint Reach2, int Margin, int Flags) Measure(Combatant c, PlanEntry e)
    {
        var record = _view.Content.Field.Action(e.Action);
        uint reach2 = Reach2(c, e.Action);
        if (e == c.Mind?.Fallback) return (reach2, 0, (record.Flags & 0x1000) != 0 ? 4 : 0);
        int root = Root(reach2), margin = 0;
        if ((record.Flags & 0x2000) != 0) margin = _view.Content.Field.NearRange(e.Action)[1] * 32;
        else if ((record.Flags & 0x80) != 0 && RangeReach(_view.Content.Field.NearRange(e.Action)) is var near && near < root) margin = near;
        bool harms = Harms(record, e.Action, e.Target, help: false);
        int flags = ((record.Flags & 0x2080) != 0 ? 2 : 0) | ((record.Flags & 0x1000) != 0 ? 4 : 0) | ((1 << record.TargetKind & 0x154) != 0 ? 8 : 0)
            | (harms ? 16 : 0) | (Harms(record, e.Action, e.Target, help: true) ? 32 : 0)
            | (harms && reach2 > 0x51000 && (record.Flags & 0x2000) == 0 ? 1 : 0);
        return (reach2, margin, flags);
    }

    /// <summary>
    /// An action's reach squared for its user (BATTLE.PRG 0x800e5698): its range (<see cref="RangeReach"/>), or its
    /// user's weapon's for a range of 255, less 32 past 32.
    /// </summary>
    private uint Reach2(Combatant c, int action)
    {
        var record = _view.Content.Field.Action(action);
        int reach = record.Range[0] == 255 ? (int)c.Range.Z * Math.Abs(Cos((c.Tilt >> 3) * 128)) >> 12 : RangeReach(record.Range);
        return reach > 32 ? (uint)((reach - 32) * (reach - 32)) : (uint)(reach * reach);
    }

    /// <summary>
    /// How far a range record reaches (BATTLE.PRG 0x800e5568): its z radius for shapes 1, 2 and 4, else its y radius,
    /// 32 units a point, by the cosine of its angle (byte 3 less its low three bits, 128ths of a turn's 4096ths), the
    /// sine for shapes other than 1 to 4.
    /// </summary>
    private int RangeReach(int[] range)
    {
        int shape = range[3] & 7, angle = (range[3] >> 3) * 128;
        if ((1 << shape & 0x16) != 0) return range[2] * 32 * Math.Abs(Cos(angle)) >> 12;
        return range[1] * 32 * Math.Abs((1 << shape & 8) != 0 ? Cos(angle) : Sin(angle)) >> 12;
    }

    /// <summary>A planned move's reach (the root of its reach squared).</summary>
    private int Reach(Combatant c, PlanEntry e) => Root(Measure(c, e).Reach2);

    /// <summary>
    /// Whether a planned move may be launched now (BATTLE.PRG 0x800dbd80): it has weight; the planner's clock has come
    /// to it; its user is ready (<see cref="Ready"/>); its target is within its reach and, for one that keeps a margin,
    /// beyond it; a crate within 320, its user not won over and no foe beside it (<see cref="Unguarded"/>); its target
    /// perceived for targeting; Ashley not in an action; a line of sight unless it needs none, and no one in the line of
    /// fire unless it does not mind. Nothing else may be under way (the port makes one move at a time).
    /// </summary>
    private bool Gate(Combatant c, PlanEntry e)
    {
        var m = c.Mind!;
        if (e.Weight == 0 || _planClock < e.Horizon || !Ready(c, e) || e.Target.Dead || e.Target.Index >= 16) return false;
        if (_phase != Phase.Free || _enemyCast != null || _enemyRelease != null || Ashley!.Mover is { Busy: true }) return false;
        var (reach2, margin, flags) = Measure(c, e);
        uint d = m.Distances[e.Target.Index], rel = m.Relations[e.Target.Index];
        if (e.Target.Cube && (d > 0x19000 || m.Converted || !Unguarded(c, e.Target))) return false;
        if (d > reach2) return false;
        if (margin > 0 && (flags & 2) != 0 && d < (uint)(margin * margin)) return false;
        if ((rel >> 31 & 1) == 0) return false;
        if ((flags & 4) == 0 && (rel & 4) == 0) return false;
        return (flags & 8) != 0 || (rel >> 27 & 1) == 0;
    }

    /// <summary>Whether an enemy is ready to make a move (BATTLE.PRG 0x800dbcb4): not recovering, and past its cooldown for a move that cools.</summary>
    private bool Ready(Combatant c, PlanEntry e) => c.Recovery == 0 && (!Cooled(c, e) || c.Cooldown == 0);

    /// <summary>Whether a move cools (BATTLE.PRG 0x800dbc60): any, for one won over; else one that harms, not on a crate.</summary>
    private bool Cooled(Combatant c, PlanEntry e) => c.Mind!.Converted || (Measure(c, e).Flags & 16) != 0 && !e.Target.Cube;

    /// <summary>
    /// Takes a token, the right to telegraph and act (BATTLE.PRG 0x800d836c): one held stays; otherwise none while the
    /// holders already exceed the room's limit, while its refractory frames run or once anyone launched this frame.
    /// One taken within 90 frames of letting one go starts past its telegraph (at 32).
    /// </summary>
    private bool AcquireToken(Combatant c)
    {
        var m = c.Mind!;
        if (m.Token != 0) return true;
        if (TokenLimit < _holders.Count || m.Refractory != 0 || _launched) return false;
        m.Token = m.AfterRelease != 0 ? 32 : 1;
        _holders.Insert(0, c);
        return true;
    }

    /// <summary>
    /// Lets a token go (BATTLE.PRG 0x800d82cc): its age goes to 0, 48 refractory frames follow (none when it is the only
    /// enemy) and 90 more in which another is taken without a telegraph; its icon goes.
    /// </summary>
    private void ReleaseToken(Combatant c)
    {
        if (!_holders.Remove(c)) return;
        var m = c.Mind!;
        m.Token = 0;
        m.Refractory = Fighters().Count == 2 ? 0 : 48;
        m.AfterRelease = 90;
        Show(c, 0);
    }

    /// <summary>
    /// A token's frame (BATTLE.PRG 0x800dbfe4): held, it ages a frame, its icon the balloon's dots (one each 8 frames)
    /// then its mark; held 120 frames without launching, the failure is remembered and it is let go. Otherwise the 90
    /// frames after a release run (the jagged balloon), and the refractory frames.
    /// </summary>
    private void StepToken(Combatant c)
    {
        var m = c.Mind!;
        if (m.Token != 0)
        {
            m.Token = (m.Token + 1) & 0xFFFF;
            if (m.Token != 0)
            {
                if (m.Token < 121) { Show(c, (m.Token < 8 ? 0 : m.Token < 16 ? 1 : m.Token < 24 ? 2 : 3) + 1); return; }
                Remember(c);
                ReleaseToken(c);
            }
        }
        if (m.AfterRelease != 0) Show(c, --m.AfterRelease == 0 ? 0 : 4);
        if (m.Refractory != 0) m.Refractory--;
    }

    /// <summary>
    /// Whether an enemy may close to its move's reach (BATTLE.PRG 0x800db8ac): with a token; not in its refractory
    /// frames; seeing its target; while no more have engaged this frame than the limit, already a holder or with room
    /// for one more.
    /// </summary>
    private bool MayEngage(Combatant c, PlanEntry e)
    {
        var m = c.Mind!;
        if (m.Token != 0) return true;
        if (m.Refractory != 0 || (m.Relations[e.Target.Index] & 1) == 0 || TokenLimit < _engagers) return false;
        return _holders.Contains(c) || TokenLimit >= _holders.Count;
    }

    /// <summary>
    /// Sets the icon over an actor (BATTLE.PRG 0x800d820c): 0x80 adds the won-over mark, 0x40 takes it off, anything
    /// else replaces the icon (0 none, 1 to 4 the telegraph's balloon, 6 alarm, 7 searching).
    /// </summary>
    private void Show(Combatant c, int value)
    {
        if (c.Mind is not { } m || c.Index == 0) return;
        if ((value & 0x80) != 0) m.Shown |= 0x80;
        else if ((value & 0x40) != 0) m.Shown &= 0x7F;
        else m.Shown = m.Shown & 0x80 | value & 0x7F;
    }

    /// <summary>Holds an icon over an actor for some frames (BATTLE.PRG 0x800d8260).</summary>
    private static void Flash(Combatant c, int icon, int frames)
    {
        c.Mind!.Icon = icon;
        c.Mind.IconTimer = frames;
    }

    /// <summary>
    /// A reaction voiced (BATTLE.PRG 0x800e48a8): its odds drawn (a roll under <paramref name="odds"/> of
    /// <paramref name="odds"/>, so always), then, unless held or within 30 frames of the last, the cue its body cries
    /// out with its next order (<see cref="Gaze"/>): 1 alert, 2 calm.
    /// </summary>
    private void Voice(Combatant c, int cue, int odds)
    {
        if (c.Mind is not { } m) return;
        int roll = Roll(odds);
        if (m.Held || m.VoiceLockout != 0 || roll >= odds) return;
        m.VoiceLockout = 30;
        m.Cue = cue;
    }

    /// <summary>
    /// Remembers failing on its first planned move's target (BATTLE.PRG 0x800e4c8c: a token held out, or a move made):
    /// its own tile, the target and the target's tile, for 900 frames. While it stands there it steps off
    /// (<see cref="Unstick"/>).
    /// </summary>
    private void Remember(Combatant c)
    {
        var m = c.Mind!;
        if (c.Plans.Count == 0 || m.NeverRemembers) return;
        m.Memory = m.Tile;
        m.MemoryTarget = c.Plans[0].Target.Index;
        m.MemoryTimer = 900;
        m.MemoryTargetTile = TileOf(c.Plans[0].Target);
    }

    /// <summary>
    /// Chooses an enemy's state for its first planned move (BATTLE.PRG 0x800d9dd8): a patroller's is 1, approach its
    /// target; one that uses special spots takes 2 for a long-reaching harmful move (its flag 1, <see cref="Measure"/>)
    /// and picks the nearest spot (<see cref="NearestSpot"/>); any other 1.
    /// </summary>
    private void Choose(Combatant c)
    {
        var m = c.Mind!;
        if (m.Behaviour != Behaviour.Patroller && m.UsesSpots && c.Plans.Count > 0 && (Measure(c, c.Plans[0]).Flags & 1) != 0)
        {
            m.State = 2;
            NearestSpot(c);
        }
        else m.State = 1;
    }

    /// <summary>
    /// The room's special spot nearest an enemy (BATTLE.PRG 0x800dd7cc): of the eight in use (bit 15), the one at the
    /// least squared distance from its position to the tile's centre at its floor (the enemies' floors grid, which
    /// BATTLE.PRG puts in place for this, 0x800e42d4: a filled pit at its fill), the first of equals; with none in use the spot stays.
    /// </summary>
    private void NearestSpot(Combatant c)
    {
        var m = c.Mind!;
        uint least = uint.MaxValue;
        foreach (int spot in Spots)
        {
            if ((spot & 0x8000) == 0) continue;
            int x = (spot & 31) * 128 + 64, z = (spot >> 5 & 31) * 128 + 64;
            uint d = Distance(c.Actor.Position, 0, new Vector3(x, FloorOf((x >> 7, z >> 7)), z), 0);
            if (d >= least) continue;
            least = d;
            m.Spot = (spot & 31, spot >> 5 & 31);
        }
    }

    /// <summary>The room's eight special spots ([0x800f58c8], room sub-section 5 at 0x440): tile x bits 0-4, z bits 5-9, bit 15 in use.</summary>
    private int[] Spots => _spots ??= Halfwords(_view.Document.GetValueOrDefault("special_spots", new Godot.Collections.Array()).AsGodotArray());

    /// <summary>The room's four patrol routes of eight points ([0x800f58d8], room sub-section 5 at 0x400), as the spots.</summary>
    private int[][] Routes
    {
        get
        {
            if (_routes != null) return _routes;
            var routes = _view.Document.GetValueOrDefault("patrol_routes", new Godot.Collections.Array()).AsGodotArray();
            _routes = new int[routes.Count][];
            for (int r = 0; r < routes.Count; r++) _routes[r] = Halfwords(routes[r].AsGodotArray());
            return _routes;
        }
    }

    private static int[] Halfwords(Godot.Collections.Array values)
    {
        var words = new int[values.Count];
        for (int i = 0; i < words.Length; i++) words[i] = values[i].AsInt32();
        return words;
    }

    /// <summary>
    /// The behaviour routine (BATTLE.PRG's table at 0x800f1790, its AI +0x68): returns whether it gave an order.
    /// </summary>
    private bool Behave(Combatant c, List<Combatant> fighters)
    {
        var m = c.Mind!;
        var first = c.Plans.Count > 0 ? c.Plans[0] : null;
        switch (m.Behaviour)
        {
            case Behaviour.Fighter:
                // 0x800dba4c: retreating it flees, else wanders; otherwise by its plan phase.
                if (!m.Retreating) return Pursue(c, fighters);
                if (!Flee(c, true, fighters)) Wander(c);
                return true;
            case Behaviour.KeepsDistance:
            {
                // 0x800dba88: keeps 256 off (320 for one whose object flags say so), or its move's reach, never backing off.
                if (m.Retreating) return Flee(c, true, fighters);
                if (m.Phase is not (0 or 1) || first == null) return Pursue(c, fighters);
                int stop = 256;
                if (m.Phase == 0) stop = Math.Min(stop, Reach(c, first));
                return Chase(c, first.Target, stop, false) || HoldRange(c, first.Target);
            }
            case Behaviour.FollowsAlly:
            {
                // 0x800dbb98: by its plan, else after its first ally, going to it from beyond 640 and wandering by it within.
                if (Pursue(c, fighters)) return true;
                if (FirstWith(c, 2, fighters) is not { } ally) return false;
                if (m.Distances[ally.Index] > 0x64000) GoTo(c, TileOf(ally), 0);
                else WanderAround(c, TileOf(ally), 5);
                return true;
            }
            case Behaviour.Escort:
                // 0x800da5cc: engages when its move is due, else keeps 288 from its leader, facing its target there.
                return Escort(c, 288, true);
            case Behaviour.PackHunter:
            {
                // 0x800da6a4: with an ally about, closes to 1024 of the first foe before pursuing.
                if (m.Retreating) return Flee(c, true, fighters);
                if (FirstWith(c, 3, fighters) is not { } foe || FirstWith(c, 2, fighters) == null) return Pursue(c, fighters);
                return Chase(c, foe, 1024, true) || Pursue(c, fighters);
            }
            case Behaviour.Bodyguard:
                return Guard(c, fighters);
            case Behaviour.Patroller:
                // 0x800daa6c: retreating it flees or wanders; else by its plan, else its patrol.
                if (m.Retreating) { if (!Flee(c, true, fighters)) Wander(c); return true; }
                if (Pursue(c, fighters)) return true;
                Patrol(c);
                return true;
            case Behaviour.ScaredEscort:
            {
                // 0x800daad8: as an escort at 384 without backing off; then it flees whoever it is wary of for a time.
                if (Escort(c, 384, false)) return true;
                foreach (var k in fighters)
                {
                    uint w = m.Relations[k.Index];
                    if ((w >> 6 & 3) == 3 || (w >> 20 & 0x7F) == 0) continue;
                    m.Relations[k.Index] = w | 0xC0;
                    bool fled = Flee(c, false, fighters);
                    m.Relations[k.Index] = m.Relations[k.Index] & ~0xC0u | (m.Relations[k.Index] >> 28 & 3) << 6;
                    if (fled) return true;
                }
                return false;
            }
            case Behaviour.Berserk:
                // 0x800dbc0c: won over, it pursues once its telegraph is done, else keeps by Ashley at 256.
                if (m.Token >= 32 && Pursue(c, fighters)) return true;
                return Chase(c, Ashley!, 256, false) || HoldRange(c, Ashley!);
            case Behaviour.FinalForm:
                return FinalForm(c);
            default:
                return false;
        }
    }

    /// <summary>
    /// The escorts (BATTLE.PRG 0x800da5cc at 288 backing off, 0x800daad8 at 384 not): its first move engaged when due
    /// (<see cref="Engage"/>), else it keeps the distance from its leader, and in range faces its planned target.
    /// </summary>
    private bool Escort(Combatant c, int distance, bool backsOff)
    {
        var m = c.Mind!;
        if (m.Phase == 0 && c.Plans.Count > 0 && Engage(c, c.Plans[0])) return true;
        if (m.Leader >= 16 || At(m.Leader) is not { } leader) return false;
        if (Chase(c, leader, distance, backsOff))
        {
            if (m.Band == 1 && m.State != 7 && c.Plans.Count > 0)
            {
                m.FaceTarget = c.Plans[0].Target.Index;
                Face(c);
            }
            return true;
        }
        return HoldRange(c, leader);
    }

    /// <summary>
    /// The bodyguard (BATTLE.PRG 0x800da738): with a foe about and its ally protectee aware of it, it stands 320 from the
    /// protectee toward the foe (the intercept holding 10 frames); else it faces the protectee with the foe within 384 of
    /// it or keeps 320 from it; with no foe it flees; with its protectee just lost it raises the alarm.
    /// </summary>
    private bool Guard(Combatant c, List<Combatant> fighters)
    {
        var m = c.Mind!;
        var foe = FirstWith(c, 3, fighters);
        var ward = m.Leader < 16 && m.Leader != c.Index ? At(m.Leader) : null;
        bool ok = ward?.Mind != null && (m.Relations[ward.Index] >> 6 & 3) == 2;
        bool was = m.Protecting;
        m.Protecting = ok;
        if (foe != null && ok && (m.Relations[ward!.Index] & 0x20) != 0)
        {
            var w = ward.Actor.Position;
            var f = Centre(TileOf(foe), 0);
            int a1 = Angle(f.X - (w.X - 64), f.Z - (w.Z - 64)), a2 = Angle(c.Actor.Position.X - w.X, c.Actor.Position.Z - w.Z);
            int cosine = (Cos(a1) * Cos(a2) + Sin(a1) * Sin(a2)) >> 12;
            if (m.Intercept != 0) m.Intercept--;
            if (cosine >= -1200 || m.Intercept != 0)
            {
                var from = Centre(TileOf(ward), w.Y);
                var toward = (f - from) with { Y = 0 };
                var spot = from + (toward.LengthSquared() > 0 ? toward.Normalized() * 320 : Vector3.Zero);
                m.Goal = ((int)Mathf.Floor(spot.X / 128), (int)Mathf.Floor(spot.Z / 128));
                m.Run = true;
                m.Order = Order.Path;
                if (m.Intercept == 0) m.Intercept = 10;
                return true;
            }
        }
        if (ok)
        {
            if (foe != null && ward!.Mind!.Distances[foe.Index] <= 0x23FFF)
            {
                m.FaceTarget = ward.Index;
                Face(c);
                return true;
            }
            if (Chase(c, ward!, 320, true) || HoldRange(c, ward!)) return true;
        }
        if (foe == null) return Flee(c, true, fighters);
        if (was)
        {
            m.Lost = true;
            Flash(c, 6, 0);
        }
        return Flee(c, true, fighters) || Flee(c, true, fighters);
    }

    /// <summary>The first actor, Ashley first, other than itself to which it stands as <paramref name="relation"/> (BATTLE.PRG 0x800dbb2c).</summary>
    private static Combatant? FirstWith(Combatant c, int relation, List<Combatant> fighters) =>
        fighters.Find(k => k != c && k.Index < 16 && (c.Mind!.Relations[k.Index] >> 6 & 3) == relation);

    /// <summary>
    /// Pursues by plan phase (BATTLE.PRG 0x800da1d4): 0, its first move's state (<see cref="RunState"/>), else the
    /// first other move due on another target; 1, its token let go, it keeps 640 from Ashley if she is a foe, else from
    /// its first move's target (a foe at 640, else at the next move's reach), else as 2; 2, its token let go, it keeps
    /// 640 from Ashley if she is a foe, else from the nearest foe.
    /// </summary>
    private bool Pursue(Combatant c, List<Combatant> fighters)
    {
        var m = c.Mind!;
        switch (m.Phase)
        {
            case 0:
                if (c.Plans.Count == 0) return false;
                if (RunState(c, c.Plans[0])) return true;
                for (int i = 1; i < c.Plans.Count; i++)
                    if (c.Plans[i].Horizon < _planClock && c.Plans[i].Target != c.Plans[0].Target) return RunState(c, c.Plans[i]);
                return false;
            case 1:
            {
                ReleaseToken(c);
                if (Relation(c, Ashley!) == 3 && (Chase(c, Ashley!, 640, true) || HoldRange(c, Ashley!))) return true;
                if (c.Plans.Count == 0) return false;
                var target = c.Plans[0].Target;
                int stop;
                bool flag;
                if (Relation(c, target) == 3) (stop, flag) = (640, true);
                else if (c.Plans.Count < 2) return false;
                else (stop, flag) = (Reach(c, c.Plans[1]), false);
                if (Chase(c, target, stop, flag) || HoldRange(c, target)) return true;
                return Nearest(c, fighters);
            }
            case 2:
                ReleaseToken(c);
                if (Relation(c, Ashley!) == 3 && (Chase(c, Ashley!, 640, true) || HoldRange(c, Ashley!))) return true;
                return Nearest(c, fighters);
            default:
                return false;
        }
    }

    /// <summary>Phase 2's close on the nearest foe at 640 (BATTLE.PRG 0x800da1d4).</summary>
    private bool Nearest(Combatant c, List<Combatant> fighters)
    {
        var m = c.Mind!;
        Combatant? nearest = null;
        foreach (var k in fighters)
            if (k.Index < 16 && (m.Relations[k.Index] >> 6 & 3) == 3 && (nearest == null || m.Distances[k.Index] < m.Distances[nearest.Index])) nearest = k;
        return nearest != null && (Chase(c, nearest, 640, true) || HoldRange(c, nearest));
    }

    /// <summary>
    /// Runs an enemy's state for a planned move (BATTLE.PRG 0x800d9e18). State 1 approaches: to the move's reach when
    /// its target is an ally or it may engage (<see cref="MayEngage"/>), counting itself an engager and taking a token
    /// once within its token radius (the reach and its reach extension, 8064 for a hopper) seeing the target; else it
    /// hovers at that radius, 640 at least. State 2 goes to its special spot first: ready to make the move, and unless
    /// the move keeps a margin and the spot is already within its reach less the margin of the target (then as 1), it
    /// wanders within 2 tiles of the spot facing the target once its goal is within 255 of it, else runs for the spot
    /// (to within 256). State 7 (nothing planned) lets its token go and keeps 640 from Ashley if she is a foe.
    /// </summary>
    private bool RunState(Combatant c, PlanEntry e)
    {
        var m = c.Mind!;
        if (c.Plans.Count == 0 || e.Target.Mind == null) return false;
        var t = e.Target;
        switch (m.State)
        {
            case 2:
            {
                if (!Ready(c, e)) goto case 1;
                var (reach2, margin, flags) = Measure(c, e);
                var spot = Centre(m.Spot, 0) with { Y = FloorOf(m.Spot) };
                if ((flags & 2) != 0 && Root(Distance(spot, m.Height, t.Origin, t.Mind!.Height)) < Root(reach2) - margin) goto case 1;
                var goal = Centre(m.Goal, 0) with { Y = FloorOf(m.Goal) };
                if (Distance(c.Actor.Position, 0, goal, 0) <= 0xFFFF)
                {
                    m.FaceTarget = t.Index;
                    WanderAround(c, m.Spot, 2);
                    return true;
                }
                m.Run = true;
                GoTo(c, m.Spot, 256);
                return true;
            }
            case 1:
            {
                int reach = Reach(c, e);
                long radius = (m.Moves & 8) != 0 ? 8064 : reach + 32 * m.ReachBonus;
                int stop;
                bool flag, engaging = true;
                if (Relation(c, t) == 2) (stop, flag) = (reach, false);
                else if (MayEngage(c, e)) (stop, flag) = (reach, true);
                else { (stop, flag) = (Root(Math.Max(radius * radius, 0x64000)), true); engaging = false; }
                if (engaging)
                {
                    _engagers++;
                    if (m.Distances[t.Index] < radius * radius && (m.Relations[t.Index] & 1) != 0) AcquireToken(c);
                }
                return Chase(c, t, stop, flag) || HoldRange(c, t);
            }
            case 7:
                ReleaseToken(c);
                return Relation(c, Ashley!) == 3 && (Chase(c, Ashley!, 640, true) || HoldRange(c, Ashley!));
            default:
                return false;
        }
    }

    /// <summary>
    /// Engages a planned move (BATTLE.PRG 0x800da4bc, the escorts' phase 0): if it may (<see cref="MayEngage"/>), it
    /// counts itself an engager, takes a token within its reach and extension seeing the target, and closes to the
    /// reach.
    /// </summary>
    private bool Engage(Combatant c, PlanEntry e)
    {
        var m = c.Mind!;
        if (!MayEngage(c, e)) return false;
        int reach = Reach(c, e);
        long radius = reach + 32 * m.ReachBonus;
        _engagers++;
        if (m.Distances[e.Target.Index] < radius * radius && (m.Relations[e.Target.Index] & 1) != 0) AcquireToken(c);
        return Chase(c, e.Target, reach, true) || HoldRange(c, e.Target);
    }

    /// <summary>
    /// Sets the movement mode (BATTLE.PRG 0x800db4ac): changing it resets that mode's state (the chase's latches, the
    /// mode-6 turn) and its look-around.
    /// </summary>
    private static void SetMode(Combatant c, int mode)
    {
        var m = c.Mind!;
        if (mode == m.Mode) return;
        if (mode == 1)
        {
            m.Searching = false;
            m.ChaseTarget = 0;
            m.SearchTimer = 0;
            m.FaceLatch = false;
            m.Band = 0;
        }
        if (mode == 6) { m.TurnLatch = false; m.TurnGoal = (-1, -1); }
        m.Mode = mode;
        m.Looks[mode] = (0, 0);
    }

    /// <summary>
    /// Chases a target to a stop distance (BATTLE.PRG 0x800db370 and 0x800e24ec). Seeing it, its range band moves with
    /// hysteresis (into range at D-96, out past D, too close at D-128, back past D-32; far without a low line of
    /// sight): far it walks for the target's tile (running beyond D, or D+128 already running), in range it faces it,
    /// too close it backs off facing it (only when <paramref name="backsOff"/>). Losing it, it searches the last known
    /// tile for 255 frames if it may, else looks about with the searching icon.
    /// </summary>
    private bool Chase(Combatant c, Combatant t, int stop, bool backsOff)
    {
        var m = c.Mind!;
        if (t.Mind == null || t.Index >= 16) return true;
        if (m.ChaseTarget != t.Index)
        {
            m.Searching = false;
            m.SearchTimer = 0;
            m.FaceLatch = false;
            m.Band = 0;
            m.LastFlag = false;
            m.LastChaseTarget = 0;
        }
        m.ChaseTarget = t.Index;
        m.Stop = stop;
        m.Goal = Aim(c, t);
        if (!ChaseStep(c, t, backsOff)) return false;
        SetMode(c, 1);
        return true;
    }

    private bool ChaseStep(Combatant c, Combatant t, bool flag)
    {
        var m = c.Mind!;
        int stop = m.Stop;
        if (t == c) { m.Order = Order.Stand; return true; }
        var pos = t.Actor.Position;
        var half = ((int)pos.X >> 6, (int)pos.Z >> 6);
        if (half == m.TargetHalf && stop == m.LastStop && t.Index == m.LastChaseTarget && flag == m.LastFlag)
        {
            if (m.Unchanged++ > 180) { m.Unchanged = 0; m.FaceLatch = false; }
        }
        else { m.Unchanged = 0; m.FaceLatch = false; }
        m.LastFlag = flag;
        m.LastChaseTarget = t.Index;
        m.LastStop = stop;
        uint rel = m.Relations[t.Index], d = m.Distances[t.Index];
        bool result;
        if ((rel & 1) != 0)
        {
            m.Searching = false;
            static long Square(int v) => (long)v * v;
            if ((rel & 4) == 0) m.Band = 0;
            switch (m.Band)
            {
                case 0: if (d <= Square(stop - 96)) m.Band = 1; break;
                case 1: if (d > Square(stop)) m.Band = 0; else if (d <= Square(stop - 128)) m.Band = 2; break;
                case 2: if (d > Square(stop - 32)) m.Band = 1; break;
            }
            bool face = m.Band != 2 && m.FaceLatch;
            if (!flag && m.Band == 2) m.Band = 1;
            if (face || m.Band == 1)
            {
                m.FaceTarget = t.Index;
                Face(c);
            }
            else if (m.Band == 0)
            {
                long run = stop + (_walking.Contains(c) && m.Run ? 128 : 0);
                m.Run = run * run < d;
                m.FaceTarget = t.Index;
                m.Closing = true;
                m.Descends |= flag;
                m.Order = Order.Path;
                // 0x800e27ec: a hopper draws its goal in along the line to it, to the distance less how far its stop
                // is from 96; drawn onto its own tile, it keeps the goal and is beside it.
                if ((m.Moves & 8) != 0)
                {
                    var g = m.Goal;
                    int x = (int)c.Actor.Position.X, z = (int)c.Actor.Position.Z, gx = g.X * 128 + 64, gz = g.Z * 128 + 64;
                    long ex = (short)(x - gx), ez = (short)(z - gz);
                    m.Aimed = g;
                    PointAt(c, m.Tile, Angle(x - gx, z - gz) & 0xFFF, Root(ex * ex + ez * ez) - Math.Abs(stop - 96));
                    if (m.Goal == m.Tile) { m.Beside = true; m.Goal = g; }
                }
            }
            else
            {
                if (m.KeepsFacing) { m.Run = true; m.FacePoint = pos; }
                else m.FaceTarget = t.Index;
                m.Heading = Toward(c, pos) + 2048;
                m.GoalPoint = ((int)pos.X, (int)pos.Z);
                m.Wary = m.Away = true;
                m.Order = Order.Away;
                if (_walking.Contains(c)) m.FaceLatch = true;
            }
            result = true;
        }
        else
        {
            if (!m.Searching && (rel & 2) != 0)
            {
                if (m.SearchesLost)
                {
                    m.Searching = true;
                    m.SearchTimer = 255;
                    m.LastKnown = (m.TargetHalf.X >> 1, m.TargetHalf.Z >> 1);
                }
                else
                {
                    if ((rel & 0x20) != 0) return false;
                    Flash(c, 7, 40);
                    return LookAround(c, 128, 1, 1);
                }
            }
            if (m.FaceLatch)
            {
                m.FaceTarget = t.Index;
                Face(c);
                result = true;
            }
            else if (!m.Searching) return false;
            else
            {
                if (m.SearchTimer != 0) m.SearchTimer--;
                if (m.LastKnown == m.Tile)
                {
                    if ((rel & 0x20) != 0) return false;
                    Flash(c, 7, 40);
                    if (LookAround(c, 128, 1, 1)) return true;
                    m.Searching = false;
                    return false;
                }
                if (m.SearchTimer == 0) { m.Searching = false; return false; }
                m.Run = true;
                m.Closing = true;
                m.Goal = m.LastKnown;
                m.Order = Order.Path;
                result = true;
            }
        }
        m.TargetHalf = half;
        return result;
    }

    /// <summary>Keeps the chase's last known half-tile of its target fresh on a frame it skips (BATTLE.PRG 0x800e48f8).</summary>
    private void KeepChase(Combatant c)
    {
        var m = c.Mind!;
        if (m.Mode == 1 && At(m.ChaseTarget) is { } t) m.TargetHalf = ((int)t.Actor.Position.X >> 6, (int)t.Actor.Position.Z >> 6);
    }

    /// <summary>
    /// Holds in range (BATTLE.PRG 0x800db74c and 0x800e2b2c, mode 6): one that turns (its AI word's turning rate) turns
    /// to face the target once aware of it on a new tile, at its turn speed, and once facing it keeps turning with it
    /// while aware, else looks about.
    /// </summary>
    private bool HoldRange(Combatant c, Combatant t)
    {
        var m = c.Mind!;
        if (t.Index >= 16) return false;
        m.Goal = TileOf(t);
        if (!Turn(c, t)) return false;
        SetMode(c, 6);
        return true;
    }

    /// <summary>
    /// The hold in range's turn (BATTLE.PRG 0x800e2b2c): with no turn speed it does nothing; seeing its target where its
    /// goal has changed it latches the angle to it; latched, it turns head first at its range turn speed until it looks
    /// that way, then, the target no longer seen, looks about (kind 1) and lets the latch go, else takes up the angle
    /// afresh. (Its branch on AI +0x481 is never taken: nothing in BATTLE.PRG writes that byte.)
    /// </summary>
    private bool Turn(Combatant c, Combatant t)
    {
        var m = c.Mind!;
        if (t == c) { m.Order = Order.Stand; return true; }
        if (m.TurnSpeed == 0) return false;
        float a = Toward(c, t.Actor.Position);
        uint rel = m.Relations[t.Index];
        if (m.Goal != m.TurnGoal && (rel & 0x20) != 0 && !m.TurnLatch) { m.TurnLatch = true; m.TurnAngle = a; m.TurnGoal = m.Goal; }
        if (!m.TurnLatch) return false;
        m.TurnRate = m.TurnSpeed;
        if ((((int)c.Actor.Facing + m.Twist) & 0xFFF) != ((int)m.TurnAngle & 0xFFF)) { m.TurnTo = m.TurnAngle; m.Order = Order.Turn; return true; }
        if ((rel & 0x20) == 0)
        {
            if (LookAround(c, 128, 1, 6)) return true;
            m.TurnLatch = false;
            return false;
        }
        m.TurnAngle = a;
        m.TurnGoal = m.Goal;
        m.TurnTo = a;
        m.Order = Order.Turn;
        return true;
    }

    /// <summary>
    /// Faces (BATTLE.PRG 0x800e2ccc): whom it faces, at its usual turn speed, head first (command 4); else the point it
    /// keeps facing, the body alone (command 3); else, head first, the lowest by the floors grid of the four tiles
    /// about the way it looks (its facing and twist), tried ahead, then a quarter one way, then the other way, then
    /// behind (retail's running sum of the steps), on the map; else it stands.
    /// </summary>
    private void Face(Combatant c)
    {
        var m = c.Mind!;
        if (Hover(c)) return;
        m.TurnRate = m.DefaultTurn;
        if (m.FaceTarget != 16 && At(m.FaceTarget) is { } t) { m.TurnTo = Toward(c, t.Actor.Position); m.Order = Order.Turn; m.Command = Body.Turn; return; }
        if (m.FacePoint is { } point) { m.TurnTo = Toward(c, point); m.BodyTurn = true; m.Order = Order.Turn; m.Command = Body.Turn; return; }
        int v = (short)(int)c.Actor.Facing + m.Twist, q = (v & 0xFFF) >> 10, dir = (v & 0x3FF) <= 512 ? 2 * q : ((q + 1) & 3) * 2;
        int best = int.MinValue, chosen = -1;
        for (int i = 0; i < 4; i++)
        {
            dir = (dir + 2 * i) & 7;
            int x = (m.Tile.X + Directions[dir].X) & 0xFF, z = (m.Tile.Z + Directions[dir].Z) & 0xFF;
            if (x >= _width || z >= _depth) continue;
            int floor = FloorOf((x, z));
            if (floor > best) { best = floor; chosen = dir; }
        }
        if (chosen < 0) { m.Order = Order.Stand; m.Command = Body.Stand; return; }
        m.TurnTo = chosen << 9;
        m.Order = Order.Turn;
        m.Command = Body.Turn;
    }

    /// <summary>The eight directions by facing octant (BATTLE.PRG 0x800f16ec): tile steps in x and z.</summary>
    private static readonly (int X, int Z)[] Directions = { (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1), (1, 1), (1, 0), (1, -1) };

    /// <summary>
    /// Looks about (BATTLE.PRG 0x800e4180): a look of kind 1 (70 frames: one way, the other, then ahead) or 2 (24
    /// frames) starts on a roll under its chance and runs its frames; returns whether it is still looking. It stands
    /// while its head turns (command 16, at 64 a frame, held by the body as it stands, <see cref="Perform"/>): kind 1 800 to its left for 17 frames, 800
    /// to its right for 35, then ahead; kind 2 1280 for 13 frames (no more than 800 shows), then ahead.
    /// </summary>
    private bool LookAround(Combatant c, int chance, int kind, int slot)
    {
        var m = c.Mind!;
        var (ticks, held) = m.Looks[slot];
        if (ticks == 0)
        {
            if (kind == 3 || chance < Roll(128)) return false;
            ticks = new[] { 20, 70, 24, 1 }[kind];
            held = kind;
        }
        m.Looks[slot] = (ticks - 1, held);
        // The glance is by the kind asked for now (retail's switch on its argument): a call of another kind only counts.
        if (kind is not (1 or 2)) return false;
        // Its upper body turns (command 16): a glance back of 1280 for its first 13 frames, or right, left, then ahead.
        m.Glance = kind == 2 ? ticks < 11 ? 0 : 1280 : ticks >= 53 ? 800 : ticks >= 18 ? -800 : 0;
        m.Order = Order.Stand;
        m.Command = Body.Stand;
        return ticks >= 2;
    }

    /// <summary>
    /// Flees (BATTLE.PRG 0x800db61c, mode 4): from the average of the foes it is aware of (or, for 90 frames after it
    /// last knew of one, from where they were), stepping away while facing them.
    /// </summary>
    private bool Flee(Combatant c, bool run, List<Combatant> fighters)
    {
        var m = c.Mind!;
        int sx = 0, sz = 0;
        bool any = false;
        foreach (var k in fighters)
        {
            if (k.Index >= 16 || (m.Relations[k.Index] >> 5 & 7) != 7) continue;
            int x = (int)k.Actor.Position.X & 0xFFFF, z = (int)k.Actor.Position.Z & 0xFFFF;
            (sx, sz) = any ? ((sx + x) >> 1, (sz + z) >> 1) : (x, z);
            any = true;
        }
        if (any) { m.FleeTimer = 90; m.Threat = (sx >> 7, sz >> 7); }
        else
        {
            if (m.FleeTimer != 0) m.FleeTimer--;
            if (m.FleeTimer == 0 || m.Mode != 4) return false;
        }
        var point = Centre(m.Threat, c.Actor.Position.Y);
        m.Goal = m.Threat;
        m.Run = run;
        m.FacePoint = point;
        m.GoalPoint = (m.Threat.X * 128 + 64, m.Threat.Z * 128 + 64);
        m.Heading = Toward(c, point) + 2048;
        m.Away = true;
        m.Order = Order.Away;
        SetMode(c, 4);
        return true;
    }

    /// <summary>Goes to a tile (BATTLE.PRG 0x800db5dc, mode 2): stands once within the radius of its centre, else walks for it.</summary>
    private void GoTo(Combatant c, (int X, int Z) tile, int radius)
    {
        var m = c.Mind!;
        m.Goal = tile;
        SetMode(c, 2);
        var centre = Centre(tile, _view.FloorAt(Centre(tile, 0)) ?? c.Actor.Position.Y);
        m.Order = (centre - c.Actor.Position).LengthSquared() < radius * radius ? Order.Stand : Order.Path;
    }

    /// <summary>Wanders about its home (BATTLE.PRG 0x800db5b0).</summary>
    private void Wander(Combatant c) => WanderAround(c, c.Mind!.Home, c.Mind.HomeRadius);

    /// <summary>
    /// Wanders about a tile (BATTLE.PRG 0x800db5b8 and 0x800e1eac, mode 3). One dormant stirs after 31 frames on a roll
    /// under 44 of 128, else lies on. It moves for its pace's move frames and idles (facing about) for its idle frames,
    /// each phase going round again on a roll under 44. Within a radius of 63 it wanders freely along a heading near
    /// a free cardinal direction, a new one at its odds, once it stalls 60 frames on a tile or the way ahead leaves the
    /// room; within less, it takes a random walk of radius-and-one cardinal steps from the tile (a step a frame, facing
    /// about meanwhile) and walks to its end.
    /// </summary>
    private void WanderAround(Combatant c, (int X, int Z) home, int radius)
    {
        var m = c.Mind!;
        if (m.Mode != 3) { m.WanderState = 0; m.Stall = 0; }
        SetMode(c, 3);
        m.WanderCount = (m.WanderCount + 1) & 0xFFFF;
        if (m.Dormant == 1)
        {
            if (m.WanderCount < 31) return;
            if (Roll(128) < 44) m.Dormant = 2;
            else { m.WanderCount = 0; return; }
        }
        if (m.WanderCount == m.MoveFrames) { if (Roll(128) < 44) m.WanderCount = 0; }
        else if (m.WanderCount >= m.IdleFrames + m.MoveFrames) m.WanderCount = Roll(128) < 44 ? m.MoveFrames : 0;
        m.CrossesZones = true;
        Afresh(m);
        if (m.WanderCount >= m.MoveFrames) { Face(c); return; }
        int odds = 1 << (m.Chance & 31);
        if (radius >= 63)
        {
            if (!(m.WanderState != 0 && Roll(odds) != 0 && m.Stall != 0))
            {
                m.WanderState = 1;
                m.Stall = 60;
                int first = 2 * Roll(4);
                m.WanderHeading = -1;
                for (int i = 0; i < 4; i++)
                {
                    int d = (first + 2 * i) & 7;
                    if (Free(c, d, m.Airborne)) { m.WanderHeading = (d * 512 - 256 + Roll(512)) & 0xFFFF; break; }
                }
                if (m.WanderHeading < 0) m.WanderHeading = Roll(4096);
            }
            Stalled(m);
            int ahead = (short)m.WanderHeading >> 9;
            foreach (int d in new[] { ahead, (ahead + 2) & 7 })
            {
                var next = d < 0 ? m.Tile : Step(m.Tile, d);
                if (_view.FloorAt(Centre(next, c.Actor.Position.Y)) == null) { m.Stall = 0; break; }
            }
            m.Heading = (short)m.WanderHeading;
            m.Order = Order.Away;
            return;
        }
        m.Wary = true;
        if (Roll(odds) == 0) m.WanderState = 0;
        switch (m.WanderState)
        {
            case 0:
                m.WanderHeading = -1;
                m.WalkSteps = 0;
                m.Stall = 60;
                m.WanderState = 1;
                Face(c);
                return;
            case 1:
            {
                int walked = RandomWalk(c, home, radius);
                if (walked < 0) m.WanderState = 0;
                else if (walked > 0) { m.WanderState = 2; m.Goal = m.WalkTile; }
                Face(c);
                return;
            }
            case 2:
                m.Goal = m.WalkTile;
                m.Order = m.Goal == m.Tile ? Order.Stand : Order.Path;
                Stalled(m);
                if (m.Goal == m.Tile || m.Stall == 0) m.WanderState = 0;
                return;
            default:
                m.WanderState = 0;
                Face(c);
                return;
        }
    }

    /// <summary>The wander's stall timer: 60 frames, run down while it stays on one tile.</summary>
    private static void Stalled(Mind m)
    {
        if (!m.Still) m.Stall = 60;
        else if (m.Stall != 0) m.Stall--;
    }

    /// <summary>
    /// A step of the wander's random walk (BATTLE.PRG 0x800e1bb8): from the tile (or where it got to) to a free cardinal
    /// neighbour, at random, once more at random if that turns straight back; -1 with none, 1 once past the radius.
    /// </summary>
    private int RandomWalk(Combatant c, (int X, int Z) from, int radius)
    {
        var m = c.Mind!;
        if (radius == 0) return -1;
        if (m.WalkSteps == 0) m.WalkTile = from;
        var free = new List<int>();
        for (int d = 0; d < 8; d += 2) if (Walkable(c, Step(m.WalkTile, d))) free.Add(d);
        if (free.Count == 0) return -1;
        int k = Roll(4096) % free.Count;
        if ((short)m.WanderHeading > 0 && free[k] == ((m.WanderHeading + 4) & 7)) k = Roll(4096) % free.Count;
        m.WalkTile = Step(m.WalkTile, free[k]);
        m.WalkSteps++;
        m.WanderHeading = free[k];
        return radius < m.WalkSteps ? 1 : 0;
    }

    /// <summary>
    /// Walks its patrol route (BATTLE.PRG 0x800db5fc and 0x800e23ac, mode 5). Taking up the patrol it makes for the
    /// route's point in use nearest its tile (Manhattan distance, the first of equals; 0x800db4ac) and may turn back at
    /// once. On its point it goes on to the next in use along its way; between points, once its 10 frames since the last
    /// are out, a roll of one in two to its odds turns it back (the roll is made every frame). It walks for its point,
    /// and on it faces about.
    /// No room of the disc gives a character behaviour 6 (the room actor records' word +0x10 bits 4-8 are 0, 1, 3 or 4
    /// throughout), so retail never runs this routine. Were it run, 0x800db5fc would skip the patrol whenever the a2
    /// register left by the code before it were 0 (it often is: the tick's step 19 leaves 0 there), and a route with
    /// no point in use would search forever (0x800e245c); the port's patrol walks and faces about instead. Both are
    /// beyond what any retail data reaches.
    /// </summary>
    private void Patrol(Combatant c)
    {
        var m = c.Mind!;
        int[] route = m.Route < Routes.Length ? Routes[m.Route] : Array.Empty<int>();
        static (int X, int Z) Point(int word) => (word & 31, word >> 5 & 31);
        if (m.Mode != 5)
        {
            int least = int.MaxValue;
            for (int i = 0; i < route.Length; i++)
            {
                if ((route[i] & 0x8000) == 0) continue;
                var p = Point(route[i]);
                int d = Math.Abs(p.X - m.Tile.X) + Math.Abs(p.Z - m.Tile.Z);
                if (d < least) { least = d; m.PatrolPoint = i; }
            }
            m.PatrolTimer = 0;
        }
        SetMode(c, 5);
        if (!Array.Exists(route, word => (word & 0x8000) != 0) || m.PatrolPoint >= route.Length) { Face(c); return; }
        bool advance = Point(route[m.PatrolPoint]) == m.Tile;
        if (!advance)
        {
            if (m.PatrolTimer != 0) m.PatrolTimer--;
            if (Roll(1 << m.PatrolTurns) == 0 && m.PatrolTimer == 0) { m.PatrolStep = -m.PatrolStep; advance = true; }
        }
        if (advance)
        {
            do m.PatrolPoint = (m.PatrolPoint + m.PatrolStep + 8) & 7;
            while ((route[m.PatrolPoint] & 0x8000) == 0);
            m.PatrolTimer = 10;
        }
        m.Goal = Point(route[m.PatrolPoint]);
        if (m.Goal == m.Tile) Face(c);
        else m.Order = Order.Path;
    }

    /// <summary>
    /// Steps off the tile it remembers (BATTLE.PRG 0x800db0bc): from it, to the first walkable of four neighbouring
    /// tiles, toward its goal and aside first and then back the other two in a rolled order, facing whom it failed on;
    /// with none, it forgets.
    /// </summary>
    private bool Unstick(Combatant c)
    {
        var m = c.Mind!;
        // With Guildenstern's final form's block about, no one steps off (0x800db0bc returns at once).
        if (_finalForm != null) return false;
        if (m.Memory != m.Tile) { m.Unstick = 0; return false; }
        m.CrossesZones = true;
        if (m.Unstick == 0)
        {
            int o = ((int)c.Actor.FacingToward(Centre(m.Goal, c.Actor.Position.Y)) >> 9) & 7, o2 = (o + 2) & 7;
            int facing = (int)c.Actor.Facing & 0xFFF, q = 2 * (((facing >> 10) + ((facing & 0x3FF) > 512 ? 1 : 0)) & 3);
            (m.UnstickDirections[0], m.UnstickDirections[1]) = q == o ? (o, o2) : (o2, o);
            (m.UnstickDirections[2], m.UnstickDirections[3]) = Roll(2) != 0 ? ((o + 4) & 7, (o2 + 4) & 7) : ((o2 + 4) & 7, (o + 4) & 7);
            for (int i = 0; i < 4; i++) if (Walkable(c, Step(m.Tile, m.UnstickDirections[i]))) { m.Unstick = i; break; }
            m.Unstick++;
        }
        if (m.Unstick >= 5)
        {
            m.Memory = null;
            m.Stuck = false;
            m.Unstick = 0;
            return false;
        }
        m.Goal = Step(m.Tile, m.UnstickDirections[m.Unstick - 1]);
        m.FaceTarget = m.UnstickFace;
        if (Walkable(c, m.Goal)) m.Order = Order.Path;
        else m.Unstick++;
        return true;
    }

    private static (int X, int Z) Step((int X, int Z) tile, int direction) => (tile.X + Directions[direction].X, tile.Z + Directions[direction].Z);

    /// <summary>
    /// A move ended (BATTLE.PRG's action end): an enemy that made a planned move remembers its target's tile
    /// (0x800deb10, <see cref="Remember"/>) and faces it as it steps off; everyone heeds the move and plans anew
    /// (<see cref="Replan"/>); and its maker lets its token go (0x800d82a8).
    /// </summary>
    private void Ended()
    {
        var acted = _acted;
        _acted = null;
        if (acted?.Caster is { Mind: { Executing: { } entry } m } caster && caster != Ashley)
        {
            m.UnstickFace = entry.Target.Index;
            Remember(caster);
            m.Executing = null;
        }
        Replan(acted);
        if (acted != null && acted.Caster != Ashley) ReleaseToken(acted.Caster);
    }

    /// <summary>
    /// Everyone heeds a move made (BATTLE.PRG 0x800dc810): each it was made on reacts (<see cref="Heed"/>), then each
    /// takes sides (<see cref="TakeSides"/>).
    /// </summary>
    private void React(Acted? acted)
    {
        var fighters = Fighters();
        foreach (var s in fighters) if (s.Mind != null) Heed(s, acted);
        foreach (var s in fighters) if (s.Mind != null) TakeSides(s, fighters);
    }

    /// <summary>
    /// How an actor reacts to a move made on it (BATTLE.PRG 0x800dd000), not Ashley, not one it made, nor one of effect
    /// class 3: help from Ashley may win over one that can be (two in 128-roll odds of 76); help from a foe calms one won
    /// over; it keeps seeing the maker 28 updates; harm from Ashley calms one won over; harm turns one that retaliates
    /// on its maker (Ashley only, with bit 26), or else, from one not yet a foe, ends the maker's override of it or
    /// overrides its own relation for 127 frames, turning on the maker with bit 26.
    /// </summary>
    private void Heed(Combatant s, Acted? acted)
    {
        if (acted == null || s == Ashley || acted.Target != s || acted.Caster == s || acted.Caster.Mind is not { } ca) return;
        if (_view.Content.Field.Unheeded(acted.Action)) return;
        var m = s.Mind!;
        var c = acted.Caster;
        var record = _view.Content.Field.Action(acted.Action);
        if (m.Dormant == 1) m.Dormant = 2;
        if (Harms(record, acted.Action, s, help: true))
        {
            if (c == Ashley && !m.Converted && m.Convertible) Convert(s, false);
            else if ((ca.Relations[s.Index] >> 6 & 3) == 3) Calm(s);
        }
        m.Relations[c.Index] = m.Relations[c.Index] & ~(0x1Fu << 15) | 28u << 15;
        if (!Harms(record, acted.Action, s, help: false)) return;
        // Harm from Ashley that costs hit points spoils Guildenstern's final form's swoop (0x800dd220).
        if (_finalForm is { } form && c == Ashley && s.Hp < acted.TargetHp) form.Unhurt = false;
        if (c == Ashley && m.Convertible) Calm(s);
        if (m.Retaliates && (!m.Avenges || c == Ashley)) { Antagonise(s, c); return; }
        uint own = m.Relations[c.Index];
        if ((own >> 6 & 3) == 3) return;
        uint theirs = ca.Relations[s.Index];
        if ((theirs >> 20 & 0x7F) != 0)
        {
            ca.Relations[s.Index] = theirs & ~(0x7Fu << 20) & ~0xC0u | (theirs >> 28 & 3) << 6;
            return;
        }
        m.Relations[c.Index] = own & ~(3u << 28) | 127u << 20 | (own >> 6 & 3) << 28;
        if (m.Avenges) Antagonise(s, c);
    }

    /// <summary>Turns an actor on another (BATTLE.PRG 0x800dc784): from not yet a foe, with the alarm and a voiced reaction; its unseen count cleared.</summary>
    private void Antagonise(Combatant s, Combatant c)
    {
        var m = s.Mind!;
        if ((m.Relations[c.Index] >> 6 & 3) < 3)
        {
            m.Lost = true;
            Flash(s, 6, 40);
            Voice(s, 2, 128);
        }
        m.Relations[c.Index] = m.Relations[c.Index] & 0xFFFF803Fu | 0xC0;
    }

    /// <summary>
    /// Takes sides (BATTLE.PRG 0x800dd344): one won over stands with Ashley and those of faction 3, against those hostile
    /// to her, neither to the rest; one that defends allies turns on whoever its mutual allies are foes to and it sees
    /// as neither, unless another mutual ally stands with them.
    /// </summary>
    private void TakeSides(Combatant s, List<Combatant> fighters)
    {
        var m = s.Mind!;
        if (m.Converted)
        {
            foreach (var k in fighters)
            {
                if (k.Mind is not { } oa) continue;
                int r = k == s || k == Ashley || k.Faction == 3 ? 2 : oa.Converted ? 1 : (oa.Relations[0] >> 6 & 3) == 3 ? 3 : 1;
                if (r == 3) m.Relations[k.Index] &= ~(0x7Fu << 20);
                m.Relations[k.Index] = m.Relations[k.Index] & ~0xC0u | (uint)r << 6;
            }
            return;
        }
        if (!m.Defends) return;
        bool Mutual(Combatant k) => k.Mind is { } km && (m.Relations[k.Index] >> 6 & 3) == 2 && (km.Relations[s.Index] >> 6 & 3) == 2;
        foreach (var k in fighters)
        {
            if (!Mutual(k)) continue;
            foreach (var j in fighters)
            {
                if (j.Index >= 16 || (m.Relations[j.Index] >> 6 & 3) != 1 || (m.Relations[j.Index] & 1) == 0 || (k.Mind!.Relations[j.Index] >> 6 & 3) != 3) continue;
                bool sided = fighters.Exists(q => Mutual(q) && ((q.Mind!.Relations[j.Index] >> 6 & 3) == 2 || (q.Mind.Relations[j.Index] >> 20 & 0x7F) != 0 && (q.Mind.Relations[j.Index] >> 28 & 3) == 2));
                if (!sided) Antagonise(s, j);
            }
        }
    }

    /// <summary>
    /// Wins an actor over (BATTLE.PRG 0x800dc210): unless forced, on a roll under 76 of 128, the second such; then it
    /// fights by the berserk routine, its own kept, with the won-over mark.
    /// </summary>
    private void Convert(Combatant s, bool force)
    {
        var m = s.Mind!;
        if (!force)
        {
            if ((_random.Randi() & 0x7F) >= 76) return;
            if (++m.Conversions < 2) return;
        }
        m.Converted = true;
        m.Calmed = m.Behaviour;
        m.Behaviour = Behaviour.Berserk;
        Show(s, 0x80);
    }

    /// <summary>
    /// Calms one won over (BATTLE.PRG 0x800dc19c): its count cleared; won over, the mark goes, its relations are set
    /// anew from its faction and its own routine comes back; one that does not retaliate raises the alarm.
    /// </summary>
    private void Calm(Combatant s)
    {
        var m = s.Mind!;
        m.Conversions = 0;
        if (!m.Converted) return;
        m.Converted = false;
        Show(s, 0x40);
        Relate(s);
        if (m.Calmed is { } own) { m.Behaviour = own; m.Calmed = null; }
        if (!m.Retaliates)
        {
            Flash(s, 6, 40);
            Voice(s, 2, 128);
        }
    }

    /// <summary>
    /// Plays a reaction an enemy voiced (the cue in its node's +0x0b, 0x800e48a8) as the actor update takes the command
    /// posted with it (0x800756d0, a frame after the tick that voiced it, and only with a command waiting): its body
    /// cries out from its sound bank (0x800ae4fc: 1 the alert cry, 2 the calm one), unless it is 3000 or more down.
    /// The cue stays until the next tick wipes it.
    /// </summary>
    private void Gaze(Combatant c)
    {
        var m = c.Mind!;
        var actor = c.Actor;
        if (m.Cue != 0 && actor.Position.Y < 3000) _view.PlayActorSound(actor, $"c{actor.Character:X2}_{m.Cue}_p128");
    }

    /// <summary>
    /// Guildenstern's final form's block (BATTLE.PRG's 32 bytes at [0x800f5920], made by 0x800e7660 as the boss joins,
    /// its swoop cooldown at 900). That it exists at all keeps every actor from stepping off a tile (0x800db0bc), has
    /// Ashley's harm spoil the swoop (0x800dd220) and hides the boss's plan as the planner ends (0x800dea40). Its +0x18
    /// and +0x1a (the swoop's starting x and z) are written and never read, and are not kept.
    /// </summary>
    private sealed class FinalFormBlock
    {
        /// <summary>The boss it serves (the actor whose node's +0x1a is 172 or 173).</summary>
        public Combatant Owner = null!;
        /// <summary>The swoop's path (<see cref="Runtime.FieldContent.SwoopPath"/>).</summary>
        public (int Ahead, int Down)[] Path = System.Array.Empty<(int, int)>();
        /// <summary>Its angle about the room's centre (+0, a turn 4096) and whether it has taken it up (+0x1c); its radius squared (+4) and whether it has measured it (+3).</summary>
        public int Angle, RadiusSquared;
        public bool Orbiting, Measured;
        /// <summary>Its cruising height (+0x12, y growing downward).</summary>
        public int Cruise;
        /// <summary>The swoop: under way (+2), the step it has come to (+0xb), its heading (+0x10), and unhurt by Ashley since it began (+0xa).</summary>
        public bool Swooping, Unhurt;
        public int Step, Heading;
        /// <summary>The frames before it may ask for a swoop again (+0xc), whether it would (+0xe), and the hit-point stages it has asked at (+0x14: 0 none, 1 three quarters, 2 a quarter).</summary>
        public int Cooldown = 900, Stage;
        public bool Requested;
        /// <summary>Its plan, put aside from the swoop's asking to its end (+0x16, and its count at +0x17); null while shown.</summary>
        public List<PlanEntry>? Hidden;
        /// <summary>Its body's command is the dive (command 24 on limb slot 4's first action) until it next flies.</summary>
        public bool Dived;
    }

    private FinalFormBlock? _finalForm;
    /// <summary>The final form's orbit: its angular speed (word [0x800f5918], 16.16 angle units a frame) and its height offset (word [0x800f591c], -100 to 500, upward).</summary>
    private int _orbitSpeed, _altitudeOffset;
    /// <summary>The scenario variable the final form and its room's scenes hand the swoop over by (byte 0x80061598 + 166: 0 orbit, 1 a swoop asked for, 4 under way, 5 the dive made, 6 broken off hurt).</summary>
    private const int SwoopVariable = 166;
    /// <summary>The middle of a 31-tile room, about which the final form circles.</summary>
    private const int RoomCentre = 1984;

    /// <summary>
    /// Guildenstern's final form's routine (BATTLE.PRG 0x800e7f8c; it always gives an order, so it never wanders):
    /// with its first move due it takes a token; its swoop cooldown runs; then by the swoop variable. At 0 it shows its
    /// plan, is not hurt in a swoop, circles (<see cref="Orbit"/>), and asks for a swoop (1, its plan hidden, the
    /// cooldown 900 again) when one is wanted, due and its dive limb (slot 4) stands. At 4, set by the room's scene,
    /// it lets its token go and swoops (<see cref="Swoop"/>); at the path's end, unhurt, it dives on Ashley if the limb
    /// stands, takes its height as its cruise and shows its plan, while hurt it ends the swoop (6); either way the
    /// orbit's speed and height offset start again. Other values leave it hovering for the scenes. It gives no order
    /// while its own move is under way.
    /// </summary>
    private bool FinalForm(Combatant c)
    {
        var b = _finalForm!;
        // While its own move is under way it gives no order, as retail's tick skips an actor in an action (0x800d8ef4);
        // the port's AI otherwise runs on through an enemy's spell.
        if (_acted?.Caster == c) return true;
        if (Due(c)) AcquireToken(c);
        int v = _view.Flags[SwoopVariable];
        if (b.Cooldown > 0) b.Cooldown--;
        switch (v)
        {
            case 0:
                Reveal(c);
                b.Unhurt = false;
                Orbit(c);
                if (b.Requested && b.Cooldown == 0 && DiveReady(c))
                {
                    b.Cooldown = 900;
                    b.Requested = false;
                    b.Hidden = new List<PlanEntry>(c.Plans);
                    c.Plans.Clear();
                    v = 1;
                }
                break;
            case 4:
                b.Orbiting = false;
                ReleaseToken(c);
                if (!Swoop(c)) break;
                if (b.Unhurt)
                {
                    if (DiveReady(c)) Dive(c);
                    b.Cruise = (int)c.Actor.Position.Y;
                    Reveal(c);
                }
                else v = 6;
                _altitudeOffset = 0;
                b.Requested = false;
                _orbitSpeed = 0;
                break;
        }
        _view.Flags[SwoopVariable] = v & 0xFF;
        return true;
    }

    /// <summary>Whether its first planned move is due (BATTLE.PRG 0x800e78f4): it has one, it is ready for it, and the planner's clock is past its frame.</summary>
    private bool Due(Combatant c) => c.Plans.Count != 0 && Ready(c, c.Plans[0]) && c.Plans[0].Horizon < _planClock;

    /// <summary>Whether the final form's dive limb (slot 4, its data's +0x708) has 2 hit points or more.</summary>
    private static bool DiveReady(Combatant c) => c.Limbs.Count > 4 && c.Limbs[4].Hp >= 2;

    /// <summary>Puts the final form's hidden plan back (its +0x470 from the block's +0x17, the block's +0x16 cleared).</summary>
    private void Reveal(Combatant c)
    {
        var b = _finalForm!;
        if (b.Hidden == null) return;
        c.Plans.Clear();
        c.Plans.AddRange(b.Hidden);
        b.Hidden = null;
    }

    /// <summary>
    /// The final form circles the room (BATTLE.PRG 0x800e7960): from its angle about the centre taken up as it begins
    /// (with its height as its cruise) and its distance, the radius comes in to 1024 at 32 a frame and the cruise to
    /// 160 at 12 (at once within 16). With its move due and its telegraph done it makes for Ashley's bearing from the
    /// centre, else the far side. Its angular speed gains a 16.16 unit a frame toward the way it turns, an eighth of
    /// itself more past 0x280000 while its mark lies ahead, a quarter while behind, the gain reversed while the mark
    /// is behind and either it means to strike or the mark is over 222 off; it is kept within 0x820000. Without a
    /// cooldown it asks for a swoop as its hit points first fall to three quarters and to under a quarter, or, slow,
    /// striking and low enough, at a roll of 1 in 512. It faces Ashley at its point on the circle, its height
    /// offset drifting toward -80 striking with its mark ahead, toward 300 heading round, and back toward 10 when
    /// neither, by the speed over the angle left (more than four times the speed: back to none, or down by that, as
    /// BATTLE.PRG does), then flies there (command 8), never lower than 255.
    /// </summary>
    private void Orbit(Combatant c)
    {
        c.Pitched = false;
        var b = _finalForm!;
        var m = c.Mind!;
        int x = (int)c.Actor.Position.X, y = (int)c.Actor.Position.Y, z = (int)c.Actor.Position.Z;
        if (!b.Orbiting)
        {
            b.Angle = (3072 - (Angle(RoomCentre - x, RoomCentre - z) & 0xFFF)) & 0xFFF;
            b.Orbiting = true;
            b.Cruise = (short)y;
        }
        if (!b.Measured)
        {
            b.RadiusSquared = (RoomCentre - x) * (RoomCentre - x) + (RoomCentre - z) * (RoomCentre - z);
            b.Measured = true;
        }
        int radius = Root(b.RadiusSquared);
        if (radius < 1024) radius += 32;
        if (radius > 1024) radius -= 32;
        if (Math.Abs(radius - 1024) < 32) radius = 1024;
        b.RadiusSquared = radius * radius;
        b.Cruise = (short)(Math.Abs(b.Cruise - 160) < 16 ? 160 : b.Cruise < 160 ? b.Cruise + 12 : b.Cruise - 12);
        var player = Ashley!.Actor.Position;
        int pa = Angle(RoomCentre - (int)player.X, RoomCentre - (int)player.Z), ma = Angle(RoomCentre - x, RoomCentre - z);
        bool attack = Due(c) && m.Token >= 32;
        int mark = attack ? pa : pa + 2048;
        int sign = _orbitSpeed < 0 ? -1 : 1;
        bool ahead = sign * (Cos(3072 - ma) * Sin(3072 - mark) - Sin(3072 - ma) * Cos(3072 - mark)) >= 0;
        int gain = ahead ? (Math.Abs(_orbitSpeed) > 0x280000 ? _orbitSpeed / 8 + sign * 0x10000 : sign * 0x10000) : _orbitSpeed / 4 + sign * 0x10000;
        if (!ahead && (attack || Cos(ma - mark) < Cos(222))) gain = -gain;
        _orbitSpeed += gain;
        if (Math.Abs(_orbitSpeed) > 0x820000) _orbitSpeed = Math.Sign(_orbitSpeed) * 0x820000;
        if (b.Cooldown == 0)
        {
            int share = c.MaxHp == 0 ? 0 : (c.Hp << 16) / c.MaxHp;
            if (b.Stage == 0 && share <= 0xBFFF || b.Stage == 1 && share < 0x4000)
            {
                b.Stage++;
                b.Requested = true;
            }
            else if (Math.Abs(_orbitSpeed) <= 0x3FFFF && attack && _altitudeOffset < 30 && (_random.Randi() & 0x1FF) == 0) b.Requested = true;
        }
        b.Angle = (b.Angle + (_orbitSpeed >> 16)) & 0xFFFF;
        m.FaceTarget = 0;
        int goalX = (RoomCentre << 12) + radius * Cos(b.Angle), goalZ = (RoomCentre << 12) + radius * Sin(b.Angle);
        int left, toward;
        if (attack && ahead) (left, toward) = (ma - pa, -80 - _altitudeOffset);
        else if (attack || ahead) (left, toward) = (pa - (ma - 2048), 300 - _altitudeOffset);
        else (left, toward) = (Math.Abs(_orbitSpeed) >> 16, 10);
        left &= 0xFFF;
        if (left > 2048) left = 4096 - left;
        if (left == 0) (left, toward) = (1, 0);
        int speed = Math.Abs(_orbitSpeed);
        int drift = unchecked((int)((long)speed * toward)) / left;
        if (Math.Abs(drift) > 4 * speed) drift = drift < 0 ? -4 * speed : 0;
        _altitudeOffset = Math.Clamp(_altitudeOffset + (drift >> 16), -100, 500);
        int height = (b.Cruise << 12) - (_altitudeOffset << 12);
        FlyFor(c, goalX, Math.Min(height, 0xFF000), goalZ);
    }

    /// <summary>
    /// The final form's swoop (BATTLE.PRG 0x800e7698), its plan hidden throughout: begun, it is unhurt, heads for the
    /// room's centre from where it is and starts its path; each frame it flies the path's next step on from where it
    /// is (<see cref="Runtime.FieldContent.SwoopPath"/>). At the path's end it waits facing while Ashley is off the
    /// ground (and, in the port, while another move is under way, as it makes one at a time), then takes up its
    /// angle about the centre again and is done (returns true). Its body's flag 0x10 at +0x5b0 (<see cref="Combatant.Pitched"/>)
    /// is set for each call that finds the step it came to under 14 (0x800e76d4, read before a new swoop starts its
    /// steps again, so a later swoop's first call, finding the last swoop's 29, is clear), and cleared as the path ends.
    /// </summary>
    private bool Swoop(Combatant c)
    {
        var b = _finalForm!;
        c.Pitched = b.Step < 14;
        c.Plans.Clear();
        int x = (int)c.Actor.Position.X, y = (int)c.Actor.Position.Y, z = (int)c.Actor.Position.Z;
        if (!b.Swooping)
        {
            b.Unhurt = true;
            b.Heading = (3072 - (Angle(x - RoomCentre, z - RoomCentre) & 0xFFF)) & 0xFFF;
            b.Step = 0;
            b.Swooping = true;
        }
        else if (b.Step >= b.Path.Length)
        {
            if (_player is { Busy: true } || _phase != Phase.Free || _enemyCast != null || _enemyRelease != null) { Face(c); return false; }
            b.Swooping = false;
            c.Pitched = false;
            b.Angle = (3072 - (Angle(RoomCentre - x, RoomCentre - z) & 0xFFF)) & 0xFFF;
            return true;
        }
        var (ahead, down) = b.Path[b.Step];
        FlyFor(c, (x << 12) + Cos(b.Heading) * ahead, (y + down) << 12, (z << 12) + Sin(b.Heading) * ahead);
        b.Measured = false;
        b.Step++;
        return false;
    }

    /// <summary>
    /// The final form's dive at the swoop's end (BATTLE.PRG 0x800e80d0): command 24 on its own, limb slot 4's first
    /// action at Ashley (retail aims at her middle, her y less half her height). The planner's end marks it made
    /// (<see cref="Planned"/>).
    /// </summary>
    private void Dive(Combatant c)
    {
        _finalForm!.Dived = true;
        int action = c.Limbs[4].Actions[0];
        if (action != 0) Execute(c, new PlanEntry { Action = action, Slot = 4, Index = 0, Target = Ashley!, Limb = Ashley!.Struck, Weight = 128 });
    }

    /// <summary>
    /// Has an enemy fly to a point (command 8, BATTLE.PRG 0x800df574: actor code 19 with the point at +0x17c, +0x180 and
    /// +0x184 in 20.12 fixed, whole units taken toward 0).
    /// </summary>
    private void FlyFor(Combatant c, int x, int y, int z)
    {
        var m = c.Mind!;
        m.Order = Order.Fly;
        m.FlyTo = (x / 4096, y / 4096, z / 4096);
        if (_finalForm?.Owner == c) _finalForm.Dived = false;
    }

    /// <summary>
    /// The planner's end with the final form's block about (BATTLE.PRG 0x800dea40): with the swoop under way (4) and
    /// the planned enemy's command the dive, the dive is made (5); and the final form's new plan stays hidden while
    /// it is set aside.
    /// </summary>
    private void Planned(Combatant enemy)
    {
        if (_finalForm is not { } b) return;
        if (_view.Flags[SwoopVariable] == 4 && b.Dived && b.Owner == enemy) _view.Flags[SwoopVariable] = 5;
        if (b.Hidden == null || b.Owner != enemy) return;
        b.Hidden = new List<PlanEntry>(enemy.Plans);
        enemy.Plans.Clear();
    }

}
