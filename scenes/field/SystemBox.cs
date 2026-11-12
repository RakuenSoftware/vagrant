// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The field's system box (text box 7, type 4; 0x800CCF08 lays a type-4 box out) on the 320 by 240 screen, scaled to
/// the window: no frame, 24 columns from x 16, its first line's top at 242 less 13 a line for the lines it opens with,
/// and each line centred across it (fb 04). A scene message (0x800CB2B8, flags 0x304: the field's messages, under
/// CHECK) and a script's type-4 box open with three lines, from 203; the information box (initInformationTextBox: the
/// attack readout, and "No target in range!" under CAUTION) with four, from 190. Either stands in a black band across
/// the bottom of the screen from three lines above its first line, with three lines darkening into it, over the HUD
/// (retail footage). It prints in the system font (the upright set, white).
/// </summary>
public sealed partial class SystemBox : RetailScreen
{
    private const int Left = 16, Columns = 24, Cell = 12, LineHeight = 13;

    private Font? _font;
    private Texture2D? _labels;
    private string[] _lines = Array.Empty<string>();

    /// <summary>The lines the box opens with: 3 for a scene message (from 203), 4 for the information box (from 190).</summary>
    public int Lines { get; set; } = 3;

    /// <summary>
    /// The word above the box (the type-4 box's second flags byte): 0 none, 1 CAUTION, 2 INFORMATION, 3 CHECK. It
    /// stands 9 lines above the box; with it one line of text starts 7 lines lower, more from the box's top (footage).
    /// </summary>
    public int Header { get; set; }

    /// <summary>The header words on the HUD's labels page, by header.</summary>
    private static readonly Rect2[] Headers = { default, new(64, 145, 33, 7), new(93, 151, 52, 7), new(64, 153, 27, 7) };

    /// <summary>Lays the box out as a type-4 box opened with <paramref name="lines"/> lines and <paramref name="header"/>.</summary>
    /// <summary>Opens the box with <paramref name="lines"/> under <paramref name="header"/>; its lines are centred unless <paramref name="centred"/> is false (MENU7's question, left-aligned from x 16 in footage).</summary>
    public void Open(int lines, int header, bool centred = true)
    {
        Centred = centred;
        Lines = Math.Clamp(lines, 1, 4);
        Header = header is >= 0 and <= 3 ? header : 0;
        QueueRedraw();
    }

    /// <summary>Whether the lines are centred across the box (the field's messages) or start at x 16.</summary>
    public bool Centred { get; private set; } = true;

    public void Configure(Font? font, Texture2D? labels = null)
    {
        _font = font;
        _labels = labels;
    }

    /// <summary>The text shown, a line to each newline; empty clears the box.</summary>
    public string Text
    {
        get => string.Join('\n', _lines);
        set
        {
            var lines = value.Length == 0 ? Array.Empty<string>() : value.Split('\n');
            if (string.Join('\n', lines) == string.Join('\n', _lines)) return;
            _lines = lines;
            // The band covers the HUD beneath it.
            if (lines.Length > 0) GetParent()?.MoveChild(this, -1);
            QueueRedraw();
        }
    }

    /// <summary>Entry <paramref name="index"/> of a converted string table, its places filled with <paramref name="strings"/>; empty where the table has none.</summary>
    public static string Entry(Godot.Collections.Array table, int index, params string[] strings) =>
        index >= 0 && index < table.Count ? Fill(table[index].AsGodotDictionary(), strings) : "";

    /// <summary>
    /// A retail string (an installed entry: its text and tokens) with what the game fills in: its string tokens (ff n)
    /// from <paramref name="strings"/> and its number tokens (fe n) from <paramref name="numbers"/>, by slot.
    /// </summary>
    public static string Fill(Godot.Collections.Dictionary entry, string[]? strings = null, int[]? numbers = null)
    {
        string text = entry["text"].AsString();
        var tokens = entry.GetValueOrDefault("tokens", new Godot.Collections.Array()).AsGodotArray();
        // From the last place back, so the earlier places stand.
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i].AsGodotDictionary();
            int at = Math.Clamp(token["offset"].AsInt32(), 0, text.Length), slot = token.GetValueOrDefault("slot", 0).AsInt32();
            string kind = token["type"].AsString();
            string fill = kind == "string" ? (strings != null && slot < strings.Length ? strings[slot] : "") : kind == "number" ? (numbers != null && slot < numbers.Length ? numbers[slot].ToString() : "") : "";
            text = text.Insert(at, fill);
        }
        return text;
    }

    public override void _Draw()
    {
        if (_font == null || _lines.Length == 0) return;
        var (unit, offset) = FieldView.Pin(Size, 1);
        int boxTop = 242 - LineHeight * Lines, band = boxTop - 3;
        // The band runs the window's width; CAUTION keeps to its left edge.
        DrawSetTransform(new Vector2(0, offset.Y), 0, new Vector2(Size.X / 320, unit.Y));
        for (int i = 1; i < 4; i++) DrawRect(new Rect2(0, band - i, 320, 1), new Color(0, 0, 0, 1 - i * 0.25f));
        DrawRect(new Rect2(0, band, 320, 240 - band + 8), Colors.Black);
        if (Header != 0 && _labels != null)
        {
            DrawSetTransform(FieldView.Pin(Size, 0).Offset, 0, unit);
            var word = Headers[Header];
            DrawTextureRectRegion(_labels, new Rect2(new Vector2(10, boxTop - 9), word.Size), word);
        }
        // The box keeps to the middle of the window.
        DrawSetTransform(offset, 0, unit);
        // Under a word, one line of text stands half a line low and two or more start at the box's first line
        // (Ep. 3: "No target in range!" 18 below CAUTION; a reclaim's two lines and the menus' help 11 below theirs).
        float centre = Left + Columns * Cell / 2f, top = boxTop + (Header != 0 && _lines.Length == 1 ? 7 : 0);
        for (int i = 0; i < _lines.Length; i++)
        {
            float width = _font.GetStringSize(_lines[i], HorizontalAlignment.Left, -1, Cell).X;
            // A glyph's cell stands on the baseline its ascent (the whole 12-pixel cell) below the line's top.
            DrawString(_font, new Vector2(Centred ? Mathf.Floor(centre - width / 2) : 16, top + LineHeight * i + Cell), _lines[i], HorizontalAlignment.Left, -1, Cell);
        }
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
