// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The memory card screens, as the menus show them when saving or loading (the file menu MENU7, the container and
/// the game over share them; loading is as saving with LOAD, without the overwrite, empty files not to be chosen): SAVE
/// slides in under the menu's header, then the two MEMORY CARD slots; the chosen slot's card is checked and its five
/// files listed three at a time, each with its picture, place, MAP%, save and clear counts, time, HP and MP. A file
/// that holds a save asks before it is overwritten; saving sweeps the file's row, flashes it, and says so.
/// The two slots are two folders of five files (<see cref="FieldView.CardFile"/>).
/// Laid out on retail's 320 by 240 screen: SAVE and the slots keep to the window's right edge, YES and NO and the
/// message to its left, the files to its middle.
/// </summary>
public sealed partial class MemoryCardMenu : RetailScreen
{
    // What each element is doing: still, sliding along x to its target, sliding up to it, or sliding in from the left.
    private enum Motion { Off, Still, AlongX, Up, FromLeft }

    private sealed class Element
    {
        public Motion Motion = Motion.Still;
        public int X, Y, Width, Height = 12, Target, Inner, Outer, File = -1;
        public string Text = "";
        public bool Selected;
    }

    // The flow: SAVE and the slots sliding in, choosing a slot, the card being checked, its files, asking before an
    // overwrite, saving, and the result shown before the screens leave.
    private enum Stage { Opening, Slots, Checking, Files, NoData, Back, Overwrite, Saving, Saved, Leaving, Fading }

    /// <summary>The slide's steps toward the target, nearest first (the menus' slide).</summary>
    private static readonly int[] Stops = { 0, 1, 2, 4, 8, 16, 32, 56, 80, 104, 128, 152, 176, 200, 224, 248 };
    // Bar colours: a slot's or file's (blue) and a header's (teal), each a bright and a dark end.
    private static readonly Color BlueBright = Color.Color8(0, 65, 107), BlueDark = Color.Color8(0, 5, 51);
    private static readonly Color TealBright = Color.Color8(25, 130, 108), TealDark = Color.Color8(1, 40, 38);
    private static readonly Color Dim = Color.Color8(80, 110, 170);
    /// <summary>How long the card is checked and the file written, in frames.</summary>
    private const int CheckFrames = 60, SaveFrames = 180, LoadFrames = 120;

    /// <summary>
    /// How long the memory cards are searched for this game's data, "Accessing MEMORY CARDs..." (MCMAN 22) under
    /// CAUTION, in the menu's 30 Hz steps: about 8.2 seconds in footage, from DATA's SAVE and as a container opens
    /// alike (Episode 3 at 16:25 and 17:14).
    /// </summary>
    public const int AccessFrames = 246;

    /// <summary>The field that saves, or null where there is nothing to save (the title).</summary>
    private FieldView? _view;
    /// <summary>Resident menu sounds, supplied by the field or title owner.</summary>
    public Action<int>? Sound;
    /// <summary>The screen's fade to black as a loaded game, or a menu left for the title, goes.</summary>
    private ColorRect _shade = null!;
    /// <summary>Where the sweep and flash are drawn, added to the screen.</summary>
    private Control _glow = null!;
    private Texture2D? _pictures, _digits, _words, _labels, _cursor;
    /// <summary>The crystal the screens stand on where no menu draws it (the title's Continue): MENU/MENUBG.BIN, as TITLE.PRG has it too.</summary>
    private Texture2D? _backdrop;
    private Font? _font;
    private Godot.Collections.Array _strings = new();
    private readonly Element[] _elements = new Element[10];
    private Stage _stage;
    private int _card = 1, _slot, _page, _frames, _stageFrames, _confirm = 1, _location, _result;
    private string _message = "";
    private bool _dismissed, _access, _saving = true, _fadeOut;

    /// <summary>The save the screens loaded, once they return 1 from loading.</summary>
    public GDictionary? Loaded { get; private set; }
    /// <summary>A cleared record supplied by the ending, saved through the same card screens.</summary>
    public GDictionary? SaveData;
    private readonly GDictionary?[] _files = new GDictionary?[5];

