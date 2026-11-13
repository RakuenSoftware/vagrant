// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The item menu the command list's ITEMS opens (MENU3.PRG), as retail draws it on the 320 by 240 screen, scaled to the
/// window, over black:
/// <list type="bullet">
/// <item>The crystal backdrop (MENU/MENUBG.BIN, 192 by 240) from x 64, fading in over eight frames, added at an
/// eighth more brightness each frame.</item>
/// <item>MENU (the HUD page through the palette at (848, 223), 36 by 10 at (64, 64)) at x 276, sliding down to y 6.</item>
/// <item>The category strip: the HUD page's icons (16 by 16 from (120 + 16 i, 128)) for WEAPONS to MISC at x 48 to
/// 144 and OPTIONS ("!") at x 32, the chosen one through the palette at (976, 223) and the rest through (992, 223),
/// sliding down to y 14 over four frames; the arrows either side, at (16, 16) and (162, 17), wobbling outward on a
/// 12-frame cycle.</item>
/// <item>STATUS: the Square button (16 by 16 at (16, 128), palette (944, 223)) at (8, 34) and the word in the HUD
/// capitals at (28, 36), dim unless a thing is chosen, over ten olive lines, sliding in from the left over ten frames.</item>
/// <item>On the right, the ITEMS header and the category's title as teal rows from x 180 (the title with what the bag
/// carries over its limit), then the category's rows: OPTIONS' EQUIP and SETUP from x 194, the others' things from
/// x 169 with their icons, eight at a time. A row's bar stands 3 above its text, 12 high.</item>
/// <item>INFORMATION and the chosen row's help in the system font.</item>
/// <item>Ashley, a copy of her field model with her weapon drawn, in her battle stance, standing at the origin of a
/// world of her own (MAINMENU.PRG): the camera is projected at H 512 about (160, 112) with the view's x scaled by
/// 9/8, and looks from 0xB00 round, four times the model's camera distance D (204 for Ashley) away, at a point
/// half her camera height (217) up and k/256 D aside, k easing from 256 to 128 over ten frames as the menu opens,
/// which stands her left of the middle.</item>
/// </list>
/// Measured against PSX longplay footage; the positions are MENU3.PRG's.
/// </summary>
public sealed partial class ItemMenu : RetailScreen
{
    /// <summary>A row: its name, its count (-1 for none), its icon cell (-1 for none) and its help.</summary>
    /// <summary>A row: its name, count (-1 none), icon (-1 none), help and material letter; a greyed one cannot be chosen now (its name in MENU1's grey-blue), and a cautionary help shows under CAUTION.</summary>
    public readonly record struct Row(string Name, int Count, int Icon, string Help, int Material = 0, bool Greyed = false, bool Caution = false, int Badge = 0);

    /// <summary>
    /// The materials' letters on the label page (Wood, Leather, Bronze, Iron, Hagane, Silver, Damascus), which retail
    /// sets at an equipment icon's top right, in orange (measured from footage: the cell 13 right of the icon's row
    /// start and 3 above its row).
    /// </summary>
    public static readonly Rect2[] MaterialLetters =
    {
        new(216, 200, 7, 7), new(216, 208, 6, 7), new(216, 216, 6, 7), new(224, 200, 5, 7), new(224, 208, 6, 7), new(224, 216, 6, 7), new(232, 200, 6, 7),
    };
    public static readonly Color MaterialTint = new(1.9f, 1.1f, 0.35f);

    public const int Options = 7;

    /// <summary>
    /// The chosen thing's panels in a slot's list: its DP and PP (shown in hundredths' ceiling, as retail shows DP),
    /// the page of its CLASS, AFFINITY or TYPE values (<paramref name="Page"/> of <paramref name="Pages"/>) by
    /// <paramref name="Labels"/>, and its ORG/EQP rows: each a label, the thing's own value, Ashley's with it on and with
    /// what she has on now. <paramref name="Ranged"/> when the rows start with RANGE and RISK.
    /// </summary>
    public sealed record Stats(int Dp, int MaxDp, int Pp, int MaxPp, string DpWord, string PpWord, int Page, string[] Pages, string[] Labels, int[] Values,
        string OrgEqp, (string Label, int Org, int Eqp, int Now)[] Rows, bool Ranged);
    private static readonly int[] Ease = { 0, 1, 2, 4, 8, 16, 32, 56, 80, 104, 128, 152, 176, 200, 224, 248 };
    private static readonly Rect2 MenuWord = new(64, 64, 36, 10);

    private Texture2D? _backdrop, _menuPage, _chosenIcons, _icons, _arrowsLive, _arrowsIdle, _button, _labels, _cursor, _rowIcons;
    private Font? _system;
    private Runtime.HudLettering? _lettering;

    private string _header = "", _title = "", _count = "";
    private IReadOnlyList<Row> _rows = Array.Empty<Row>();

    /// <summary>The list's rows as shown, for the checks.</summary>
    public IReadOnlyList<Row> Rows => _rows;
    private int _category = Options, _chosen, _first, _frames, _ticks;
    private bool _browsing = true;

    private enum Mode { Items, Slots, List, ContainerTop, ContainerList, Header, Data, Setup, Status }

    /// <summary>
    /// The container's screens (MENU/MENUD.PRG): its words and both stores' counts by category against their limits,
    /// the three buttons of a transfer list (STATUS, CANCEL, END), a confirmation (question, YES, NO, the chosen one)
    /// and the help shown when the list is empty.
    /// </summary>
    public sealed record Container(string[] Categories, int[] BagCounts, int[] BagLimits, int[] BoxCounts, int[] BoxLimits, string BagWord, string BoxWord,
        string Slash, string Ok, string[] Buttons, (string Question, string Yes, string No, int Chosen)? Confirm, string Empty);
    private Container? _container;
    private static readonly Rect2 CautionWord = new(64, 145, 33, 7);
    private Mode _mode, _previous;
    private int _modeFrames;

    private void Enter(Mode mode)
    {
        if (mode == _mode) return;
        (_previous, _mode, _modeFrames) = (_mode, mode, 0);
    }
    private string[] _slotLabels = Array.Empty<string>();
    private Stats? _stats;
    /// <summary>A chosen thing's name while its actions are up (retail footage).</summary>
    public static readonly Color ChosenName = new(0.84f, 0.86f, 0.45f);
    /// <summary>What can be done with the chosen thing (MISC: USE, DISCARD, DISCARD ALL, SORT), shown at the left, or null.</summary>
    private IReadOnlyList<Row>? _actions;

    /// <summary>The row whose help shows: the chosen action's while a thing's actions are up, else the chosen row's.</summary>
    private Row? Helped => _mode is Mode.Items or Mode.ContainerList && _actions is { } shown && _action < shown.Count ? shown[_action] : _chosen >= 0 && _chosen < _rows.Count ? _rows[_chosen] : null;

    /// <summary>The help shown, and whether it stands under CAUTION.</summary>
    public (string Text, bool Caution) Help => _mode == Mode.Status ? (_status?.Help ?? "", false) : _mode == Mode.Setup && _setupCaution.Length > 0 ? (_setupCaution, true) : (Helped?.Help ?? "", Helped is { Caution: true });
    private int _action;
    private Texture2D? _digits;

    private SubViewport? _stage;
    private Camera3D? _eye;
    private bool _shown;
    private AnimationPlayer? _pose;
    private string _clip = "";

