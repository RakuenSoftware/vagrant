// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GDictionary = Godot.Collections.Dictionary;
using Wol.Games.Vagrant.Installation;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// Real-time battle in the field, on the 30 Hz clock in map units, between Ashley and the room's hostile characters.
/// <list type="bullet">
/// <item>Circle draws her weapon and Cross sheathes it. With it drawn she moves by her battle set.</item>
/// <item>Circle with the weapon drawn opens the attack sphere: an ellipsoid of the weapon's range plus her reach
/// (32 units a step) round her. Time stops while it is open. Left and right choose among the limbs inside it, each
/// shown with its chance to hit; Circle attacks and Cross closes the sphere.</item>
/// <item>An attack rolls to hit, plays the attack for the target's height (72, 73 or 74), and lands its damage
/// halfway through, by the retail rules below.</item>
/// <item>The target flinches from the front or the back. At no hit points it plays its death and stays where it
/// fell, held in its last frame, out of the battle.</item>
/// </list>
/// RISK rises by the blade's cost with each attack and falls over time, faster with the weapon sheathed; hit and
/// magic points come back at the same pace.
/// Hostile characters with their weapons drawn fight by BATTLE.PRG's enemy AI (FieldEnemies.cs): they perceive,
/// plan, take turns to telegraph and act, and move as it has them.
/// </summary>
public sealed partial class FieldBattle
{
    /// <summary>Animations by slot: draw, sheathe, attacks low, level and high, flinch front/back, Ashley's flinches, death.</summary>
    public const int Draw = 9, Sheathe = 10, AttackLow = 72, Attack = 73, AttackHigh = 74, FlinchFront = 64, FlinchBack = 66, AshleyFlinchFront = 68, AshleyFlinchBack = 69, Death = 70;
    /// <summary>
    /// Status effects by effect id: lowering strength, intelligence and agility (1 to 3), slowing (5), raising them
    /// (6 to 8), quickening (10), silence (11), regeneration (13), paralysis (15), poison (16), numbness (17) and curse
    /// (18). Each sets a status bit (<see cref="StatusBit"/>), which lasts by its group's timer (<see cref="Combatant.SetStatus"/>).
    /// </summary>
    public const int Degenerate = 1, Psychodrain = 2, Leadbones = 3, Slow = 5, Herakles = 6, Enlighten = 7, Invigorate = 8,
        Quicken = 10, Silence = 11, Regen = 13, Paralysis = 15, Poison = 16, Numbness = 17, Curse = 18;


    /// <summary>Equipment as battle reads it: strength and agility, DP, and type, class and affinity values.</summary>
    public sealed class Gear
    {
        public int Id, Strength, Intelligence, Agility, Dp, MaxDp, Pp, MaxPp;
        public int[] Types = new int[4], Classes = new int[8], Affinities = new int[8];
        /// <summary>The inventory record it was read from, which wear and PP are written back to.</summary>
        public GDictionary? Record;
        /// <summary>The special effects of the gems set in it (a gem record's byte 4).</summary>
        public readonly HashSet<int> Effects = new();

        public bool Has(int effect) => Effects.Contains(effect);

        /// <summary>Wears it down by <paramref name="amount"/> DP, to no less than none.</summary>
        public void Wear(int amount)
        {
            if (MaxDp <= 0 || amount <= 0) return;
            Dp = Math.Max(0, Dp - amount);
            if (Record != null) Record["dp"] = Dp;
        }

        /// <summary>Restores <paramref name="amount"/> DP, to no more than its most.</summary>
        public void Mend(int amount)
        {
            if (MaxDp <= 0 || amount <= 0) return;
            Dp = Math.Min(MaxDp, Dp + amount);
            if (Record != null) Record["dp"] = Dp;
        }

        /// <summary>Spends <paramref name="amount"/> of its PP, to no less than none.</summary>
        public void Lose(int amount)
        {
            if (amount <= 0) return;
            Pp = Math.Max(0, Pp - amount);
            if (Record != null) Record["pp"] = Pp;
        }

        /// <summary>Adds <paramref name="amount"/> PP, to no more than its most.</summary>
        public void Gain(int amount)
        {
            if (MaxPp <= 0 || amount <= 0) return;
            Pp = Math.Min(MaxPp, Pp + amount);
            if (Record != null) Record["pp"] = Pp;
        }

        public static Gear From(GDictionary record)
        {
            int[] Ints(string key, int count) { var a = record.GetValueOrDefault(key, new Godot.Collections.Array()).AsGodotArray(); var v = new int[count]; for (int i = 0; i < count && i < a.Count; i++) v[i] = a[i].AsInt32(); return v; }
            return new Gear
            {
                Id = record.GetValueOrDefault("id", 0).AsInt32(), Strength = record.GetValueOrDefault("strength", 0).AsInt32(), Intelligence = record.GetValueOrDefault("intelligence", 0).AsInt32(), Agility = record.GetValueOrDefault("agility", 0).AsInt32(),
                Dp = record.GetValueOrDefault("dp", 0).AsInt32(), MaxDp = record.GetValueOrDefault("max_dp", 0).AsInt32(),
                Pp = record.GetValueOrDefault("pp", 0).AsInt32(), MaxPp = record.GetValueOrDefault("max_pp", 0).AsInt32(),
                Record = record,
                Types = Ints("types", 4), Classes = Ints("classes", 8), Affinities = Ints("affinities", 8),
            };
        }

        /// <summary>Worn-down DP as a percentage of the most.</summary>
        public int Worn => MaxDp > 0 ? (MaxDp - Dp) * 100 / MaxDp : 0;

        /// <summary>
        /// A class or affinity value learnt with use (BATTLE.PRG 0x80080a9c for classes, 0x80080534 and 0x800807e8 for
        /// affinities), on the piece's own record (its bytes at 0x20 and 0x28): one below 100 rises by 1 when a roll
        /// of 0 to 255 is no more than 70 less a quarter of it and 100; only then each of <paramref name="lowered"/>
        /// above -100 falls by 1 when a roll is no more than a quarter of it and 100, and 20.
        /// </summary>
        /// <remarks><paramref name="told"/> hears each change, by its message index (a class, or an affinity and 6) and whether it rose.</remarks>
        public void Drift(bool affinity, int raised, int[] lowered, Func<int, int> random, Action<int, bool>? told = null)
        {
            var values = affinity ? Affinities : Classes;
            var own = Record?.GetValueOrDefault(affinity ? "affinities" : "classes", new Godot.Collections.Array()).AsGodotArray();
            int Base(int i) => own != null && i < own.Count ? own[i].AsInt32() : values[i];
            void Move(int i, int by)
            {
                if (own != null && i < own.Count) own[i] = Base(i) + by;
                values[i] += by;
            }
            int value = Base(raised);
            if (value >= 100 || random(256) > 70 - (value + 100) / 4) return;
            Move(raised, 1);
            told?.Invoke(affinity ? raised + 6 : raised, true);
            foreach (int other in lowered)
            {
                int was = Base(other);
                if (was < -99 || random(256) > (was + 100) / 4 + 20) continue;
                Move(other, -1);
                told?.Invoke(affinity ? other + 6 : other, false);
            }
        }
    }

    /// <summary>A limb: its hit points, agility bonus, type and affinity defences, armour, damage split and bone.</summary>
    public sealed class Limb
    {
        public int Hp, MaxHp, Agility, Bone = -1;
        /// <summary>How it evades chain abilities (its data's +0x39f, the limb record's byte 3; hit function 7, 0x8007f1a4).</summary>
        public int ChainEvasion;

        /// <summary>The model part the limb is (its first bone's body part: 1 HEAD, 2 R.ARM, 3 L.ARM, 4 LEGS, 6 BODY...), or 0 when the rig does not say.</summary>
        public int Part;
        public int[] Types = new int[4], Affinities = new int[8], Split = new int[6];
        /// <summary>The four actions the limb lists (0 is none), from which an enemy plans its moves (<see cref="PlanMoves"/>).</summary>
        public int[] Actions = new int[4];
        public Gear Armour = new();
    }

    public sealed partial class Combatant
    {
        /// <summary>The limb a spell or blow on the whole character lands on: its record's (byte 0x32), else its first.</summary>
        public int DefaultLimb = -1;
        public int Struck => DefaultLimb >= 0 && DefaultLimb < Limbs.Count && Limbs[DefaultLimb].MaxHp > 0 ? DefaultLimb : Limbs.FindIndex(l => l.MaxHp > 0);

        public EventActor Actor = null!;
        /// <summary>Whether Ashley can take it as a target: a living, present enemy that is not vanished (a hopper mid-hop).</summary>
        public bool Targetable => Hostile && !Dead && Hp > 0 && !Suspended && Actor.Visible;
        /// <summary>How high its middle stands above its feet (y down), where its attack sphere is centred.</summary>
        public float Centre;
        /// <summary>Its weapon's range angle (the range record's fourth byte, its low three bits cleared), which pitches the sphere.</summary>
        public int Tilt;
        /// <summary>Its weapon's range record as it stands (its data's +0x38 to +0x3b: x, y and z radii and the angle and shape).</summary>
        public readonly int[] WeaponRange = new int[4];

        /// <summary>
        /// Takes its attack sphere from its weapon's range record (x, y, z and angle): each radius the range and its
        /// reach, 32 units a point (BATTLE.PRG 0x8006b3c0), and the angle's pitch.
        /// </summary>
        public void Reaching(Godot.Collections.Array range)
        {
            for (int i = 0; i < 4; i++) WeaponRange[i] = i < range.Count ? range[i].AsInt32() : 0;
            Range = new Vector3(range[0].AsInt32() + Reach, range[1].AsInt32() + Reach, range[2].AsInt32() + Reach) * 32;
            Tilt = range.Count > 3 ? range[3].AsInt32() & 0xF8 : 0;
        }
        /// <summary>
        /// How an enemy moves on foot: the one movement Ashley's is (BATTLE.PRG 0x800a5280), at its record's speeds, its
        /// rises above a step walls and its drops fallen; null for Ashley, whom the player moves.
        /// </summary>
        public FieldPlayer? Mover;
        public string Name = "";
        public int Category, Hp, MaxHp, Mp, MaxMp, Strength, Intelligence, Agility, WeaponStrength, WeaponAgility, Class, Risk, DamageType = 1, Cost;
        /// <summary>The kind of its weapon's cost (its data's +0x14b, the blade record's byte 0x11; 2 is RISK), which its attack pays (0x8008574c).</summary>
        public int CostKind;
        /// <summary>
        /// Its side bits (its actor's +0x1c, set by BATTLE.PRG 0x80076d50): 1 Ashley, 2 the second actor and characters of
        /// stance 0, 4 one of stance 2 or drawn, 8 one of stance 3 or sheathed (0x8007c0ac); 0x8000 once it is out of the
        /// battle (0x8007d08c). Actions take those of the sides their target kind names (0x800E81D4).
        /// </summary>
        public int Side;
        /// <summary>Its model's girth and height (the header's 0x54 and 0x56, its actor's +0x63c and +0x63e).</summary>
        public int Girth = 63, Height = 128;
        /// <summary>Whether Ashley's kill tally has counted it (its actor's +0x29, 0x8008554c).</summary>
        public bool Tallied;
        /// <summary>Whether Analyze has been cast on it (its data's +0x954 bit 17, 0x80083d7c).</summary>
        public bool Analyzed;
        public Gear Blade = new(), Shield = new(), Accessory = new();

        /// <summary>
        /// The affinity its blows strike with (BATTLE.PRG 0x80085d30): with its weapon drawn, the first of the weapon's
        /// seven affinities with the highest value above 0, else physical (0).
        /// </summary>
        public int Affinity
        {
            get
            {
                int affinity = 0, most = 0;
                if (Drawn) for (int i = 0; i < 7; i++) if (Blade.Affinities[i] > most) (affinity, most) = (i, Blade.Affinities[i]);
                return affinity;
            }
        }
        public readonly List<Limb> Limbs = new();
        /// <summary>Set by <see cref="Land"/> on a struck character; the enemy AI does not read it (it heeds a blow at the action's end, <see cref="Heed"/>).</summary>
        public bool Engaged;
        /// <summary>
        /// Its planner's state (BATTLE.PRG's AI record): its plan (+0x2f0), its faction (+0x2c, the AI word's bits 21 and
        /// 22), whether it falls back on its last resorts (+0x26) and weighs moves flagged 0x10 lightly (+0x2b), its
        /// cooldown (+0x13f, frames until it may plan sooner), its recovery (its data's +0x954, 35 a frame) and what
        /// each move adds to it, and its two slowing limbs (its data's +0x34 and +0x35).
        /// </summary>
        public readonly List<PlanEntry> Plans = new();
        public int Faction, Cooldown, Recovery, RecoveryBonus;
        public bool LastResorts, WeighsLightly;
        public (int A, int B)? SlowingLimbs;

        /// <summary>Its statuses, a bit each as BATTLE.PRG keeps them (its data's +0x948; their rules at SLUS_010.40 0x8004EECC).</summary>
        public uint Status;
        /// <summary>The statuses it cannot take (its data's +0x944, its character record's word at 0x28).</summary>
        public uint Immunity;
        /// <summary>
        /// Its six status timers in status ticks (its data's +0x94c to +0x951): one for each group of statuses
        /// (<see cref="FieldContent.StatusGroup"/>), then the turn of its poison or regeneration.
        /// </summary>
        public readonly int[] Timers = new int[6];
        /// <summary>
        /// What its Tarnish or Prostasia moved its gear's strength by, and its fusion's or guard's two affinities and
        /// amount (0x80086fa8), put back as the status goes.
        /// </summary>
        public int GearShift;
        public (int Raised, int Lowered, int By, bool Armour) AffinityShift;
        /// <summary>
        /// A thing rather than a fighter (its data's +0x957 = 0x80, as BATTLE.PRG 0x8007647C makes a crate): every
        /// action hits it (0x8007f28c), every amount is 999 (0x800838ec) and it takes no status (0x80081060).
        /// </summary>
        public bool Object => Cube;
        /// <summary>The action of Ashley's whose blow left it at no hit points, which its kill counts for (-1 none).</summary>
        public int FelledBy = -1;

        /// <summary>Restores up to <paramref name="amount"/> hit points, never past its most; returns what it restored.</summary>
        public int Heal(int amount) { int healed = Math.Clamp(amount, 0, MaxHp - Hp); Hp += healed; return healed; }

        /// <summary>Restores up to <paramref name="amount"/> magic points, never past its most; returns what it restored.</summary>
        public int Restore(int amount) { int restored = Math.Clamp(amount, 0, MaxMp - Mp); Mp += restored; return restored; }

        public bool Has(int status) => status > 0 && status < StatusBits.Length && StatusBits[status] >= 0 && (Status >> StatusBits[status] & 1) != 0;

        /// <summary>
        /// Sets a status bit (BATTLE.PRG 0x80086fa8): the timer of each group it is in is loaded (the five groups'
        /// durations are those of rules 5, 11, 19, 13 and 13), poison and regeneration start their turns (rules 14 and
        /// 17's periods), Tarnish and Prostasia move its gear's strength by 7% of its intelligence and its weapon's, and
        /// a fusion or guard moves its weapon's, or its shield's and armour's, affinities by half of it.
        /// </summary>
        public void SetStatus(int bit, FieldContent rules)
        {
            if ((Status >> bit & 1) != 0) return;
            Status |= 1u << bit;
            for (int group = 0; group < 5; group++)
                if ((rules.StatusGroup(group) >> bit & 1) != 0) Timers[group] = rules.Status(GroupDurations[group]).Duration;
            if (bit is 14 or 17) Timers[5] = rules.Status(bit).Period;
            int mind = EffectiveIntelligence + Blade.Intelligence;
            if (bit is 19 or 20)
            {
                GearShift = Math.Max(0, mind * 7 / 100) * (bit == 19 ? -1 : 1);
                Blade.Strength += GearShift;
                Shield.Strength += GearShift;
                foreach (var limb in Limbs) if (limb.Armour.Id != 0) limb.Armour.Strength += GearShift;
            }
            if (bit is >= 21 and <= 28)
            {
                // Luft, Spark, Soil and Frost (and the guards in the same order) raise air, fire, earth and water and
                // lower their opposites (0x80086ea4, 0x80086ef4).
                var (raised, lowered) = ((bit - 21) % 4) switch { 0 => (1, 3), 1 => (2, 4), 2 => (3, 1), _ => (4, 2) };
                AffinityShift = (raised, lowered, Math.Max(0, mind / 2), bit >= 25);
                Shift(AffinityShift.By);
            }
        }

        /// <summary>
        /// Clears a status bit (BATTLE.PRG 0x80086754): a group's timer stops once none of its statuses is left, and what
        /// the status moved is put back.
        /// </summary>
        public void ClearStatus(int bit, FieldContent rules)
        {
            if ((Status >> bit & 1) == 0) return;
            Status &= ~(1u << bit);
            for (int group = 0; group < 5; group++)
                if ((Status & rules.StatusGroup(group)) == 0) Timers[group] = 0;
            if ((Status & (1u << 14 | 1u << 17)) == 0) Timers[5] = 0;
            if (bit is 19 or 20)
            {
                Blade.Strength -= GearShift;
                Shield.Strength -= GearShift;
                foreach (var limb in Limbs) if (limb.Armour.Id != 0) limb.Armour.Strength -= GearShift;
                GearShift = 0;
            }
            if (bit is >= 21 and <= 28)
            {
                Shift(-AffinityShift.By);
                AffinityShift = default;
            }
        }

        // A fusion's or guard's move of affinities: the weapon's, or the shield's and every armour's.
        private void Shift(int by)
        {
            var (raised, lowered, _, armour) = AffinityShift;
            var gears = new List<Gear>();
            if (armour) { foreach (var limb in Limbs) gears.Add(limb.Armour); gears.Add(Shield); }
            else gears.Add(Blade);
            foreach (var gear in gears)
            {
                gear.Affinities[raised] += by;
                gear.Affinities[lowered] -= by;
            }
        }

        /// <summary>
        /// A stat with its statuses (0x80086fa8): lowered (or cursed, which lowers all three) to its base less a tenth
        /// of it, raised to its base and a tenth. Only one of the six raising and lowering statuses can stand at once.
        /// </summary>
        private int Changed(int value, int lower, int raise) =>
            (Status & (1u << lower | 1u << 16)) != 0 ? value - value / 10 : (Status >> raise & 1) != 0 ? value + value / 10 : value;

        public int EffectiveStrength => Changed(Strength, 5, 6);
        public int EffectiveIntelligence => Changed(Intelligence, 7, 8);
        public int EffectiveAgility => Changed(Agility, 9, 10);
        public Vector3 Range;
        public int Reach;
        public bool Hostile, Marked, Drawn, Dead;
        /// <summary>A crate taken as a target: BATTLE.PRG gives each crate an actor and a fighter record named "Cube" (0x8007647C, 1 of 1 HP).</summary>
        public bool Cube;
        /// <summary>A crate's kind: 2 and 4 take blows (+0x954 bit 16 clear, 0x8007647C), the rest none.</summary>
        public int CubeKind;
        /// <summary>What it may drop when felled (its record's drop table) and its room record's three items.</summary>
        public GDictionary? Drops;
        public Godot.Collections.Array? DropItems;
        /// <summary>Set by <see cref="Land"/> on a struck character; the enemy AI keeps its own timings (<see cref="Mind"/>).</summary>
        public int Timer;
        /// <summary>Which side of its head its balloon stands (0 right, 1 left), turned as it crosses Ashley.</summary>
        public int BalloonSide;
        /// <summary>How it moves (BATTLE.PRG 0x800e6828, by its movement profiles' flags): walking, hopping or flying.</summary>
        public Gait Gait;
        /// <summary>Its speed: units a frame walking, running or flying, or how far a hop reaches.</summary>
        public float Speed;
        /// <summary>Whether a walker is running (its run, 19) rather than walking (13); set as the AI drives it.</summary>
        public bool Runs;
        /// <summary>Frames since a hopper last hopped; it hops again every 6 (0x800e153c's count at AI +0x8c).</summary>
        public int HopCount;
        /// <summary>Where a hop is under way: 0 none, 1 vanishing, 2 reappearing; and the tile it reappears on.</summary>
        public int HopState;
        public Vector3 HopTo;
        /// <summary>A flyer's height above the floor under it, kept as it flies.</summary>
        public float Altitude;
        /// <summary>Whether it routes between the room's zones (its AI word's bit 14, AI +0x29; 0x800e5b68), or walks straight at her.</summary>
        public bool Routes;
        public string Set = "base";
        /// <summary>The animation set it fights by while its weapon is drawn.</summary>
        public string BattleSet = "battle";
        /// <summary>The weapon model in the character's hand, shown while it is drawn.</summary>
        public Node3D? Weapon;
        /// <summary>Its own weapon model's asset, which an event's weapon model (5c) may stand in for until restored.</summary>
        public string OwnWeapon = "";
        /// <summary>The shield model on its arm, shown with the weapon.</summary>
        public Node3D? ShieldModel;
        /// <summary>Taken out of the battle while an event runs (55).</summary>
        public bool Suspended;
    }

    private enum Phase { Free, Drawing, Sheathing, Sphere, Striking, Dying, Over, Casting }

    private readonly FieldView _view;
    private readonly RandomNumberGenerator _random = new();
    public readonly Dictionary<string, Combatant> Combatants = new();
    public Combatant? Ashley { get; private set; }
    public bool Drawn { get; private set; }
    private Phase _phase = Phase.Free;
    private int _frames;
    private readonly List<(Combatant Who, List<int> Parts)> _targets = new();
    private int _target, _part;
    private Combatant? _attacker, _victim;
    private int _victimLimb;
    /// <summary>The 30 Hz frame count, for RISK and recovery.</summary>
    private long _tick;
    private bool _struck;

    /// <summary>
    /// The exchange abilities answer (BATTLE.PRG's [0x800F19CC]): whether the last blow landed, the action it was and
    /// the affinity it struck with.
    /// </summary>
    private bool _landed;
    private int _blowAffinity, _blowAction = 1;
    /// <summary>The blow under way, resolved as it started, which lands at its hit.</summary>
    private Resolution? _blow;
    private int _landAt;
    private FieldPlayer? _player;
    private Func<string, bool> _pressed = _ => false;
    private readonly List<(Label3D Label, int Frames)> _numbers = new();
    // The damage numbers shown in the HUD's digits: where over the target (map units), the number and its age in frames.
    private readonly List<(Vector3 At, int Value, int Age)> _popups = new();
    private const int PopupFrames = 32;
    private Label _status = null!;

    /// <summary>Raised when an enemy dies, before it is removed, so the room can test its conditions.</summary>
    public event Action<Combatant>? Defeated;
    /// <summary>Raised when Ashley's own attack or Break Art (not a spell) fells an enemy: it counts towards her arts.</summary>
    /// <summary>An enemy Ashley fells, with the action that felled it (1 an attack, a chain ability's id, 184 and on an art).</summary>
    public event Action<Combatant, int>? Felled;
    /// <summary>Whether Ashley has fallen.</summary>
    public bool GameOver => _phase == Phase.Over;
    /// <summary>Whether time is stopped for the attack sphere.</summary>
    public bool Paused => _phase is Phase.Sphere;
    /// <summary>The character or object currently selected in the attack or spell sphere.</summary>
    public Combatant? SphereTarget => Paused && _target < _targets.Count ? _targets[_target].Who : null;
    /// <summary>Whether an action holds Ashley in place, or she is still reeling from a hit.</summary>
    public bool Busy => _phase != Phase.Free || _stagger > 0;
    /// <summary>Raised as Ashley's strike ends, when a reclaim due may open.</summary>
    public event Action? AshleyActed;
    private int _stagger;

    private BattleHud? _hud;
    /// <summary>The HUD (HP, MP, RISK, the figure and the puzzle timer), once built.</summary>
    public BattleHud? Hud => _hud;

    /// <summary>Shows or hides the HUD: the bars and the status line.</summary>
    public void ShowHud(bool shown)
    {
        if (_hud != null) _hud.Visible = shown;
        _status.Visible = shown;
    }
    /// <summary>The abilities Ashley has set to Circle, Triangle and Square: chains when she attacks, defenses when struck.</summary>
    /// <summary>The abilities on Circle, Triangle and Square (0 none): the field's own arrays, shared, so a choice made in the menu outlives the room.</summary>
    public int[] ChainSlots { get; private set; } = new int[3];
    public int[] DefenseSlots { get; private set; } = new int[3];
    private static readonly string[] SlotButtons = { "ps_circle", "ps_triangle", "ps_square" };
    /// <summary>Frames left to chain onto, or defend against, the hit that just landed; the hit's damage; the chain so far.</summary>
    private int _lastDamage, _chain;
    // The strike's timing (BATTLE.PRG 0x80078AB4): whether a press was graded yet, and the last button that linked.
    private bool _attempted;
    /// <summary>The last press's grade (0 too fast to 4 too slow; -1 none yet) and how many were graded, for the checks.</summary>
    public int LastGrade { get; private set; } = -1;
    public int Grades { get; private set; }
    /// <summary>While Ashley's strike plays, its frames from the hit (negative before it); null otherwise.</summary>
    public int? AshleyStrikeFrame => _phase == Phase.Striking && _attacker == Ashley && _attacker != null ? _landAt - _frames : null;
    /// <summary>While a blow lands on Ashley, its frames from the hit; null once a defense was attempted.</summary>
    public int? AshleyDefenseFrame => _phase == Phase.Striking && _victim == Ashley && _victim != null && !_attempted ? _landAt - _frames : null;
    private int _lastButton = -1;
    private Combatant? _lastStruck;

    public FieldBattle(FieldView view, Node hud)
    {
        _view = view;
        // The retail HUD, drawn from the installed HUD page; without it, a line of text.
        if (view.Content.Has("hud"))
            try
            {
                _hud = new BattleHud { Name = "BattleHud" };
                var hudData = view.Content.Data("hud");
                _hud.Configure(view.Content.ScreenTexture("hud_digits", true), view.Content.ScreenTexture("hud_labels", true),
                    hudData.ContainsKey("battle_mode") ? view.Content.ScreenTexture("hud_battle_mode") : null,
                    hudData.ContainsKey("emotes") ? view.Content.ScreenTexture("hud_emotes") : null,
                    view.Content.HudLettering, view.Content.ScreenTexture("hud_page_976", true));
                if (hudData.ContainsKey("chain_cue")) _hud.ChainCue = view.Content.ScreenTexture("hud_chain_cue");
                if (view.Content.Has("hud_cursor")) _hud.StatusPage = view.Content.ScreenTexture("hud_cursor", true);
                _hud.NoticePage = view.Content.ScreenTexture("hud_page_848", true);
                hud.AddChild(_hud);
            }
            catch (System.IO.IOException) { _hud = null; }
            catch (System.IO.InvalidDataException) { _hud = null; }
        _status = new Label { Position = new Vector2(16, 12), Name = "Status" };
        _status.AddThemeColorOverride("font_outline_color", Colors.Black);
        _status.AddThemeConstantOverride("outline_size", 4);
        hud.AddChild(_status);
        if (_hud != null) _status.Position = new Vector2(16, 150);
        // The enemies' map of the room is made as the room loads (BATTLE.PRG 0x80089888 calls 0x800e6f9c).
        Survey();
    }

