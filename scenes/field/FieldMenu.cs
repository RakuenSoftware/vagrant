// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The field menu Triangle opens: the retail rows Magic, Break Arts, Battle Abilities, Items, Status, Map, Data,
/// Options, Score and Quick Manual, drawn as retail draws its command list (<see cref="MenuList"/>) with only the
/// commands that can be used. Up and down move between them, Circle opens one, and Triangle or Cross goes back. Time
/// stands still while it is open.
/// <list type="bullet">
/// <item>Items lists what Ashley carries; Circle uses a consumable, by the effects the installation read for it.</item>
/// <item>Status shows her hit and magic points, RISK, strength, intelligence and agility, and each limb's condition.</item>
/// <item>Data saves the game, while she stands on a magic circle.</item>
/// <item>Battle Abilities shows the abilities set to Circle, Triangle and Square; Options opens the settings window.</item>
/// </list>
/// Magic and Break Arts wait on spells and arts; Map, Score and the manual are not built yet.
/// </summary>
public sealed partial class FieldMenu : PanelContainer
{
    public static readonly string[] Rows = { "Magic", "Break Arts", "Battle Abilities", "Items", "Status", "Map", "Data", "Options", "Score", "Quick Manual" };
    private const int Magic = 0, Arts = 1, Abilities = 2, Items = 3, Status = 4, Map = 5, Data = 6, Options = 7, Score = 8, Manual = 9, Container = 10, Chest = 11, Reclaim = 12;
    /// <summary>The Quick Manual's open topic (-1 for the list) and the first line shown.</summary>
    private int _topic = -1, _scroll;
    /// <summary>The area the map shows: 0 for Ashley's own.</summary>
    private int _area;
    private const int ManualLines = 22;
    private MapCanvas _map = null!;
    private SettingsDialog _settings = null!;

    private FieldView _view = null!;
    private Label _text = null!;
    // Retail's command list, over the whole screen beside the panel.
    private MenuList _list = null!;
    private int _row = Items, _page = -1, _cursor;
    /// <summary>In a workshop, the blade picked to fit a grip to.</summary>
    private GDictionary? _blade, _grip;
    private readonly List<GDictionary?> _assemblyGems = new();
    private int _assemblySlot;
    /// <summary>In the Combine tab, the first blade picked (null while choosing it).</summary>
    private GDictionary? _combining;
    private GDictionary? _combineSecond, _combineResult;
    private GDictionary? _renaming;
    private readonly char[] _nameLetters = new char[20];
    private int _nameCaret, _nameColumn, _nameRow, _nameCommand = -1, _nameWait, _nameAnswer = -1;
    private string _nameCandidate = "";

    /// <summary>The loose blades, shields and armour that can be combined: all but the one already picked.</summary>
    private List<GDictionary> CombineList()
    {
        var list = _view.Parts(null, includeSet: true);
        foreach (var value in _view.Equipment)
        {
            var entry = value.AsGodotDictionary();
            if (entry["kind"].AsString() is "armour" or "shield" && entry["record"].AsGodotDictionary().GetValueOrDefault("category", 0).AsInt32() != 7) list.Add(entry);
        }
        if (_combining != null) list.RemoveAll(entry => entry.RecursiveEqual(_combining));
        if (_setupCommand == Combine && _setup is >= 7 and <= 9)
            list.RemoveAll(entry => _setupKind == 0 ? entry["kind"].AsString() is not ("blade" or "blades")
                : entry["kind"].AsString() is not ("armour" or "shield")
                    || (entry["record"].AsGodotDictionary()["id"].AsInt32() is >= 127 and <= 142) != (_setupKind == 1));
        return list;
    }

    /// <summary>What takes gems: the weapons, then the shields (each as a holder with "shield" set and its record as the entry's).</summary>
    private List<GDictionary> GemHolders()
    {
        var list = _view.Weapons();
        foreach (var piece in Armour())
            if (piece.GetValueOrDefault("id", 0).AsInt32() is >= 127 and <= 142)
                list.Add(new GDictionary { ["entry"] = new GDictionary { ["record"] = piece }, ["name"] = _view.ItemName(piece["id"].AsInt32()), ["shield"] = true });
        return list;
    }

    /// <summary>The armour and accessories Ashley carries.</summary>
    private List<GDictionary> Armour()
    {
        var list = new List<GDictionary>();
        foreach (var value in _view.Equipment)
        {
            var entry = value.AsGodotDictionary();
            if (entry["kind"].AsString() is "armour" or "shield") list.Add(entry["record"].AsGodotDictionary());
        }
        return list;
    }
    private string _note = "";

    /// <summary>Whether the menu is open.</summary>
    public bool Open => Visible;

    /// <summary>The command list's chosen row (0 MAGIC to 9 QUICK MANUAL), for the checks.</summary>
    public int Row => _row;

    /// <summary>STATUS's equipment for the checks: 0 not up, 1 the list, 2 a thing's panels; and the chosen slot.</summary>
    public (int Stage, int Slot) StatusEquipment => (_statusEquip, _slot);

    /// <summary>The map screen's chosen room's name, for the checks.</summary>
    public string MapRoomName => _map.Visible ? _map.RoomName : "";

    /// <summary>The page open over the command list (its row), or -1 on the list.</summary>
    public int Page => _page;

    /// <summary>Whether BREAK ARTS has just been refused out of battle mode, so Circle again opens it.</summary>
    private bool _artsRefused;

    /// <summary>The item menu's list rows as shown, for tests.</summary>
    public IReadOnlyList<ItemMenu.Row> ItemRows => _items.Rows;

    public void Configure(FieldView view)
    {
        _view = view;
        Visible = false;
        Position = new Vector2(760, 40);
        CustomMinimumSize = new Vector2(480, 0);
        _text = new Label();
        _text.AddThemeColorOverride("font_outline_color", Colors.Black);
        _text.AddThemeConstantOverride("outline_size", 4);
        AddChild(_text);
        _list = new MenuList { Name = "Commands", Visible = false };
        _list.Configure(view.Content.Has("hud_labels") ? view.Content.ScreenTexture("hud_labels", true) : null, view.Content.Has("hud_cursor") ? view.Content.ScreenTexture("hud_cursor") : null,
            view.Content.Has("system_font") ? view.Content.BitmapFont("system_font") : null, view.Content.HudLettering);
        _bag = new GadgetBag { Name = "GadgetBag", Visible = false };
        _bag.Configure(view.Content.Has("hud_labels") ? view.Content.ScreenTexture("hud_labels", true) : null, view.Content.Has("hud_cursor") ? view.Content.ScreenTexture("hud_cursor") : null,
            view.Content.HudLettering, view.Content.Has("system_font") ? view.Content.BitmapFont("system_font") : null,
            view.Content.Has("gadget_bag") ? view.Content.Data("gadget_bag") : null);
        // The list and the bag draw over the whole screen, so they stand beside the menu's panel rather than in it.
        TreeEntered += () =>
        {
            if (_list.GetParent() == null) _view.Screen.CallDeferred(Node.MethodName.AddChild, _list);
            if (_bag.GetParent() == null) _view.Screen.CallDeferred(Node.MethodName.AddChild, _bag);
        };
        // The item menu covers the whole screen, the HUD with it.
        var itemLayer = new CanvasLayer { Layer = 8, Name = "ItemLayer" };
        AddChild(itemLayer);
        _items = new ItemMenu { Name = "ItemMenu", Visible = false };
        _items.Configure(view.Content);
        itemLayer.AddChild(_items);
        _cards = new MemoryCardMenu { Name = "MemoryCard" };
        _cards.Configure(view.Content, view);
        itemLayer.AddChild(_cards);
        _settings = new SettingsDialog { Name = "Settings" };
        AddChild(_settings);
        // The map screen covers the field and the HUD, as the item menu does.
        _map = new MapCanvas { Name = "Map", Visible = false };
        itemLayer.AddChild(_map);
        _map.Configure(view.Content);
    }

    private GDictionary? _chest;
    private GadgetBag _bag = null!;

    // ITEMS owns its categories (0 WEAPONS to 6 MISC, 7 OPTIONS); SETUP owns every workshop page.
    private ItemMenu _items = null!;
    /// <summary>The memory card screens, over the item menu's backdrop: the container's save and a save point's.</summary>
    private MemoryCardMenu _cards = null!;
    /// <summary>Whether the memory card screens are loading a game after the game over.</summary>
    private bool _loadingGame;

    /// <summary>After the game over: the LOAD screens, over the menu's backdrop; a loaded game resumes, leaving goes to the title.</summary>
    public void OpenLoad()
    {
        Show(true);
        _page = Data;
        _loadingGame = true;
        _items.Enter();
        _items.MenuWordShown = false;
        _items.ShowAshley(null, "");
        _cards.OpenLoad(fadeOut: true);
        Draw();
    }

    /// <summary>The memory card screens' stage, empty when they are not up (<see cref="MemoryCardMenu.Showing"/>).</summary>
    public string CardStage => _cards.Showing;

    /// <summary>The item menu's help as shown, and whether it is under CAUTION.</summary>
    public (string Text, bool Caution) ItemHelpShown => _items.Help;
    private int _category = ItemMenu.Options;
    /// <summary>EQUIP's stage (0 not open, 1 the slots, 2 a slot's list) and its slot (0 WEAPON to 7 ACCESSORY).</summary>
    private int _equip, _slot, _statPage;
    /// <summary>Each slot's place among the worn armour (-3 the shield, 0 to 4 the limbs, -1 the accessory).</summary>
    private static readonly int[] SlotLimbs = { 0, -3, 0, 1, 2, 3, 4, -1 };

    /// <summary>Opens a chest on the gadget bag: Circle takes the chosen thing, Square everything, Cross or Triangle closes.</summary>
    public void OpenChest(GDictionary chest)
    {
        Show(true);
        _view.Battle?.CoverHud(true, window: true);
        _page = Chest;
        _chest = chest;
        _cursor = 0;
        Draw();
    }

    // A reclaim (MENU2.PRG's actionUnlock): the stage (0 the art's message, 1 choosing the kind, 2 choosing the
    // ability, 3 the ability's message), the art and whether an ability is due, the kind and its remaining abilities,
    // and the frames a message has left.
    private int _reclaimStage, _reclaimArt, _reclaimKind, _reclaimWait;
    // The Break Arts page (MENU1.PRG): the weapon category shown, and whether View Arts' list of categories is up.
    private int _artsCategory;
    private bool _artsCategories;
    // The Magic page (MENU0.PRG): the class open (-1 while the classes are listed).
    private int _magicClass = -1;
    private bool _reclaimAbility;
    private readonly List<int> _reclaimable = new();

    /// <summary>
    /// Reclaims what Ashley's action has earned (MENU2.PRG's actionUnlock): a Break Art first, its line under CHECK
    /// ("Ashley has reclaimed the &lt;category&gt; Break Art &lt;name&gt; from his suppressed memory."), then a battle
    /// ability: CHAIN ABILITIES or DEFENSE ABILITIES when both have some left ("Select a battle ability."), then the
    /// kind's remaining abilities in retail's order with each one's description, and its line ("Ashley has reclaimed the
    /// battle ability &lt;name&gt; from suppressed memory."). A line closes on a button or after 120 frames.
    /// </summary>
    public void OpenReclaim(int art, bool ability)
    {
        Show(true);
        // The HUD stays up, BATTLE MODE and all, with the figure put away (footage).
        _view.Battle?.CoverHud(true, window: true, banner: true);
        _page = Reclaim;
        _reclaimArt = art;
        _reclaimAbility = ability;
        _cursor = 0;
        if (art >= 0) ShowReclaimArt();
        else BeginAbilityReclaim();
        Draw();
    }

    private Godot.Collections.Array ReclaimLines => _view.Content.Data("battle_abilities")["unlock"].AsGodotArray();

    private void ShowReclaimArt()
    {
        var actions = _view.Content.Data("spells")["actions"].AsGodotDictionary();
        string name = actions.GetValueOrDefault(_reclaimArt.ToString(), new GDictionary()).AsGodotDictionary().GetValueOrDefault("name", "").AsString();
        int category = (_reclaimArt - 184) / 4 + 1;
        string kind = category < ReclaimLines.Count ? ReclaimLines[category].AsGodotDictionary()["text"].AsString() : "";
        _view.ReclaimArt(_reclaimArt);
        _view.Message(SystemBox.Fill(ReclaimLines[0].AsGodotDictionary(), new[] { kind, name }), header: 3, lines: 4, frames: 0, centred: false);
        _reclaimStage = 0;
        _reclaimWait = 120;
    }

    private List<int> Remaining(int kind)
    {
        var list = new List<int>();
        foreach (var id in _view.Content.Data("battle_abilities")[kind == 0 ? "chain_unlocks" : "defense_unlocks"].AsGodotArray())
            if (!_view.Abilities.Contains(id.AsInt32())) list.Add(id.AsInt32());
        return list;
    }

    private void BeginAbilityReclaim()
    {
        if (!_reclaimAbility) { Show(false); return; }
        bool chains = Remaining(0).Count > 0, defenses = Remaining(1).Count > 0;
        if (!chains && !defenses)
        {
            _view.Message(SystemBox.Fill(ReclaimLines[16].AsGodotDictionary()), header: 3, lines: 4, frames: 0, centred: false);
            _reclaimStage = 3;
            _reclaimWait = 120;
            return;
        }
        if (chains && defenses) { _reclaimStage = 1; _cursor = 0; return; }
        ChooseReclaimKind(chains ? 0 : 1);
    }

    private void ChooseReclaimKind(int kind)
    {
        _reclaimKind = kind;
        _reclaimable.Clear();
        _reclaimable.AddRange(Remaining(kind));
        _reclaimStage = 2;
        _cursor = 0;
    }

    private void StepReclaim(Func<string, bool> pressed, int step)
    {
        bool any = pressed("ps_circle") || pressed("ps_cross") || pressed("ps_triangle") || pressed("ps_square");
        switch (_reclaimStage)
        {
            case 0:
                if (any || --_reclaimWait <= 0) { _view.SystemBox.Text = ""; BeginAbilityReclaim(); }
                break;
            case 1:
                if (step != 0) { _cursor = 1 - _cursor; _view.MenuSound(FieldView.MenuChange); }
                if (pressed("ps_circle")) { _view.MenuSound(FieldView.MenuSelect); ChooseReclaimKind(_cursor); }
                break;
            case 2:
                if (step != 0 && _reclaimable.Count > 1) { _cursor = (_cursor + step + _reclaimable.Count) % _reclaimable.Count; _view.MenuSound(FieldView.MenuChange); }
                if (pressed("ps_circle"))
                {
                    _view.MenuSound(FieldView.MenuSelect);
                    int id = _reclaimable[_cursor];
                    _view.ReclaimAbility(id);
                    string name = AbilityName(id);
                    _view.Message(SystemBox.Fill(ReclaimLines[13].AsGodotDictionary(), new[] { "", name }), header: 3, lines: 4, frames: 0, centred: false);
                    _reclaimStage = 3;
                    _reclaimWait = 120;
                }
                // Backing out offers the kinds again, when there are two.
                else if ((pressed("ps_cross") || pressed("ps_triangle")) && Remaining(1 - _reclaimKind).Count > 0) { _view.MenuSound(FieldView.MenuLeave); _reclaimStage = 1; _cursor = _reclaimKind; }
                break;
            case 3:
                if (any || --_reclaimWait <= 0) { _view.SystemBox.Text = ""; Show(false); }
                break;
        }
    }

    /// <summary>The abilities of a kind Ashley has (0 chain, actions 22 to 39; 1 defense, 40 to 53), in the order learned.</summary>
    private List<int> KindAbilities(int kind) => _view.Abilities.FindAll(id => kind == 0 ? id is >= 22 and < 40 : id is >= 40 and <= 53);

    private string AbilityName(int id) =>
        _view.Content.Data("abilities").GetValueOrDefault(id.ToString(), new GDictionary()).AsGodotDictionary().GetValueOrDefault("name", "").AsString();

    /// <summary>The spells of a class (0 Warlock to 3 Enchanter) Ashley knows, in the menu's order.</summary>
    private List<int> ClassSpells(int magicClass)
    {
        var list = new List<int>();
        foreach (var id in _view.Content.Data("magic_menu")["classes"].AsGodotArray()[magicClass].AsGodotArray())
            if (_view.Spells.Contains(id.AsInt32())) list.Add(id.AsInt32());
        return list;
    }

    /// <summary>
    /// The Magic page (MENU0.PRG): the four classes (greyed with no spell learned), then a class's known spells under
    /// its bar, each greyed when it cannot be cast (MP short, silenced), with its MP in the cost box. Circle casts.
    /// </summary>
    private bool StepMagic(Func<string, bool> pressed, int step)
    {
        if (_magicClass < 0)
        {
            if (step != 0) { _cursor = (_cursor + step + 4) % 4; _view.MenuSound(FieldView.MenuChange); }
            if (pressed("ps_circle"))
            {
                if (ClassSpells(_cursor).Count == 0) _view.MenuSound(FieldView.MenuInvalid);
                else { _view.MenuSound(FieldView.MenuSelect); _magicClass = _cursor; _cursor = 0; }
            }
            else if (pressed("ps_triangle") || pressed("ps_cross")) { _view.MenuSound(FieldView.MenuLeave); _page = -1; _view.Battle?.CoverHud(true); }
            Draw();
            return true;
        }
        var spells = ClassSpells(_magicClass);
        if (step != 0 && spells.Count > 1) { _cursor = (_cursor + step + spells.Count) % spells.Count; _view.MenuSound(FieldView.MenuChange); }
        if (pressed("ps_circle") && spells.Count > 0)
        {
            if (!SpellCastable(spells[_cursor])) _view.MenuSound(FieldView.MenuInvalid);
            else
            {
                _view.MenuSound(FieldView.MenuSelect);
                // Casting leaves the menu, and the spell plays out in the field.
                _note = _view.Cast(spells[_cursor]);
                if (_note.Length == 0) { Show(false); return false; }
            }
        }
        else if (pressed("ps_triangle") || pressed("ps_cross")) { _view.MenuSound(FieldView.MenuLeave); _cursor = _magicClass; _magicClass = -1; }
        Draw();
        return true;
    }

    private int SpellCost(int spell) =>
        _view.Content.Data("spells")["actions"].AsGodotDictionary().GetValueOrDefault(spell.ToString(), new GDictionary()).AsGodotDictionary().GetValueOrDefault("cost", 0).AsInt32();