    public void Configure(Wol.Games.Vagrant.Runtime.RetailContent content)
    {
        Texture2D? Page(string name) => content.ScreenTextureIfAny(name);
        _backdrop = Page("item_menu_backdrop");
        _menuPage = Page("hud_page_848");
        _chosenIcons = Page("hud_page_976");
        _icons = Page("hud_page_992");
        _arrowsLive = Page("hud_page_896");
        _arrowsIdle = Page("hud_page_912");
        _button = Page("hud_page_944");
        _elements = Page("hud_page_1008");
        _greyCursor = Page("hud_page_928");
        _labels = Page("hud_labels");
        _digits = Page("hud_digits");
        _cursor = Page("hud_cursor");
        _system = content.Has("system_font") ? content.BitmapFont("system_font") : null;
        _lettering = content.HudLettering;
    }

    /// <summary>Starts the menu's entrance: the backdrop fades in and its parts slide into place.</summary>
    /// <summary>Whether the MENU word shows at the top right: not over the game over's LOAD screens, as retail's has none.</summary>
    public bool MenuWordShown { get; set; } = true;

    public void Enter()
    {
        _frames = 0;
        MenuWordShown = true;
        (_mode, _previous, _modeFrames) = (Mode.Items, Mode.Items, 0);
        QueueRedraw();
    }

