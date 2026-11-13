// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// Reads the retail item records: the New Game inventory that TITLE.PRG copies over the field inventory, and the item
/// names in MENU/ITEMNAME.BIN. The field inventory (0xF00 bytes) holds 8 weapons (0x20 bytes each) from 0, 8 shields
/// (0x30) from 0x100, 16 blades (0x2C) from 0x280, 16 grips (0x10) from 0x540, 16 pieces of armour (0x28) from 0x640,
/// 48 gems (0x1C) from 0x8C0 and 64 other items (4 bytes) from 0xE00.
/// </summary>
public sealed class InventoryConversion
{
    /// <summary>The New Game inventory in TITLE.PRG, which loads at 0x80068800.</summary>
    public const int NewGameInventory = unchecked((int)(0x80074C24u - 0x80068800u));
    public const int InventorySize = 0xF00;

    private readonly byte[] _inventory;

    public InventoryConversion(byte[] title)
    {
        if (NewGameInventory + InventorySize > title.Length) throw new InvalidDataException("TITLE.PRG is truncated.");
        _inventory = title[NewGameInventory..(NewGameInventory + InventorySize)];
    }

    private static JsonArray Signed(byte[] data, int at, int count)
    {
        var values = new JsonArray();
        for (int i = 0; i < count; i++) values.Add((int)(sbyte)data[at + i]);
        return values;
    }

    /// <summary>
    /// A blade record (0x2C bytes): id, weapon model, category, maximum and current DP and PP, strength,
    /// intelligence, agility, damage type (byte 16, low two bits) and the kind of its cost (bits 2 to 4; 2 is RISK,
    /// BATTLE.PRG's +0x14b), range (x, y, z, shape), eight classes, eight affinities and material.
    /// </summary>
    public static JsonObject Blade(byte[] d, int at) => new()
    {
        ["id"] = (int)d[at], ["model"] = (int)d[at + 2], ["category"] = (int)d[at + 3],
        ["dp"] = (int)BitConverter.ToUInt16(d, at + 8), ["max_dp"] = (int)BitConverter.ToUInt16(d, at + 4),
        ["pp"] = (int)BitConverter.ToUInt16(d, at + 10), ["max_pp"] = (int)BitConverter.ToUInt16(d, at + 6),
        ["strength"] = (int)(sbyte)d[at + 12], ["intelligence"] = (int)(sbyte)d[at + 13], ["agility"] = (int)(sbyte)d[at + 14],
        ["damage_type"] = d[at + 16] & 3, ["cost_kind"] = d[at + 16] >> 2 & 7, ["cost"] = (int)d[at + 15], ["range"] = new JsonArray { (int)d[at + 20], (int)d[at + 21], (int)d[at + 22], (int)d[at + 23] },
        ["classes"] = Signed(d, at + 24, 8), ["affinities"] = Signed(d, at + 32, 8), ["material"] = (int)BitConverter.ToUInt16(d, at + 40),
    };