    /// <summary>
    /// Takes a character into battle with its record's statistics: its weapon, shield and accessory, and its limbs,
    /// each aimed at through the rig bone in <paramref name="limbBones"/>.
    /// </summary>
    public Combatant Enlist(EventActor actor, GDictionary stats, bool hostile, bool marked, bool drawn, Godot.Collections.Array? limbBones = null, Godot.Collections.Array? limbParts = null, int stance = 2)
    {
        var weapon = stats["weapon"].AsGodotDictionary();
        var range = weapon["range"].AsGodotArray();
        int reach = stats["reach"].AsInt32();
        var combatant = new Combatant
        {
            Actor = actor, Name = stats.GetValueOrDefault("name", "").AsString(), Hp = stats["hp"].AsInt32(), MaxHp = stats["hp"].AsInt32(), Mp = stats["mp"].AsInt32(), MaxMp = stats["mp"].AsInt32(),
            Strength = stats["strength"].AsInt32(), Intelligence = stats.GetValueOrDefault("intelligence", 0).AsInt32(), Agility = stats["agility"].AsInt32(), Class = stats.GetValueOrDefault("class", 0).AsInt32(),
            WeaponStrength = weapon["strength"].AsInt32(), WeaponAgility = weapon["agility"].AsInt32(),
            DamageType = Math.Clamp(weapon.GetValueOrDefault("damage_type", 1).AsInt32(), 1, 3), Cost = weapon.GetValueOrDefault("cost", 0).AsInt32(),
            CostKind = weapon.GetValueOrDefault("cost_kind", 0).AsInt32(),
            Blade = Gear.From(weapon), Shield = Gear.From(stats.GetValueOrDefault("shield", new GDictionary()).AsGodotDictionary()),
            Accessory = Gear.From(stats.GetValueOrDefault("accessory", new GDictionary()).AsGodotDictionary()),
            Reach = reach,
            Hostile = hostile, Marked = marked, Drawn = drawn,
            Category = weapon.GetValueOrDefault("category", 0).AsInt32(),
            DefaultLimb = stats.GetValueOrDefault("default_limb", -1).AsInt32(),
        };
        if (actor.Id != "slot:0")
        {
            // Its movement profiles' flags set how it moves (0x800e6828): 0x30 flies, 0x08 hops, else it walks (0x42) at
            // the first walking profile's speed (0x800def8c), or stands when none walks; a flyer or hopper by the speed
            // of the profile with its flag (a hop reaching 128 units a point of it, 0x800e638c).
            var profiles = new List<(int Flags, int Speed)>();
            foreach (var profile in stats.GetValueOrDefault("moves", new Godot.Collections.Array()).AsGodotArray())
                profiles.Add((profile.AsGodotArray()[0].AsInt32(), profile.AsGodotArray()[1].AsInt32() * 9 / 10));
            int flags = 0;
            foreach (var (f, _) in profiles) flags |= f;
            combatant.Gait = (flags & 0x30) != 0 ? Gait.Fly : (flags & 0x08) != 0 ? Gait.Hop : Gait.Walk;
            // A walker closing in runs whenever a profile lets it (flags 0x84; the approach, 0x800e24ec, asks to run past
            // 10 units, 0x800e4fb0), else walks (0x42), at that profile's speed.
            foreach (int mask in combatant.Gait switch { Gait.Fly => new[] { 0x30 }, Gait.Hop => new[] { 0x08 }, _ => new[] { 0x84, 0x42 } })
            {
                foreach (var (f, speed) in profiles) if ((f & mask) != 0) { combatant.Speed = combatant.Gait == Gait.Hop ? speed * 128 : speed; combatant.Runs = mask == 0x84; break; }
                if (combatant.Speed > 0) break;
            }
            combatant.Mover = new FieldPlayer(actor, _view.FloorAt, _view.Stairs) { Crowded = Crowded, Climbs = false, Animates = false, Turns = false, WalkSpeed = combatant.Gait == Gait.Walk ? combatant.Speed : 0 };
            if (_view.FloorAt(actor.Position) is { } under) combatant.Altitude = under - actor.Position.Y;
        }
        combatant.Centre = _view.Content.Character(actor.Character).GetValueOrDefault("centre", -200).AsInt32() * Installation.CharacterConversion.ModelScale;
        // Its girth and height from its model (actor +0x63c and +0x63e), which size it in a line of fire.
        combatant.Girth = _view.Content.Character(actor.Character).GetValueOrDefault("girth", 63).AsInt32();
        combatant.Height = _view.Content.Character(actor.Character).GetValueOrDefault("height", 128).AsInt32();
        combatant.Reaching(range);
        // Ashley fights by the set for her weapon's category; others by their one battle set.
        if (actor.Id == "slot:0" && actor.HasClip($"bt{combatant.Category:X}_1")) combatant.BattleSet = $"bt{combatant.Category:X}";
        combatant.Set = drawn ? combatant.BattleSet : "base";
        // Its immunities, and with them (a negative word) immunity to magic as status bit 31 (BATTLE.PRG 0x80076cec).
        combatant.Immunity = (uint)stats.GetValueOrDefault("immunity", 0L).AsInt64();
        if ((combatant.Immunity & 0x80000000) != 0) combatant.Status = 0x80000000;
        long ai = stats.GetValueOrDefault("ai", 0L).AsInt64();
        combatant.LastResorts = stats.GetValueOrDefault("last_resorts", false).AsBool();
        combatant.WeighsLightly = stats.GetValueOrDefault("weighs_lightly", false).AsBool();
        combatant.RecoveryBonus = stats.GetValueOrDefault("recovery", 0).AsInt32();
        if (stats.GetValueOrDefault("slowing_limbs", new Godot.Collections.Array()).AsGodotArray() is { Count: 2 } slowing)
            combatant.SlowingLimbs = (slowing[0].AsInt32(), slowing[1].AsInt32());
        combatant.Routes = (ai & 1 << 14) != 0;
        var limbs = stats.GetValueOrDefault("limbs", new Godot.Collections.Array()).AsGodotArray();
        for (int i = 0; i < limbs.Count; i++)
        {
            var record = limbs[i].AsGodotDictionary();
            int hp = record["hp"].AsInt32();
            if (hp == 0) continue;
            var limb = new Limb
            {
                Hp = hp, MaxHp = hp, Agility = record["agility"].AsInt32(), Armour = Gear.From(record["armour"].AsGodotDictionary()),
                ChainEvasion = record.GetValueOrDefault("chain_evasion", 0).AsInt32(),
                Bone = limbBones != null && i < limbBones.Count ? limbBones[i].AsInt32() : -1,
                Part = limbParts != null && i < limbParts.Count ? limbParts[i].AsInt32() : 0,
            };
            for (int k = 0; k < 4; k++) limb.Types[k] = record["types"].AsGodotArray()[k].AsInt32();
            for (int k = 0; k < 8; k++) limb.Affinities[k] = record["affinities"].AsGodotArray()[k].AsInt32();
            for (int k = 0; k < 6; k++) limb.Split[k] = record["split"].AsGodotArray()[k].AsInt32();
            while (combatant.Limbs.Count < i) combatant.Limbs.Add(new Limb());
            combatant.Limbs.Add(limb);
            for (int k = 0; k < 4; k++) limb.Actions[k] = record["actions"].AsGodotArray()[k].AsInt32();
        }
        // Its side (0x80076d50): Ashley 1, the second actor 2, else by its room record's stance byte (+0x23), which the
        // room's setup turns into flags (0x80089444: 0 and 3 to 6, 1 to 0, 2 to 1) and those into a side (flags & 3: 0
        // 2, 1 4, else 8).
        combatant.Side = actor.Id == "slot:0" ? 1 : actor.Id == "slot:1" ? 2 : stance switch { 1 => 2, 2 => 4, _ => 8 };
        // Its actor index is its slot (the room's setup or the event names it; retail allots no other).
        combatant.Index = actor.Id.StartsWith("slot:", StringComparison.Ordinal) && int.TryParse(actor.Id[5..], out int slot) && slot is >= 0 and < 16 ? slot : 16;
        Combatants[actor.Id] = combatant;
        if (actor.Id == "slot:0") Ashley = combatant;
        // Only Ashley and the sides of 4 have an AI record (0x800e6178 makes one where the side has 1 or 4); drawing or
        // sheathing a weapon later moves a character between 4 and 8 but makes or frees none.
        if (combatant.Side is 1 or 4) Awaken(combatant, (uint)ai, stats);
        return combatant;
    }

    /// <summary>
    /// An event draws (1) or sheathes (0) a character's weapon, or toggles it (2). Ashley then moves by the set to
    /// match; another character with its weapon drawn is hostile, and passive with it sheathed.
    /// </summary>
    public void SetWeapon(string id, int mode)
    {
        if (!Combatants.TryGetValue(id, out var c)) return;
        bool drawn = mode == 2 ? !c.Drawn : mode == 1;
        c.Drawn = drawn;
        c.Actor.WeaponOut = null;
        c.Set = drawn ? c.BattleSet : "base";
        if (c == Ashley) Drawn = drawn;
        else if (!c.Dead) c.Hostile = drawn;
        // One on side 4 or 8 goes to 4 drawn and 8 sheathed (0x8007c124).
        if (c != Ashley && c.Side is 4 or 8) c.Side = drawn ? 4 : 8;
    }

    /// <summary>
    /// Takes up a weapon assembled from a blade and grip record: its category (and with it the battle set and arts),
    /// strength and agility (blade and grip together), damage type, RISK cost, range, wear, the grip's type values and
    /// the blade's class and affinity values.
    /// </summary>
    /// <param name="gems">The gems set in it, whose class and affinity bonuses add to the blade's.</param>
    public void EquipWeapon(Combatant c, GDictionary blade, GDictionary grip, Godot.Collections.Array? gems = null)
    {
        int Int(GDictionary d, string key) => d.GetValueOrDefault(key, 0).AsInt32();
        c.Category = Int(blade, "category");
        c.WeaponStrength = Int(blade, "strength") + Int(grip, "strength");
        c.WeaponAgility = Int(blade, "agility") + Int(grip, "agility");
        int intelligence = Int(blade, "intelligence") + Int(grip, "intelligence");
        c.DamageType = Math.Clamp(Int(blade, "damage_type"), 1, 3);
        c.Cost = Int(blade, "cost");
        c.CostKind = Int(blade, "cost_kind");
        c.Reaching(blade.GetValueOrDefault("range", new Godot.Collections.Array { 3, 2, 3 }).AsGodotArray());
        var gear = Gear.From(blade);
        gear.Types = Gear.From(grip).Types;
        gear.Strength = c.WeaponStrength;
        gear.Agility = c.WeaponAgility;
        gear.Intelligence = intelligence;
        foreach (var value in gems ?? new Godot.Collections.Array())
        {
            var record = value.AsGodotDictionary();
            var gem = Gear.From(record);
            for (int i = 0; i < 8; i++) { gear.Classes[i] += gem.Classes[i]; gear.Affinities[i] += gem.Affinities[i]; }
            if (record.GetValueOrDefault("special", 0).AsInt32() is > 0 and var special) gear.Effects.Add(special);
        }
        c.Blade = gear;
        if (c.Actor.HasClip($"bt{c.Category:X}_1")) c.BattleSet = $"bt{c.Category:X}";
        if (c.Drawn) c.Set = c.BattleSet;
    }

    /// <summary>Puts a piece of armour on a limb, or an accessory on (limb -1).</summary>
    public static void EquipArmour(Combatant c, int limb, GDictionary record)
    {
        var gear = Gear.From(record);
        if (limb == -3)
        {
            // A shield's gems add their class and affinity values and their special effects, as a weapon's do.
            foreach (var value in record.GetValueOrDefault("gems", new Godot.Collections.Array()).AsGodotArray())
            {
                var gemRecord = value.AsGodotDictionary();
                var gem = Gear.From(gemRecord);
                for (int i = 0; i < 8; i++) { gear.Classes[i] += gem.Classes[i]; gear.Affinities[i] += gem.Affinities[i]; }
                if (gemRecord.GetValueOrDefault("special", 0).AsInt32() is > 0 and var special) gear.Effects.Add(special);
            }
            c.Shield = gear;
            return;
        }
        if (limb < 0) { c.Accessory = gear; return; }
        if (limb < c.Limbs.Count) c.Limbs[limb].Armour = gear;
    }

    /// <summary>
    /// Whether Unseal breaks a seal (BATTLE.PRG 0x8007f434): a roll under 100 beats the caster's risk plus 150, times
    /// 100 over 256 (100 at most), and 20 more.
    /// </summary>
    public bool Unseals(Combatant caster) => _random.RandiRange(0, 99) < Math.Min(100, (caster.Risk + 150) * 100 / 256) + 20;

    /// <summary>Hit points as a percentage of the most, as the retail condition reads them; 0 for anyone not in battle.</summary>
    public int HitPointPercent(string id) =>
        Combatants.TryGetValue(id, out var c) && c.MaxHp > 0 ? (int)Math.Ceiling(c.Hp * 100.0 / c.MaxHp) : 0;

    /// <summary>The living hostile characters.</summary>
    public int HostileCount()
    {
        int count = 0;
        foreach (var c in Combatants.Values) if (c.Targetable) count++;
        return count;
    }

    /// <summary>
    /// Advances one frame. <paramref name="player"/> is Ashley's movement, which the battle holds still during an
    /// action and switches to her battle set while her weapon is drawn; <paramref name="pressed"/> tells whether a
    /// button was newly pressed since the last frame.
    /// </summary>
    public void Step(FieldPlayer player, Func<string, bool> pressed)
    {
        _pressed = pressed;
        if (Ashley == null) return;
        _player = player;
        StepNumbers();
        if (_stagger > 0) _stagger--;
        // Opening the sphere (Circle with the weapon drawn, not paralysed) launches Ashley's action this frame.
        Clocks(_phase == Phase.Free && Drawn && !Ashley.Has(Paralysis) && _pressed("ps_circle"));
        switch (_phase)
        {
            case Phase.Free:
                if (_emptySphere > 0 && --_emptySphere == 0) ShowRange(false);
                if (Ashley.Has(Paralysis)) break;
                if (_pressed("ps_circle"))
                {
                    if (!Drawn) Begin(Phase.Drawing, player, Draw);
                    else OpenSphere(player);
                }
                else if (Drawn && _pressed("ps_cross")) Begin(Phase.Sheathing, player, Sheathe);
                break;
            case Phase.Drawing: case Phase.Sheathing:
                if (--_frames > 0 && Ashley.Actor.Waiting != 0) break;
                Drawn = _phase == Phase.Drawing;
                Ashley.Drawn = Drawn;
                Ashley.Set = Drawn ? Ashley.BattleSet : "base";
                player.Set = Ashley.Set;
                _phase = Phase.Free;
                break;
            case Phase.Sphere:
                StepSphere(player);
                PulseHighlight();
                break;
            case Phase.Striking:
                StepStrike();
                break;
            case Phase.Dying:
                if (--_frames <= 0) _phase = Phase.Over;
                break;
            case Phase.Casting:
                StepCast();
                break;
        }
        // The enemies' bodies carry out what their AI posted at the end of the last frame, as the frame's actor update
        // does before anything else (BATTLE.PRG 0x80075554, at 0x80079e08).
        if (_phase is Phase.Free or Phase.Drawing or Phase.Sheathing && Ashley is { Dead: false })
            foreach (var c in Fighters()) if (c != Ashley && c.Mind != null && !c.Dead) Embody(c);
        // The fight is planned anew as a move ends and as the hostile come and go (BATTLE.PRG 0x800dec88).
        bool acting = _phase is Phase.Striking or Phase.Casting || _enemyCast != null || _enemyRelease != null;
        int hostile = 0;
        foreach (var c in Combatants.Values) if (c != Ashley && c.Targetable) hostile++;
        // An action's end puts the frame step back to 2 (0x8006fc90).
        if (!acting && _wasActing) { _view.FrameStep = 2; Ended(); }
        else if (!acting && hostile != _hostileCount) Replan(null);
        _wasActing = acting;
        if (!acting) _hostileCount = hostile;
        // The balloons are drawn before the AI's tick (0x8008ac78 at 0x8007a5fc), and put away while the sphere is open
        // (BATTLE.PRG 0x8009627C draws none while its flag is set).
        if (_phase != Phase.Sphere) StepBalloons();
        else _hud?.Balloons(new(), _ => null);
        if (_phase is Phase.Free or Phase.Drawing or Phase.Sheathing) { StepEnemies(); StepEnemyCast(); }
        StepEnemyRelease();
        StepRange();
        ShowWeapons();
        ShowStatus();
        StepActing();
    }

    /// <summary>A sheathed weapon (and shield) is not drawn; Ashley's shows from partway into her draw until partway into her sheathe.</summary>
    public void ShowWeapons()
    {
        foreach (var c in Combatants.Values)
        {
            // In a scene another fighter's weapon and shield show only once its script shows them (23's weapon byte:
            // the Dullahan's summoning sets 5, MAP011's Zombie Fighter rises empty-handed); in play its drawn state does.
            bool shown = c.Actor.WeaponOut ?? (c == Ashley ? _phase switch { Phase.Drawing => _frames < 40, Phase.Sheathing => _frames > 35, _ => Drawn } : c.Drawn && _view.Controlled);
            if (c.Weapon != null) c.Weapon.Visible = shown;
            if (c.ShieldModel != null) c.ShieldModel.Visible = shown;
        }
    }

    /// <summary>An event removes or replaces a body: release its battle references before its nodes are freed.</summary>
    public void RemoveActor(string id)
    {
        if (!Combatants.Remove(id, out var removed)) return;
        ReleaseToken(removed);
        _walking.Remove(removed);
        removed.Plans.Clear();
        foreach (var other in Combatants.Values) other.Plans.RemoveAll(plan => plan.Target == removed);
        _targets.RemoveAll(target => target.Who == removed);
        _target = Math.Clamp(_target, 0, Math.Max(0, _targets.Count - 1));
        removed.Weapon = removed.ShieldModel = null;
        if (_enemyCast is { } cast && (cast.Caster == removed || cast.Target == removed))
        {
            _enemyCast = null;
            if (_enemyChant != null && GodotObject.IsInstanceValid(_enemyChant)) _enemyChant.Mode = 0;
            _enemyChant = null;
        }
        if (_enemyRelease is { } release && release.Caster == removed) { _enemyRelease = null; _enemyEffect = null; }
        if (Ashley == removed) Ashley = null;
    }

    private readonly HashSet<Combatant> _walking = new();

    /// <summary>How near two fighters' centres may come: their bodies' radii (FieldPlayer.Radius) together, about.</summary>
    private const float Spacing = 96;

    /// <summary>Whether moving an actor to a position would bring it within <see cref="Spacing"/> of a living fighter it is not already closer to.</summary>
    public bool Crowded(EventActor mover, Vector3 next)
    {
        foreach (var other in Combatants.Values) if (other.Actor == mover && other.PassingThrough) return false;
        foreach (var other in Combatants.Values)
        {
            if (other.Actor == mover || other.Dead || !other.Actor.Visible) continue;
            // An enemy's body is never stopped by bodies 0 and 1 (BATTLE.PRG 0x800a8fd4 tests bodies 2 to 16 only).
            if (mover != Ashley?.Actor && other.Index is 0 or 1) continue;
            var at = other.Actor.Position;
            float after = new Vector2(next.X - at.X, next.Z - at.Z).Length(), before = new Vector2(mover.Position.X - at.X, mover.Position.Z - at.Z).Length();
            if (after < Spacing && after < before) return true;
        }
        return false;
    }

    /// <summary>
    /// Plays a battle animation by number from the fighter's set (its battle set while its weapon is drawn), or from
    /// its common set when the battle set lacks it; returns the set played from, or null when neither has it.
    /// </summary>
    private static string? Play(Combatant c, int number, int blend, bool once = false)
    {
        foreach (string set in c.Set != "base" ? new[] { c.Set, "base" } : new[] { "base" })
            if (c.Actor.Animate($"{set}_{number}", blend, once: once)) return set;
        return null;
    }

    private void Begin(Phase phase, FieldPlayer player, int animation)
    {
        player.Halt();
        _phase = phase;
        _frames = 45;
        Ashley!.Actor.Animate($"{Ashley.BattleSet}_{animation}", 5);
    }

    /// <summary>
    /// Whether a point is inside the attacker's attack sphere (BATTLE.PRG 0x800c1034, the one the sphere draws, 0x800c05ec):
    /// the ellipsoid of its range about its middle (its model's centre height above its feet, 0x800a190c), turned to its
    /// facing and pitched by its weapon's range angle (the range record's fourth byte less its low three, in 16ths of a
    /// 4096th of a turn).
    /// </summary>
    private static bool Reaches(Combatant attacker, Vector3 point)
    {
        var offset = point - Middle(attacker);
        float angle = attacker.Actor.Facing * Mathf.Tau / 4096f;
        var local = new Basis(Vector3.Right, attacker.Tilt * 16 * Mathf.Tau / 4096f) * (new Basis(Vector3.Up, -angle) * offset);
        var r = attacker.Range;
        return local.X * local.X / (r.X * r.X) + local.Y * local.Y / (r.Y * r.Y) + local.Z * local.Z / (r.Z * r.Z) < 1;
    }

    /// <summary>Whether the attacker's sphere reaches any of the target's limbs (0x80071d54), or its middle when it has none.</summary>
    internal static bool InSphere(Combatant attacker, Combatant target)
    {
        bool limbs = false;
        for (int limb = 0; limb < target.Limbs.Count; limb++)
        {
            if (target.Limbs[limb].MaxHp <= 0) continue;
            limbs = true;
            if (Reaches(attacker, Aim(target, limb))) return true;
        }
        return !limbs && Reaches(attacker, Aim(target, -1));
    }

    /// <summary>
    /// What an action does to one fighter, as BATTLE.PRG's resolver (0x80085b10) works it out before anything is shown:
    /// its 68-byte result entry (cleared by 0x80085718). Each amount has a mode, as the applier (0x800875c8) takes it:
    /// 1 or 3 (a critical) take it off, 2 adds it; for RISK only 2 acts, and lowers it.
    /// </summary>
    public sealed class Outcome
    {
        public Combatant Who = null!;
        /// <summary>The limb it falls on (+1; -1 for none).</summary>
        public int Limb;
        /// <summary>The chance it was rolled at (+2), 255 while none was (made 0 as the resolve ends, 0x80085a34).</summary>
        public int Chance = 255;
        /// <summary>How it shows (+3): 6 not reached, 4 nothing came of it (a miss), 0 harm, 5 help, 1 a critical (0x80085a34).</summary>
        public int Kind = 6;
        /// <summary>A place rather than a fighter (+0x40): effects pass it by.</summary>
        public bool Void;
        public int Hp, Mp, Risk, Strength, Intelligence, Agility, MaxHp, MaxMp, BladeDp, BladePp, Wear, ShieldPp;
        public int HpMode, MpMode, RiskMode, StrengthMode, IntelligenceMode, AgilityMode, MaxHpMode, MaxMpMode, BladeDpMode, BladePpMode, WearMode, ShieldPpMode;
        /// <summary>Mode bits 16 and 17: a status effect's showing, 1 harmful, 2 helpful (set by the status handlers, read only by <see cref="Showing"/>).</summary>
        public int ClassMode;
        /// <summary>The status bits it sets (+0x14) and clears (+0x18).</summary>
        public uint Set, Clear;
        /// <summary>
        /// The user's entry marked by Eureka, Fixate or Unlock (mode bits 26 and 27 = 2): nothing reads it but the
        /// showing (0x80085a34), which it keeps from being a miss; the effect's own script does the work.
        /// </summary>
        public bool Marked;
        /// <summary>Each limb's share of the hit points (+0x20 + 4i), and the DP its armour loses with them (+0x22 + 4i).</summary>
        public readonly int[] LimbHp = new int[6], LimbWear = new int[6];

        public bool Nothing => HpMode == 0 && MpMode == 0 && RiskMode == 0 && StrengthMode == 0 && IntelligenceMode == 0 && AgilityMode == 0 && MaxHpMode == 0
            && MaxMpMode == 0 && ClassMode == 0 && BladeDpMode == 0 && BladePpMode == 0 && WearMode == 0 && ShieldPpMode == 0 && !Marked && Set == 0 && Clear == 0;

        /// <summary>
        /// Settles how the entry shows as the resolve ends (BATTLE.PRG 0x80085a34), a place excepted: a chance left at
        /// 255 becomes 0; then, while it is still 6, an entry with no mode and no status set or cleared becomes 4 (a miss,
        /// chance 0), and otherwise the first of mode fields 0 to 8 (hit points, magic points, RISK, strength,
        /// intelligence, agility, the most hit and magic points, the status showing) that is not 0 gives 0 for 1, 5 for 2
        /// and 1 for 3.
        /// </summary>
        public void Showing()
        {
            if (Void) return;
            if (Chance == 255) Chance = 0;
            if (Kind != 6) return;
            if (Nothing) { Kind = 4; Chance = 0; return; }
            foreach (int mode in new[] { HpMode, MpMode, RiskMode, StrengthMode, IntelligenceMode, AgilityMode, MaxHpMode, MaxMpMode, ClassMode })
                if (mode != 0) { Kind = mode switch { 1 => 0, 2 => 5, _ => 1 }; return; }
        }

        /// <summary>Clears what it holds but its fighter and limb (0x80085718), as for a crate that takes nothing.</summary>
        public void Reset()
        {
            Hp = Mp = Risk = Strength = Intelligence = Agility = MaxHp = MaxMp = BladeDp = BladePp = Wear = ShieldPp = 0;
            HpMode = MpMode = RiskMode = StrengthMode = IntelligenceMode = AgilityMode = MaxHpMode = MaxMpMode = ClassMode = BladeDpMode = BladePpMode = WearMode = ShieldPpMode = 0;
            Set = Clear = 0;
            Marked = false;
            Array.Clear(LimbHp);
            Array.Clear(LimbWear);
            Chance = 0;
            Kind = 4;
        }
    }

    /// <summary>An action resolved (BATTLE.PRG's 2124-byte result): its record, its user's entry and each target's.</summary>
    public sealed class Resolution
    {
        public ActionRecord Record;
        public Combatant? Caster;
        public Outcome? Self;
        public readonly List<Outcome> Targets = new();
        /// <summary>Whether its user could pay for it (0x8008574c), which a live resolve has charged.</summary>
        public bool Paid = true;
    }

    // The resolver's passing state (BATTLE.PRG's globals): whether the last effect's prerequisite held (0x800F1A00), the
    // last hit roll (0x800F1A04), the last amount (0x800F1A08) and whether the last blow was a critical (0x800F1A0C).
    private bool _prerequisite, _hitRolled, _critical;
    private int _previous;
    // The exchange's counts ([0x800F19CC]+0 and +4): the chain links made (0 as an action starts, one more as each
    // link resolves, 0x8006fe3c), whose count less one a chain's amounts add; and the actions in it (1 as it starts,
    // one more for each link or defense, 0x8006fe38, 0x8006ff28), from 10 of which a chain link costs more.
    private int _links, _exchanges = 1;
    // The room's trap level, as a trap's reckoning reads it (0x8008e4c4).
    private int _trapLevel;

    /// <summary>SLUS_010.40 0x800427a8: a roll of 0 to <paramref name="n"/> - 1.</summary>
    private int Random(int n) => n <= 0 ? 0 : _random.RandiRange(0, n - 1);

