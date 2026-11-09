// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Godot;
using Wol.Games.Vagrant.Installation;

namespace Wol.Games.Vagrant.Runtime;

/// <summary>
/// The field's content, decoded from the player's verified disc files as the game needs it, as the retail game
/// decodes them when it loads a room: rooms (MAP/ZONEnnn.ZND and its MPDs), their events and the event files they
/// chain to, characters with their animation sets, weapons, visual effects, overlays, the menus' data, and which
/// bank each sound and music piece plays from. Everything is decoded once per session and kept.
/// </summary>
public sealed class FieldContent
{
    /// <summary>The model-alias byte table in BATTLE.PRG (loaded at 0x80068800), one entry per model id.</summary>
    private const long ModelAliases = 0x800E8960;
    /// <summary>BATTLE.PRG's byte table of which of a weapon's seven palettes each material is drawn with.</summary>
    private const long MaterialPalettes = 0x800E8FC8;
    // Sound driver tables in the verified US executable (SLUS_010.40, loaded at 0x80010000 after a 2048-byte header).
    private const long MusicSectors = 0x8004A6C0, SoundFileMap = 0x8004B1B0, SoundSectors = 0x8004A530, EffectSectors = 0x8004A9E0;
    private const int ScriptLengthsAt = 0x80A98, SwingSounds = 0x80ADC;    // BATTLE.PRG 0x800E9298 and 0x800E92DC

    private readonly Func<string, byte[]> _read;
    /// <summary>Every file the installation keeps from the disc.</summary>
    private readonly List<string> _paths = new();
    private readonly Func<string, bool> _exists;
    private readonly Func<long, string> _pathAt;
    private readonly byte[] _battle, _executable;
    private readonly Dictionary<string, JsonObject?> _data = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Image> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (JsonObject Definition, byte[] Model)?> _characters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _weapons = new(StringComparer.Ordinal);
    /// <summary>The zone unit file (ZUD) that carries a room character's model and animations, by model id.</summary>
    private readonly Dictionary<int, string> _units = new();
    /// <summary>The battle animation set a character fights with: OBJ/&lt;model&gt;_BTn.SEQ for its weapon's category n.</summary>
    private readonly Dictionary<int, string> _battleSets = new();
    /// <summary>Each room's own sound bank, by "zzzmm".</summary>
    private readonly Dictionary<string, byte[]> _roomBanks = new(StringComparer.Ordinal);
    /// <summary>The event sound banks an event file starts with: those of the room or event that first leads to it.</summary>
    private readonly Dictionary<string, (int[] Slots, int Selected, int[] Animations, int AnimationSelected)> _parentBanks = new(StringComparer.Ordinal);

    /// <summary>Where converted rigs are kept between sessions (null to keep none): see <see cref="RetailContent"/>.</summary>
    private readonly string? _cache;
    /// <summary>A file the installation converted (the rooms, Ashley's rig), verified, by its asset name; null when it has none.</summary>
    private readonly Func<string, byte[]?> _installed;

    public FieldContent(Func<string, byte[]> read, Func<string, bool> exists, Func<long, string> pathAt, IEnumerable<string> paths, string? cache = null, Func<string, byte[]?>? installed = null)
    {
        _cache = cache;
        _installed = installed ?? (_ => null);
        _paths.AddRange(paths);
        _read = read;
        _exists = exists;
        _pathAt = pathAt;
        _battle = read("BATTLE/BATTLE.PRG");
        _executable = read("SLUS_010.40");
        // Ashley fights with her weapon's battle set (she has every category's, below).
        _battleSets[0] = BattleSet(0, Data("player")!["weapon"]!);
    }

    private long Word(long address) => BitConverter.ToUInt32(_executable, (int)(2048 + address - 0x80010000));
    private int Byte(long address) => _executable[(int)(2048 + address - 0x80010000)];

    /// <summary>A fighter's battle set: by its weapon's category in hex, or A, bare-handed, with no weapon.</summary>
    private static string BattleSet(int model, JsonNode weapon)
    {
        int category = (int)weapon["model"]! == 0 ? 10 : (int)weapon["category"]!;
        return $"OBJ/{model:X2}_BT{category:X}.SEQ";
    }

    /// <summary>Names a fighter's weapon model in its material's palette; empty without one.</summary>
    private string WeaponAsset(JsonNode weapon)
    {
        int model = (int)weapon["model"]!, palette = _battle[(int)(MaterialPalettes - 0x80068800) + (int)weapon["material"]!];
        if (model <= 0 || !_exists($"OBJ/{model:X2}.WEP")) return "";
        return $"weapon_{model:X2}_{palette}";
    }