    /// <summary>
    /// Stands a copy of <paramref name="ashley"/>'s model on the menu, its held weapon and shield shown, playing
    /// <paramref name="clip"/>; null takes her away.
    /// </summary>
    public void ShowAshley(EventActor? ashley, string clip)
    {
        if (_stage == null)
        {
            _stage = new SubViewport { Name = "Stage", OwnWorld3D = true, TransparentBg = true, Size = new Vector2I(640, 576), RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            _eye = new Camera3D { KeepAspect = Camera3D.KeepAspectEnum.Height, Fov = Mathf.RadToDeg(2 * Mathf.Atan(128f / 512)), Near = 0.05f, Far = 4096 / Installation.MapConversion.UnitsPerMetre, Current = true };
            _stage.AddChild(_eye);
            AddChild(_stage);
        }
        foreach (var child in _stage.GetChildren()) if (child != _eye) { _stage.RemoveChild(child); child.QueueFree(); }
        _pose = null;
        _clip = clip;
        _shown = ashley != null;
        if (ashley == null) return;
        var copy = (Node3D)ashley.Node.Duplicate();
        copy.Transform = Transform3D.Identity;
        copy.Visible = true;
        _stage.AddChild(copy);
        void Reveal(Node node, bool held)
        {
            held |= node.Name == "Weapon" || node.Name == "Shield";
            if (held && node is Node3D shown) shown.Visible = true;
            foreach (var child in node.GetChildren()) Reveal(child, held);
        }
        Reveal(copy, false);
        // The field lights her by the room's light map; on the menu she stands in plain light (0x80, which leaves a
        // texel as it is), on her own copies of the materials.
        foreach (var node in copy.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mesh = (MeshInstance3D)node;
            for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
                if (mesh.Mesh.SurfaceGetMaterial(surface) is StandardMaterial3D material && material.Duplicate() is StandardMaterial3D plain)
                {
                    plain.AlbedoColor = plain.VertexColorUseAsAlbedo ? new Color(2, 2, 2) : Colors.White;
                    plain.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                    mesh.SetSurfaceOverrideMaterial(surface, plain);
                }
        }
        if (ashley.Player != null && ashley.Node.GetPathTo(ashley.Player) is var path && copy.GetNodeOrNull<AnimationPlayer>(path) is { } player)
        {
            // Nothing steps the copy by hand: it plays on its own, and starts its stance again when it ends.
            player.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Idle;
            player.Active = true;
            if (player.HasAnimation(clip)) player.Play(clip);
            player.AnimationFinished += _ => { if (player.HasAnimation(_clip)) player.Play(_clip); };
            _pose = player;
        }
        PlaceEye();
    }

    /// <summary>MENU3's camera for the k of this frame (see the summary), in the copy's own world.</summary>
    private void PlaceEye()
    {
        if (_eye == null) return;
        const float Distance = 204, Height = 217;
        float Angle = (0xB00 + (_mode == Mode.Status ? _statusYaw : 0)) * Mathf.Tau / 4096, pitch = _mode == Mode.Status ? _statusPitch * Mathf.Tau / 4096 : 0;
        // Opening a slot's list slides her to the middle (k to 0) over ten frames, and closing it back.
        // A limb's defences slide her to the middle too (MENU4's state 9, over ten frames), and back.
        float k = _mode == Mode.Status && _status != null ? (_status.Shown != null ? Ease[Math.Max(0, 10 - _defencesFrames)] : _defencesFrames < 10 ? 128 - Ease[10 - _defencesFrames] : 128)
            : _mode == Mode.List ? Ease[Math.Max(0, 10 - _modeFrames)]
            : _previous == Mode.List && _modeFrames < 10 ? 128 - Ease[10 - _modeFrames]
            : _frames >= 10 ? 128 : 128 + Ease[10 - _frames];
        var look = new Vector3(-Mathf.Sin(Angle) * k / 256 * Distance, -Height / 2, Mathf.Cos(Angle) * k / 256 * Distance);
        // MENU4 raises the eye by the pitch, its sine up and its cosine across (MENU3 keeps it level).
        var eye = look + new Vector3(Mathf.Cos(Angle) * Mathf.Cos(pitch) * 4 * Distance, -Mathf.Sin(pitch) * 4 * Distance, Mathf.Sin(Angle) * Mathf.Cos(pitch) * 4 * Distance);
        Vector3 Godot(Vector3 retail) => EventActor.RetailSpace * retail;
        _eye.LookAtFromPosition(Godot(eye), Godot(look), Vector3.Up);
    }

    /// <summary>
    /// Shows category <paramref name="category"/> (0 WEAPONS to 6 MISC, 7 OPTIONS) under <paramref name="header"/>,
    /// titled <paramref name="title"/> with <paramref name="count"/> beside it, its <paramref name="rows"/> with
    /// <paramref name="chosen"/> pointed at; <paramref name="browsing"/> while left and right change the category.
    /// </summary>
    public void Set(string header, int category, string title, string count, IReadOnlyList<Row> rows, int chosen, bool browsing, IReadOnlyList<Row>? actions = null, int action = 0)
    {
        Enter(Mode.Items);
        (_header, _category, _title, _count, _rows, _chosen, _browsing) = (header, category, title, count, rows, chosen, browsing && actions == null);
        (_actions, _action) = (actions, action);
        // Eight things show at a time; the list scrolls to keep the chosen one in view.
        if (_chosen < _first) _first = Math.Max(0, _chosen);
        if (_chosen >= _first + 8) _first = _chosen - 7;
        if (_first > Math.Max(0, rows.Count - 8)) _first = Math.Max(0, rows.Count - 8);
        QueueRedraw();
    }

    /// <summary>
    /// EQUIP's page: <paramref name="header"/> ("EQUIP/WEAPON" for the chosen slot), the eight slots' labels and what is
    /// worn in each (<paramref name="slots"/>), with <paramref name="chosen"/> pointed at.
    /// </summary>
    public void SetSlots(string header, string[] labels, IReadOnlyList<Row> slots, int chosen, bool strip = true)
    {
        Enter(Mode.Slots);
        (_header, _slotLabels, _rows, _chosen, _browsing, _strip) = (header, labels, slots, chosen, false, strip);
        QueueRedraw();
    }

    /// <summary>
    /// A slot's list: <paramref name="header"/>, then <paramref name="rows"/>, the first what is worn there and the
    /// rest what could be, with <paramref name="chosen"/> pointed at.
    /// </summary>
    public void SetList(string header, IReadOnlyList<Row> rows, int chosen, Stats? stats = null)
    {
        _stats = stats;
        Enter(Mode.List);
        (_header, _rows, _chosen, _browsing) = (header, rows, chosen, false);
        if (_chosen >= 1 && _chosen < _first + 1) _first = _chosen - 1;
        if (_chosen >= _first + 7) _first = _chosen - 6;
        if (_chosen == 0) _first = 0;
        QueueRedraw();
    }

    /// <summary>The container's first menu: REMOVE, STORE and OK (<paramref name="rows"/>, the last drawn bare) under its header.</summary>
    public void SetContainerTop(string header, IReadOnlyList<Row> rows, int chosen, Container container)
    {
        Enter(Mode.ContainerTop);
        (_header, _rows, _chosen, _browsing, _container) = (header, rows, chosen, false, container);
        QueueRedraw();
    }

    /// <summary>
    /// DATA's page (MENU7.PRG): its header and SAVE and LOAD as rows from x 194, with <paramref name="chosen"/> pointed
    /// at, and the play time: <paramref name="label"/> ("PLAY    TIME") at (224, 188) and <paramref name="time"/> under it
    /// at (224, 200), in the HUD's lettering.
    /// </summary>
    public void SetData(string header, IReadOnlyList<Row> rows, int chosen, string label, string time)
    {
        if (_mode != Mode.Data) Enter(Mode.Data);
        (_header, _rows, _chosen, _browsing, _playLabel, _playTime) = (header, rows, chosen, false, label, time);
        QueueRedraw();
    }

    private string _playLabel = "", _playTime = "";

    /// <summary>
    /// A page of SETUP (MENU12.BIN) over the backdrop alone: <paramref name="headers"/> as teal bars from (180, 18), a
    /// line apart, then the rows from the line below the last, 16 apart and eight at a time: commands bare from x 194,
    /// or things with their icons from x 169 (<paramref name="icons"/>), the pointer at the chosen one (footage:
    /// SETUP's six commands; SETUP and ATTACH GEMS over WEAPONS and SHIELDS; "ATTACH: Weapon" over the weapons).
    /// </summary>
    public void SetSetup(IReadOnlyList<string> headers, IReadOnlyList<Row> rows, int chosen, bool icons, int rowTop = -1, string caution = "")
    {
        if (_mode != Mode.Setup) Enter(Mode.Setup);
        (_headers, _rows, _chosen, _browsing, _iconRows) = (headers, rows, chosen, false, icons);
        (_setupRowTop, _setupCaution) = (rowTop, caution);
        if (_chosen < _first) _first = Math.Max(0, _chosen);
        if (_chosen >= _first + 8) _first = _chosen - 7;
        if (_first > Math.Max(0, rows.Count - 8)) _first = Math.Max(0, rows.Count - 8);
        QueueRedraw();
    }

    private IReadOnlyList<string> _headers = Array.Empty<string>();
    private bool _iconRows;
    private int _setupRowTop = -1;
    private string _setupCaution = "";

    /// <summary>Only the header (none when empty), for the memory card screens drawn over the menu; with <paramref name="caution"/>, that under CAUTION (the container's "Accessing MEMORY CARDs..." and its question as it is left).</summary>
    /// <summary>A limb on the STATUS screen: its name (BATTLE.PRG's, "#R.ARM"), condition (0 DYING to 4 EXCELLENT) and rig bone.</summary>
    public sealed record StatusLimb(string Name, int Condition, int Bone);

    /// <summary>
    /// The STATUS screen (MENU/MENU4.PRG): the HP, MP and RISK blocks, the limbs with their conditions, the status
    /// effects' icons (indices into the 32 retail ones), STRENGTH, INTELLIGENCE and AGILITY as ORG/EQP, the chosen
    /// element (0 to 5 a limb, 6 to 8 a stat, 9 HP, 10 MP, 11 RISK, 13 an icon, <paramref name="Icon"/>) and its help.
    /// </summary>
    public sealed record Status(int Hp, int MaxHp, int Mp, int MaxMp, int Risk, IReadOnlyList<StatusLimb> Limbs, string[] Conditions, Color[] ConditionColours,
        IReadOnlyList<int> Icons, int[] IconCells, string OrgEqp, (string Label, int Org, int Eqp)[] Stats, int Chosen, int Icon, string Help, Defences? Shown = null,
        string Name = "", bool Known = true);

    /// <summary>
    /// A limb's defences on the STATUS screen (Square): the AFFINITY (1) or TYPE (2) page of the limb's own values, the
    /// pages offered, and the chosen row.
    /// </summary>
    public sealed record Defences(int Page, string[] Pages, bool[] Enabled, string[] Labels, int[] Values, int Row);

    /// <summary>Frames since the STATUS screen's defences opened or closed, as the model slides to the middle and back.</summary>
    private int _defencesFrames = 99;
    private Status? _status;

    /// <summary>Whether the slots show the category strip and STATUS: not on the STATUS screen's equipment (MENU4).</summary>
    private bool _strip = true;

    /// <summary>The model's turn on the STATUS screen (L2 with the pad): its yaw and its pitch, 0 to 896, in 4096ths of a turn.</summary>
    private int _statusYaw, _statusPitch;

    /// <summary>Shows the STATUS screen; entering it stands the model at its first view.</summary>
    public void SetStatus(Status status)
    {
        if (_mode != Mode.Status) { _statusYaw = 0; _statusPitch = 0; }
        if ((status.Shown != null) != (_status?.Shown != null) && _mode == Mode.Status) _defencesFrames = 0;
        Enter(Mode.Status);
        _status = status;
        PlaceEye();
        QueueRedraw();
    }

    /// <summary>Turns the STATUS screen's model (MENU4: 64 a frame either way; the pitch from level to 896).</summary>
    public void TurnModel(int yaw, int pitch)
    {
        _statusYaw += yaw;
        _statusPitch = Math.Clamp(_statusPitch - pitch, 0, 896);
        PlaceEye();
    }

    public void SetHeader(string header, string caution = "", IReadOnlyList<Row>? answers = null, int chosen = -1)
    {
        Enter(Mode.Header);
        (_header, _rows, _chosen, _browsing, _headerCaution) = (header, answers ?? Array.Empty<Row>(), chosen, false, caution);
        QueueRedraw();
    }

    private string _headerCaution = "";

    /// <summary>A transfer list: category <paramref name="category"/> (or the pending list), its things and the container's counts.</summary>
    public void SetContainerList(string header, int category, string title, string count, IReadOnlyList<Row> rows, int chosen, Container container, IReadOnlyList<Row>? actions = null, int action = 0)
    {
        Enter(Mode.ContainerList);
        (_header, _category, _title, _count, _rows, _chosen, _browsing, _container) = (header, category, title, count, rows, chosen, container.Confirm == null && actions == null, container);
        (_actions, _action) = (actions, action);
        if (_chosen < _first) _first = Math.Max(0, _chosen);
        if (_chosen >= _first + 8) _first = _chosen - 7;
        if (_first > Math.Max(0, rows.Count - 8)) _first = Math.Max(0, rows.Count - 8);
        QueueRedraw();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Visible || (++_ticks & 1) != 0) return;
        _frames++;
        _modeFrames++;
        if (_defencesFrames < 99) { _defencesFrames++; if (_defencesFrames <= 11) PlaceEye(); }
        if (_frames <= 11 || _modeFrames <= 11) PlaceEye();
        if (_frames <= 16 || _browsing || _shown) QueueRedraw();
    }