    /// <summary>SLUS_010.40 0x80042860: the average of twelve rolls of <see cref="Random"/>, bunched about the middle.</summary>
    private int Bell(int n)
    {
        int sum = 0;
        for (int i = 0; i < 12; i++) sum += Random(n);
        return sum / 12;
    }

    /// <summary>
    /// Resolves an action (BATTLE.PRG 0x80085b10) by <paramref name="caster"/> (null for a trap) on its targets, each a
    /// fighter and limb. For each of its two effects, by its prerequisite: none (0) does nothing; 3 falls on the user,
    /// rolled; 6 to 8 on the first target, unrolled; any other on each target in turn whose prerequisite holds
    /// (0x8007fdd8), rolled (0x8007f230). A <paramref name="live"/> resolve skips an effect that misses and varies its
    /// amounts; a preview (the sphere's readout, the enemy planner's reckoning, 0x8008631c) reckons it all the same and
    /// varies nothing. Each entry then takes Slow and Ashley's crippled limbs by its limbs' hit points (0x80085008) and its
    /// showing (0x80085a34). A live resolve charges the cost (0x8008574c) at once, whatever lands.
    /// </summary>
    /// <remarks>
    /// A missile (record word 0x0C bit 0x4000) is walked to each target first (<see cref="LineOfFire"/>).
    /// </remarks>
    public Resolution Resolve(int action, Combatant? caster, List<(Combatant Who, int Limb)> targets, bool live)
    {
        var record = _view.Content.Field.Action(action);
        var r = new Resolution { Record = record, Caster = caster };
        if (caster != null) r.Self = new Outcome { Who = caster, Limb = caster.Struck };
        foreach (var (who, limb) in targets) r.Targets.Add(new Outcome { Who = who, Limb = limb });
        _previous = 0;
        _hitRolled = false;
        for (int index = 0; index < 2; index++)
        {
            var effect = record.Effects[index];
            switch (effect.Prerequisite)
            {
                case 0:
                    break;
                case 3:
                    if (r.Self == null) break;
                    _prerequisite = true;
                    if (!Hits(r, effect, index, r.Self, live) && live) break;
                    Step(r, effect, index, r.Self, live);
                    break;
                case 6 or 7 or 8:
                    if (r.Targets.Count > 0) Step(r, effect, index, r.Targets[0], live);
                    break;
                default:
                    foreach (var target in r.Targets)
                    {
                        if (live && record.Projectile && caster != null && !target.Who.Object) LineOfFire(r, target);
                        if (target.Void) continue;
                        _prerequisite = Prerequisite(effect, index, target);
                        if (!_prerequisite) { if (index == 0) target.Chance = 255; continue; }
                        if (!Hits(r, effect, index, target, live) && live) continue;
                        Step(r, effect, index, target, live);
                        // A crate of a kind that takes no blow (+0x954 bit 16) has its result cleared (0x80085f0c).
                        if (target.Who.Cube && target.Who.CubeKind is not (2 or 4)) target.Reset();
                    }
                    break;
            }
        }
        foreach (var entry in r.Self != null ? new List<Outcome>(r.Targets) { r.Self } : r.Targets)
        {
            Settle(entry);
            entry.Showing();
        }
        if (live && caster != null) r.Paid = Pay(record, caster, apply: true);
        return r;
    }

    /// <summary>
    /// Whether an effect's prerequisite holds for a target (BATTLE.PRG 0x8007fdd8, by the table at 0x800E8254): 1 to 3
    /// always (but a second effect on 1 not on one the first left untouched), 4 only on class 2 (the undead), 5 never on
    /// them, 9 on only while the target can take the status (its immunities and the statuses that keep it off), some only
    /// on one with a weapon, shield or armour; 6 to 8 are never tested here.
    /// </summary>
    private bool Prerequisite(ActionEffect effect, int index, Outcome target)
    {
        int p = effect.Prerequisite;
        if (index != 0 && p == 1 && target.Kind == 4) return false;
        var c = target.Who;
        bool weapon = c.Blade.Id != 0, armour = c.Shield.Id != 0;
        foreach (var limb in c.Limbs) armour |= limb.Armour.Id != 0;
        bool Takes(int bit)
        {
            var rule = _view.Content.Field.Status(bit);
            return (rule.Own & c.Immunity) == 0 && (rule.BlockedBy & c.Status) == 0;
        }
        return p switch
        {
            1 or 2 or 3 => true,
            4 => (c.Class & 7) == 2,
            5 => (c.Class & 7) != 2,
            9 => Takes(5), 10 => Takes(7), 11 => Takes(9), 12 => Takes(19) && (weapon || armour), 13 => Takes(4),
            14 => Takes(6), 15 => Takes(8), 16 => Takes(10), 17 => Takes(20) && (weapon || armour), 18 => Takes(11),
            19 => Takes(12), 20 => Takes(18), 21 => Takes(17), 22 => Takes(13), 23 => Takes(14), 24 => Takes(15), 25 => Takes(16),
            >= 26 and <= 29 => Takes(p - 5) && weapon,
            >= 30 and <= 33 => Takes(p - 5) && armour,
            34 => Takes(29), 35 => Takes(30) && (c.Class & 7) == 2, 36 => Takes(30) && (c.Class & 7) != 2,
            37 or 38 or 39 => true,
            _ => false,
        };
    }

    /// <summary>
    /// Rolls an effect on a target (BATTLE.PRG 0x8007f230): a crate is always hit; else its hit function's chance
    /// (<see cref="HitFunction"/>), none for a spell on one immune to magic (status bit 31), doubled on one with status
    /// bit 2 (Ashley's crippled left arm), at most 100, kept as the target's chance; and Ashley has a hidden 10 more.
    /// </summary>
    private bool Hits(Resolution r, ActionEffect effect, int index, Outcome target, bool live)
    {
        int chance = !target.Void && target.Who.Object ? 100 : HitFunction(r, effect, index, target, live);
        if (r.Record.CostKind == 1 && (target.Who.Status & 0x80000000) != 0) chance = 0;
        if (!_prerequisite) chance = 255;
        if (chance != 255)
        {
            if (!target.Void && (target.Who.Status & 4) != 0) chance *= 2;
            if (chance > 100) chance = 100;
        }
        if (index == 0 || target.Chance == 255) target.Chance = chance & 0xFF;
        int roll = Random(100);
        if (chance == 255) return _hitRolled = false;
        if (r.Caster != null && r.Caster == Ashley) chance += 10;
        return _hitRolled = roll < chance;
    }

    /// <summary>
    /// A hit function's chance (BATTLE.PRG's table at 0x800E8234, by the effect word's bits 13 to 15): 0 never; 1 only
    /// if the effect before it landed; 2 a sure 100 with the gems' 20s; 3 agility against agility (each with its gear and
    /// scaled by what RISK leaves of it, the target's with the limb's bonus) plus 100; 4 intelligence against
    /// intelligence scaled by the target's share of its hit points; 5 as 3 but 130 less the cost; 6 by the user's RISK;
    /// 7 by the limb's chain evasion. 3 to 5 vary by a bunched 5 either way on a live roll and take the gems' 20s.
    /// </summary>
    private int HitFunction(Resolution r, ActionEffect effect, int index, Outcome target, bool live)
    {
        var caster = r.Caster;
        var t = target.Who;
        int bonus = target.Limb >= 0 && target.Limb < t.Limbs.Count ? t.Limbs[target.Limb].Agility : 0;
        int Vary(int c)
        {
            if (live) c += Bell(11) - 5;
            c += Gems(r, caster) - Guards(r, effect, t);
            return c < 0 ? 0 : c == 255 ? 254 : c;
        }
        int Scaled(int value, Combatant c) => value * (100 - c.Risk) / 100;
        switch (effect.Hit)
        {
            case 0: return unchecked((int)0x8007e974);
            case 1: return _hitRolled ? 100 : 255;
            case 2: return 100 + Gems(r, caster) - Guards(r, effect, t);
            case 3: return Vary((caster != null ? Scaled(Agility(caster), caster) : 0) - Scaled(Agility(t) + bonus, t) + 100);
            case 4:
            {
                int defence = Scaled(Intelligence(t) + bonus, t);
                defence = t.MaxHp != 0 ? defence * t.Hp / t.MaxHp : 0;
                return Vary((caster != null ? Scaled(Intelligence(caster), caster) : 0) - defence);
            }
            case 5: return Vary((caster != null ? Scaled(Agility(caster), caster) : 0) - Scaled(Agility(t) + bonus, t) + 130 - r.Record.Cost);
            case 6: { int c = ((caster?.Risk ?? 0) + 150) * 100 / 256; return c == 255 ? 254 : c; }
            default:
            {
                int evasion = target.Limb >= 0 && target.Limb < t.Limbs.Count ? t.Limbs[target.Limb].ChainEvasion : 0;
                int c = (255 - evasion) * 100 / 255;
                return c == 255 ? 254 : c;
            }
        }
    }

    /// <summary>
    /// The user's weapon gems' 20 to hit (BATTLE.PRG 0x8007e5e0, with its weapon drawn): effect 2 for an action that
    /// costs magic points, effect 1 for any other.
    /// </summary>
    private static int Gems(Resolution r, Combatant? caster) =>
        caster != null && caster.Drawn && caster.Blade.Has(r.Record.CostKind == 1 ? 2 : 1) ? 20 : 0;

    /// <summary>
    /// The target's shield gems' 20s against being hit (BATTLE.PRG 0x8007e6a0, with its weapon drawn): effect 6 against
    /// an action that costs magic points, effect 5 against any other but the defense abilities (40 to 53) and items
    /// (224 on); and the gem that guards against the effect's status (the switch at 0x80068A48: Degenerate 11,
    /// Psychodrain 12, Leadbones 13, slowing 14, Silence 16, paralysis 18, poison 19, numbness 21).
    /// </summary>
    private static int Guards(Resolution r, ActionEffect effect, Combatant target)
    {
        if (!target.Drawn) return 0;
        int guard = 0, id = r.Record.Id;
        if (r.Record.CostKind == 1 ? target.Shield.Has(6) : target.Shield.Has(5) && (id < 40 || id is >= 54 and <= 223)) guard += 20;
        int gem = effect.Effect switch { 1 => 11, 2 => 12, 3 => 13, 5 => 14, 11 => 16, 15 => 18, 16 => 19, 17 => 21, _ => 0 };
        if (gem != 0 && target.Shield.Has(gem)) guard += 20;
        return guard;
    }

    /// <summary>A fighter's agility with what it wears: accessory, every limb's armour, and its weapon and shield while drawn.</summary>
    private static int Agility(Combatant c)
    {
        int agility = c.EffectiveAgility + c.Accessory.Agility;
        foreach (var limb in c.Limbs) agility += limb.Armour.Agility;
        if (c.Drawn) agility += c.Blade.Agility + c.Shield.Agility;
        return agility;
    }

    /// <summary>A fighter's intelligence with what it wears, as hit function 4 sums it (0x8007eca8).</summary>
    private static int Intelligence(Combatant c)
    {
        int intelligence = c.EffectiveIntelligence + c.Accessory.Intelligence;
        foreach (var limb in c.Limbs) intelligence += limb.Armour.Intelligence;
        if (c.Drawn) intelligence += c.Blade.Intelligence + c.Shield.Intelligence;
        return intelligence;
    }

    /// <summary>
    /// One effect on one entry (BATTLE.PRG 0x80085390): a crate takes 999 hit points; a spell (an action that costs
    /// magic points) on one with Magic Ward or immune to magic (status bits 18 and 31) does nothing, and any spell
    /// that reaches a target takes its Silence, regeneration and Magic Ward away (0x800854f4); anything else is its
    /// effect's handler (<see cref="Handle"/>).
    /// </summary>
    private void Step(Resolution r, ActionEffect effect, int index, Outcome target, bool live)
    {
        var who = target.Who;
        if (!target.Void && who.Object) { target.HpMode = 1; target.Hp += 999; }
        else if (r.Record.CostKind != 1) Handle(r, effect, index, target, live);
        else
        {
            if (!target.Void && (who.Status & 0x80040000) != 0) _previous = 0;
            else Handle(r, effect, index, target, live);
            if (!target.Void) target.Clear |= who.Status & 0x61000;
        }
        // Ashley's kill tally (0x8008554c): her attack, chain ability or art (costs 2, 3 and 6) whose amount reaches a
        // fighter's hit points counts once for that fighter towards her weapon's arts.
        if (live && r.Caster == Ashley && r.Self != null && !target.Void && !who.Object && who != Ashley && r.Record.CostKind is 2 or 3 or 6
            && target.Hp >= who.Hp && !who.Tallied)
        {
            who.Tallied = true;
            _view.CountKill();
        }
    }

    /// <summary>
    /// Sets a status on an entry (BATTLE.PRG 0x80081020) unless the target is a crate or a status it has keeps it off:
    /// the bit, if the target is not immune and has it not already (or the status clears itself, and so starts again),
    /// and the statuses it clears.
    /// </summary>
    private bool Afflict(Outcome target, int bit)
    {
        var c = target.Who;
        var rule = _view.Content.Field.Status(bit);
        if (c.Object || (c.Status & rule.BlockedBy) != 0) return false;
        uint set = rule.Own & ~c.Immunity & (~c.Status | rule.Clears), clear = rule.Clears & c.Status;
        target.Set |= set;
        target.Clear |= clear;
        return (set | clear) != 0;
    }

    /// <summary>Clears a status on an entry (BATTLE.PRG 0x800810cc), if the target has it; whether it did.</summary>
    private bool Cure(Outcome target, int bit)
    {
        if (target.Who.Object) return false;
        uint clear = _view.Content.Field.Status(bit).Own & target.Who.Status;
        target.Clear |= clear;
        return clear != 0;
    }

    /// <summary>
    /// An effect's handler (BATTLE.PRG's table at 0x800E8378): 1 to 26 set a status (<see cref="StatusBits"/>); 27 to 30
    /// cure paralysis, poison, curse and numbness; 31 dispels statuses 5 to 12 and 17 to 28; 32 cures paralysis, poison
    /// and numbness; 34 cures status 30 and takes every hit point; 35 wears the struck limb's armour and the shield by
    /// twice the damage; 37 and 38 clear the room's traps; 40 to 49 heal on a condition of the hit taken; 50 to 57 raise
    /// or lower a stat for good by 1 to 4; 58 is damage, 59 takes magic points, 60 and 61 drain the weapon's or shield's
    /// PP, 62 and 63 restore hit or magic points, 64 and 65 add PP or DP to the weapon, 66 lowers RISK.
    /// </summary>
    /// <remarks>
    /// Effects 33 (Eureka), 36 (Fixate) and 39 (Unlock) only mark the user's entry (<see cref="Outcome.Marked"/>); their
    /// work is done by their effect's script on the place the request names (0x8006cdd8 marks a trap seen, 0x8006ce70
    /// breaks a seal), here by the room (<see cref="FieldView.Cast"/>). Analyze (14) marks its target analysed (its data's
    /// +0x954 bit 17); no reader of that bit is in BATTLE.PRG.
    /// </remarks>
    private void Handle(Resolution r, ActionEffect effect, int index, Outcome t, bool live)
    {
        var who = t.Who;
        switch (effect.Effect)
        {
            case >= 1 and <= 26:
                // A status set or cleared marks the entry harmful (1) or helpful (2) in its mode bits 16 and 17 (each
                // handler's own constant): lowering, slowing, silence, paralysis, poison, numbness and curse harm;
                // raising, quickening and regeneration help; the ward, Analyze and the fusions and guards neither.
                if (Afflict(t, StatusBits[effect.Effect]) && effect.Effect is not (12 or 14 or >= 19)) t.ClassMode = effect.Effect is >= 6 and <= 10 or 13 ? 2 : 1;
                if (effect.Effect == 14 && live) who.Analyzed = true;
                break;
            case 33 or 36 or 39:
                if (r.Self != null) r.Self.Marked = true;
                break;
            case >= 27 and <= 30:
                if (Cure(t, effect.Effect switch { 27 => 13, 28 => 14, 29 => 16, _ => 15 })) t.ClassMode = 2;
                break;
            case 31: t.Clear |= who.Status & 0x1ffe1fe0; break;
            case 32:
                t.Clear |= who.Status & 0xe000;
                if ((who.Status & 0xe000) != 0) t.ClassMode = 2;
                break;
            case 34:
                Cure(t, 30);
                t.HpMode = 1;
                t.Hp = who.Hp;
                break;
            case 35:
                t.Wear = 2 * t.Hp;
                if (t.Limb is >= 0 and < 6) t.LimbWear[t.Limb] = 2 * t.Hp;
                t.WearMode = 1;
                break;
            case 37: if (live) _view.Flags[64] = 1; break;
            case 38: if (live) _view.Flags[32] = 1; break;
            case >= 40 and <= 49:
            {
                var taken = _view.Content.Field.Action(_blowAction);
                bool heals = effect.Effect switch
                {
                    40 => taken.CostKind == 1,
                    41 => taken.CostKind != 1,
                    49 => _landed,
                    _ => _blowAffinity == effect.Effect - 42,
                };
                if (!heals) break;
                int amount = Amount(r, effect, index, t, live);
                t.HpMode = 2;
                t.Hp += amount;
                if (!t.Void) HealLimbs(t, amount);
                break;
            }
            case >= 50 and <= 57:
            {
                int amount = Random(4) + 1, mode = effect.Effect <= 54 ? 2 : 1;
                switch ((effect.Effect - 50) % 5)
                {
                    case 0: t.Strength += amount; t.StrengthMode = mode; break;
                    case 1: t.Intelligence += amount; t.IntelligenceMode = mode; break;
                    case 2: t.Agility += amount; t.AgilityMode = mode; break;
                    case 3: t.MaxHp += amount; t.MaxHpMode = mode; break;
                    default: t.MaxMp += amount; t.MaxMpMode = mode; break;
                }
                break;
            }
            case 58:
            {
                _critical = false;
                int amount = Amount(r, effect, index, t, live);
                t.Hp += amount;
                t.HpMode = _critical ? 3 : 1;
                if (t.Void) break;
                var shares = Split(r.Record, t, amount);
                if (!who.Object) t.Clear |= who.Status & 0x8000;
                if (live) Wear(r.Record, r.Caster, who, amount, shares);
                break;
            }
            case 59:
            {
                int amount = Amount(r, effect, index, t, live);
                if (who.Mp < amount) { amount = who.Mp; _previous = who.Mp; }
                t.MpMode = 1;
                t.Mp += amount;
                break;
            }
            case 60:
                if (who.Blade.Id == 0) break;
                t.BladePp += who.Blade.Pp;
                t.BladePpMode = 1;
                break;
            case 61:
                if (!_landed || who.Shield.Id == 0) break;
                t.ShieldPp += who.Shield.Pp;
                t.ShieldPpMode = 1;
                break;
            case 62:
            {
                int amount = Amount(r, effect, index, t, live);
                t.HpMode = 2;
                t.Hp += amount;
                if (!t.Void) HealLimbs(t, amount);
                break;
            }
            case 63:
                t.Mp += Amount(r, effect, index, t, live);
                t.MpMode = 2;
                break;
            case 64:
                t.BladePp += Amount(r, effect, index, t, live);
                t.BladePpMode = 2;
                break;
            case 65:
                t.BladeDp += Amount(r, effect, index, t, live);
                t.BladeDpMode = 2;
                break;
            case 66:
            {
                int amount = Amount(r, effect, index, t, live);
                if (amount == 0) break;
                t.Risk += amount;
                t.RiskMode = 2;
                break;
            }
        }
    }

    /// <summary>
    /// Shares damage out over the target's limbs (BATTLE.PRG 0x80080000) by the action's target kind (the table at
    /// 0x80068A90): kinds 0 to 4 evenly over the limbs it has (what does not divide is lost); 5, 7 and 14 by the struck
    /// limb's split; 6, 8, 11 and 12 all on the struck limb; 9, 10 and 13 on none. Returns the shares.
    /// </summary>
    private static int[] Split(ActionRecord record, Outcome t, int amount)
    {
        var shares = new int[6];
        var c = t.Who;
        int limb = t.Limb;
        switch (record.TargetKind)
        {
            case <= 4:
            {
                int count = 0;
                foreach (var l in c.Limbs) if (l.MaxHp != 0) count++;
                if (count == 0) break;
                for (int i = 0; i < 6 && i < c.Limbs.Count; i++) if (c.Limbs[i].MaxHp != 0) shares[i] = amount / count;
                break;
            }
            case 5 or 7 or 14:
                if (limb >= 0 && limb < c.Limbs.Count) for (int i = 0; i < 6; i++) shares[i] = amount * c.Limbs[limb].Split[i] / 100;
                break;
            case 6 or 8 or 11 or 12:
                if (limb is >= 0 and < 6) shares[limb] = amount;
                break;
        }
        for (int i = 0; i < 6; i++) t.LimbHp[i] += shares[i];
        return shares;
    }

    /// <summary>
    /// Shares healing out over the target's limbs (BATTLE.PRG 0x800801e0): each by what it lacks of all the limbs lack,
    /// the share twice over (its halfword shifted up one).
    /// </summary>
    private static void HealLimbs(Outcome t, int amount)
    {
        var limbs = t.Who.Limbs;
        int total = 0;
        foreach (var l in limbs) total += l.MaxHp - l.Hp;
        if (total <= 0) return;
        for (int i = 0; i < 6 && i < limbs.Count; i++) t.LimbHp[i] += (short)(amount * (limbs[i].MaxHp - limbs[i].Hp) / total) * 2;
    }

    /// <summary>
    /// A real hit's wear (BATTLE.PRG 0x800802c4 and 0x800803a4), as it is resolved: the user's weapon (but for a spell)
    /// loses the damage in DP and gains a tenth of it in PP (5% more with a gem of effect 9); the target's shield, while
    /// drawn, the same (5% less PP with that gem); and each limb's armour loses its share.
    /// </summary>
    private static void Wear(ActionRecord record, Combatant? caster, Combatant target, int damage, int[] shares)
    {
        if (record.CostKind != 1 && caster != null)
        {
            caster.Blade.Wear(damage);
            caster.Blade.Gain(damage / 10 * (caster.Blade.Has(9) ? 105 : 100) / 100);
        }
        if (target.Drawn)
        {
            target.Shield.Wear(damage);
            target.Shield.Gain(damage / 10 * (target.Shield.Has(9) ? 95 : 100) / 100);
        }
        for (int i = 0; i < 6 && i < target.Limbs.Count; i++) target.Limbs[i].Armour.Wear(shares[i]);
    }

    /// <summary>
    /// An effect's amount (BATTLE.PRG 0x800838ec): 999 on a crate, else its reckoning's (the table at 0x800E830C, by the
    /// effect word's bits 16 to 21), as a 16-bit value, kept as the last amount. The reckonings: 0 none (the routine
    /// returns its own address, 4400); 1 the last amount; 2 and 25 a blow (<see cref="Physical"/>); 3 a spell's
    /// (<see cref="Magic"/>); 12 and 14 a trap's (<see cref="Trapped"/>); 13 healing (<see cref="Mending"/>); 15 five
    /// times the factor, give or take a bunched 5; 16 and 17 the hit and magic points the target lacks; 26 its RISK. The
    /// rest answer a hit just dealt or taken (the exchange at [0x800F19CC]): 4, 6 and 18 a tenth per factor of the
    /// damage dealt, 5 and 11 of the hit or magic points the user lacks, 7 a hundredth of the target's hit points, 8 the
    /// weapon's PP, each with the chain's links so far; 9, 10, 19, 20 and 22 a tenth per factor of the hit taken, if it
    /// landed (9 and 19 only for a spell, 10 and 20 not); 21 a tenth per factor of the spell's cost; 23 the shield's PP;
    /// 24 a tenth per factor of the last amount.
    /// </summary>
    /// <remarks>The links added are the exchange's count less one ([0x800F19CC]+0): the chain's links before this one, -1 outside a chain.</remarks>
    private int Amount(Resolution r, ActionEffect effect, int index, Outcome t, bool live)
    {
        var c = r.Caster;
        var who = t.Who;
        int f = effect.Factor, links = _links - 1;
        var taken = _view.Content.Field.Action(_blowAction);
        int amount = !t.Void && who.Object ? 999 : effect.Reckoning switch
        {
            0 => 0x1130,
            1 => _previous,
            2 => Physical(r, effect, t, index, live, arts: false),
            3 => Magic(r, effect, t, index, live),
            4 or 6 or 18 => _lastDamage * f / 10 + links,
            5 => c != null ? (c.MaxHp - c.Hp) * f / 10 + links : 0,
            7 => who.Hp * f / 100 + links,
            8 => (c?.Blade.Pp ?? 0) + links,
            9 or 19 => taken.CostKind == 1 && _landed ? _lastDamage * f / 10 : 0,
            10 or 20 => taken.CostKind != 1 && _landed ? _lastDamage * f / 10 : 0,
            11 => c != null ? (c.MaxMp - c.Mp) * f / 10 + links : 0,
            12 => Trapped(effect, who, t.Limb, live, cap: true),
            13 => Mending(r, effect, t, index, live),
            14 => Trapped(effect, who, t.Limb, live, cap: false),
            15 => Math.Max(0, f * 5 + (live ? Bell(11) - 5 : 0)),
            16 => who.MaxHp - who.Hp,
            17 => who.MaxMp - who.Mp,
            21 => taken.CostKind == 1 ? taken.Cost * f / 10 : 0,
            22 => _landed ? _lastDamage * f / 10 : 0,
            23 => c?.Shield.Pp ?? 0,
            24 => (int)((uint)(_previous * f) / 10),
            25 => Physical(r, effect, t, index, live, arts: true),
            26 => who.Risk,
            _ => 0,
        };
        _previous = (short)amount;
        return _previous;
    }

    /// <summary>
    /// GemScale (BATTLE.PRG 0x8007fe5c): 5% more for the user's weapon gem of effect 4 (a spell) or 3 (anything else),
    /// 5% less for the target's shield gem of effect 8 or 7, drawn or not.
    /// </summary>
    private static int GemScale(ActionRecord record, int value, Combatant? caster, Combatant target)
    {
        bool spell = record.CostKind == 1;
        int scale = 100 + (caster != null && caster.Blade.Has(spell ? 4 : 3) ? 5 : 0) - (target.Shield.Has(spell ? 8 : 7) ? 5 : 0);
        return (short)((short)value * scale / 100);
    }

