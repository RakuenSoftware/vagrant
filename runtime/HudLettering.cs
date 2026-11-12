// SPDX-License-Identifier: AGPL-3.0-only
using Godot;

namespace Wol.Games.Vagrant.Runtime;

/// <summary>
/// The HUD's hand-lettered alphabet, which the HUD, the menus and the results screen print in: glyphs 10 pixels high
/// on the HUD's labels page (row 76 or 86 by the column's low bit), each its own width, capitals pulled together by
/// BATTLE.PRG's kerning table (the hud document's font: widths, columns, kerning).
/// </summary>
public sealed class HudLettering
{
    private readonly Texture2D _labels;
    private readonly int[] _widths, _columns, _kerning;

    public HudLettering(Texture2D labels, Godot.Collections.Dictionary font)
    {
        _labels = labels;
        _widths = font["widths"].AsInt32Array();
        _columns = font["columns"].AsInt32Array();
        _kerning = font["kerning"].AsInt32Array();
    }

    /// <summary>Prints a line at a place in screen pixels (each <paramref name="unit"/> wide and high); returns where it ends.</summary>
    public float Draw(CanvasItem canvas, string text, Vector2 at, Vector2 unit, Color? tint = null)
    {
        for (int k = 0; k < text.Length; k++)
        {
            int c = text[k] - 32;
            if (c < 0 || c >= _widths.Length) continue;
            int width = _widths[c];
            if (width == 0) continue;
            int column = _columns[c];
            var source = new Rect2(column & 0xFE, 76 + (column & 1) * 10, width, 10);
            canvas.DrawTextureRectRegion(_labels, new Rect2(at * unit, source.Size * unit), source, tint ?? Colors.White);
            at.X += width - Kerning(text, k);
        }
        return at.X;
    }

    /// <summary>A line's width in screen pixels, as it prints.</summary>
    public float Width(string text)
    {
        float width = 0;
        for (int k = 0; k < text.Length; k++)
        {
            int c = text[k] - 32;
            if (c < 0 || c >= _widths.Length || _widths[c] == 0) continue;
            width += _widths[c] - Kerning(text, k);
        }
        return width;
    }

    /// <summary>How far a capital pulls the next capital toward it.</summary>
    private int Kerning(string text, int k) =>
        k + 1 < text.Length && text[k] is >= 'A' and <= 'Z' && text[k + 1] is >= 'A' and <= 'Z' ? _kerning[(text[k] - 'A') * 26 + text[k + 1] - 'A'] : 0;
}