    private bool SpellCastable(int spell) => _view.Battle?.Ashley is { } ashley && ashley.Mp >= SpellCost(spell) && !ashley.Has(FieldBattle.Silence);

    /// <summary>The arts of a weapon category Ashley has reclaimed, in order.</summary>
    private List<int> CategoryArts(int category)
    {
        var list = new List<int>();
        for (int i = 0; i < 4; i++) if (_view.Arts.Contains(184 + (category - 1) * 4 + i)) list.Add(184 + (category - 1) * 4 + i);
        return list;
    }

    /// <summary>
    /// The Break Arts page (MENU1.PRG): the equipped category's reclaimed arts, then View Arts. An art of another
    /// category, or any out of battle mode, is greyed and refused. View Arts lists the ten categories (greyed with no art
    /// yet); Circle shows one's arts.
    /// </summary>
    private bool StepArts(Func<string, bool> pressed, int step)
    {
        if (_artsCategories)
        {
            if (step != 0) { _cursor = (_cursor + step + 10) % 10; _view.MenuSound(FieldView.MenuChange); }
            // Back on the page, the pointer stands on View Arts (Ep. 3 7:16).
            if (pressed("ps_circle")) { _view.MenuSound(FieldView.MenuSelect); _artsCategory = _cursor + 1; _artsCategories = false; _cursor = CategoryArts(_artsCategory).Count; }
            else if (pressed("ps_triangle") || pressed("ps_cross")) { _view.MenuSound(FieldView.MenuLeave); _artsCategories = false; _cursor = CategoryArts(_artsCategory).Count; }
            Draw();
            return true;
        }
        var arts = CategoryArts(_artsCategory);
        int rows = arts.Count + 1;
        if (step != 0) { _cursor = (_cursor + step + rows) % rows; _view.MenuSound(FieldView.MenuChange); }
        if (pressed("ps_circle"))
        {
            // View Arts opens on the first category, DAGGER (Ep. 3 7:14).
            if (_cursor == arts.Count) { _view.MenuSound(FieldView.MenuSelect); _artsCategories = true; _cursor = 0; }
            else if (!ArtUsable(arts[_cursor])) _view.MenuSound(FieldView.MenuInvalid);
            else
            {
                _view.MenuSound(FieldView.MenuSelect);
                _note = _view.UseArt(arts[_cursor]);
                if (_note.Length == 0) { Show(false); return false; }
            }
        }
        else if (pressed("ps_triangle") || pressed("ps_cross")) { _view.MenuSound(FieldView.MenuLeave); _page = -1; _view.Battle?.CoverHud(true); }
        Draw();
        return true;
    }

    private bool ArtUsable(int art) =>
        _view.Battle?.Ashley is { } ashley && _view.Battle.Drawn && (art - 184) / 4 + 1 == ashley.Category &&
        ashley.Hp > _view.Content.Data("spells")["actions"].AsGodotDictionary().GetValueOrDefault(art.ToString(), new GDictionary()).AsGodotDictionary().GetValueOrDefault("cost", 0).AsInt32();

    /// <summary>Opens on a container: what Ashley carries and what is stored, L1 and R1 between them, Circle moving one across.</summary>
    public void OpenContainer()
    {
        Show(true);
        _page = Container;
        // It asks first (MENU7.PRG): "Check the container?" under CAUTION, YES and NO over the field.
        _boxStage = -1;
        _boxCategory = 0;
        _pending.Clear();
        _cursor = 0;
        Draw();
    }

    /// <summary>EQUIP's eight slots under a header: each slot's name (MENU3 strings 12 to 19, the part after a slash) and what it has on.</summary>
    private void ShowSlots(Godot.Collections.Array strings, string header, bool strip)
    {
        var labels = new string[8];
        for (int i = 0; i < 8; i++) labels[i] = SystemBox.Entry(strings, 12 + i) is var line && line.IndexOf('/') is >= 0 and var at ? line[(at + 1)..] : line;
        var slots = new List<ItemMenu.Row>();
        for (int i = 0; i < 8; i++) slots.Add(SlotRow(i));
        _items.SetSlots(header, labels, slots, _slot, strip: strip);
    }

    private string CheckString(int index) =>
        SystemBox.Entry(_view.Content.Has("memory_card") ? _view.Content.Data("memory_card").GetValueOrDefault("container_check", new Godot.Collections.Array()).AsGodotArray() : new Godot.Collections.Array(), index);

    // The container (MENU/MENUD.PRG): the question over the field (stage -1: "Check the container?"), the memory
    // cards read (-2), its first menu (0: REMOVE, STORE, OK), a transfer list (1) of one category, the confirmation
    // (2), and the question as it is left (3: save, or the transaction is lost). What is picked waits in the pending
    // list until it is confirmed.
    private int _boxStage, _boxCategory, _boxConfirm, _boxAccess;

    /// <summary>The chosen thing's action in a transfer list (0 STORE or REMOVE, 1 DISCARD, 2 SORT), or -1 when they are not up; and a refusal it gave.</summary>
    private int _boxAction = -1;
    private string _boxRefusal = "";

    /// <summary>The transfer list's page of what has been picked (STORED or REMOVED), which it opens on; the categories follow it.</summary>
    private const int BoxPicked = 7;

    /// <summary>
    /// A thing's actions in a transfer list: STORE (or REMOVE) with its help (MENUD 33 and 34, 31 and 32), then
    /// ITEMHELP's DISCARD and SORT (footage: STORE, DISCARD, SORT down the left).
    /// </summary>
    private List<ItemMenu.Row> BoxActions()
    {
        var rows = new List<ItemMenu.Row>
        {
            new(BoxString(_storing ? 33 : 31), -1, -1, BoxString(_storing ? 34 : 32)),
            new(ItemHelp(671), -1, -1, ItemHelp(672)),
            new(ItemHelp(675), -1, -1, ItemHelp(676)),
        };
        if (_boxRefusal.Length > 0 && _boxAction >= 0 && _boxAction < rows.Count) rows[_boxAction] = rows[_boxAction] with { Help = _boxRefusal, Caution = true };
        return rows;
    }

    /// <summary>
    /// Discards a thing from the side being transferred from: an item one at a time, equipment whole, refusing what
    /// is worn or part of a set (ITEMHELP 664, 665). Returns a refusal, or empty.
    /// </summary>
    private string BoxDiscard(Variant thing)
    {
        if (thing.VariantType == Variant.Type.Int)
        {
            var store = _storing ? _view.Items : _view.StoredItems;
            int id = thing.AsInt32();
            if (--store[id] <= 0) store.Remove(id);
            return "";
        }
        var entry = thing.AsGodotDictionary();
        if (_storing && _view.IsWorn(entry)) return ItemHelp(664);
        if (_storing && _view.InSet(entry)) return ItemHelp(665);
        if (_storing) _view.Equipment.Remove(entry);
        else _view.Stored.Remove(entry);
        return "";
    }


    /// <summary>Whether the container is reading the memory cards, for tests.</summary>
    public bool ContainerAccessing => Visible && _page == Container && _boxStage == -2;

    /// <summary>What the container's screen says under CAUTION while it reads the cards or asks as it is left, for tests.</summary>
    public string ContainerCaution => !Visible || _page != Container ? "" : _boxStage == -2 ? CardString(22) : _boxStage == 3 ? CheckString(4) : "";

    private string CardString(int index) =>
        SystemBox.Entry(_view.Content.Has("memory_card") ? _view.Content.Data("memory_card")["strings"].AsGodotArray() : new Godot.Collections.Array(), index);
    private bool _storing;
    private readonly List<Variant> _pending = new();

    private GDictionary BoxData => _view.Content.Has("container_menu") ? _view.Content.Data("container_menu") : new GDictionary();
    private string BoxString(int index) => SystemBox.Entry(BoxData.GetValueOrDefault("strings", new Godot.Collections.Array()).AsGodotArray(), index);

    /// <summary>A thing's bag category: an equipment entry by its kind, an item id (its whole stack) as MISC.</summary>
    private static int BoxCategory(Variant thing) => thing.VariantType == Variant.Type.Int ? 6 : FieldView.BagCategory(thing.AsGodotDictionary()["kind"].AsString());

    /// <summary>What could move in the transfer: Ashley's storable things, or what the container holds, not already picked.</summary>
    private List<Variant> BoxCandidates(int category)
    {
        var list = new List<Variant>();
        foreach (var thing in ContainerList(_storing ? 0 : 1))
            if (BoxCategory(thing) == category && !_pending.Exists(p => p.VariantType == thing.VariantType && (thing.VariantType == Variant.Type.Int ? p.AsInt32() == thing.AsInt32() : p.AsGodotDictionary() == thing.AsGodotDictionary())))
                list.Add(thing);
        return list;
    }

    /// <summary>How many of each category a store holds: the bag (Ashley's) or the container.</summary>
    private int[] BoxCounts(bool bag)
    {
        var counts = new int[7];
        if (bag) return _view.BagCounts();
        foreach (var value in _view.Stored) counts[FieldView.BagCategory(value.AsGodotDictionary()["kind"].AsString())]++;
        foreach (var (_, count) in _view.StoredItems) if (count > 0) counts[6]++;
        return counts;
    }

    /// <summary>A thing as a list row: an item's stack with its count, a piece of equipment with its icon.</summary>
    private ItemMenu.Row BoxRow(Variant thing)
    {
        if (thing.VariantType == Variant.Type.Int)
        {
            int id = thing.AsInt32();
            int count = (_storing ? _view.Items : _view.StoredItems).GetValueOrDefault(id);
            var item = _view.BagEntry(new GDictionary { ["kind"] = "item", ["id"] = id, ["count"] = count });
            return new ItemMenu.Row(item.Name, count, -1, item.Help);
        }
        var entry = thing.AsGodotDictionary();
        string kind = entry["kind"].AsString();
        if (kind == "weapons")
            foreach (var weapon in _view.Weapons())
                if (weapon["entry"].AsGodotDictionary().RecursiveEqual(entry)) return WeaponRow(weapon) with { Badge = _storing && _view.Holds(weapon) ? 2 : 0 };
        var record = entry["record"].AsGodotDictionary();
        var shown = _view.BagEntry(new GDictionary { ["kind"] = kind switch { "blades" => "blade", "grips" => "grip", _ => kind }, ["record"] = record });
        // Ashley's own things carry their SET and EQP badges (footage: the container's BLADES).
        return new ItemMenu.Row(_view.EquipmentName(entry), -1, shown.Icon, shown.Help, shown.Material, Badge: _storing ? _view.Badge(entry) : 0);
    }

    private ItemMenu.Container BoxView((string, string, string, int)? confirm = null, string empty = "")
    {
        var data = BoxData;
        string[] Words(string key) => data.GetValueOrDefault(key, new Godot.Collections.Array()).AsStringArray();
        var limits = data.GetValueOrDefault("limits", new Godot.Collections.Array()).AsGodotArray();
        var boxLimits = new int[7];
        for (int i = 0; i < 7 && i < limits.Count; i++) boxLimits[i] = limits[i].AsInt32();
        return new ItemMenu.Container(Words("categories"), BoxCounts(true), GadgetBag.Limits, BoxCounts(false), boxLimits,
            data.GetValueOrDefault("bag", "").AsString(), data.GetValueOrDefault("container", "").AsString(), data.GetValueOrDefault("slash", "/").AsString(),
            data.GetValueOrDefault("ok", "OK").AsString(), Words("buttons"), confirm, empty);
    }

    /// <summary>The container's input: its menu, a transfer list, or the confirmation.</summary>
    private bool StepContainer(Func<string, bool> pressed, int step)
    {
        if (_boxStage == -1)
        {
            if (step != 0) { _cursor = 1 - _cursor; _view.MenuSound(FieldView.MenuChange); }
            bool yes = pressed("ps_circle") && _cursor == 0;
            if (yes)
            {
                // The cards are read first, the CONTAINER header over the backdrop.
                _view.MenuSound(FieldView.MenuSelect);
                _boxStage = -2;
                _boxAccess = 0;
                _items.Enter();
                _items.ShowAshley(null, "");
                _view.SystemBox.Text = "";
            }
            else if (pressed("ps_circle") || pressed("ps_triangle") || pressed("ps_cross"))
            {
                _view.MenuSound(FieldView.MenuLeave);
                _view.SystemBox.Text = "";
                Show(false);
                return false;
            }
            return true;
        }
        if (_boxStage == -2)
        {
            if (++_boxAccess < MemoryCardMenu.AccessFrames) return true;
            _boxStage = 0;
            _cursor = 0;
            return true;
        }
        if (_boxStage == 3)
        {
            // YES saves (the container's contents are kept on the memory card); NO or leaving goes back to the field,
            // the transfer kept only until the game is next left unsaved (footage: Cross back to the field, then DATA).
            if (step != 0) { _cursor = 1 - _cursor; _view.MenuSound(FieldView.MenuChange); }
            if (pressed("ps_circle") && _cursor == 0)
            {
                _view.MenuSound(FieldView.MenuSelect);
                _cards.OpenSave(_view.ContainerLocation);
            }
            else if (pressed("ps_circle") || pressed("ps_triangle") || pressed("ps_cross"))
            {
                _view.MenuSound(FieldView.MenuLeave);
                Show(false);
                return false;
            }
            return true;
        }
        if (_boxStage == 0)
        {
            if (step != 0) _cursor = (_cursor + step + 3) % 3;
            if (pressed("ps_circle"))
            {
                // OK ends the transfer, asking first whether to save: the container's contents are kept on the card.
                if (_cursor == 2) { LeaveContainer(); return true; }
                _storing = _cursor == 1;
                _boxStage = 1;
                _boxCategory = BoxPicked;
                _boxAction = -1;
                _pending.Clear();
                _cursor = 0;
            }
            else if (pressed("ps_triangle") || pressed("ps_cross")) LeaveContainer();
            return true;
        }
        if (_boxStage == 2)
        {
            if (step != 0) _boxConfirm = 1 - _boxConfirm;
            if (pressed("ps_circle") && _boxConfirm == 0)
            {
                foreach (var thing in _pending)
                {
                    if (thing.VariantType == Variant.Type.Int) _view.MoveItems(thing.AsInt32(), _storing);
                    else if (_storing) _view.Store(thing.AsGodotDictionary());
                    else _view.Retrieve(thing.AsGodotDictionary());
                }
                _pending.Clear();
                _boxStage = 0;
                _cursor = _storing ? 1 : 0;
            }
            else if (pressed("ps_circle") || pressed("ps_cross") || pressed("ps_triangle")) _boxStage = 1;
            return true;
        }
        var candidates = _boxCategory == BoxPicked ? new List<Variant>(_pending) : BoxCandidates(_boxCategory);
        if (_boxAction >= 0)
        {
            // A thing's actions: up and down choose, Circle does it, Cross or Triangle puts them away.
            if (step != 0) { _boxAction = (_boxAction + step + 3) % 3; _boxRefusal = ""; _view.MenuSound(FieldView.MenuChange); }
            if (pressed("ps_triangle") || pressed("ps_cross")) { _boxAction = -1; _boxRefusal = ""; _view.MenuSound(FieldView.MenuLeave); }
            else if (pressed("ps_circle") && candidates.Count > 0)
            {
                var thing = candidates[Math.Min(_cursor, candidates.Count - 1)];
                if (_boxAction == 1)
                {
                    _boxRefusal = BoxDiscard(thing);
                    _view.MenuSound(_boxRefusal.Length > 0 ? FieldView.MenuInvalid : 28);
                    if (_boxRefusal.Length > 0) return true;
                }
                else
                {
                    _view.MenuSound(FieldView.MenuSelect);
                    if (_boxAction == 0) _pending.Add(thing);
                }
                _boxAction = -1;
                _cursor = Math.Max(0, Math.Min(_cursor, BoxCandidates(_boxCategory).Count - 1));
            }
            return true;
        }
        int across = (pressed("ps_right") || pressed("ps_r1") ? 1 : 0) - (pressed("ps_left") || pressed("ps_l1") ? 1 : 0);
        if (across != 0) { _boxCategory = (_boxCategory + across + 8) % 8; _cursor = 0; }
        if (candidates.Count > 0 && step != 0) _cursor = (_cursor + step + candidates.Count) % candidates.Count;
        if (pressed("ps_circle") && candidates.Count > 0)
        {
            // On the picked page Circle puts a thing back; elsewhere it opens the thing's actions.
            if (_boxCategory == BoxPicked)
            {
                _pending.RemoveAt(Math.Min(_cursor, candidates.Count - 1));
                _cursor = Math.Max(0, Math.Min(_cursor, _pending.Count - 1));
            }
            else { _boxAction = 0; _boxRefusal = ""; }
        }
        // Triangle cancels the transfer; Cross ends it, asking first when something was picked.
        else if (pressed("ps_triangle")) { _pending.Clear(); _boxStage = 0; _cursor = _storing ? 1 : 0; }
        else if (pressed("ps_cross"))
        {
            if (_pending.Count > 0) { _boxStage = 2; _boxConfirm = 0; }
            else { _boxStage = 0; _cursor = _storing ? 1 : 0; }
        }
        return true;
    }

    /// <summary>
    /// Asks, as the container is left, "The container transaction will be cancelled if game is not saved. Save?"
    /// (MENU7.PRG) under CAUTION, YES chosen, YES and NO at (194, 148) over the backdrop under CONTAINER.
    /// </summary>
    private void LeaveContainer()
    {
        _view.MenuSound(FieldView.MenuSelect);
        _boxStage = 3;
        _cursor = 0;
    }