    /// <summary>
    /// A blow's damage (BATTLE.PRG 0x80081148; reckonings 2 and 25). Its damage type is the effect's, else the weapon's
    /// (1 without); its affinity the effect's, else the drawn weapon's strongest (<see cref="Combatant.Affinity"/>). The
    /// attack is the user's strength with its accessory's (and its weapon's while drawn), scaled by a quarter of a term
    /// plus 100: while drawn the weapon's type, affinity and class (against the target's) values, less the percent of
    /// its DP worn away, plus its PP's share by its category (BATTLE.PRG 0x800E82F4); and the accessory's type, class and
    /// affinity values. The factor scales the attack. The defence is the target's strength with its accessory's, less
    /// 20, times the limb's type and affinity defences and the accessory's type, class (against the user's) and
    /// affinity values; plus the limb's armour's strength times its own (less a quarter of its wear); plus the drawn
    /// shield's likewise (and a quarter of its PP percent); over 100 plus half the target's RISK. The difference, by the
    /// gems (<see cref="GemScale"/>) and at least 0, takes 1 to 5 more on a live blow; an art (25) by a user with status
    /// bit 1 (Ashley's crippled right arm) is halved; and a live attack (actions 0 to 5) doubles on a critical, a roll
    /// under a tenth of RISK and 10.
    /// </summary>
    /// <remarks>The weapon's type value is one byte, its data's +0x145 (read at 0x800813c8): the grip's value for the
    /// weapon's own damage type (0x8006a334 copies grip byte 0x1c + the blade's type there), whatever type the blow
    /// strikes with. A live first effect on a fighter then drifts the gear (<see cref="WeaponDrift"/>, <see cref="GuardDrift"/>).</remarks>
    private int Physical(Resolution r, ActionEffect effect, Outcome target, int index, bool live, bool arts)
    {
        var a = r.Caster;
        var d = target.Who;
        if (a == null) return 0;
        int type = effect.Type != 0 ? effect.Type : a.DamageType;
        int affinity = effect.Affinity != 0 ? effect.Affinity - 1 : a.Affinity;
        int ac = a.Class & 7, tc = d.Class & 7;
        int strength = a.EffectiveStrength + a.Accessory.Strength, term = 0;
        if (a.Drawn)
        {
            var blade = a.Blade;
            strength += blade.Strength;
            term = blade.Types[a.DamageType] + blade.Affinities[affinity] + blade.Classes[tc] - blade.Worn;
            if (blade.MaxPp != 0) term += blade.Pp * _view.Content.Field.PhantomShare(a.Category) / blade.MaxPp;
        }
        term += a.Accessory.Types[type] + a.Accessory.Classes[tc] + a.Accessory.Affinities[affinity];
        int attack = (short)strength * (short)(term / 4 + 100) / 100;
        attack = attack * (effect.Factor * 10 + 100) / 100;
        var limb = target.Limb >= 0 && target.Limb < d.Limbs.Count ? d.Limbs[target.Limb] : new Limb();
        var armour = limb.Armour;
        int limbFactor = 100 + limb.Types[type] + limb.Affinities[affinity] + d.Accessory.Types[type] + d.Accessory.Classes[ac] + d.Accessory.Affinities[affinity];
        int armourFactor = 100 + armour.Types[type] + armour.Classes[ac] + armour.Affinities[affinity] - armour.Worn / 4;
        int shieldStrength = 0, shieldFactor = 100;
        if (d.Drawn)
        {
            var shield = d.Shield;
            shieldStrength = shield.Strength;
            shieldFactor = 100 + (shield.Types[type] & 0xFF) + shield.Affinities[affinity] + shield.Classes[ac] - shield.Worn / 4 + (shield.MaxPp != 0 ? shield.Pp * 100 / shield.MaxPp / 4 : 0);
        }
        int defence = (((short)(d.EffectiveStrength + d.Accessory.Strength) - 20) * limbFactor + shieldStrength * shieldFactor + armour.Strength * armourFactor) / (d.Risk / 2 + 100);
        int damage = GemScale(r.Record, (short)(attack - defence), a, d);
        if (damage < 0) damage = 0;
        if (live) damage += 1 + Random(5);
        if (arts && (a.Status & 2) != 0) damage = (short)damage / 2;
        if (live && r.Record.Id < 6 && Random(100) < a.Risk / 10 + 10)
        {
            damage = (damage & 0xFFFF) * 2;
            _critical = true;
        }
        if (live && index == 0 && !target.Void)
        {
            // The target's strongest defence by affinity at the struck limb (with its drawn shield's), above 0.
            int strongest = 0, most = 0;
            for (int i = 0; i < 7; i++)
            {
                int guard = limb.Affinities[i] + armour.Affinities[i] + (d.Drawn ? d.Shield.Affinities[i] : 0);
                if (guard > most) (strongest, most) = (i, guard);
            }
            WeaponDrift(r.Record, a, tc + 1, strongest + 1);
            GuardDrift(r.Record, d, ac + 1, affinity + 1, target.Limb);
        }
        return (short)damage;
    }

    /// <summary>
    /// The user's weapon learns from a real blow (BATTLE.PRG 0x80080f78), unless the action costs magic points or it has
    /// none: its class value against the target's class (<paramref name="classArg"/> less one; 0 for none) rises,
    /// the next two classes round falling (0x80080a9c); and its affinity value opposite the target's strongest defence
    /// (<paramref name="affinityArg"/> less one, or the effect's own affinity) rises, the physical value and the defence's
    /// falling (0x80080534: air and earth, fire and water, light and dark trade places; physical lowers all six).
    /// </summary>
    private void WeaponDrift(ActionRecord record, Combatant c, int classArg, int affinityArg)
    {
        if (record.CostKind == 1 || c.Blade.Id == 0) return;
        if (classArg != 0) ClassDrift(c.Blade, classArg - 1, Told(c, 240));
        int index = record.Effects[0].Affinity != 0 ? record.Effects[0].Affinity - 1 : affinityArg - 1;
        if (affinityArg == 0 || index is < 0 or > 6) return;
        int raised = new[] { 0, 3, 4, 1, 2, 6, 5 }[index];
        c.Blade.Drift(true, raised, index == 0 ? new[] { 1, 2, 3, 4, 5, 6 } : new[] { 0, index }, Random, Told(c, 240));
    }

    /// <summary>
    /// The target's guard learns from a real hit (BATTLE.PRG 0x80080c9c): one of the pieces the action's target kind
    /// reaches, picked at random (the table at 0x80068B28: kinds 1 to 4 its drawn shield and every armoured limb; 5, 7
    /// and 14 its drawn shield and the struck limb's armour; 6, 8, 11 and 12 the struck limb's armour; others none), has
    /// its class value against the user's class rise (0x80080a9c; <paramref name="classArg"/> less one, 0 for none) and
    /// its affinity value for the blow's affinity rise, the physical value and the opposite affinity falling
    /// (0x800807e8; <paramref name="affinityArg"/> less one, or the effect's own affinity).
    /// </summary>
    private void GuardDrift(ActionRecord record, Combatant c, int classArg, int affinityArg, int limb)
    {
        var pieces = new List<Gear>();
        bool shield = c.Drawn && c.Shield.Id != 0;
        bool Armoured(int l) => l >= 0 && l < c.Limbs.Count && c.Limbs[l].MaxHp != 0 && c.Limbs[l].Armour.Id != 0;
        switch (record.TargetKind)
        {
            case >= 1 and <= 4:
                if (shield) pieces.Add(c.Shield);
                for (int l = 0; l < 6; l++) if (Armoured(l)) pieces.Add(c.Limbs[l].Armour);
                break;
            case 5 or 7 or 14:
                if (shield) pieces.Add(c.Shield);
                if (Armoured(limb)) pieces.Add(c.Limbs[limb].Armour);
                break;
            case 6 or 8 or 11 or 12:
                if (Armoured(limb)) pieces.Add(c.Limbs[limb].Armour);
                break;
        }
        if (pieces.Count == 0) return;
        var piece = pieces[Random(pieces.Count)];
        var told = Told(c, piece == c.Shield ? 241 : c.Limbs.Find(l => l.Armour == piece)?.Part ?? 0);
        if (classArg != 0) ClassDrift(piece, classArg - 1, told);
        int index = record.Effects[0].Affinity != 0 ? record.Effects[0].Affinity - 1 : affinityArg - 1;
        if (affinityArg == 0 || index is < 0 or > 6) return;
        piece.Drift(true, index, index == 0 ? new[] { 1, 2, 3, 4, 5, 6 } : new[] { 0, new[] { 0, 3, 4, 1, 2, 6, 5 }[index] }, Random, told);
    }

    /// <summary>
    /// A class value learnt (BATTLE.PRG 0x80080a9c): classes 0 to 5 rise with the next two round them falling.
    /// </summary>
    /// <remarks>
    /// For classes 6 and 7 the jump table (0x80068B10, six entries) is skipped and the fallers are the caller's s1 and
    /// s2: from 0x80080f78 the blade's address (data+0x54) and the action record's, so the bytes at the wrapped sums
    /// blade+blade+0x20 and blade+action+0x20 would fall. That path is never taken: the class passed is a fighter's
    /// data+0x37 &amp; 7 (0x8008195c, 0x80081978), which only 0x80076a28 writes, from its character record's byte 3 bits
    /// 5 to 7, and every character record on the disc (each ZND's templates and INITBTL.PRG's Ashley) has class 0 to
    /// 5; a crate's record is cleared to 0 (0x800764b8). The contracts check this from the disc.
    /// </remarks>
    private void ClassDrift(Gear gear, int c, Action<int, bool>? told)
    {
        if (c is < 0 or > 5) return;
        gear.Drift(false, c, new[] { (c + 1) % 6, (c + 2) % 6 }, Random, told);
    }


    /// <summary>
    /// What tells of a change to one of Ashley's pieces (BATTLE.PRG 0x80096768, only for her own gear): a notice in the
    /// lower-left corner (<see cref="BattleHud.AddNotice"/>). <paramref name="piece"/> is retail's argument: 240 her
    /// weapon, 241 her shield, else the armoured limb's body part (its data's +0x39e), which picks the piece's sprite
    /// (1 head, 2 and 3 the arms, 4 legs, 5 and 6 the body). Each kind is told only while its OPTIONS setting is ON:
    /// WEAPON STATUS for the weapon, ARMOR STATUS for the shield and armour (bits 0x40 and 0x80 of [0x80060020],
    /// <see cref="FieldView.Options"/>).
    /// </summary>
    private Action<int, bool>? Told(Combatant owner, int piece)
    {
        if (owner != Ashley || _hud == null || (_view.Options & (piece == 240 ? 0x40u : 0x80u)) == 0) return null;
        int sprite = piece switch { 240 => 0, 241 => 1, 1 => 4, 4 => 6, 6 => 5, 2 or 3 or 5 => piece, _ => piece };
        return (index, raised) =>
        {
            var (pu, pv, pw, ph) = _view.Content.Field.NoticeSprite(false, sprite);
            var (vu, vv, vw, vh) = _view.Content.Field.NoticeSprite(true, index);
            var (r, g, b) = _view.Content.Field.NoticeColour(raised);
            _hud.AddNotice(new Rect2(pu, pv, pw, ph), new Rect2(vu, vv, vw, vh), raised, new Color(r / 128f, g / 128f, b / 128f));
        };
    }

    /// <summary>
    /// A spell's damage (BATTLE.PRG 0x800819fc; reckoning 3): the user's intelligence with its accessory's (and its
    /// weapon's while drawn), scaled by a quarter of a term plus 100 (while drawn, the weapon's affinity value, less the
    /// percent of its DP worn away, plus its percent of PP; and the accessory's type and affinity values), then by the
    /// factor; less the target's defence as for a blow but on intelligence and without classes, and with no type
    /// defences for an effect of type 0; by the gems, at least 0, and 1 to 5 more when live. The affinity is the
    /// effect's, else the drawn weapon's strongest.
    /// </summary>
    private int Magic(Resolution r, ActionEffect effect, Outcome target, int index, bool live)
    {
        var a = r.Caster;
        var d = target.Who;
        if (a == null) return 0;
        int type = effect.Type, affinity = effect.Affinity != 0 ? effect.Affinity - 1 : a.Affinity;
        int intelligence = a.EffectiveIntelligence + a.Accessory.Intelligence, term = 0;
        if (a.Drawn)
        {
            var blade = a.Blade;
            intelligence += blade.Intelligence;
            term = blade.Affinities[affinity] - blade.Worn + (blade.MaxPp != 0 ? blade.Pp * 100 / blade.MaxPp : 0);
        }
        term += a.Accessory.Types[type] + a.Accessory.Affinities[affinity];
        int attack = (short)intelligence * (short)(term / 4 + 100) / 100;
        attack = attack * (effect.Factor * 10 + 100) / 100;
        int damage = GemScale(r.Record, (short)(attack - MindDefence(d, target.Limb, type, affinity, affinity >= 0)), a, d);
        if (damage < 0) damage = 0;
        if (live) damage += 1 + Random(5);
        if (live && index == 0 && affinity != 0 && !target.Void) GuardDrift(r.Record, d, 0, affinity + 1, target.Limb);
        return (short)damage;
    }

    /// <summary>
    /// Healing (BATTLE.PRG 0x800829b8; reckoning 13), as <see cref="Magic"/> on the one healed but for these: the
    /// affinity is the effect's alone, its terms counted only when it is not physical; no 1 to 5, but a bunched 5 either
    /// way when live, and at least 0 after.
    /// </summary>
    /// <remarks>
    /// Retail takes the affinity index as the effect word's bits 29 to 31 less one (0x80082a54). For index 0 (affinity
    /// 1) 0x80082aa4 skips the store at 0x80082ac0, so the weapon's term is the halfword left at sp+30 by an earlier
    /// call; for affinity 0 the index is 255 and it reads +0x184+510. Neither is reached: 0x800829b8 is only reckoning
    /// 13 (the table at 0x800E830C, entry at 0x800E8340; no direct call), and the only effects in SLUS_010.40's action
    /// table (256 records at 0x8004B9DC, two effects each, every action id an AI, menu or trap names below 256) that
    /// name it are Heal's (actions 65 and 96), affinity 6, light. The contracts check this from the disc. The branches
    /// below for index 0 and 255 therefore never run, as in retail.
    /// </remarks>
    private int Mending(Resolution r, ActionEffect effect, Outcome target, int index, bool live)
    {
        var a = r.Caster;
        var d = target.Who;
        if (a == null) return 0;
        int type = effect.Type, affinity = effect.Affinity - 1;
        bool affine = affinity != 0;
        int Of(int[] values) => affine && affinity >= 0 ? values[affinity] : 0;
        int intelligence = a.EffectiveIntelligence + a.Accessory.Intelligence, term = 0;
        if (a.Drawn)
        {
            var blade = a.Blade;
            intelligence += blade.Intelligence;
            term = Of(blade.Affinities) - blade.Worn + (blade.MaxPp != 0 ? blade.Pp * 100 / blade.MaxPp : 0);
        }
        term += a.Accessory.Types[type] + (affinity >= 0 ? a.Accessory.Affinities[affinity] : 0);
        int amount = intelligence * (term / 4 + 100) / 100;
        amount = amount * (effect.Factor * 10 + 100) / 100;
        int result = GemScale(r.Record, amount - MindDefence(d, target.Limb, type, affinity, affine), a, d);
        if (result < 0) result = 0;
        if (live) result = Math.Max(0, result + Bell(11) - 5);
        if (live && index == 0 && affine && !target.Void) GuardDrift(r.Record, d, 0, affinity + 1, target.Limb);
        return (short)result;
    }

    /// <summary>
    /// A target's defence against a spell or healing (BATTLE.PRG 0x80081c..., 0x80082c...): its intelligence with its
    /// accessory's, less 20, times the limb's type (only for a type) and affinity defences and the accessory's type and
    /// affinity values; plus the limb's armour's intelligence times its own (less a quarter of its wear); plus, while
    /// drawn, the shield's likewise (and a quarter of its PP percent); over 100 plus half its RISK.
    /// </summary>
    private static int MindDefence(Combatant d, int limbIndex, int type, int affinity, bool affine)
    {
        var limb = limbIndex >= 0 && limbIndex < d.Limbs.Count ? d.Limbs[limbIndex] : new Limb();
        var armour = limb.Armour;
        int Of(int[] values) => affine && affinity >= 0 ? values[affinity] : 0;
        int limbFactor = 100 + (type != 0 ? limb.Types[type] : 0) + Of(limb.Affinities) + d.Accessory.Types[type] + (affinity >= 0 ? d.Accessory.Affinities[affinity] : 0);
        int armourFactor = 100 + (type != 0 ? armour.Types[type] : 0) + Of(armour.Affinities) - armour.Worn / 4;
        int shieldMind = 0, shieldFactor = 100;
        if (d.Drawn)
        {
            var shield = d.Shield;
            shieldMind = shield.Intelligence;
            shieldFactor = 100 + (type != 0 ? shield.Types[type] & 0xFF : 0) + Of(shield.Affinities) - shield.Worn / 4 + (shield.MaxPp != 0 ? shield.Pp * 100 / shield.MaxPp / 4 : 0);
        }
        return (((short)(d.EffectiveIntelligence + d.Accessory.Intelligence) - 20) * limbFactor + shieldMind * shieldFactor + armour.Intelligence * armourFactor) / (d.Risk / 2 + 100);
    }

    /// <summary>
    /// A trap's damage or a heal panel's healing (BATTLE.PRG 0x8008255c and 0x80082ff4; reckonings 12 and 14), read from
    /// the target alone: a tenth of its most hit points times the factor, plus ten for each trap level and ten more,
    /// less its defence as for a blow (strength, the limb's type and affinity defences, its armour's and its drawn
    /// shield's) but without classes, and with no affinity terms for a physical one; at least 0, a bunched 5 either way
    /// when live. A trap's never fells (<paramref name="cap"/>): at most the hit points less one.
    /// </summary>
    public int Trapped(ActionEffect effect, Combatant target, int limbIndex, bool live, bool cap)
    {
        int value = (short)(target.MaxHp / 10) * effect.Factor + (_trapLevel + 1) * 10;
        int type = effect.Type, affinity = effect.Affinity - 1;
        bool affine = affinity is > 0 and < 8;
        var limb = limbIndex >= 0 && limbIndex < target.Limbs.Count ? target.Limbs[limbIndex] : new Limb();
        var armour = limb.Armour;
        int limbFactor = 100 + (type != 0 ? limb.Types[type] : 0) + (affine ? limb.Affinities[affinity] : 0);
        int armourFactor = 100 + (type != 0 ? armour.Types[type] : 0) + (affine ? armour.Affinities[affinity] : 0) - armour.Worn / 4;
        int shieldStrength = 0, shieldFactor = 100;
        if (target.Drawn)
        {
            var shield = target.Shield;
            shieldStrength = shield.Strength;
            shieldFactor = 100 + (type != 0 ? shield.Types[type] & 0xFF : 0) + (affine ? shield.Affinities[affinity] : 0) - shield.Worn / 4 + (shield.MaxPp != 0 ? shield.Pp * 100 / shield.MaxPp / 4 : 0);
        }
        limbFactor += target.Accessory.Types[type] + (affinity is >= 0 and < 8 ? target.Accessory.Affinities[affinity] : 0);
        int defence = (((short)(target.EffectiveStrength + target.Accessory.Strength) - 20) * limbFactor + shieldStrength * shieldFactor + armour.Strength * armourFactor) / (target.Risk / 2 + 100);
        int damage = Math.Max(0, value - defence);
        if (live) damage = Math.Max(0, damage + Bell(11) - 5);
        if (cap && damage >= target.Hp) damage = target.Hp - 1;
        return damage;
    }

    /// <summary>
    /// Slow and Ashley's crippled limbs, by an entry's limbs' hit points after it (BATTLE.PRG 0x80085008): Slow is set
    /// when every slowing limb it names (0 names none) is at 1 hit point or less, and cleared when one is above; Ashley's
    /// first four limbs set and clear their bits (<see cref="FieldContent.CrippledBit"/>) likewise.
    /// </summary>
    private void Settle(Outcome t)
    {
        var c = t.Who;
        if (t.Void || c.Object) return;
        int After(int limb)
        {
            if (limb < 0 || limb >= c.Limbs.Count || limb >= 6) return 0;
            int hp = c.Limbs[limb].Hp;
            return t.HpMode switch { 2 => hp + t.LimbHp[limb], 1 or 3 => hp - t.LimbHp[limb], _ => hp };
        }
        if (c.SlowingLimbs is { } slowing && (slowing.A | slowing.B) != 0)
        {
            bool standing = slowing.A != 0 && After(slowing.A) >= 2 || slowing.B != 0 && After(slowing.B) >= 2;
            if ((c.Status & 0x10) == 0 && !standing) Afflict(t, 4);
            else if ((c.Status & 0x10) != 0 && standing) Cure(t, 4);
        }
        if (c != Ashley) return;
        for (int limb = 0; limb < 4; limb++)
        {
            int bit = _view.Content.Field.CrippledBit(limb);
            if ((c.Status >> bit & 1) != 0) { if (After(limb) >= 2) Cure(t, bit); }
            else if (After(limb) < 2) Afflict(t, bit);
        }
    }

    /// <summary>
    /// Pays an action's cost (BATTLE.PRG 0x8008574c) by its kind: 1 magic points, 2 RISK (raised by it), 3 hit points
    /// (more than it must be held), 4 the weapon's PP, 6 the weapon's own (Ashley's attack: its RISK cost); 5 and 7 cost
    /// nothing. A chain ability from the tenth link on costs the links squared over 10 more. RISK past 100 is held at 100
    /// and the action goes ahead; any other cost not met fails and takes nothing. <paramref name="apply"/> charges it.
    /// </summary>
    /// <remarks>Kind 6 takes the weapon's cost kind and amount (its data's +0x14b and +0x146, the blade record's bytes
    /// 0x11 and 0x12, copied by 0x8006a334). The tenth-link rule counts the exchange's actions (<see cref="_exchanges"/>).</remarks>
    public bool Pay(ActionRecord record, Combatant c, bool apply)
    {
        int kind = record.CostKind, cost = record.Cost;
        if (kind == 6) (kind, cost) = (c.CostKind, c.Cost);
        if (record.Id is >= 22 and <= 39 && kind == 2 && _exchanges >= 10) cost += _exchanges * _exchanges / 10;
        int have;
        switch (kind)
        {
            case 1: have = c.Mp; break;
            case 2: have = 100 - c.Risk; break;
            case 3: have = c.Hp; cost++; break;
            case 4: have = c.Blade.Pp; break;
            default: return true;
        }
        if (have < cost)
        {
            if (kind != 2) return false;
            if (apply) c.Risk = 100;
            return true;
        }
        if (!apply) return true;
        switch (kind)
        {
            case 1: c.Mp = have - cost; break;
            case 2: c.Risk = 100 - (have - cost); break;
            case 3: c.Hp = have - (cost - 1); break;
            case 4: c.Blade.Lose(cost); break;
        }
        return true;
    }

    /// <summary>
    /// Applies an entry to its fighter (BATTLE.PRG 0x800875c8), as the action's effect reaches it: nothing comes of it
    /// ("Miss"); hit points (with each limb's share; armour wears with damage), magic points, RISK, the stats for good
    /// (cap 999), the most hit and magic points, the weapon's DP and PP, the shield's and struck armour's DP, the
    /// shield's PP; then the statuses it clears, then those it sets. Death waits for the next status tick
    /// (<see cref="Recover"/>). <paramref name="action"/> is Ashley's action that may fell it (-1 none).
    /// </summary>
    private void Apply(Outcome t, int action = -1, bool flinch = true, bool front = true)
    {
        var c = t.Who;
        if (t.Void) return;
        var rules = _view.Content.Field;
        // A crate a spell or a turned missile reaches breaks when it takes its hit point (0x80074a20).
        if (c.Cube)
        {
            if (t.HpMode is 1 or 3) { c.Hp = 0; _view.BreakCrate(c.Actor.Id); }
            return;
        }
        if (t.Kind == 4) { if (!c.Cube) Number(c, "Miss"); return; }
        switch (t.HpMode)
        {
            case 2:
                if (!c.Cube) Number(c, "+" + Math.Min(t.Hp, c.MaxHp - c.Hp));
                c.Hp = Math.Min(c.MaxHp, c.Hp + t.Hp);
                for (int i = 0; i < 6 && i < c.Limbs.Count; i++) c.Limbs[i].Hp = Math.Clamp(c.Limbs[i].Hp + t.LimbHp[i], 0, c.Limbs[i].MaxHp);
                break;
            case 1 or 3:
                for (int i = 0; i < 6 && i < c.Limbs.Count; i++)
                {
                    c.Limbs[i].Hp = Math.Max(0, c.Limbs[i].Hp - t.LimbHp[i]);
                    c.Limbs[i].Armour.Wear(t.LimbWear[i]);
                }
                if (c.Hp > 0 && c.Hp <= t.Hp) c.FelledBy = action;
                Strike(c, t.Hp, flinch, front);
                break;
        }
        switch (t.MpMode)
        {
            case 2: c.Mp = Math.Min(c.MaxMp, c.Mp + t.Mp); break;
            case 1: c.Mp = Math.Max(0, c.Mp - t.Mp); break;
        }
        if (t.RiskMode == 2) c.Risk = Math.Max(0, c.Risk - t.Risk);
        foreach (var (stat, amount, mode) in new[] { (0, t.Strength, t.StrengthMode), (1, t.Intelligence, t.IntelligenceMode), (2, t.Agility, t.AgilityMode), (3, t.MaxHp, t.MaxHpMode), (4, t.MaxMp, t.MaxMpMode) })
        {
            if (mode == 0 || stat >= 3 && mode != 2) continue;
            int by = mode == 2 ? amount : -amount;
            if (c == Ashley) { _view.Grow(stat, by, fill: false); continue; }
            switch (stat)
            {
                case 0: c.Strength = Math.Clamp(c.Strength + by, 0, 999); break;
                case 1: c.Intelligence = Math.Clamp(c.Intelligence + by, 0, 999); break;
                case 2: c.Agility = Math.Clamp(c.Agility + by, 0, 999); break;
                case 3: c.MaxHp = Math.Min(999, c.MaxHp + by); break;
                default: c.MaxMp = Math.Min(999, c.MaxMp + by); break;
            }
        }
        if (t.BladeDpMode == 2) c.Blade.Mend(t.BladeDp);
        if (t.BladePpMode == 2) c.Blade.Gain(t.BladePp);
        else if (t.BladePpMode == 1) c.Blade.Lose(t.BladePp);
        if (t.WearMode == 1)
        {
            c.Shield.Wear(t.Wear);
            if (t.Limb >= 0 && t.Limb < c.Limbs.Count) c.Limbs[t.Limb].Armour.Wear(t.Wear);
        }
        if (t.ShieldPpMode == 1) c.Shield.Lose(t.ShieldPp);
        for (int bit = 0; bit < 32; bit++) if ((t.Clear >> bit & 1) != 0) c.ClearStatus(bit, rules);
        for (int bit = 0; bit < 32; bit++) if ((t.Set >> bit & 1) != 0) c.SetStatus(bit, rules);
    }