    // CLASS/AFFINITY/TYPE (MAINMENU.PRG): the bar of a value, centred at x 116 and 24 either way, from the one colour
    // to the other as it grows.
    private static readonly Color PlusFrom = Color.Color8(24, 115, 129), PlusTo = Color.Color8(48, 224, 208), MinusFrom = Color.Color8(110, 63, 105), MinusTo = Color.Color8(220, 120, 159);
    private static readonly Color Lit = Color.Color8(0xF0, 0xC8, 0x28), Higher = Color.Color8(0x60, 0xA0, 0xFF), Lower = Color.Color8(0xFF, 0x90, 0x30);

    /// <summary>
    /// The panels (MAINMENU.PRG): DP and PP as the HUD shows HP and MP, at (10, 40) and (80, 40) with their words a
    /// line above; the page header (the Square button at (16, 46), the three page names from x 34 at y 48, the shown
    /// one lit) on a dark bar from x 24; the page's rows from y 64, 16 apart, each label right-aligned to x 64 and value
    /// to x 88 in the HUD capitals over its bar; and ORG/EQP at (252, 126), or 146 without RANGE and RISK, its rows 10
    /// apart, labels right-aligned to x 248 and the values to 276 and 308, Ashley's blue where it would rise and orange
    /// where it would fall.
    /// </summary>
    private void DrawStats(Stats stats, Vector2 unit, Action<string, float, float, bool> text, Action<float, float, float, Color, Color> bar)
    {
        if (_labels == null || _lettering == null) return;
        void Capitals(string word, float x, float y, bool right = false, Color? tint = null) =>
            _lettering!.Draw(this, word, new Vector2(right ? x - _lettering!.Width(word) : x, y), unit, tint);
        if (_digits != null)
        {
            Capitals(stats.DpWord, 10, 31);
            BattleHud.Reading(this, _digits, new Vector2I(10, 40), (stats.Dp + 99) / 100, (stats.MaxDp + 99) / 100, BattleHud.HpColour, unit);
            if (stats.MaxPp > 0 || stats.Pp > 0)
            {
                Capitals(stats.PpWord, 80, 31);
                BattleHud.Reading(this, _digits, new Vector2I(80, 40), stats.Pp, stats.MaxPp, BattleHud.MpColour, unit);
            }
        }
        DrawPage(stats.Page, stats.Pages, null, stats.Labels, stats.Values, unit, bar);
        OrgEqp(stats.OrgEqp, stats.Rows, stats.Ranged, unit);
    }


    /// <summary>
    /// The CLASS, AFFINITY or TYPE page (MAINMENU.PRG): its header (the Square button at (16, 46), the page names from
    /// x 34 at y 48, the shown one lit and those not offered greyed) on a dark bar from x 24, then its rows from y 64,
    /// 16 apart, each label right-aligned to x 64 and value to x 88 in the HUD capitals over its bar and gauge.
    /// </summary>
    private void DrawPage(int page, string[] pages, bool[]? enabled, string[] labels, int[] values, Vector2 unit, Action<float, float, float, Color, Color> bar)
    {
        if (_labels == null || _lettering == null) return;
        void Capitals(string text, float x, float y, bool right = false) =>
            _lettering!.Draw(this, text, new Vector2(right ? x - _lettering!.Width(text) : x, y), unit);
        // The page header.
        bar(24, 49, 135, MenuList.ChosenLight, MenuList.ChosenDark);
        if (_button != null) DrawTextureRectRegion(_button, new Rect2(new Vector2(16, 46) * unit, new Vector2(16, 16) * unit), new Rect2(16, 128, 16, 16));
        if (_system != null)
        {
            float x = 34;
            for (int i = 0; i < pages.Length; i++)
            {
                if (i > 0) { DrawText("/", x, Colors.White); x += _system.GetStringSize("/", HorizontalAlignment.Left, -1, 12).X; }
                DrawText(pages[i], x, i == page ? Lit : enabled != null && i < enabled.Length && !enabled[i] ? MenuList.Greyed : Colors.White);
                x += _system.GetStringSize(pages[i], HorizontalAlignment.Left, -1, 12).X;
            }
            void DrawText(string word, float at, Color colour)
            {
                DrawSetTransform(_pin, 0, unit);
                DrawString(_system!, new Vector2(at, 48 + 10), word, HorizontalAlignment.Left, -1, 12, colour);
                DrawSetTransform(_pin, 0, Vector2.One);
            }
        }
        for (int j = 0; j < labels.Length && j < values.Length; j++)
        {
            // MAINMENU's rows stand at 64 + 16 j.
            float y = 64 + 16 * j;
            // The row's bar under its label and value, then the gauge's slanted track (MAINMENU, measured from footage:
            // the bar to x 84, the track from x 90 to 144 on its top line, leaning 4 left by its foot).
            bar(0, y, 84, MenuList.BarDark, MenuList.BarLight);
            DrawPolygon(new[] { new Vector2(90, y) * unit, new Vector2(144, y) * unit, new Vector2(140, y + 12) * unit, new Vector2(86, y + 12) * unit },
                new[] { MenuList.BarLight, MenuList.BarDark, MenuList.BarDark, MenuList.BarLight });
            Capitals(labels[j], 64, y + 1, right: true);
            Capitals(values[j].ToString(), 88, y + 1, right: true);
            int length = Math.Clamp(values[j] * 24 / 100, -24, 24);
            if (length != 0)
            {
                var (from, to) = length > 0 ? (PlusFrom, PlusTo) : (MinusFrom, MinusTo);
                var end = from.Lerp(to, Math.Abs(length) / 24f);
                float a = 116, b = 116 + length;
                DrawPolygon(new[] { new Vector2(Math.Min(a, b), y + 2) * unit, new Vector2(Math.Max(a, b), y + 2) * unit, new Vector2(Math.Max(a, b), y + 6) * unit, new Vector2(Math.Min(a, b), y + 6) * unit },
                    length > 0 ? new[] { from, end, end, from } : new[] { end, from, from, end });
            }
            DrawLine(new Vector2(116.5f, y - 2) * unit, new Vector2(116.5f, y + 8) * unit, Color.Color8(0x80, 0x90, 0xC0), unit.X);
        }
    }

    /// <summary>
    /// ORG/EQP (MAINMENU.PRG): at (252, 126), or 146 without RANGE and RISK, its rows 10 apart, labels right-aligned to
    /// x 248 and the values to 276 and 308, the second blue where it would rise and orange where it would fall.
    /// </summary>
    private void OrgEqp(string word, (string Label, int Org, int Eqp, int Now)[] rows, bool ranged, Vector2 unit, bool known = true)
    {
        if (_labels == null || _lettering == null) return;
        void Capitals(string text, float x, float y, bool right = false, Color? tint = null) =>
            _lettering!.Draw(this, text, new Vector2(right ? x - _lettering!.Width(text) : x, y), unit, tint);
        Side(2);
        float top = ranged ? 126 : 146;
        Capitals(word, 252, top);
        for (int i = 0; i < rows.Length; i++)
        {
            var (label, org, eqp, now) = rows[i];
            float y = ranged ? 136 + 10 * i : 156 + 10 * i;
            Capitals(label, 248, y, right: true);
            // A character not analysed reads "??" (MAINMENU's renderEquipStats).
            Capitals(known ? org.ToString() : "??", 276, y, right: true);
            Capitals(known ? eqp.ToString() : "??", 308, y, right: true, known ? eqp > now ? Higher : eqp < now ? Lower : null : null);
        }
    }