    /// <summary>Shows the container's screen for its stage.</summary>
    private void DrawContainer()
    {
        if (_boxStage is -2 or 3)
        {
            if (_boxStage == -2) _items.SetHeader(BoxString(0), CardString(22));
            else _items.SetHeader(BoxString(0), CheckString(4), new[] { new ItemMenu.Row(CheckString(1), -1, -1, ""), new ItemMenu.Row(CheckString(2), -1, -1, "") }, _cursor);
            return;
        }
        if (_boxStage == 0)
        {
            var rows = new List<ItemMenu.Row> { new(BoxString(12), -1, -1, BoxString(13)), new(BoxString(14), -1, -1, BoxString(15)), new(BoxView().Ok, -1, -1, BoxString(1)) };
            _items.SetContainerTop(BoxString(0), rows, _cursor, BoxView());
            return;
        }
        var counts = BoxCounts(_storing);
        int[] limits = _storing ? GadgetBag.Limits : BoxView().BoxLimits;
        if (_boxStage == 2)
        {
            var picked = _pending.ConvertAll(BoxRow);
            _items.SetContainerList(BoxString(_storing ? 17 : 16), 7, BoxString(_storing ? 26 : 25), $"{_pending.Count}/{limits[6],3}", picked, 0,
                BoxView((BoxString(_storing ? 11 : 10), BoxString(8).TrimStart('G'), BoxString(9).TrimStart('G'), _boxConfirm)));
            return;
        }
        if (_boxCategory == BoxPicked)
        {
            // What has been picked, with what the list is for when it is empty.
            _items.SetContainerList(BoxString(_storing ? 17 : 16), BoxPicked, BoxString(_storing ? 26 : 25), $"{_pending.Count}/{limits[6],3}",
                _pending.ConvertAll(BoxRow), _cursor, BoxView(empty: BoxString(_storing ? 30 : 29)));
            return;
        }
        var candidates = BoxCandidates(_boxCategory);
        _items.SetContainerList(BoxString(_storing ? 17 : 16), _boxCategory, BoxString(18 + _boxCategory), $"{counts[_boxCategory]}/{limits[_boxCategory],3}",
            candidates.ConvertAll(BoxRow), _cursor, BoxView(empty: BoxString(_storing ? 28 : 27)), _boxAction >= 0 ? BoxActions() : null, Math.Max(0, _boxAction));
    }

    /// <summary>The container page's list: equipment entries, then item ids, from Ashley (tab 0) or the container.</summary>
    private List<Variant> ContainerList(int tab)
    {
        var list = new List<Variant>();
        if (tab == 0) foreach (var entry in _view.Storable()) list.Add(entry);
        else foreach (var entry in _view.Stored) list.Add(entry);
        var items = new List<int>(tab == 0 ? _view.Items.Keys : _view.StoredItems.Keys);
        items.Sort();
        foreach (int id in items) list.Add(id);
        return list;
    }

    public void Show(bool open)
    {
        Visible = open;
        // A shortcut's page kept the field dark, as the shortcut menu had it.
        if (_shortcut) _view.DimField(0);
        _shortcut = false;
        _itemAction = -1;
        _actionRefusal = "";
        _setup = -1;
        _setupTarget = null;
        _view.Battle?.CoverHud(open);
        _page = -1;
        _note = "";
        Draw();
    }

    /// <summary>Whether a page was opened from the shortcut menu: it then has no command list behind it, nor MENU over it.</summary>
    private bool _shortcut;
    // The classes of magic on the shortcut menu's buttons: triangle, square, circle and cross (0 Warlock to 3 Enchanter).
    private static readonly int[] ShortcutClasses = { 1, 2, 0, 3 };

    /// <summary>
    /// A shortcut command's name (the game's manual, "Shortcut Commands": ITEMS, DEFENSE, CHAIN and BREAK ARTS on the
    /// direction pad, 0 to 3 as up, left, right and down; SHAMAN, SORCERER, WARLOCK and ENCHANTER on triangle, square,
    /// circle and cross, 4 to 7): the menu's own name for it, a kind of ability or a class of magic by its first word.
    /// A class Ashley has no spell of has none.
    /// </summary>
    public string ShortcutName(int command)
    {
        var names = _view.Content.Has("battle_text") ? _view.Content.Data("battle_text")["menu"].AsGodotArray() : new Godot.Collections.Array();
        string Name(int row) => row < names.Count ? names[row].AsGodotDictionary()["text"].AsString() : Rows[row].ToUpperInvariant();
        if (command == 0) return Name(Items);
        if (command == 3) return Name(Arts);
        if (command is 1 or 2)
        {
            if (!_view.Content.Has("battle_abilities")) return command == 1 ? "DEFENSE" : "CHAIN";
            var strings = _view.Content.Data("battle_abilities")["strings"].AsGodotArray();
            int at = command == 1 ? 3 : 0;
            return at < strings.Count ? strings[at].AsGodotDictionary()["text"].AsString().Split(' ')[0] : "";
        }
        if (!_view.Content.Has("magic_menu") || ClassSpells(ShortcutClasses[command - 4]).Count == 0) return "";
        var classes = _view.Content.Data("magic_menu")["strings"].AsGodotArray();
        int name = ShortcutClasses[command - 4] * 3;
        return name < classes.Count ? classes[name].AsGodotDictionary()["text"].AsString().Split(' ')[0] : "";
    }

    /// <summary>Whether a shortcut command can be used now: Break Arts only in battle mode, the rest once Ashley has something there.</summary>
    public bool ShortcutUsable(int command) => command switch
    {
        0 => true,
        1 or 2 => Usable(Abilities),
        3 => Usable(Arts) && _view.Battle?.Drawn == true,
        _ => ShortcutName(command).Length != 0,
    };

    /// <summary>
    /// Opens a shortcut command's page at once, with no command list behind it, so leaving the page closes the menu
    /// (Episode 4 at 3:15 and 4:38). ITEMS opens the item menu on MISC, ready to use a thing; DEFENSE and CHAIN the two
    /// kinds of battle ability, the pointer on theirs; BREAK ARTS its page; a class of magic its spells. Returns
    /// false, opening nothing, for a command that cannot be used now.
    /// </summary>
    public bool OpenShortcut(int command)
    {
        if (!ShortcutUsable(command)) return false;
        Show(true);
        _shortcut = true;
        _cursor = 0;
        _topic = -1;
        _area = 0;
        _abilityKind = -1;
        if (command == 0)
        {
            _page = Items;
            _category = 6;
            _items.Enter();
            _items.ShowAshley(_view.Player, $"{_view.Battle?.Ashley?.BattleSet ?? "battle"}_1");
        }
        else
        {
            if (command is 1 or 2) { _page = Abilities; _cursor = command == 1 ? 1 : 0; }
            else if (command == 3) { _page = Arts; _artsCategory = Math.Max(1, _view.Battle?.Ashley?.Category ?? 1); _artsCategories = false; }
            else { _page = Magic; _magicClass = ShortcutClasses[command - 4]; }
            _view.Battle?.CoverHud(true, window: true, banner: true);
            // The field stays as dark under the page as under the shortcut menu (Episode 4 at 3:16).
            _view.DimField(FieldView.ShortcutDim);
        }
        Draw();
        return true;
    }

    private bool Usable(int row) => row is Items or Status or Data or Options or Score || row == Manual && _view.Content.Has("manual") || row == Map && _view.Document.GetValueOrDefault("scene", -1).AsInt32() > 0 || row == Magic && _view.Spells.Count > 0 || row == Arts && _view.Arts.Count > 0 || row == Abilities && _view.Abilities.Count > 0;

    /// <summary>
    /// The item menu's rows for its category: OPTIONS' EQUIP and SETUP (MENU3.PRG strings 8 to 11), the things Ashley
    /// carries of the bag's other categories (weapons whole, then loose blades, grips, shields, armour and gems), and
    /// her items with their counts under MISC.
    /// </summary>
    private List<ItemMenu.Row> CategoryRows()
    {
        var rows = new List<ItemMenu.Row>();
        var strings = _view.Content.Has("item_menu") ? _view.Content.Data("item_menu")["strings"].AsGodotArray() : new Godot.Collections.Array();
        if (_category == ItemMenu.Options)
        {
            rows.Add(new ItemMenu.Row(SystemBox.Entry(strings, 8), -1, -1, SystemBox.Entry(strings, 9)));
            rows.Add(new ItemMenu.Row(SystemBox.Entry(strings, 10), -1, -1, SystemBox.Entry(strings, 11)));
            return rows;
        }
        if (_category == 6)
        {
            foreach (int id in Consumables())
            {
                var entry = _view.BagEntry(new GDictionary { ["kind"] = "item", ["id"] = id, ["count"] = _view.Items[id] });
                rows.Add(new ItemMenu.Row(entry.Name, entry.Count, -1, entry.Help));
            }
            return rows;
        }
        if (_category == 0)
        {
            foreach (var weapon in _view.Weapons()) rows.Add(WeaponRow(weapon) with { Badge = _view.Holds(weapon) ? 2 : 0 });
            return rows;
        }
        foreach (var held in CategoryEntries())
        {
            var entry = _view.BagEntry(new GDictionary { ["kind"] = held["kind"].AsString().TrimEnd('s'), ["record"] = held["record"] });
            rows.Add(new ItemMenu.Row(entry.Name, -1, entry.Icon, entry.Help, entry.Material, Badge: _view.Badge(held)));
        }
        return rows;
    }

    /// <summary>
    /// The equipment a category from BLADES to GEMS lists, in the order it is kept: the parts of the starting sets
    /// ("blades", "grips") and loose things ("blade", "grip", the shields, armour and gems) alike.
    /// </summary>
    private List<GDictionary> CategoryEntries()
    {
        var list = new List<GDictionary>();
        foreach (var value in _view.Equipment)
            if (FieldView.BagCategory(value.AsGodotDictionary()["kind"].AsString()) == _category) list.Add(value.AsGodotDictionary());
        return list;
    }

    /// <summary>A weapon as the item menu lists it: its name and icon, and "Class: Weapon" with its name as help.</summary>
    private ItemMenu.Row WeaponRow(GDictionary weapon)
    {
        var entry = _view.BagEntry(new GDictionary { ["kind"] = "weapon", ["record"] = new GDictionary { ["name"] = weapon["name"], ["blade"] = weapon["blade"], ["gems"] = weapon.GetValueOrDefault("gems", new Godot.Collections.Array()) } });
        return new ItemMenu.Row(entry.Name, -1, entry.Icon, entry.Help, entry.Material);
    }

    /// <summary>A shield, armour or accessory as the item menu lists it.</summary>
    private ItemMenu.Row ArmourRow(GDictionary record)
    {
        int id = record.GetValueOrDefault("id", 0).AsInt32();
        var entry = _view.BagEntry(new GDictionary { ["kind"] = id is >= 127 and <= 142 ? "shield" : "armour", ["record"] = record });
        return new ItemMenu.Row(entry.Name, -1, entry.Icon, entry.Help, entry.Material);
    }

    /// <summary>What a slot has on: its row, or NONE and "Nothing equipped." (MENU3.PRG strings 20 and 21).</summary>
    private ItemMenu.Row SlotRow(int slot)
    {
        var strings = _view.Content.Has("item_menu") ? _view.Content.Data("item_menu")["strings"].AsGodotArray() : new Godot.Collections.Array();
        if (slot == 0)
        {
            foreach (var weapon in _view.Weapons()) if (_view.Holds(weapon)) return WeaponRow(weapon);
        }
        else if (_view.EquippedArmour.TryGetValue(SlotLimbs[slot], out var worn) && worn.Count > 0) return ArmourRow(worn);
        return new ItemMenu.Row(SystemBox.Entry(strings, 20), -1, -1, SystemBox.Entry(strings, 21));
    }

    /// <summary>
    /// What could go in a slot: the weapons not in hand, or the carried pieces of the slot's kind (shields 127 to 142,
    /// helms 143 to 158, body armour 159 to 174, leg armour 175 to 190, arm armour 191 to 206, accessories 207 to 253)
    /// that are not worn.
    /// </summary>
    private List<GDictionary> SlotCandidates(int slot)
    {
        var list = new List<GDictionary>();
        if (slot == 0) { foreach (var weapon in _view.Weapons()) if (!_view.Holds(weapon)) list.Add(weapon); return list; }
        var (low, high) = slot switch { 1 => (127, 142), 4 => (143, 158), 5 => (159, 174), 6 => (175, 190), 7 => (207, 253), _ => (191, 206) };
        foreach (var piece in Armour())
        {
            int id = piece.GetValueOrDefault("id", 0).AsInt32();
            if (id < low || id > high) continue;
            if (System.Linq.Enumerable.Any(_view.EquippedArmour.Values, worn => worn.RecursiveEqual(piece))) continue;
            list.Add(piece);
        }
        return list;
    }

    /// <summary>What a slot has on: the weapon in hand (with its blade and grip) or the piece worn there; null for none.</summary>
    private GDictionary? Worn(int slot)
    {
        if (slot == 0) { foreach (var weapon in _view.Weapons()) if (_view.Holds(weapon)) return weapon; return null; }
        return _view.EquippedArmour.TryGetValue(SlotLimbs[slot], out var worn) && worn.Count > 0 ? worn : null;
    }

    /// <summary>
    /// The panels for <paramref name="thing"/> in <paramref name="slot"/>'s list: a weapon's blade DP and PP, its
    /// blade's classes and affinities and its grip's types, and RANGE, RISK and its strength, intelligence and agility
    /// (blade and grip together) against Ashley's; a piece of armour's DP and PP, its own values, and her defence
    /// with it on (her strength, intelligence and agility and those of all she wears) against now.
    /// </summary>
    private ItemMenu.Stats SlotStats(int slot, GDictionary? thing)
    {
        var labels = _view.Content.Has("equip_labels") ? _view.Content.Data("equip_labels") : new GDictionary();
        string[] Words(string key) => labels.GetValueOrDefault(key, new Godot.Collections.Array()).AsStringArray();
        int[] Values(GDictionary? record, string key, int count)
        {
            var values = new int[count];
            var list = record?.GetValueOrDefault(key, new Godot.Collections.Array()).AsGodotArray() ?? new Godot.Collections.Array();
            for (int i = 0; i < count && i < list.Count; i++) values[i] = list[i].AsInt32();
            return values;
        }
        int Value(GDictionary? record, string key) => record?.GetValueOrDefault(key, 0).AsInt32() ?? 0;
        var pages = Words("pages");
        var pageLabels = Words(_statPage switch { 0 => "classes", 1 => "affinities", _ => "types" });
        var ashley = _view.Battle?.Ashley;
        int strength = ashley?.Strength ?? 0, intelligence = ashley?.Intelligence ?? 0, agility = ashley?.Agility ?? 0;
        var weaponWords = Words("weapon");
        var armourWords = Words("armour");
        if (slot == 0)
        {
            var blade = thing?.GetValueOrDefault("blade", new GDictionary()).AsGodotDictionary();
            var grip = thing?.GetValueOrDefault("grip", new GDictionary()).AsGodotDictionary();
            var held = Worn(0);
            var heldBlade = held?.GetValueOrDefault("blade", new GDictionary()).AsGodotDictionary();
            var heldGrip = held?.GetValueOrDefault("grip", new GDictionary()).AsGodotDictionary();
            int[] values = _statPage switch { 0 => Values(blade, "classes", 6), 1 => Values(blade, "affinities", 7), _ => Values(grip, "types", 3) };
            int Sum(GDictionary? a, GDictionary? b, string key) => Value(a, key) + Value(b, key);
            var range = blade?.GetValueOrDefault("range", new Godot.Collections.Array()).AsGodotArray();
            // Ashley's with it (MAINMENU's ORG/EQP): RANGE adds her reach to the blade's; the stats add her own and her
            // accessory's to the weapon's.
            int reach = ashley?.Reach ?? 0;
            var accessory = _view.EquippedArmour.GetValueOrDefault(-1);
            var heldRange = heldBlade?.GetValueOrDefault("range", new Godot.Collections.Array()).AsGodotArray();
            int Range(Godot.Collections.Array? r) => r is { Count: > 0 } ? r[0].AsInt32() : 0;
            var rows = new (string, int, int, int)[weaponWords.Length];
            for (int i = 0; i < weaponWords.Length; i++)
                rows[i] = i switch
                {
                    0 => (weaponWords[i], Range(range), Range(range) + reach, Range(heldRange) + reach),
                    1 => (weaponWords[i], Value(blade, "cost"), Value(blade, "cost"), Value(blade, "cost")),
                    2 => (weaponWords[i], Sum(blade, grip, "strength"), strength + Value(accessory, "strength") + Sum(blade, grip, "strength"), strength + Value(accessory, "strength") + Sum(heldBlade, heldGrip, "strength")),
                    3 => (weaponWords[i], Sum(blade, grip, "intelligence"), intelligence + Value(accessory, "intelligence") + Sum(blade, grip, "intelligence"), intelligence + Value(accessory, "intelligence") + Sum(heldBlade, heldGrip, "intelligence")),
                    _ => (weaponWords[i], Sum(blade, grip, "agility"), agility + Value(accessory, "agility") + Sum(blade, grip, "agility"), agility + Value(accessory, "agility") + Sum(heldBlade, heldGrip, "agility")),
                };
            return new ItemMenu.Stats(Value(blade, "dp"), Value(blade, "max_dp"), Value(blade, "pp"), Value(blade, "max_pp"), labels.GetValueOrDefault("dp", "DP").AsString(), labels.GetValueOrDefault("pp", "PP").AsString(),
                _statPage, pages, pageLabels, values, labels.GetValueOrDefault("org_eqp", "").AsString(), rows, true);
        }
        int[] own = _statPage switch { 0 => Values(thing, "classes", 6), 1 => Values(thing, "affinities", 7), _ => Values(thing, "types", 3) };
        // Her defence where the piece goes: her own, the piece's and her accessory's (MAINMENU's ORG/EQP).
        var worn = Worn(slot);
        var wornAccessory = slot == 7 ? null : _view.EquippedArmour.GetValueOrDefault(-1);
        int Wearing(string key, GDictionary? piece) => Value(piece, key) + Value(wornAccessory, key);
        var armourRows = new (string, int, int, int)[armourWords.Length];
        string[] keys = { "strength", "intelligence", "agility" };
        int[] bases = { strength, intelligence, agility };
        for (int i = 0; i < armourWords.Length && i < 3; i++)
            armourRows[i] = (armourWords[i], Value(thing, keys[i]), bases[i] + Wearing(keys[i], thing), bases[i] + Wearing(keys[i], worn));
        return new ItemMenu.Stats(Value(thing, "dp"), Value(thing, "max_dp"), Value(thing, "pp"), Value(thing, "max_pp"), labels.GetValueOrDefault("dp", "DP").AsString(), labels.GetValueOrDefault("pp", "PP").AsString(),
            _statPage, pages, pageLabels, own, labels.GetValueOrDefault("org_eqp", "").AsString(), armourRows, false);
    }

    /// <summary>A consumable's id, from 323 to 356.</summary>
    /// <summary>The chosen thing's action, while its actions are open (MISC: 0 USE, 1 DISCARD, 2 DISCARD ALL, 3 SORT; equipment: 0 SEARCH, 1 DISCARD, 2 SORT), or -1.</summary>
    private int _itemAction = -1;