    /// <summary>
    /// Combining blades (the workshop's Combine; MENU/MENUC.PRG, loaded at 0x80102800, combines at 0x8010b2b4).
    /// MENU/BLADE.SYD opens with three offsets: a 96 by 96 table of the blade a pair makes (by the two blades' ids), a
    /// table of the material it takes (by first material less three, its category, second material less three and
    /// its category: 5 × 10 × 5 × 10), and a 16-byte record a blade id: its model (1), category (2), damage type (3),
    /// the kind of its cost (4, low three bits) and its cost (5), a value (6), strength, intelligence and agility (8 to 10) and its
    /// range (0xC to 0xF). The executable's material records (0x8004EDCC, 30 bytes each) add strength,
    /// intelligence and agility (bytes 0x18, 0x1A and 0x1C). The class and affinity values are mixed by weights
    /// The materials each workshop works (MENUC 0x8010BB30, a bit per material, by the room's workshop kind) limit
    /// what it combines.
    /// (MENUC 0x8010BB84: 6 by 6 for classes, 0x8010BBA8: 7 by 7 for affinities, by where each blade's highest value
    /// lies; tenths at 0x8010BBDC for blades of one material and 0x8010BBE4 for two).
    /// </summary>
    /// <remarks>
    /// Shields (MENU/SHIELD.SYD; 0x8010b598) and armour (ARMOR.SYD; 0x8010b80c) go alike, by their index within the
    /// kind (the item id less 126): a 17 by 17 (shields) or 65 by 65 table of the result (armour indices counting from
    /// 16), a material table (8 × 8 by the two materials for shields; 8 × 4 × 8 × 4 by material and category less two
    /// for armour), and an 8-byte record an index (model 1, category 2, a shield's gem slots 3, strength,
    /// intelligence and agility 4 to 6). Their type values mix by where each piece's strongest of types 1 to 3 lies
    /// (0x8010b150; tenths at 0x8010BBEC for one material and 0x8010BBF0 for two, by whether those agree).
    /// </remarks>
    public static JsonObject Combining(byte[] blades, byte[] combiner, byte[] executable, byte[] shields, byte[] armour)
    {
        int results = BitConverter.ToInt32(blades, 0), materials = BitConverter.ToInt32(blades, 4), kinds = BitConverter.ToInt32(blades, 8);
        if (kinds + 96 * 16 > blades.Length || materials + 2500 > kinds || results + 96 * 96 > materials) throw new InvalidDataException("MENU/BLADE.SYD is malformed.");
        JsonArray Bytes(byte[] d, int at, int count) { var list = new JsonArray(); for (int i = 0; i < count; i++) list.Add((int)d[at + i]); return list; }
        var kindList = new JsonArray();
        for (int id = 0; id < 96; id++)
        {
            int p = kinds + id * 16;
            kindList.Add(new JsonObject
            {
                ["model"] = (int)blades[p + 1], ["category"] = (int)blades[p + 2], ["damage_type"] = blades[p + 3] & 3, ["cost_kind"] = blades[p + 4] & 7, ["cost"] = (int)blades[p + 5],
                ["value6"] = (int)blades[p + 6], ["strength"] = (int)(sbyte)blades[p + 8], ["intelligence"] = (int)(sbyte)blades[p + 9], ["agility"] = (int)(sbyte)blades[p + 10],
                ["range"] = new JsonArray { (int)blades[p + 12], (int)blades[p + 13], (int)blades[p + 14], (int)blades[p + 15] },
            });
        }
        var stats = new JsonArray();
        int materialRecords = unchecked((int)(0x8004EDCCu - 0x8000F800u));
        for (int m = 0; m < 8; m++)
        {
            int p = materialRecords + m * 30;
            stats.Add(new JsonArray { (int)(sbyte)executable[p + 0x18], (int)(sbyte)executable[p + 0x1A], (int)(sbyte)executable[p + 0x1C] });
        }
        int At(uint address) => (int)(address - 0x80102800u);
        JsonArray Halves(int at) { var list = new JsonArray(); for (int i = 0; i < 4; i++) list.Add((int)BitConverter.ToInt16(combiner, at + i * 2)); return list; }
        return new JsonObject
        {
            ["results"] = Bytes(blades, results, 96 * 96), ["materials"] = Bytes(blades, materials, 2500), ["kinds"] = kindList, ["material_stats"] = stats,
            ["workshop_materials"] = Bytes(combiner, At(0x8010BB30), 7),
            ["class_positions"] = Bytes(combiner, At(0x8010BB84), 36), ["affinity_positions"] = Bytes(combiner, At(0x8010BBA8), 49),
            ["same_material"] = Halves(At(0x8010BBDC)), ["two_materials"] = Halves(At(0x8010BBE4)),
            ["types_same_material"] = new JsonArray { (int)BitConverter.ToInt16(combiner, At(0x8010BBEC)), (int)BitConverter.ToInt16(combiner, At(0x8010BBEE)) },
            ["types_two_materials"] = new JsonArray { (int)BitConverter.ToInt16(combiner, At(0x8010BBF0)), (int)BitConverter.ToInt16(combiner, At(0x8010BBF2)) },
            ["shields"] = Pieces(shields, 17, 64, 17), ["armour"] = Pieces(armour, 65, 1024, 81),
        };

        JsonObject Pieces(byte[] file, int side, int materialCount, int records)
        {
            int resultsAt = BitConverter.ToInt32(file, 0), materialsAt = BitConverter.ToInt32(file, 4), recordsAt = BitConverter.ToInt32(file, 8);
            if (recordsAt + records * 8 > file.Length || materialsAt + materialCount > recordsAt || resultsAt + side * side > materialsAt) throw new InvalidDataException("A combining table is malformed.");
            var kindsOut = new JsonArray();
            for (int i = 0; i < records; i++)
            {
                int p = recordsAt + i * 8;
                kindsOut.Add(new JsonObject { ["model"] = (int)file[p + 1], ["category"] = (int)file[p + 2], ["gem_slots"] = (int)file[p + 3], ["strength"] = (int)(sbyte)file[p + 4], ["intelligence"] = (int)(sbyte)file[p + 5], ["agility"] = (int)(sbyte)file[p + 6] });
            }
            return new JsonObject { ["results"] = Bytes(file, resultsAt, side * side), ["materials"] = Bytes(file, materialsAt, materialCount), ["kinds"] = kindsOut };
        }
    }

