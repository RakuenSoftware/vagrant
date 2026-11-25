// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace Wol.Games.Vagrant.Title;

/// <summary>The retail title wheel and its palette/motion rules, advanced at 30 Hz by TitleFlow.</summary>
public sealed class TitleMenuState
{
    public const int NewGame = 0, Continue = 1, Vibration = 2, Sound = 3, Mono = 4, VibrationOff = 5, VibrationOn = 6, Stereo = 7;
    /// <summary>The added Settings word, composed at install time from the disc's menu lettering.</summary>
    public const int Settings = 10;
    /// <summary>Words on the rotating wheel, in order. The retail wheel has the first four; Settings follows Sound.</summary>
    public static readonly int[] Wheel = { NewGame, Continue, Vibration, Sound, Settings };
    /// <summary>Retail palette table entries: menu words dark and lit, glow blue and gold.</summary>
    public const int Dark = 0, Lit = 1, GlowBlue = 2, Gold = 3;
    public const int Selected = 64;

    public enum Motion { Resting, Rising, Falling, Opened }

    /// <summary>One menu word. Byte-sized fields wrap as the retail eight-bit counters do.</summary>
    public sealed class Word
    {
        public bool Enabled;
        public Motion State;
        public int Brightness;
        public int Glow;
        public int Row;
        public int Target;
        public int GlowRow;
        public int Warmth;
        public readonly ushort[] Palette = new ushort[16];
        public int PaletteVersion;
    }

    private readonly Word[] _words = new Word[11];
    private readonly ushort[][] _palettes;
    public Word this[int index] => _words[index];
    public int Count => _words.Length;

    public TitleMenuState(ushort[][] palettes)
    {
        _palettes = palettes;
        for (int i = 0; i < _words.Length; i++) _words[i] = new Word();
        Reset();
    }

    /// <summary>Disables every word and restores the dark palette the title uploads for rows 384..391.</summary>
    public void Reset()
    {
        for (int i = 0; i < _words.Length; i++)
        {
            _words[i].Enabled = false;
            Array.Copy(_palettes[Dark], _words[i].Palette, 16);
            _words[i].PaletteVersion++;
        }
    }

    /// <summary>Starts a word fading in at <paramref name="row"/>.</summary>
    public void Show(int index, int row)
    {
        var word = _words[index];
        word.Enabled = true;
        word.State = Motion.Resting;
        word.Brightness = 0;
        word.Glow = 0;
        word.Row = row & 0xFF;
        word.Target = row & 0xFF;
        word.GlowRow = row & 0xFF;
        word.Warmth = 0;
    }

    /// <summary>Blends a word's palette from one retail palette toward another, in sixteenths.</summary>
    public void Recolour(int index, int sixteenths, int from, int to)
    {
        var palette = _words[index].Palette;
        ushort[] a = _palettes[from], b = _palettes[to];
        palette[0] = 0;
        for (int i = 1; i < 16; i++)
        {
            int Channel(int shift)
            {
                int start = (a[i] >> shift) & 31, end = (b[i] >> shift) & 31;
                return ((start * 16 + (end - start) * sixteenths) >> 4) & 31;
            }
            palette[i] = (ushort)(Channel(0) | (Channel(5) << 5) | (Channel(10) << 10) | 0x8000);
        }
        _words[index].PaletteVersion++;
    }

    /// <summary>Advances brightness, motion, warmth and glow once.</summary>
    public void Step()
    {
        for (int i = 0; i < _words.Length; i++)
        {
            var word = _words[i];
            if (!word.Enabled) continue;
            word.Brightness = Math.Min(word.Brightness + 8, 128);
            switch (word.State)
            {
                case Motion.Resting:
                    if (word.Brightness != 128 || word.Row != Selected) break;
                    word.GlowRow = word.Row;
                    if (word.Warmth < 16)
                    {
                        word.Glow = (word.Warmth * 16) & 0xFF;
                        word.Warmth = Math.Min(word.Warmth + 4, 16);
                    }
                    break;
                case Motion.Rising:
                    word.Row = (word.Row - 4) & 0xFF;
                    if (word.Row == word.Target) word.State = Motion.Resting;
                    break;
                case Motion.Falling:
                    word.Row = (word.Row + 4) & 0xFF;
                    if (word.Row == word.Target) word.State = Motion.Resting;
                    break;
            }
            if (word.State != Motion.Opened) Recolour(i, word.Warmth, Dark, Lit);
            if (word.Warmth != 0 && word.Row != Selected) word.Warmth--;
            if (word.Glow != 0)
            {
                if (word.GlowRow != word.Row)
                {
                    word.Glow = (word.Glow - 16) & 0xFF;
                    word.GlowRow = (word.GlowRow + (word.GlowRow < word.Row ? 2 : -2)) & 0xFF;
                }
                else if (word.Warmth == 16 && word.Glow > 128)
                {
                    word.Glow = (word.Glow - 16) & 0xFF;
                }
            }
        }
    }

}