    /// <summary>
    /// SETUP's page (MENU12.BIN): -1 when not open; 0 its commands; 1 WEAPONS or SHIELDS, for ATTACH GEMS or
    /// DISASSEMBLE; 2 the weapons or shields (or, for RENAME, the weapons); 3 the gems to set in the chosen one; 4 YES
    /// or NO to take it apart.
    /// </summary>
    private int _setup = -1;
    private int _setupCommand, _setupKind, _setupCursor;
    private GDictionary? _setupTarget;
    /// <summary>What the last setup did, shown as the information until the cursor moves.</summary>
    private string _setupNote = "";

    private string SetupString(int index, params string[] values) =>
        SystemBox.Entry(_view.Content.Has("setup_menu") ? _view.Content.Data("setup_menu")["list"].AsGodotArray() : new Godot.Collections.Array(), index, values);

    /// <summary>SETUP's commands: ASSEMBLE WEAPONS, ATTACH GEMS, DISASSEMBLE, RENAME, REPAIR and COMBINE (MENU12 words 1 to 11, each with its help after).</summary>
    private const int Assemble = 0, AttachGems = 1, Disassemble = 2, Rename = 3, Repair = 4, Combine = 5;

    /// <summary>ASSEMBLE, REPAIR and COMBINE need a workshop: elsewhere they stand greyed, "This command can only be used at a workshop." (50) under CAUTION.</summary>
    private bool SetupGreyed(int command) => command is Assemble or Repair or Combine && !_view.Workshop;

    /// <summary>The weapons or shields SETUP's lists show.</summary>
    private List<GDictionary> SetupHolders() => _setupKind == 0 ? _view.Weapons() : GemHolders().FindAll(holder => holder.ContainsKey("shield"));

    /// <summary>SETUP's page as drawn: its headers, its rows (with icons for things), and its information.</summary>
    private (List<string> Headers, List<ItemMenu.Row> Rows, bool Icons) SetupPage()
    {
        var rows = new List<ItemMenu.Row>();
        var headers = new List<string> { SetupString(0) };
        bool icons = false;
        switch (_setup)
        {
            case 0:
                for (int c = 0; c < 6; c++)
                    rows.Add(SetupGreyed(c) ? new ItemMenu.Row(SetupString(1 + 2 * c), -1, -1, SetupString(50), Greyed: true, Caution: true) : new ItemMenu.Row(SetupString(1 + 2 * c), -1, -1, SetupString(2 + 2 * c)));
                break;
            case 1:
                headers.Add(SetupString(1 + 2 * _setupCommand));
                rows.Add(new ItemMenu.Row(SetupString(28), -1, -1, SetupString(_setupCommand == AttachGems ? 29 : 37)));
                rows.Add(new ItemMenu.Row(SetupString(30), -1, -1, SetupString(_setupCommand == AttachGems ? 31 : 38)));
                break;
            case 2:
            {
                icons = true;
                headers = new List<string> { SetupString(_setupCommand switch { AttachGems => 32 + _setupKind, Disassemble => 41 + _setupKind, _ => 7 }) };
                foreach (var holder in SetupHolders())
                {
                    var row = holder.ContainsKey("shield")
                        ? ArmourRow(holder["entry"].AsGodotDictionary()["record"].AsGodotDictionary())
                        : WeaponRow(holder);
                    if (_setupCommand == AttachGems)
                    {
                        // What cannot take a gem stands greyed, saying so; the rest say how many it takes, both under
                        // CAUTION (footage).
                        int slots = FieldView.GemSlots(holder);
                        row = slots == 0
                            ? row with { Help = SetupString(59 + 2 * _setupKind), Greyed = true, Caution = true }
                            : row with { Help = SetupString(58 + 2 * _setupKind, slots.ToString()), Caution = true };
                    }
                    else row = row with { Help = SetupString(_setupCommand == Rename ? 23 : 26 + _setupKind) };
                    rows.Add(row);
                }
                break;
            }
            case 3:
                icons = true;
                headers = new List<string> { SetupString(32 + _setupKind) };
                foreach (var gem in _view.Gems())
                {
                    var entry = _view.BagEntry(new GDictionary { ["kind"] = "gem", ["record"] = gem["record"] });
                    rows.Add(new ItemMenu.Row(entry.Name, -1, entry.Icon, entry.Help, entry.Material));
                }
                break;
            case 4:
            {
                if (_setupCommand == Repair)
                {
                    headers.Add(SetupString(9));
                    foreach (int word in new[] { 34, 35 }) rows.Add(new ItemMenu.Row(SetupString(word), -1, -1, SetupString(45), Caution: true));
                    break;
                }
                headers = new List<string> { SetupString(41 + _setupKind) };
                bool held = _setupTarget != null && _setupKind == 0 && _view.Holds(_setupTarget);
                string question = SetupString(_setupKind == 0 ? (held ? 40 : 39) : 43);
                rows.Add(new ItemMenu.Row(SetupString(34), -1, -1, question, Caution: true));
                rows.Add(new ItemMenu.Row(SetupString(35), -1, -1, question, Caution: true));
                break;
            }
            case 5:
                headers.Add(SetupString(9));
                break;
            case 6:
                headers.Add(SetupString(11));
                for (int i = 0; i < 3; i++) rows.Add(new ItemMenu.Row(SetupString(70 + i), -1, -1, SetupString(73 + i)));
                break;
            case 7:
            case 8:
                icons = true;
                headers = new List<string> { SetupString(82 + _setupKind) };
                foreach (var entry in CombineList())
                {
                    var record = entry["record"].AsGodotDictionary();
                    var item = _view.BagEntry(entry);
                    bool refused = !_view.Works(record);
                    int badge = entry.ContainsKey("assembly") ? _view.Holds(entry["assembly"].AsGodotDictionary()) ? 2 : 1 : _view.Badge(entry);
                    rows.Add(new ItemMenu.Row(item.Name, -1, item.Icon, SetupString(refused ? 20 + _setupKind : 76 + _setupKind), item.Material, Greyed: refused, Caution: refused, Badge: badge));
                }
                break;
            case 9:
                headers = new List<string> { SetupString(79 + _setupKind), _combineResult == null ? "" : _view.EquipmentName(_combineResult) };
                bool undo = _combining != null && (_view.Badge(_combining) != 0 || _combining.ContainsKey("assembly"))
                    || _combineSecond != null && (_view.Badge(_combineSecond) != 0 || _combineSecond.ContainsKey("assembly"));
                foreach (int word in new[] { 34, 35, 85 }) rows.Add(new ItemMenu.Row(SetupString(word), -1, -1, undo ? SetupString(86) : "", Caution: undo));
                break;
            case 10:
                headers = new List<string> { SetupString(62) };
                foreach (var (entry, empty, help) in new[] { (_blade, 14, 54), (_grip, 15, 55) })
                {
                    GadgetBag.Entry? item = entry == null ? null : _view.BagEntry(entry);
                    rows.Add(new ItemMenu.Row(item?.Name ?? SetupString(empty), -1, item?.Icon ?? -1, SetupString(help), item?.Material ?? -1));
                }
                foreach (var gem in _assemblyGems)
                {
                    GadgetBag.Entry? item = gem == null ? null : _view.BagEntry(gem);
                    rows.Add(new ItemMenu.Row(item?.Name ?? SetupString(16), -1, item?.Icon ?? -1, SetupString(56), item?.Material ?? -1));
                }
                rows.Add(new ItemMenu.Row(NameString(9), -1, -1, SetupString(57), Greyed: _blade == null || _grip == null));
                break;
            case 11:
            case 12:
            case 13:
                headers = new List<string> { SetupString(_setup == 11 ? 63 : _setup == 12 ? 65 : 67) };
                icons = true;
                var entries = _setup == 13 ? _view.Gems() : _view.Parts(null, grips: _setup == 12);
                foreach (var entry in entries)
                {
                    if (_setup == 13 && _assemblyGems.Exists(g => g != null && g.RecursiveEqual(entry))) continue;
                    var item = _view.BagEntry(entry);
                    bool material = _setup == 11 && !_view.Works(entry["record"].AsGodotDictionary());
                    bool fits = _setup == 11 ? _grip == null || FieldView.Fits(entry, _grip) : _setup != 12 || _blade == null || FieldView.Fits(_blade, entry);
                    rows.Add(new ItemMenu.Row(item.Name, -1, item.Icon, SetupString(material ? 20 : !fits ? _setup == 11 ? 19 : 18 : _setup == 11 ? 64 : _setup == 12 ? 66 : 68), item.Material, Greyed: material || !fits, Caution: material || !fits));
                }
                break;
            case 14:
                headers = new List<string> { SetupString(62) };
                foreach (int word in new[] { 34, 35, 36 }) rows.Add(new ItemMenu.Row(SetupString(word), -1, -1, SetupString(69), Caution: true));
                break;

        }
        // What the last setup did stands as the information until the cursor moves.
        if (_setupNote.Length > 0 && _setupCursor < rows.Count) rows[_setupCursor] = rows[_setupCursor] with { Help = _setupNote, Caution = false };
        return (headers, rows, icons);
    }

    /// <summary>
    /// A frame of SETUP. Up and down choose; Circle goes on (a greyed row refuses with the invalid sound), Cross and
    /// Triangle go back a page, from the commands to OPTIONS. Assembly keeps its components loose until YES.
    /// </summary>
    private bool StepSetup(Func<string, bool> pressed, int step)
    {
        if (_setup == 10)
        {
            if (pressed("ps_triangle")) { _setup = 0; _setupCursor = Assemble; _blade = _grip = null; _assemblyGems.Clear(); _view.MenuSound(FieldView.MenuLeave); Draw(); return true; }
            if (pressed("ps_cross"))
            {
                if (_setupCursor == 0) _blade = null;
                else if (_setupCursor == 1) { _grip = null; _assemblyGems.Clear(); }
                else if (_setupCursor - 2 < _assemblyGems.Count) _assemblyGems[_setupCursor - 2] = null;
                else { _setup = 0; _setupCursor = Assemble; }
                _view.MenuSound(FieldView.MenuLeave); Draw(); return true;
            }
            if (pressed("ps_start"))
            {
                if (_blade != null && _grip != null) { _setup = 14; _setupCursor = 1; _view.MenuSound(FieldView.MenuSelect); }
                else _view.MenuSound(FieldView.MenuInvalid);
                Draw(); return true;
            }
        }
        if ((_setup is >= 7 and <= 9 || _setup is >= 11 and <= 13) && pressed("ps_square"))
        { _statPage = (_statPage + 1) % 3; _view.MenuSound(FieldView.MenuChange); Draw(); return true; }
        if (_setup == 5)
        {
            if (pressed("ps_circle") || pressed("ps_cross") || pressed("ps_triangle")) { _view.MenuSound(FieldView.MenuLeave); _setup = 0; _setupCursor = Repair; }
            Draw();
            return true;
        }
        var (_, rows, _) = SetupPage();
        if (rows.Count > 1 && step != 0) { _setupCursor = (_setupCursor + step + rows.Count) % rows.Count; _setupNote = ""; _view.MenuSound(FieldView.MenuChange); }
        if (pressed("ps_triangle") || pressed("ps_cross"))
        {
            _view.MenuSound(FieldView.MenuLeave);
            _setupNote = "";
            switch (_setup)
            {
                case 0: _setup = -1; _cursor = 1; _items.ShowAshley(_view.Player, $"{_view.Battle?.Ashley?.BattleSet ?? "battle"}_1"); break;
                case 1: _setup = 0; _setupCursor = _setupCommand; break;
                case 2: _setup = _setupCommand == Rename ? 0 : 1; _setupCursor = _setupCommand == Rename ? Rename : _setupKind; break;
                case 4 when _setupCommand == Repair: _setup = 0; _setupCursor = Repair; break;
                case 11: case 12: case 13: case 14: _setup = 10; _setupCursor = _assemblySlot; break;
                case 6: _setup = 0; _setupCursor = Combine; break;
                case 7: _setup = 6; _setupCursor = _setupKind; _combining = null; break;
                case 8: _setup = 7; _setupCursor = 0; _combining = null; break;
                case 9: _setup = 8; _setupCursor = 0; _combineSecond = _combineResult = null; break;
                default: _setup = 2; _setupCursor = Math.Max(0, SetupHolders().IndexOf(_setupTarget!)); _setupTarget = null; break;
            }
            Draw();
            return true;
        }
        if (!pressed("ps_circle") || rows.Count == 0) { Draw(); return true; }
        int chosen = Math.Min(_setupCursor, rows.Count - 1);
        if (rows[chosen].Greyed) { _view.MenuSound(FieldView.MenuInvalid); Draw(); return true; }
        _view.MenuSound(FieldView.MenuSelect);
        _setupNote = "";
        switch (_setup)
        {
            case 0:
                _setupCommand = chosen;
                if (chosen is AttachGems or Disassemble) { _setup = 1; _setupCursor = 0; }
                else if (chosen == Rename) { _setup = 2; _setupKind = 0; _setupCursor = 0; }
                else if (chosen == Repair) { _setup = 4; _setupCursor = 0; }
                else if (chosen == Combine) { _setup = 6; _setupCursor = 0; _combining = _combineSecond = _combineResult = null; }
                else if (chosen == Assemble)
                {
                    if (_view.BagCounts()[0] >= GadgetBag.Limits[0]) { _setupNote = SetupString(51); _view.MenuSound(FieldView.MenuInvalid); }
                    else { _setup = 10; _setupCursor = 0; _blade = _grip = null; _assemblyGems.Clear(); }
                }
                break;
            case 10:
                _assemblySlot = chosen;
                if (chosen == 2 + _assemblyGems.Count) { _setup = 14; _setupCursor = 1; }
                else { _setup = chosen == 0 ? 11 : chosen == 1 ? 12 : 13; _setupCursor = 0; }
                break;
            case 11:
                _blade = _view.Parts(null)[chosen]; _setup = 10; _setupCursor = 0;
                break;
            case 12:
                _grip = _view.Parts(null, grips: true)[chosen];
                int slots = _grip["record"].AsGodotDictionary().GetValueOrDefault("gem_slots", 0).AsInt32();
                while (_assemblyGems.Count > slots) _assemblyGems.RemoveAt(_assemblyGems.Count - 1);
                while (_assemblyGems.Count < slots) _assemblyGems.Add(null);
                _setup = 10; _setupCursor = 1;
                break;
            case 13:
                _assemblyGems[_assemblySlot - 2] = _view.Gems().FindAll(g => !_assemblyGems.Exists(selected => selected != null && selected.RecursiveEqual(g)))[chosen];
                _setup = 10; _setupCursor = _assemblySlot;
                break;
            case 14:
                if (chosen == 0)
                {
                    _view.Assemble(_blade!, _grip!);
                    var weapon = _view.Weapons().Find(w => w["blade"].AsGodotDictionary().RecursiveEqual(_blade!["record"].AsGodotDictionary()))!;
                    foreach (var gem in _assemblyGems) if (gem != null) _view.Attach(weapon, gem);
                    _setup = 0; _setupCursor = Assemble; _blade = _grip = null; _assemblyGems.Clear();
                    NameWeapon(weapon, assembled: true);
                }
                else if (chosen == 1) { _setup = 10; _setupCursor = _assemblySlot; }
                else { _setup = 0; _setupCursor = Assemble; _blade = _grip = null; _assemblyGems.Clear(); }
                break;
            case 1:
                _setupKind = chosen;
                _setup = 2;
                _setupCursor = 0;
                break;
            case 6:
                _setupKind = chosen; _setup = 7; _setupCursor = 0;
                break;
            case 7:
                _combining = CombineList()[chosen]; _setup = 8; _setupCursor = 0;
                break;
            case 8:
                _combineSecond = CombineList()[chosen];
                _setupNote = _view.Combine(_combining!, _combineSecond, out _combineResult, commit: false);
                if (_combineResult != null) { _setup = 9; _setupCursor = 1; _setupNote = ""; }
                else _view.MenuSound(FieldView.MenuInvalid);
                break;
            case 9:
                if (chosen == 0)
                {
                    _setupNote = _view.Combine(_combining!, _combineSecond!, out _);
                    _setup = 7; _setupCursor = 0; _combining = null;
                }
                else if (chosen == 1) { _setup = 8; _setupCursor = 0; }
                else { _setup = 6; _setupCursor = _setupKind; _combining = null; }
                _combineSecond = _combineResult = null;
                break;
            case 2:
                _setupTarget = SetupHolders()[chosen];
                if (_setupCommand == AttachGems) { _setup = 3; _setupCursor = 0; }
                else if (_setupCommand == Disassemble) { _setup = 4; _setupCursor = 1; }
                else
                {
                    NameWeapon(_setupTarget!);
                    _setupTarget = null;
                }
                break;
            case 3:
                _setupNote = _view.Attach(_setupTarget!, _view.Gems()[chosen]);
                _setup = 2;
                _setupCursor = Math.Max(0, SetupHolders().IndexOf(_setupTarget!));
                _setupTarget = null;
                break;
            case 4:
                if (_setupCommand == Repair)
                {
                    if (chosen == 0) { _view.MenuSound(13); _view.RepairAll(); _setup = 5; _setupCursor = 0; }
                    else { _setup = 0; _setupCursor = Repair; }
                    break;
                }
                if (chosen == 0)
                {
                    if (_setupKind == 0) _setupNote = _view.Disassemble(_setupTarget!);
                    else
                    {
                        // A shield comes apart by giving up its gems.
                        while (_view.Detach(_setupTarget!) is var taken && taken.StartsWith("The ", StringComparison.Ordinal)) _setupNote = taken;
                        if (_setupNote.Length == 0) _setupNote = SetupString(25);
                    }
                }
                _setup = 2;
                _setupCursor = 0;
                _setupTarget = null;
                break;
        }
        Draw();
        return true;
    }

    /// <summary>A refusal the chosen action gave, shown under CAUTION in place of its help until the cursor moves.</summary>
    private string _actionRefusal = "";

    private string ItemHelp(int index)
    {
        var help = _view.Content.Has("item_help") ? _view.Content.Data("item_help")["list"].AsGodotArray() : new Godot.Collections.Array();
        return index < help.Count ? SystemBox.Fill(help[index].AsGodotDictionary()) : "";
    }