    /// <summary>
    /// A gem record (0x1C bytes): its id, a word, a special effect (byte 4; nonzero for the gems the help text gives a
    /// chance against an attack or a status) and three bytes after it whose meaning was not settled, then its bonuses
    /// against the eight classes (from byte 8) and its eight affinities (from 0x10: none, air, fire, earth, water,
    /// light, dark; Undine Jasper has water +15 and fire -5, Haeralis +15 against humans).
    /// </summary>
    public static JsonObject Gem(byte[] d, int at) => new()
    {
        ["id"] = (int)BitConverter.ToUInt16(d, at), ["special"] = (int)d[at + 4], ["bytes"] = new JsonArray { (int)d[at + 5], (int)d[at + 6], (int)d[at + 7] },
        ["classes"] = SignedBytes(d, at + 8, 8), ["affinities"] = SignedBytes(d, at + 0x10, 8),
    };

    private static JsonArray SignedBytes(byte[] d, int at, int count)
    {
        var values = new JsonArray();
        for (int i = 0; i < count; i++) values.Add((int)(sbyte)d[at + i]);
        return values;
    }

    /// <summary>A grip record (0x10 bytes): id, category, gem slots, strength, intelligence, agility and four type bonuses.</summary>
    public static JsonObject Grip(byte[] d, int at) => new()
    {
        ["id"] = (int)BitConverter.ToUInt16(d, at), ["category"] = (int)d[at + 3], ["gem_slots"] = (int)d[at + 4],
        ["strength"] = (int)(sbyte)d[at + 5], ["intelligence"] = (int)(sbyte)d[at + 6], ["agility"] = (int)(sbyte)d[at + 7], ["types"] = Signed(d, at + 8, 4),
    };

    /// <summary>
    /// An armour record (0x28 bytes): id, model (a shield's), category, DP and PP, gem slots (a shield's), strength, intelligence, agility, four type defences, eight
    /// classes, eight affinities, material and the limb it is worn on.
    /// </summary>
    public static JsonObject Armour(byte[] d, int at) => new()
    {
        ["id"] = (int)d[at], ["model"] = (int)d[at + 2], ["category"] = (int)d[at + 3], ["dp"] = (int)BitConverter.ToUInt16(d, at + 8), ["max_dp"] = (int)BitConverter.ToUInt16(d, at + 4),
        ["pp"] = (int)BitConverter.ToUInt16(d, at + 10), ["max_pp"] = (int)BitConverter.ToUInt16(d, at + 6), ["gem_slots"] = (int)d[at + 12],
        ["strength"] = (int)(sbyte)d[at + 13], ["intelligence"] = (int)(sbyte)d[at + 14], ["agility"] = (int)(sbyte)d[at + 15],
        ["types"] = Signed(d, at + 16, 4), ["classes"] = Signed(d, at + 20, 8), ["affinities"] = Signed(d, at + 28, 8),
        ["material"] = (int)BitConverter.ToUInt16(d, at + 36), ["limb"] = (int)d[at + 38],
    };

    /// <summary>
    /// The inventory: weapons (a name, the blade and grip slots they are assembled from, counting from 1, and whether
    /// equipped), blades, grips, armour (by limb slot) and other items with their counts. Empty slots are left out;
    /// each entry keeps its slot.
    /// </summary>
    public JsonObject Export()
    {
        var weapons = new JsonArray();
        for (int i = 0; i < 8; i++)
        {
            int at = i * 0x20;
            if (_inventory[at + 1] == 0) continue;
            var (name, _) = DialogueConversion.Decode(_inventory, at + 8);
            weapons.Add(new JsonObject { ["slot"] = i + 1, ["blade"] = (int)_inventory[at + 1], ["grip"] = (int)_inventory[at + 2], ["equipped"] = _inventory[at + 3] != 0, ["name"] = name });
        }
        var blades = new JsonArray();
        for (int i = 0; i < 16; i++)
            if (_inventory[0x280 + i * 0x2C] != 0) { var blade = Blade(_inventory, 0x280 + i * 0x2C); blade["slot"] = i + 1; blades.Add(blade); }
        var grips = new JsonArray();
        for (int i = 0; i < 16; i++)
            if (BitConverter.ToUInt16(_inventory, 0x540 + i * 0x10) != 0) { var grip = Grip(_inventory, 0x540 + i * 0x10); grip["slot"] = i + 1; grips.Add(grip); }
        var armour = new JsonArray();
        for (int i = 0; i < 16; i++)
            if (_inventory[0x640 + i * 0x28] != 0) { var piece = Armour(_inventory, 0x640 + i * 0x28); piece["slot"] = i + 1; armour.Add(piece); }
        var items = new JsonArray();
        for (int i = 0; i < 64; i++)
        {
            int at = 0xE00 + i * 4;
            if (BitConverter.ToUInt16(_inventory, at) == 0 || _inventory[at + 2] == 0) continue;
            items.Add(new JsonObject { ["id"] = (int)BitConverter.ToUInt16(_inventory, at), ["count"] = (int)_inventory[at + 2] });
        }
        return new JsonObject { ["weapons"] = weapons, ["blades"] = blades, ["grips"] = grips, ["armour"] = armour, ["items"] = items };
    }