    /// <summary>
    /// A document by its installed name: a room ("field_…"), an event ("event_…"), an effect ("effect_nnn"), or
    /// the menus' and inventory's data. Null when the disc has no such thing.
    /// </summary>
    public JsonObject? Data(string name)
    {
        if (_data.TryGetValue(name, out var known)) return known;
        JsonObject? document = name switch
        {
            _ when name.StartsWith("field_", StringComparison.Ordinal) => Room(name["field_".Length..]),
            _ when name.StartsWith("event_", StringComparison.Ordinal) => Event(name["event_".Length..]),
            _ when name.StartsWith("effect_", StringComparison.Ordinal) && int.TryParse(name["effect_".Length..], out int id) => Effect(id),
            _ when name.StartsWith("area_", StringComparison.Ordinal) && name != "area_names" && int.TryParse(name["area_".Length..], out int scene) =>
                _exists($"SMALL/SCEN{scene:d3}.ARM") ? AreaMapConversion.Export(_read($"SMALL/SCEN{scene:d3}.ARM")) : null,
            "area_names" => new JsonObject { ["list"] = AreaMapConversion.Names(_read("MENU/MENU5.PRG")) },
            "map_menu" => MapMenu(),
            "inventory" => Inventory(),
            "player" => Player(),
            "item_names" => new JsonObject { ["list"] = InventoryConversion.Names(_read("MENU/ITEMNAME.BIN")) },
            "item_help" => new JsonObject { ["list"] = InventoryConversion.Help(_read("MENU/ITEMHELP.BIN")) },
            // SETUP's pages (MENU/MENU12.BIN, laid out as ITEMHELP.BIN is): their words, help and refusals.
            "setup_menu" => new JsonObject { ["list"] = InventoryConversion.Help(_read("MENU/MENU12.BIN")) },
            "gadget_bag" => InventoryConversion.GadgetBag(_read("MENU/MENUB.PRG")),
            "item_menu" => ItemMenu(),
            "equip_labels" => InventoryConversion.EquipLabels(_read("MENU/MAINMENU.PRG")),
            "container_menu" => InventoryConversion.ContainerMenu(_read("MENU/MENUD.PRG")),
            "memory_card" => MemoryCard(),
            "results" => Results(),
            "consumables" => InventoryConversion.Consumables(_read("MENU/MAINMENU.PRG"), _executable),
            "abilities" => InventoryConversion.Abilities(_executable),
            "spells" => InventoryConversion.Spells(_executable),
            "trap_actions" => InventoryConversion.TrapActions(_executable),
            "battle_abilities" => InventoryConversion.BattleAbilitiesMenu(_read("MENU/MENU2.PRG"), _executable),
            "break_arts" => InventoryConversion.BreakArtsMenu(_read("MENU/MENU1.PRG")),
            "status_menu" => InventoryConversion.StatusMenu(_read("MENU/MENU4.PRG"), _battle),
            "magic_menu" => InventoryConversion.MagicMenu(_read("MENU/MENU0.PRG"), _battle),
            "combining" => Combining(),
            "manual" => Manual(),
            "ending" => Ending(),
            _ => null,
        };
        _data[name] = document;
        return document;
    }

    /// <summary>An image a document names (room textures, effect atlases, manual pictures, illustrations, overlay layers).</summary>
    public Image? Image(string name)
    {
        if (_images.TryGetValue(name, out var image)) return image;
        // Images come with the document that names them.
        // A room's are "field_zzzz_mmm.key", "field_zzzz_mmm.sky" and "field_zzzz_mmm_key_framesn".
        if (name.StartsWith("field_", StringComparison.Ordinal) && _installed(name) is { } png)
        {
            var picture = new Image();
            if (picture.LoadPngFromBuffer(png) != Error.Ok) throw new InvalidDataException($"The installed {name} could not be read.");
            return _images[name] = picture;
        }
        if (name.StartsWith("field_", StringComparison.Ordinal) && name.Length >= 14) Data(name[..14]);
        else if (name.StartsWith("effect_", StringComparison.Ordinal)) Data(name[..10]);
        else if (name.StartsWith("manual_", StringComparison.Ordinal)) Data("manual");
        else if (name.StartsWith("ending_", StringComparison.Ordinal)) Data("ending");
        else if (name.StartsWith("hud_page_", StringComparison.Ordinal) && int.TryParse(name["hud_page_".Length..], out int clut))
            _images[name] = new HudConversion(_read("BATTLE/SYSTEM.DAT"), _battle).Page(clut);
        else if (name.StartsWith("hud_second_", StringComparison.Ordinal) && name["hud_second_".Length..].Split('_') is [var px, var py] && int.TryParse(px, out int secondX) && int.TryParse(py, out int secondY))
            _images[name] = new HudConversion(_read("BATTLE/SYSTEM.DAT"), _battle).SecondPage(secondX, secondY);
        else if (name == "item_menu_backdrop") Data("item_menu");
        else if (name == "map_backdrop") Data("map_menu");
        else if (name == "memory_card_pictures") Data("memory_card");
        else if (name is "results_sheet" or "results_reel") Data("results");
        else if (name.StartsWith("overlay_", StringComparison.Ordinal) && int.TryParse(name[8..11], out int overlay)) Overlay(overlay);
        return _images.GetValueOrDefault(name);
    }