    /// <summary>
    /// The chosen thing's actions, ITEMHELP.BIN's words each with its help (the entry after): a MISC thing's USE,
    /// DISCARD, DISCARD ALL and SORT (667, 671, 673, 675); a blade's, grip's or gem's SEARCH, DISCARD and SORT (669,
    /// 671, 675; SEARCH finds what it is attached to, so shields and armour, never attached, are left without), SEARCH greyed with "This item is not part of a set, and thus cannot be searched." (677) under CAUTION when
    /// it is no part of a set (footage: a loose gem).
    /// </summary>
    private List<ItemMenu.Row> ItemActions()
    {
        var rows = new List<ItemMenu.Row>();
        if (_category == 6)
            foreach (int word in new[] { 667, 671, 673, 675 }) rows.Add(new ItemMenu.Row(ItemHelp(word), -1, -1, ItemHelp(word + 1)));
        else
        {
            var entries = CategoryEntries();
            bool inSet = _cursor < entries.Count && _view.InSet(entries[_cursor]);
            rows.Add(inSet ? new ItemMenu.Row(ItemHelp(669), -1, -1, ItemHelp(670)) : new ItemMenu.Row(ItemHelp(669), -1, -1, ItemHelp(677), Greyed: true, Caution: true));
            rows.Add(new ItemMenu.Row(ItemHelp(671), -1, -1, ItemHelp(672)));
            rows.Add(new ItemMenu.Row(ItemHelp(675), -1, -1, ItemHelp(676)));
        }
        if (_actionRefusal.Length > 0 && _itemAction >= 0 && _itemAction < rows.Count) rows[_itemAction] = rows[_itemAction] with { Help = _actionRefusal, Caution = true };
        return rows;
    }

    /// <summary>
    /// A piece of equipment's chosen action. SEARCH finds the weapon a set's part belongs to (WEAPONS, with it chosen);
    /// greyed, it refuses with the invalid sound. DISCARD drops a loose thing, refusing what is worn (664) or part of a
    /// set (665). SORT leaves the list in the order it is kept.
    /// </summary>
    private void EquipmentAction()
    {
        var entries = CategoryEntries();
        if (_cursor >= entries.Count) { _itemAction = -1; return; }
        var entry = entries[_cursor];
        switch (_itemAction)
        {
            case 0:
            {
                if (!_view.InSet(entry)) { _view.MenuSound(FieldView.MenuInvalid); return; }
                _view.MenuSound(FieldView.MenuSelect);
                var weapons = _view.Weapons();
                var record = entry["record"].AsGodotDictionary();
                string part = entry["kind"].AsString() == "blades" ? "blade" : "grip";
                int found = weapons.FindIndex(weapon => weapon[part].AsGodotDictionary().RecursiveEqual(record));
                _itemAction = -1;
                if (found < 0) return;
                _category = 0;
                _cursor = found;
                return;
            }
            case 1:
                if (_view.IsWorn(entry)) { _actionRefusal = ItemHelp(664); _view.MenuSound(FieldView.MenuInvalid); return; }
                if (_view.InSet(entry)) { _actionRefusal = ItemHelp(665); _view.MenuSound(FieldView.MenuInvalid); return; }
                _view.MenuSound(28);
                _view.Equipment.Remove(entry);
                _itemAction = -1;
                _cursor = Math.Min(_cursor, Math.Max(0, CategoryEntries().Count - 1));
                return;
            default:
                _view.MenuSound(FieldView.MenuSelect);
                _itemAction = -1;
                return;
        }
    }

    private List<int> Consumables()
    {
        var list = new List<int>();
        foreach (var (id, count) in _view.Items) if (count > 0) list.Add(id);
        list.Sort();
        return list;
    }

    private string NameString(int index, params string[] values) => SystemBox.Entry(_view.Content.Data("name_menu")["strings"].AsGodotArray(), index, values);

    /// <summary>MENU8 0x80105314: twenty character cells, the caret and keyboard at zero, and eight entrance frames.</summary>
    private void NameWeapon(GDictionary weapon, bool assembled = false)
    {
        _renaming = weapon;
        Array.Fill(_nameLetters, ' ');
        string name = assembled ? "" : weapon["name"].AsString();
        name.CopyTo(0, _nameLetters, 0, Math.Min(name.Length, _nameLetters.Length));
        _nameCaret = _nameColumn = _nameRow = 0;
        _nameCommand = _nameAnswer = -1;
        _nameWait = 8;
        _items.ShowAshley(null, "");
    }

    /// <summary>MENU8's keyboard: replace a cell with Circle; L1/R1 move the caret, Triangle inserts a space,
    /// Square deletes at the caret and Cross backspaces. Start or OK opens Yes/No/Cancel change.</summary>
    private void StepName(Func<string, bool> pressed)
    {
        if (_nameWait > 0) { _nameWait--; Draw(); return; }
        int vertical = (pressed("ps_down") ? 1 : 0) - (pressed("ps_up") ? 1 : 0);
        if (_nameAnswer >= 0)
        {
            if (vertical != 0) { _nameAnswer = (_nameAnswer + vertical + 3) % 3; _view.MenuSound(FieldView.MenuChange); }
            if (pressed("ps_cross")) { _nameAnswer = -1; _view.MenuSound(FieldView.MenuLeave); }
            else if (pressed("ps_circle"))
            {
                _view.MenuSound(FieldView.MenuSelect);
                if (_nameAnswer == 0) { _note = _view.Rename(_renaming!, _nameCandidate); _renaming = null; }
                else if (_nameAnswer == 2) _renaming = null;
                _nameAnswer = -1;
            }
            Draw(); return;
        }
        int caret = (pressed("ps_r1") ? 1 : 0) - (pressed("ps_l1") ? 1 : 0);
        if (caret != 0) { _nameCaret = (_nameCaret + caret + 20) % 20; _view.MenuSound(FieldView.MenuChange); }
        bool insert = pressed("ps_triangle"), delete = pressed("ps_square"), back = pressed("ps_cross"), confirm = pressed("ps_start");
        if (_nameCommand >= 0)
        {
            if (vertical != 0) { _nameCommand = (_nameCommand + vertical + 4) % 4; _view.MenuSound(FieldView.MenuChange); }
            if (pressed("ps_right")) { _nameRow = _nameCommand == 3 ? 1 : 0; _nameColumn = 0; _nameCommand = -1; _view.MenuSound(FieldView.MenuChange); }
            else if (pressed("ps_circle"))
            {
                confirm |= _nameCommand == 0; back |= _nameCommand == 1;
                delete |= _nameCommand == 2; insert |= _nameCommand == 3;
            }
        }
        else
        {
            if (vertical != 0) { _nameRow = (_nameRow + vertical + 9) % 9; _view.MenuSound(FieldView.MenuChange); }
            if (pressed("ps_left"))
            {
                if (_nameColumn == 0) _nameCommand = _nameRow == 0 ? 2 : 3; else _nameColumn--;
                _view.MenuSound(FieldView.MenuChange);
            }
            else if (pressed("ps_right")) { _nameColumn = (_nameColumn + 1) % 14; _view.MenuSound(FieldView.MenuChange); }
            if (pressed("ps_circle"))
            {
                var grid = NameString(13).Split('\n');
                _nameLetters[_nameCaret] = grid[_nameRow][_nameColumn];
                _nameCaret = Math.Min(19, _nameCaret + 1);
                _view.MenuSound(FieldView.MenuChange);
            }
        }
        if (back && _nameCaret > 0) { _nameCaret--; delete = true; }
        if (delete)
        {
            for (int i = _nameCaret; i < 19; i++) _nameLetters[i] = _nameLetters[i + 1];
            _nameLetters[19] = ' '; _view.MenuSound(FieldView.MenuChange);
        }
        if (insert)
        {
            for (int i = 19; i > _nameCaret; i--) _nameLetters[i] = _nameLetters[i - 1];
            _nameLetters[_nameCaret] = ' '; _view.MenuSound(FieldView.MenuChange);
        }
        if (confirm)
        {
            _nameCandidate = new string(_nameLetters).TrimEnd();
            if (_nameCandidate.Length == 0) _nameCandidate = _view.ItemName(_renaming!["blade"].AsGodotDictionary()["id"].AsInt32());
            _nameAnswer = 0; _view.MenuSound(FieldView.MenuSelect);
        }
        Draw();
    }

    /// <summary>Advances one frame with the buttons newly pressed; returns false once the menu has closed.</summary>
    public bool Step(Func<string, bool> pressed)
    {
        if (!Visible) return false;
        // Options opens the settings window; the menu waits while it is open.
        if (_settings.Visible) return true;
        if (_renaming != null)
        {
            StepName(pressed);
            return true;
        }
        if (_page == Options) { _page = -1; Draw(); return true; }
        if (_cards.Open)
        {
            // Saving or leaving closes the container; a save point's returns to the menu.
            if (_cards.Step(pressed) is var result && result != 0)
            {
                if (_loadingGame) { _loadingGame = false; _view.AfterGameOver(result > 0 ? _cards.Loaded : null); Show(false); return false; }
                // DATA's LOAD resumes the loaded game; backing out returns to the page.
                if (_dataLoading) { _dataLoading = false; if (result > 0) { _view.AfterGameOver(_cards.Loaded); Show(false); return false; } Draw(); return true; }
                if (_page == Data) { Draw(); return true; }
                if (_page == Container) { Show(false); return false; }
                _page = -1;
            }
            Draw();
            return true;
        }
        int step = (pressed("ps_down") ? 1 : 0) - (pressed("ps_up") ? 1 : 0);
        // A page opened from the shortcut menu has no command list to go back to.
        if (_page < 0 && _shortcut) { Show(false); return false; }
        if (_page < 0)
        {
            // The command list's sounds (BATTLE.PRG's list): the cursor's change, Circle's select, Triangle's and Cross's leave.
            int was = _row;
            if (step != 0)
                do _row = (_row + step + Rows.Length) % Rows.Length; while (!Usable(_row));
            if (_row != was) _view.MenuSound(FieldView.MenuChange);
            if (_row != was) _artsRefused = false;
            // BREAK ARTS is refused outside battle mode (BATTLE.PRG's menu reasons, under CAUTION); Circle again while
            // the refusal stands opens the page anyway, its arts greyed (Ep. 3 6:57: the one press clears the caution
            // and the page slides in; 7:56: the first press refuses).
            bool refuse = pressed("ps_circle") && _row == Arts && _view.Battle?.Drawn != true && !(_artsRefused && _view.SystemBox.Text.Length > 0);
            if (pressed("ps_circle") && !refuse) _view.MenuSound(FieldView.MenuSelect);
            else if (pressed("ps_triangle") || pressed("ps_cross")) _view.MenuSound(FieldView.MenuLeave);
            if (refuse)
            {
                _view.MenuSound(FieldView.MenuInvalid);
                var text = _view.Content.Data("battle_text");
                _view.Message(SystemBox.Fill(text["menu_refusal"].AsGodotDictionary(), new[] { text["menu_reasons"].AsGodotArray()[2].AsGodotDictionary()["text"].AsString() }), header: 1, lines: 4, frames: 90, centred: false);
                _artsRefused = true;
                Draw();
                return true;
            }
            if (pressed("ps_circle") && _row == Arts && _artsRefused) { _view.SystemBox.Text = ""; _artsRefused = false; }
            if (pressed("ps_circle") && _row == Magic) { _magicClass = -1; _view.Battle?.CoverHud(true, window: true); }
            if (pressed("ps_circle") && _row == Arts) { _artsCategory = Math.Max(1, _view.Battle?.Ashley?.Category ?? 1); _artsCategories = false; _view.Battle?.CoverHud(true, window: true); }
            if (pressed("ps_circle")) { _page = _row; _cursor = 0; _note = ""; _topic = -1; _area = 0; _abilityKind = -1; if (_page == Abilities) _view.Battle?.CoverHud(true, window: true); if (_page == Data) { _cursor = 0; _items.Enter(); _items.ShowAshley(null, ""); } if (_page == Options) _settings.Open(_view); if (_page == Items) { _category = ItemMenu.Options; _items.Enter(); _items.ShowAshley(_view.Player, $"{_view.Battle?.Ashley?.BattleSet ?? "battle"}_1"); } if (_page == Map && _view.Content.Has("map_menu")) _map.Open(_view, 0, _view.Gameplay.Yaw); if (_page == Status && _view.Content.Has("status_menu")) { _statusChosen = 0; _defencePage = -1; _statusEquip = 0; _statusWho = null; _items.Enter(); _items.ShowAshley(_view.Player, $"{_view.Battle?.Ashley?.BattleSet ?? "battle"}_1"); } }
            else if (pressed("ps_triangle") || pressed("ps_cross")) { Show(false); return false; }
        }
        else if (_page == Items && _equip > 0)
        {
            if (_equip == 1)
            {
                if (step != 0) { _slot = (_slot + step + 8) % 8; _view.MenuSound(FieldView.MenuChange); }
                if (pressed("ps_circle")) { _equip = 2; _cursor = 0; _view.MenuSound(FieldView.MenuSelect); }
                else if (pressed("ps_triangle") || pressed("ps_cross")) { _equip = 0; _cursor = 0; _view.MenuSound(FieldView.MenuLeave); }
            }
            else
            {
                var candidates = SlotCandidates(_slot);
                int count = candidates.Count + 1;
                if (step != 0 && count > 1) { _cursor = (_cursor + step + count) % count; _view.MenuSound(FieldView.MenuChange); }
                // Square turns the panel's page: CLASS, AFFINITY, TYPE.
                if (pressed("ps_square")) _statPage = (_statPage + 1) % 3;
                if (pressed("ps_circle"))
                {
                    // Taking up a weapon or shield sounds 0x21, the empty row 0x22 (MENU3.PRG); the armour a choice's.
                    _view.MenuSound(_slot is 0 or 1 ? (_cursor == 0 ? 0x22 : 0x21) : FieldView.MenuSelect);
                    if (_cursor > 0)
                    {
                        var choice = candidates[_cursor - 1];
                        if (_slot == 0) _view.Equip(choice);
                        else _view.Wear(choice, _slot is 2 or 3 ? SlotLimbs[_slot] : null);
                    }
                    _equip = 1;
                }
                else if (pressed("ps_triangle") || pressed("ps_cross")) { _equip = 1; _view.MenuSound(FieldView.MenuLeave); }
            }
        }
        else if (_page == Data) StepData(pressed, step);
        else if (_page == Map && _view.Content.Has("map_menu"))
        {
            int left = _map.Step(_view, pressed, _view.Gameplay.Yaw);
            if (left == 2) { Show(false); return false; }
            if (left == 1) _page = -1;
        }
        else if (_page == Status && _view.Content.Has("status_menu")) { if (!StepStatus(pressed)) return false; }
        else if (_page == Reclaim) StepReclaim(pressed, step);
        else if (_page == Abilities)
        {
            // BATTLE ABILITIES: CHAIN ABILITIES and DEFENSE ABILITIES; Circle opens one (still drawn as text).
            if (_abilityKind < 0)
            {
                if (step != 0) { _cursor = 1 - _cursor; _view.MenuSound(FieldView.MenuChange); }
                if (pressed("ps_circle"))
                {
                    // A kind Ashley has none of yet does not open (its help says so).
                    if (KindAbilities(_cursor).Count == 0) _view.MenuSound(FieldView.MenuInvalid);
                    else { _abilityKind = _cursor; _cursor = 0; _view.MenuSound(FieldView.MenuSelect); }
                }
                else if (pressed("ps_triangle") || pressed("ps_cross")) { _page = -1; _view.MenuSound(FieldView.MenuLeave); _view.Battle?.CoverHud(true); }
            }
            else
            {
                // A kind's abilities: up and down choose one, Circle, Triangle or Square puts it on that button (taking
                // it off any other), and Cross goes back to the kinds (Episode 4 at 10:04 to 10:16).
                var list = KindAbilities(_abilityKind);
                if (list.Count > 1 && step != 0) { _cursor = (_cursor + step + list.Count) % list.Count; _view.MenuSound(FieldView.MenuChange); }
                var slots = _abilityKind == 0 ? _view.ChainButtons : _view.DefenseButtons;
                string[] buttons = { "ps_circle", "ps_triangle", "ps_square" };
                for (int b = 0; b < 3 && list.Count > 0; b++)
                {
                    if (!pressed(buttons[b])) continue;
                    int id = list[Math.Min(_cursor, list.Count - 1)];
                    for (int other = 0; other < 3; other++) if (slots[other] == id) slots[other] = 0;
                    slots[b] = id;
                    _view.MenuSound(FieldView.MenuSelect);
                    break;
                }
                if (pressed("ps_cross")) { _cursor = _abilityKind; _abilityKind = -1; _view.MenuSound(FieldView.MenuLeave); }
            }
        }
        else if (_page == Items && _setup >= 0) return StepSetup(pressed, step);
        else if (_page == Items)
        {
            // With a thing's actions up, up and down choose among them and Cross or Triangle puts them away.
            if (_itemAction >= 0 && !pressed("ps_circle"))
            {
                int actions = ItemActions().Count;
                if (step != 0) { _itemAction = (_itemAction + step + actions) % actions; _actionRefusal = ""; _view.MenuSound(FieldView.MenuChange); }
                if (pressed("ps_triangle") || pressed("ps_cross")) { _itemAction = -1; _actionRefusal = ""; _view.MenuSound(FieldView.MenuLeave); }
                Draw();
                return true;
            }
            int across = (pressed("ps_right") || pressed("ps_r1") ? 1 : 0) - (pressed("ps_left") || pressed("ps_l1") ? 1 : 0);
            if (across != 0) { _category = (_category + across + 8) % 8; _cursor = 0; _view.MenuSound(FieldView.MenuChange); }
            var rows = CategoryRows();
            if (rows.Count > 1 && step != 0) { _cursor = (_cursor + step + rows.Count) % rows.Count; _view.MenuSound(FieldView.MenuChange); }
            if (pressed("ps_circle") && rows.Count > 0)
            {
                // Discarding sounds 28 (MENU3.PRG); anything else a choice's. Equipment's actions sound their own.
                bool equipmentAction = _category is 1 or 2 or 5 && _itemAction >= 0;
                if (!equipmentAction) _view.MenuSound(_category == 6 && _itemAction is 1 or 2 ? 28 : FieldView.MenuSelect);
                if (equipmentAction) EquipmentAction();
                else if (_category is 1 or 2 or 5) { _itemAction = 0; _actionRefusal = ""; }
                else if (_category == ItemMenu.Options)
                {
                    // EQUIP opens its slots; SETUP its page of commands, over the backdrop alone.
                    if (_cursor == 0) { _equip = 1; _slot = 0; }
                    else { _setup = 0; _setupCursor = 0; _setupNote = ""; _items.ShowAshley(null, ""); }
                    _cursor = 0;
                }
                else if (_category == 6 && _itemAction < 0) _itemAction = 0;
                else if (_category == 6 && _itemAction != 0)
                {
                    // DISCARD drops one, DISCARD ALL the whole stack; SORT orders the list (by item, as it is kept).
                    var items = Consumables();
                    if (_cursor < items.Count && _itemAction is 1 or 2)
                    {
                        int id = items[_cursor];
                        if (_itemAction == 2 || --_view.Items[id] <= 0) _view.Items.Remove(id);
                        _cursor = Math.Min(_cursor, Math.Max(0, Consumables().Count - 1));
                    }
                    _itemAction = -1;
                }
                else if (_category == 6)
                {
                    // Using an item leaves the menu; it takes effect in the field.
                    _itemAction = -1;
                    var items = Consumables();
                    if (_cursor < items.Count)
                    {
                        int id = items[_cursor];
                        var definition = _view.Content.Data("consumables").GetValueOrDefault(id.ToString(), new GDictionary()).AsGodotDictionary();
                        bool grimoire = definition.GetValueOrDefault("grimoire", false).AsBool();
                        _learned = "";
                        Show(false);
                        if (grimoire)
                        {
                            // A grimoire is cast as the spell it teaches (footage, Ep. 2 29:50: Grimoire Guerir opens
                            // the sphere on Ashley as Heal, "Ashley Riot HP 59/100%", spends its MP, `Spell: "Heal"`,
                            // then "Mastered the spell"); it is learned and used up on Circle. Where it cannot be cast
                            // it is learned at once and named (entry 23).
                            int spell = GrimoireSpell(definition["action"].AsInt32());
                            if (!_view.Spells.Contains(spell) && _view.Cast(spell, () => Read(id, spell)).Length == 0) return false;
                            _note = Use(id);
                            if (_learned.Length > 0) _view.Announce(23, _learned);
                            else if (_note.Length > 0) _view.Message(_note);
                            return false;
                        }
                        // Anything else is aimed first, the sphere on Ashley ("Ashley Riot 100%", "TARGET: Single"),
                        // and takes effect on Circle; retail then names the effect (menu table entry 27).
                        void TakeEffect()
                        {
                            string note = Use(id);
                            if (definition.ContainsKey("name")) _view.Announce(27, definition["name"].AsString());
                            else if (note.Length > 0) _view.Message(note);
                        }
                        if (_view.Battle is not { Ashley: { } holder } battle || !battle.AimAt(new List<(FieldBattle.Combatant, List<int>)> { (holder, new List<int> { -1 }) }, null, (_, _) => battle.UseItem(definition, () => Use(id)), definition)) TakeEffect();
                        return false;
                    }
                }
            }
            else if (pressed("ps_triangle") || pressed("ps_cross")) { _page = -1; _view.MenuSound(FieldView.MenuLeave); }
        }
        else
        {
            if (_page == Chest && _chest != null)
            {
                var contents = _view.ChestContents(_chest);
                // The bag's sounds (MENUB.PRG): the cursor's change, 13 as a thing is taken, leaving on END.
                if (contents.Count > 1 && step != 0) { _cursor = (_cursor + step + contents.Count) % contents.Count; _view.MenuSound(FieldView.MenuChange); }
                if (pressed("ps_circle") && contents.Count > 0) { _view.TakeFromChest(_chest, Math.Min(_cursor, contents.Count - 1)); _view.MenuSound(13); }
                else if (pressed("ps_square"))
                {
                    _view.MenuSound(contents.Count > 0 ? 13 : FieldView.MenuInvalid);
                    while (_view.ChestContents(_chest).Count > 0) _view.TakeFromChest(_chest, 0);
                }
                else if (pressed("ps_triangle") || pressed("ps_cross")) { _view.MenuSound(FieldView.MenuLeave); _chest = null; Show(false); return false; }
                contents = _view.ChestContents(_chest);
                if (contents.Count == 0) { _chest = null; Show(false); return false; }
                _cursor = Math.Min(_cursor, contents.Count - 1);
                Draw();
                return true;
            }
            if (_page == Container)
            {
                bool open = StepContainer(pressed, step);
                Draw();
                return open;
            }
            if (_page == Manual)
            {
                // The topic list, then a topic's text: up and down scroll it, Triangle or Cross goes back.
                var topics = _view.Content.Data("manual")["topics"].AsGodotArray();
                if (_topic < 0)
                {
                    if (step != 0) _cursor = (_cursor + step + topics.Count) % topics.Count;
                    if (pressed("ps_circle")) { _topic = _cursor; _scroll = 0; }
                    else if (pressed("ps_triangle") || pressed("ps_cross")) _page = -1;
                }
                else
                {
                    int lines = topics[_topic].AsGodotDictionary()["lines"].AsGodotArray().Count;
                    _scroll = Math.Clamp(_scroll + step, 0, Math.Max(0, lines - ManualLines));
                    if (pressed("ps_triangle") || pressed("ps_cross")) _topic = -1;
                }
                Draw();
                return true;
            }
            if (_page == Magic && _view.Content.Has("magic_menu")) return StepMagic(pressed, step);
            if (_page == Arts) return StepArts(pressed, step);
            if (_page == Magic && pressed("ps_circle") && _view.Spells.Count > 0)
            {
                // Casting leaves the menu, and the spell plays out in the field.
                _note = _view.Cast(_view.Spells[Math.Min(_cursor, _view.Spells.Count - 1)]);
                if (_note.Length == 0) { Show(false); return false; }
            }
            else if (pressed("ps_triangle") || pressed("ps_cross")) { _page = -1; _note = ""; }
        }
        Draw();
        return true;
    }

