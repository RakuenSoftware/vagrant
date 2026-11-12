// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The gadget bag window a chest opens (MENU/MENUB.PRG), as retail draws it on the 320 by 240 screen, scaled to the
/// window. Measured from retail footage:
/// <list type="bullet">
/// <item>The chest's contents down the left, a row every 16 lines from y 50: a bar 12 high to x 175 (from x 44 with its
/// icon at x 22 for equipment, from x 27 for items), the name in the system font, an item's count right-aligned to
/// x 167, and the menu's pointer at the chosen row.</item>
/// <item>"GADGET BAG" lettered at (176, 13), and under it the bag's categories (WEAPON, BLADE, GRIP, SHIELD, ARMOR, GEM,
/// MISC) at x 192, 12 lines apart from y 25, grey but for the chosen entry's in white, each with what is carried
/// over its limit ("1/ 8": the limit in a two-digit field) right-aligned to x 295, the chosen one's count blue.</item>
/// <item>The commands below: Circle GET and Square ALL at y 109, Cross END at y 125, each the label page's button icon
/// and its word.</item>
/// <item>INFORMATION at (8, 181) and the chosen entry's description in the system font from (15, 190), 13 lines apart.</item>
/// <item>An equipment icon carries its material's letter at its top right (see <see cref="ItemMenu.MaterialLetters"/>).</item>
/// </list>
/// </summary>
public sealed partial class GadgetBag : RetailScreen
{
    /// <summary>One thing in the chest: its name, its count (0 for equipment), its bag category and its icon cell (-1 none).</summary>
    public readonly record struct Entry(string Name, int Count, int Category, int Icon, string Help, int Material = 0);

    public static readonly int[] Limits = { 8, 16, 16, 8, 16, 48, 64 };
    private static readonly int[] CategoryTops = { 25, 37, 49, 61, 73, 85, 97 };
    private static readonly Color IconTint = new(0.42f, 1.25f, 1.2f), IconBox = Color.Color8(0x30, 0x9C, 0x98), Grey = new(0.55f, 0.55f, 0.55f), Chosen = Color.Color8(0x50, 0x70, 0xFF);

    private Texture2D? _labels, _cursor;
    private Font? _system;
    private Runtime.HudLettering? _lettering;
    private Godot.Collections.Dictionary? _words;
    private IReadOnlyList<Entry> _entries = Array.Empty<Entry>();
    private int[] _carried = new int[7];
    private int _chosen;

    public void Configure(Texture2D? labels, Texture2D? cursor, Runtime.HudLettering? lettering, Font? system, Godot.Collections.Dictionary? words)
    {
        _labels = labels;
        _cursor = cursor;
        _system = system;
        _words = words;
        _lettering = lettering;
    }

    /// <summary>Shows the chest's <paramref name="entries"/> with <paramref name="chosen"/> pointed at, and what the bag carries in each category.</summary>
    public void Set(IReadOnlyList<Entry> entries, int chosen, int[] carried)
    {
        _entries = entries;
        _chosen = chosen;
        _carried = carried;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_labels == null || _words == null || _lettering == null) return;
        // The chest's contents and the description keep to the window's left edge, the bag's panel to its right.
        var (unit, offset) = FieldView.Pin(Size, 0);
        void Side(int side) { offset = FieldView.Pin(Size, side).Offset; DrawSetTransform(offset); }
        void Text(string text, float x, float top, bool right = false)
        {
            if (_system == null) return;
            DrawSetTransform(offset, 0, unit);
            float w = right ? _system.GetStringSize(text, HorizontalAlignment.Left, -1, 12).X : 0;
            DrawString(_system, new Vector2(x - w, top + 12), text, HorizontalAlignment.Left, -1, 12);
            DrawSetTransform(offset, 0, Vector2.One);
        }