    /// <summary>The leader lines' teal (MENU4.PRG: 0x687018), drawn half over what is under it, and the chosen limb's doubled and whole.</summary>
    private static readonly Color Leader = Color.Color8(0x18, 0x70, 0x68);

    /// <summary>The status effects whose icons take the second palette, (912, 223), by bit (BATTLE.PRG: 0x0F0F906A).</summary>
    private const uint SecondPalette = 0x0F0F906A;

    private Texture2D? _elements;

    /// <summary>
    /// The STATUS screen over the backdrop and model (MENU4.PRG). The HP, MP and RISK blocks stand where the HUD's do,
    /// RISK always as a number. Each limb is a row from y 34, 16 apart: its name right-aligned to x 216 in the HUD
    /// capitals, its condition's square (7 wide in the condition's colour, 5 inside it at twice it) at (216, y + 2)
    /// and the condition's word from 224; a leader line runs from the limb on the model, at 45 degrees to the row's
    /// foot (y + 8), then along it to x 216. The status effects' icons, eight to a row 16 apart, stand from (0, 144),
    /// an element's mark 8 right of an element effect's. STRENGTH, INTELLIGENCE and AGILITY are ORG/EQP's rows. The
    /// pointer is the menu's 16 by 16 cursor, grey off the limbs.
    /// </summary>
    private void DrawStatus(Status status, Vector2 unit, Action<Texture2D?, Rect2, Vector2, Color?> sprite)
    {
        if (_labels == null || _digits == null || _lettering == null) return;
        Side(0);
        BattleHud.Block(this, _labels, _digits, 2, status.Risk, 100, unit, status.Known);
        BattleHud.Block(this, _labels, _digits, 1, status.Mp, status.MaxMp, unit, status.Known);
        BattleHud.Block(this, _labels, _digits, 0, status.Hp, status.MaxHp, unit, status.Known);
        // The icons.
        for (int n = 0; n < status.Icons.Count; n++)
        {
            int i = status.Icons[n];
            if (i < 0 || i >= status.IconCells.Length) continue;
            var at = new Vector2((n & 7) * 16, 144 + (n >> 3) * 16);
            int cell = status.IconCells[i];
            sprite(((SecondPalette >> i) & 1) != 0 ? _arrowsIdle : _arrowsLive, new Rect2(cell & 0xFF, cell >> 8, 16, 16), at, null);
            if (i >= 16) sprite(_elements, new Rect2((i & 3) * 8 + 0x68, 0x30, 8, 8), at + new Vector2(8, 0), null);
        }
        // Square's STATUS button at the top left (MENU4: its band from x 8, the button at (8, 36), the word at (28, 38)),
        // lit on a limb; put away while a limb's defences are up.
        var defences = status.Shown;
        if (defences == null)
        {
            for (int i = 0; i < 10; i++)
            {
                var from = Color.Color8(64, 56, 32).Lerp(Color.Color8(16, 16, 8), i / 9f);
                DrawLine(new Vector2(8, 38 + i + 0.5f) * unit, new Vector2(96 - i, 38 + i + 0.5f) * unit, from, unit.Y);
            }
            sprite(_button, new Rect2(16, 128, 16, 16), new Vector2(8, 36), null);
            _lettering!.Draw(this, "STATUS", new Vector2(28, 38), unit, status.Chosen < status.Limbs.Count ? null : new Color(0.5f, 0.5f, 0.5f));
        }
        else
        {
            void Bar(float x, float y, float width, Color from, Color to) =>
                FieldView.Band(this, new Vector2(x, y), new Vector2(x + width, y + 12), from, to, unit);
            DrawPage(defences.Page, defences.Pages, defences.Enabled, defences.Labels, defences.Values, unit, Bar);
        }
        // The character's name on the header bar at the top right (MENU4: a row at (180, 18), 140 wide).
        if (status.Name.Length > 0)
        {
            Side(2);
            FieldView.Band(this, new Vector2(180, 18), new Vector2(320, 30), MenuList.HeaderLight, MenuList.HeaderDark, unit);
            if (_system != null)
            {
                DrawSetTransform(_pin, 0, unit);
                DrawString(_system, new Vector2(186, 18 + 12), status.Name, HorizontalAlignment.Left, -1, 12);
                DrawSetTransform(_pin, 0, Vector2.One);
            }
        }
        // The limbs, with the model in the middle of the screen; while a limb's defences are up the others are dimmed
        // to half and the lines put away.
        Side(1);
        Skeleton3D? bones = _stage?.FindChildren("*", "Skeleton3D", true, false) is { Count: > 0 } found ? (Skeleton3D)found[0] : null;
        for (int i = 0; i < status.Limbs.Count; i++)
        {
            var limb = status.Limbs[i];
            float y = 34 + 16 * i;
            // The rig names retail bone n "bn".
            int index = bones == null || limb.Bone < 0 ? -1 : bones.FindBone($"b{limb.Bone}");
            if (_eye != null && bones != null && index >= 0)
            {
                var point = bones.GlobalTransform * bones.GetBoneGlobalPose(index).Origin;
                if (defences == null && !_eye.IsPositionBehind(point))
                {
                    // The stage is drawn 320 by 256 from (0, -16).
                    var on = _eye.UnprojectPosition(point);
                    var from = new Vector2(on.X * 320 / 640f, on.Y * 256 / 576f - 16);
                    float foot = y + 8;
                    var knee = new Vector2(from.X + Math.Abs(foot - from.Y), foot);
                    bool chosen = i == status.Chosen;
                    var colour = chosen ? new Color(Leader.R * 2, Leader.G * 2, Leader.B * 2) : new Color(Leader, 0.5f);
                    DrawPolyline(new[] { from * unit, knee * unit, new Vector2(216, foot) * unit }, colour, unit.Y);
                }
            }
            string name = limb.Name.TrimStart('#');
            bool dim = defences != null && i != status.Chosen;
            Color? shade = dim ? new Color(0.5f, 0.5f, 0.5f) : null;
            _lettering!.Draw(this, name, new Vector2(216 - _lettering!.Width(name), y), unit, shade);
            var c = limb.Condition < status.ConditionColours.Length ? status.ConditionColours[limb.Condition] : Colors.White;
            if (dim) c = new Color(c.R / 2, c.G / 2, c.B / 2);
            DrawRect(new Rect2(new Vector2(216, y + 2) * unit, new Vector2(7, 7) * unit), c);
            DrawRect(new Rect2(new Vector2(217, y + 3) * unit, new Vector2(5, 5) * unit), new Color(Math.Min(1, c.R * 2), Math.Min(1, c.G * 2), Math.Min(1, c.B * 2)));
            if (limb.Condition < status.Conditions.Length) _lettering!.Draw(this, status.Conditions[limb.Condition], new Vector2(224, y), unit, shade);
        }
        var rows = new (string, int, int, int)[status.Stats.Length];
        for (int i = 0; i < rows.Length; i++) rows[i] = (status.Stats[i].Label, status.Stats[i].Org, status.Stats[i].Eqp, status.Stats[i].Eqp);
        OrgEqp(status.OrgEqp, rows, false, unit, status.Known);
        // The pointer (MENU4's places, the menu cursor's 16 by 16 cell from (32, 48)): at a limb, a stat, HP, MP or RISK.
        Vector2? pointer = defences != null ? new Vector2(0, 50 + 16 * defences.Row) : status.Chosen switch
        {
            >= 0 and < 6 => new Vector2(160, 20 + 16 * status.Chosen),
            >= 6 and < 9 => new Vector2(152, 146 + 10 * (status.Chosen - 6)),
            9 => new Vector2(-4, 4),
            10 => new Vector2(66, 4),
            11 => new Vector2(-4, 12),
            13 => new Vector2((status.Icon & 7) * 16, 144 + (status.Icon >> 3) * 16 - 8),
            _ => null,
        };
        if (pointer is { } p)
        {
            Side(defences != null ? 0 : status.Chosen < 6 ? 1 : status.Chosen < 9 ? 2 : 0);
            sprite(defences != null || status.Chosen < status.Limbs.Count ? _arrowsLive : _greyCursor, new Rect2(32, 48, 16, 16), p, null);
        }
    }