    /// <summary>The executable's action table: 52-byte records (SLUS_010.40 0x8004B9DC).</summary>
    public const int Actions = 0x3C1DC;

    /// <summary>
    /// An action record's effects, its two words at 0x14 and 0x18 (an effect of 0 is none): the effect, its
    /// prerequisite, hit function, reckoning, factor, damage type and affinity.
    /// </summary>
    private static JsonArray Effects(byte[] executable, int record)
    {
        var effects = new JsonArray();
        for (int i = 0; i < 2; i++)
        {
            var effect = new ActionEffect(BitConverter.ToUInt32(executable, record + 0x14 + i * 4));
            if (effect.Effect == 0) continue;
            effects.Add(new JsonObject
            {
                ["effect"] = effect.Effect, ["prerequisite"] = effect.Prerequisite, ["hit"] = effect.Hit,
                ["reckoning"] = effect.Reckoning, ["factor"] = effect.Factor, ["type"] = effect.Type, ["affinity"] = effect.Affinity,
            });
        }
        return effects;
    }

    /// <summary>
    /// What each consumable (items 323 to 356) does: MAINMENU.PRG (loading at 0x800F9800) maps each to an action, one
    /// byte apiece from 0x80102214, and the executable's action table (52-byte records from 0x8004B9DC) gives the
    /// action's two effects and its name, which the field announces as the item's effect ("Recover 100 HP"). An effect word holds the effect (bits 0 to 6), how its amount is reckoned (16 to 21) and
    /// a factor (22 to 26). Effects 0x3e, 0x3f and 0x42 restore hit points, magic points and lower RISK; reckoning 15
    /// is five times the factor, 16 and 17 the hit and magic points missing, and 26 all of RISK.
    /// </summary>
    public static JsonObject Consumables(byte[] mainMenu, byte[] executable)
    {
        const int map = unchecked((int)(0x80102214u - 0x800F9800u));
        var consumables = new JsonObject();
        for (int id = 323; id <= 457; id++)
        {
            int action = mainMenu[map + id - 323], record = Actions + action * 52;
            if (action == 0) continue;
            var effects = Effects(executable, record);
            // Grimoires (386 and up) teach the spell they map to rather than acting at once.
            consumables[id.ToString()] = new JsonObject { ["action"] = action, ["name"] = DialogueConversion.Decode(executable, record + 0x1C).Text, ["visual"] = (int)executable[record + 1], ["effects"] = effects, ["grimoire"] = id >= 386,
                ["camera"] = executable[record + 13] >> 4 & 3, ["range"] = new JsonArray { (int)executable[record + 4], (int)executable[record + 5], (int)executable[record + 6] } };
        }
        return consumables;
    }

    /// <summary>
    /// The chain abilities (actions 22 to 39) and defense abilities (40 to 53) from the executable's action table:
    /// each one's name (at 0x1c), its RISK cost (byte 3), its visual effect (byte 1; the resident effect file's entry) and its
    /// two effect words, as for consumables.
    /// </summary>
    public static JsonObject Abilities(byte[] executable)
    {
        var abilities = new JsonObject();
        for (int id = 22; id <= 53; id++)
        {
            int record = Actions + id * 52;
            var effects = Effects(executable, record);
            if (effects.Count == 0) continue;
            abilities[id.ToString()] = new JsonObject { ["name"] = DialogueConversion.Decode(executable, record + 0x1C).Text, ["cost"] = (int)executable[record + 3], ["visual"] = (int)executable[record + 1], ["effects"] = effects };
        }
        return abilities;
    }