    /// <summary>
    /// A shape an action takes its targets in (BATTLE.PRG's descriptor, tested by 0x800c1564 through the table at
    /// 0x80069844): its kind (a range or area record's fourth byte, low three bits), its x, y and z radii (32 units each),
    /// its pitch (the fourth byte's high five bits, 16ths of a 4096th of a turn), its yaw and its centre. A point is
    /// moved to the centre and turned by the yaw and pitch (y is down); then 1 is an ellipsoid; 2 a cylinder centred on
    /// the centre and 3 one standing on it, as high as its y radius; 4 a cone standing on it, narrowing to a point at that
    /// height, and 5 the same turned over and centred; 6 a box and 7 a diamond standing on it; 0 holds nothing.
    /// </summary>
    public readonly record struct Shape(int Kind, int X, int Y, int Z, int Pitch, float Yaw, Vector3 Centre)
    {
        public static Shape Of(int[] bytes, float yaw, Vector3 centre) => new(bytes[3] & 7, bytes[0], bytes[1], bytes[2], bytes[3] & 0xF8, yaw, centre);

        public bool Holds(Vector3 point)
        {
            int pitch = Kind == 5 ? Pitch + 128 : Pitch;
            var local = new Basis(Vector3.Right, pitch * 16 * Mathf.Tau / 4096) * (new Basis(Vector3.Up, -Yaw * Mathf.Tau / 4096) * (point - Centre));
            int x = (int)local.X, y = (int)local.Y, z = (int)local.Z;
            // An axis in units of its radius, 4096 a radius (0x800c1564: the offset times 0x8000 over the radius, down 12).
            static long Unit(int d, int r) => r == 0 ? (d == 0 ? 0 : 0x10000) : (long)d * (0x8000 / r) >> 12;
            int rx = X, rz = Z;
            bool Ellipse(int dx, int dz) => Unit(dx, rx) * Unit(dx, rx) + Unit(dz, rz) * Unit(dz, rz) < 65536;
            int height = Y;
            if (Kind is 2 or 5) { y -= 32 * Y; height = 2 * Y; }
            bool upright = -32 * height <= y && y <= 0;
            switch (Kind)
            {
                case 1: return Unit(x, X) * Unit(x, X) + Unit(y, Y) * Unit(y, Y) + Unit(z, Z) * Unit(z, Z) < 65536;
                case 2 or 3: return upright && Ellipse(x, z);
                case 4 or 5:
                {
                    if (!upright || height == 0) return false;
                    int scale = 256 - -y * 8 / height;
                    return scale > 0 && Ellipse(x * 256 / scale, z * 256 / scale);
                }
                case 6: return upright && Math.Abs(x) <= 32 * X && Math.Abs(z) <= 32 * Z;
                case 7: return upright && (long)Math.Abs(x) * Z + (long)Math.Abs(z) * X <= 32L * X * Z;
                default: return false;
            }
        }
    }

    /// <summary>A fighter's middle, its model's centre height above its feet (BATTLE.PRG 0x800a190c), where its shapes are centred.</summary>
    private static Vector3 Middle(Combatant c) => c.Actor.Position + new Vector3(0, c.Centre, 0);

    /// <summary>
    /// What a shape holds, in actor order (BATTLE.PRG 0x80071f68): the fighters the action's target kind takes (its side
    /// bits, 0x800E81D4: 7 for fighters; the user only when the kind allows, 0x800E81B4), each by its middle, or for a kind
    /// that takes limbs (0x800E81C4) each of their limbs by its point (0x800a1b9c).
    /// </summary>
    /// <remarks>Only fighters are scanned here; traps (0x20) and map objects (0x80) are the room's (<see cref="FieldView.Cast"/>).</remarks>
    private List<(Combatant Who, int Limb)> Scan(ActionRecord record, Combatant caster, Shape shape)
    {
        var kind = _view.Content.Field.TargetKind(record.TargetKind);
        var found = new List<(Combatant, int)>();
        if ((kind.Sides & 7) == 0) return found;
        foreach (var c in Combatants.Values)
        {
            if (c == caster && !kind.Self || !Takes(c, kind.Sides)) continue;
            if (!kind.Limbs) { if (shape.Holds(Middle(c))) found.Add((c, c.Struck)); continue; }
            for (int limb = 0; limb < c.Limbs.Count; limb++)
                if (c.Limbs[limb].MaxHp > 0 && shape.Holds(Aim(c, limb))) found.Add((c, limb));
        }
        return found;
    }

