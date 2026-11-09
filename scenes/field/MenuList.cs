// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The field menu's lists as retail draws them on the 320 by 240 screen, scaled to the window: a title word in the
/// top right ("COMMAND" for the commands, "MENU" in a submenu), a bar for each entry from x 200 to the screen's edge
/// every 16 lines from y 15 (12 high; dark blue on the left to blue on the right, the chosen one bright on the left
/// and darkening) with its name in the system font from x 202, the pointer just left of the chosen bar, and in a submenu its
/// command as a teal header in the top row from x 181. Below, the INFORMATION label and the chosen entry's help in the
/// system font, left-aligned from x 16 on the system box's four lines (the first at y 190).
/// </summary>
public sealed partial class MenuList : Control
{
    internal static readonly Color BarDark = Color.Color8(0x00, 0x02, 0x2E), BarLight = Color.Color8(0x00, 0x32, 0x61);
    internal static readonly Color ChosenLight = Color.Color8(0x08, 0x34, 0x5C), ChosenDark = Color.Color8(0x00, 0x03, 0x2E);
    internal static readonly Color HeaderLight = Color.Color8(0x06, 0x51, 0x50), HeaderDark = Color.Color8(0x00, 0x1D, 0x22);
    // The label page's words: COMMAND, MENU and INFORMATION; the cursor page's pointer.
    private static readonly Rect2 CommandWord = new(1, 65, 60, 8), MenuWord = new(65, 65, 33, 8), InformationWord = new(93, 151, 52, 7), Pointer = new(31, 44, 17, 20);

    private Texture2D? _labels, _cursor;
    private Font? _system;
    private IReadOnlyList<string> _entries = Array.Empty<string>();
    private string? _header;
    private string _help = "";
    private IReadOnlyList<int>? _icons;
    private bool _headerDimmed;
    private int _chosen;
    private (string Left, string Right) _note = ("", "");
    private (float Left, float Top)? _prompt;
    private bool _titled = true;
    private (string Name, int Icon)? _sub;
    private IReadOnlyList<int>? _kinds;
    private (string Label, string Value)? _cost;
    // A greyed entry's name, the olive bar of a further list, a dimmed header's name, and the cost box (MENU1.PRG, measured from footage).
    public static readonly Color Greyed = Color.Color8(0x68, 0x6C, 0xB0);

    /// <summary>The tint that turns a category icon's grey box retail's light teal.</summary>
    private static readonly Color IconTint = new(0.92f, 2.8f, 2.6f);
    private static readonly Color OliveDark = Color.Color8(0x08, 0x07, 0x05), OliveLight = Color.Color8(0x36, 0x2C, 0x1B), Dimmed = Color.Color8(0x70, 0x90, 0x90), CostDark = Color.Color8(0x04, 0x03, 0x1B), CostLight = Color.Color8(0x36, 0x25, 0x5D);
    private int[] _widths = Array.Empty<int>(), _columns = Array.Empty<int>(), _kerning = Array.Empty<int>();