    /// <summary>
    /// The item menu (MENU3.PRG): its strings (the table at 0x6BB8: the categories WEAPONS to OPTIONS, EQUIP and SETUP
    /// with their help, the equip headers, NONE, "Nothing equipped." and the refusals) and the crystal backdrop it
    /// stands on (MENU/MENUBG.BIN, <see cref="InventoryConversion.Backdrop"/>).
    /// </summary>
    /// <summary>The map screen (MENU5.PRG) and its backdrop (MENU/MAPBG.BIN, <see cref="AreaMapConversion.Backdrop"/>).</summary>
    private JsonObject MapMenu()
    {
        _images["map_backdrop"] = AreaMapConversion.Backdrop(_read("MENU/MAPBG.BIN"));
        return AreaMapConversion.Menu(_read("MENU/MENU5.PRG"));
    }

    private JsonObject ItemMenu()
    {
        _images["item_menu_backdrop"] = InventoryConversion.Backdrop(_read("MENU/MENUBG.BIN"));
        return new JsonObject { ["strings"] = InventoryConversion.MenuStrings(_read("MENU/MENU3.PRG"), 0x6BB8), ["backdrop"] = "item_menu_backdrop" };
    }

    /// <summary>The memory card screens' text, tables and save pictures (<see cref="MemoryCardConversion"/>).</summary>
    private JsonObject MemoryCard()
    {
        _images["memory_card_pictures"] = MemoryCardConversion.Pictures(_read("MENU/SPMCIMG.BIN"), _read("MENU/MCDATA.BIN"));
        return MemoryCardConversion.Export(_read("MENU/MCMAN.BIN"), _executable, _battle, _read("MENU/MENU7.PRG"));
    }

    /// <summary>The CONGRATULATIONS screen's tables and lettering (<see cref="ResultsConversion"/>).</summary>
    private JsonObject Results()
    {
        var rank = _read("SMALL/RANK.DIS");
        _images["results_sheet"] = ResultsConversion.Sheet(rank, 0);
        _images["results_reel"] = ResultsConversion.Sheet(rank, 2);
        return ResultsConversion.Export(_read("MENU/MENUF.PRG"));
    }

    private JsonObject Inventory()
    {
        // New Game gives her the inventory in TITLE.PRG; her equipped weapon's blade carries its material.
        var inventory = new InventoryConversion(_read("TITLE/TITLE.PRG")).Export();
        foreach (var blade in inventory["blades"]!.AsArray()) blade!["asset"] = WeaponAsset(blade);
        foreach (var piece in inventory["armour"]!.AsArray())
            if ((int)piece!["id"]! is >= 127 and <= 142) piece["asset"] = WeaponAsset(piece);
        return inventory;
    }

    private JsonObject Player()
    {
        // Ashley, the player, is model 0 in every room; no event creates her. Her starting record is in INITBTL.PRG.
        var player = MapConversion.CharacterRecord(_read("BATTLE/INITBTL.PRG"), MapConversion.AshleyRecord);
        var inventory = Data("inventory")!;
        foreach (var weapon in inventory["weapons"]!.AsArray())
        {
            if (!(bool)weapon!["equipped"]!) continue;
            foreach (var blade in inventory["blades"]!.AsArray())
                if ((int)blade!["slot"]! == (int)weapon["blade"]!) player["weapon"]!["material"] = (int)blade["material"]!;
        }
        player["weapon"]!["asset"] = WeaponAsset(player["weapon"]!);
        return player;
    }

    private JsonObject Combining()
    {
        // Combining makes blades of any kind in any metal; the field names their models by the material's palette.
        var combining = InventoryConversion.Combining(_read("MENU/BLADE.SYD"), _read("MENU/MENUC.PRG"), _executable, _read("MENU/SHIELD.SYD"), _read("MENU/ARMOR.SYD"));
        var palettes = new JsonArray();
        for (int material = 0; material < 8; material++) palettes.Add((int)_battle[(int)(MaterialPalettes - 0x80068800) + material]);
        combining["palettes"] = palettes;
        return combining;
    }

    private JsonObject Manual()
    {
        // The Quick Manual's topics (SMALL/HELP01.HF0 to HELP14.HF0).
        var topics = new JsonArray();
        for (int topic = 1; topic <= ManualConversion.Topics; topic++)
            topics.Add(ManualConversion.Export(_read($"SMALL/HELP{topic:d2}.HF0"), _read($"SMALL/HELP{topic:d2}.HF1"), $"manual_{topic:d2}", _images));
        return new JsonObject { ["topics"] = topics };
    }