    private bool _dataLoading;

    /// <summary>
    /// DATA's page (MENU7.PRG): SAVE (only on a magic circle; elsewhere it refuses with the refusal's sound) opens the
    /// memory card screens to save, LOAD to load a game and resume it; Triangle or Cross goes back to the commands.
    /// </summary>
    private void StepData(Func<string, bool> pressed, int step)
    {
        if (step != 0) { _cursor = 1 - _cursor; _view.MenuSound(FieldView.MenuChange); }
        if (pressed("ps_circle"))
        {
            if (_cursor == 0 && _view.SavePoint == 0) { _view.MenuSound(FieldView.MenuInvalid); return; }
            _view.MenuSound(FieldView.MenuSelect);
            if (_cursor == 0) _cards.OpenSave(_view.SavePoint, access: true);
            else { _dataLoading = true; _cards.OpenLoad(fadeOut: true); }
        }
        else if (pressed("ps_triangle") || pressed("ps_cross")) { _page = -1; _view.MenuSound(FieldView.MenuLeave); }
    }

    /// <summary>
    /// Uses a consumable on Ashley: its action (MAINMENU.PRG's map) resolved and applied by the battle
    /// (<see cref="FieldBattle.UseAction"/>): hit and magic points restored, RISK lowered, statuses cured or set, stats
    /// raised or lowered for good. A grimoire teaches its spell instead.
    /// </summary>
    private string Use(int id)
    {
        if (_view.Battle?.Ashley is not { } ashley) return "";
        var definition = _view.Content.Data("consumables").GetValueOrDefault(id.ToString(), new GDictionary()).AsGodotDictionary();
        if (definition.Count == 0) return $"The {_view.ItemName(id)} cannot be used here.";
        if (definition.GetValueOrDefault("grimoire", false).AsBool()) return Read(id, definition["action"].AsInt32());
        _view.Acted(definition.GetValueOrDefault("action", 0).AsInt32());
        // Effect 33 (the Eye of Argon) plays its effect at each of the room's traps, which reveals them.
        bool seeing = definition["effects"].AsGodotArray().Count > 0 && definition["effects"].AsGodotArray()[0].AsGodotDictionary()["effect"].AsInt32() == 33;
        if (seeing) foreach (var effect in _view.SeeTraps(definition.GetValueOrDefault("visual", 0).AsInt32(), ashley.Actor)) _view.Battle?.HideHudFor(effect);
        else _view.Battle?.HideHudFor(_view.PlayEffect(definition.GetValueOrDefault("visual", 0).AsInt32(), ashley.Actor, ashley.Actor));
        // The item's action is resolved and applied on her (BATTLE.PRG 0x80085b10), and what changed is told.
        var parts = new List<string>();
        int hp = ashley.Hp, mp = ashley.Mp, risk = ashley.Risk;
        uint status = ashley.Status;
        var outcome = _view.Battle?.UseAction(definition.GetValueOrDefault("action", 0).AsInt32());
        if (outcome != null && outcome.Kind != 4)
        {
            if (outcome.HpMode == 2) parts.Add($"HP +{ashley.Hp - hp}");
            if (outcome.MpMode == 2) parts.Add($"MP +{ashley.Mp - mp}");
            if (outcome.RiskMode == 2) parts.Add($"RISK -{risk - ashley.Risk}");
            if ((status & ~ashley.Status) != 0) parts.Add("cured");
            if ((ashley.Status & ~status) != 0) parts.Add("strengthened");
            string[] stats = { "STR", "INT", "AGL", "Max HP", "Max MP" };
            foreach (var (stat, amount, mode) in new[] { (0, outcome.Strength, outcome.StrengthMode), (1, outcome.Intelligence, outcome.IntelligenceMode), (2, outcome.Agility, outcome.AgilityMode), (3, outcome.MaxHp, outcome.MaxHpMode), (4, outcome.MaxMp, outcome.MaxMpMode) })
                if (mode != 0) parts.Add($"{stats[stat]} {(mode == 2 ? "+" : "-")}{amount}");
        }
        if (--_view.Items[id] <= 0) _view.Items.Remove(id);
        return $"{_view.ItemName(id)}: " + (parts.Count > 0 ? string.Join(", ", parts) : "no effect.");
    }

    /// <summary>
    /// Reads a grimoire: it is used up and teaches its spell. A levelled spell (actions 106 to 133) read again
    /// teaches its next level, up to the fourth.
    /// </summary>
    /// <summary>The spell a grimoire teaches: its action, or the next level of it already known.</summary>
    private int GrimoireSpell(int action)
    {
        int level = 0;
        while (action is >= 106 and <= 133 && level < 3 && _view.Spells.Contains(action)) { action++; level++; }
        return action;
    }

    private string Read(int id, int action)
    {
        action = GrimoireSpell(action);
        var spells = _view.Content.Data("spells")["actions"].AsGodotDictionary();
        string name = spells.GetValueOrDefault(action.ToString(), new GDictionary()).AsGodotDictionary().GetValueOrDefault("name", $"spell {action}").AsString();
        if (_view.Spells.Contains(action)) return $"{name} is already known.";
        _view.Spells.Add(action);
        if (--_view.Items[id] <= 0) _view.Items.Remove(id);
        _learned = name;
        return "";
    }

    private bool _bagDimmed;

    // On the Battle Abilities page, the kind opened (0 chain, 1 defense), or -1 for the page's list.
    private int _abilityKind = -1;

    // The spell a grimoire just taught, which the system box names as the menu closes.
    private string _learned = "";

    /// <summary>
    /// The STATUS screen's chosen element (MENU4.PRG): 0 to 5 a limb, 6 to 8 STRENGTH, INTELLIGENCE and AGILITY, 9 HP,
    /// 10 MP, 11 RISK, 13 the status effects' icons (the one chosen in <see cref="_statusIcon"/>).
    /// </summary>
    private int _statusChosen, _statusIcon;

    /// <summary>A limb's defences on the STATUS screen (Square): -1 when not up, else the page (1 AFFINITY, 2 TYPE); and the chosen row.</summary>
    private int _defencePage = -1, _defenceRow;

    /// <summary>
    /// STATUS's equipment (Circle on a limb; MENU4 0x80107E80): 0 not up, 1 the list (EQUIP's slots: weapon, shield,
    /// the limbs' armour, the accessory) on the limb's own, 2 the chosen thing's panels (Square turns their page).
    /// </summary>
    private int _statusEquip;

    /// <summary>Whose STATUS is shown: Ashley, or (L1 and R1) one of the room's fighters (MENU4 0x80103160).</summary>
    private FieldBattle.Combatant? _statusWho;

    /// <summary>The fighters STATUS goes round: Ashley, then the room's living enemies in sight.</summary>
    private List<FieldBattle.Combatant> StatusFighters()
    {
        var fighters = new List<FieldBattle.Combatant>();
        if (_view.Battle?.Ashley is { } ashley) fighters.Add(ashley);
        if (_view.Battle != null)
            foreach (var c in _view.Battle.Combatants.Values)
                if (c != _view.Battle.Ashley && c.Hostile && !c.Dead && c.Actor.Visible) fighters.Add(c);
        return fighters;
    }

    /// <summary>The fighter STATUS shows, Ashley unless another is chosen and still there.</summary>
    private FieldBattle.Combatant? StatusWho => _statusWho != null && StatusFighters().Contains(_statusWho) ? _statusWho : _view.Battle?.Ashley;

    /// <summary>
    /// Where the pad leads from each element (MENU4's table at 0x801080FC: up, down, left, right). Below the last limb
    /// is STRENGTH; left of a stat, or below RISK, are the icons.
    /// </summary>
    private static readonly int[][] StatusMoves =
    {
        new[] { 10, 1, 10, 0 }, new[] { 0, 2, 10, 1 }, new[] { 1, 3, 10, 2 }, new[] { 2, 4, 10, 3 }, new[] { 3, 5, 10, 4 }, new[] { 4, 6, 10, 5 },
        new[] { 5, 7, 13, 6 }, new[] { 6, 8, 13, 7 }, new[] { 7, 8, 13, 8 }, new[] { 9, 11, 9, 10 }, new[] { 10, 0, 9, 0 }, new[] { 9, 13, 11, 0 },
    };

    /// <summary>The status effects shown as icons, by retail bit (0x800C9FF4: strength down and up, intelligence, agility, quicken, silence, paralysis, poison, numbness, curse, regeneration).</summary>
    private List<int> StatusIcons(FieldBattle.Combatant who)
    {
        var bits = new SortedSet<int>();
        foreach (var (status, icon) in new[]
        {
            (FieldBattle.Degenerate, 0), (FieldBattle.Herakles, 1), (FieldBattle.Psychodrain, 2), (FieldBattle.Enlighten, 3), (FieldBattle.Leadbones, 4), (FieldBattle.Invigorate, 5),
            (FieldBattle.Quicken, 6), (FieldBattle.Silence, 7), (FieldBattle.Paralysis, 8), (FieldBattle.Poison, 9), (FieldBattle.Numbness, 10), (FieldBattle.Curse, 11), (FieldBattle.Regen, 12),
        })
            if (who.Has(status)) bits.Add(icon);
        return new List<int>(bits);
    }

    /// <summary>
    /// A frame of the STATUS screen: the pad moves through the limbs, stats, blocks and icons by MENU4's table; L2
    /// with the pad turns the model; Cross goes back to the command list and Triangle leaves the menu.
    /// </summary>
    private bool StepStatus(Func<string, bool> pressed)
    {
        if (pressed("ps_triangle")) { _view.MenuSound(FieldView.MenuLeave); Show(false); return false; }
        if (_statusEquip > 0)
        {
            // The equipment list: up and down choose, Circle shows what is there (nothing refuses), Cross goes back,
            // to the limb the list stands on.
            if (_statusEquip == 2)
            {
                if (pressed("ps_square")) { _statPage = (_statPage + 1) % 3; _view.MenuSound(FieldView.MenuChange); }
                if (pressed("ps_cross")) { _statusEquip = 1; _view.MenuSound(FieldView.MenuLeave); }
                Draw();
                return true;
            }
            int moved = (pressed("ps_down") ? 1 : 0) - (pressed("ps_up") ? 1 : 0);
            if (moved != 0) { _slot = (_slot + moved + 8) % 8; _view.MenuSound(FieldView.MenuChange); }
            if (pressed("ps_circle"))
            {
                if (Worn(_slot) != null) { _statusEquip = 2; _statPage = 0; _view.MenuSound(FieldView.MenuSelect); }
                else _view.MenuSound(FieldView.MenuInvalid);
            }
            else if (pressed("ps_cross"))
            {
                _view.MenuSound(FieldView.MenuLeave);
                _statusEquip = 0;
                // Back on the limb whose armour row was chosen (MENU4: the row less two), within the limbs.
                if (_view.Battle?.Ashley is { } owner) _statusChosen = Math.Clamp(ShownLimb(owner, SlotLimbs[_slot]), 0, Math.Max(0, StatusLimbs(owner).Count - 1));
            }
            Draw();
            return true;
        }
        if (pressed("ps_cross") && _defencePage < 0) { _view.MenuSound(FieldView.MenuLeave); _page = -1; _items.ShowAshley(null, ""); Draw(); return true; }
        if (Input.IsActionPressed("ps_l2"))
        {
            int yaw = (Input.IsActionPressed("ps_right") ? 64 : 0) - (Input.IsActionPressed("ps_left") ? 64 : 0);
            int pitch = (Input.IsActionPressed("ps_down") ? 64 : 0) - (Input.IsActionPressed("ps_up") ? 64 : 0);
            if (yaw != 0 || pitch != 0) _items.TurnModel(yaw, pitch);
            Draw();
            return true;
        }
        if (StatusWho is not { } ashley) { Draw(); return true; }
        bool own = ashley == _view.Battle?.Ashley;
        int limbs = StatusLimbs(ashley).Count;
        // L1 and R1 go round the fighters, the model sliding to the next (MENU4: Ashley's values known, an enemy's
        // only once analysed).
        if (_defencePage < 0 && (pressed("ps_l1") || pressed("ps_r1")))
        {
            var fighters = StatusFighters();
            if (fighters.Count > 1)
            {
                int at = Math.Max(0, fighters.IndexOf(ashley));
                var next = fighters[(at + (pressed("ps_r1") ? 1 : fighters.Count - 1)) % fighters.Count];
                _statusWho = next;
                _view.MenuSound(FieldView.MenuChange);
                _items.ShowAshley(next.Actor, next == _view.Battle?.Ashley ? $"{next.BattleSet}_1" : $"{next.Set}_{FieldPlayer.Idle}");
                _statusChosen = Math.Min(_statusChosen, Math.Max(0, StatusLimbs(next).Count - 1));
            }
            Draw();
            return true;
        }
        if (_defencePage > 0)
        {
            // MENU4's state 9: L1 and R1 go round the limbs, up and down the rows (seven on AFFINITY, three on TYPE),
            // Square turns the page, Cross goes back and Triangle leaves.
            if (pressed("ps_cross")) { _view.MenuSound(FieldView.MenuLeave); _defencePage = -1; Draw(); return true; }
            int was = _statusChosen, wasRow = _defenceRow;
            if (pressed("ps_l1")) _statusChosen = (_statusChosen + limbs - 1) % Math.Max(1, limbs);
            if (pressed("ps_r1")) _statusChosen = (_statusChosen + 1) % Math.Max(1, limbs);
            int rows = _defencePage == 1 ? 7 : 3;
            _defenceRow = Math.Min(_defenceRow, rows - 1);
            if (pressed("ps_up")) _defenceRow = (_defenceRow + rows - 1) % rows;
            if (pressed("ps_down")) _defenceRow = (_defenceRow + 1) % rows;
            if (pressed("ps_square")) { _defencePage = 3 - _defencePage; _defenceRow = Math.Min(_defenceRow, (_defencePage == 1 ? 7 : 3) - 1); _view.MenuSound(FieldView.MenuChange); }
            if (_statusChosen != was || _defenceRow != wasRow) _view.MenuSound(FieldView.MenuChange);
            Draw();
            return true;
        }
        var icons = StatusIcons(ashley);
        int direction = pressed("ps_up") ? 0 : pressed("ps_down") ? 1 : pressed("ps_left") ? 2 : pressed("ps_right") ? 3 : -1;
        if (direction >= 0)
        {
            int was = _statusChosen, wasIcon = _statusIcon;
            if (_statusChosen == 13)
            {
                // Among the icons left and right step through them; up, or right past the last, goes back.
                if (direction == 2 && _statusIcon > 0) _statusIcon--;
                else if (direction == 3 && _statusIcon + 1 < icons.Count) _statusIcon++;
                else if (direction is 0 or 3) _statusChosen = 6;
            }
            else
            {
                int next = StatusMoves[_statusChosen][direction];
                // Past the limbs there is only STRENGTH below, else the pointer keeps to the last limb.
                if (next < 6 && next >= limbs) next = direction == 1 ? 6 : limbs - 1;
                if (next == 13 && icons.Count == 0) next = _statusChosen;
                if (next == 13) _statusIcon = 0;
                _statusChosen = next;
            }
            if (_statusChosen != was || _statusIcon != wasIcon) _view.MenuSound(FieldView.MenuChange);
        }
        // Square on a limb shows its defences, from AFFINITY (MENU4: the class page is not offered); elsewhere it is
        // refused.
        if (pressed("ps_square"))
        {
            if (_statusChosen < limbs && own) { _view.MenuSound(FieldView.MenuSelect); _defencePage = 1; _defenceRow = 0; }
            else _view.MenuSound(FieldView.MenuInvalid);
        }
        // Circle on a limb opens the equipment, on that limb's armour.
        if (pressed("ps_circle"))
        {
            if (_statusChosen < limbs && own) { _view.MenuSound(FieldView.MenuSelect); _statusEquip = 1; _slot = 2 + IndexOfLimb(ashley, _statusChosen); }
            else _view.MenuSound(FieldView.MenuInvalid);
        }
        Draw();
        return true;
    }