    /// <summary>
    /// Everything an action aimed at <paramref name="target"/> (and its limb) takes (BATTLE.PRG 0x800708ec, 0x80070b04):
    /// for kinds 2, 4, 6 and 8 those of the range's <paramref name="candidates"/> inside the area round the chosen point
    /// (the record's bytes 8 to 11, unturned; 0x80072734, 0x8007053c); for kinds 11 and 12 every fighter or limb inside
    /// the range's shape from the user aimed at the point, pitched up a quarter turn (0x800719fc); else the target alone.
    /// </summary>
    private List<(Combatant Who, int Limb)> Area(ActionRecord record, Combatant caster, Combatant target, int limb, List<(Combatant Who, int Limb)> candidates)
    {
        bool limbs = _view.Content.Field.TargetKind(record.TargetKind).Limbs;
        Vector3 Point(Combatant c, int l) => limbs && l >= 0 ? Aim(c, l) : Middle(c);
        var point = Point(target, limb);
        switch (record.TargetKind)
        {
            case 2 or 4 or 6 or 8:
            {
                var area = Shape.Of(record.Area, 0, point);
                return candidates.FindAll(c => area.Holds(Point(c.Who, c.Limb)));
            }
            case 11 or 12:
            {
                var from = Middle(caster);
                var to = point - from;
                int pitch = (int)(Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length()) * 4096 / Mathf.Tau / 16) + 64;
                return Scan(record, caster, new Shape(record.Range[3] & 7, record.Range[0], record.Range[1], record.Range[2], pitch, caster.Actor.FacingToward(point), from));
            }
            default:
                return Request(record, target, limb);
        }
    }

    /// <summary>
    /// Whether a fighter can be taken by an action whose kind names <paramref name="sides"/> (BATTLE.PRG 0x80071f68): one
    /// of its sides, not out of the battle (0x8000), and present (0x800a0be0's flags 0x3000001 = 1: living, shown).
    /// </summary>
    private static bool Takes(Combatant c, int sides) =>
        (c.Side & sides) != 0 && (c.Side & 0x8000) == 0 && !c.Dead && c.Hp > 0 && !c.Suspended && !c.Cube && c.Actor.Visible;

    /// <summary>
    /// What an action launched at <paramref name="target"/> takes (BATTLE.PRG 0x80072ec4 then 0x80070f28, the same for
    /// Ashley and the enemies): for an area kind, those its range shape holds round its user (turned with it) and then
    /// its area (<see cref="Area"/>); else the target alone.
    /// </summary>
    private List<(Combatant Who, int Limb)> Targets(ActionRecord record, Combatant caster, Combatant target, int limb)
    {
        if (record.TargetKind is not (2 or 4 or 6 or 8 or 11 or 12)) return Request(record, target, limb);
        var range = Shape.Of(record.Range[0] == 255 ? caster.WeaponRange : record.Range, caster.Actor.Facing, Middle(caster));
        return Area(record, caster, target, limb, Scan(record, caster, range));
    }

    /// <summary>
    /// What the planner reckons an area action on one target with (BATTLE.PRG 0x800863a4): the shape round the target (the
    /// record's area, kinds 2, 4, 6 and 8) or aimed at it from the user (its range, 11 and 12); then kinds 1 to 4 take
    /// the target whole, 5 and 7 its given limb, and 6, 8, 11 and 12 each of the target's limbs inside the shape, and no
    /// other fighter.
    /// </summary>
    private List<(Combatant Who, int Limb)> Reckoned(ActionRecord record, Combatant caster, Combatant target, int limb)
    {
        var point = Middle(target);
        Shape shape;
        if (record.TargetKind is 11 or 12)
        {
            var from = Middle(caster);
            var to = point - from;
            int pitch = (int)(Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length()) * 4096 / Mathf.Tau / 16) + 64;
            shape = new Shape(record.Range[3] & 7, record.Range[0], record.Range[1], record.Range[2], pitch, caster.Actor.FacingToward(point), from);
        }
        else shape = Shape.Of(record.Area, 0, point);
        var taken = new List<(Combatant, int)>();
        switch (record.TargetKind)
        {
            case <= 4: taken.Add((target, target.Struck)); break;
            case 5 or 7: taken.Add((target, limb)); break;
            case 6 or 8 or 11 or 12:
                for (int l = 0; l < target.Limbs.Count; l++)
                    if (target.Limbs[l].MaxHp > 0 && shape.Holds(Aim(target, l))) taken.Add((target, l));
                break;
        }
        return taken;
    }

    /// <summary>
    /// A missile's line of fire to an entry (BATTLE.PRG 0x800e6c34, from 0x80085f44 on a live resolve): from the user's
    /// middle to the target's point in 32 steps, each step testing every fighter but the user on sides 1, 4 or 0x10 (a
    /// box 63 units each way across and 128 high on its feet, ahead of the user) and then the floor. A fighter in the
    /// way takes the entry, at its own limb, unless it is the target (the result is marked redirected, +0x48); the floor
    /// makes the entry a place (+0x40), which every effect passes by.
    /// </summary>
    /// <remarks>A fighter's box is its model's girth across and height high (actor +0x63c, +0x63e) on its feet. A crate is
    /// an actor (0x800765b0, side 0x10) whose body is an object of the table at 0x800f45e0 (0x8009998c), boxed 63 across
    /// and 128 high on its body's place, the bottom of the crate. Then the room's moving platforms stop it within 64
    /// across and from their point to 64 under it (0x800e6ba0, the list of 0x8008d2c0).</remarks>
    private void LineOfFire(Resolution r, Outcome entry)
    {
        if (entry.Void || r.Caster is not { } caster) return;
        var from = Middle(caster);
        var to = entry.Limb >= 0 ? Aim(entry.Who, entry.Limb) : Middle(entry.Who);
        var step = new Vector3(Mathf.Floor((to.X - from.X) / 32), Mathf.Floor((to.Y - from.Y) / 32), Mathf.Floor((to.Z - from.Z) / 32));
        var point = from;
        for (int i = 0; i < 32; i++)
        {
            point += step;
            foreach (var c in Combatants.Values)
            {
                if (c == caster || c.Dead || c.Cube || !c.Actor.Visible || (c.Side & 0xFFEA) != 0) continue;
                var at = c.Actor.Position;
                int half = c.Height / 2;
                if (Math.Abs(at.X - point.X) >= c.Girth || Math.Abs(at.Y - half - point.Y) >= half || Math.Abs(at.Z - point.Z) >= c.Girth) continue;
                if ((at.X - from.X) * step.X + (at.Z - from.Z) * step.Z < 0) continue;
                if (c != entry.Who) { entry.Who = c; entry.Limb = c.Struck; }
                return;
            }
            foreach (var crate in _view.Crates)
            {
                var at = new Vector3(crate.X * 128 + 64, crate.Bottom, crate.Z * 128 + 64);
                if (Math.Abs(at.X - point.X) >= 63 || Math.Abs(at.Y - 64 - point.Y) >= 64 || Math.Abs(at.Z - point.Z) >= 63) continue;
                if ((at.X - from.X) * step.X + (at.Z - from.Z) * step.Z < 0) continue;
                // The crate takes the entry, whole.
                if (Crate(crate.Id) is { } cube) { entry.Who = cube; entry.Limb = -1; }
                else entry.Void = true;
                return;
            }
            foreach (var at in _view.PlatformPoints())
                if (Math.Abs(at.X - point.X) < 64 && Math.Abs(at.Y + 32 - point.Y) < 32 && Math.Abs(at.Z - point.Z) < 64) { entry.Void = true; return; }
            if (_view.FloorAt(point) is { } floor && point.Y >= floor) { entry.Void = true; return; }
        }
    }

    /// <summary>The fighters and limbs an action on <paramref name="target"/> takes: the limb chosen for one that takes limbs, else the fighter's own (its data's +0x36).</summary>
    private List<(Combatant Who, int Limb)> Request(ActionRecord record, Combatant target, int limb) =>
        new() { (target, _view.Content.Field.TargetKind(record.TargetKind).Limbs && limb >= 0 ? limb : target.Struck) };

    /// <summary>
    /// The battle abilities' timing (BATTLE.PRG 0x80078AB4), in 60 Hz ticks about the moment the hit lands: a button set
    /// to an ability is graded once a link, by how far its press falls from that moment against a margin of 10 ticks
    /// less a third of the chain so far (at least 2): too fast up to a margin early, fast before the hit, in time up to
    /// a margin after it, slow up to two margins and too slow after. Pressed in time (and not the button of the last
    /// link) a chain ability adds its effect onto Ashley's hit and a new link begins 4 frames on; a defense ability eases
    /// the hit she took. Each costs its RISK. The red "!" shows over Ashley from two ticks before the hit for a
    /// margin's frames. Returns whether the strike is held open for the timing.
    /// </summary>
    private bool StepAbilities()
    {
        if (_attacker == null || _victim == null || Ashley == null) return false;
        bool chain = _attacker == Ashley && !_victim.Dead && _victim.Hp > 0 && Array.Exists(ChainSlots, slot => slot != 0);
        bool defense = _victim == Ashley && Array.Exists(DefenseSlots, slot => slot != 0);
        if (!chain && !defense) return false;
        int margin = Math.Max(2, 10 - _chain / 3), ticks = (_landAt - _frames) * 2;
        if (ticks == -2) _hud?.Cue(Ashley.Actor.Position - new Vector3(0, Crown(Ashley), 0), margin, Project);
        if (!_attempted)
        {
            var slots = chain ? ChainSlots : DefenseSlots;
            for (int i = 0; i < 3; i++)
            {
                if (slots[i] == 0 || !_pressed(SlotButtons[i])) continue;
                _attempted = true;
                int grade = ticks <= -margin ? 0 : ticks <= 0 ? 1 : ticks <= margin ? 2 : ticks <= margin * 2 ? 3 : 4;
                LastGrade = grade;
                Grades++;
                _hud?.Timing(grade, _random.RandiRange(0, 7));
                if (grade != 2 || i == _lastButton) break;
                _lastButton = i;
                UseAbility(slots[i], chain);
                _view.CountAbilityPoint(chain);
                if (!chain) break;
                _chain++;
                _hud?.Chains(_chain);
                // The next link's hit, 4 frames on.
                _attempted = false;
                _landAt = _frames - 4;
                break;
            }
        }
        // The strike is held for the slowest grade.
        int left = _landAt - _frames;
        return !_attempted && _struck && left * 2 <= margin * 2;
    }

    /// <summary>
    /// A chain or defense ability, resolved and applied at once (BATTLE.PRG 0x8006fd24 resolves a link live, <see
    /// cref="Resolve"/>): Ashley is its user, and its target the enemy struck for a chain, or the attacker for a
    /// defense; it costs its RISK (and more from the tenth link). Its amounts answer the hit just dealt or taken
    /// (<see cref="Amount"/>): the timing runs on blows only, so no hit taken is a spell.
    /// </summary>
    private void UseAbility(int id, bool chain)
    {
        if (Ashley == null || _lastStruck == null) return;
        var ability = _view.Content.Data("abilities").GetValueOrDefault(id.ToString(), new GDictionary()).AsGodotDictionary();
        if (ability.Count == 0) return;
        var other = chain ? _lastStruck : _attacker;
        if (other == null) return;
        // Its flash: a resident small effect, on the one struck for a chain, on Ashley for a defense.
        int visual = ability.GetValueOrDefault("visual", 0).AsInt32();
        if (visual > 0) _view.PlayResident(visual, chain ? Ashley.Actor : _lastStruck.Actor, chain ? _lastStruck.Actor : Ashley.Actor);
        // A chain link counts a link and an action, a defense an action (0x8006fe38, 0x8006ff28), before it is resolved.
        if (chain) _links++;
        _exchanges++;
        var result = Resolve(id, Ashley, new List<(Combatant, int)> { (other, chain ? _victimLimb : other.Struck) }, live: true);
        if (result.Self != null) Apply(result.Self);
        foreach (var target in result.Targets)
        {
            // A chain's damage is the hit the next link answers.
            if (chain && target.HpMode is 1 or 3 && target.Hp > 0) _lastDamage = target.Hp;
            if (target.Kind != 4) Apply(target, chain ? id : -1, flinch: false);
        }
    }

    /// <summary>
    /// A Break Art (the menu's Break Arts): it needs the weapon drawn and more hit points than its cost. It strikes the
    /// nearest enemy in Ashley's sphere, resolved (and its hit points paid) as it starts: the wind-up is battle
    /// animation 91 (or 93), the release the art's own set for the target's height, and its blow lands at its effect's.
    /// </summary>
    public string Art(int id, FieldPlayer player)
    {
        if (Ashley == null || _phase != Phase.Free || EnemyActing) return "Not now.";
        if (!Drawn) return "Draw your weapon first.";
        var art = _view.Content.Data("spells")["actions"].AsGodotDictionary().GetValueOrDefault(id.ToString(), new GDictionary()).AsGodotDictionary();
        if (art.Count == 0) return "That art is not known here.";
        int cost = art["cost"].AsInt32();
        if (Ashley.Hp <= cost) return "Not enough HP.";
        (Combatant Who, int Limb)? aim = null;
        foreach (var c in Combatants.Values)
        {
            if (!Takes(c, _view.Content.Field.TargetKind(_view.Content.Field.Action(id).TargetKind).Sides) || !InSphere(Ashley, c)) continue;
            if (aim == null || (c.Actor.Position - Ashley.Actor.Position).Length() < (aim.Value.Who.Actor.Position - Ashley.Actor.Position).Length())
                aim = (c, c.Limbs.FindIndex(limb => limb.MaxHp > 0));
        }
        if (aim is not { } target) return _view.RetailMessage(0);
        _links = 0;
        _exchanges = 1;
        _resolution = Resolve(id, Ashley, Request(_view.Content.Field.Action(id), target.Who, target.Limb), live: true);
        player.Halt();
        _spell = art;
        _spellTarget = target.Who;
        _acted = new Acted(Ashley, id, target.Who);
        _victimLimb = target.Limb;
        _artNumber = (id - 184) % 4;
        _released = false;
        _view.Attacked();
        Ashley.Actor.Facing = Ashley.Actor.FacingToward(target.Who.Actor.Position);
        _phase = Phase.Casting;
        HoldCamera(Ashley, target.Who, art, () =>
        {
            if (Play(Ashley, 91, 8) == null) Play(Ashley, 93, 8);
            _frames = Math.Max(10, (int)Math.Ceiling((Ashley.Actor.Player?.CurrentAnimationLength ?? 0.5) * 30));
            _view.Announce(25, art["name"].AsString());
        });
        return "";
    }

    private int _artNumber = -1;

    private GDictionary? _spell;
    private Combatant? _spellTarget;
    // What Ashley's spell, art or item under way was resolved to as it started.
    private Resolution? _resolution;
    // The spell under way, by action (0 for none or an art).
    private int _spellAction;
    private bool _released;
    // A spell's effect is playing, and what it does lands when it has run.
    private bool _landing;

    /// <summary>
    /// Casts a learned spell (the menu's Magic): it needs its MP. A spell that heals (effect 62) is cast on Ashley;
    /// one that harms, on the nearest enemy within its range (32 units a step). Ashley plays the wind-up (85, or 87)
    /// and then the release (88, or 89); the spell takes effect as it is released. Returns a message, or empty when
    /// the spell is under way.
    /// </summary>
    public string Cast(int id, FieldPlayer player, Action? confirmed = null)
    {
        if (Ashley == null || _phase != Phase.Free || EnemyActing) return "Not now.";
        var spell = _view.Content.Data("spells")["actions"].AsGodotDictionary().GetValueOrDefault(id.ToString(), new GDictionary()).AsGodotDictionary();
        if (spell.Count == 0) return "That spell is not known here.";
        int cost = spell["cost"].AsInt32();
        if (Ashley.Mp < cost) return "Not enough MP.";
        if (Ashley.Has(Silence)) return "Ashley is silenced.";
        if (Ashley.Has(Paralysis)) return "Ashley is paralysed.";
        // The sphere opens on what its range takes (BATTLE.PRG 0x80071f68): every fighter (or limb) inside its shape,
        // turned with Ashley about her middle, in actor order, Ashley herself only for a kind that may be used on its
        // user; nothing is spent until a target is chosen.
        var record = _view.Content.Field.Action(id);
        var found = Scan(record, Ashley, Shape.Of(record.Range[0] == 255 ? Ashley.WeaponRange : record.Range, Ashley.Actor.Facing, Middle(Ashley)));
        if (found.Count == 0) return _view.RetailMessage(0);
        var candidates = new List<(Combatant Who, List<int> Parts)>();
        foreach (var (who, limb) in found)
        {
            int at = candidates.FindIndex(c => c.Who == who);
            if (at < 0) candidates.Add((who, new List<int> { limb }));
            else candidates[at].Parts.Add(limb);
        }
        player.Halt();
        if (!AimAt(candidates, spell, (target, limb) => { confirmed?.Invoke(); BeginCast(id, spell, target, limb, found, player); }, spellAction: id)) return "Not now.";
        return "";
    }

    /// <summary>
    /// A spell's cast once its target is chosen: it is resolved on all its area takes (<see cref="Area"/>), and its MP
    /// paid, at once (BATTLE.PRG 0x80070f28), then the wind-up plays and its name is announced.
    /// </summary>
    private void BeginCast(int id, GDictionary spell, Combatant target, int limb, List<(Combatant Who, int Limb)> found, FieldPlayer player)
    {
        if (Ashley == null) return;
        var record = _view.Content.Field.Action(id);
        if (!Pay(record, Ashley, apply: false)) return;
        _links = 0;
        _exchanges = 1;
        _resolution = Resolve(id, Ashley, Area(record, Ashley, target, limb, found), live: true);
        _view.Acted(id);
        player.Halt();
        _spell = spell;
        _spellAction = id;
        _spellTarget = target;
        _acted = new Acted(Ashley, id, target);
        _released = false;
        if (target != Ashley) Ashley.Actor.Facing = Ashley.Actor.FacingToward(target!.Actor.Position);
        _phase = Phase.Casting;
        HoldCamera(Ashley, target!, spell, () =>
        {
            if (Play(Ashley, 85, 6) == null) Play(Ashley, 87, 6);
            Chant(2);
            _view.Announce(23, spell["name"].AsString());
        });
    }

    /// <summary>E000's casting sparkle about Ashley while she casts, or null.</summary>
    private FieldEffect? _chant;

    /// <summary>
    /// The casting itself (BATTLE.PRG 0x8007138C, ended by 0x80078748): the action is requested in casting mode
    /// <paramref name="mode"/> (1 an item, 2 a spell), E000's entry 56 sparkles about her while it lasts, and it lasts
    /// until the action's name has been shown out (the scene message's 120 ticks once typed; footage, Ep. 2 29:52:
    /// Heal's casting runs 3.2 s from the camera's cut to her release).
    /// </summary>
    private void Chant(int mode)
    {
        _chant = _view.PlayResident(56, Ashley!.Actor, Ashley.Actor, mode: mode);
        _frames = FieldView.AnnounceFrames + 9;
    }

    private void EndChant()
    {
        if (_chant != null && GodotObject.IsInstanceValid(_chant)) _chant.Mode = 0;
        _chant = null;
    }

    /// <summary>An item waiting on its casting to take effect, or null.</summary>
    private Action? _itemApply;

    /// <summary>What starts an action once the camera has reached its view, or null.</summary>
    private Action? _onSettled;

    /// <summary>
    /// Puts the camera on an action as retail does before it starts (BATTLE.PRG 0x800707xx, stepped by 0x80078xxx):
    /// the look-at goes halfway between the actor and its target, and the action's view (its record's camera: 1 close
    /// in, 2 drawn back) is taken; a close view is dropped when the two stand 0x1C1 units or more apart (0x80077F70).
    /// <paramref name="start"/> runs once the view is reached; null for an action already under way.
    /// </summary>
    private void HoldCamera(Combatant actor, Combatant target, GDictionary action, Action? start)
    {
        int view = action.GetValueOrDefault("camera", 0).AsInt32();
        var from = actor.Actor.Position + new Vector3(0, -GameplayCamera.LookHeight, 0);
        var to = target.Actor.Position + new Vector3(0, -GameplayCamera.LookHeight, 0);
        if (view == 1 && (to - from).Length() >= 0x1C1) view = 0;
        _view.Gameplay.Act((from + to) / 2, view);
        _onSettled = start;
        _frames = 1;
    }

    /// <summary>
    /// An item used in battle (its action's camera, then <paramref name="apply"/>, which plays its effect): Ashley is
    /// held until the effect has run (footage: the Elixir of Queens closes in on her, then "Item Effect").
    /// </summary>
    public void UseItem(GDictionary definition, Action apply)
    {
        if (Ashley == null || _phase != Phase.Free) { apply(); return; }
        _player?.Halt();
        _spell = definition;
        _spellAction = 0;
        _spellTarget = Ashley;
        _released = false;
        _artNumber = -1;
        _phase = Phase.Casting;
        HoldCamera(Ashley, Ashley, definition, () =>
        {
            // An item is started as a spell is (0x8007138C: the same casting animation), in casting mode 1.
            _itemApply = apply;
            if (Play(Ashley, 85, 6) == null) Play(Ashley, 87, 6);
            Chant(1);
            if (definition.ContainsKey("name")) _view.Announce(27, definition["name"].AsString());
        });
    }

    private void StepCast()
    {
        if (_onSettled is { } start)
        {
            if (!_view.Gameplay.Settled) return;
            _onSettled = null;
            start();
            return;
        }
        bool playing = _castEffect != null && GodotObject.IsInstanceValid(_castEffect) && !_castEffect.Finished;
        // What the spell or art does lands at its effect's hit, even while her motion still runs.
        if (_landing && Lands(_castEffect))
        {
            _landing = false;
            if (_resolution != null) Release(_resolution);
            _resolution = null;
            if (Ashley!.Dead) return;
        }
        if (--_frames > 0) return;
        if (!_released)
        {
            _released = true;
            if (_artNumber >= 0)
            {
                var offset = Aim(_spellTarget!, _victimLimb) - (Ashley!.Actor.Position + new Vector3(0, -150, 0));
                float pitch = Mathf.Atan2(-offset.Y, new Vector2(offset.X, offset.Z).Length()) * 4096 / Mathf.Tau;
                Ashley.Actor.Animate($"art{Ashley.Category:X}{_artNumber}_{(pitch < -228 ? 0 : pitch >= 172 ? 2 : 1)}", 4, once: true);
            }
            else
            {
                EndChant();
                if (_itemApply is { } apply)
                {
                    // The item takes effect as its casting ends: its effect plays (and puts the HUD away).
                    _itemApply = null;
                    apply();
                    _frames = 1;
                    return;
                }
                if (Play(Ashley!, 88, 4) == null) Play(Ashley!, 89, 4);
            }
            _frames = Math.Max(10, (int)Math.Ceiling((Ashley!.Actor.Player?.CurrentAnimationLength ?? 0.5) * 30));
            // The spell's or art's effect plays from her release or swing.
            _castEffect = _view.PlayEffect(_spell!.GetValueOrDefault("visual", 0).AsInt32(), Ashley!.Actor, _spellTarget!.Actor);
            _landing = true;
            return;
        }
        // The action lasts until its effect has run and its blow has landed (footage: Heal's burst ends and the HUD is
        // back before "Mastered" shows).
        if (playing || _landing) return;
        // A spell's first cast to the end masters it (BATTLE.PRG 0x8007B63C): retail says so as the action ends.
        if (_artNumber < 0 && _spellAction > 0 && _view.Mastered.Add(_spellAction)) _view.Announce(30, _spell!["name"].AsString());
        _artNumber = -1;
        _spellAction = 0;
        Play(Ashley!, 1, 6);
        _phase = Phase.Free;
    }

    /// <summary>
    /// What an action resolved at its launch does, applied as its effect reaches its targets (BATTLE.PRG 0x8006cb04 and
    /// 0x8006ca20): its user's entry when anything falls on it, then each target's. A kill by Ashley's attack, chain
    /// ability or art (costs of RISK, hit points or the weapon's) counts for her weapon (0x8008554c).
    /// </summary>
    private void Release(Resolution r)
    {
        int action = r.Caster == Ashley && r.Record.CostKind is 2 or 3 or 6 ? r.Record.Id : -1;
        if (r.Self is { Nothing: false } self) Apply(self, action);
        foreach (var target in r.Targets)
        {
            Apply(target, action);
            if (Ashley is { Dead: true }) return;
        }
    }

    /// <summary>
    /// What the sphere's readout gives as a spell's "HP" (its preview, <see cref="Resolve"/> unvaried): the hit points
    /// it would restore or take (footage: Heal on Ashley at 116 of 250 reads "HP 59", and she is at 175 after it), else
    /// the target's own; and its chance.
    /// </summary>
    /// <remarks>Retail's readouts (0x800707e4, 0x80071e18, 0x80071ed8, 0x800722d4, 0x8007239c) all preview through
    /// 0x8008631c, unvaried: no 1 to 5.</remarks>
    private (int Points, int Chance) SpellReadout(int action, Combatant target, int limb)
    {
        var record = _view.Content.Field.Action(action);
        var preview = Resolve(action, Ashley, Request(record, target, limb), live: false).Targets[0];
        int points = preview.HpMode is 1 or 2 or 3 ? preview.Hp : target.Hp;
        return (points, preview.Chance);
    }

    /// <summary>
    /// A trap's blow on Ashley as the test reads it: its reckoning (<see cref="Trapped"/>) at the room's trap level for
    /// one of her limbs.
    /// </summary>
    public int TrapDamage(Combatant target, ActionEffect effect, int level, int limb, bool vary)
    {
        _trapLevel = level;
        return Trapped(effect, target, limb, vary, cap: true);
    }

    /// <summary>
    /// A trap's action on Ashley (a room trap of type n is action n + 6, <see cref="Installation.InventoryConversion.TrapActions"/>),
    /// resolved with no user at the room's trap level and applied at once, on her own limb (her data's +0x36, as
    /// BATTLE.PRG 0x80073d30 requests it): damage (<see cref="Trapped"/>), a heal panel's healing (the same less its defence, with
    /// no cap) and its RISK cut, a status, or a cure.
    /// </summary>
    public void Trap(int action, int visual, int level)
    {
        if (Ashley is not { } ashley || ashley.Dead) return;
        _view.PlayEffect(visual, ashley.Actor, ashley.Actor);
        _trapLevel = level;
        Release(Resolve(action, null, new List<(Combatant, int)> { (ashley, ashley.Struck) }, live: true));
        ShowStatus();
    }

    /// <summary>
    /// An item used on Ashley (actions 224 on; MAINMENU.PRG maps each consumable to one), resolved live with her as
    /// user and target and applied at once. Returns her entry.
    /// </summary>
    public Outcome? UseAction(int action)
    {
        if (Ashley is not { } ashley) return null;
        var result = Resolve(action, ashley, new List<(Combatant, int)> { (ashley, ashley.Struck) }, live: true);
        Release(result);
        ShowStatus();
        return result.Targets[0];
    }

    /// <summary>
    /// Damage off the hit points, its number shown (to at most 999; none for a crate, 0x80087578), then a flinch from the
    /// front or back unless <paramref name="flinch"/> is false or none are left. A fighter left at none dies at the next
    /// status tick (<see cref="Recover"/>). Returns whether it has none left.
    /// </summary>
    public bool Strike(Combatant target, int damage, bool flinch = true, bool front = true)
    {
        target.Hp = Math.Max(0, target.Hp - damage);
        if (!target.Cube) Number(target, Math.Min(999, damage).ToString());
        if (target.Dead || target.Hp == 0) return true;
        int flinched = target == Ashley ? (front ? AshleyFlinchFront : AshleyFlinchBack) : front ? FlinchFront : FlinchBack;
        if (flinch && Play(target, flinched, 2) is { } set) target.Actor.Queue($"{set}_1", 6);
        return false;
    }

    /// <summary>
    /// A fighter at no hit points dies (BATTLE.PRG 0x800792e4, at the status tick): its death (70); Ashley's ends the
    /// battle; an enemy's counts as felled by Ashley's action, if one did it.
    /// </summary>
    private void Die(Combatant c)
    {
        c.Dead = true;
        c.Side |= 0x8000;
        Play(c, Death, 4, once: true);
        if (c == Ashley) { _phase = Phase.Dying; _frames = 90; return; }
        if (c.FelledBy >= 0) Felled?.Invoke(c, c.FelledBy);
        Defeated?.Invoke(c);
    }

    /// <summary>
    /// Effect ids to the status bits their handlers set (BATTLE.PRG's effect table at 0x800E8378, each handler passing
    /// its bit to 0x80081020): lowering strength, intelligence and agility 5, 7 and 9, slowing 4, raising 6, 8 and 10,
    /// quickening 11, silence 12, regeneration 17, paralysis 13, poison 14, numbness 15, curse 16 and the rest.
    /// </summary>
    private static readonly int[] StatusBits = { -1, 5, 7, 9, 19, 4, 6, 8, 10, 20, 11, 12, 18, 17, 29, 13, 14, 15, 16, 21, 22, 23, 24, 25, 26, 27, 28 };

    /// <summary>The status bit an effect id sets (<see cref="StatusBits"/>), or -1.</summary>
    public static int StatusBit(int effect) => effect > 0 && effect < StatusBits.Length ? StatusBits[effect] : -1;

    /// <summary>
    /// The status rules whose duration loads each of the five group timers (BATTLE.PRG 0x80086fa8 reads rule 5's for
    /// the stat changes, 11's for Slow and Quicken, 19's for Tarnish to the guards, and 13's for paralysis and for numbness).
    /// </summary>
    private static readonly int[] GroupDurations = { 5, 11, 19, 13, 13 };

    /// <summary>
    /// One move an enemy has planned (a 20-byte entry of its AI record's list at +0x2f0, built by BATTLE.PRG
    /// 0x800dd918): its score (the fight's well-being after it, times 128, plus its weight), the action and the limb
    /// slot and place it is listed in, its target and the target's limb, the frame of the planner's clock from which it
    /// may be made, and its weight (a hit chance in 128ths; 0 when it no longer can be made).
    /// </summary>
    public sealed class PlanEntry
    {
        public int Score, Action, Slot, Index, Limb, Horizon, Weight;
        public Combatant Target = null!;
        public PlanEntry Copy() => (PlanEntry)MemberwiseClone();
    }

    /// <summary>
    /// A fighter as the planner reckons it (BATTLE.PRG's 24-byte snapshot, 0x800dc424): hit and magic points, its
    /// weapon's and shield's DP (its data's +0x150 and +0x2b4), each with its most, and its statuses.
    /// </summary>
    private struct Standing
    {
        public int Hp, MaxHp, Mp, MaxMp, BladeDp, BladeMaxDp, ShieldDp, ShieldMaxDp;
        public uint Statuses;
    }

    /// <summary>The frames since the fight was last planned (BATTLE.PRG's clock at +0x18, a frame each AI tick, 0x800d8ef4).</summary>
    private int _planClock;
    /// <summary>The targets each action is planned on already this round (BATTLE.PRG's marks at 0x800f5904, a byte a target and action).</summary>
    private readonly HashSet<(Combatant Target, int Action)> _planned = new();
    /// <summary>The last resorts met while planning (0x800f58f8, sixteen at most) and the limb slots and places they came from (0x800f58f4).</summary>
    private readonly List<PlanEntry> _lastResorts = new();
    private int _lastResortSlots;
    /// <summary>Whether something was under way last frame, and how many were hostile: a change has the fight planned anew.</summary>
    private bool _wasActing;
    private int _hostileCount = -1;

    /// <summary>
    /// The largest magic cost among an actor's actions (its AI record's +0x17, 0x800e5710), 1 for one without a planner
    /// (Ashley). Silence counts against one only when it is not 0.
    /// </summary>
    private int MostMagic(Combatant c)
    {
        if (c == Ashley) return 1;
        int most = 0;
        foreach (var limb in c.Limbs)
            foreach (int action in limb.Actions)
                if (action != 0 && _view.Content.Field.Action(action) is { CostKind: 1 } record) most = Math.Max(most, record.Cost);
        return most;
    }

    /// <summary>
    /// How <paramref name="from"/> stands to <paramref name="to"/>: bits 6 and 7 of its relation word for it (its AI
    /// record's +0x1a4, <see cref="Mind.Relations"/>). 3 is a foe, 2 an ally, 1 neither.
    /// </summary>
    /// <summary>How the AI currently regards another fighter: 1 neither, 2 ally, 3 foe; 0 without a relation.</summary>
    public static int Relation(Combatant from, Combatant to) => from.Mind is { } mind && (uint)to.Index < 16 ? (int)(mind.Relations[to.Index] >> 6 & 3) : 0;

    /// <summary>
    /// A fighter's well-being as an enemy reckons it (BATTLE.PRG 0x800dcc94, summed by 0x800dc284): six times the
    /// standing of its statuses (<see cref="FieldContent.Status"/>; silence only for one with a magic cost,
    /// <see cref="MostMagic"/>), less 128 when it is down and 84 under 44/128 of its hit points; plus four times its hit
    /// points, its magic points, and twice its weapon's and its shield's DP, each a share of its most in 128ths.
    /// </summary>
    private int WellBeing(Combatant c, Standing s)
    {
        int standing = 0;
        bool silenceCounts = MostMagic(c) != 0;
        for (int bit = 0; bit < 32; bit++)
            if ((s.Statuses >> bit & 1) != 0 && (bit != 12 || silenceCounts)) standing += _view.Content.Field.Status(bit).Weight;
        if (s.Hp == 0) standing -= 128;
        if (s.Hp * 128 < s.MaxHp * 44) standing -= 84;
        int life = s.MaxHp != 0 ? s.Hp * 128 / s.MaxHp : 0, magic = s.MaxMp != 0 ? s.Mp * 128 / s.MaxMp : 0;
        int gear = (s.BladeMaxDp != 0 ? s.BladeDp * 128 / s.BladeMaxDp : 0) + (s.ShieldMaxDp != 0 ? s.ShieldDp * 128 / s.ShieldMaxDp : 0);
        return 6 * standing + 4 * life + magic + 2 * gear;
    }

    /// <summary>The fighters the planner reckons with (BATTLE.PRG's actor list, 0x800f1928): Ashley and every living fighter but the crates.</summary>
    private List<Combatant> Fighters(bool includeHidden = false)
    {
        var fighters = new List<Combatant>();
        if (Ashley != null && !Ashley.Dead) fighters.Add(Ashley);
        foreach (var c in Combatants.Values) if (c != Ashley && !c.Dead && !c.Cube && (includeHidden || c.Actor.Visible || c.HopState != 0 && (c.Actor.Flags & 1) != 0)) fighters.Add(c);
        return fighters;
    }

    /// <summary>
    /// Whether an enemy can make the action in limb slot <paramref name="slot"/> (BATTLE.PRG 0x800e4df8): the limb above
    /// 1 hit point and the cost met, magic points while not silenced, hit points leaving more than half, or its
    /// weapon's DP; paralysed, only an action costing magic points or of kind 2.
    /// </summary>
    private bool Usable(Combatant enemy, int slot, int action)
    {
        if (slot >= enemy.Limbs.Count || enemy.Limbs[slot].Hp < 2) return false;
        var record = _view.Content.Field.Action(action);
        if (enemy.Has(Paralysis) && record.CostKind is not (1 or 2)) return false;
        return record.CostKind switch
        {
            1 => enemy.Mp >= record.Cost && !enemy.Has(Silence),
            3 => enemy.Hp >= record.Cost + enemy.MaxHp / 2,
            4 => enemy.Blade.Dp >= record.Cost,
            _ => true,
        };
    }

    /// <summary>
    /// How an action picks its target (BATTLE.PRG 0x800e5710, by a bit of its record's target kind): 1 a target, 2 a
    /// target's limbs, 3 an area, 0 none; and whether it may not be made on its user (kinds 3, 4, 7, 8 and 14).
    /// </summary>
    private static (int Kind, bool NotSelf) Targeting(ActionRecord record)
    {
        int bit = 1 << record.TargetKind;
        return ((bit & 0xA) != 0 ? 1 : (bit & 0x40A0) != 0 ? 2 : (bit & 0x1954) != 0 ? 3 : 0, (bit & 0x4198) != 0);
    }

    /// <summary>
    /// Whether an action is harm (bit 15 of its planning flags) or help (bit 14) to a target (BATTLE.PRG 0x800dc484 and
    /// 0x800dc48c): action 0x60's harm and help trade places on a target of class 2.
    /// </summary>
    private static bool Harms(ActionRecord record, int action, Combatant target, bool help)
    {
        bool set = (record.Flags >> (help ? 14 : 15) & 1) != 0;
        return action == 0x60 && (target.Class & 7) == 2 ? !set : set;
    }

    /// <summary>
    /// Whether an enemy may make an action on a target (BATTLE.PRG 0x800e4cf4): an ally or foe; help only on an ally,
    /// and, unless its flags take any (0x100), one in a trouble it is for (bits 9 to 11: unable to act, short of magic,
    /// under half its hit points; 0x800e4f14); harm not on an ally; and not on itself when its kind may not.
    /// </summary>
    private bool Allowed(Combatant enemy, int action, Combatant target)
    {
        int relation = Relation(enemy, target);
        if (relation < 2) return false;
        var record = _view.Content.Field.Action(action);
        if (Harms(record, action, target, help: true))
        {
            if (relation >= 3) return false;
            int most = MostMagic(target);
            bool unable = (target.Status & 0x9E2B0) != 0 || most != 0 && target.Has(Silence);
            bool short_ = most != 0 && (target.Mp < most || target.Mp * 2 < target.MaxMp);
            bool weak = target.Hp * 2 < target.MaxHp;
            int troubles = (unable ? 1 : 0) | (short_ ? 2 : 0) | (weak ? 4 : 0);
            if ((record.Flags & 0x100) == 0 && (record.Flags >> 9 & troubles) == 0) return false;
        }
        if (Harms(record, action, target, help: false) && relation < 3) return false;
        return !(target == enemy && Targeting(record).NotSelf);
    }

    /// <summary>
    /// What a move would leave the fight at (BATTLE.PRG 0x800dcc94), as a plan entry's score: each fighter's standing
    /// (0x800dc424) after the move, reckoned by the same resolver as the move itself, unvaried (0x8008631c,
    /// <see cref="Resolve"/>): its user pays the cost (magic points, hit points, or for the weapon's own cost its DP;
    /// 0x800dc888) and each entry's hit and magic points, the weapon's and the shield's DP and statuses go on its
    /// fighter's standing; each fighter's <see cref="WellBeing"/> added for an ally and taken off for any other but one
    /// standing neither way; times 128, plus the weight: the chance the move has on its target, in 128ths (0x800dcbd8; 0
    /// when nothing came of it), or 128 for none. An action flagged 0x20 is passed over nine times in ten; one flagged
    /// 0x10, for an enemy that weighs those lightly, is not reckoned: it scores 1 over standing still, at weight 40.
    /// </summary>
    private int Score(Combatant enemy, int action, Combatant? target, int limb, List<Combatant> fighters, out int weight)
    {
        weight = 128;
        var record = action != 0 ? _view.Content.Field.Action(action) : default;
        if ((record.Flags & 0x20) != 0 && (_random.Randi() & 0x7F) < 115) return int.MinValue;
        var standings = new Dictionary<Combatant, Standing>();
        foreach (var f in fighters)
            standings[f] = new Standing
            {
                Hp = f.Hp, MaxHp = f.MaxHp, Mp = f.Mp, MaxMp = f.MaxMp, BladeDp = f.Blade.Dp, BladeMaxDp = f.Blade.MaxDp,
                ShieldDp = f.Shield.Dp, ShieldMaxDp = f.Shield.MaxDp, Statuses = f.Status,
            };
        int bonus = 0;
        if (enemy.WeighsLightly && (record.Flags & 0x10) != 0) { bonus = 1; weight = 40; }
        else if (action != 0 && target != null)
        {
            var result = Resolve(action, enemy, Targeting(record).Kind == 3 ? Reckoned(record, enemy, target, limb) : Request(record, target, limb), live: false);
            // 0x800dc30c: mode 2 adds, any other takes off, held between 0 and the most.
            static int Moved(int value, int most, int by, int mode) => Math.Clamp(mode == 2 ? value + by : value - by, 0, most);
            var user = standings[enemy];
            switch (record.CostKind)
            {
                case 1: user.Mp = Moved(user.Mp, user.MaxMp, record.Cost, 1); break;
                case 3: user.Hp = Moved(user.Hp, user.MaxHp, record.Cost, 1); break;
                case 6: user.BladeDp = Moved(user.BladeDp, user.BladeMaxDp, record.Cost, 1); break;
            }
            standings[enemy] = user;
            foreach (var entry in result.Self != null ? new List<Outcome>(result.Targets) { result.Self } : result.Targets)
            {
                if (entry.Void || !standings.TryGetValue(entry.Who, out var standing)) continue;
                standing.Hp = Moved(standing.Hp, standing.MaxHp, entry.Hp, entry.HpMode);
                standing.Mp = Moved(standing.Mp, standing.MaxMp, entry.Mp, entry.MpMode);
                standing.BladeDp = Moved(standing.BladeDp, standing.BladeMaxDp, entry.BladeDp, entry.BladeDpMode);
                standing.ShieldDp = Moved(standing.ShieldDp, standing.ShieldMaxDp, entry.Wear, entry.WearMode);
                standing.Statuses = (standing.Statuses | entry.Set) & ~entry.Clear;
                standings[entry.Who] = standing;
            }
            var mine = result.Targets.Find(entry => entry.Who == target && !entry.Void);
            int chance = mine?.Chance ?? 0;
            weight = (chance + chance / 4 + chance / 32) & 0xFF;
        }
        int total = 0;
        foreach (var f in fighters)
        {
            int relation = Relation(enemy, f);
            if (relation == 1) continue;
            int wellBeing = WellBeing(f, standings[f]);
            total += relation == 2 ? wellBeing : -wellBeing;
        }
        return (total + bonus) * 128 + weight;
    }

    /// <summary>
    /// Adds a move to an enemy's plan (BATTLE.PRG 0x800dc344): not past sixteen, nor one of weight 0; its target is
    /// marked as planned for its action.
    /// </summary>
    private void Propose(Combatant enemy, PlanEntry entry)
    {
        if (enemy.Plans.Count >= 16 || entry.Weight == 0) return;
        _planned.Add((entry.Target, entry.Action));
        enemy.Plans.Add(entry.Copy());
    }

    /// <summary>
    /// Keeps a move flagged 0x40 as a last resort (BATTLE.PRG 0x800dc4f0): once a limb slot and place, sixteen at most.
    /// </summary>
    private void KeepLastResort(PlanEntry entry)
    {
        if ((_view.Content.Field.Action(entry.Action).Flags & 0x40) == 0) return;
        int bit = 1 << (entry.Slot * 4 + entry.Index);
        if ((_lastResortSlots & bit) != 0 || _lastResorts.Count >= 16) return;
        _lastResortSlots |= bit;
        _lastResorts.Add(entry.Copy());
    }

    /// <summary>
    /// An enemy's moves as of <paramref name="horizon"/> (BATTLE.PRG 0x800dd918): every action it can make
    /// (<see cref="Usable"/>) on every living fighter it may (<see cref="Allowed"/>) and has not yet planned for it, each
    /// scored (<see cref="Score"/>) and planned when it beats standing still. One on a target scores once. One on a
    /// target's limbs scores on each limb it has: the best of those whose weight a roll of 0 to 127 falls under is
    /// planned if it beats standing still, else the best of them all if that does. One on an area scores once and is
    /// planned so likewise. Every one is offered as a last resort, a limb one at a limb the roll (the low two bits of a
    /// roll and its next two, added) picks or else its last.
    /// </summary>
    private void Propose(Combatant enemy, int horizon, List<Combatant> fighters)
    {
        int standStill = Score(enemy, 0, null, 0, fighters, out _);
        for (int slot = 0; slot < 6 && slot < enemy.Limbs.Count; slot++)
            for (int index = 0; index < 4; index++)
            {
                int action = enemy.Limbs[slot].Actions[index];
                if (action == 0 || !Usable(enemy, slot, action)) continue;
                var (kind, _) = Targeting(_view.Content.Field.Action(action));
                foreach (var target in fighters)
                {
                    if (target.Hp == 0 || !Allowed(enemy, action, target) || _planned.Contains((target, action))) continue;
                    var candidate = new PlanEntry { Action = action, Slot = slot, Index = index, Target = target, Horizon = horizon };
                    if (kind == 1)
                    {
                        candidate.Score = Score(enemy, action, target, 0, fighters, out candidate.Weight);
                        KeepLastResort(candidate);
                        if (candidate.Score > standStill) Propose(enemy, candidate);
                        continue;
                    }
                    if (kind is not (2 or 3)) continue;
                    PlanEntry? chosen = null, best = null;
                    int last = -1;
                    for (int limb = 0; limb < (kind == 2 ? 6 : 1); limb++)
                    {
                        if (kind == 2 && (limb >= target.Limbs.Count || target.Limbs[limb].MaxHp == 0)) continue;
                        last = limb;
                        candidate.Limb = kind == 2 ? limb : 0;
                        candidate.Score = Score(enemy, action, target, candidate.Limb, fighters, out candidate.Weight);
                        if ((_random.Randi() & 0x7F) < candidate.Weight && (chosen == null || chosen.Score < candidate.Score)) chosen = candidate.Copy();
                        if (best == null || best.Score < candidate.Score) best = candidate.Copy();
                    }
                    if (kind == 2)
                    {
                        int roll = (int)(_random.Randi() & 0xF), pick = (roll & 3) + (roll >> 2 & 1) + (roll >> 3 & 1);
                        candidate.Limb = pick < target.Limbs.Count && target.Limbs[pick].MaxHp != 0 || last < 0 ? pick : last;
                    }
                    KeepLastResort(candidate);
                    if (chosen != null && chosen.Score > standStill) Propose(enemy, chosen);
                    else if (best != null && best.Score > standStill) Propose(enemy, best);
                }
            }
    }

    /// <summary>
    /// Sorts plan entries from <paramref name="first"/> to <paramref name="last"/> by score, best first (BATTLE.PRG
    /// 0x800e6958, a quicksort pivoting on the middle entry).
    /// </summary>
    private static void SortPlans(List<PlanEntry> plans, int first, int last)
    {
        if (first >= last) return;
        int middle = (first + last) >> 1;
        (plans[first], plans[middle]) = (plans[middle], plans[first]);
        int kept = first;
        for (int i = first + 1; i <= last; i++)
            if (plans[first].Score < plans[i].Score) { kept++; (plans[kept], plans[i]) = (plans[i], plans[kept]); }
        (plans[first], plans[kept]) = (plans[kept], plans[first]);
        SortPlans(plans, first, kept - 1);
        SortPlans(plans, kept + 1, last);
    }

    /// <summary>
    /// Brings every fighter <paramref name="frames"/> on as an enemy reckons ahead (BATTLE.PRG 0x800de030), the clock
    /// <paramref name="clock"/> with it: from 2881 frames each limb mends 2 hit points; slowing ends unless both its
    /// slowing limbs are crippled; hit points come back a frame in 120 and magic points one in 180 (less one); poison
    /// takes and regeneration gives 5% of its hit points a turn of its period (<see cref="FieldContent.Status"/>), in
    /// seconds of 30 frames; and a status that would have run out by the clock ends.
    /// </summary>
    private void Project(List<Combatant> fighters, int frames, ref int clock)
    {
        clock += frames;
        int seconds = frames / 32 + frames / 512;
        foreach (var c in fighters)
        {
            if (clock >= 2881) foreach (var limb in c.Limbs) limb.Hp = Math.Min(limb.Hp + 2, limb.MaxHp);
            if (c.SlowingLimbs is { } pair && (pair.A < c.Limbs.Count && c.Limbs[pair.A].Hp >= 2 || pair.B < c.Limbs.Count && c.Limbs[pair.B].Hp >= 2)) c.Status &= ~0x10u;
            if (frames / 120 > 0) c.Hp = Math.Min(c.Hp - 1 + frames / 120, c.MaxHp);
            if (frames / 180 > 0) c.Mp = Math.Min(c.Mp - 1 + frames / 180, c.MaxMp);
            if (c.Has(Poison)) c.Hp = Math.Max(0, c.Hp - seconds / _view.Content.Field.Status(StatusBits[Poison]).Period * (c.MaxHp * 5) / 100);
            if (c.Has(Regen)) c.Hp = Math.Max(0, c.Hp + seconds / _view.Content.Field.Status(StatusBits[Regen]).Period * (c.MaxHp * 5) / 100);
            // A group's statuses end once its timer, a status tick every 30 frames, is past the clock (0x800de2f4).
            for (int group = 0; group < 5; group++)
            {
                uint mask = _view.Content.Field.StatusGroup(group);
                if ((c.Status & mask) != 0 && c.Timers[group] * 30 <= clock) c.Status &= ~mask;
            }
        }
    }

    /// <summary>
    /// Plans an enemy's moves (BATTLE.PRG 0x800de3e4). With every fighter as it stands kept aside, it looks ahead in three
    /// passes (<see cref="FieldContent.PlanPasses"/>: now, 455 and 2900 frames): each at least as far as it must wait
    /// (its cooldown, or its recovery at 35 a frame), every fighter brought on that far (<see cref="Project"/>), its
    /// moves proposed (<see cref="Propose(Combatant, int, List{Combatant})"/>) and sorted, and its best kept (10, then
    /// what of 13 the first left, then 3), the rest let go to be proposed again. With none, one that falls back on its
    /// last resorts plans those at weight 128. The fighters are put back; then, of two or more, one of the first pass's
    /// may go first: in turn, any whose weight (at most 76) a roll of 0 to 127 falls under, the rest sorted after it.
    /// Each after the first waits at least 80 frames on it, and each a further 0 to 7. Then they are checked
    /// (<see cref="Recheck"/>), and Guildenstern's final form's swoop heeds the plan's end (<see cref="Planned"/>).
    /// </summary>
    private void PlanMoves(Combatant enemy, List<Combatant> fighters)
    {
        enemy.Plans.Clear();
        _lastResorts.Clear();
        _lastResortSlots = 0;
        foreach (var limb in enemy.Limbs) foreach (int action in limb.Actions) _planned.RemoveWhere(mark => mark.Action == action);
        var kept = new List<(Combatant C, int Hp, int Mp, uint Status, int[] Limbs)>();
        foreach (var f in fighters) kept.Add((f, f.Hp, f.Mp, f.Status, f.Limbs.ConvertAll(l => l.Hp).ToArray()));
        int wait = Math.Max(enemy.Cooldown, enemy.Recovery / 35), clock = 0;
        var passes = _view.Content.Field.PlanPasses();
        var keep = new int[3];
        var chosen = new List<PlanEntry>[3];
        for (int pass = 0; pass < 3; pass++)
        {
            int horizon = Math.Max(passes[pass].Horizon, wait);
            Project(fighters, horizon, ref clock);
            Propose(enemy, horizon, fighters);
            SortPlans(enemy.Plans, 0, enemy.Plans.Count - 1);
            keep[pass] = Math.Min(enemy.Plans.Count, pass == 1 ? Math.Max(0, 16 - passes[2].Keep - keep[0]) : passes[pass].Keep);
            chosen[pass] = enemy.Plans.GetRange(0, keep[pass]);
            for (int i = keep[pass]; i < enemy.Plans.Count; i++) _planned.Remove((enemy.Plans[i].Target, enemy.Plans[i].Action));
            enemy.Plans.Clear();
        }
        foreach (var list in chosen) foreach (var entry in list) if (enemy.Plans.Count < 16) enemy.Plans.Add(entry);
        if (enemy.Plans.Count == 0 && enemy.LastResorts)
        {
            foreach (var entry in _lastResorts) { entry.Weight = 128; enemy.Plans.Add(entry); }
            SortPlans(enemy.Plans, 0, enemy.Plans.Count - 1);
        }
        foreach (var (c, hp, mp, status, limbs) in kept)
        {
            c.Hp = hp;
            c.Mp = mp;
            c.Status = status;
            for (int i = 0; i < limbs.Length; i++) c.Limbs[i].Hp = limbs[i];
        }
        if (enemy.Plans.Count >= 2)
            for (int pass = 0; pass < 3; pass++)
            {
                if (keep[pass] == 0) continue;
                for (int i = 0; i < keep[pass]; i++)
                {
                    long roll = _random.Randi() & 0x7F;
                    if (enemy.Plans[i].Action == 0 || roll >= Math.Min(enemy.Plans[i].Weight, 76)) continue;
                    (enemy.Plans[0], enemy.Plans[i]) = (enemy.Plans[i], enemy.Plans[0]);
                    SortPlans(enemy.Plans, 1, keep[pass] - 1);
                    break;
                }
                break;
            }
        for (int i = 0; i < enemy.Plans.Count; i++)
        {
            if (i > 0 && enemy.Plans[i].Horizon - enemy.Plans[0].Horizon < 80) enemy.Plans[i].Horizon = enemy.Plans[0].Horizon + 80;
            enemy.Plans[i].Horizon += (int)(_random.Randi() & 7);
        }
        Recheck(enemy);
        Planned(enemy);
    }

    /// <summary>
    /// Checks an enemy's plan as it stands (BATTLE.PRG 0x800d85d8, each AI tick): a move whose time has come that it may
    /// no longer make on its target, or can no longer make, is dropped (weight 0); if its first is dropped, the next
    /// still standing takes its place (staying in its own place too) and its state is chosen anew
    /// (<see cref="Choose"/>), and with none the plan is empty. Its plan phase (AI +0x131) follows: with no plan, 2
    /// while it has a foe and 3 otherwise; else 1 while its first is more than 30 frames off and 0 once nearer. Returns
    /// whether its first was dropped.
    /// </summary>
    private bool Recheck(Combatant enemy)
    {
        var mind = enemy.Mind!;
        bool dropped = false;
        foreach (var entry in enemy.Plans)
            if (entry.Weight != 0 && entry.Horizon <= _planClock && (!Allowed(enemy, entry.Action, entry.Target) || !Usable(enemy, entry.Slot, entry.Action)))
                entry.Weight = 0;
        if (enemy.Plans.Count > 0 && enemy.Plans[0].Weight == 0)
        {
            dropped = true;
            int next = enemy.Plans.FindIndex(1, entry => entry.Weight != 0);
            if (next < 0) enemy.Plans.Clear();
            else
            {
                enemy.Plans[0] = enemy.Plans[next];
                Choose(enemy);
            }
        }
        if (enemy.Plans.Count == 0)
        {
            mind.Phase = 3;
            foreach (var other in Fighters()) if (Relation(enemy, other) == 3) { mind.Phase = 2; break; }
        }
        else mind.Phase = _planClock + 30 < enemy.Plans[0].Horizon ? 1 : 0;
        return dropped;
    }

    /// <summary>
    /// Plans the fight anew (BATTLE.PRG 0x800dec88, after any move ends and as fighters join or leave): the clock goes
    /// back to 0; every enemy lets go of its chase and back-off latches and its pause, and the one that made
    /// <paramref name="acted"/> its 90 frames after a release, and builds its fallback move (<see cref="BuildFallback"/>,
    /// 0x800dd604); each heeds the action (<see cref="React"/>); and each
    /// hostile enemy plans its moves (<see cref="PlanMoves"/>), taking a state for its first (<see cref="Choose"/>), or
    /// state 7 with none, its converted mark shown again.
    /// </summary>
    private void Replan(Acted? acted)
    {
        _planClock = 0;
        foreach (var c in Fighters())
        {
            if (c.Mind is not { } mind || c == Ashley) continue;
            mind.FaceLatch = false;
            mind.Pause = 0;
            if (acted?.Caster == c) mind.AfterRelease = 0;
            BuildFallback(c);
        }
        React(acted);
        var fighters = Fighters();
        foreach (var enemy in fighters)
        {
            if (enemy == Ashley || !enemy.Hostile || enemy.Hp == 0 || enemy.Mind == null) continue;
            PlanMoves(enemy, fighters);
            if (enemy.Plans.Count == 0) enemy.Mind.State = 7;
            else Choose(enemy);
            if (enemy.Mind.Converted) Show(enemy, 0x80);
        }
        _planClock = 0;
    }

    /// <summary>
    /// An enemy's planned move, when it is a spell (54 to 140) or special attack (141 to 183, such as breaths and tail
    /// attacks), resolved and paid for as it set out (<paramref name="result"/>): the wind-up and release on its target,
    /// and what it was resolved to lands at the effect's hit. Anything else is an attack.
    /// </summary>
    private bool EnemySpell(Combatant enemy, int action, Combatant target, Resolution result)
    {
        if (action is < 54 or > 183) return false;
        var spell = _view.Content.Data("spells")["actions"].AsGodotDictionary().GetValueOrDefault(action.ToString(), new GDictionary()).AsGodotDictionary();
        if (spell.Count == 0) return false;
        enemy.Actor.Facing = enemy.Actor.FacingToward(target.Actor.Position);
        if (Play(enemy, 85, 6) == null) Play(enemy, 87, 6);
        // Its casting lasts as Ashley's does, while its name shows, sparkling in mode 2 for a spell and 3 for a
        // special attack (footage: the Dullahan's Drain Heart).
        _enemyCast = (enemy, spell, target, result, FieldView.AnnounceFrames + 9);
        _enemyChant = _view.PlayResident(56, enemy.Actor, enemy.Actor, mode: action <= 140 ? 2 : 3);
        HoldCamera(enemy, target, spell, null);
        _view.Announce(action <= 140 ? 23 : 24, spell["name"].AsString());
        return true;
    }

    private (Combatant Caster, GDictionary Spell, Combatant Target, Resolution Result, int Frames)? _enemyCast;

    /// <summary>
    /// Whether an enemy's spell or special attack is under way: the battle makes one action at a time, so Ashley's
    /// spells and arts wait for it (an enemy's waits on hers, <see cref="Gate"/>).
    /// </summary>
    public bool EnemyActing => _enemyCast != null || _enemyRelease != null;
    private FieldEffect? _enemyChant;

    /// <summary>The visual effect of the spell last released, which the HUD stays put away for.</summary>
    private FieldEffect? _castEffect;

    /// <summary>Puts the HUD away while an item's effect plays, as a spell's (footage: an Elixir of Queens' "Item Effect").</summary>
    public void HideHudFor(FieldEffect? effect)
    {
        if (effect == null) return;
        _castEffect = effect;
        StepActing();
    }

    /// <summary>Puts the HUD away while a spell is under way, and brings it back once its effect has run.</summary>
    private void StepActing()
    {
        bool casting = _phase == Phase.Casting && _artNumber < 0 && (_spellAction > 0 || _onSettled != null || _itemApply != null) || _enemyCast != null;
        bool playing = _castEffect != null && GodotObject.IsInstanceValid(_castEffect) && !_castEffect.Finished;
        if (!casting && !playing) _castEffect = null;
        // The camera goes back to Ashley once nothing is under way.
        if (!casting && !playing && _phase != Phase.Casting) _view.Gameplay.EndAction();
        if (_hud != null) _hud.Acting = casting || playing;
    }

    /// <summary>An enemy's spell under way: it is released after its wind-up.</summary>
    private void StepEnemyCast()
    {
        if (_enemyCast is not { } cast) return;
        if (cast.Frames > 1) { _enemyCast = cast with { Frames = cast.Frames - 1 }; return; }
        _enemyCast = null;
        if (_enemyChant != null && GodotObject.IsInstanceValid(_enemyChant)) _enemyChant.Mode = 0;
        _enemyChant = null;
        if (cast.Caster.Dead || cast.Target.Dead || Ashley == null || Ashley.Dead) return;
        if (Play(cast.Caster, 88, 4) == null) Play(cast.Caster, 89, 4);
        cast.Caster.Actor.Queue($"{cast.Caster.Set}_1", 6);
        // Its effect plays now; what it does lands at the effect's hit, or once the effect has run.
        var target = cast.Target;
        _castEffect = _enemyEffect = _view.PlayEffect(cast.Spell.GetValueOrDefault("visual", 0).AsInt32(), cast.Caster.Actor, target.Actor);
        _enemyRelease = (cast.Caster, cast.Result);
    }

    private FieldEffect? _enemyEffect;
    private (Combatant Caster, Resolution Result)? _enemyRelease;

    /// <summary>
    /// Whether what a spell or art does lands now: at its effect's hit (its timeline's action 0 or 1), or with no hit
    /// once the effect has run, or at once with none (footage: Heal's 59 shows as the camera comes back).
    /// </summary>
    private static bool Lands(FieldEffect? effect) => effect == null || !GodotObject.IsInstanceValid(effect) || effect.Finished || effect.Struck;

    /// <summary>An enemy's spell lands as <see cref="Lands"/> has it.</summary>
    private void StepEnemyRelease()
    {
        if (_enemyRelease is not { } release) return;
        if (!Lands(_enemyEffect)) return;
        _enemyRelease = null;
        _enemyEffect = null;
        if (release.Caster.Dead) return;
        Release(release.Result);
    }

    /// <summary>
    /// Sets the learned abilities to the buttons. With <paramref name="chain"/> and <paramref name="defense"/> the field's
    /// own arrays are taken and kept. A button keeps the ability it has while Ashley knows it; an empty one takes the
    /// next learned ability of its kind not on a button, chains (actions 22 to 39) and defenses (40 to 53) each filling
    /// Circle, Triangle and Square in turn.
    /// </summary>
    public void Assign(IEnumerable<int> learned, int[]? chain = null, int[]? defense = null)
    {
        if (chain != null) ChainSlots = chain;
        if (defense != null) DefenseSlots = defense;
        var known = new List<int>(learned);
        foreach (var (slots, low, high) in new[] { (ChainSlots, 22, 39), (DefenseSlots, 40, 53) })
        {
            for (int i = 0; i < 3; i++) if (!known.Contains(slots[i])) slots[i] = 0;
            foreach (int id in known)
            {
                if (id < low || id > high || Array.IndexOf(slots, id) >= 0) continue;
                int empty = Array.IndexOf(slots, 0);
                if (empty < 0) break;
                slots[empty] = id;
            }
        }
    }


    /// <summary>A limb's aim point: its bone, or the body's middle without one.</summary>
    private static Vector3 Aim(Combatant c, int limb) =>
        limb >= 0 && limb < c.Limbs.Count && c.Limbs[limb].Bone >= 0 ? c.Actor.BonePosition(c.Limbs[limb].Bone) : c.Actor.Position + new Vector3(0, -150, 0);

    /// <summary>
    /// Circle with the weapon drawn opens the attack sphere on what is in range: time stops, the room darkens, the
    /// sphere's wireframe shows Ashley's reach, and the target's parts list down the right of the screen while the
    /// system box reads out the chosen part (BATTLE.PRG's text table entry 20: the part and its "HP n/n%", then
    /// "TARGET:" with the attack's kind, damage type, affinity and the target's class). Left and right choose the
    /// enemy, up and down the part, Circle strikes it and Cross closes the sphere. With nothing in range the system box
    /// says so (field message 0) and time runs on.
    /// </summary>
    private void OpenSphere(FieldPlayer player)
    {
        _targets.Clear();
        foreach (var c in Combatants.Values)
        {
            // What the attack (target kind 14) takes: sides 1, 2, 4 and 0x10 (0x800E81D4), never Ashley herself.
            if (c == Ashley || !Takes(c, _view.Content.Field.TargetKind(_view.Content.Field.Action(1).TargetKind).Sides)) continue;
            // Its parts are the limbs the sphere reaches, whether or not its body is in (retail footage: the Minotaur,
            // well within reach, offers HEAD and R.LEG, or L.ARM, BODY and L.LEG; a soldier at the edge only L.ARM). A
            // character without limbs is one target, reached by its body.
            var parts = new List<int>();
            bool limbs = false;
            for (int limb = 0; limb < c.Limbs.Count; limb++)
                if (c.Limbs[limb].MaxHp > 0) { limbs = true; if (Reaches(Ashley!, Aim(c, limb))) parts.Add(limb); }
            if (!limbs && InSphere(Ashley!, c)) parts.Add(-1);
            if (parts.Count > 0) _targets.Add((c, parts));
        }
        // The crates the sphere reaches are targets too, each taken whole and named by its fighter record (Episode 4,
        // 6:01: "Cube", the stack's bottom crate lit): the crates' actors (<see cref="PlaceObjects"/>), whose stand-in
        // stands 150 under the crate's middle, where an actor without bones is aimed at and struck.
        PlaceObjects();
        foreach (var crate in _view.Crates)
        {
            var middle = new Vector3(crate.X * 128 + 64, crate.Bottom - 64, crate.Z * 128 + 64);
            if (_objects.Find(o => o.Actor.Id == crate.Id) is not { } cube || !Reaches(Ashley!, middle)) continue;
            _targets.Add((cube, new List<int> { -1 }));
        }
        // With nothing in range the sphere still shows, for as long as the message under CAUTION (retail footage).
        if (_targets.Count == 0) { _view.Message(_view.RetailMessage(0), header: 1, lines: 4); ShowRange(true); _emptySphere = 30; return; }
        player.Halt();
        Ashley!.Actor.Animate($"{Ashley.BattleSet}_1", 4);
        _target = 0;
        _part = 0;
        _phase = Phase.Sphere;
        SetFrozen(true);
        _view.DimRoom(true);
        ShowRange(true);
        ShowTarget();
    }

    /// <summary>A spell or item waiting on the sphere for its target, and the spell (null for an item).</summary>
    private Action<Combatant, int>? _aimed;
    private GDictionary? _aimSpell;
    private int _aimAction;

    /// <summary>
    /// Opens the sphere on what a spell or an item can be used on, each taken whole ("TARGET" down the right, the
    /// HUD's "#TARGET"): Circle hands the chosen one to <paramref name="confirm"/>, Cross puts the sphere away and
    /// nothing is spent. The readout is the target's name with its hit points and the spell's chance, or an item's
    /// chance alone ("Ashley Riot 100%"), then "TARGET: Single" with a spell's affinity and the target's class
    /// (footage: Heal's "Ashley Riot HP 59/100%", "TARGET: Single LIT/Human"; an Elixir's "TARGET: Single"). False,
    /// and nothing opened, while another action is under way.
    /// </summary>
    public bool AimAt(List<(Combatant Who, List<int> Parts)> candidates, GDictionary? spell, Action<Combatant, int> confirm, GDictionary? action = null, int spellAction = 0)
    {
        if (candidates.Count == 0 || Ashley == null || _phase != Phase.Free) return false;
        _targets.Clear();
        _targets.AddRange(candidates);
        _aimed = confirm;
        _aimSpell = spell;
        _aimAction = spellAction;
        _target = 0;
        _part = 0;
        _phase = Phase.Sphere;
        SetFrozen(true);
        _view.DimRoom(true);
        // The sphere is the action's own range (0x800707xx: its record's bytes 4 to 6, 32 units each).
        Vector3? radii = null;
        if ((action ?? spell)?.GetValueOrDefault("range", new Godot.Collections.Array()).AsGodotArray() is { Count: >= 3 } range)
            radii = new Vector3(range[0].AsInt32(), range[1].AsInt32(), range[2].AsInt32()) * 32;
        ShowRange(true, radii);
        ShowTarget();
        return true;
    }

    private void StepSphere(FieldPlayer player)
    {
        if (_pressed("ps_cross")) { _aimed = null; CloseSphere(); return; }
        if (_aimed != null)
        {
            if (_targets.Count > 1 && _pressed("ps_right")) { _target = (_target + 1) % _targets.Count; _part = 0; ShowTarget(); }
            if (_targets.Count > 1 && _pressed("ps_left")) { _target = (_target + _targets.Count - 1) % _targets.Count; _part = 0; ShowTarget(); }
            int count = _targets[_target].Parts.Count;
            if (count > 1 && _pressed("ps_down")) { _part = (_part + 1) % count; ShowTarget(); }
            if (count > 1 && _pressed("ps_up")) { _part = (_part + count - 1) % count; ShowTarget(); }
            if (!_pressed("ps_circle")) return;
            var act = _aimed;
            var (aimedAt, aimedParts) = _targets[_target];
            _aimed = null;
            CloseSphere();
            act(aimedAt, aimedParts[Math.Min(_part, aimedParts.Count - 1)]);
            return;
        }
        int before = _target * 64 + _part;
        if (_pressed("ps_right")) { _target = (_target + 1) % _targets.Count; _part = 0; }
        if (_pressed("ps_left")) { _target = (_target + _targets.Count - 1) % _targets.Count; _part = 0; }
        int parts = _targets[_target].Parts.Count;
        if (_pressed("ps_down")) _part = (_part + 1) % parts;
        if (_pressed("ps_up")) _part = (_part + parts - 1) % parts;
        if (_target * 64 + _part != before) ShowTarget();
        if (_pressed("ps_circle"))
        {
            var (who, chosen) = _targets[_target];
            int limb = chosen[_part];
            CloseSphere();
            _acted = new Acted(Ashley!, 1, who);
            // The attack is resolved, and its RISK paid, as it starts (BATTLE.PRG 0x80070f28).
            _links = 0;
            _exchanges = 1;
            _blow = Resolve(1, Ashley!, Request(_view.Content.Field.Action(1), who, limb), live: true);
            Strike(Ashley!, who, limb);
        }
    }

    // The parts' names in the text table (entries 1 to 15) for a humanoid's limbs in their record order: right arm,
    // left arm, head, body, legs.
    private static readonly int[] PartNames = { 1, 2, 0, 5, 3, 5 };

    /// <summary>
    /// The text table's part (0-based, of entries 1 to 15) naming a fighter's limb: its model part less one, or, when
    /// the rig does not say (an installation made before parts were kept), a humanoid's in record order; -1 for none.
    /// </summary>
    public static int PartNameIndex(Combatant who, int limb) =>
        limb < 0 || limb >= who.Limbs.Count ? -1 : who.Limbs[limb].Part > 0 ? who.Limbs[limb].Part - 1 : limb < PartNames.Length ? PartNames[limb] : -1;

    private string PartName(Combatant who, int limb)
    {
        var names = _view.Content.Data("battle_text")["parts"].AsGodotArray();
        int part = PartNameIndex(who, limb);
        return part >= 0 && part < names.Count ? names[part].AsGodotDictionary()["text"].AsString() : who.Name;
    }

    /// <summary>The chosen target's parts down the right and its readout in the system box.</summary>
    private void ShowTarget()
    {
        if (!_view.Content.Has("battle_text")) return;
        var text = _view.Content.Data("battle_text");
        var (who, parts) = _targets[_target];
        int limb = parts[_part];
        string Entry(string list, int index)
        {
            var entries = text[list].AsGodotArray();
            return index >= 0 && index < entries.Count ? entries[index].AsGodotDictionary()["text"].AsString() : "";
        }
        if (_aimed != null)
        {
            // A spell's or an item's target, whole (or the limb chosen, for a spell that takes limbs): its name, then its
            // hit points and the spell's chance or the item's chance alone; "Single", with a spell's affinity and the
            // target's class.
            bool byLimb = _aimAction != 0 && _view.Content.Field.TargetKind(_view.Content.Field.Action(_aimAction).TargetKind).Limbs;
            string chance = _aimSpell is { } spell
                ? SystemBox.Fill(text["hit_points"].AsGodotDictionary(), numbers: SpellReadout(_aimAction, who, limb) is var (points, spellOdds) ? new[] { points, spellOdds } : null)
                : SystemBox.Fill(text.GetValueOrDefault("item_chance", new GDictionary()).AsGodotDictionary(), numbers: new[] { 0, 100 });
            int affinity = 0;
            if (_aimSpell is { } cast) foreach (var effect in cast["effects"].AsGodotArray()) { affinity = Math.Max(0, effect.AsGodotDictionary()["affinity"].AsInt32() - 1); break; }
            _view.SystemBox.Open(4, 0);
            _view.SystemBox.Text = SystemBox.Fill(text["target"].AsGodotDictionary(), new[]
            {
                who.Name, chance, Entry("kinds", 1), "", _aimSpell != null ? Entry("affinities", affinity) : "", _aimSpell != null ? Entry("classes", who.Class & 7) : "",
            }).Trim(' ');
            string whole = text.GetValueOrDefault("whole_target", "#TARGET").AsString().TrimStart('#');
            _hud?.Targets(new List<string> { whole }, 0);
            _hud?.AimedAt(Aim(who, byLimb ? limb : -1), Project);
            // The whole target is lit (part -1 lights every vertex), cyan at once: an aimed target does not flash red
            // (footage: Ashley under the Elixir's sphere).
            Highlight(who, byLimb ? limb : -2);
            _flash = 0;
            return;
        }
        if (who.Cube)
        {
            // A crate: its name alone, then the blow (footage: "Cube", "TARGET: Single EGD/LIT"); every crate in reach
            // is a TARGET on its side of the screen, and the chosen one is lit at once, with no red flash.
            _view.SystemBox.Open(4, 0);
            _view.SystemBox.Text = SystemBox.Fill(text["target"].AsGodotDictionary(), new[]
            {
                who.Name, "", Entry("kinds", 1), Entry("damage", Ashley!.DamageType - 1), Entry("affinities", Ashley.Affinity), "",
            }).Trim(' ');
            string label = text.GetValueOrDefault("whole_target", "#TARGET").AsString().TrimStart('#');
            bool chosenSide = LeftOf(who);
            var same = new List<string>();
            var others = new List<string>();
            int index = 0;
            foreach (var (c, _) in _targets)
            {
                if (!c.Cube) continue;
                if (LeftOf(c) != chosenSide) { others.Add(label); continue; }
                if (c == who) index = same.Count;
                same.Add(label);
            }
            _hud?.Targets(chosenSide ? (others.Count > 0 ? others : null) : same, chosenSide ? -1 : index, chosenSide ? same : (others.Count > 0 ? others : null), chosenSide ? index : -1);
            _hud?.AimedAt(Aim(who, -1), Project);
            Highlight(who, -2);
            _flash = 0;
            return;
        }
        // The chosen part's own hit points, and the chance to hit it.
        // Its chance is the attack's preview (BATTLE.PRG 0x8008631c, unvaried; without Ashley's hidden 10).
        int odds = Resolve(1, Ashley, Request(_view.Content.Field.Action(1), who, limb), live: false).Targets[0].Chance;
        string hp = SystemBox.Fill(text["hit_points"].AsGodotDictionary(), numbers: new[] { limb >= 0 && limb < who.Limbs.Count ? who.Limbs[limb].Hp : who.Hp, odds });
        // A single blow (kind 1), its blade's damage type (1 blunt, 2 edged, 3 piercing), its affinity, the target's
        // class, in the information box.
        _view.SystemBox.Open(4, 0);
        _view.SystemBox.Text = SystemBox.Fill(text["target"].AsGodotDictionary(), new[]
        {
            PartName(who, limb), hp, Entry("kinds", 1), Entry("damage", Ashley!.DamageType - 1), Entry("affinities", Ashley.Affinity), Entry("classes", who.Class & 7),
        }).Trim(' ');
        // Each side of the screen lists the parts of an enemy standing on that side of Ashley: the chosen one's, and on
        // the other side the first other enemy there (retail footage); the chosen list has its part lit.
        bool chosenLeft = LeftOf(who);
        List<string>? other = null;
        foreach (var (c, otherParts) in _targets)
            if (c != who && LeftOf(c) != chosenLeft) { other = otherParts.ConvertAll(part => PartName(c, part)); break; }
        var mine = parts.ConvertAll(part => PartName(who, part));
        _hud?.Targets(chosenLeft ? other : mine, chosenLeft ? -1 : _part, chosenLeft ? mine : other, chosenLeft ? _part : -1);
        _hud?.AimedAt(Aim(who, limb), Project);
        Highlight(who, limb);
    }

    /// <summary>A crate as the actor it is (<see cref="PlaceObjects"/>, BATTLE.PRG 0x800765b0), or null when it has none.</summary>
    private Combatant? Crate(string id)
    {
        PlaceObjects();
        return _objects.Find(o => o.Actor.Id == id);
    }

    /// <summary>Whether a target stands left of Ashley on the screen (the side its parts are listed on); a crate by its middle.</summary>
    private bool LeftOf(Combatant c)
    {
        var camera = _view.Field?.Camera;
        if (camera == null || !camera.IsInsideTree()) return false;
        var at = c.Cube ? EventActor.RetailSpace * Aim(c, -1) : c.Actor.Node.GlobalPosition;
        return camera.UnprojectPosition(at).X < camera.UnprojectPosition(Ashley!.Actor.Node.GlobalPosition).X;
    }

    // Target mode lights the chosen part: an added pass over the target's model, cyan where a vertex's bone belongs
    // to the part (or everywhere, for a target taken whole), pulsing. As the sphere opens, the whole target first flashes red, fading over 24 frames (as the
    // footage shows it).
    private static Shader? _highlightShader;
    private EventActor? _highlighted;
    private int _flash;

    /// <summary>Lights <paramref name="limb"/> of <paramref name="who"/>'s model (none with a null target).</summary>
    private void Highlight(Combatant? who, int limb)
    {
        // A crate is drawn in grey and teal rather than with the added pass (see FieldView.LightCrate).
        if (who is { Cube: true })
        {
            if (_highlighted != null) SetHighlight(_highlighted, null);
            _highlighted = null;
            _view.LightCrate(who.Actor.Id, 1);
            return;
        }
        _view.LightCrate(null, 0);
        if (_highlighted != null && _highlighted != who?.Actor) SetHighlight(_highlighted, null);
        if (who != null && _highlighted == null) _flash = 24;
        _highlighted = who?.Actor;
        if (who == null) return;
        var limbs = _view.Content.Character(who.Actor.Character).GetValueOrDefault("bone_limbs", new Godot.Collections.Array()).AsGodotArray();
        SetHighlight(who.Actor, (limbs, limb + 1));
    }

    private static void SetHighlight(EventActor actor, (Godot.Collections.Array Limbs, int Part)? lit)
    {
        _highlightShader ??= new Shader { Code = "shader_type spatial; render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_back; uniform int bind_limb[64]; uniform int part; uniform float pulse; uniform float flash; varying float lit; void vertex() { lit = part < 0 || bind_limb[min(int(BONE_INDICES.x), 63)] == part ? 1.0 : 0.0; } void fragment() { ALBEDO = vec3(0.15, 0.55, 0.6) * pulse * lit * (1.0 - flash) + vec3(0.95, 0.05, 0.05) * flash; ALPHA = 1.0; }" };
        foreach (var node in actor.Node.FindChildren("*", "MeshInstance3D", true, false))
        {
            var instance = (MeshInstance3D)node;
            if (instance.Mesh == null) continue;
            for (int surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
            {
                if (instance.Mesh.SurfaceGetMaterial(surface) is not BaseMaterial3D material) continue;
                if (lit is not { } shown) { material.NextPass = null; continue; }
                // A skin's bind order is not the skeleton's: each bind names its bone ("b" and its number).
                var binds = new int[64];
                var skeleton = instance.GetNodeOrNull<Skeleton3D>(instance.Skeleton);
                if (instance.Skin is { } skin)
                    for (int b = 0; b < skin.GetBindCount() && b < 64; b++)
                    {
                        string name = skin.GetBindName(b).ToString();
                        if (name.Length == 0 && skeleton != null && skin.GetBindBone(b) is >= 0 and var index && index < skeleton.GetBoneCount()) name = skeleton.GetBoneName(index);
                        // Only a limb's first bone is marked; the bones below it are the limb's too.
                        int index2 = skeleton?.FindBone(name) ?? -1;
                        while (index2 >= 0 && skeleton != null)
                        {
                            string held = skeleton.GetBoneName(index2);
                            if (held.StartsWith('b') && int.TryParse(held[1..], out int bone) && bone < shown.Limbs.Count && shown.Limbs[bone].AsInt32() != 0) { binds[b] = shown.Limbs[bone].AsInt32(); break; }
                            index2 = skeleton.GetBoneParent(index2);
                        }
                    }
                var pass = material.NextPass as ShaderMaterial ?? new ShaderMaterial { Shader = _highlightShader };
                pass.SetShaderParameter("bind_limb", binds);
                pass.SetShaderParameter("part", shown.Part);
                pass.SetShaderParameter("flash", 1.0f);
                material.NextPass = pass;
            }
        }
    }

    /// <summary>The lit part pulses on the 30 Hz clock.</summary>
    private void PulseHighlight()
    {
        float pulse = 0.6f + 0.4f * Mathf.Sin(_tick * Mathf.Tau / 16);
        if (_phase == Phase.Sphere && _target < _targets.Count && _targets[_target].Who is { Cube: true } crate) _view.LightCrate(crate.Actor.Id, pulse);
        if (_highlighted == null) return;
        if (_flash > 0) _flash--;
        float flash = _flash / 24f;
        // Ashley glows cyan with the target's red flash (retail footage).
        if (Ashley != null) _view.Glow(Ashley.Actor, flash, FieldView.AshleyGlow);
        foreach (var node in _highlighted.Node.FindChildren("*", "MeshInstance3D", true, false))
            if (((MeshInstance3D)node).Mesh is { } mesh)
                for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                    if (mesh.SurfaceGetMaterial(surface) is BaseMaterial3D { NextPass: ShaderMaterial pass }) { pass.SetShaderParameter("pulse", pulse); pass.SetShaderParameter("flash", flash); }
    }

    /// <summary>
    /// Puts BATTLE MODE away while the field menu's command list is open, as its title stands there; a window over the
    /// left of the screen (the gadget bag) leaves BATTLE MODE and hides the figure instead.
    /// </summary>
    /// <summary>Covers the HUD for a menu: wholly, or (a window over the field) only the figure and, under a titled page, BATTLE MODE.</summary>
    public void CoverHud(bool covered, bool window = false, bool banner = false)
    {
        if (_hud == null) return;
        _hud.Covered = covered && !window;
        _hud.FigureHidden = covered && window;
        _hud.BannerHidden = covered && window && !banner;
    }

    /// <summary>Ends an attack sphere an event cut short, so time runs again when Ashley is the player's.</summary>
    public void EndSphere()
    {
        if (_phase == Phase.Sphere) CloseSphere();
    }

    private void CloseSphere()
    {
        SetFrozen(false);
        _view.DimRoom(false);
        ShowRange(false);
        _view.SystemBox.Text = "";
        _hud?.Targets(null, 0);
        _hud?.AimedAt(null, null);
        Highlight(null, 0);
        if (Ashley != null) _view.Glow(Ashley.Actor, 0, FieldView.AshleyGlow);
        _phase = Phase.Free;
    }

    private MeshInstance3D? _range;

    /// <summary>
    /// The attack sphere's wireframe about Ashley: her reach's ellipsoid (the one <see cref="InSphere"/> tests),
    /// turned with her and centred on her body, in four great circles through its poles and five parallels of green
    /// lines (about as many as retail's show) drawn over the
    /// scene.
    /// </summary>
    /// <summary>Frames left of the sphere shown with nothing in range.</summary>
    private int _emptySphere;

    private void ShowRange(bool shown, Vector3? radii = null)
    {
        if (!shown || Ashley == null) { if (_range != null) _range.Visible = false; return; }
        if (_range == null)
        {
            _range = new MeshInstance3D { Name = "AttackSphere", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            // Its lines are sorted by depth with the room (they go into the ordering table as its polygons do), so a pillar
            // nearer the camera hides them (footage: the Elixir's sphere behind the cellar's column).
            _range.MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Color.Color8(0x30, 0x98, 0x38), RenderPriority = 100 };
            Ashley.Actor.Node.GetParent().AddChild(_range);
        }
        // A sphere newly shown grows open: its scale is the cosine of 64 n (of 4096 a turn) as n counts down from 15
        // a frame (BATTLE.PRG 0x800C05EC sets it, the sphere's draw scales by it).
        if (!_range.Visible) _rangeGrow = 15;
        _rangeRadii = radii ?? Ashley.Range;
        BuildRange();
    }

    // The sphere's radii (Ashley's reach, or a spell's or item's range: its record's bytes 4 to 6 times 32) and its
    // growth count.
    private Vector3 _rangeRadii;
    private int _rangeGrow;

    private void StepRange()
    {
        if (_range is not { Visible: true } || _rangeGrow == 0) return;
        _rangeGrow--;
        BuildRange();
    }

    private void BuildRange()
    {
        if (_range == null || Ashley == null) return;
        var mesh = new ImmediateMesh();
        var r = _rangeRadii * Mathf.Cos(_rangeGrow * 64 * Mathf.Tau / 4096);
        float facing = Ashley.Actor.Facing * Mathf.Tau / 4096f;
        var turn = new Basis(Vector3.Up, facing);
        var centre = Ashley.Actor.Position + new Vector3(0, -90, 0);
        Vector3 Point(float longitude, float latitude) =>
            EventActor.RetailSpace * (centre + turn * new Vector3(r.X * Mathf.Cos(latitude) * Mathf.Cos(longitude), r.Y * Mathf.Sin(latitude), r.Z * Mathf.Cos(latitude) * Mathf.Sin(longitude)));
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        const int Steps = 32;
        for (int m = 0; m < 4; m++)
            for (int i = 0; i < Steps; i++)
            {
                float lon = m * Mathf.Pi / 4;
                mesh.SurfaceAddVertex(Point(lon, i * Mathf.Tau / Steps));
                mesh.SurfaceAddVertex(Point(lon, (i + 1) * Mathf.Tau / Steps));
            }
        for (int p = -2; p <= 2; p++)
            for (int i = 0; i < Steps; i++)
            {
                float lat = p * Mathf.Pi / 6;
                mesh.SurfaceAddVertex(Point(i * Mathf.Tau / Steps, lat));
                mesh.SurfaceAddVertex(Point((i + 1) * Mathf.Tau / Steps, lat));
            }
        mesh.SurfaceEnd();
        _range.Mesh = mesh;
        _range.Visible = true;
    }

    /// <summary>Stops or restarts every character's animation while the attack sphere holds time.</summary>
    private void SetFrozen(bool frozen)
    {
        foreach (var actor in _view.Actors.Values)
            if (actor.Player != null) actor.Player.SpeedScale = frozen ? 0 : 1;
    }

    /// <summary>
    /// Starts an attack on a limb: the attacker faces the target and plays the attack for the limb's height (low
    /// below a pitch of -228, high from 172, level between); the hit lands halfway.
    /// </summary>
    /// <summary>The sounds the strike's animation plays, by the frames left when they do.</summary>
    private readonly List<(int At, string Sound)> _cues = new();

    /// <summary>An animation's script events ([frame, operation, operands…]), as the installer kept them.</summary>
    private Godot.Collections.Array Events(Combatant c, string clip) =>
        _view.Content.Character(c.Actor.Character)
            .GetValueOrDefault("events", new GDictionary()).AsGodotDictionary().GetValueOrDefault(clip, new Godot.Collections.Array()).AsGodotArray();

    private void Strike(Combatant attacker, Combatant target, int limb)
    {
        if (attacker == Ashley && target.Hostile) _view.Attacked();
        _attacker = attacker;
        _victim = target;
        _victimLimb = limb;
        _struck = false;
        _attempted = false;
        if (_chain == 0) _lastButton = -1;
        attacker.Actor.Facing = attacker.Actor.FacingToward(target.Actor.Position);
        var offset = Aim(target, limb) - (attacker.Actor.Position + new Vector3(0, -150, 0));
        float pitch = Mathf.Atan2(-offset.Y, new Vector2(offset.X, offset.Z).Length()) * 4096 / Mathf.Tau;
        int animation = pitch < -228 ? AttackLow : pitch >= 172 ? AttackHigh : Attack;
        string? set = Play(attacker, animation, 6, once: true);
        if (set == null) { animation = Attack; set = Play(attacker, Attack, 6, once: true); }
        // The attack is counted on the 30 Hz event clock, a tick to a SEQ frame. Its script says in which frame the
        // blow lands (operation 2) and its sounds play (4 and 5; 88 is the weapon's swing); without one, the blow
        // lands halfway through.
        const int Ticks = Installation.CharacterConversion.TicksPerFrame;
        _frames = Math.Max(2, (int)Math.Ceiling((attacker.Actor.Player?.CurrentAnimationLength ?? 1) * Installation.CharacterConversion.FramesPerSecond * Ticks));
        _landAt = _frames / 2;
        _cues.Clear();
        foreach (var value in Events(attacker, $"{set}_{animation}"))
        {
            var entry = value.AsGodotArray();
            int frame = entry[0].AsInt32() * Ticks, op = entry[1].AsInt32();
            if (op == 2) _landAt = Math.Clamp(_frames - frame, 0, _frames - 1);
            if (op is 4 or 5 && entry.Count > 2)
            {
                int sound = entry[2].AsInt32();
                if (sound == 88)
                {
                    var swings = _view.Content.SwingSounds;
                    int category = attacker.Drawn ? attacker.Category : 0;
                    sound = category < swings.Length ? swings[category] : 0;
                }
                if (sound > 0) _cues.Add((_frames - frame, $"effect_{sound}_p128"));
            }
        }
        _phase = Phase.Striking;
    }

    private void StepStrike()
    {
        if (_attacker == null || _victim == null) { _phase = Phase.Free; return; }
        _frames--;
        for (int i = _cues.Count - 1; i >= 0; i--)
            if (_frames <= _cues[i].At) { _view.PlayActorSound(_attacker.Actor, _cues[i].Sound); _cues.RemoveAt(i); }
        if (!_struck && _frames <= _landAt)
        {
            _struck = true;
            Land(_attacker, _victim);
            if (_phase != Phase.Striking) return;
        }
        if (StepAbilities()) return;
        if (_frames > 0) return;
        _chain = 0;
        _lastButton = -1;
        if (_attacker == Ashley) AshleyActed?.Invoke();
        Play(_attacker, 1, 6);
        _attacker = _victim = null;
        _phase = Phase.Free;
    }

    /// <summary>
    /// A blow lands: what it was resolved to as it started (<see cref="_blow"/>) is applied to its target, with the
    /// blow's flash at the struck limb, a miss shown, the flinch from the front or back; the hit dealt or taken is what
    /// chain and defense abilities answer.
    /// </summary>
    private void Land(Combatant attacker, Combatant target)
    {
        var blow = _blow;
        _blow = null;
        // The blow's entry; its line of fire may have turned it onto another fighter or a crate.
        if (blow == null || blow.Targets.Count == 0) return;
        var entry = blow.Targets[0];
        target = entry.Who;
        target.Engaged = true;
        if (entry.Void) { _view.PlayResident(0x27, attacker.Actor, target.Actor); return; }
        // A crate takes no number (0x80087578) and does not move. One whose record has +0x954 bit 16 (every kind but 2
        // and 4) has its blow's result cleared (0x80085f0c): the flash and nothing else (footage, 6:02). A kind 2 or 4
        // crate is always hit for 999 (0x8007f28c, 0x800838ec) and breaks (0x80074a20).
        if (target.Cube)
        {
            _view.PlayResident(1, attacker.Actor, target.Actor);
            if (entry.Kind != 4) { target.Hp = 0; _view.BreakCrate(target.Actor.Id); }
            return;
        }
        int limb = entry.Limb;
        var first = blow.Record.Effects[0];
        _blowAction = blow.Record.Id;
        _blowAffinity = first.Affinity != 0 ? first.Affinity - 1 : attacker.Affinity;
        _landed = false;
        // The blow's flash, a resident small effect at the struck limb (BATTLE.PRG 0x8006c84c): 1 for a hit, 0x27 otherwise.
        int bone = limb >= 0 && limb < target.Limbs.Count ? target.Limbs[limb].Bone : -1;
        if (entry.Kind == 4) { Number(target, "Miss"); _view.PlayResident(0x27, attacker.Actor, target.Actor, bone); return; }
        _view.PlayResident(1, attacker.Actor, target.Actor, bone);
        _lastDamage = entry.HpMode is 1 or 3 ? entry.Hp : 0;
        _lastStruck = target;
        _landed = true;
        var toward = (attacker.Actor.Position - target.Actor.Position) with { Y = 0 };
        float facing = target.Actor.Facing * Mathf.Tau / 4096f;
        bool front = new Vector3(-Mathf.Sin(facing), 0, -Mathf.Cos(facing)).Dot(toward) >= 0;
        if (blow.Self is { Nothing: false } self) Apply(self);
        Apply(entry, attacker == Ashley ? blow.Record.Id : -1, front: front);
        if (target.Hp == 0) return;
        if (target == Ashley) { _player?.Halt(); _stagger = 15; }
        target.Timer = Math.Max(target.Timer, 30);
    }


    /// <summary>How an enemy moves: walking (and running), hopping from tile to tile, or flying.</summary>
    public enum Gait { Walk, Hop, Fly }

    /// <summary>
    /// Each enemy's balloon over its head, by the icon its AI shows (BATTLE.PRG's byte at 0x800f2260, set by
    /// <see cref="Show"/>, drawn by 0x80095c18): the rounded balloon with one, two then three dots (1 to 3) over a
    /// token's first 24 frames, then the jagged balloon with its mark (4), which also stands while the 90 frames after a
    /// release run; the alarm (6) and the searching mark (7). One won over (bit 7) shows its mark (5) unless a
    /// telegraph's balloon stands. Any actor with an icon has its balloon (0x8009627c draws every one of 1 to 15), at its
    /// head point (body +0x646 above its feet); they are drawn before the AI's tick, so each shows the last frame's icon.
    /// </summary>
    private void StepBalloons()
    {
        if (_hud == null) return;
        var shown = new List<(Vector3 Head, int Kind, int Side)>();
        var camera = _view.Field?.Camera;
        if (Ashley == null || camera == null || !camera.IsInsideTree()) { _hud.Balloons(shown, Project); return; }
        foreach (var enemy in Combatants.Values)
        {
            if (enemy.Dead || enemy == Ashley || enemy.Mind == null || enemy.Index is < 1 or > 15) continue;
            int icon = enemy.Mind.Shown, kind = (icon & 0x80) == 0 ? icon : (icon & 7) is >= 1 and <= 4 ? icon & 7 : 5;
            if (kind is < 1 or > 7) continue;
            // Its side turns once its head is 32 past Ashley's head across the screen (in the 320-wide screen's units).
            var head = enemy.Actor.Position - new Vector3(0, Crown(enemy), 0);
            float mine = camera.UnprojectPosition(EventActor.RetailSpace * head).X, hers = camera.UnprojectPosition(EventActor.RetailSpace * (Ashley.Actor.Position - new Vector3(0, Crown(Ashley), 0))).X;
            float apart = (mine - hers) / Math.Max(1, _view.GetViewport().GetVisibleRect().Size.Y / FieldView.VisibleLines);
            if (apart > 32) enemy.BalloonSide = 0;
            if (apart < -32) enemy.BalloonSide = 1;
            shown.Add((head, kind, enemy.BalloonSide));
        }
        _hud.Balloons(shown, Project);
    }

    /// <summary>A character's head point above its feet (body +0x646, its model header's u16 at 0x5e).</summary>
    private int Crown(Combatant c) => _view.Content.Character(c.Actor.Character).GetValueOrDefault("crown", 0).AsInt32();

    /// <summary>A map point's place on the window, or null behind the camera.</summary>
    private Vector2? Project(Vector3 point) => _view.Field?.Camera is { } c && c.IsInsideTree() && !c.IsPositionBehind(EventActor.RetailSpace * point) ? c.UnprojectPosition(EventActor.RetailSpace * point) : null;

    public void Number(Combatant? target, string text, Vector3? position = null)
    {
        // A number of damage is the HUD's large digits over the target (footage: grey for both sides, each digit
        // dropping into place from the left, held until it goes after about 32 frames).
        var parent = target?.Actor.Node.GetParent() ?? _view.Field?.Geometry;
        if (parent == null) return;
        var at = position ?? target!.Actor.Position + new Vector3(0, -150, 0);
        if (int.TryParse(text, out int value) && value >= 0)
        {
            _popups.Add((at, value, 0));
            return;
        }
        var label = new Label3D
        {
            Text = text, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FontSize = 48, OutlineSize = 12,
            Modulate = target == Ashley ? new Color(1, 0.5f, 0.5f) : Colors.White,
        };
        parent.AddChild(label);
        label.GlobalPosition = EventActor.RetailSpace * at;
        _numbers.Add((label, 40));
    }

    /// <summary>Ages the damage numbers by a frame, rising as they go; each lasts 40 frames.</summary>
    public void StepNumbers()
    {
        for (int i = _popups.Count - 1; i >= 0; i--)
        {
            var (at, value, age) = _popups[i];
            if (++age >= PopupFrames) _popups.RemoveAt(i);
            else _popups[i] = (at, value, age);
        }
        _hud?.Numbers(_popups, Project);
        _hud?.StepLabels();
        for (int i = _numbers.Count - 1; i >= 0; i--)
        {
            var (label, frames) = _numbers[i];
            if (--frames <= 0 || !GodotObject.IsInstanceValid(label)) { if (GodotObject.IsInstanceValid(label)) label.QueueFree(); _numbers.RemoveAt(i); continue; }
            label.Position += new Vector3(0, 0.01f, 0);
            label.Modulate = label.Modulate with { A = Math.Min(1, frames / 10f) };
            _numbers[i] = (label, frames);
        }
    }

    /// <summary>
    /// The vertical blanks gone since the battle's clocks last ran: they run once a retail frame, which takes
    /// <see cref="FieldView.FrameStep"/> blanks ([0x8005E24C]) where a frame of the port takes 2.
    /// </summary>
    private int _blanks;
    /// <summary>The frames the sphere has been up after the one it opened on (the act clock, set to 0 as the action is launched, first runs on the second, see <see cref="Clocks"/>).</summary>
    private int _sphereUp;

    /// <summary>
    /// Runs the battle's clocks for this frame as BATTLE.PRG's main loop does (0x8007a408 to 0x8007a4a0), by the
    /// battle's mode as the frame begins: not on the frame Ashley opens her sphere (the launch at 0x80079d88 sets the
    /// loop's skip, sp+32); in play (mode 1, [0x800F196C]) the battle clock (<see cref="Recover"/>, via 0x80088b6c);
    /// while an action is under way (mode 2, from the sphere's opening through the action's end, 0x80072ec4) the act
    /// clock instead (0x80088b8c), which only counts while the sphere is up ([0x800F18F0] 1 to 3; the sphere's first
    /// frame after its opening still has 0 there).
    /// </summary>
    private void Clocks(bool opening)
    {
        // An item's or a room spell's effect (Eureka, Fixate) is an action under way as well (<see cref="HideHudFor"/>).
        bool acting = _phase is Phase.Striking or Phase.Casting || EnemyActing || _castEffect != null && GodotObject.IsInstanceValid(_castEffect) && !_castEffect.Finished;
        if (_phase != Phase.Sphere) _tick++;
        if (_phase == Phase.Sphere) { if (++_sphereUp == 1) _view.ActClock = 0; }
        else _sphereUp = 0;
        _blanks += 2;
        int step = _view.FrameStep;
        if (_blanks < step) return;
        _blanks = 0;
        if (opening) return;
        if (_phase == Phase.Sphere) { if (_sphereUp >= 2) ActClock(step); return; }
        if (!acting) Recover(step);
    }

    /// <summary>
    /// The act clock (BATTLE.PRG 0x80088b8c, [0x800E8498], 0 to 29, set to 0 as an action is launched, 0x80088ca0):
    /// while Ashley holds her sphere open with status bit 3 (her crippled body) and the action is hers, not a trap's
    /// (state +0x4c 0), her RISK rises 1 (to 100) each time it is 0; then it runs on by the frame's step.
    /// </summary>
    private void ActClock(int step)
    {
        if (Ashley != null && (Ashley.Status & 8) != 0 && _view.ActClock == 0 && Ashley.Risk < 100) Ashley.Risk++;
        _view.ActClock = (_view.ActClock + step) % 30;
    }

    /// <summary>
    /// The battle clock's step for every fighter (BATTLE.PRG 0x80088554), read before it runs on by the frame's
    /// <paramref name="step"/> ([0x800E8490], 0 to 719; every 240 the next limb's turn to mend comes, [0x800E8494]):
    /// every 30, a fighter with hit points recovers: with its weapon drawn RISK falls 1 every 120, magic points rise 1
    /// and its weapon and shield each lose a PP every 180, and hit points (and the limb whose turn it is) rise 1 every
    /// 240; sheathed RISK falls 1 every 30 (every 120 with status bit 3, Ashley's crippled body), magic points rise every
    /// 60 and hit points every 120. Every 60 the status tick runs (<see cref="Tick"/>), and one at no hit points dies
    /// (0x800889d4). The clocks are BATTLE.PRG's own, 0 as it loads and never set again, so they run on from room to
    /// room (<see cref="FieldView.BattleClock"/>).
    /// </summary>
    private void Recover(int step)
    {
        if (Ashley == null || Ashley.Dead) return;
        int clock = _view.BattleClock, mending = _view.Mending;
        foreach (var c in new List<Combatant>(Combatants.Values))
        {
            if (clock % 30 != 0) break;
            if (c.Dead || c.Cube) continue;
            if (c.Hp != 0)
            {
                var limb = mending < c.Limbs.Count ? c.Limbs[mending] : null;
                bool drawn = c == Ashley ? Drawn : c.Drawn;
                if (drawn)
                {
                    if (clock % 120 == 0) c.Risk = Math.Max(0, c.Risk - 1);
                    if (clock % 180 == 0)
                    {
                        c.Restore(1);
                        c.Blade.Lose(1);
                        c.Shield.Lose(1);
                    }
                    if (clock % 240 == 0) { c.Heal(1); if (limb != null) limb.Hp = Math.Min(limb.MaxHp, limb.Hp + 1); }
                }
                else
                {
                    if ((c.Status & 8) == 0 || clock % 120 == 0) c.Risk = Math.Max(0, c.Risk - 1);
                    if (clock % 60 == 0) c.Restore(1);
                    if (clock % 120 == 0) { c.Heal(1); if (limb != null) limb.Hp = Math.Min(limb.MaxHp, limb.Hp + 1); }
                }
                if (clock % 60 == 0) Tick(c);
            }
            if (clock % 60 == 0 && c.Hp == 0) Die(c);
        }
        clock = (clock + step) % 720;
        if (clock % 240 == 0) _view.Mending = (mending + 1) % 6;
        _view.BattleClock = clock;
    }

    /// <summary>
    /// The status tick (BATTLE.PRG 0x80087ef4): each running group timer counts down, and at 0 its group's statuses end.
    /// Poison's or regeneration's turn counts down too: poison then takes a twentieth of the hit points and 0 to 5 more
    /// (at least 1; it can fell), every 2 ticks; regeneration gives a twentieth of the most less 2 and a bunched 0 to 4
    /// (no least), and each limb an eighth of that, every 5. Slow ends once a slowing limb is above 1 hit point, and
    /// Ashley's crippled-limb bits once their limb is.
    /// </summary>
    private void Tick(Combatant c)
    {
        var rules = _view.Content.Field;
        for (int group = 0; group < 5; group++)
        {
            if (c.Timers[group] == 0 || --c.Timers[group] != 0) continue;
            uint mask = rules.StatusGroup(group);
            for (int bit = 0; bit < 32; bit++) if ((mask >> bit & 1) != 0) c.ClearStatus(bit, rules);
        }
        if (c.Timers[5] != 0 && --c.Timers[5] == 0)
        {
            if ((c.Status & 0x4000) != 0)
            {
                c.Timers[5] = rules.Status(14).Period;
                int harm = c.Hp / 20 + Random(6);
                if (harm <= 0) harm = 1;
                c.Hp = c.Hp < harm ? 0 : c.Hp - harm;
                Number(c, harm.ToString());
            }
            else if ((c.Status & 0x20000) != 0)
            {
                c.Timers[5] = rules.Status(17).Period;
                int amount = c.MaxHp / 20 - 2 + Bell(5);
                c.Hp = Math.Clamp(c.Hp + amount, 0, c.MaxHp);
                foreach (var limb in c.Limbs) if (limb.MaxHp != 0) limb.Hp = Math.Clamp(limb.Hp + amount / 8, 0, limb.MaxHp);
            }
        }
        int Hp(int limb) => limb >= 0 && limb < c.Limbs.Count ? c.Limbs[limb].Hp : 0;
        if ((c.Status & 0x10) != 0 && c.SlowingLimbs is { } slowing && (Hp(slowing.A) >= 2 || Hp(slowing.B) >= 2)) c.ClearStatus(4, rules);
        if (c != Ashley || c.SlowingLimbs is not { A: not 0 }) return;
        for (int limb = 0; limb < 4; limb++)
            if (Hp(limb) >= 2) c.ClearStatus(rules.CrippledBit(limb), rules);
    }

    private void ShowStatus()
    {
        if (Ashley == null) return;
        _hud?.Set(Ashley.Hp, Ashley.MaxHp, Ashley.Mp, Ashley.MaxMp, Ashley.Risk, Drawn, Ashley.Limbs.ConvertAll(limb => (limb.Hp, limb.MaxHp)));
        _hud?.ShowStatuses(Ashley.Status);
        _status.Text = _hud == null ? $"HP {Ashley.Hp}/{Ashley.MaxHp}   MP {Ashley.Mp}/{Ashley.MaxMp}   RISK {Ashley.Risk}" + (Drawn ? "   BATTLE" : "") : "";
        if (GameOver) _status.Text += "   GAME OVER";
    }

    /// <summary>Removes the HUD and any damage numbers, when the room changes.</summary>
    public void Clear()
    {
        foreach (var (label, _) in _numbers) if (GodotObject.IsInstanceValid(label)) label.QueueFree();
        _numbers.Clear();
        _popups.Clear();
        _hud?.Numbers(_popups, Project);
        _status.QueueFree();
        _range?.QueueFree();
        _hud?.QueueFree();
    }
}