    private JsonObject Ending()
    {
        // The ending: its illustrations and credits (see EndingConversion).
        var illustrations = new JsonArray();
        for (int n = EndingConversion.FirstIllustration; n <= EndingConversion.LastIllustration; n++)
        {
            string asset = $"ending_illust_{n:d2}";
            _images[asset] = EndingConversion.Tim(EndingConversion.Decompress(_read($"ENDING/ILLUST{n:d2}.BIN")));
            illustrations.Add(asset);
        }
        return new JsonObject { ["illustrations"] = illustrations, ["credits"] = EndingConversion.Credits(_read("ENDING/ENDING.PRG")) };
    }

    /// <summary>Every room the disc has, by name ("z001_m00"): each zone's maps, in its list's order.</summary>
    public List<string> RoomNames()
    {
        var names = new List<string>();
        for (int zone = 0; zone < 1000; zone++)
            for (int map = 0; HasRoom(zone, map); map++) names.Add($"z{zone:d3}_m{map:d2}");
        return names;
    }

    /// <summary>
    /// Converts a room for the installation: its document and its pictures, by asset name. What the conversion kept
    /// of the room is dropped again, so that converting every room does not hold them all.
    /// </summary>
    public (JsonObject Document, List<(string Name, Image Image)> Images)? ConvertRoom(string name)
    {
        if (Data("field_" + name) is not { } document) return null;
        var images = new List<(string, Image)>();
        foreach (var (key, image) in _images)
            if (key.StartsWith("field_" + name, StringComparison.Ordinal)) images.Add((key, image));
        foreach (var (key, _) in images) _images.Remove(key);
        _data.Remove("field_" + name);
        _rooms.Remove(name);
        return (document, images);
    }

    /// <summary>Whether the disc has a room: its zone, and a map of that index in the zone's list with a file.</summary>
    public bool HasRoom(int zone, int map)
    {
        if (!_exists($"MAP/ZONE{zone:d3}.ZND")) return false;
        byte[] znd = _read($"MAP/ZONE{zone:d3}.ZND");
        // A slot of the zone's room list with no sector is empty (ZONE000 lists such a room).
        return map >= 0 && map < BitConverter.ToInt32(znd, 4) / 8 && BitConverter.ToUInt32(znd, BitConverter.ToInt32(znd, 0) + map * 8) != 0;
    }

    private readonly Dictionary<string, MapConversion> _rooms = new(StringComparer.Ordinal);

    /// <summary>A room ("zzz_mmm" as "z001_m00") as the field's document; its textures go to the images.</summary>
    private JsonObject? Room(string name)
    {
        if (name.Length != 8 || !int.TryParse(name[1..4], out int zone) || !int.TryParse(name[6..8], out int map) || !HasRoom(zone, map)) return null;
        byte[] znd = _read($"MAP/ZONE{zone:d3}.ZND");
        int mpdList = BitConverter.ToInt32(znd, 0);
        string mpdPath = _pathAt(BitConverter.ToUInt32(znd, mpdList + map * 8));
        byte[] mpd = _read(mpdPath);
        var room = new MapConversion("field_" + name, znd, mpd);
        // A room the installation converted is read as it was made; what its conversion leaves behind (the room for
        // its local events and sound bank, its characters' unit files and battle sets) is made again from it.
        if (_installed("field_" + name) is { } installed && JsonNode.Parse(installed) is JsonObject kept)
        {
            _rooms[name] = room;
            _roomBanks[$"{zone:d3}{map:d2}"] = room.SoundBank;
            foreach (var actor in kept["room_actors"]!.AsArray())
            {
                int character = (int)actor!["character"]!;
                _units.TryAdd(character, _pathAt((uint)(long)actor["unit_sector"]!));
                _battleSets.TryAdd(character, BattleSet(character, actor["stats"]!["weapon"]!));
            }
            return kept;
        }
        room.AddObjects(_battle);
        // A room's sky is BG/<map number>OP01A, where the disc has one.
        string sky = $"BG/{Path.GetFileNameWithoutExtension(mpdPath)[3..]}OP01A";
        if (_exists(sky + ".TIM")) room.AddSky(_read(sky + ".TIM"), _read(sky + ".FAR"));
        var document = room.Export(_images);
        _rooms[name] = room;
        _roomBanks[$"{zone:d3}{map:d2}"] = room.SoundBank;
        foreach (var actor in document["room_actors"]!.AsArray())
        {
            int character = (int)actor!["character"]!;
            _units.TryAdd(character, _pathAt((uint)(long)actor["unit_sector"]!));
            _battleSets.TryAdd(character, BattleSet(character, actor["stats"]!["weapon"]!));
            actor["stats"]!["weapon"]!["asset"] = WeaponAsset(actor["stats"]!["weapon"]!);
            if (actor["stats"]!["shield"] is { } carried && (int)carried["id"]! != 0) carried["asset"] = WeaponAsset(carried);
            // What it drops can be taken up, in its material.
            if (actor["stats"]!["drops"]?["weapon"]?["blade"] is { } dropped) dropped["asset"] = WeaponAsset(dropped);
            if (actor["stats"]!["drops"]?["shield"]?["record"] is { } droppedShield) droppedShield["asset"] = WeaponAsset(droppedShield);
        }
        // Every blade a chest holds can be taken up, in its material.
        foreach (var chest in document["chests"]!.AsArray())
        {
            var loot = chest!["loot"]!;
            if (loot["weapon"]?["blade"] is { } assembled) assembled["asset"] = WeaponAsset(assembled);
            if (loot["blade"] is { } blade) blade["asset"] = WeaponAsset(blade);
            // Shields are weapon models too (OBJ/60.WEP to 6F.WEP), held on the shield mount.
            if (loot["shield"] is { } shield) shield["asset"] = WeaponAsset(shield);
        }
        // The zone loads its music (ZND byte 24) into music slot 3 before the room's event starts.
        int music = znd[24];
        if (music != 0) document["music"] = new JsonObject { ["3"] = $"music_{music:d3}" };
        return document;
    }