    /// <summary>
    /// Spells (actions 54 to 140), monsters' special attacks (141 to 183: physical, hit function 3 and reckoning 2,
    /// as their natural attacks 2 to 5 are) and Break Arts (184 to 223) from the action table: name, cost (MP for spells, HP
    /// for arts), the range (x, y, z, in 32-unit steps, at bytes 4 to 6) and shape (byte 7, low three bits), the area
    /// (bytes 8 to 10), and each effect word in full: effect, prerequisite, hit function, reckoning, factor, damage type
    /// and affinity. The executable's table at 0x8004FDE4 gives the kills each weapon category needs for its four arts.
    /// </summary>
    /// <summary>
    /// The traps' actions: a room trap of type n (0 to 15) is the executable's action n + 6 (BATTLE.PRG 0x80073F7C
    /// announces it so, under announcement 22 + its type). Each with its name, its type (byte 2, bits 1 to 3), its
    /// visual effect (byte 1) and its effects (the words at 0x14 and 0x18, as <see cref="Spells"/> reads them).
    /// </summary>
    public static JsonObject TrapActions(byte[] executable)
    {
        var traps = new JsonObject();
        for (int type = 0; type < 16; type++)
        {
            int record = Actions + (type + 6) * 52;
            var effects = Effects(executable, record);
            if (effects.Count == 0) continue;
            traps[type.ToString()] = new JsonObject
            {
                ["name"] = DialogueConversion.Decode(executable, record + 0x1C).Text, ["kind"] = executable[record + 2] >> 1 & 7,
                ["visual"] = (int)executable[record + 1], ["effects"] = effects,
            };
        }
        return traps;
    }

    /// <summary>
    /// The Battle Abilities page (MENU/MENU2.PRG): its strings (the table at 0x1E90: CHAIN ABILITIES, its help and its
    /// "yet to learn" line, the same three for DEFENSE ABILITIES, then each ability's description), the points each
    /// reclaimed ability takes (the executable's 22 halfwords at 0x8004FE5C), and the reclaim's lines and lists.
    /// </summary>
    public static JsonObject BattleAbilitiesMenu(byte[] menu2, byte[] executable)
    {
        const int requirements = 0x4FE5C - 0x10000 + 0x800;
        var points = new JsonArray();
        for (int i = 0; i < 22; i++) points.Add(BitConverter.ToUInt16(executable, requirements + i * 2));
        // What a reclaim offers (0x2860, 0x286C: eleven chain and eleven defense abilities, in order) and each ability's
        // description, by action, as an index into the strings (0x2878, 54 bytes; 0 for none).
        var chains = new JsonArray();
        var defenses = new JsonArray();
        for (int i = 0; i < 11; i++) { chains.Add((int)menu2[0x2860 + i]); defenses.Add((int)menu2[0x286C + i]); }
        var descriptions = new JsonArray();
        for (int i = 0; i < 54; i++) descriptions.Add((int)menu2[0x2878 + i]);
        return new JsonObject
        {
            ["strings"] = MenuStrings(menu2, 0x1E90), ["requirements"] = points,
            // The reclaim's lines (0x26D8): a Break Art's, the weapon categories' names (1 to 11), the chooser's help,
            // a battle ability's, the two kinds and "all reclaimed".
            ["unlock"] = MenuStrings(menu2, 0x26D8), ["chain_unlocks"] = chains, ["defense_unlocks"] = defenses, ["descriptions"] = descriptions,
        };
    }

    /// <summary>
    /// The Break Arts menu's strings (MENU/MENU1.PRG, the table at 0xC78): for each weapon category its name, its help
    /// and its "yet to learn" line (three each, dagger first), View Arts and its help, then each art's description, four
    /// a category.
    /// </summary>
    public static JsonObject BreakArtsMenu(byte[] menu1) => new() { ["strings"] = MenuStrings(menu1, 0xC78) };

    /// <summary>
    /// The STATUS screen's words (MENU/MENU4.PRG, loaded at 0x80102800, and BATTLE.PRG, at 0x80068800): MENU4's
    /// string table at 0x4C48 (the stats' and limbs' help, the "values" line, the limb conditions' and status effects'
    /// descriptions), its five condition words by the pointers at 0x5884 (DYING, BAD, AVERAGE, GOOD, EXCELLENT), the
    /// limb names by BATTLE.PRG's sixteen pointers at 0x800EA620 (a leading '#' right-aligns them), the conditions'
    /// colours at 0x800EBC54 (five 0xBBGGRR words) and the status effects' icons at 0x800EBC14 (32 halfwords, a cell's
    /// u and v on the HUD page).
    /// </summary>
    public static JsonObject StatusMenu(byte[] menu4, byte[] battle)
    {
        const uint Menu4 = 0x80102800, Battle = 0x80068800;
        if (menu4.Length < 0x5884 + 20 || battle.Length < 0x83454 + 20) throw new InvalidDataException("MENU4.PRG or BATTLE.PRG is truncated.");
        string Ascii(byte[] file, uint base_, int pointer)
        {
            uint at = BitConverter.ToUInt32(file, pointer) - base_;
            if (at >= file.Length) throw new InvalidDataException("A STATUS screen word lies outside its file.");
            int end = Array.IndexOf(file, (byte)0, (int)at);
            return System.Text.Encoding.ASCII.GetString(file, (int)at, (end < 0 ? file.Length : end) - (int)at);
        }
        var conditions = new JsonArray();
        for (int i = 0; i < 5; i++) conditions.Add(Ascii(menu4, Menu4, 0x5884 + 4 * i));
        var limbs = new JsonArray();
        for (int i = 0; i < 16; i++) limbs.Add(Ascii(battle, Battle, 0x81E20 + 4 * i));
        var colours = new JsonArray();
        for (int i = 0; i < 5; i++)
        {
            uint word = BitConverter.ToUInt32(battle, 0x83454 + 4 * i);
            colours.Add(new JsonArray { (int)(word & 0xFF), (int)((word >> 8) & 0xFF), (int)((word >> 16) & 0xFF) });
        }
        var icons = new JsonArray();
        for (int i = 0; i < 32; i++) icons.Add((int)BitConverter.ToUInt16(battle, 0x83414 + 2 * i));
        return new JsonObject { ["strings"] = MenuStrings(menu4, 0x4C48), ["conditions"] = conditions, ["limb_names"] = limbs, ["condition_colours"] = colours, ["icons"] = icons };
    }