    public void Configure(Wol.Games.Vagrant.Runtime.RetailContent content, FieldView? view)
    {
        _view = view;
        if (view != null) Sound = view.MenuSound;
        Texture2D? Page(string name)
        {
            try { return content.ScreenTexture(name); }
            catch (System.IO.IOException) { return null; }
        }
        if (content.Has("memory_card")) _strings = content.Data("memory_card")["strings"].AsGodotArray();
        _pictures = Page("memory_card_pictures");
        _digits = Page("hud_digits");
        _words = Page("hud_page_848");
        _labels = Page("hud_labels");
        _cursor = Page("hud_cursor");
        if (view == null) _backdrop = Page("item_menu_backdrop");
        _font = content.Has("system_font") ? content.BitmapFont("system_font") : null;
        _glow = new Control { Name = "Glow", MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1, Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add } };
        _glow.Draw += DrawGlow;
        AddChild(_glow);
        _shade = new ColorRect { Name = "Shade", Color = new Color(0, 0, 0, 0), MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 };
        AddChild(_shade);
        Visible = false;
    }

    /// <summary>Whether the screens are up.</summary>
    public bool Open => Visible;
    /// <summary>The screens' stage (Opening, Slots, Checking, Files, Back, Overwrite, Saving, Saved, Leaving), and whether everything has come to rest.</summary>
    public string Showing => Visible ? _stage + (Settled() ? "" : "*") : "";

    /// <summary>Back to the slots: the leaving sound, both slots slide out to come in again, the card's slot chosen, no message.</summary>
    private void BackToSlots()
    {
        Sound?.Invoke(FieldView.MenuLeave);
        Slide(1, 320);
        Slide(2, 320);
        _slot = _card - 1;
        _message = "";
        Enter(Stage.Back);
    }

    private string Text(int index) => SystemBox.Entry(_strings, index);

    /// <summary>
    /// Starts saving, for a save of <paramref name="location"/> (a save point's or container's location number). From
    /// the menu's DATA the cards are first searched for this game's save (<paramref name="access"/>), which says so.
    /// </summary>
    public void OpenSave(int location, bool access = false)
    {
        _location = location;
        Begin(true, access, false);
    }

    /// <summary>Starts loading; with <paramref name="fadeOut"/> the screen also fades when the screens are left.</summary>
    public void OpenLoad(bool fadeOut)
    {
        Loaded = null;
        Begin(false, false, fadeOut);
    }

    private void Begin(bool saving, bool access, bool fadeOut)
    {
        (_saving, _access, _fadeOut) = (saving, access, fadeOut);
        Array.Clear(_elements);
        _slot = 0;
        _confirm = 1;
        _message = "";
        _frames = 0;
        _result = 0;
        _shade.Color = new Color(0, 0, 0, 0);
        var save = Add(0, 320, 34, 140, Text(saving ? 0 : 2));
        save.Motion = Motion.AlongX;
        save.Target = 180;
        save.Inner = 8;
        save.Selected = true;
        Enter(Stage.Opening);
        Visible = true;
        QueueRedraw();
    }

    private Element Add(int id, int x, int y, int width, string text) => _elements[id] = new Element { X = x, Y = y, Width = width, Text = text };

    private void Enter(Stage stage) { _stage = stage; _stageFrames = 0; }

    /// <summary>Whether every element has come to rest.</summary>
    private bool Settled() => Array.TrueForAll(_elements, e => e == null || e.Motion is Motion.Still or Motion.Off);

    private void Slide(int id, int target)
    {
        if (_elements[id] is not { } element) return;
        element.Motion = Motion.AlongX;
        element.Target = target;
    }

    /// <summary>Steps a frame: 0 while the screens are up, 1 once saved (or loaded) and gone, -1 when left without.</summary>
    public int Step(Func<string, bool> pressed)
    {
        if (!Visible) return _result;
        _frames++;
        _stageFrames++;
        int step = (pressed("ps_down") ? 1 : 0) - (pressed("ps_up") ? 1 : 0);
        switch (_stage)
        {
            case Stage.Opening:
                // The slots follow SAVE, one a frame, then the question.
                if (!Settled()) break;
                if (_access)
                {
                    if (_message.Length == 0) { _message = Text(22); _stageFrames = 0; }
                    if (_stageFrames < AccessFrames) break;
                    _access = false;
                    _message = "";
                }
                if (_elements[1] == null)
                {
                    Add(1, 320, 50, 126, Text(4)).Motion = Motion.AlongX;
                    _elements[1].Target = 194;
                    Add(2, 320, 66, 126, Text(5)).Motion = Motion.AlongX;
                    _elements[2].Target = 194;
                    break;
                }
                _message = Text(6);
                Enter(Stage.Slots);
                break;
            case Stage.Slots:
                _elements[1 + _slot].Selected = true;
                _elements[2 - _slot].Selected = false;
                if (pressed("ps_cross"))
                {
                    Sound?.Invoke(FieldView.MenuLeave);
                    for (int i = 0; i < 3; i++) Slide(i, 320);
                    _message = "";
                    Enter(Stage.Leaving);
                    _result = -1;
                    _dismissed = false;
                }
                else if (pressed("ps_circle"))
                {
                    // The chosen slot moves out to SAVE's column and takes its colour; the other leaves.
                    Sound?.Invoke(FieldView.MenuSelect);
                    _card = 1 + _slot;
                    _message = Text(7);
                    Slide(1 + _slot, 180);
                    _elements[1 + _slot].Inner = 1;
                    Slide(2 - _slot, 320);
                    Enter(Stage.Checking);
                }
                else if (step != 0) { _slot = 1 - _slot; Sound?.Invoke(FieldView.MenuChange); }
                break;
            case Stage.Checking:
                if (!Settled()) break;
                if (_card == 2 && _elements[2].Y > 50) { _elements[2].Motion = Motion.Up; _elements[2].Target = 50; break; }
                if (_stageFrames < CheckFrames) break;
                for (int i = 0; i < 5; i++)
                {
                    _files[i] = FieldView.ReadCardFile(_card, i + 1);
                    var file = Add(5 + i, 64, 72 + 40 * i, 256, "");
                    file.File = i;
                    file.Height = 32;
                }
                _page = 0;
                _slot = 0;
                if (!_saving)
                {
                    // Loading starts on the newest save, or says there is none.
                    int newest = -1;
                    for (int i = 0; i < 5; i++)
                        if (_files[i] is { } file && (newest < 0 || file.GetValueOrDefault("generation", 0).AsInt32() > _files[newest]!.GetValueOrDefault("generation", 0).AsInt32())) newest = i;
                    if (newest < 0)
                    {
                        for (int i = 5; i < 10; i++) _elements[i] = null!;
                        _message = Text(9);
                        Enter(Stage.NoData);
                        break;
                    }
                    (_page, _slot) = newest == 4 ? (2, 2) : newest == 0 ? (0, 0) : (newest - 1, 1);
                }
                for (int i = 0; i < 5; i++) _elements[5 + i].Y = 72 + 40 * (i - _page);
                _message = Text(12);
                Enter(Stage.Files);
                break;
            case Stage.NoData:
                if (!(pressed("ps_circle") || pressed("ps_cross") || pressed("ps_triangle") || pressed("ps_square"))) break;
                BackToSlots();
                break;
            case Stage.Files:
            {
                int chosen = _page + _slot;
                if (pressed("ps_cross"))
                {
                    // Back to the slots: the files go, the slot leaves, and both slots come in again.
                    for (int i = 5; i < 10; i++) _elements[i] = null!;
                    BackToSlots();
                    break;
                }
                if (pressed("ps_circle") && !_saving)
                {
                    // Only a file holding a save can be loaded.
                    if (_files[chosen] != null) { Sound?.Invoke(FieldView.MenuSelect); _message = Text(15); Enter(Stage.Saving); }
                    else Sound?.Invoke(FieldView.MenuInvalid);
                    break;
                }
                if (pressed("ps_circle"))
                {
                    Sound?.Invoke(FieldView.MenuSelect);
                    _elements[5 + chosen].Selected = true;
                    if (_files[chosen] == null) BeginSaving();
                    else
                    {
                        // YES and NO slide in from the left, NO chosen.
                        _confirm = 1;
                        Add(3, -126, 18, 126, Text(36)).Motion = Motion.FromLeft;
                        Add(4, -126, 34, 126, Text(37)).Motion = Motion.FromLeft;
                        _message = Text(33);
                        Enter(Stage.Overwrite);
                    }
                    break;
                }
                if (step < 0) { if (_slot == 0) { if (_page > 0) _page--; } else _slot--; }
                if (step > 0) { if (_slot == 2) { if (_page < 2) _page++; } else _slot++; }
                if (_page + _slot != chosen) Sound?.Invoke(FieldView.MenuChange);
                for (int i = 0; i < 5; i++)
                {
                    _elements[5 + i].Y = 72 + 40 * (i - _page);
                    _elements[5 + i].Selected = i == _page + _slot;
                }
                break;
            }
            case Stage.Overwrite:
                if (!Settled()) break;
                _elements[3].Selected = _confirm == 0;
                _elements[4].Selected = _confirm == 1;
                if (pressed("ps_circle") || pressed("ps_cross"))
                {
                    bool yes = pressed("ps_circle") && _confirm == 0;
                    Sound?.Invoke(yes ? FieldView.MenuSelect : FieldView.MenuLeave);
                    Slide(3, -126);
                    Slide(4, -126);
                    _elements[3].Motion = _elements[4].Motion = Motion.FromLeft;
                    if (yes) BeginSaving();
                    else { _message = Text(12); Enter(Stage.Files); }
                }
                else if (step != 0) { _confirm = 1 - _confirm; Sound?.Invoke(FieldView.MenuChange); }
                break;
            case Stage.Saving when !_saving:
                if (_stageFrames == LoadFrames)
                {
                    Loaded = _files[_page + _slot];
                    _message = Text(17);
                    _result = 1;
                    _dismissed = false;
                    Enter(Stage.Saved);
                }
                break;
            case Stage.Saving:
                if (_stageFrames == SaveFrames && (_view != null || SaveData != null))
                {
                    int chosen = _page + _slot;
                    bool saved = (_view != null ? _view.SaveGame(_card, chosen + 1, _location) : FieldView.WriteCardFile(_card, chosen + 1, _location, SaveData!)) == Error.Ok;
                    _files[chosen] = saved ? FieldView.ReadCardFile(_card, chosen + 1) : _files[chosen];
                    _message = Text(saved ? 16 : 10);
                    _result = saved ? 1 : -1;
                    Sound?.Invoke(saved ? FieldView.MenuFileDone : FieldView.MenuInvalid);
                    Enter(Stage.Saved);
                }
                break;
            case Stage.Saved:
                // The result stays for 150 frames, or until a button; the screens leave once the flash is over.
                if (pressed("ps_circle") || pressed("ps_cross") || pressed("ps_triangle") || pressed("ps_square")) _dismissed = true;
                if (_stageFrames > 16 && (_dismissed || _stageFrames >= 150))
                {
                    // A loaded game goes with the screen fading to black.
                    if (!_saving && _result > 0) { Enter(Stage.Fading); break; }
                    Visible = false;
                    return _result;
                }
                break;
            case Stage.Fading:
                // Darkening by 8 a frame, for 30 frames.
                _shade.Color = new Color(0, 0, 0, Math.Min(1, _stageFrames * 8 / 255f));
                if (_stageFrames > 30) { Visible = false; return _result; }
                break;
            case Stage.Back:
                if (!Settled()) break;
                _elements[1] = _elements[2] = null!;
                Enter(Stage.Opening);
                break;
            case Stage.Leaving:
                if (!Settled()) break;
                if (_fadeOut) { Enter(Stage.Fading); break; }
                Visible = false;
                return _result;
        }
        Animate();
        QueueRedraw();
        return 0;
    }

    private void BeginSaving()
    {
        _message = Text(14);
        _dismissed = false;
        Enter(Stage.Saving);
    }

    /// <summary>Moves each element a frame toward its target and steps its colours.</summary>
    private void Animate()
    {
        foreach (var e in _elements)
        {
            if (e == null) continue;
            e.Outer = e.Selected ? 8 : Math.Max(0, e.Outer - 1);
            // A file with nothing to load cannot be chosen, and stays dark.
            if (!_saving && e.File >= 0 && _files[e.File] == null) e.Outer = 0;
            if ((e.Inner & 7) != 0) e.Inner++;
            switch (e.Motion)
            {
                case Motion.AlongX when e.X < e.Target:
                    // Leaving to the right, 32 a frame.
                    e.X = Math.Min(e.Target, e.X + 32);
                    if (e.X == e.Target) e.Motion = Motion.Still;
                    break;
                case Motion.AlongX:
                {
                    // Arriving from the right through the stops, widening to the screen's edge.
                    int j = 1;
                    while (j < Stops.Length && e.Target + Stops[j] < e.X) j++;
                    e.X = e.Target + Stops[j - 1];
                    e.Width = Math.Max(e.Width, 320 - e.X);
                    if (e.X == e.Target) e.Motion = Motion.Still;
                    break;
                }
                case Motion.Up:
                {
                    int j = 1;
                    while (j < Stops.Length && e.Target + Stops[j] < e.Y) j++;
                    e.Y = e.Target + Stops[j - 1];
                    if (e.Y == e.Target) e.Motion = Motion.Still;
                    break;
                }
                case Motion.FromLeft when e.X < e.Target:
                {
                    int j = 1;
                    while (j < Stops.Length && e.X < -Stops[j]) j++;
                    e.X = -Stops[j - 1];
                    if (e.X == 0) e.Motion = Motion.Still;
                    break;
                }
                case Motion.FromLeft:
                    e.X = Math.Max(e.Target, e.X - 32);
                    if (e.X == e.Target) e.Motion = Motion.Still;
                    break;
            }
        }
    }

    /// <summary>A bar's colour at one end: between blue and teal by <paramref name="inner"/>, bright to dark by <paramref name="outer"/>.</summary>
    private static Color BarColour(int outer, int inner)
    {
        var bright = BlueBright.Lerp(TealBright, inner / 8f);
        var dark = BlueDark.Lerp(TealDark, inner / 8f);
        return bright.Lerp(dark, outer / 8f);
    }

    // The transform for a side of the window (0 left, 1 middle, 2 right), drawing in retail's units.
    private void Side(CanvasItem item, int side)
    {
        var (unit, offset) = FieldView.Pin(Size, side);
        item.DrawSetTransform(offset, 0, unit);
    }


    private void Print(string text, float x, float y, Color? colour = null)
    {
        if (_font == null || text.Length == 0) return;
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++) DrawString(_font, new Vector2(x, y + 12 + 13 * i), lines[i], HorizontalAlignment.Left, -1, 12, colour ?? Colors.White);
    }



    private void Digits(int value, int places, float x, float y)
    {
        for (int divisor = (int)Math.Pow(10, places - 1); divisor > 0; divisor /= 10, x += 5)
            FieldView.Sprite(this, _digits, new Rect2(value / divisor % 10 * 6, 0, 6, 10), new Vector2(x, y), Vector2.One);
    }

    public override void _Draw()
    {
        if (!Visible) return;
        _glow.QueueRedraw();
        if (_backdrop != null)
        {
            var (scale, centre) = FieldView.Pin(Size, 1);
            DrawRect(new Rect2(Vector2.Zero, Size), Colors.Black);
            DrawSetTransform(centre, 0, scale);
            DrawTextureRectRegion(_backdrop, new Rect2(64, 0, 192, 240), new Rect2(0, 0, 192, 240));
        }
        for (int id = 0; id < _elements.Length; id++)
        {
            if (_elements[id] is not { } e || e.Motion == Motion.Off) continue;
            // The files scroll within the three rows from y 72.
            if (e.File >= 0 && (e.Y < 72 || e.Y > 152)) continue;
            Side(this, e.File >= 0 ? 1 : id is 3 or 4 ? 0 : 2);
            FieldView.Band(this, new Vector2(e.X + 2, e.Y + 2), new Vector2(e.X + 2 + e.Width, e.Y + 2 + e.Height), new Color(0, 0, 0, 0.5f), new Color(0, 0, 0, 0.5f), Vector2.One);
            FieldView.Band(this, new Vector2(e.X, e.Y), new Vector2(e.X + e.Width, e.Y + e.Height), BarColour(8 - e.Outer, e.Inner), BarColour(e.Outer, e.Inner), Vector2.One);
            Print(e.Text, e.X + 6, e.Y);
            if (e.File >= 0) DrawFile(e);
        }

        // The message box: black from y 187 across the window, the three lines above it darkening into it.
        var (unit, middle) = FieldView.Pin(Size, 1);
        DrawSetTransform(new Vector2(0, middle.Y), 0, new Vector2(Size.X / 320, unit.Y));
        for (int i = 1; i < 4; i++) DrawRect(new Rect2(0, 187 - i, 320, 1), new Color(0, 0, 0, 1 - i * 0.25f));
        DrawRect(new Rect2(0, 187, 320, 60), Colors.Black);
        Side(this, 0);
        FieldView.Sprite(this, _labels, new Rect2(64, 145, 33, 7), new Vector2(10, 181), Vector2.One);
        // The cursor, pulsing brighter.
        if (CursorAt() is { } at)
        {
            int pulse = _frames & 15;
            float bright = 1 + (pulse switch { 0 => 0, 1 => 56, 2 => 112, _ => 128 - 8 * pulse }) / 128f;
            Side(this, _stage == Stage.Overwrite ? 0 : _stage == Stage.Slots ? 2 : 1);
            FieldView.Sprite(this, _cursor, new Rect2(32, 48, 16, 16), at, Vector2.One, new Color(bright, bright, bright));
        }
        Side(this, 0);
        Print(_message, 16, 192);
        DrawSetTransform(Vector2.Zero);
    }

    /// <summary>
    /// The saving sweep and the saved flash, added to what is under them: the sweep's pink rises over 48 columns to
    /// its front and falls over the 16 ahead; the flash lights the row with a soft edge above and below.
    /// </summary>
    private void DrawGlow()
    {
        if (!Visible || _page + _slot is var chosen && (chosen > 4 || _elements[5 + chosen] is not { } e)) return;
        float y = e.Y;
        if (y < 72 || y > 152) return;
        Side(_glow, 1);
        if (_stage == Stage.Saving)
        {
            float front = 64 + (320 + 16 - 64) * _stageFrames / (float)(_saving ? SaveFrames : LoadFrames);
            var pink = _saving ? Color.Color8(255, 0, 127) : Color.Color8(224, 255, 0);
            FieldView.Band(_glow, new Vector2(Math.Max(64, front - 48), y), new Vector2(front, y + 32), Colors.Black, pink, Vector2.One);
            FieldView.Band(_glow, new Vector2(front, y), new Vector2(front + 16, y + 32), pink, Colors.Black, Vector2.One);
        }
        else if (_stage == Stage.Saved && _stageFrames <= 16 && _result > 0)
        {
            int k = _stageFrames - 1;
            int c = k switch { 0 => 56, 1 => 112, _ => Math.Max(0, 120 - 8 * k) };
            var flash = _saving ? Color.Color8((byte)c, 0, (byte)(c / 2)) : Color.Color8((byte)(c - c / 8), (byte)c, 0);
            FieldView.Band(_glow, new Vector2(64, y - 8), new Vector2(320, y), Colors.Black, flash, Vector2.One, true);
            FieldView.Band(_glow, new Vector2(64, y), new Vector2(320, y + 32), flash, flash, Vector2.One, true);
            // The message box, drawn over the rows, covers what would fall below y 184.
            if (y + 32 < 184) FieldView.Band(_glow, new Vector2(64, y + 32), new Vector2(320, Math.Min(184, y + 40)), flash, flash.Lerp(Colors.Black, Math.Min(8, 184 - y - 32) / 8f), Vector2.One, true);
        }
        _glow.DrawSetTransform(Vector2.Zero);
    }

    private Vector2? CursorAt() => _stage switch
    {
        Stage.Slots => new Vector2(180, (_slot + 2) * 16 + 10),
        Stage.Files => new Vector2(24, _slot * 40 + 62),
        Stage.Overwrite when Settled() => new Vector2(0, _confirm * 16 + 10),
        _ => null,
    };

    /// <summary>
    /// A file's row: its picture to the left of the bar with NO. and its number above that, then the save's place,
    /// MAP%, SAVE and CLEAR counts on its first line and HP, MP and TIME on its second; an empty file says so. While
    /// saving, a pink sweep crosses the chosen row, then the row flashes.
    /// </summary>
    private void DrawFile(Element e)
    {
        var save = _files[e.File];
        float x = e.X, y = e.Y;
        // The picture: the save's location (its cell in the pictures), a container's chest, or NEW DATA.
        int location = save == null ? _saving ? -1 : -5 : save.GetValueOrDefault("location", 0).AsInt32() - 1;
        if (location >= 48) location = -3;
        var cell = location < 0 ? new Vector2(0, (~location) * 32) : new Vector2(64 * (1 + (location >> 3 & 1) + 2 * (location >> 4 & 3)), (location & 7) * 32);
        float shade = (128 - (8 - e.Outer) * 8) / 128f;
        FieldView.Sprite(this, _pictures, new Rect2(cell, new Vector2(64, 32)), new Vector2(x - 64, y), Vector2.One, new Color(shade, shade, shade));
        FieldView.Sprite(this, _digits, new Rect2(242, 0, 13, 8), new Vector2(x - 22, y), Vector2.One);
        Digits(e.File + 1, 2, x - 9, y);
        if (save == null) Print(Text(_saving ? 20 : 19), x + 6, y + 10, _saving ? null : Dim);
        else
        {
            Print(Text(save.GetValueOrDefault("location", 0).AsInt32() + 41), x + 6, y + 4);
            float line = y + 4;
            FieldView.Sprite(this, _words, new Rect2(224, 120, 17, 8), new Vector2(172, line), Vector2.One);
            FieldView.Sprite(this, _digits, new Rect2(248, 8, 3, 8), new Vector2(189, line), Vector2.One);
            int map = save.GetValueOrDefault("map_percent", 0).AsInt32();
            Digits(map, map == 100 ? 3 : 2, 192, line);
            FieldView.Sprite(this, _labels, new Rect2(170, 76, 10, 10), new Vector2(map == 100 ? 207 : 202, line - 1), Vector2.One);
            FieldView.Sprite(this, _words, new Rect2(128, 56, 22, 8), new Vector2(217, line), Vector2.One);
            FieldView.Sprite(this, _digits, new Rect2(248, 8, 3, 8), new Vector2(239, line), Vector2.One);
            Digits(save.GetValueOrDefault("save_count", 0).AsInt32(), 4, 242, line);
            FieldView.Sprite(this, _words, new Rect2(224, 112, 26, 8), new Vector2(267, line), Vector2.One);
            FieldView.Sprite(this, _digits, new Rect2(248, 8, 3, 8), new Vector2(293, line), Vector2.One);
            Digits(save.GetValueOrDefault("clear_count", 0).AsInt32(), 2, 296, line);
            line += 13;
            long seconds = save.GetValueOrDefault("frames", 0).AsInt64() / 60;
            int hours = (int)Math.Min(100, seconds / 3600);
            FieldView.Sprite(this, _words, new Rect2(104, 56, 19, 8), new Vector2(240, line), Vector2.One);
            Digits(hours, hours == 100 ? 3 : 2, hours == 100 ? 263 : 268, line);
            FieldView.Sprite(this, _digits, new Rect2(248, 8, 3, 8), new Vector2(279, line), Vector2.One);
            Digits((int)(seconds / 60 % 60), 2, 282, line);
            FieldView.Sprite(this, _digits, new Rect2(248, 8, 3, 8), new Vector2(293, line), Vector2.One);
            Digits((int)(seconds % 60), 2, 296, line);
            Gauge(88, line, 0, save.GetValueOrDefault("hp", 0).AsInt32(), save.GetValueOrDefault("max_hp", 0).AsInt32());
            Gauge(158, line, 1, save.GetValueOrDefault("mp", 0).AsInt32(), save.GetValueOrDefault("max_mp", 0).AsInt32());
        }
    }

    /// <summary>
    /// HP or MP: its word, the value in the large digits and the maximum in the small ones after a dot, over a bar
    /// filled in sixty-fourths, its far end the stat's colour and its near end yellow the fuller it is.
    /// </summary>
    private void Gauge(float x, float y, int stat, int value, int maximum)
    {
        int fill = maximum > 0 ? (value * 64 + maximum - 1) / maximum : 0;
        var colour = stat == 0 ? Color.Color8(45, 224, 207) : Color.Color8(220, 120, 159);
        var near = colour.Lerp(Color.Color8(255, 240, 158), fill / 64f);
        DrawRect(new Rect2(x, y + 8, 66, 5), Color.Color8(0, 40, 64));
        if (fill > 0) FieldView.Band(this, new Vector2(x + 1, y + 9), new Vector2(x + 1 + fill, y + 12), near, colour, Vector2.One);
        FieldView.Sprite(this, _labels, stat == 0 ? new Rect2(144, 0, 16, 9) : new Rect2(160, 0, 18, 9), new Vector2(x, y), Vector2.One);
        int valueDigits = value.ToString().Length, maximumDigits = maximum.ToString().Length;
        float at = x + 1 - valueDigits * 6 + 55 - maximumDigits * 5;
        foreach (char digit in value.ToString()) { FieldView.Sprite(this, _digits, new Rect2((digit - '0') * 8 + 64, 0, 7, 12), new Vector2(at, y - 1), Vector2.One); at += 6; }
        FieldView.Sprite(this, _digits, new Rect2(32, 9, 5, 7), new Vector2(at + 1, y), Vector2.One);
        at += 6;
        foreach (char digit in maximum.ToString()) { FieldView.Sprite(this, _digits, new Rect2((digit - '0') * 6, 0, 6, 10), new Vector2(at, y), Vector2.One); at += 5; }
    }
}