    /// <summary>
    /// An event: a room's own script section ("z001_m00"), one of its local events ("z001_m00_l0") or an event
    /// file ("evt0001"). Event files, local events and rooms an event changes to start with the sound banks and
    /// animation sets of the room or event that first leads to them.
    /// </summary>
    private JsonObject? Event(string name)
    {
        byte[] data;
        int offset;
        (int[] Slots, int Selected, int[] Animations, int AnimationSelected)? banks = _parentBanks.TryGetValue(name, out var inherited) ? inherited : null;
        if (name.StartsWith("evt", StringComparison.Ordinal))
        {
            string path = $"EVENT/{name[3..]}.EVT";
            if (!_exists(path)) return null;
            (data, offset) = (_read(path), 0);
        }
        else
        {
            string roomName = name[..8];
            if (Data("field_" + roomName) is not { } document) return null;
            var room = _rooms[roomName];
            byte[] znd = _read($"MAP/ZONE{int.Parse(roomName[1..4]):d3}.ZND");
            data = _read(_pathAt(BitConverter.ToUInt32(znd, BitConverter.ToInt32(znd, 0) + int.Parse(roomName[6..8]) * 8)));
            if (name.Length > 8)
            {
                // A local event plays with the banks the room's script left.
                if (!_parentBanks.TryGetValue(name, out var roomBanks)) Data("event_" + roomName);
                banks = _parentBanks.TryGetValue(name, out roomBanks) ? roomBanks : null;
                offset = room.LocalEvent(int.Parse(name[10..]));
            }
            // The script section (offset and length at 16 and 20); a room without one has an empty event.
            else offset = BitConverter.ToUInt32(data, 20) < 0x12 ? -1 : (int)BitConverter.ToUInt32(data, 16);
        }
        var conversion = new EventConversion(name, data, offset, _battle, banks);
        var converted = conversion.Export();
        // Weapon models events hand to characters are in material 0, as retail loads them.
        void WeaponModels(JsonNode? node)
        {
            if (node is JsonArray array) foreach (var item in array) WeaponModels(item);
            if (node is not JsonObject entry) return;
            if (entry["type"] is JsonValue type && type.TryGetValue(out string? command) && command == "actor.weapon_model" && entry["args"]?["model"] is { } model)
                entry["args"]!["asset"] = WeaponAsset(new JsonObject { ["model"] = (int)model, ["material"] = 0 });
            foreach (var (_, child) in entry) WeaponModels(child);
        }
        WeaponModels(converted["events"]);
        // The events it leads to start with its banks: its chains, and for a room, the events its conditions start.
        foreach (var value in converted["chains"]!.AsArray()) _parentBanks.TryAdd($"evt{(int)value!:d4}", conversion.SoundBanks);
        // A room an event changes to starts its own script with the banks and animation sets the event left: retail
        // keeps them across the change (map017's local event loads E016.ESQ for Ashley's rise in map022). A room
        // script already converted without them is converted again.
        foreach (var value in converted["transitions"]!.AsArray())
        {
            int zone = (int)value!["zone"]!;
            if (zone == 0 && !name.StartsWith('z')) continue;
            string room = $"z{(zone == 0 ? int.Parse(name[1..4]) : zone):d3}_m{(int)value["map"]!:d2}";
            if (_parentBanks.TryAdd(room, conversion.SoundBanks)) _data.Remove("event_" + room);
        }
        if (name.Length == 8 && Data("field_" + name) is { } roomDocument)
            foreach (var condition in roomDocument["conditions"]!.AsArray())
            {
                int id = (int)condition!["event"]!;
                _parentBanks.TryAdd((bool)condition["local"]! ? $"{name}_l{id}" : $"evt{id:d4}", conversion.SoundBanks);
            }
        return converted;
    }