    public void Configure(Texture2D? labels, Texture2D? cursor, Font? system, Godot.Collections.Dictionary? font = null)
    {
        _labels = labels;
        _cursor = cursor;
        _system = system;
        if (font != null)
        {
            _widths = font["widths"].AsInt32Array();
            _columns = font["columns"].AsInt32Array();
            _kerning = font["kerning"].AsInt32Array();
        }
        MouseFilter = MouseFilterEnum.Ignore;
        AnchorRight = 1;
        AnchorBottom = 1;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    /// <summary>
    /// Shows <paramref name="entries"/> with <paramref name="chosen"/> pointed at, under the command
    /// <paramref name="header"/> in a submenu (null for the commands), and the chosen entry's <paramref name="help"/>.
    /// </summary>
    /// <param name="note">A line under the entries in the HUD's lettering: its left part from x 206 and its right part ending at x 302 (the Battle Abilities page's NEXT and its points, as MENU2.PRG prints them).</param>
    /// <param name="prompt">Draws the entries alone as a question's answers over the field, rows from this left edge and top, with no title (the container's YES and NO).</param>
    /// <param name="titled">Whether COMMAND or MENU stands at the top right (not over a reclaim, MENU2.PRG's chooser).</param>
    /// <param name="sub">A second header under the first, the level now chosen (a Break Arts category: its name and its
    /// label-page icon cell, -1 for none); the first header then stands dimmed, and the entries a step further right.</param>
    /// <param name="kinds">Each entry's style: 0 plain, 1 greyed (not usable now), 2 on an olive bar (a further list), 3 yellow (the one in hand).</param>
    /// <param name="icons">Each entry's label-page icon cell, drawn in a box at the row's left before its bar (View Arts' categories), or none.</param>
    /// <param name="headerDimmed">Whether the header's name is dimmed: by default under a second bar; the Break Arts pages dim it out of battle mode only.</param>
    /// <param name="cost">A cost box at the bottom left, its label and value (a Break Art's HP, MENU1.PRG).</param>
    public void Set(IReadOnlyList<string> entries, int chosen, string? header = null, string help = "", (string Left, string Right)? note = null, (float Left, float Top)? prompt = null, bool titled = true,
        (string Name, int Icon)? sub = null, IReadOnlyList<int>? kinds = null, (string Label, string Value)? cost = null,
        IReadOnlyList<int>? icons = null, bool? headerDimmed = null)
    {
        _titled = titled;
        _icons = icons;
        _headerDimmed = headerDimmed ?? sub != null;
        _sub = sub;
        _kinds = kinds;
        _cost = cost;
        _prompt = prompt;
        _entries = entries;
        _chosen = chosen;
        _header = header;
        _help = help;
        _note = note ?? ("", "");
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_labels == null) return;
        // The list keeps to the window's right edge, the help to its left.
        var (unit, offset) = FieldView.Pin(Size, 2);
        DrawSetTransform(offset);
        void Sprite(Texture2D texture, Rect2 source, Vector2 at) => DrawTextureRectRegion(texture, new Rect2(at * unit, source.Size * unit), source);
        void Bar(int row, float left, Color from, Color to)
        {
            float top = 18 + 16 * row;
            DrawPolygon(new[] { new Vector2(left, top) * unit, new Vector2(320, top) * unit, new Vector2(320, top + 12) * unit, new Vector2(left, top + 12) * unit }, new[] { from, to, to, from });
        }
        if (_prompt is { } at)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                bool lit = i == _chosen;
                float top = at.Top + 16 * i;
                DrawPolygon(new[] { new Vector2(at.Left, top) * unit, new Vector2(320, top) * unit, new Vector2(320, top + 12) * unit, new Vector2(at.Left, top + 12) * unit },
                    new[] { lit ? ChosenLight : BarDark, lit ? ChosenDark : BarLight, lit ? ChosenDark : BarLight, lit ? ChosenLight : BarDark });
                if (_system != null)
                {
                    DrawSetTransform(offset, 0, unit);
                    DrawString(_system, new Vector2(at.Left + 6, top + 1 + 12), _entries[i], HorizontalAlignment.Left, -1, 12);
                    DrawSetTransform(offset, 0, Vector2.One);
                }
            }
            if (_cursor != null && _chosen >= 0 && _chosen < _entries.Count) Sprite(_cursor, Pointer, new Vector2(at.Left - 14, at.Top + 16 * _chosen - 10));
            DrawSetTransform(Vector2.Zero);
            return;
        }
        var title = _header == null ? CommandWord : MenuWord;
        if (_titled) Sprite(_labels, title, new Vector2(313 - title.Size.X, 6));
        int first = 0;
        // The names print in the system font (its six-pixel upright set), a cell's top a line inside its bar.
        void Name(string text, float x, int row, Color? colour = null)
        {
            if (_system == null) return;
            DrawSetTransform(offset, 0, unit);
            DrawString(_system, new Vector2(x, 18 + 16 * row + 1 + 12), text, HorizontalAlignment.Left, -1, 12, colour ?? Colors.White);
            DrawSetTransform(offset, 0, Vector2.One);
        }
        if (_header != null)
        {
            // Under a second header the first stands dimmed, its bar turned (dark on the left).
            if (_sub == null) Bar(0, 181, HeaderLight, HeaderDark);
            else Bar(0, 181, HeaderDark, HeaderLight);
            // Under a second bar the header's name starts further left (Ep. 3's Break Arts pages: x 186).
            Name(_header, _sub == null ? 191 : 186, 0, _headerDimmed ? Dimmed : null);
            first = 1;
        }
        float left = 200;
        if (_sub is { } sub)
        {
            Bar(1, 181, HeaderLight, HeaderDark);
            // The category's icon (a grey box with the weapon dark on it), tinted to retail's light teal (footage: the box's
            // grey 66 shows (61, 185, 171)).
            if (sub.Icon >= 0) DrawTextureRectRegion(_labels, new Rect2(new Vector2(181, 18 + 16 - 2) * unit, new Vector2(16, 16) * unit), new Rect2(sub.Icon % 16 * 16, 98 + sub.Icon / 16 * 16, 16, 16), IconTint);
            Name(sub.Name, 202, 1);
            first = 2;
            left = 195;
        }
        for (int i = 0; i < _entries.Count; i++)
        {
            bool chosen = i == _chosen;
            int kind = _kinds != null && i < _kinds.Count ? _kinds[i] : 0;
            int icon = _icons != null && i < _icons.Count ? _icons[i] : -1;
            // An entry with an icon has it in a box 16 wide at the row's left, light teal when chosen and half as bright
            // otherwise, its bar after it and its name 24 in (Ep. 3 7:14, View Arts: boxes from 195, names from 219).
            float barLeft = icon >= 0 ? left + 16 : left, nameLeft = icon >= 0 ? left + 24 : left + 2 + (_sub != null ? 2 : 0);
            if (icon >= 0)
                DrawTextureRectRegion(_labels, new Rect2(new Vector2(left, 18 + 16 * (first + i) - 2) * unit, new Vector2(16, 16) * unit), new Rect2(icon % 16 * 16, 98 + icon / 16 * 16, 16, 16), chosen ? IconTint : IconTint * new Color(0.48f, 0.48f, 0.48f));
            if (kind == 2) Bar(first + i, barLeft, chosen ? OliveLight : OliveDark, chosen ? OliveDark : OliveLight);
            else Bar(first + i, barLeft, chosen ? ChosenLight : BarDark, chosen ? ChosenDark : BarLight);
            Name(_entries[i], nameLeft, first + i, kind == 1 ? Greyed : kind == 3 ? ItemMenu.ChosenName : null);
        }
        if (_cursor != null && _chosen >= 0 && _chosen < _entries.Count) Sprite(_cursor, Pointer, new Vector2(left - 20, 18 + 16 * (first + _chosen) - 7));
        if (_cost is { } cost && _widths.Length > 0)
        {
            // MENU1.PRG's cost box: a bar from the window's left edge, the label at 6 and the value ending at 72.
            var left0 = FieldView.Pin(Size, 0).Offset;
            DrawSetTransform(left0);
            DrawPolygon(new[] { new Vector2(0, 164) * unit, new Vector2(78, 164) * unit, new Vector2(78, 176) * unit, new Vector2(0, 176) * unit }, new[] { CostDark, CostLight, CostLight, CostDark });
            BattleHud.Letter(this, _labels, _widths, _columns, _kerning, cost.Label, new Vector2(6, 165), unit);
            float width = BattleHud.Letter(this, _labels, _widths, _columns, _kerning, cost.Value, new Vector2(-1000, 0), unit) + 1000;
            BattleHud.Letter(this, _labels, _widths, _columns, _kerning, cost.Value, new Vector2(72 - width, 165), unit);
            DrawSetTransform(offset);
        }
        if (_note.Left.Length + _note.Right.Length > 0 && _widths.Length > 0)
        {
            float y = 18 + 16 * (first + _entries.Count);
            BattleHud.Letter(this, _labels, _widths, _columns, _kerning, _note.Left, new Vector2(206, y), unit);
            float width = BattleHud.Letter(this, _labels, _widths, _columns, _kerning, _note.Right, new Vector2(-1000, 0), unit) + 1000;
            BattleHud.Letter(this, _labels, _widths, _columns, _kerning, _note.Right, new Vector2(302 - width, y), unit);
        }
        if (_help.Length > 0)
        {
            offset = FieldView.Pin(Size, 0).Offset;
            DrawSetTransform(offset);
            Sprite(_labels, InformationWord, new Vector2(10, 182));
            if (_system != null)
            {
                DrawSetTransform(offset, 0, unit);
                var lines = _help.Split('\n');
                for (int i = 0; i < lines.Length && i < 4; i++) DrawString(_system, new Vector2(16, 190 + 13 * i + 12), lines[i], HorizontalAlignment.Left, -1, 12);
            }
        }
        DrawSetTransform(Vector2.Zero);
    }
}