    /// <summary>
    /// The Magic menu (MENU/MENU0.PRG, its strings at 0x2258: each class's name, help and "yet to learn" line, WARLOCK,
    /// SHAMAN, SORCERER, ENCHANTER, then the spells' descriptions class by class) and each class's spells in its menu
    /// order (BATTLE.PRG's lists at 0x800EBD70, 0x800EBD84, 0x800EBD8C and 0x800EBDA0: 18, 6, 18 and 8 actions).
    /// </summary>
    public static JsonObject MagicMenu(byte[] menu0, byte[] battle)
    {
        var classes = new JsonArray();
        foreach (var (at, count) in new[] { (0x800EBD70u, 18), (0x800EBD84u, 6), (0x800EBD8Cu, 18), (0x800EBDA0u, 8) })
        {
            var list = new JsonArray();
            for (int i = 0; i < count; i++) list.Add((int)battle[(int)(at - 0x80068800u) + i]);
            classes.Add(list);
        }
        return new JsonObject { ["strings"] = MenuStrings(menu0, 0x2258), ["classes"] = classes };
    }

    public static JsonObject Spells(byte[] executable)
    {
        const int thresholds = unchecked((int)(0x8004FDE4u - 0x8000F800u));
        var spells = new JsonObject();
        foreach (var (first, last) in new[] { (54, 140), (141, 183), (184, 223) })
            for (int id = first; id <= last; id++)
            {
                int record = Actions + id * 52;
                var effects = Effects(executable, record);
                if (effects.Count == 0) continue;
                spells[id.ToString()] = new JsonObject
                {
                    ["name"] = DialogueConversion.Decode(executable, record + 0x1C).Text, ["cost"] = (int)executable[record + 3], ["visual"] = (int)executable[record + 1],
                    ["range"] = new JsonArray { (int)executable[record + 4], (int)executable[record + 5], (int)executable[record + 6] },
                    ["shape"] = executable[record + 7] & 7, ["area"] = new JsonArray { (int)executable[record + 8], (int)executable[record + 9], (int)executable[record + 10] },
                    ["effects"] = effects,
                    // The camera while it plays (byte 13 bits 4-5): 0 as it is, 1 close in, 2 drawn back.
                    ["camera"] = executable[record + 13] >> 4 & 3,
                };
            }
        var kills = new JsonArray();
        for (int category = 0; category < 10; category++)
        {
            var row = new JsonArray();
            for (int i = 0; i < 4; i++) row.Add((int)BitConverter.ToUInt16(executable, thresholds + (category * 4 + i) * 2));
            kills.Add(row);
        }
        return new JsonObject { ["actions"] = spells, ["art_kills"] = kills };
    }

    /// <summary>The item names: MENU/ITEMNAME.BIN holds 512 records of 24 bytes in the game's text, by item id.</summary>
    public static JsonArray Names(byte[] names)
    {
        var list = new JsonArray();
        for (int id = 0; id < 512 && (id + 1) * 24 <= names.Length; id++) list.Add(DialogueConversion.Decode(names, id * 24).Text);
        return list;
    }

