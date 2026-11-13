// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GDictionary = Godot.Collections.Dictionary;
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
    /// (18). Statuses last 15 seconds, slowing and quickening 20; regeneration, poison and curse last until cured.
    /// </summary>
    public const int Degenerate = 1, Psychodrain = 2, Leadbones = 3, Slow = 5, Herakles = 6, Enlighten = 7, Invigorate = 8,
        Quicken = 10, Silence = 11, Regen = 13, Paralysis = 15, Poison = 16, Numbness = 17, Curse = 18;

    /// <summary>How long a status lasts, in 30 Hz frames, or -1 until cured.</summary>
    public static int Duration(int status) => status switch
    {
        Slow or Quicken => 20 * 30, Regen or Poison or Curse => -1, _ => 15 * 30,
    };


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

        /// <summary>Spends all its PP.</summary>
        public void Drain()
        {
            Pp = 0;
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
    }

    /// <summary>A limb: its hit points, agility bonus, type and affinity defences, armour, damage split and bone.</summary>
    public sealed class Limb
    {
        public int Hp, MaxHp, Agility, Bone = -1;

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
        public bool Targetable => Hostile && !Dead && !Suspended && Actor.Visible;
        /// <summary>How high its middle stands above its feet (y down), where its attack sphere is centred.</summary>
        public float Centre;
        /// <summary>Its weapon's range angle (the range record's fourth byte, its low three bits cleared), which pitches the sphere.</summary>
        public int Tilt;

        /// <summary>
        /// Takes its attack sphere from its weapon's range record (x, y, z and angle): each radius the range and its
        /// reach, 32 units a point (BATTLE.PRG 0x8006b3c0), and the angle's pitch.
        /// </summary>
        public void Reaching(Godot.Collections.Array range)
        {
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

        /// <summary>Statuses by effect id, with the frames each has left (-1 lasts until cured).</summary>
        public readonly Dictionary<int, int> Statuses = new();

        public bool Has(int status) => Statuses.ContainsKey(status);

        /// <summary>Restores up to <paramref name="amount"/> hit points, never past its most; returns what it restored.</summary>
        public int Heal(int amount) { int healed = Math.Clamp(amount, 0, MaxHp - Hp); Hp += healed; return healed; }

        /// <summary>Restores up to <paramref name="amount"/> magic points, never past its most; returns what it restored.</summary>
        public int Restore(int amount) { int restored = Math.Clamp(amount, 0, MaxMp - Mp); Mp += restored; return restored; }

        /// <summary>
        /// Sets <paramref name="status"/> for its whole <see cref="Duration"/> by its rules (<see cref="FieldContent.Status"/>;
        /// BATTLE.PRG 0x80081020): not while a status that keeps it off is set; the statuses it clears go (raising or
        /// lowering one strength, intelligence or agility clears the other five; poison clears regeneration); and one
        /// already set stays as it is unless it clears itself.
        /// </summary>
        public void Afflict(int status, FieldContent rules)
        {
            int bit = status > 0 && status < StatusBits.Length ? StatusBits[status] : -1;
            if (bit < 0) { Statuses[status] = Duration(status); return; }
            var (_, clears, blockedBy, _) = rules.Status(bit);
            foreach (int other in Statuses.Keys)
                if (other > 0 && other < StatusBits.Length && StatusBits[other] >= 0 && (blockedBy >> StatusBits[other] & 1) != 0) return;
            if (Statuses.ContainsKey(status) && (clears >> bit & 1) == 0) return;
            foreach (int other in new List<int>(Statuses.Keys))
                if (other > 0 && other < StatusBits.Length && StatusBits[other] >= 0 && (clears >> StatusBits[other] & 1) != 0) Statuses.Remove(other);
            Statuses[status] = Duration(status);
        }

        /// <summary>
        /// A cure effect: 27 paralysis, 28 poison, 29 curse, 30 numbness, 31 the magic cancel (every raising and
        /// lowering status), 32 paralysis, poison and numbness together. Returns whether it cured anything.
        /// </summary>
        public bool Cure(int effect)
        {
            int before = Statuses.Count;
            switch (effect)
            {
                case 27: Statuses.Remove(Paralysis); break;
                case 28: Statuses.Remove(Poison); break;
                case 29: Statuses.Remove(Curse); break;
                case 30: Statuses.Remove(Numbness); break;
                case 31: foreach (int status in new List<int>(Statuses.Keys)) if (status is <= 12 or >= 19) Statuses.Remove(status); break;
                case 32: foreach (int status in new[] { Paralysis, Poison, Numbness }) Statuses.Remove(status); break;
            }
            return Statuses.Count < before;
        }

        /// <summary>A stat with its statuses: a tenth less for a lowering one (or curse), a tenth more for a raising one.</summary>
        public int Scaled(int value, int lower, int raise) =>
            value * (100 - (Has(lower) ? 10 : 0) - (Has(Curse) ? 10 : 0) + (Has(raise) ? 10 : 0)) / 100;

        public int EffectiveStrength => Scaled(Strength, Degenerate, Herakles);
        public int EffectiveIntelligence => Scaled(Intelligence, Psychodrain, Enlighten);
        public int EffectiveAgility => Scaled(Agility, Leadbones, Invigorate);
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

    /// <summary>Whether the last blow landed, and the affinity it struck with, which defense abilities test.</summary>
    private bool _landed;
    private int _blowAffinity;
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
                hud.AddChild(_hud);
            }
            catch (System.IO.IOException) { _hud = null; }
            catch (System.IO.InvalidDataException) { _hud = null; }
        _status = new Label { Position = new Vector2(16, 12), Name = "Status" };
        _status.AddThemeColorOverride("font_outline_color", Colors.Black);
        _status.AddThemeConstantOverride("outline_size", 4);
        hud.AddChild(_status);
        if (_hud != null) _status.Position = new Vector2(16, 150);
    }

    /// <summary>
    /// Takes a character into battle with its record's statistics: its weapon, shield and accessory, and its limbs,
    /// each aimed at through the rig bone in <paramref name="limbBones"/>.
    /// </summary>
    public Combatant Enlist(EventActor actor, GDictionary stats, bool hostile, bool marked, bool drawn, Godot.Collections.Array? limbBones = null, Godot.Collections.Array? limbParts = null)
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
                profiles.Add((profile.AsGodotArray()[0].AsInt32(), profile.AsGodotArray()[1].AsInt32()));
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
            combatant.Mover = new FieldPlayer(actor, _view.FloorAt, _view.Stairs) { Crowded = Crowded, Climbs = false, Animates = false, WalkSpeed = combatant.Gait == Gait.Walk ? combatant.Speed : 0 };
            if (_view.FloorAt(actor.Position) is { } under) combatant.Altitude = under - actor.Position.Y;
        }
        combatant.Centre = _view.Content.Character(actor.Character).GetValueOrDefault("centre", -200).AsInt32() * Installation.CharacterConversion.ModelScale;
        combatant.Reaching(range);
        // Ashley fights by the set for her weapon's category; others by their one battle set.
        if (actor.Id == "slot:0" && actor.HasClip($"bt{combatant.Category:X}_1")) combatant.BattleSet = $"bt{combatant.Category:X}";
        combatant.Set = drawn ? combatant.BattleSet : "base";
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
        Combatants[actor.Id] = combatant;
        if (actor.Id == "slot:0") Ashley = combatant;
        Awaken(combatant, (uint)ai, stats);
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
        c.DamageType = Math.Clamp(Int(blade, "damage_type"), 1, 3);
        c.Cost = Int(blade, "cost");
        c.Reaching(blade.GetValueOrDefault("range", new Godot.Collections.Array { 3, 2, 3 }).AsGodotArray());
        var gear = Gear.From(blade);
        gear.Types = Gear.From(grip).Types;
        gear.Strength = c.WeaponStrength;
        gear.Agility = c.WeaponAgility;
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
        foreach (var c in Combatants.Values) if (c.Hostile && !c.Dead && !c.Suspended) count++;
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
        if (_phase != Phase.Sphere) Recover();
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
        // The fight is planned anew as a move ends and as the hostile come and go (BATTLE.PRG 0x800dec88).
        bool acting = _phase is Phase.Striking or Phase.Casting || _enemyCast != null || _enemyRelease != null;
        int hostile = 0;
        foreach (var c in Combatants.Values) if (c != Ashley && c.Hostile && !c.Dead) hostile++;
        if (!acting && _wasActing) Ended();
        else if (!acting && hostile != _hostileCount) Replan(null);
        _wasActing = acting;
        if (!acting) _hostileCount = hostile;
        if (_phase is Phase.Free or Phase.Drawing or Phase.Sheathing) { StepEnemies(); StepEnemyCast(); }
        StepEnemyRelease();
        // The balloons are put away while the sphere is open (BATTLE.PRG 0x8009627C draws none while its flag is set).
        StepRange();
        if (_phase != Phase.Sphere) StepBalloons();
        else _hud?.Balloons(new(), _ => null);
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

    private readonly HashSet<Combatant> _walking = new();

    /// <summary>How near two fighters' centres may come: their bodies' radii (FieldPlayer.Radius) together, about.</summary>
    private const float Spacing = 96;

    /// <summary>Whether moving an actor to a position would bring it within <see cref="Spacing"/> of a living fighter it is not already closer to.</summary>
    public bool Crowded(EventActor mover, Vector3 next)
    {
        foreach (var other in Combatants.Values)
        {
            if (other.Actor == mover || other.Dead || !other.Actor.Visible) continue;
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
        var offset = point - (attacker.Actor.Position + new Vector3(0, attacker.Centre, 0));
        float angle = attacker.Actor.Facing * Mathf.Tau / 4096f;
        var local = new Basis(Vector3.Right, attacker.Tilt * 16 * Mathf.Tau / 4096f) * (new Basis(Vector3.Up, -angle) * offset);
        var r = attacker.Range;
        return local.X * local.X / (r.X * r.X) + local.Y * local.Y / (r.Y * r.Y) + local.Z * local.Z / (r.Z * r.Z) < 1;
    }

    /// <summary>Whether the attacker's sphere reaches any of the target's limbs (0x80071d54), or its middle when it has none.</summary>
    private static bool InSphere(Combatant attacker, Combatant target)
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

    /// <summary>A fighter's agility with what it wears: accessory, every limb's armour, and its weapon and shield while drawn.</summary>
    private static int Agility(Combatant c)
    {
        int agility = c.EffectiveAgility + c.Accessory.Agility;
        foreach (var limb in c.Limbs) agility += limb.Armour.Agility;
        if (c.Drawn) agility += c.WeaponAgility + c.Shield.Agility;
        return agility;
    }

    /// <summary>
    /// The chance to hit a limb: the attacker's agility less the target's (with the limb's agility bonus), each
    /// scaled by what RISK leaves of it, plus 100, from 0 to 100; <paramref name="vary"/> adds the roll's 5 either way.
    /// </summary>
    public int HitChance(Combatant attacker, Combatant target, int limb, bool vary = false)
    {
        int attack = Agility(attacker) * (100 - attacker.Risk) / 100;
        int bonus = limb >= 0 && limb < target.Limbs.Count ? target.Limbs[limb].Agility : 0;
        int defence = (Agility(target) + bonus) * (100 - target.Risk) / 100;
        int chance = attack - defence + 100 + (vary ? _random.RandiRange(-5, 5) : 0);
        // A drawn weapon's gem of effect 1 adds 20 to the chance; a drawn shield's of effect 5 takes 20 off.
        if (attacker.Drawn && attacker.Blade.Has(1)) chance += 20;
        if (target.Drawn && target.Shield.Has(5)) chance -= 20;
        // Ashley's crippled left arm doubles an enemy's chance to hit her.
        if (target == Ashley && Crippled(target, 1)) chance *= 2;
        return Math.Clamp(chance, 0, 100);
    }

    /// <summary>
    /// Damage to a limb by the retail formula. The weapon's damage type and its strongest affinity set what it
    /// tests; with it drawn, its damage term is the grip's value for that type, its affinity and its class value
    /// against the target, less its wear, a quarter of which, plus 100, scales the attacker's strength (with its
    /// weapon and accessory). The defence is the target's strength above 20 scaled by the limb's type and affinity
    /// defences, plus the limb armour's strength scaled by its class, type and affinity defences (less a quarter of
    /// its wear), and the shield's likewise while drawn, all divided by 100 plus half the target's RISK. The damage is
    /// the difference, never below 0, plus 1 to 5; it doubles on a critical, a RISK/10 + 10 percent chance.
    /// </summary>
    /// <param name="type">A damage type to use instead of the weapon's (a monster's special attack names its own).</param>
    /// <param name="affinity">Likewise an affinity (0 to 7) instead of the attacker's own (<see cref="Combatant.Affinity"/>).</param>
    public int Damage(Combatant attacker, Combatant target, int limb, int type = 0, int affinity = -1)
    {
        if (type == 0) type = attacker.Drawn ? attacker.DamageType : 1;
        if (affinity < 0) affinity = attacker.Affinity;
        _blowAffinity = affinity;
        int strength = attacker.EffectiveStrength + attacker.Accessory.Strength + (attacker.Drawn ? attacker.WeaponStrength : 0);
        int term = attacker.Accessory.Types[type] + attacker.Accessory.Affinities[affinity] + attacker.Accessory.Classes[target.Class & 7];
        if (attacker.Drawn) term += attacker.Blade.Types[type] + attacker.Blade.Affinities[affinity] + attacker.Blade.Classes[target.Class & 7] - attacker.Blade.Worn;
        int attack = strength * (term / 4 + 100) / 100;
        var struck = limb >= 0 && limb < target.Limbs.Count ? target.Limbs[limb] : new Limb();
        int limbFactor = 100 + struck.Types[type] + struck.Affinities[affinity] + target.Accessory.Types[type] + target.Accessory.Affinities[affinity];
        var armour = struck.Armour;
        int armourFactor = 100 + armour.Classes[attacker.Class & 7] + armour.Types[type] + armour.Affinities[affinity] - armour.Worn / 4;
        var shield = target.Shield;
        int shieldFactor = target.Drawn && shield.Id != 0 ? 100 + shield.Classes[attacker.Class & 7] + shield.Types[type] + shield.Affinities[affinity] - shield.Worn / 4 : 0;
        int defence = ((target.EffectiveStrength + target.Accessory.Strength - 20) * limbFactor + shield.Strength * shieldFactor + armour.Strength * armourFactor) / (target.Risk / 2 + 100);
        // A weapon gem of effect 3 adds 5 percent to the difference; a shield gem of effect 7 takes 5 off.
        int scale = 100 + (attacker.Blade.Has(3) ? 5 : 0) - (target.Shield.Has(7) ? 5 : 0);
        int damage = Math.Max(0, attack - defence) * scale / 100 + 1 + _random.RandiRange(0, 4);
        if (_random.RandiRange(0, 99) < attacker.Risk / 10 + 10) damage *= 2;
        // Ashley's crippled right arm halves the damage she does.
        if (attacker == Ashley && Crippled(attacker, 0)) damage /= 2;
        return damage;
    }

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
        if (ticks == -2) _hud?.Cue(Ashley.Actor.Position + new Vector3(0, -210, 0), margin, Project);
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
                Apply(slots[i], chain);
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
    /// An ability's effects from its installed record (BATTLE.PRG 0x80085390: each effect's handler from the table at
    /// 0x800E8378, its amount from the reckoning table at 0x800E830C). The amount: 1 the previous effect's; 4, 6 and 18
    /// a tenth per factor of the last hit; 5 and 11 of the hit points or magic points Ashley lacks; 8 her weapon's PP;
    /// 22 of the hit taken, if it landed; 10 and 20 the same for a blow, 9, 19 and 21 for a spell; 23 her shield's PP.
    /// A chain's amounts add its links so far. The effect's prerequisite 3 falls on Ashley, any other on the other
    /// side: the enemy struck for a chain, the attacker for a defense. Then 58 takes hit points, 59 magic points, 62
    /// restores hit points (40 and 41 only against a spell or a blow, 42 to 48 only against their affinity, 49 only if
    /// the hit landed), 63 magic points, 64 the weapon's PP and 65 its DP; 60 and 61 spend all the weapon's or the
    /// shield's PP; 11, 15, 16 and 17 set silence, paralysis, poison or numbness and 27 and 30 cure paralysis or
    /// numbness. The timing runs on blows only, so no hit taken is a spell.
    /// </summary>
    private void Apply(int id, bool chain)
    {
        if (Ashley == null || _lastStruck == null) return;
        var ability = _view.Content.Data("abilities").GetValueOrDefault(id.ToString(), new GDictionary()).AsGodotDictionary();
        if (ability.Count == 0) return;
        Ashley.Risk = Math.Min(100, Ashley.Risk + ability["cost"].AsInt32());
        // Its flash: a resident small effect, on the one struck for a chain, on Ashley for a defense.
        int visual = ability.GetValueOrDefault("visual", 0).AsInt32();
        if (visual > 0) _view.PlayResident(visual, chain ? Ashley.Actor : _lastStruck.Actor, chain ? _lastStruck.Actor : Ashley.Actor);
        var other = chain ? _lastStruck : _attacker;
        const bool magic = false;
        int links = chain ? _chain : 0, previous = 0;
        foreach (var value in ability["effects"].AsGodotArray())
        {
            var effect = value.AsGodotDictionary();
            int kind = effect["effect"].AsInt32(), reckoning = effect["reckoning"].AsInt32(), factor = effect["factor"].AsInt32();
            var target = effect.GetValueOrDefault("prerequisite", 0).AsInt32() == 3 ? Ashley : other;
            if (target == null) continue;
            int amount = reckoning switch
            {
                1 => previous,
                4 or 6 or 18 => _lastDamage * factor / 10 + links,
                5 => (Ashley.MaxHp - Ashley.Hp) * factor / 10 + links,
                8 => Ashley.Blade.Pp + links,
                11 => (Ashley.MaxMp - Ashley.Mp) * factor / 10 + links,
                22 => _landed ? _lastDamage * factor / 10 : 0,
                10 or 20 => !magic && _landed ? _lastDamage * factor / 10 : 0,
                9 or 19 or 21 => 0,
                23 => Ashley.Shield.Pp,
                _ => 0,
            };
            previous = amount;
            switch (kind)
            {
                case 58:
                    if (amount <= 0) break;
                    if (chain) _lastDamage = amount;
                    Strike(target, amount, chain ? id : -1, flinch: false);
                    break;
                case 59:
                    target.Mp -= Math.Min(target.Mp, Math.Max(0, amount));
                    break;
                case 62:
                case 40 when magic:
                case 41 when !magic:
                case >= 42 and <= 48 when kind - 42 == _blowAffinity:
                case 49 when _landed:
                    if (amount > 0) Number(target, "+" + target.Heal(amount));
                    break;
                case 63:
                    target.Restore(amount);
                    break;
                case 64:
                    target.Blade.Gain(amount);
                    break;
                case 65:
                    target.Blade.Mend(amount);
                    break;
                case 60:
                    if (target.Blade.MaxPp > 0) target.Blade.Drain();
                    break;
                case 61:
                    if (_landed && target.Shield.MaxPp > 0) target.Shield.Drain();
                    break;
                case 11 or 15 or 16 or 17:
                    target.Afflict(kind, _view.Content.Field);
                    break;
                case 27 or 30:
                    target.Cure(kind);
                    break;
            }
        }
    }

    /// <summary>
    /// A Break Art (the menu's Break Arts): it needs the weapon drawn and more hit points than its cost, which it
    /// takes. It strikes the nearest enemy in Ashley's sphere: the wind-up is battle animation 91 (or 93), the release
    /// the art's own set for the target's height, and the hit is her attack's damage scaled by the art's factor.
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
            if (!c.Targetable || !InSphere(Ashley, c)) continue;
            if (aim == null || (c.Actor.Position - Ashley.Actor.Position).Length() < (aim.Value.Who.Actor.Position - Ashley.Actor.Position).Length())
                aim = (c, c.Limbs.FindIndex(limb => limb.MaxHp > 0));
        }
        if (aim is not { } target) return _view.RetailMessage(0);
        Ashley.Hp -= cost;
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
        bool heals = false;
        foreach (var effect in spell["effects"].AsGodotArray()) heals |= effect.AsGodotDictionary()["effect"].AsInt32() == 62;
        // The sphere opens on what it can take: Ashley for one that heals, else the enemies within its range, nearest
        // first; nothing is spent until a target is chosen.
        var candidates = new List<Combatant>();
        if (heals) candidates.Add(Ashley);
        else
        {
            var range = spell["range"].AsGodotArray();
            float reach = Math.Max(range[0].AsInt32(), range[2].AsInt32()) * 32f;
            foreach (var c in Combatants.Values)
                if (c.Targetable && (c.Actor.Position - Ashley.Actor.Position).Length() <= reach) candidates.Add(c);
            if (candidates.Count == 0) return _view.RetailMessage(0);
            candidates.Sort((a, b) => (a.Actor.Position - Ashley.Actor.Position).Length().CompareTo((b.Actor.Position - Ashley.Actor.Position).Length()));
        }
        player.Halt();
        if (!AimAt(candidates, spell, target => { confirmed?.Invoke(); BeginCast(id, spell, target, player); })) return "Not now.";
        return "";
    }

    /// <summary>A spell's cast once its target is chosen: its MP, the wind-up and its name announced.</summary>
    private void BeginCast(int id, GDictionary spell, Combatant target, FieldPlayer player)
    {
        if (Ashley == null) return;
        int cost = spell["cost"].AsInt32();
        if (Ashley.Mp < cost) return;
        Ashley.Mp -= cost;
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
            if (_artNumber >= 0) StrikeArt(_spell!, _spellTarget!);
            else Release(Ashley!, _spell!, _spellTarget!, play: false);
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

    /// <summary>An art's hit, at its effect's: it rolls to hit as an attack does, and deals the attack's damage scaled by its factor (a tenth each, over 100).</summary>
    private void StrikeArt(GDictionary art, Combatant target)
    {
        int factor = art["effects"].AsGodotArray()[0].AsGodotDictionary()["factor"].AsInt32();
        int limb = _victimLimb;
        if (_random.RandiRange(0, 99) >= Math.Min(100, HitChance(Ashley!, target, limb, vary: true) + 10)) { Number(target, "Miss"); return; }
        int damage = Damage(Ashley!, target, limb) * (factor * 10 + 100) / 100;
        Strike(target, damage, 184);
    }

    /// <summary>
    /// A spell's effects on its target. To hit (hit function 5): agility, each scaled by RISK, less the target's,
    /// plus 130 less the spell's cost, give or take 5; healing and help (2) always land. Damage (effect 58): the
    /// caster's intelligence (with weapon and accessory), scaled by a quarter of the weapon's affinity term plus 100
    /// and by the factor, less the target's intelligence above 20 scaled by its limb's type and affinity defences and
    /// its armour's, over 100 plus half its RISK; plus 1 to 5. Healing (62): the same shape on Ashley's own
    /// intelligence. A status effect (1 to 26) lands by its hit roll and is set for its duration.
    /// </summary>
    private void Release(Combatant caster, GDictionary spell, Combatant target, bool play = true)
    {
        if (play) _castEffect = _view.PlayEffect(spell.GetValueOrDefault("visual", 0).AsInt32(), caster.Actor, target.Actor);
        foreach (var value in spell["effects"].AsGodotArray())
        {
            var effect = value.AsGodotDictionary();
            int kind = effect["effect"].AsInt32();
            // Effects 38 and 37 clear the room's traps (flags 32 and 64; 0x80084340, 0x80084310).
            if (kind is 37 or 38) { _view.Flags[kind == 38 ? 32 : 64] = 1; continue; }
            var (hp, status, missed, shown, _) = Reckon(caster, spell, effect, target);
            if (missed) { if (shown) Number(target, "Miss"); continue; }
            if (status != 0) { target.Afflict(status, _view.Content.Field); Number(target, spell["name"].AsString()); continue; }
            if (kind == 62) { target.Hp += hp; Number(target, "+" + hp); continue; }
            if (hp >= 0) continue;
            Strike(target, -hp);
            if (target.Dead && target == Ashley) return;
        }
    }

    /// <summary>
    /// What one of a spell's effects does to its target, as <see cref="Release"/> describes: the hit points it restores
    /// (or takes, below 0), the status it sets, or a miss (shown, but not for a physical status's), with the chance it
    /// was rolled against (100 for what always lands; BATTLE.PRG keeps it with the result, 0x8007f3d0). An enemy
    /// reckons its moves ahead with these same rolls (<see cref="Score"/>).
    /// </summary>
    private (int Hp, int Status, bool Missed, bool Shown, int Chance) Reckon(Combatant caster, GDictionary spell, GDictionary effect, Combatant target)
    {
        int kind = effect["effect"].AsInt32(), factor = effect["factor"].AsInt32(), hit = effect["hit"].AsInt32();
        int type = Math.Clamp(effect["type"].AsInt32(), 1, 3), affinity = Math.Max(0, effect["affinity"].AsInt32() - 1);
        // Healing is reckoned on the one healed, give or take 5 (0x800829B8).
        if (kind == 62) return (Math.Min(target.MaxHp - target.Hp, Math.Max(0, SpellDamage(caster, effect, target, target.Struck) + _random.RandiRange(-5, 5))), 0, false, false, 100);
        if (kind is >= 1 and <= 26 && kind != 4 && kind != 9 && kind != 14)
        {
            // A status spell lands by its hit roll (help always does) and sets its status; a physical one (hit
            // function 3, a monster's special attack) by the attack's chance.
            // A drawn shield's gem that guards against the status takes 20 off its chance, which the always-landing
            // help spells otherwise have whole.
            int guard = Guarded(target, kind) ? 20 : 0;
            int chance = hit switch
            {
                3 => HitChance(caster, target, -1, vary: true) - guard,
                5 => Math.Clamp(Agility(caster) - Agility(target) + 130 - spell["cost"].AsInt32(), 0, 100) - guard,
                _ => 100 - guard,
            };
            if (_random.RandiRange(0, 99) >= chance) return (0, 0, true, hit != 3, chance);
            return (0, kind, false, false, chance);
        }
        if (kind != 58 || target == caster) return (0, 0, false, false, 100);
        // A spell on the whole target lands on its record's limb (the request's limb, 0x8007138C).
        int limb = target.Struck;
        if (hit == 3 && effect["reckoning"].AsInt32() == 2)
        {
            // A physical action (a monster's special attack): the attack's chance and damage by its own type and
            // affinity, scaled by its factor as a spell is.
            int chance = HitChance(caster, target, limb, vary: true);
            if (_random.RandiRange(0, 99) >= chance) return (0, 0, true, true, chance);
            return (-(Damage(caster, target, limb, type, affinity) * (factor * 10 + 100) / 100), 0, false, false, chance);
        }
        int odds = 100;
        if (hit == 5)
        {
            odds = Math.Clamp(Agility(caster) * (100 - caster.Risk) / 100 - Agility(target) * (100 - target.Risk) / 100 + 130 - spell["cost"].AsInt32() + _random.RandiRange(-5, 5), 0, 100);
            if (_random.RandiRange(0, 99) >= Math.Min(100, odds + (caster == Ashley ? 10 : 0))) return (0, 0, true, true, odds);
        }
        return (-(SpellDamage(caster, effect, target, limb) + 1 + _random.RandiRange(0, 4)), 0, false, false, odds);
    }

    /// <summary>
    /// A spell effect's strength from its caster (BATTLE.PRG's magic damage and healing calculators, 0x800819FC and
    /// 0x800829B8): intelligence with the accessory's (and the weapon's while drawn), scaled by a quarter of a term
    /// plus 100 (the weapon's affinity less the percent of its DP worn away plus its percent of PP, while drawn, and
    /// the accessory's type and affinity), and by the effect's factor.
    /// </summary>
    private static int SpellAttack(Combatant caster, GDictionary effect)
    {
        int type = Math.Clamp(effect["type"].AsInt32(), 0, 3), affinity = Math.Max(0, effect["affinity"].AsInt32() - 1);
        int intelligence = caster.EffectiveIntelligence + caster.Accessory.Intelligence + (caster.Drawn ? caster.Blade.Intelligence : 0);
        var blade = caster.Blade;
        int term = caster.Drawn ? (affinity != 0 ? blade.Affinities[affinity] : 0) - blade.Worn + (blade.MaxPp != 0 ? blade.Pp * 100 / blade.MaxPp : 0) : 0;
        term += (type != 0 ? caster.Accessory.Types[type] : 0) + caster.Accessory.Affinities[affinity];
        return intelligence * (term / 4 + 100) / 100 * (effect["factor"].AsInt32() * 10 + 100) / 100;
    }

    /// <summary>
    /// A damage or healing spell's amount before its variation: <see cref="SpellAttack"/> less the target's defence,
    /// its intelligence (with its accessory's) above 20 scaled by the limb's type and affinity defences and its
    /// accessory's, plus its armour's intelligence scaled by the armour's (less a quarter of the percent worn away) and,
    /// while its weapon is drawn, its shield's (plus a quarter of its percent of PP), all over 100 plus half its RISK;
    /// a type 0 effect meets no type defence. Healing is reckoned the same way on the one healed, so Ashley's own
    /// intelligence and armour take from her Heal (footage: 59 at 116 of 250).
    /// </summary>
    private static int SpellDamage(Combatant caster, GDictionary effect, Combatant target, int limb)
    {
        int type = Math.Clamp(effect["type"].AsInt32(), 0, 3), affinity = Math.Max(0, effect["affinity"].AsInt32() - 1);
        var struck = limb >= 0 ? target.Limbs[limb] : new Limb();
        int Factor(Gear gear) => (type != 0 ? gear.Types[type] : 0) + (affinity != 0 ? gear.Affinities[affinity] : 0);
        int stat = 100 + (type != 0 ? struck.Types[type] : 0) + (affinity != 0 ? struck.Affinities[affinity] : 0) + (type != 0 ? target.Accessory.Types[type] : 0) + target.Accessory.Affinities[affinity];
        int armour = 100 + Factor(struck.Armour) - struck.Armour.Worn / 4;
        int defence = (target.EffectiveIntelligence + target.Accessory.Intelligence - 20) * stat + struck.Armour.Intelligence * armour;
        if (target.Drawn)
        {
            var shield = target.Shield;
            defence += shield.Intelligence * (100 + Factor(shield) - shield.Worn / 4 + (shield.MaxPp != 0 ? shield.Pp * 100 / shield.MaxPp / 4 : 0));
        }
        defence /= target.Risk / 2 + 100;
        // Effect 4 in the weapon and 8 in the shield do for spells what 3 and 7 do for blows.
        int scale = 100 + (caster.Blade.Has(4) ? 5 : 0) - (target.Shield.Has(8) ? 5 : 0);
        return Math.Max(0, (SpellAttack(caster, effect) - defence) * scale / 100);
    }

    /// <summary>
    /// What the sphere's readout gives as a spell's "HP": the hit points it would restore or take before its
    /// variation (footage: Heal on Ashley at 116 of 250 reads "HP 59", and she is at 175 after it), else the target's own.
    /// </summary>
    private int SpellPoints(GDictionary spell, Combatant target)
    {
        foreach (var value in spell["effects"].AsGodotArray())
        {
            var effect = value.AsGodotDictionary();
            switch (effect["effect"].AsInt32())
            {
                case 62: return SpellDamage(Ashley!, effect, target, target.Struck);
                case 58 when target != Ashley: return SpellDamage(Ashley!, effect, target, target.Struck) + 1;
            }
        }
        return target.Hp;
    }

    /// <summary>
    /// A damage trap's blow (BATTLE.PRG's trap-panel reckoning): a tenth of the most hit points times the effect's
    /// factor, plus ten for each trap level and ten more, less a defence: (strength with the accessory's, less 20)
    /// times the limb's factor (100, or 100 plus the limb's defence against the damage type, plus its defence against
    /// the affinity and the accessory's), plus the limb's armour strength times its factor (likewise from the armour,
    /// less a quarter of the percent of its DP worn away), plus the shield's while the weapon is drawn (likewise, and
    /// plus a quarter of its PP percent), all over 100 plus half the RISK. With <paramref name="vary"/> it moves by
    /// up to 5 either way. It never fells: at most the hit points less one.
    /// </summary>
    public int TrapDamage(Combatant target, GDictionary effect, int level, int limb, bool vary)
    {
        int value = target.MaxHp / 10 * effect["factor"].AsInt32() + (level + 1) * 10;
        int type = Math.Clamp(effect["type"].AsInt32(), 0, 3), affinity = effect["affinity"].AsInt32() - 1;
        var part = limb >= 0 && limb < target.Limbs.Count ? target.Limbs[limb] : null;
        var armour = part?.Armour ?? new Gear();
        int statFactor = 100, armourFactor = 100, shieldFactor = 100, shieldStat = 0;
        if (type != 0)
        {
            statFactor = (part?.Types[type] ?? 0) + 100;
            armourFactor = armour.Types[type] + 100;
        }
        if (affinity is > 0 and < 8)
        {
            statFactor += part?.Affinities[affinity] ?? 0;
            armourFactor += armour.Affinities[affinity];
        }
        if (armour.MaxDp != 0) armourFactor -= (armour.MaxDp - armour.Dp) * 100 / armour.MaxDp >> 2;
        if (target.Drawn)
        {
            var shield = target.Shield;
            shieldStat = shield.Strength;
            if (type != 0) shieldFactor += shield.Types[type];
            if (affinity is > 0 and < 8) shieldFactor += shield.Affinities[affinity];
            if (shield.MaxDp != 0) shieldFactor -= (shield.MaxDp - shield.Dp) * 100 / shield.MaxDp >> 2;
            if (shield.MaxPp != 0) shieldFactor += shield.Pp * 100 / shield.MaxPp >> 2;
        }
        statFactor += target.Accessory.Types[type] + (affinity is >= 0 and < 8 ? target.Accessory.Affinities[affinity] : 0);
        int defence = ((target.EffectiveStrength + target.Accessory.Strength - 20) * statFactor + shieldStat * shieldFactor + armour.Strength * armourFactor) / (target.Risk / 2 + 100);
        int damage = Math.Max(0, value - defence);
        if (vary) damage = Math.Max(0, damage + _random.RandiRange(-5, 5));
        return Math.Min(damage, Math.Max(0, target.Hp - 1));
    }

    /// <summary>
    /// A trap's action on Ashley (<see cref="Installation.InventoryConversion.TrapActions"/>), at the room's trap
    /// level. Its damage is <see cref="TrapDamage"/>'s, on one of her limbs at random (how retail picks the limb was not
    /// read); a heal panel heals the same value before defence: a tenth of her most hit points times the effect's
    /// factor, plus ten for each level and ten more. A status trap sets its status (15 paralysis, 16 poison, 18 curse); the cure panels
    /// clear by <see cref="Combatant.Cure"/> (32 paralysis, poison and numbness, 29 curse).
    /// </summary>
    public void Trap(GDictionary action, int level)
    {
        if (Ashley is not { } ashley || ashley.Dead) return;
        _view.PlayEffect(action.GetValueOrDefault("visual", 0).AsInt32(), ashley.Actor, ashley.Actor);
        foreach (var value in action["effects"].AsGodotArray())
        {
            var effect = value.AsGodotDictionary();
            int kind = effect["effect"].AsInt32();
            int strength = ashley.MaxHp / 10 * effect["factor"].AsInt32() + (level + 1) * 10;
            switch (kind)
            {
                case 58:
                {
                    var limbs = new List<int>();
                    for (int l = 0; l < ashley.Limbs.Count; l++) if (ashley.Limbs[l].MaxHp > 0) limbs.Add(l);
                    int limb = limbs.Count > 0 ? limbs[_random.RandiRange(0, limbs.Count - 1)] : -1;
                    Strike(ashley, TrapDamage(ashley, effect, level, limb, true));
                    break;
                }
                case 62:
                {
                    Number(ashley, "+" + ashley.Heal(strength));
                    break;
                }
                case Paralysis or Poison or Curse:
                    ashley.Afflict(kind, _view.Content.Field);
                    break;
                case >= 27 and <= 32:
                    ashley.Cure(kind);
                    break;
            }
        }
        ShowStatus();
    }

    /// <summary>
    /// Whether the target's drawn shield holds a gem that guards against a status (BATTLE.PRG's defence gem bonus): 11
    /// Degenerate, 12 Psychodrain, 13 Leadbones, 14 Slow, 16 Silence, 18 Paralysis, 19 Poison and 21 Numbness.
    /// </summary>
    private static bool Guarded(Combatant target, int status) => target.Drawn && status switch
    {
        Degenerate => target.Shield.Has(11), Psychodrain => target.Shield.Has(12), Leadbones => target.Shield.Has(13), Slow => target.Shield.Has(14),
        Silence => target.Shield.Has(16), Paralysis => target.Shield.Has(18), Poison => target.Shield.Has(19), Numbness => target.Shield.Has(21),
        _ => false,
    };

    /// <summary>
    /// A blow's wear (BATTLE.PRG 0x800802c4 and 0x800803a4): the attacker's weapon and, while drawn, the target's
    /// shield lose the damage in DP, and each gains a tenth of it in PP, five percent more for a weapon with a gem of
    /// effect 9 and five percent less for a shield with one.
    /// </summary>
    private static void Wear(Combatant attacker, Combatant target, int damage)
    {
        attacker.Blade.Wear(damage);
        attacker.Blade.Gain(damage / 10 * (attacker.Blade.Has(9) ? 105 : 100) / 100);
        if (!target.Drawn) return;
        target.Shield.Wear(damage);
        target.Shield.Gain(damage / 10 * (target.Shield.Has(9) ? 95 : 100) / 100);
    }

    /// <summary>
    /// Damage off the hit points, its number shown (to at most 999), then death (70) at none (Ashley's ends the
    /// battle; an enemy's is counted as felled by the action <paramref name="felledBy"/>, -1 for none), or else a
    /// flinch from the front or back unless <paramref name="flinch"/> is false. Returns whether the target is dead.
    /// </summary>
    public bool Strike(Combatant target, int damage, int felledBy = -1, bool flinch = true, bool front = true)
    {
        target.Hp = Math.Max(0, target.Hp - damage);
        Number(target, Math.Min(999, damage).ToString());
        if (target.Dead) return true;
        if (target.Hp == 0)
        {
            target.Dead = true;
            Play(target, Death, 4, once: true);
            if (target == Ashley) { _phase = Phase.Dying; _frames = 90; return true; }
            if (felledBy >= 0) Felled?.Invoke(target, felledBy);
            Defeated?.Invoke(target);
            return true;
        }
        int flinched = target == Ashley ? (front ? AshleyFlinchFront : AshleyFlinchBack) : front ? FlinchFront : FlinchBack;
        if (flinch && Play(target, flinched, 2) is { } set) target.Actor.Queue($"{set}_1", 6);
        return false;
    }

    /// <summary>
    /// Effect ids to the status bits their handlers set (BATTLE.PRG's effect table at 0x800E8378, each handler passing
    /// its bit to 0x80081020): lowering strength, intelligence and agility 5, 7 and 9, slowing 4, raising 6, 8 and 10,
    /// quickening 11, silence 12, regeneration 17, paralysis 13, poison 14, numbness 15, curse 16 and the rest.
    /// </summary>
    private static readonly int[] StatusBits = { -1, 5, 7, 9, 19, 4, 6, 8, 10, 20, 11, 12, 18, 17, 29, 13, 14, 15, 16, 21, 22, 23, 24, 25, 26, 27, 28 };

    /// <summary>A fighter's statuses as BATTLE.PRG keeps them, a bit each (its data's +0x948).</summary>
    private static uint StatusWord(Combatant c)
    {
        uint word = 0;
        foreach (int status in c.Statuses.Keys) if (status > 0 && status < StatusBits.Length && StatusBits[status] >= 0) word |= 1u << StatusBits[status];
        return word;
    }

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
    /// A fighter as the planner reckons it (BATTLE.PRG's 24-byte snapshot, 0x800dc424): hit, magic and its weapon's
    /// and shield's phantom points, each with its most, and its statuses.
    /// </summary>
    private struct Standing
    {
        public int Hp, MaxHp, Mp, MaxMp, BladePp, BladeMaxPp, ShieldPp, ShieldMaxPp;
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
    private static int Relation(Combatant from, Combatant to) => to.Index < 16 ? (int)(from.Mind!.Relations[to.Index] >> 6 & 3) : 0;

    /// <summary>
    /// A fighter's well-being as an enemy reckons it (BATTLE.PRG 0x800dcc94, summed by 0x800dc284): six times the
    /// standing of its statuses (<see cref="FieldContent.Status"/>; silence only for one with a magic cost,
    /// <see cref="MostMagic"/>), less 128 when it is down and 84 under 44/128 of its hit points; plus four times its hit
    /// points, its magic points, and twice its weapon's and its shield's phantom points, each a share of its most in
    /// 128ths.
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
        int phantom = (s.BladeMaxPp != 0 ? s.BladePp * 128 / s.BladeMaxPp : 0) + (s.ShieldMaxPp != 0 ? s.ShieldPp * 128 / s.ShieldMaxPp : 0);
        return 6 * standing + 4 * life + magic + 2 * phantom;
    }

    /// <summary>The fighters the planner reckons with (BATTLE.PRG's actor list, 0x800f1928): Ashley and every living fighter but the crates.</summary>
    private List<Combatant> Fighters()
    {
        var fighters = new List<Combatant>();
        if (Ashley != null && !Ashley.Dead) fighters.Add(Ashley);
        foreach (var c in Combatants.Values) if (c != Ashley && !c.Dead && !c.Cube) fighters.Add(c);
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
            bool unable = (StatusWord(target) & 0x9E2B0) != 0 || most != 0 && target.Has(Silence);
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
    /// (0x800dc424) after the move, its user paying the cost (magic points, hit points or its weapon's phantom points;
    /// 0x800dc888) and its target taking what the move rolls (an attack its hit and <see cref="Damage"/>, a spell each
    /// effect's <see cref="Reckon"/>; a miss takes nothing), each fighter's <see cref="WellBeing"/> added for an ally and
    /// taken off for any other but one standing neither way; times 128, plus the weight: the chance the move rolled on
    /// its target, in 128ths, or 0 had it missed (0x800dcbd8); 128 for none. An action flagged 0x20 is passed over nine
    /// times in ten; one flagged 0x10, for an enemy that weighs those lightly, is not reckoned: it scores 1 over standing
    /// still, at weight 40.
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
                Hp = f.Hp, MaxHp = f.MaxHp, Mp = f.Mp, MaxMp = f.MaxMp, BladePp = f.Blade.Pp, BladeMaxPp = f.Blade.MaxPp,
                ShieldPp = f.Shield.Pp, ShieldMaxPp = f.Shield.MaxPp, Statuses = StatusWord(f),
            };
        int bonus = 0;
        if (enemy.WeighsLightly && (record.Flags & 0x10) != 0) { bonus = 1; weight = 40; }
        else if (action != 0 && target != null)
        {
            var user = standings[enemy];
            switch (record.CostKind)
            {
                case 1: user.Mp = Math.Clamp(user.Mp - record.Cost, 0, user.MaxMp); break;
                case 3: user.Hp = Math.Clamp(user.Hp - record.Cost, 0, user.MaxHp); break;
                case 6: user.BladePp = Math.Clamp(user.BladePp - record.Cost, 0, user.BladeMaxPp); break;
            }
            standings[enemy] = user;
            weight = 0;
            var struck = standings[target];
            var spell = _view.Content.Data("spells")["actions"].AsGodotDictionary().GetValueOrDefault(action.ToString(), new GDictionary()).AsGodotDictionary();
            if (action is < 54 or > 183 || spell.Count == 0)
            {
                int chance = Math.Min(100, HitChance(enemy, target, limb, vary: true));
                if (_random.RandiRange(0, 99) < chance)
                {
                    struck.Hp = Math.Clamp(struck.Hp - Damage(enemy, target, limb), 0, struck.MaxHp);
                    weight = chance + chance / 4 + chance / 32;
                }
            }
            else
            {
                bool landed = false;
                int chance = 0;
                foreach (var value in spell["effects"].AsGodotArray())
                {
                    var (hp, status, missed, _, odds) = Reckon(enemy, spell, value.AsGodotDictionary(), target);
                    if (missed) continue;
                    landed = true;
                    chance = odds;
                    struck.Hp = Math.Clamp(struck.Hp + hp, 0, struck.MaxHp);
                    if (status > 0 && status < StatusBits.Length && StatusBits[status] is int bit and >= 0)
                    {
                        var (_, clears, blockedBy, _) = _view.Content.Field.Status(bit);
                        if ((struck.Statuses & blockedBy) == 0) struck.Statuses = struck.Statuses & ~(clears & struck.Statuses) | 1u << bit;
                    }
                }
                if (landed) weight = Math.Clamp(chance, 0, 100) + Math.Clamp(chance, 0, 100) / 4 + Math.Clamp(chance, 0, 100) / 32;
            }
            standings[target] = struck;
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
            if (c.SlowingLimbs is { } pair && (pair.A < c.Limbs.Count && c.Limbs[pair.A].Hp >= 2 || pair.B < c.Limbs.Count && c.Limbs[pair.B].Hp >= 2)) c.Statuses.Remove(Slow);
            if (frames / 120 > 0) c.Hp = Math.Min(c.Hp - 1 + frames / 120, c.MaxHp);
            if (frames / 180 > 0) c.Mp = Math.Min(c.Mp - 1 + frames / 180, c.MaxMp);
            if (c.Has(Poison)) c.Hp = Math.Max(0, c.Hp - seconds / _view.Content.Field.Status(StatusBits[Poison]).Period * (c.MaxHp * 5) / 100);
            if (c.Has(Regen)) c.Hp = Math.Max(0, c.Hp + seconds / _view.Content.Field.Status(StatusBits[Regen]).Period * (c.MaxHp * 5) / 100);
            foreach (var (status, left) in new Dictionary<int, int>(c.Statuses)) if (left >= 0 && left <= clock) c.Statuses.Remove(status);
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
    /// (<see cref="Recheck"/>).
    /// </summary>
    private void PlanMoves(Combatant enemy, List<Combatant> fighters)
    {
        enemy.Plans.Clear();
        _lastResorts.Clear();
        _lastResortSlots = 0;
        foreach (var limb in enemy.Limbs) foreach (int action in limb.Actions) _planned.RemoveWhere(mark => mark.Action == action);
        var kept = new List<(Combatant C, int Hp, int Mp, Dictionary<int, int> Statuses, int[] Limbs)>();
        foreach (var f in fighters) kept.Add((f, f.Hp, f.Mp, new Dictionary<int, int>(f.Statuses), f.Limbs.ConvertAll(l => l.Hp).ToArray()));
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
        foreach (var (c, hp, mp, statuses, limbs) in kept)
        {
            c.Hp = hp;
            c.Mp = mp;
            c.Statuses.Clear();
            foreach (var (status, left) in statuses) c.Statuses[status] = left;
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
        foreach (var enemy in Combatants.Values)
        {
            if (enemy == Ashley || !enemy.Hostile || enemy.Dead || enemy.Cube || enemy.Mind == null) continue;
            PlanMoves(enemy, fighters);
            if (enemy.Plans.Count == 0) enemy.Mind.State = 7;
            else Choose(enemy);
            if (enemy.Mind.Converted) Show(enemy, 0x80);
        }
        _planClock = 0;
    }

    /// <summary>
    /// An enemy's planned move, when it is a spell (54 to 140) or special attack (141 to 183, such as breaths and tail
    /// attacks): it pays the action's cost (<see cref="FieldContent.Action"/>: magic points, or hit points), then
    /// the wind-up and release on its target, and its effects by the same rules as Ashley's. Anything else is an attack.
    /// </summary>
    private bool EnemySpell(Combatant enemy, int action, Combatant target)
    {
        if (action is < 54 or > 183) return false;
        var spell = _view.Content.Data("spells")["actions"].AsGodotDictionary().GetValueOrDefault(action.ToString(), new GDictionary()).AsGodotDictionary();
        if (spell.Count == 0) return false;
        var record = _view.Content.Field.Action(action);
        if (record.CostKind == 1) enemy.Mp = Math.Max(0, enemy.Mp - record.Cost);
        if (record.CostKind == 3) enemy.Hp = Math.Max(1, enemy.Hp - record.Cost);
        enemy.Actor.Facing = enemy.Actor.FacingToward(target.Actor.Position);
        if (Play(enemy, 85, 6) == null) Play(enemy, 87, 6);
        // Its casting lasts as Ashley's does, while its name shows, sparkling in mode 2 for a spell and 3 for a
        // special attack (footage: the Dullahan's Drain Heart).
        _enemyCast = (enemy, spell, target, FieldView.AnnounceFrames + 9);
        _enemyChant = _view.PlayResident(56, enemy.Actor, enemy.Actor, mode: action <= 140 ? 2 : 3);
        HoldCamera(enemy, target, spell, null);
        _view.Announce(action <= 140 ? 23 : 24, spell["name"].AsString());
        return true;
    }

    private (Combatant Caster, GDictionary Spell, Combatant Target, int Frames)? _enemyCast;

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
        _enemyRelease = (cast.Caster, cast.Spell, target);
    }

    private FieldEffect? _enemyEffect;
    private (Combatant Caster, GDictionary Spell, Combatant Target)? _enemyRelease;

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
        if (release.Caster.Dead || release.Target.Dead) return;
        Release(release.Caster, release.Spell, release.Target, play: false);
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

    /// <summary>Whether a limb is at death's door: 1 hit point or none, with any to lose.</summary>
    private static bool Crippled(Combatant c, int limb) => limb < c.Limbs.Count && c.Limbs[limb].MaxHp > 0 && c.Limbs[limb].Hp <= 1;

    /// <summary>Ashley's legs are crippled: she moves at half speed.</summary>
    public bool Lame => Ashley != null && Crippled(Ashley, 4);

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
            if (!c.Targetable) continue;
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
    private Action<Combatant>? _aimed;
    private GDictionary? _aimSpell;

    /// <summary>
    /// Opens the sphere on what a spell or an item can be used on, each taken whole ("TARGET" down the right, the
    /// HUD's "#TARGET"): Circle hands the chosen one to <paramref name="confirm"/>, Cross puts the sphere away and
    /// nothing is spent. The readout is the target's name with its hit points and the spell's chance, or an item's
    /// chance alone ("Ashley Riot 100%"), then "TARGET: Single" with a spell's affinity and the target's class
    /// (footage: Heal's "Ashley Riot HP 59/100%", "TARGET: Single LIT/Human"; an Elixir's "TARGET: Single"). False,
    /// and nothing opened, while another action is under way.
    /// </summary>
    public bool AimAt(List<Combatant> candidates, GDictionary? spell, Action<Combatant> confirm, GDictionary? action = null)
    {
        if (candidates.Count == 0 || Ashley == null || _phase != Phase.Free) return false;
        _targets.Clear();
        foreach (var c in candidates) _targets.Add((c, new List<int> { -1 }));
        _aimed = confirm;
        _aimSpell = spell;
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

    /// <summary>A spell's chance on its target as the sphere shows it: healing and help always land; others by agility, each scaled by RISK, less the target's, plus 130 less the cost.</summary>
    private int SpellChance(GDictionary spell, Combatant target)
    {
        foreach (var effect in spell["effects"].AsGodotArray()) if (effect.AsGodotDictionary()["effect"].AsInt32() == 62) return 100;
        return Math.Clamp(Agility(Ashley!) * (100 - Ashley!.Risk) / 100 - Agility(target) * (100 - target.Risk) / 100 + 130 - spell["cost"].AsInt32(), 0, 100);
    }

    private void StepSphere(FieldPlayer player)
    {
        if (_pressed("ps_cross")) { _aimed = null; CloseSphere(); return; }
        if (_aimed != null)
        {
            if (_targets.Count > 1 && _pressed("ps_right")) { _target = (_target + 1) % _targets.Count; ShowTarget(); }
            if (_targets.Count > 1 && _pressed("ps_left")) { _target = (_target + _targets.Count - 1) % _targets.Count; ShowTarget(); }
            if (!_pressed("ps_circle")) return;
            var act = _aimed;
            var aimedAt = _targets[_target].Who;
            _aimed = null;
            CloseSphere();
            act(aimedAt);
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
            // A spell's or an item's target, whole: its name, then its hit points and the spell's chance or the item's
            // chance alone; "Single", with a spell's affinity and the target's class.
            string chance = _aimSpell is { } spell
                ? SystemBox.Fill(text["hit_points"].AsGodotDictionary(), numbers: new[] { SpellPoints(spell, who), SpellChance(spell, who) })
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
            _hud?.AimedAt(Aim(who, -1), Project);
            // The whole target is lit (part -1 lights every vertex), cyan at once: an aimed target does not flash red
            // (footage: Ashley under the Elixir's sphere).
            Highlight(who, -2);
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
        string hp = SystemBox.Fill(text["hit_points"].AsGodotDictionary(), numbers: new[] { limb >= 0 && limb < who.Limbs.Count ? who.Limbs[limb].Hp : who.Hp, HitChance(Ashley!, who, limb) });
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
    /// Resolves a hit: the roll (the player has a hidden 10 more), damage, shared out over the limbs by the struck
    /// limb's split, the flinch or death. The attack raises the attacker's RISK by its blade's cost.
    /// </summary>
    private void Land(Combatant attacker, Combatant target)
    {
        int limb = _victimLimb;
        target.Engaged = true;
        attacker.Risk = Math.Min(100, attacker.Risk + attacker.Cost);
        // A crate takes no number (0x80087578) and does not move. One whose record has +0x954 bit 16 (every kind but 2
        // and 4) has its blow's result cleared (0x80085718): the flash and nothing else (footage, 6:02). A kind 2 or 4
        // crate takes the blow as anyone does, its one hit point gone on a hit, and breaks (0x80074a20).
        if (target.Cube)
        {
            if (target.CubeKind is not (2 or 4)) { _view.PlayResident(1, attacker.Actor, target.Actor); return; }
            if (_random.RandiRange(0, 99) >= Math.Min(100, HitChance(attacker, target, -1, vary: true) + 10)) { _view.PlayResident(0x27, attacker.Actor, target.Actor); return; }
            _view.PlayResident(1, attacker.Actor, target.Actor);
            _view.BreakCrate(target.Actor.Id);
            return;
        }
        int chance = Math.Min(100, HitChance(attacker, target, limb, vary: true) + (attacker == Ashley ? 10 : 0));
        _landed = false;
        // The blow's flash, a resident small effect at the struck limb (BATTLE.PRG 0x8006c84c): 1 for a hit, 0x27 otherwise.
        int bone = limb >= 0 && limb < target.Limbs.Count ? target.Limbs[limb].Bone : -1;
        if (_random.RandiRange(0, 99) >= chance) { Number(target, "Miss"); _view.PlayResident(0x27, attacker.Actor, target.Actor, bone); return; }
        _view.PlayResident(1, attacker.Actor, target.Actor, bone);
        int damage = Damage(attacker, target, limb);
        _lastDamage = damage;
        _lastStruck = target;
        _landed = true;

        if (limb >= 0 && limb < target.Limbs.Count)
            for (int i = 0; i < target.Limbs.Count; i++)
            {
                int share = i < 6 ? target.Limbs[limb].Split[i] : 0;
                target.Limbs[i].Hp = Math.Max(0, target.Limbs[i].Hp - damage * share / 100);
                // Each limb's armour wears by its share of the damage.
                target.Limbs[i].Armour.Wear(damage * share / 100);
            }
        Wear(attacker, target, damage);
        var toward = (attacker.Actor.Position - target.Actor.Position) with { Y = 0 };
        float facing = target.Actor.Facing * Mathf.Tau / 4096f;
        bool front = new Vector3(-Mathf.Sin(facing), 0, -Mathf.Cos(facing)).Dot(toward) >= 0;
        if (Strike(target, damage, attacker == Ashley ? 1 : -1, front: front)) return;
        if (target == Ashley) { _player?.Halt(); _stagger = 15; }
        target.Timer = Math.Max(target.Timer, 30);
    }


    /// <summary>
    /// Where an enemy heads for to reach a point (BATTLE.PRG 0x800e19fc): in the point's zone, or not routing, straight
    /// at it; else for the nearer to it of the two connector tiles the room links its zone to the point's by, along z
    /// first then x (0x800def30); and on the connector, through it into the next zone: its steps (bits 13 and 14, less
    /// one) in its direction (bits 11 and 12: octants 2, 6, 4 and 0; 0x800f175c). A connector with bit 15 is not used;
    /// one with bit 10 is a crate step (0x800dfaf8): it serves only while the crate its pair names stands beside it (the
    /// room's "crate_steps"), when the enemy climbs onto that crate (mode 14; taken as the crate's tile), and otherwise
    /// the enemy waits on it.
    /// </summary>
    private Vector3 Waypoint(Combatant enemy, Vector3 point)
    {
        var position = enemy.Actor.Position;
        int x = (int)Mathf.Floor(position.X / 128), z = (int)Mathf.Floor(position.Z / 128);
        int tx = (int)Mathf.Floor(point.X / 128), tz = (int)Mathf.Floor(point.Z / 128);
        int from = _view.TileZone(x, z), to = _view.TileZone(tx, tz);
        if (!enemy.Routes || from == to) return point;
        uint link = _view.ZoneLink(to, from);
        if ((link & 0x1be0) == 0) return point;
        int best = -1, bestDistance = int.MaxValue;
        foreach (int half in (link & 0x1be00000) != 0 ? new[] { (int)(link & 0xFFFF), (int)(link >> 16) } : new[] { (int)(link & 0xFFFF) })
        {
            if ((half & 0x8000) != 0) continue;
            int cx = half >> 5 & 31, cz = half & 31, distance = (cx - tx) * (cx - tx) + (cz - tz) * (cz - tz);
            if (distance < bestDistance) (best, bestDistance) = (half, distance);
        }
        if (best < 0) return point;
        int connectorX = best >> 5 & 31, connectorZ = best & 31;
        enemy.Mover!.Climbs = false;
        if (x == connectorX && z == connectorZ && (best & 0x400) != 0)
        {
            var steps = _view.Document.GetValueOrDefault("crate_steps", new Godot.Collections.Array()).AsGodotArray();
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i].AsGodotArray();
                if (step[0].AsInt32() != x || step[1].AsInt32() != z || _view.CrateTile(i / 2) is not { } crate || crate.X != step[2].AsInt32() || crate.Z != step[3].AsInt32()) continue;
                enemy.Mover.Climbs = true;
                return new Vector3(crate.X * 128 + 64, position.Y, crate.Z * 128 + 64);
            }
            return new Vector3(connectorX * 128 + 64, position.Y, connectorZ * 128 + 64);
        }
        if (x == connectorX && z == connectorZ)
        {
            int octant = new[] { 2, 6, 4, 0 }[best >> 11 & 3], steps = (best >> 13 & 3) + 1;
            float angle = octant * 512 * Mathf.Tau / 4096;
            int dx = Mathf.RoundToInt(-Mathf.Sin(angle)), dz = Mathf.RoundToInt(-Mathf.Cos(angle));
            return new Vector3((x + dx * steps) * 128 + 64, position.Y, (z + dz * steps) * 128 + 64);
        }
        return z != connectorZ ? new Vector3(position.X, position.Y, connectorZ * 128 + 64) : new Vector3(connectorX * 128 + 64, position.Y, position.Z);
    }

    /// <summary>How an enemy moves: walking (and running), hopping from tile to tile, or flying.</summary>
    public enum Gait { Walk, Hop, Fly }

    /// <summary>
    /// Each enemy's balloon over its head, by the icon its AI shows (BATTLE.PRG's byte at 0x800f2260, set by
    /// <see cref="Show"/>): the rounded balloon with one, two then three dots (1 to 3) over a token's first 24 frames,
    /// then the jagged balloon with its mark (4), which also stands while the 90 frames after a release run.
    /// </summary>
    private void StepBalloons()
    {
        if (_hud == null) return;
        var shown = new List<(Vector3 Head, int Kind, int Side)>();
        foreach (var enemy in Combatants.Values)
        {
            if (enemy.Dead || !enemy.Hostile || enemy.Mind == null) continue;
            int kind = enemy.Mind.Shown & 0x7F;
            if (kind is < 1 or > 4 || Ashley == null) continue;
            // It turns to the other side of its head once it is 32 units past Ashley across the screen.
            var camera = _view.Field?.Camera;
            if (camera == null || !camera.IsInsideTree()) continue;
            float mine = camera.UnprojectPosition(EventActor.RetailSpace * enemy.Actor.Position).X, hers = camera.UnprojectPosition(EventActor.RetailSpace * Ashley.Actor.Position).X;
            float apart = (mine - hers) / Math.Max(1, _view.GetViewport().GetVisibleRect().Size.Y / FieldView.VisibleLines);
            if (apart > 32) enemy.BalloonSide = 0;
            if (apart < -32) enemy.BalloonSide = 1;
            shown.Add((enemy.Actor.Position + new Vector3(0, -210, 0), kind, enemy.BalloonSide));
        }
        _hud.Balloons(shown, Project);
    }

    /// <summary>A map point's place on the window, or null behind the camera.</summary>
    private Vector2? Project(Vector3 point) => _view.Field?.Camera is { } c && c.IsInsideTree() && !c.IsPositionBehind(EventActor.RetailSpace * point) ? c.UnprojectPosition(EventActor.RetailSpace * point) : null;

    private void Number(Combatant target, string text)
    {
        // A number of damage is the HUD's large digits over the target (footage: grey for both sides, each digit
        // dropping into place from the left, held until it goes after about 32 frames).
        if (int.TryParse(text, out int value) && value >= 0)
        {
            _popups.Add((target.Actor.Position + new Vector3(0, -150, 0), value, 0));
            return;
        }
        var label = new Label3D
        {
            Text = text, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FontSize = 48, OutlineSize = 12,
            Modulate = target == Ashley ? new Color(1, 0.5f, 0.5f) : Colors.White,
        };
        target.Actor.Node.GetParent().AddChild(label);
        label.GlobalPosition = EventActor.RetailSpace * (target.Actor.Position + new Vector3(0, -150, 0));
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
    /// RISK falls and Ashley recovers over time: with her weapon drawn RISK drops 1 every 60 frames, MP rises 1 every
    /// 90 and HP 1 every 120 (and a limb with it, in turn); sheathed, every 15, 30 and 60.
    /// </summary>
    private void Recover()
    {
        if (Ashley == null || Ashley.Dead) return;
        _tick++;
        // Statuses run down, and poison and regeneration act: poison takes a twentieth of the hit points left (and 0
        // to 5 more) every 2 seconds, regeneration gives a twentieth of the most, less 2, every 5.
        foreach (var c in Combatants.Values)
        {
            if (c.Dead) continue;
            foreach (int status in new List<int>(c.Statuses.Keys))
                if (c.Statuses[status] > 0 && --c.Statuses[status] == 0) c.Statuses.Remove(status);
            if (c.Has(Poison) && _tick % 60 == 0) { int harm = Math.Max(1, c.Hp / 20 + _random.RandiRange(0, 5)); c.Hp = Math.Max(1, c.Hp - harm); Number(c, harm.ToString()); }
            if (c.Has(Regen) && _tick % 150 == 0) c.Heal(Math.Max(1, c.MaxHp / 20 - 2 + _random.RandiRange(0, 4)));
        }
        // A crippled body slows RISK's fall with the weapon sheathed.
        int risk = Drawn || Crippled(Ashley, 3) ? 60 : 15, magic = Drawn ? 90 : 30, health = Drawn ? 120 : 60;
        if (_tick % risk == 0) Ashley.Risk = Math.Max(0, Ashley.Risk - 1);
        if (_tick % magic == 0) Ashley.Restore(1);
        if (_tick % health == 0)
        {
            Ashley.Heal(1);
            if (Ashley.Limbs.Count > 0)
            {
                var limb = Ashley.Limbs[(int)(_tick / health % Ashley.Limbs.Count)];
                limb.Hp = Math.Min(limb.MaxHp, limb.Hp + 1);
            }
        }
    }

    private void ShowStatus()
    {
        if (Ashley == null) return;
        _hud?.Set(Ashley.Hp, Ashley.MaxHp, Ashley.Mp, Ashley.MaxMp, Ashley.Risk, Drawn, Ashley.Limbs.ConvertAll(limb => (limb.Hp, limb.MaxHp)));
        _hud?.ShowStatuses(Ashley.Statuses.Keys);
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