    /// <summary>A fighter's limbs as STATUS shows them: BATTLE.PRG's names for a humanoid's limbs in record order, each condition and bone.</summary>
    private List<ItemMenu.StatusLimb> StatusLimbs(FieldBattle.Combatant who)
    {
        var names = _view.Content.Data("status_menu")["limb_names"].AsGodotArray();
        var limbs = new List<ItemMenu.StatusLimb>();
        for (int i = 0; i < who.Limbs.Count; i++)
        {
            var limb = who.Limbs[i];
            if (limb.MaxHp == 0) continue;
            // BATTLE.PRG's limb names (1 HEAD, 2 R.ARM, 3 L.ARM, 4 LEGS, 6 BODY) are the text table's parts less one.
            int name = FieldBattle.PartNameIndex(who, i) + 1;
            // 0x800C9E5C: DYING under 2, BAD under a quarter, AVERAGE under three quarters, GOOD short of whole, else EXCELLENT.
            int condition = limb.Hp < 2 ? 0 : limb.Hp * 4 < limb.MaxHp ? 1 : limb.Hp * 4 < limb.MaxHp * 3 ? 2 : limb.Hp < limb.MaxHp ? 3 : 4;
            limbs.Add(new ItemMenu.StatusLimb(name > 0 && name < names.Count ? names[name].AsString() : "", condition, limb.Bone));
        }
        return limbs;
    }