    /// <summary>A visual effect (EFFECT/Ennn.P with its textures and palettes): its blocks, with its atlas among the images.</summary>
    private JsonObject? Effect(int id) =>
        id is < 0 or >= 320 ? null : EffectConversion.Export(id, path => _exists(path) ? _read(path) : null, $"effect_{id:d3}", _images);

    /// <summary>A full-screen overlay's layers, their images among the images.</summary>
    public JsonObject Overlay(int id)
    {
        string name = $"overlay_{id:d3}";
        if (_data.TryGetValue(name, out var known)) return known!;
        var overlay = new OverlayConversion(name, _read(OverlayConversion.PathFor(_pathAt, _battle, id))).Export(_images);
        _data[name] = overlay;
        return overlay;
    }

    /// <summary>The weapons' swing sounds, by category (BATTLE.PRG 0x800E92DC; 0 also for a sheathed weapon).</summary>
    public int[] SwingSoundIds()
    {
        var swings = new int[16];
        for (int category = 0; category < 16; category++) swings[category] = _battle[SwingSounds + category];
        return swings;
    }

    /// <summary>Whether the disc has a character model or weapon ("character_XX", "weapon_XX_p").</summary>
    public bool HasModel(string name) =>
        name.StartsWith("weapon_", StringComparison.Ordinal) ? name.Length > 9 && _exists($"OBJ/{name[7..9]}.WEP")
        : Character(name) != null;

    /// <summary>A weapon's model as binary glTF: OBJ/&lt;id&gt;.WEP in one of its palettes ("weapon_XX_p").</summary>
    public byte[] Weapon(string name)
    {
        if (_weapons.TryGetValue(name, out var model)) return model;
        string[] parts = name.Split('_');
        var weapon = CharacterConversion.Weapon(name, _read($"OBJ/{parts[1]}.WEP"), int.Parse(parts[2]));
        weapon.Export();
        return _weapons[name] = weapon.Model;
    }

    /// <summary>
    /// A character ("character_XX"): its definition (bones, mounts, limbs and clip events) and its rig as binary
    /// glTF, with every animation set that names it. Null when the disc has no model for it.
    /// </summary>
    public (JsonObject Definition, byte[] Model)? Character(string name)
    {
        if (_characters.TryGetValue(name, out var known)) return known;
        int character = System.Convert.ToInt32(name["character_".Length..], 16);
        var result = Model(character) is { } shape ? Build(name, character, shape) : ((JsonObject, byte[])?)null;
        _characters[name] = result;
        return result;
    }

    /// <summary>
    /// The unit file that carries a character: that of a decoded room which names it, else, for a model the disc has
    /// no OBJ/XX.SHP for (event characters like Hardin and Sydney), the first zone unit file (MAP/ZnnnUnn.ZUD, in
    /// disc order) whose first byte, its model id, is the character's.
    /// </summary>
    private string? Unit(int character)
    {
        if (_units.TryGetValue(character, out var known)) return known;
        if (_exists($"OBJ/{character:X2}.SHP")) return null;
        if (_unitIndex == null)
        {
            _unitIndex = new Dictionary<int, string>();
            var paths = _paths.FindAll(path => path.StartsWith("MAP/", StringComparison.Ordinal) && path.EndsWith(".ZUD", StringComparison.Ordinal));
            paths.Sort(StringComparer.Ordinal);
            foreach (string path in paths) _unitIndex.TryAdd(_read(path)[0], path);
        }
        if (!_unitIndex.TryGetValue(character, out var found)) return null;
        _units[character] = found;
        return found;
    }

    private Dictionary<int, string>? _unitIndex;

    /// <summary>A character's model (SHP): from its unit file, else OBJ/XX.SHP.</summary>
    private byte[]? Model(int character)
    {
        if (Unit(character) is { } unitPath)
        {
            // A unit file is two id words, then offset and size pairs: the model (SHP), weapon (WEP), shield,
            // an extra animation set, and the character's own animation set, common and battle moves together.
            byte[] unit = _read(unitPath);
            int modelAt = BitConverter.ToInt32(unit, 8), modelSize = BitConverter.ToInt32(unit, 12);
            if (modelAt + modelSize > unit.Length) throw new InvalidDataException($"{unitPath} is malformed.");
            return unit[modelAt..(modelAt + modelSize)];
        }
        string model = $"OBJ/{character:X2}.SHP";
        return _exists(model) ? _read(model) : null;
    }

    /// <summary>A rig definition's clips that move their actor, made when an older definition has none.</summary>
    private static JsonObject Movement(JsonObject definition)
    {
        if (definition["movement"] is JsonObject movement) return movement;
        definition["movement"] = movement = new JsonObject();
        return movement;
    }