    private Texture2D? _greyCursor;

    /// <summary>Where the part being drawn keeps to: the window's left edge, its middle or its right edge (see FieldView.Pin).</summary>
    private Vector2 _pin;

    private void Side(int side)
    {
        _pin = FieldView.Pin(Size, side).Offset;
        DrawSetTransform(_pin);
    }

    /// <summary>
    /// The stores' counts by category in the HUD capitals, each a count (blue) right-aligned before the slash and its
    /// limit right-aligned after (MENUD.PRG). The first menu shows both: GADGET BAG at (57, 85) with its counts to x 77
    /// and 110, the category names centred on x 159, and CONTAINER at (186, 85) with its counts to x 207 and 247, a row
    /// every 12 lines from y 98. A transfer list shows the container's alone down the left: CONTAINER at (15, 73), the
    /// names from x 22 and the counts to x 96 and 125, from y 85.
    /// </summary>
    private void DrawCounts(Container c, Vector2 unit, bool both)
    {
        if (_labels == null || _lettering == null) return;
        void Word(string word, float x, float y, Color? tint = null) => _lettering!.Draw(this, word, new Vector2(x, y), unit, tint);
        var blue = Color.Color8(0x50, 0x70, 0xFF);
        void Count(int count, int limit, float countRight, float limitRight, float y)
        {
            string a = count.ToString(), b = limit.ToString();
            Word(a, countRight - _lettering!.Width(a), y, blue);
            Word(c.Slash, countRight, y);
            // A limit of three digits starts just after the slash.
            Word(b, Math.Max(limitRight - _lettering!.Width(b), countRight + _lettering!.Width(c.Slash)), y);
        }
        if (both)
        {
            Word(c.BagWord, 57, 85);
            Word(c.BoxWord, 186, 85);
            for (int i = 0; i < c.Categories.Length && i < 7; i++)
            {
                float y = 98 + 12 * i;
                Count(c.BagCounts[i], c.BagLimits[i], 77, 110, y);
                Word(c.Categories[i], 159 - _lettering!.Width(c.Categories[i]) / 2, y);
                Count(c.BoxCounts[i], c.BoxLimits[i], 207, 247, y);
            }
            return;
        }
        Word(c.BoxWord, 15, 73);
        for (int i = 0; i < c.Categories.Length && i < 7; i++)
        {
            float y = 85 + 12 * i;
            Word(c.Categories[i], 22, y);
            Count(c.BoxCounts[i], c.BoxLimits[i], 96, 125, y);
        }
    }