    /// <summary>The STATUS screen as it stands for Ashley.</summary>
    private ItemMenu.Status StatusScreen()
    {
        var menu = _view.Content.Data("status_menu");
        var strings = menu["strings"].AsGodotArray();
        var ashley = StatusWho!;
        // An enemy's hit points, magic points and RISK show only once Analyze has been cast on it (MENU4 0x80103c68,
        // 0x80103de8: its data's +0x954 bit 17); else their placeholder (0x8010399c).
        bool known = ashley == _view.Battle?.Ashley || ashley.Analyzed;
        var limbs = StatusLimbs(ashley);
        var icons = StatusIcons(ashley);
        var conditions = menu["conditions"].AsStringArray();
        var colours = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(menu["condition_colours"].AsGodotArray(), v => { var c = v.AsGodotArray(); return Color.Color8((byte)c[0].AsInt32(), (byte)c[1].AsInt32(), (byte)c[2].AsInt32()); }));
        var cells = menu["icons"].AsInt32Array();
        var labels = _view.Content.Has("equip_labels") ? _view.Content.Data("equip_labels") : new GDictionary();
        var words = labels.GetValueOrDefault("stats", new Godot.Collections.Array()).AsGodotArray();
        string Word(int i) => i < words.Count ? words[i].AsString() : "";
        // ORG/EQP: her own strength, intelligence and agility, then with what she has on.
        var stats = new[]
        {
            (Word(0), ashley.Strength, ashley.EffectiveStrength), (Word(1), ashley.Intelligence, ashley.EffectiveIntelligence), (Word(2), ashley.Agility, ashley.EffectiveAgility),
        };
        if (_statusChosen < 6 && _statusChosen >= limbs.Count) _statusChosen = Math.Max(0, limbs.Count - 1);
        if (_statusChosen == 13 && icons.Count == 0) _statusChosen = 6;
        _statusIcon = Math.Clamp(_statusIcon, 0, Math.Max(0, icons.Count - 1));
        // The help: a limb's "name / condition" (string 12 with the text table's part name and strings 13 to 17), a
        // stat's or block's (strings 0 to 5), or the chosen effect's (18 on).
        string help;
        if (_statusChosen < 6 && _statusChosen < limbs.Count)
        {
            var parts = _view.Content.Data("battle_text")["parts"].AsGodotArray();
            int part = FieldBattle.PartNameIndex(ashley, IndexOfLimb(ashley, _statusChosen));
            string partName = part >= 0 && part < parts.Count ? parts[part].AsGodotDictionary()["text"].AsString() : "";
            help = SystemBox.Fill(strings[12].AsGodotDictionary(), new[] { partName, SystemBox.Entry(strings, 13 + limbs[_statusChosen].Condition) });
        }
        else help = "";
        ItemMenu.Defences? defences = null;
        if (_defencePage > 0 && _statusChosen < limbs.Count)
        {
            // The limb's own values (MENU4 0x80105F44): its affinities, or its blunt, edged and piercing types; each
            // row's help is strings 50 to 56, or 57 to 59.
            var limb = ashley.Limbs[IndexOfLimb(ashley, _statusChosen)];
            var pages = labels.GetValueOrDefault("pages", new Godot.Collections.Array()).AsStringArray();
            var rowLabels = labels.GetValueOrDefault(_defencePage == 1 ? "affinities" : "types", new Godot.Collections.Array()).AsStringArray();
            int[] values = _defencePage == 1 ? limb.Affinities[..7] : limb.Types[1..4];
            defences = new ItemMenu.Defences(_defencePage, pages, new[] { false, true, true }, rowLabels, values, _defenceRow);
            help = SystemBox.Entry(strings, (_defencePage == 1 ? 50 : 57) + _defenceRow);
        }
        else if (_statusChosen is >= 6 and <= 11) help = SystemBox.Entry(strings, _statusChosen - 6);
        else if (_statusChosen == 13 && icons.Count > 0) help = SystemBox.Entry(strings, 18 + icons[_statusIcon]);
        return new ItemMenu.Status(ashley.Hp, ashley.MaxHp, ashley.Mp, ashley.MaxMp, ashley.Risk, limbs, conditions, colours, icons, cells,
            labels.GetValueOrDefault("org_eqp", "").AsString(), stats, _statusChosen, _statusIcon, help, defences, ashley.Name, known);
    }

    /// <summary>Where among the shown limbs a fighter's limb record stands, or -1.</summary>
    private static int ShownLimb(FieldBattle.Combatant who, int record)
    {
        for (int i = 0, n = 0; i < who.Limbs.Count; i++)
        {
            if (who.Limbs[i].MaxHp == 0) continue;
            if (i == record) return n;
            n++;
        }
        return -1;
    }

    /// <summary>The index in a fighter's limb records of its <paramref name="shown"/>th limb with hit points.</summary>
    private static int IndexOfLimb(FieldBattle.Combatant who, int shown)
    {
        for (int i = 0, n = 0; i < who.Limbs.Count; i++)
            if (who.Limbs[i].MaxHp > 0 && n++ == shown) return i;
        return -1;
    }

    private void Draw()
    {
        bool reclaimList = Visible && _page == Reclaim && _reclaimStage is 1 or 2;
        bool artsList = Visible && _page == Arts && _view.Content.Has("break_arts");
        bool magicList = Visible && _page == Magic && _view.Content.Has("magic_menu");
        artsList |= magicList;
        _list.Visible = Visible && (_page < 0 || _page == Abilities) || reclaimList || artsList;
        bool itemMenu = Visible && _page == Items;
        if (_items.Visible && !itemMenu && _page != Items && _page != Container && _page != Status) _items.ShowAshley(null, "");
        // The field darkens behind the gadget bag (retail footage: to about 45%).
        if (_page == Chest || _bagDimmed) { _bagDimmed = Visible && _page == Chest; _view.DimField(_bagDimmed ? 0.55f : 0); }
        bool asking = Visible && _page == Container && _boxStage == -1;
        bool statusPage = Visible && _page == Status && _view.Content.Has("status_menu");
        _items.Visible = itemMenu || statusPage || Visible && (_renaming != null || _page == Container && !asking || _page == Data || _cards.Open);
        if (asking)
        {
            // YES and NO from x 194 at y 148 and 164, as the container's rows are drawn, with no MENU title; the
            // question in the system box under CAUTION.
            _list.Visible = true;
            _list.Set(new[] { CheckString(1), CheckString(2) }, _cursor, prompt: (194, 148));
            if (_view.SystemBox.Text.Length == 0) { _view.SystemBox.Open(4, 1, centred: false); _view.SystemBox.Text = CheckString(0); }
        }
        if (statusPage && _view.Battle?.Ashley != null)
        {
            if (_statusEquip == 0) _items.SetStatus(StatusScreen());
            else
            {
                // EQUIP's slots and a thing's panels, as the item menu shows them.
                var strings = _view.Content.Has("item_menu") ? _view.Content.Data("item_menu")["strings"].AsGodotArray() : new Godot.Collections.Array();
                if (_statusEquip == 1)
                {
                    // Under the character's name (MENU4 keeps its header up), without the item menu's strip.
                    ShowSlots(strings, StatusWho?.Name ?? SystemBox.Entry(strings, 12 + _slot), strip: false);
                }
                else _items.SetList(SystemBox.Entry(strings, 12 + _slot), new List<ItemMenu.Row> { SlotRow(_slot) }, 0, SlotStats(_slot, Worn(_slot)));
            }
        }
        if (Visible && _page == Data && !_cards.Open && _view.Content.Has("memory_card"))
        {
            // DATA over the menu's backdrop: SAVE and LOAD (MCMAN's words 0 and 2) and the play time, hours, minutes,
            // seconds and hundredths (its sixtieths times five thirds), as MENU7.PRG prints them.
            var card = _view.Content.Data("memory_card");
            var words = card["strings"].AsGodotArray();
            var commands = _view.Content.Has("battle_text") ? _view.Content.Data("battle_text")["menu"].AsGodotArray() : new Godot.Collections.Array();
            long frames = _view.PlayFrames;
            long seconds = frames / 60;
            string time = $"{Math.Min(99, seconds / 3600):d2}:{seconds / 60 % 60:d2}:{seconds % 60:d2}:{frames % 60 * 5 / 3:d2}";
            _items.SetData(Data < commands.Count ? commands[Data].AsGodotDictionary()["text"].AsString() : "DATA",
                // Retail shows no help on this page (footage).
                new[] { new ItemMenu.Row(SystemBox.Entry(words, 0), -1, -1, ""), new ItemMenu.Row(SystemBox.Entry(words, 2), -1, -1, "") }, _cursor,
                card.GetValueOrDefault("play_time", "").AsString(), time);
        }
        if (Visible && _cards.Open)
        {
            // Over the container's header, or the menu's DATA.
            var commands = _view.Content.Has("battle_text") ? _view.Content.Data("battle_text")["menu"].AsGodotArray() : new Godot.Collections.Array();
            // The game over's LOAD has none.
            _items.SetHeader(_loadingGame ? "" : _page == Container ? BoxString(0) : Data < commands.Count ? commands[Data].AsGodotDictionary()["text"].AsString() : "DATA");
        }
        else if (Visible && _page == Container) DrawContainer();
        else if (_items.Visible && _page != Data && !statusPage && _renaming == null)
        {
            var strings = _view.Content.Has("item_menu") ? _view.Content.Data("item_menu")["strings"].AsGodotArray() : new Godot.Collections.Array();
            var commands = _view.Content.Has("battle_text") ? _view.Content.Data("battle_text")["menu"].AsGodotArray() : new Godot.Collections.Array();
            string header = Items < commands.Count ? commands[Items].AsGodotDictionary()["text"].AsString() : "ITEMS";
            int carried = _category < 7 ? _view.BagCounts()[_category] : 0;
            string count = _category < 7 ? $"{carried}/{GadgetBag.Limits[_category],3}" : "";
            if (_setup >= 0)
            {
                var (headers, setupRows, icons) = SetupPage();
                ItemMenu.Stats? stats = null;
                GDictionary? piece = _setup == 9 ? _combineResult : _setup is 7 or 8 && _setupCursor < CombineList().Count ? CombineList()[_setupCursor] : null;
                if (piece != null)
                {
                    var record = piece["record"].AsGodotDictionary();
                    stats = _setupKind == 0 ? SlotStats(0, new GDictionary { ["blade"] = record, ["grip"] = new GDictionary() })
                        : SlotStats(_setupKind == 1 ? 1 : record["category"].AsInt32() switch { 2 => 2, 3 => 4, 4 => 5, _ => 6 }, record);
                }
                if (_setup is >= 10 and <= 14)
                {
                    stats = SlotStats(0, new GDictionary { ["blade"] = _blade == null ? new GDictionary() : _blade["record"], ["grip"] = _grip == null ? new GDictionary() : _grip["record"] });
                }
                _items.SetSetup(headers, setupRows, _setupCursor, icons, _setupCommand == Repair && _setup == 4 ? 146 : -1, _setup == 5 ? SetupString(46) : "", stats, assembly: _setup == 10);
            }
            else if (_equip == 1)
            {
                ShowSlots(strings, SystemBox.Entry(strings, 12 + _slot), strip: true);
            }
            else if (_equip == 2)
            {
                var rows = new List<ItemMenu.Row> { SlotRow(_slot) };
                foreach (var candidate in SlotCandidates(_slot)) rows.Add(_slot == 0 ? WeaponRow(candidate) : ArmourRow(candidate));
                var candidates = SlotCandidates(_slot);
                GDictionary? chosen = _cursor > 0 && _cursor <= candidates.Count ? candidates[_cursor - 1] : Worn(_slot);
                _items.SetList(SystemBox.Entry(strings, 12 + _slot), rows, _cursor, SlotStats(_slot, chosen));
            }
            else _items.Set(header, _category, SystemBox.Entry(strings, _category), count, CategoryRows(), _cursor, true, _itemAction >= 0 ? ItemActions() : null, Math.Max(0, _itemAction));
        }
        if (_renaming != null)
            _items.SetNaming(new ItemMenu.Naming(new string(_nameLetters), _nameCaret, NameString(13).Split('\n'),
                new[] { NameString(9), NameString(10), NameString(11), NameString(12) }, _nameColumn, _nameRow, _nameCommand,
                NameString(5, _nameCandidate), new[] { NameString(6), NameString(7), NameString(8) }, _nameAnswer));
        _bag.Visible = Visible && _page == Chest && _chest != null;
        if (!Visible) return;
        if (_bag.Visible)
        {
            var entries = new List<GadgetBag.Entry>();
            foreach (var value in _view.ChestContents(_chest!)) entries.Add(_view.BagEntry(value.AsGodotDictionary()));
            _bag.Set(entries, _cursor, _view.BagCounts());
        }
        // The commands are retail's list; the pages are still drawn as text in a panel.
        bool abilityList = _page == Abilities;
        bool panel = _renaming == null && _page >= 0 && !statusPage && !(_page == Map && _view.Content.Has("map_menu")) && _page != Chest && _page != Container && _page != Data && _page != Reclaim && !artsList && !(_page == Items) && !abilityList;
        SelfModulate = panel ? Colors.White : Colors.Transparent;
        _text.Visible = panel;
        if (_page < 0)
        {
            var names = _view.Content.Has("battle_text") ? _view.Content.Data("battle_text")["menu"].AsGodotArray() : new Godot.Collections.Array();
            var shown = new List<string>();
            var greyed = new List<int>();
            int chosen = 0;
            for (int i = 0; i < Rows.Length; i++)
            {
                if (!Usable(i)) continue;
                if (i == _row) chosen = shown.Count;
                shown.Add(i < names.Count ? names[i].AsGodotDictionary()["text"].AsString() : Rows[i].ToUpperInvariant());
                // BREAK ARTS stands greyed out of battle mode, though it still opens (Ep. 3 6:57).
                greyed.Add(i == Arts && _view.Battle?.Drawn != true ? 1 : 0);
            }
            _list.Set(shown, chosen, kinds: greyed);
        }
        else if (magicList)
        {
            var page = _view.Content.Data("magic_menu");
            var strings = page["strings"].AsGodotArray();
            var menuNames = _view.Content.Has("battle_text") ? _view.Content.Data("battle_text")["menu"].AsGodotArray() : new Godot.Collections.Array();
            string header = menuNames.Count > Magic ? menuNames[Magic].AsGodotDictionary()["text"].AsString() : "MAGIC";
            if (_magicClass < 0)
            {
                var names = new List<string>();
                var kinds = new List<int>();
                for (int c = 0; c < 4; c++) { names.Add(SystemBox.Entry(strings, c * 3)); kinds.Add(ClassSpells(c).Count == 0 ? 1 : 0); }
                _list.Set(names, _cursor, header, SystemBox.Entry(strings, _cursor * 3 + (ClassSpells(_cursor).Count == 0 ? 2 : 1)), kinds: kinds, titled: !_shortcut);
            }
            else
            {
                var spells = ClassSpells(_magicClass);
                var all = page["classes"].AsGodotArray()[_magicClass].AsGodotArray();
                int first = new[] { 12, 30, 36, 54 }[_magicClass];
                var actions = _view.Content.Data("spells")["actions"].AsGodotDictionary();
                // Eight rows at most, the window following the cursor.
                int top = Math.Clamp(_cursor - 7, 0, Math.Max(0, spells.Count - 8));
                var shown = spells.GetRange(top, Math.Min(8, spells.Count - top));
                var names = shown.ConvertAll(id => actions.GetValueOrDefault(id.ToString(), new GDictionary()).AsGodotDictionary().GetValueOrDefault("name", "").AsString());
                var kinds = shown.ConvertAll(id => SpellCastable(id) ? 0 : 1);
                int chosen = spells.Count > 0 ? spells[_cursor] : 0, index = 0;
                for (int i = 0; i < all.Count; i++) if (all[i].AsInt32() == chosen) index = i;
                _list.Set(names, _cursor - top, header, spells.Count > 0 ? SystemBox.Entry(strings, first + index) : "", sub: (SystemBox.Entry(strings, _magicClass * 3), -1), kinds: kinds,
                    cost: spells.Count > 0 ? ("MP", SpellCost(chosen).ToString()) : null, titled: !_shortcut);
            }
        }
        else if (artsList)
        {
            var strings = _view.Content.Data("break_arts")["strings"].AsGodotArray();
            var menuNames = _view.Content.Has("battle_text") ? _view.Content.Data("battle_text")["menu"].AsGodotArray() : new Godot.Collections.Array();
            string header = menuNames.Count > Arts ? menuNames[Arts].AsGodotDictionary()["text"].AsString() : "BREAK ARTS";
            if (_artsCategories)
            {
                // Eight rows at most, the window following the cursor, so the list keeps clear of the help.
                var names = new List<string>();
                var kinds = new List<int>();
                int top = Math.Clamp(_cursor - 7, 0, 2);
                var icons = new List<int>();
                // Each category with its icon; greyed with no art back, yellow for the one in hand (Ep. 3 7:14: AXE & MACE).
                int held = _view.Battle?.Ashley?.Category ?? 0;
                for (int c = top + 1; c <= Math.Min(10, top + 8); c++) { names.Add(SystemBox.Entry(strings, (c - 1) * 3)); kinds.Add(CategoryArts(c).Count == 0 ? 1 : c == held ? 3 : 0); icons.Add(3 + c); }
                bool none = CategoryArts(_cursor + 1).Count == 0;
                _list.Set(names, _cursor - top, header, SystemBox.Entry(strings, _cursor * 3 + (none ? 2 : 1)), sub: (SystemBox.Entry(strings, 30), -1), kinds: kinds, icons: icons, headerDimmed: _view.Battle?.Drawn != true, titled: !_shortcut);
            }
            else
            {
                var arts = CategoryArts(_artsCategory);
                var actions = _view.Content.Data("spells")["actions"].AsGodotDictionary();
                var names = arts.ConvertAll(a => actions.GetValueOrDefault(a.ToString(), new GDictionary()).AsGodotDictionary().GetValueOrDefault("name", "").AsString());
                var kinds = arts.ConvertAll(a => ArtUsable(a) ? 0 : 1);
                names.Add(SystemBox.Entry(strings, 30));
                kinds.Add(2);
                bool onArt = _cursor < arts.Count;
                string help = onArt ? SystemBox.Entry(strings, 32 + arts[_cursor] - 184) : SystemBox.Entry(strings, 31);
                // NEXT: the kills still wanted for the category's next art (none once all four are back).
                (string, string)? next = null;
                int row = _artsCategory % 10, learned = arts.Count;
                if (learned < 4)
                {
                    var thresholds = _view.Content.Data("spells")["art_kills"].AsGodotArray()[row].AsGodotArray();
                    next = ("NEXT", $"{Math.Max(0, thresholds[learned].AsInt32() - _view.ArtKills[row])}PT");
                }
                (string, string)? cost = onArt ? ("HP", actions.GetValueOrDefault(arts[_cursor].ToString(), new GDictionary()).AsGodotDictionary().GetValueOrDefault("cost", 0).AsInt32().ToString()) : null;
                // BREAK ARTS is dimmed over the page out of battle mode only (Ep. 3 7:01 against 7:09).
                _list.Set(names, _cursor, header, help, next, sub: (SystemBox.Entry(strings, (_artsCategory - 1) * 3), 3 + _artsCategory), kinds: kinds, cost: cost, headerDimmed: _view.Battle?.Drawn != true, titled: !_shortcut);
            }
        }
        else if (reclaimList)
        {
            var page = _view.Content.Data("battle_abilities");
            var unlock = page["unlock"].AsGodotArray();
            string Line(int i) => i < unlock.Count ? unlock[i].AsGodotDictionary()["text"].AsString() : "";
            // The kinds stand where the commands do, untitled; the list under its kind's bar, also with no title word.
            if (_reclaimStage == 1) _list.Set(new[] { Line(14), Line(15) }, _cursor, null, Line(12), titled: false);
            else
            {
                var strings = page["strings"].AsGodotArray();
                var descriptions = page["descriptions"].AsGodotArray();
                // Four rows at most show (MENU2.PRG; the footage shows four of eleven), the window following the cursor.
                int first = Math.Clamp(_cursor - 3, 0, Math.Max(0, _reclaimable.Count - 4));
                var names = _reclaimable.GetRange(first, Math.Min(4, _reclaimable.Count - first)).ConvertAll(AbilityName);
                int id = _reclaimable.Count > 0 ? _reclaimable[_cursor] : 0, index = id < descriptions.Count ? descriptions[id].AsInt32() : 0;
                string help = index > 0 && index < strings.Count ? strings[index].AsGodotDictionary()["text"].AsString() : "";
                _list.Set(names, _cursor - first, Line(14 + _reclaimKind), help, titled: false);
            }
        }
        else if (abilityList && _abilityKind >= 0 && _view.Content.Has("battle_abilities"))
        {
            // A kind's abilities, as Episode 4 shows them at 10:04: under BATTLE ABILITIES and the kind's bar, each with
            // the face button it is on, the chosen one's description below and its RISK in the cost box.
            var page = _view.Content.Data("battle_abilities");
            var strings = page["strings"].AsGodotArray();
            var menuNames = _view.Content.Has("battle_text") ? _view.Content.Data("battle_text")["menu"].AsGodotArray() : new Godot.Collections.Array();
            var descriptions = page["descriptions"].AsGodotArray();
            var list = KindAbilities(_abilityKind);
            var slots = _abilityKind == 0 ? _view.ChainButtons : _view.DefenseButtons;
            // The slots are Circle, Triangle and Square; the label page's buttons run triangle, square, circle.
            int[] buttonOf = { 2, 0, 1 };
            var names = list.ConvertAll(AbilityName);
            var buttons = list.ConvertAll(id => Array.IndexOf(slots, id) is >= 0 and var slot ? buttonOf[slot] : -1);
            int chosen = list.Count > 0 ? list[Math.Min(_cursor, list.Count - 1)] : 0;
            int description = chosen < descriptions.Count ? descriptions[chosen].AsInt32() : 0;
            int cost = _view.Content.Data("abilities").GetValueOrDefault(chosen.ToString(), new GDictionary()).AsGodotDictionary().GetValueOrDefault("cost", 0).AsInt32();
            _list.Set(names, Math.Min(_cursor, Math.Max(0, list.Count - 1)), menuNames.Count > Abilities ? menuNames[Abilities].AsGodotDictionary()["text"].AsString() : "BATTLE ABILITIES",
                description > 0 ? SystemBox.Entry(strings, description) : "", sub: (SystemBox.Entry(strings, _abilityKind * 3), -1), cost: ("RISK", cost.ToString()), titled: !_shortcut, buttons: buttons, headerDimmed: false);
        }
        else if (abilityList && _view.Content.Has("battle_abilities"))
        {
            // As MENU2.PRG shows it: the command as the header, the two kinds, the kill points to the next ability, and
            // the chosen kind's help (or that Ashley has none of that kind yet), over the field with Ashley's figure
            // put away for the help.
            var page = _view.Content.Data("battle_abilities");
            var strings = page["strings"].AsGodotArray();
            var names = _view.Content.Has("battle_text") ? _view.Content.Data("battle_text")["menu"].AsGodotArray() : new Godot.Collections.Array();
            bool any = _view.Abilities.Exists(id => _cursor == 0 ? id < 40 : id >= 40);
            var needed = page["requirements"].AsGodotArray();
            // The points still wanted for the next reclaim (MENU2.PRG): its requirement less the points so far.
            int next = _view.AbilitiesReclaimed < needed.Count ? needed[_view.AbilitiesReclaimed].AsInt32() : 0;
            int points = Math.Max(0, next - _view.AbilityPoints);
            // From the shortcut menu the two kinds stand alone, under BATTLE MODE: no header, title or points (Episode 4 at 3:15).
            if (_shortcut) _list.Set(new[] { SystemBox.Entry(strings, 0), SystemBox.Entry(strings, 3) }, _cursor, null, SystemBox.Entry(strings, _cursor * 3 + (any ? 1 : 2)), titled: false);
            else _list.Set(new[] { SystemBox.Entry(strings, 0), SystemBox.Entry(strings, 3) }, _cursor, names.Count > Abilities ? names[Abilities].AsGodotDictionary()["text"].AsString() : "BATTLE ABILITIES",
                SystemBox.Entry(strings, _cursor * 3 + (any ? 1 : 2)), ("NEXT", $"{points}PT"));
        }
        _map.Visible = Visible && _page == Map;
        var lines = new List<string>();
        if (_page < 0)
            for (int i = 0; i < Rows.Length; i++) lines.Add((i == _row ? "> " : "  ") + Rows[i] + (Usable(i) ? "" : "  (not yet)"));
        else if (_page == Magic)
        {
            lines.Add("Magic");
            var spells = _view.Content.Data("spells")["actions"].AsGodotDictionary();
            for (int i = 0; i < _view.Spells.Count; i++)
            {
                var spell = spells.GetValueOrDefault(_view.Spells[i].ToString(), new GDictionary()).AsGodotDictionary();
                lines.Add((i == _cursor ? "> " : "  ") + $"{spell.GetValueOrDefault("name", "?")}  MP {spell.GetValueOrDefault("cost", 0)}");
            }
        }
        else if (_page == Arts)
        {
            lines.Add("Break Arts");
            var spells = _view.Content.Data("spells")["actions"].AsGodotDictionary();
            for (int i = 0; i < _view.Arts.Count; i++)
            {
                var art = spells.GetValueOrDefault(_view.Arts[i].ToString(), new GDictionary()).AsGodotDictionary();
                lines.Add((i == _cursor ? "> " : "  ") + $"{art.GetValueOrDefault("name", "?")}  HP {art.GetValueOrDefault("cost", 0)}");
            }
        }
        else if (_page == Abilities && _view.Battle is { } battle)
        {
            string Name(int id) => id == 0 ? "-" : _view.Content.Data("abilities").GetValueOrDefault(id.ToString(), new GDictionary()).AsGodotDictionary().GetValueOrDefault("name", $"action {id}").AsString();
            string[] buttons = { "Circle", "Triangle", "Square" };
            lines.Add("Chain Abilities");
            for (int i = 0; i < 3; i++) lines.Add($"  {buttons[i],-9} {Name(battle.ChainSlots[i])}");
            lines.Add("Defense Abilities");
            for (int i = 0; i < 3; i++) lines.Add($"  {buttons[i],-9} {Name(battle.DefenseSlots[i])}");
        }
        else if (_page == Map)
        {
            int scene = _map.Scene;
            var names = _view.Content.Data("area_names").GetValueOrDefault("list", new Godot.Collections.Array()).AsGodotArray();
            lines.Add((scene >= 0 && scene < names.Count ? names[scene].AsString() : "Map") + (MapCanvas.Areas(_view).Count > 1 ? "   (Square: next area)" : ""));
            lines.Add(_map.RoomName);
        }
        else if (_page == Score)
        {
            int chests = 0;
            for (int i = 0; i < 64; i++) if (_view.Flags[0x3C0 + i] != 0) chests++;
            lines.Add("Score");
            lines.Add($"  Treasure chests opened  {chests * 100 / 52}%");
            lines.Add($"  Map completion          {_view.Visited.Count * 100 / 361}%");
            lines.Add($"  Break Arts learned      {_view.Arts.Count}");
        }
        else if (_page == Data) lines.Add("Data");
        else if (_page == Manual)
        {
            var topics = _view.Content.Data("manual")["topics"].AsGodotArray();
            if (_topic < 0)
            {
                lines.Add("Quick Manual");
                for (int i = 0; i < topics.Count; i++) lines.Add((i == _cursor ? "> " : "  ") + $"{i + 1,2}. {TopicName(topics[i].AsGodotDictionary()["title"].AsString())}");
            }
            else
            {
                var text = topics[_topic].AsGodotDictionary()["lines"].AsGodotArray();
                for (int i = _scroll; i < text.Count && i < _scroll + ManualLines; i++) lines.Add(text[i].AsString());
                if (text.Count > ManualLines) lines.Add($"   ({_scroll + 1}-{Math.Min(text.Count, _scroll + ManualLines)} of {text.Count})");
            }
        }
        if (_note.Length > 0) { lines.Add(""); lines.Add(_note); }
        _text.Text = string.Join("\n", lines);
        ShowPictures();
    }

    private readonly List<CanvasItem> _pictures = new();
    /// <summary>The menu's font with its spaces six retail pixels wide, for Quick Manual pages.</summary>
    private FontVariation? _manualFont;
    private readonly Dictionary<string, Texture2D> _pictureTextures = new();

    /// <summary>
    /// The open Quick Manual topic's pictures and rules over its text, where MENU/MENUE.PRG draws them on its page
    /// (0x80103e6c and 0x80103d30), scaled from retail's pixels to the menu's lines and spaces.
    /// </summary>
    private void ShowPictures()
    {
        foreach (var picture in _pictures) picture.QueueFree();
        _pictures.Clear();
        bool page = _page == Manual && _topic >= 0 && Visible;
        if (page && _manualFont == null)
        {
            var font = _text.GetThemeFont("font");
            float space = font.GetStringSize(" ", HorizontalAlignment.Left, -1, _text.GetThemeFontSize("font_size")).X;
            _manualFont = new FontVariation { BaseFont = font };
            _manualFont.SetSpacing(TextServer.SpacingType.Space, (int)Math.Round(6 * _text.GetLineHeight() / 13f - space));
        }
        if (page) _text.AddThemeFontOverride("font", _manualFont);
        else if (_manualFont != null) _text.RemoveThemeFontOverride("font");
        if (!page) return;
        var topic = _view.Content.Data("manual")["topics"].AsGodotArray()[_topic].AsGodotDictionary();
        // Retail lays the page out in pixels, 13 a line, and the text's offsets became spaces of the font's six-pixel
        // advance; the page's spaces are widened to six of its pixels, so the pictures keep their shape and the text
        // beside them clears them.
        float lineHeight = _text.GetLineHeight(), down = lineHeight / 13f, across = down;
        float top = _scroll * 13, bottom = (_scroll + ManualLines) * 13;
        Vector2 Place(int x, int y) => new(x * across, (y - top) * down);
        foreach (var value in topic.GetValueOrDefault("pictures", new Godot.Collections.Array()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            int y = record["y"].AsInt32(), height = record["height"].AsInt32();
            if (y + height <= top || y >= bottom) continue;
            string name = record["image"].AsString();
            if (!_pictureTextures.TryGetValue(name, out var texture)) _pictureTextures[name] = texture = _view.Content.ScreenTexture(name);
            var picture = new TextureRect
            {
                Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.Nearest,
                Position = Place(record["x"].AsInt32(), y), Size = new Vector2(record["width"].AsInt32() * across, height * down),
            };
            _text.AddChild(picture);
            _pictures.Add(picture);
        }
        // The page's rules: coloured lines between two points.
        foreach (var value in topic.GetValueOrDefault("rules", new Godot.Collections.Array()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            var from = record["from"].AsGodotArray();
            var to = record["to"].AsGodotArray();
            if (Math.Max(from[1].AsInt32(), to[1].AsInt32()) < top || Math.Min(from[1].AsInt32(), to[1].AsInt32()) >= bottom) continue;
            var colour = record["colour"].AsGodotArray();
            var rule = new Line2D { Width = Math.Max(1, down), DefaultColor = Color.Color8((byte)colour[0].AsInt32(), (byte)colour[1].AsInt32(), (byte)colour[2].AsInt32()) };
            rule.AddPoint(Place(from[0].AsInt32(), from[1].AsInt32()));
            rule.AddPoint(Place(to[0].AsInt32(), to[1].AsInt32()));
            _text.AddChild(rule);
            _pictures.Add(rule);
        }
    }

    /// <summary>A topic's name in the list: its opening sentence without "This section explains" or "reviews", capitalised.</summary>
    private static string TopicName(string sentence)
    {
        foreach (string opening in new[] { "This section explains ", "This section reviews " })
            if (sentence.StartsWith(opening, StringComparison.Ordinal)) sentence = sentence[opening.Length..];
        sentence = sentence.TrimEnd('.');
        return sentence.Length == 0 ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }

}