    private (JsonObject, byte[]) Build(string name, int character, byte[] shape)
    {
        var animations = new List<(string, byte[], int)>();
        string baseSet = $"OBJ/{character:X2}_COM.SEQ";
        bool own = false;
        if (Unit(character) is { } unitPath)
        {
            byte[] unit = _read(unitPath);
            int seqAt = BitConverter.ToInt32(unit, 40), seqSize = BitConverter.ToInt32(unit, 44);
            int extraAt = BitConverter.ToInt32(unit, 32), extraSize = BitConverter.ToInt32(unit, 36);
            if (seqAt + seqSize > unit.Length || extraAt + extraSize > unit.Length) throw new InvalidDataException($"{unitPath} is malformed.");
            // The unit's second set fills slots its own set leaves empty (0xFF): MAP001's knight (Z001U00) stands in
            // slot 2 of it, which the opening asks for (BATTLE.PRG 0x800AD494 looks an animation up by its slot).
            if (extraSize > 0) animations.Add(("base", unit, extraAt));
            if (seqSize > 0) { animations.Add(("base", unit, seqAt)); own = true; }
        }
        if (!own && _exists(baseSet)) { animations.Add(("base", _read(baseSet), 0)); own = true; }
        if (!own && FamilySet(character) is { } family) animations.Add(("base", family.Unit, family.At));
        if (_battleSets.TryGetValue(character, out var battleSet) && _exists(battleSet)) animations.Add(("battle", _read(battleSet), 0));
        // Event animation sets are not part of the rig: an event's set is added when the event first plays from it.
        var conversion = new CharacterConversion(name, shape, animations) { ScriptLengths = _battle[ScriptLengthsAt..(ScriptLengthsAt + 70)] };
        // A rig built before, from the same unit file and battle set, is kept in the cache: its definition and model
        // as they were made, and only its skeleton rebuilt, for the event sets added to it later.
        // Ashley's rig, the same in every game, the installation converts.
        if (character == 0 && _installed("rig_" + name) is { } rig && _installed("rig_" + name + "_definition") is { } made && JsonNode.Parse(made) is JsonObject defined)
        {
            conversion.Prepare();
            _conversions[character] = (conversion, defined);
            return (defined, rig);
        }
        string? cached = _cache == null ? null : Path.Combine(_cache, $"{name}_{(Unit(character) ?? "obj").Replace('/', '_')}_{(_battleSets.GetValueOrDefault(character) ?? "none").Replace('/', '_')}");
        if (cached != null && File.Exists(cached + ".json") && File.Exists(cached + ".glb") && JsonNode.Parse(File.ReadAllText(cached + ".json")) is JsonObject kept)
        {
            conversion.Prepare();
            _conversions[character] = (conversion, kept);
            return (kept, File.ReadAllBytes(cached + ".glb"));
        }
        var definition = conversion.Export();
        if (cached != null)
        {
            File.WriteAllBytes(cached + ".glb", conversion.Model);
            File.WriteAllText(cached + ".json", definition.ToJsonString());
        }
        _conversions[character] = (conversion, definition);
        return (definition, conversion.Model);
    }

    private readonly Dictionary<int, (CharacterConversion Conversion, JsonObject Definition)> _conversions = new();

    /// <summary>
    /// A base set for a model that has none of its own (no set in its unit file and no OBJ/XX_COM.SEQ): that of a
    /// model BATTLE.PRG's alias table puts in its family (0x85 to 0x8A answer to 0x81), from the first zone unit file
    /// (MAP/ZnnnUnn.ZUD, whose first byte is its model id) that carries one. Sets for another skeleton are left off.
    /// </summary>
    private (byte[] Unit, int At)? FamilySet(int character)
    {
        int alias = _battle[(int)(ModelAliases - 0x80068800) + character];
        var family = new HashSet<int>();
        for (int model = 0; model < 256; model++)
            if (model != character && (_battle[(int)(ModelAliases - 0x80068800) + model] == character || model == alias)) family.Add(model);
        if (family.Count == 0) return null;
        var paths = new List<string>();
        foreach (var path in _paths) if (path.StartsWith("MAP/", StringComparison.Ordinal) && path.EndsWith(".ZUD", StringComparison.Ordinal)) paths.Add(path);
        paths.Sort(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            byte[] unit = _read(path);
            int seqAt = BitConverter.ToInt32(unit, 40), seqSize = BitConverter.ToInt32(unit, 44);
            if (family.Contains(unit[0]) && seqSize > 0 && seqAt + seqSize <= unit.Length) return (unit, seqAt);
        }
        return null;
    }
    private readonly Dictionary<(int, string), List<(string, Animation)>> _setClips = new();