    public override void _Draw()
    {
        var unit = FieldView.Pin(Size, 1).Unit;
        // Black over the whole window, beyond the 4:3 screen too.
        DrawSetTransform(Vector2.Zero);
        DrawRect(new Rect2(Vector2.Zero, Size), Colors.Black);
        // The backdrop and Ashley stay in the middle; MENU and the lists keep to the window's right edge, the strip,
        // STATUS, the panels and the description to its left.
        Side(1);
        // The backdrop, a step brighter each frame for eight frames.
        float fade = Math.Min(8, _frames) / 8f;
        FieldView.Sprite(this, _backdrop, new Rect2(0, 0, 192, 240), new Vector2(64, 0), unit, new Color(fade, fade, fade));
        // Ashley, over the backdrop and under everything else. Her view is 320 by 256 about (160, 112), its x scaled
        // by 9/8: the picture's width over its height is 640/576, so that 160 columns either side see 160/576 of the
        // depth.
        if (_shown && _stage != null) DrawTextureRect(_stage.GetTexture(), new Rect2(new Vector2(0, 112 - 128) * unit, new Vector2(320, 256) * unit), false);
        int menuSlide = Math.Min(12, _frames * 2);
        Side(2);
        if (MenuWordShown) FieldView.Sprite(this, _menuPage, MenuWord, new Vector2(276, menuSlide - 6), unit);
        Side(0);

        // A slot's list puts the strip and STATUS away, but for the chosen category's icon at the left; the
        // container's first menu has neither.
        if (_mode == Mode.List) FieldView.Sprite(this, _chosenIcons, new Rect2(120 + 16 * _category, 128, 16, 16), new Vector2(0, 14), unit);
        else if (_mode == Mode.Slots && !_strip) { }
        else if (_mode is Mode.ContainerTop or Mode.Header or Mode.Data or Mode.Setup or Mode.Status) { }
        else
        {
            // The category strip and its arrows.
            // MENU3 slides the strip down to y 16.
            float iconsY = Math.Min(4, _frames) * 8 - 16;
            for (int i = 0; i < 8; i++)
                FieldView.Sprite(this, i == _category ? _chosenIcons : _icons, new Rect2(120 + 16 * i, 128, 16, 16), new Vector2(((i + 1) & 7) * 16 + 32, iconsY), unit);
            int wobble = _browsing ? (_frames % 12) switch { < 3 => 0, < 6 => 1, < 9 => 2, _ => 1 } : 0;
            FieldView.Sprite(this, _browsing ? _arrowsLive : _arrowsIdle, new Rect2(0, 48, 16, 16), new Vector2(16 - wobble, iconsY), unit);
            FieldView.Sprite(this, _browsing ? _arrowsLive : _arrowsIdle, new Rect2(16, 48, 16, 16), new Vector2(162 + wobble, iconsY + 1), unit);

            // STATUS, sliding in from the left.
            float statusX = -Ease[Math.Max(0, 10 - Math.Min(10, _frames))];
            for (int i = 0; i < 10; i++)
            {
                var from = Color.Color8(64, 56, 32).Lerp(Color.Color8(16, 16, 8), i / 9f);
                DrawLine(new Vector2(statusX + 8, 38 + i + 0.5f) * unit, new Vector2(statusX + 96 - i, 38 + i + 0.5f) * unit, from, unit.Y);
            }
            if (_mode == Mode.ContainerList && _container != null && _labels != null && _lettering != null)
            {
                // A transfer list's buttons: Square STATUS and Triangle CANCEL, then Cross END a row lower, each on its
                // olive band.
                for (int i = 0; i < 10; i++)
                {
                    var shade = Color.Color8(64, 56, 32).Lerp(Color.Color8(16, 16, 8), i / 9f);
                    DrawLine(new Vector2(statusX + 8, 54 + i + 0.5f) * unit, new Vector2(statusX + 76 - i, 54 + i + 0.5f) * unit, shade, unit.Y);
                    DrawLine(new Vector2(statusX + 80, 38 + i + 0.5f) * unit, new Vector2(statusX + 150 - i, 38 + i + 0.5f) * unit, shade, unit.Y);
                }
                FieldView.Sprite(this, _button, new Rect2(0, 128, 16, 16), new Vector2(statusX + 80, 36), unit);
                FieldView.Sprite(this, _button, new Rect2(48, 128, 16, 16), new Vector2(statusX + 8, 52), unit);
                _lettering!.Draw(this, _container.Buttons[1], new Vector2(statusX + 100, 38), unit);
                // END is dim while the confirmation is up.
                _lettering!.Draw(this, _container.Buttons[2], new Vector2(statusX + 28, 54), unit, _container.Confirm != null ? new Color(0.5f, 0.5f, 0.5f) : null);
            }
            FieldView.Sprite(this, _button, new Rect2(16, 128, 16, 16), new Vector2(statusX + 8, 36), unit);
            // Lit (0x80) while a thing is chosen whose status it can show, dim (0x40) otherwise.
            if (_labels != null && _lettering != null)
            {
                // In a transfer list it is lit only on a piece of equipment.
                bool lit = _mode == Mode.ContainerList ? _container?.Confirm == null && _rows.Count > 0 && _category < 6 : _category != Options;
                var tint = lit ? Colors.White : new Color(0.5f, 0.5f, 0.5f);
                _lettering!.Draw(this, "STATUS", new Vector2(statusX + 28, 38), unit, tint);
            }
        }

        // The rows.
        void Bar(float x, float y, float width, Color from, Color to)
        {
            float top = y;
            FieldView.Band(this, new Vector2(x, top), new Vector2(x + width, top + 12), from, to, unit);
        }
        void Text(string text, float x, float y, bool right = false, Color? colour = null, Font? font = null)
        {
            font ??= _system;
            if (font == null) return;
            DrawSetTransform(_pin, 0, unit);
            float w = right ? font.GetStringSize(text, HorizontalAlignment.Left, -1, 12).X : 0;
            DrawString(font, new Vector2(x - w, y + 12), text, HorizontalAlignment.Left, -1, 12, colour ?? Colors.White);
            DrawSetTransform(_pin, 0, Vector2.One);
        }
        // A list's count of what it holds over its limit ("8/ 64") in the HUD capitals: the limit right-aligned to
        // six short of the row's end, a space before it, then the slash and the count (retail footage).
        void Count(string text, float right, float y)
        {
            if (_labels == null || _lettering == null) return;
            int slash = text.IndexOf('/');
            string held = slash < 0 ? text : text[..(slash + 1)], limit = slash < 0 ? "" : text[(slash + 1)..].Trim();
            float end = right - 6;
            if (limit.Length > 0)
            {
                _lettering!.Draw(this, limit, new Vector2(end - _lettering!.Width(limit), y + 1), unit);
                end -= _lettering!.Width(limit) + 6;
            }
            _lettering!.Draw(this, held, new Vector2(end - _lettering!.Width(held), y + 1), unit);
        }
        float slide = Ease[Math.Max(0, 10 - Math.Min(10, _frames))];
        // A header bar down the right with its words (and a count at its end), as each page heads its list.
        void Header(string text, float y, string count = "")
        {
            Bar(180 + slide, y, 140, MenuList.HeaderLight, MenuList.HeaderDark);
            Text(text, 186 + slide, y);
            if (count.Length > 0) Count(count, 316 + slide, y);
        }
        Side(2);
        void Row(Row row, float x, float y, float width, bool chosen, bool held = false)
        {
            Bar(x, y, width, chosen ? MenuList.ChosenLight : MenuList.BarDark, chosen ? MenuList.ChosenDark : MenuList.BarLight);
            if (row.Icon >= 0 && _labels != null) FieldView.Sprite(this, _labels, new Rect2(row.Icon % 16 * 16, 98 + row.Icon / 16 * 16, 16, 16), new Vector2(x + 3, y - 5), unit, new Color(0.42f, 1.25f, 1.2f));
            if (row.Icon >= 0 && row.Material is >= 1 and <= 7 && _labels != null) FieldView.Sprite(this, _labels, MaterialLetters[row.Material - 1], new Vector2(x + 13, y - 3), unit, MaterialTint);
            // SET (16 by 13 at (80, 129)) or EQP (16 by 16 at (96, 128)) stands 24 left of the icon (retail footage:
            // the container's BLADES), from the HUD page through the gold palette at (976, 223).
            if (row.Badge == 1) FieldView.Sprite(this, _chosenIcons, new Rect2(80, 129, 16, 13), new Vector2(x - 21, y - 4), unit);
            else if (row.Badge == 2) FieldView.Sprite(this, _chosenIcons, new Rect2(96, 128, 16, 16), new Vector2(x - 21, y - 5), unit);
            Text(row.Name, x + (row.Icon >= 0 ? 22 : 6), y, colour: held ? ChosenName : row.Greyed ? MenuList.Greyed : null);
            if (row.Count >= 0 && _digits != null)
            {
                // A thing's count in the HUD's small digits (6 wide, 5 apart), ending at x 310 (retail footage).
                string digits = row.Count.ToString();
                for (int k = 0; k < digits.Length; k++)
                    FieldView.Sprite(this, _digits, new Rect2((digits[k] - '0') * 6, 0, 6, 10), new Vector2(309 - 5 * (digits.Length - k), y + 1), unit);
            }
        }
        if (_mode == Mode.Slots)
        {
            // EQUIP: the header, then the eight slots from y 34, each labelled to its left in the HUD capitals.
            Header(_header, 18);
            for (int i = 0; i < _rows.Count; i++)
            {
                float y = 34 + 16 * i;
                Row(_rows[i], 169 + slide, y, 151, i == _chosen);
                if (i < _slotLabels.Length && _labels != null && _lettering != null)
                {
                    float width = _lettering!.Width(_slotLabels[i]);
                    _lettering!.Draw(this, _slotLabels[i], new Vector2(159 + slide - width, y + 1), unit);
                }
            }
            if (_chosen >= 0 && _chosen < _rows.Count) FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(155, 34 + 16 * _chosen - 10), unit);
        }
        else if (_mode == Mode.List)
        {
            // A slot's list: the header across the left, what is worn there beside it, and what could be from y 34,
            // six at a time.
            Side(0);
            Bar(16, 18, 164, MenuList.HeaderLight, MenuList.HeaderDark);
            Text(_header, 20, 18);
            Side(2);
            if (_rows.Count > 0) Row(_rows[0], 155, 18, 165, _chosen == 0);
            for (int i = Math.Max(1, _first); i < _rows.Count && i < _first + 7; i++)
                Row(_rows[i], 169, 34 + 16 * (i - Math.Max(1, _first)), 151, i == _chosen);
            if (_chosen == 0) FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(141, 18 - 10), unit);
            else if (_chosen >= Math.Max(1, _first) && _chosen < _rows.Count) FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(155, 34 + 16 * (_chosen - Math.Max(1, _first)) - 10), unit);
            if (_stats is { } stats) { Side(0); DrawStats(stats, unit, (text, x, y, right) => Text(text, x, y, right), Bar); }
        }
        else if (_mode == Mode.Header)
        {
            if (_header.Length > 0)
            {
                Bar(180, 18, 140, MenuList.HeaderLight, MenuList.HeaderDark);
                Text(_header, 186, 18);
            }
            // Answers (the container's YES and NO as it is left) from x 194 at y 148, the pointer at the chosen one.
            for (int i = 0; i < _rows.Count; i++) Row(_rows[i], 194, 148 + 16 * i, 126, i == _chosen);
            if (_chosen >= 0 && _chosen < _rows.Count) FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(180, 148 + 16 * _chosen - 10), unit);
            if (_headerCaution.Length > 0)
            {
                Side(0);
                FieldView.Sprite(this, _labels, CautionWord, new Vector2(8, 181), unit);
                var lines = _headerCaution.Split('\n');
                for (int i = 0; i < lines.Length && i < 3; i++) Text(lines[i], 16, 192 + 13 * i);
            }
            DrawSetTransform(Vector2.Zero);
            return;
        }
        else if (_mode == Mode.Setup)
        {
            for (int h = 0; h < _headers.Count; h++)
            {
                Bar(180 + slide, 18 + 16 * h, 140, MenuList.HeaderLight, MenuList.HeaderDark);
                Text(_headers[h], 186 + slide, 18 + 16 * h);
            }
            float top = _setupRowTop >= 0 ? _setupRowTop : 18 + 16 * _headers.Count, x = _iconRows ? 169 : 194;
            for (int i = _first; i < _rows.Count && i < _first + 8; i++) Row(_rows[i], x + slide, top + 16 * (i - _first), _iconRows ? 151 : 126, i == _chosen);
            if (_chosen >= _first && _chosen < _rows.Count) FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(x - 14, top + 16 * (_chosen - _first) - 10), unit);
        }
        else if (_mode == Mode.Data)
        {
            Header(_header, 18);
            for (int i = 0; i < _rows.Count; i++) Row(_rows[i], 194 + slide, 34 + 16 * i, 126, i == _chosen);
            if (_chosen >= 0 && _chosen < _rows.Count) FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(180, 34 + 16 * _chosen - 10), unit);
            if (_labels != null && _lettering != null)
            {
                _lettering!.Draw(this, _playLabel, new Vector2(224, 188), unit);
                _lettering!.Draw(this, _playTime, new Vector2(224, 200), unit);
            }
        }
        else if (_mode == Mode.Status && _status is { } status) DrawStatus(status, unit, (texture, source, at, tint) => FieldView.Sprite(this, texture, source, at, unit, tint));
        else if (_mode == Mode.ContainerTop && _container is { } top)
        {
            // CONTAINER, then REMOVE and STORE as rows and OK bare; below, the bag's and the container's counts.
            Header(_header, 18);
            for (int i = 0; i < _rows.Count; i++)
            {
                if (i < _rows.Count - 1) Row(_rows[i], 194 + slide, 34 + 16 * i, 126, i == _chosen);
                else Text(_rows[i].Name, 200 + slide, 34 + 16 * i);
            }
            if (_chosen >= 0 && _chosen < _rows.Count) FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(180, 34 + 16 * _chosen - 10), unit);
            Side(1);
            DrawCounts(top, unit, true);
        }
        else if (_mode == Mode.ContainerList && _container is { } list)
        {
            Header(_header, 18);
            Header(_title, 34, _count);
            for (int i = _first; i < _rows.Count && i < _first + 8; i++)
                Row(_rows[i], 169 + slide, 50 + 16 * (i - _first), 151, i == _chosen && list.Confirm == null && _actions == null, i == _chosen && _actions != null);
            if (list.Confirm == null && _actions == null && _chosen >= _first && _chosen < _rows.Count)
                FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(155, 50 + 16 * (_chosen - _first) - 10), unit);
            Side(0);
            if (list.Confirm == null && _actions == null) DrawCounts(list, unit, false);
            if (_actions is { } boxActions && list.Confirm == null)
            {
                // The chosen thing's actions down the left in place of the counts, the last on line 162 (footage:
                // STORE, DISCARD and SORT).
                for (int k = 0; k < boxActions.Count; k++)
                    Row(boxActions[k], 0, 162 - 16 * (boxActions.Count - 1 - k), 125, k == _action);
                FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(-2, 162 - 16 * (boxActions.Count - 1 - _action) - 10), unit);
            }
            if (list.Confirm is { } ask)
            {
                // YES and NO over the left, the question under CAUTION.
                Row(new Row(ask.Yes, -1, -1, ""), 0, 146, 125, ask.Chosen == 0);
                Row(new Row(ask.No, -1, -1, ""), 0, 162, 125, ask.Chosen == 1);
                FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(-2, 146 + 16 * ask.Chosen - 10), unit);
                FieldView.Sprite(this, _labels, CautionWord, new Vector2(8, 181), unit);
                var lines = ask.Question.Split('\n');
                for (int i = 0; i < lines.Length && i < 3; i++) Text(lines[i], 16, 192 + 13 * i);
                DrawSetTransform(Vector2.Zero);
                return;
            }
            if (_rows.Count == 0 && list.Empty.Length > 0)
            {
                FieldView.Sprite(this, _labels, MenuList.InformationWord, new Vector2(8, 181), unit);
                var lines = list.Empty.Split('\n');
                for (int i = 0; i < lines.Length && i < 3; i++) Text(lines[i], 16, 192 + 13 * i);
                DrawSetTransform(Vector2.Zero);
                return;
            }
        }
        else
        {
            Header(_header, 18);
            Header(_title, 34, _count);
            bool options = _category == Options;
            float rowX = options ? 194 : 169, rowWidth = options ? 126 : 151;
            for (int i = _first; i < _rows.Count && i < _first + 8; i++)
                // While its actions are up the chosen thing's bar is unlit and its name yellow.
                Row(_rows[i], rowX + slide, 50 + 16 * (i - _first), rowWidth, i == _chosen && _actions == null, i == _chosen && _actions != null);
            if (_actions == null && _chosen >= _first && _chosen < _rows.Count)
                FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(options ? 180 : 155, 50 + 16 * (_chosen - _first) - 10), unit);
            if (_actions is { } actions)
            {
                // The chosen thing's actions down the left, the last on line 162 as the container's NO is, the
                // pointer at the chosen one; its help replaces the thing's.
                Side(0);
                for (int k = 0; k < actions.Count; k++)
                    Row(actions[k], 0, 162 - 16 * (actions.Count - 1 - k), 125, k == _action);
                FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(-2, 162 - 16 * (actions.Count - 1 - _action) - 10), unit);
            }
        }

        // The chosen row's help.
        Side(0);
        var (help, caution) = Help;
        if (help.Length > 0)
        {
            FieldView.Sprite(this, _labels, caution ? CautionWord : MenuList.InformationWord, new Vector2(8, 181), unit);
            var lines = help.Split('\n');
            for (int i = 0; i < lines.Length && i < 4; i++) Text(lines[i], 16, 192 + 13 * i);
        }
        DrawSetTransform(Vector2.Zero);
    }
}