        // The contents.
        Side(0);
        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            float top = 50 + 16 * i;
            bool chosen = i == _chosen, equipment = entry.Count == 0;
            float left = equipment ? 44 : 27;
            var (from, to) = chosen ? (MenuList.ChosenLight, MenuList.ChosenDark) : (MenuList.BarLight, MenuList.BarDark);
            FieldView.Band(this, new Vector2(left, top), new Vector2(175, top + 12), from, to, unit);
            if (equipment)
            {
                // The label page's icon cell is grey with its picture dark; drawn tinted teal it is retail's icon box.
                if (entry.Icon >= 0) FieldView.Sprite(this, _labels, new Rect2(entry.Icon % 16 * 16, 98 + entry.Icon / 16 * 16, 16, 16), new Vector2(22, top - 2), unit, IconTint);
                else DrawRect(new Rect2(new Vector2(22, top - 2) * unit, new Vector2(16, 16) * unit), IconBox);
                if (entry.Material is >= 1 and <= 7) FieldView.Sprite(this, _labels, ItemMenu.MaterialLetters[entry.Material - 1], new Vector2(32, top), unit, ItemMenu.MaterialTint);
            }
            Text(entry.Name, left + 2, top);
            if (!equipment) Text(entry.Count.ToString(), 167, top, right: true);
        }
        if (_cursor != null && _chosen >= 0 && _chosen < _entries.Count) FieldView.Sprite(this, _cursor, MenuList.Pointer, new Vector2(7, 50 + 16 * _chosen - 4), unit);

        // The bag.
        Side(2);
        _lettering!.Draw(this, _words["title"].AsString(), new Vector2(176, 13), unit, Colors.White);
        var categories = _words["categories"].AsGodotArray();
        int chosenCategory = _chosen >= 0 && _chosen < _entries.Count ? _entries[_chosen].Category : -1;
        string slash = _words["slash"].AsString();
        for (int c = 0; c < categories.Count && c < Limits.Length; c++)
        {
            bool lit = c == chosenCategory;
            _lettering!.Draw(this, categories[c].AsString(), new Vector2(192, CategoryTops[c]), unit, lit ? Colors.White : Grey);
            // The limit takes a two-digit field after the slash ("1/ 8", "0/48"); the count stands right of the slash.
            string most = Limits[c].ToString(), count = _carried[c].ToString();
            float slashAt = 295 - 2 * _lettering!.Width("0") - _lettering!.Width(slash);
            _lettering!.Draw(this, most, new Vector2(295 - _lettering!.Width(most), CategoryTops[c]), unit, lit ? Colors.White : Grey);
            _lettering!.Draw(this, slash, new Vector2(slashAt, CategoryTops[c]), unit, lit ? Colors.White : Grey);
            _lettering!.Draw(this, count, new Vector2(slashAt - _lettering!.Width(count), CategoryTops[c]), unit, lit ? Chosen : Grey);
        }
        // The commands: the label page's button icons (triangle, square, circle, cross at x 0, 16, 32 and 48 of y 130).
        FieldView.Sprite(this, _labels, new Rect2(32, 130, 16, 16), new Vector2(175, 109), unit, Color.Color8(0xF0, 0x90, 0xB0));
        _lettering!.Draw(this, _words["get"].AsString(), new Vector2(190, 112), unit, Colors.White);
        FieldView.Sprite(this, _labels, new Rect2(16, 130, 16, 16), new Vector2(233, 109), unit, Color.Color8(0xC0, 0x90, 0xE0));
        _lettering!.Draw(this, _words["all"].AsString(), new Vector2(249, 112), unit, Colors.White);
        FieldView.Sprite(this, _labels, new Rect2(48, 130, 16, 16), new Vector2(175, 125), unit, Color.Color8(0x90, 0xB0, 0xD0));
        _lettering!.Draw(this, _words["end"].AsString(), new Vector2(190, 128), unit, Colors.White);

        // The chosen entry's description.
        Side(0);
        if (chosenCategory >= 0 || _entries.Count > 0)
        {
            FieldView.Sprite(this, _labels, MenuList.InformationWord, new Vector2(8, 181), unit);
            string help = _chosen >= 0 && _chosen < _entries.Count ? _entries[_chosen].Help : "";
            var lines = help.Split('\n');
            for (int i = 0; i < lines.Length && i < 3; i++) Text(lines[i], 15, 190 + 13 * i);
        }
        DrawSetTransform(Vector2.Zero);
    }
}