    /// <summary>
    /// The item help (MENU/ITEMHELP.BIN): a table of halfword offsets in halfwords, its first the table's length, to
    /// 679 strings in the game's text. The descriptions come by kind: blades from entry 0 (id - 1), grips from 90 (id - 6),
    /// gems and the other items from 121 and 183 (id - 140: a consumable's effect, a key's door) and accessories from 610
    /// (id + 387); then
    /// the menus' words: the weapon and armour class words (383 to 398 "Weapon" and "Armor", 399 to 408 the blade
    /// categories, 409 to 411 the damage types, 412 and 413 "One" and "Two"), the grip, shield and armour kinds (641
    /// to 649) and the templates "Class: {0}{1}" (651) and its weapon line (652: material, category, damage type and
    /// hands). Each entry is its text and tokens.
    /// </summary>
    public static JsonArray Help(byte[] help)
    {
        var list = new JsonArray();
        int count = BitConverter.ToUInt16(help, 0);
        for (int i = 0; i < count && i * 2 + 2 <= help.Length; i++)
        {
            int at = BitConverter.ToUInt16(help, i * 2) * 2;
            if (at >= help.Length) { list.Add(new JsonObject { ["text"] = "", ["tokens"] = new JsonArray() }); continue; }
            var (text, tokens) = DialogueConversion.Decode(help, at);
            list.Add(new JsonObject { ["text"] = text, ["tokens"] = tokens });
        }
        return list;
    }

    /// <summary>
    /// The gadget bag window's words, in the field menu overlay MENU/MENUB.PRG as the plain ASCII its lettering draws
    /// (0x800C6540): the title "GADGET BAG" at 420, the categories WEAPON, BLADE, GRIP, SHIELD, ARMOR, GEM and MISC
    /// stored from 380 back to 336, the slash at 432 and the commands GET, ALL and END at 436, 440 and 444.
    /// </summary>
    /// <summary>
    /// A menu overlay's string table at <paramref name="table"/>: halfword offsets, in halfwords from the table, the
    /// first of which (where the first string begins) is the count. Each is the decoded text with its tokens.
    /// </summary>
    public static JsonArray MenuStrings(byte[] overlay, int table)
    {
        var list = new JsonArray();
        int count = BitConverter.ToUInt16(overlay, table);
        for (int i = 0; i < count; i++)
        {
            var (text, tokens) = DialogueConversion.Decode(overlay, table + BitConverter.ToUInt16(overlay, table + i * 2) * 2);
            list.Add(new JsonObject { ["text"] = text, ["tokens"] = tokens });
        }
        return list;
    }

    /// <summary>
    /// The menus' crystal backdrop, MENU/MENUBG.BIN: a 256-colour palette (15-bit, 512 bytes), then the 8-bit picture,
    /// 192 by 240, packed as words: each control word writes its low half's count of zero words, then copies its high
    /// half's count of words that follow, for 0x1780 words of input.
    /// </summary>
    public static Godot.Image Backdrop(byte[] file)
    {
        if (file.Length < 0x200) throw new InvalidDataException("MENUBG.BIN is truncated.");
        var pixels = new byte[192 * 240];
        int at = 0x200, written = 0;
        for (int read = 0; read < 0x1780 && at + 4 <= file.Length;)
        {
            uint word = BitConverter.ToUInt32(file, at);
            at += 4;
            read++;
            written += (int)(word & 0xFFFF) * 4;
            int literal = (int)(word >> 16) * 4;
            for (int i = 0; i < literal && at + i < file.Length; i++)
                if (written + i < pixels.Length) pixels[written + i] = file[at + i];
            written += literal;
            at += literal;
            read += literal / 4;
        }
        var image = Godot.Image.CreateEmpty(192, 240, false, Godot.Image.Format.Rgba8);
        for (int i = 0; i < pixels.Length; i++)
        {
            ushort colour = BitConverter.ToUInt16(file, pixels[i] * 2);
            image.SetPixel(i % 192, i / 192, TitleConversion.ToColor(colour, false));
        }
        return image;
    }