    /// <summary>
    /// An animation set's clips for a character, loaded as the game loads it rather than with the rig. Ashley can
    /// take up any weapon: each category's battle set is OBJ/00_BTn.SEQ ("btn") and its Break Arts OBJ/00BTnB0a.SEQ
    /// for art a ("artna"; each slot a pitch: low, level, high). An event set ("esqnnn") is OBJ/Ennn.ESQ (nnn in
    /// hex on the disc): the character's own entry, or that of the model it answers to (BATTLE.PRG's alias table).
    /// The ESQ is a count, the character ids, a flag each, then offsets from 0x28 to one SEQ per character. The
    /// clips' animation scripts join the character's definition.
    /// </summary>
    public List<(string Clip, Animation Animation)> AnimationSet(int character, string set)
    {
        if (_setClips.TryGetValue((character, set), out var known)) return known;
        var clips = new List<(string, Animation)>();
        _setClips[(character, set)] = clips;
        if (Character($"character_{character:X2}") == null) return clips;
        if (character == 0 && set.Length == 3 && set.StartsWith("bt", StringComparison.Ordinal) || character == 0 && set.Length == 5 && set.StartsWith("art", StringComparison.Ordinal))
        {
            string file = set[0] == 'b' ? $"OBJ/00_BT{set[2]}.SEQ" : $"OBJ/00BT{set[3]}B0{set[4]}.SEQ";
            var (conversion, definition) = _conversions[character];
            if (_exists(file)) clips.AddRange(conversion.Clips(set, _read(file), 0, definition["events"]!.AsObject(), Movement(definition)));
            return clips;
        }
        if (!set.StartsWith("esq", StringComparison.Ordinal) || !int.TryParse(set[3..], out int id)) return clips;
        string path = $"OBJ/E{id:X3}.ESQ";
        if (!_exists(path)) return clips;
        byte[] esq = _read(path);
        int entries = BitConverter.ToInt32(esq, 0), start = 4 + entries * 12;
        int alias = _battle[(int)(ModelAliases - 0x80068800) + character];
        foreach (int owner in new[] { character, alias })
        {
            for (int entry = 0; entry < entries && clips.Count == 0; entry++)
                if (BitConverter.ToInt32(esq, 4 + entry * 4) == owner)
                {
                    var (conversion, definition) = _conversions[character];
                    clips.AddRange(conversion.Clips(set, esq, start + BitConverter.ToInt32(esq, 4 + entries * 8 + entry * 4), definition["events"]!.AsObject(), Movement(definition)));
                }
            if (clips.Count > 0) break;
        }
        return clips;
    }

    /// <summary>A music id's sequence and instrument bank, as the executable's tables name them.</summary>
    public (byte[] Sequence, byte[] Bank)? Music(string name)
    {
        if (!name.StartsWith("music_", StringComparison.Ordinal) || !int.TryParse(name[6..], out int music)) return null;
        return (_read(_pathAt(Word(MusicSectors + music * 4))), _read(_pathAt(Word(SoundSectors + Byte(SoundFileMap + music) * 4))));
    }

    /// <summary>
    /// The bank a sound plays from and how: "se&lt;file&gt;_&lt;id&gt;_p&lt;pan&gt;" from an event bank (SE/EFFECTnn by the
    /// executable's table), "resident_…" and "effect_…" from the resident sounds, "room&lt;zzzmm&gt;_…" from a room's
    /// bank, "fx&lt;nnn&gt;_…" from an effect's and "c&lt;XX&gt;_…" from a character's model. Null when its bank lacks it.
    /// </summary>
    public (string BankName, byte[]? Bank, int Id, int Pan, int Group)? Sound(string key)
    {
        string[] parts = key.Split('_');
        if (parts.Length < 3 || !int.TryParse(parts[1], out int id) || !int.TryParse(parts[^1].TrimStart('p'), out int pan)) return null;
        string kind = parts[0];
        if (kind is "resident" or "effect") return ("", null, id, pan, kind == "resident" ? 2 : 1);
        byte[]? bank = null;
        int group = 3;
        if (kind.StartsWith("room", StringComparison.Ordinal))
        {
            group = 2;
            if (!_roomBanks.ContainsKey(kind[4..])) Data($"field_z{kind[4..7]}_m{kind[7..]}");
            bank = _roomBanks.GetValueOrDefault(kind[4..]);
        }
        else if (kind.StartsWith("fx", StringComparison.Ordinal) && _exists($"EFFECT/E{kind[2..]}.P")) bank = EffectConversion.SoundBank(_read($"EFFECT/E{kind[2..]}.P"));
        else if (kind.StartsWith("se", StringComparison.Ordinal) && int.TryParse(kind[2..], out int file)) bank = _read(_pathAt(Word(EffectSectors + file * 4)));
        else if (kind.StartsWith("c", StringComparison.Ordinal) && kind.Length == 3)
        {
            // A character's own sounds sit in its model's sound section (offset 0x128, from 0xF8), laid out as an
            // event bank: a count, then offsets from the section's start.
            group = 0;
            if (Model(Convert.ToInt32(kind[1..], 16)) is { Length: >= 0x12C } model)
            {
                int section = BitConverter.ToInt32(model, 0x128) + 0xF8;
                if (section > 0xF8 && section < model.Length) bank = model[section..];
            }
        }
        if (bank == null || !SoundDriver.HasSound(bank, id)) return null;
        return ("bank_" + kind, bank, id, pan, group);
    }
}