    /// <summary>
    /// The equip lists' stat panels' words, from MAINMENU.PRG: its ASCII labels from 0x14 (a leading '#' sets them
    /// right-aligned; the file holds them last first) and the panel header's three page names, game text at 0x88D4
    /// between colour codes (fb 00), which the menu sets to light the page shown.
    /// </summary>
    public static JsonObject EquipLabels(byte[] menu)
    {
        string Ascii(int at)
        {
            int end = at;
            while (end < menu.Length && menu[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(menu, at, end - at).TrimStart('#');
        }
        JsonArray List(params int[] offsets)
        {
            var list = new JsonArray();
            foreach (int at in offsets) list.Add(Ascii(at));
            return list;
        }
        var pages = new JsonArray();
        int p = 0x88D4;
        while (p + 1 < menu.Length && menu[p] == 0xFB && pages.Count < 3)
        {
            p += 2;
            int start = p;
            while (p < menu.Length && menu[p] != 0xFB && menu[p] != 0) p++;
            var word = new byte[p - start + 1];
            Array.Copy(menu, start, word, 0, p - start);
            word[^1] = 0xE7;
            string text = DialogueConversion.Decode(word, 0).Text;
            if (text != "/") pages.Add(text);
        }
        return new JsonObject
        {
            ["classes"] = List(0x98, 0x90, 0x88, 0x7C, 0x74, 0x6C),
            ["affinities"] = List(0x60, 0x58, 0x50, 0x48, 0x40, 0x38, 0x30),
            ["types"] = List(0x28, 0x20, 0x14),
            ["weapon"] = List(0xB0, 0xA8, 0xE4, 0xD8, 0xF0),
            ["armour"] = List(0xC8, 0xB8, 0xF0),
            // STATUS's rows (MAINMENU's statStr): STRENGTH, INTELLIGENCE, AGILITY.
            ["stats"] = List(0x10C, 0xFC, 0xF0),
            ["org_eqp"] = Ascii(0x118), ["dp"] = Ascii(0x124), ["pp"] = Ascii(0x128),
            ["pages"] = pages,
        };
    }

    /// <summary>
    /// The container's menu (MENU/MENUD.PRG): its string table at 0x6D2C (CONTAINER, the commands REMOVE and STORE
    /// with their help, the headers, the category titles WEAPONS to MISC, REMOVED and STORED, the confirmations and
    /// refusals), the container's limits by category (seven halfwords at 0x7144: 32, 64, 64, 32, 64, 192, 256), and
    /// its ASCII words: CONTAINER and GADGET BAG at 0x110 and 0x11C, the category names (WEAPON to MISC, centred, '$')
    /// from 0x158 back to 0x128, the slash at 0x160, OK at 0x1FC and the
    /// transfer list's buttons STATUS, CANCEL and END at 0x200, 0x208 and 0x210.
    /// </summary>
    public static JsonObject ContainerMenu(byte[] menu)
    {
        string Ascii(int at)
        {
            int end = at;
            while (end < menu.Length && menu[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(menu, at, end - at).TrimStart('$', '#');
        }
        var limits = new JsonArray();
        for (int i = 0; i < 7; i++) limits.Add((int)BitConverter.ToUInt16(menu, 0x7144 + i * 2));
        var categories = new JsonArray();
        foreach (int at in new[] { 0x158, 0x150, 0x148, 0x140, 0x138, 0x130, 0x128 }) categories.Add(Ascii(at));
        return new JsonObject
        {
            ["strings"] = MenuStrings(menu, 0x6D2C), ["limits"] = limits, ["categories"] = categories,
            ["container"] = Ascii(0x110), ["bag"] = Ascii(0x11C), ["slash"] = Ascii(0x160), ["ok"] = Ascii(0x1FC),
            ["buttons"] = new JsonArray { Ascii(0x200), Ascii(0x208), Ascii(0x210) },
        };
    }

    public static JsonObject GadgetBag(byte[] menu)
    {
        string At(int offset)
        {
            int end = Array.IndexOf(menu, (byte)0, offset);
            return System.Text.Encoding.ASCII.GetString(menu, offset, Math.Max(0, end - offset));
        }
        var categories = new JsonArray();
        foreach (int offset in new[] { 380, 372, 364, 356, 348, 344, 336 }) categories.Add(At(offset));
        return new JsonObject { ["title"] = At(420), ["categories"] = categories, ["slash"] = At(432), ["get"] = At(436), ["all"] = At(440), ["end"] = At(444) };
    }
}

/// <summary>
/// One of an action record's two effect words (SLUS_010.40 0x8004B9DC + id * 52, at 0x14 and 0x18), as BATTLE.PRG's
/// resolver (0x80085b10) reads it: the effect (bits 0 to 6, the handler table at 0x800E8378), its prerequisite (7 to
/// 12: who it falls on and what it needs, 0x8007fdd8), its hit function (13 to 15, 0x800E8234), its reckoning (16 to
/// 21, 0x800E830C), a factor (22 to 26), a damage type (27 and 28) and an affinity (29 to 31; 0 the weapon's).
/// </summary>
public readonly record struct ActionEffect(uint Word)
{
    public int Effect => (int)(Word & 0x7F);
    public int Prerequisite => (int)(Word >> 7 & 0x3F);
    public int Hit => (int)(Word >> 13 & 7);
    public int Reckoning => (int)(Word >> 16 & 0x3F);
    public int Factor => (int)(Word >> 22 & 0x1F);
    public int Type => (int)(Word >> 27 & 3);
    public int Affinity => (int)(Word >> 29);
}
