// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Scenes.Title;

/// <summary>
/// The retail title's rotating word wheel, drawn natively in the 512x448 title display.
/// Words occupy 32-row slots on a 128-row drum (0 and 128 are hidden; 32 above, 64 selected, 96 below).
/// Brightness ramps by absolute drum row, the selected word warms from its dark to its lit palette,
/// and a soft glow is added behind it. All state advances once per menu update (30 Hz).
/// </summary>
public partial class TitleMenu : Node2D
{
    public const int NewGame = 0, Continue = 1, Vibration = 2, Sound = 3, Mono = 4, VibrationOff = 5, VibrationOn = 6, Stereo = 7;
    /// <summary>The added Settings word, composed at install time from the disc's menu lettering.</summary>
    public const int Settings = 10;
    /// <summary>Words on the rotating wheel, in order. The retail wheel has the first four; Settings follows Sound.</summary>
    public static readonly int[] Wheel = { NewGame, Continue, Vibration, Sound, Settings };
    /// <summary>Retail palette table entries: menu words dark and lit, glow blue and gold.</summary>
    public const int Dark = 0, Lit = 1, GlowBlue = 2, Gold = 3;
    public const int Selected = 64;
    /// <summary>The title's draw area starts 16 rows above its interlaced display.</summary>
    public const int DisplayTop = 16;
    public static readonly Rect2I Panel = new(176, 256 - DisplayTop, 160, 128);

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
        public ImageTexture? Texture;
        public bool Recoloured = true;
    }

    public enum Layer { Glow, Opaque, Additive }

    private readonly Word[] _words = new Word[TitleMenuWords];
    private const int TitleMenuWords = 11;
    private RetailContent _content = null!;
    private readonly Node2D[] _layers = new Node2D[3];

    public Word this[int index] => _words[index];

    public void Bind(RetailContent content)
    {
        _content = content;
        for (int i = 0; i < _words.Length; i++) _words[i] = new Word();
        foreach (Layer layer in Enum.GetValues<Layer>())
        {
            var node = new TitleMenuLayer { Menu = this, Kind = layer, Name = layer.ToString() };
            if (layer != Layer.Opaque) node.Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
            AddChild(node);
            _layers[(int)layer] = node;
        }
        Reset();
    }

    /// <summary>Disables every word and restores the dark palette the title uploads for rows 384..391.</summary>
    public void Reset()
    {
        for (int i = 0; i < _words.Length; i++)
        {
            _words[i].Enabled = false;
            Array.Copy(_content.TitlePalettes[Dark], _words[i].Palette, 16);
            _words[i].Recoloured = true;
        }
        Redraw();
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
        ushort[] a = _content.TitlePalettes[from], b = _content.TitlePalettes[to];
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
        _words[index].Recoloured = true;
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
        Redraw();
    }

    public void Redraw()
    {
        foreach (var layer in _layers) layer?.QueueRedraw();
    }

    private ImageTexture Texture(int index)
    {
        var word = _words[index];
        if (word.Texture != null && !word.Recoloured) return word.Texture;
        Image indices = _content.TitleWords[index];
        var image = Image.CreateEmpty(indices.GetWidth(), indices.GetHeight(), false, Image.Format.Rgba8);
        for (int y = 0; y < indices.GetHeight(); y++)
            for (int x = 0; x < indices.GetWidth(); x++)
            {
                int texel = (int)Math.Round(indices.GetPixel(x, y).R * 255f);
                ushort colour = word.Palette[texel];
                image.SetPixel(x, y, texel == 0 || colour == 0 ? new Color(0, 0, 0, 0) : Wol.Engine.Display.DisplaySpace.FromRgba32(Installation.TitleConversion.ToColor(colour, false)));
            }
        if (word.Texture == null) word.Texture = ImageTexture.CreateFromImage(image);
        else word.Texture.Update(image);
        word.Recoloured = false;
        return word.Texture;
    }

    /// <summary>Draws one blend layer. The retail order is all glows, then all words, highest entry first.</summary>
    public void DrawLayer(CanvasItem canvas, Layer layer)
    {
        if (_content == null) return;
        for (int i = _words.Length - 1; i >= 0; i--)
        {
            var word = _words[i];
            if (!word.Enabled) continue;
            if (layer == Layer.Glow)
            {
                if (word.Glow == 0) continue;
                Texture2D glow = _content.TitleGlows[i];
                Vector2I origin = _content.TitleGlowOrigins[i];
                var place = word.State == Motion.Opened
                    ? new Vector2(origin.X + 64, origin.Y + word.GlowRow - DisplayTop)
                    : new Vector2(origin.X + word.GlowRow, origin.Y + word.GlowRow - DisplayTop);
                var target = new Rect2(place, glow.GetSize());
                // The retail title repaints only its menu panel each frame; glow is confined to that panel.
                var clipped = target.Intersection(new Rect2(Panel.Position, Panel.Size));
                if (clipped.Size.X <= 0 || clipped.Size.Y <= 0) continue;
                float scale = word.Glow / 128f;
                canvas.DrawTextureRectRegion(glow, clipped, new Rect2(clipped.Position - place, clipped.Size), new Color(scale, scale, scale));
                continue;
            }
            int x = word.State == Motion.Opened ? 192 : word.Row + 128, source = 0, width = 128;
            if (x < 176)
            {
                source = 176 - x;
                width = x - 48;
                x = 176;
            }
            if (x + width > 336) width = 336 - x;
            if (width <= 0) continue;
            ImageTexture texture = Texture(i);
            for (int j = 0; j < 32; j++)
            {
                int row = j + word.Row;
                int band = (row >> 5) & 3;
                if (band == 0) continue;
                bool opaque;
                int level;
                if (word.State == Motion.Opened || band == 2)
                {
                    opaque = word.Brightness == 128;
                    level = word.Brightness;
                }
                else
                {
                    opaque = false;
                    level = band == 1 ? (word.Brightness * (row - 32)) >> 5 : (word.Brightness * (127 - row)) >> 5;
                }
                if ((layer == Layer.Opaque) != opaque) continue;
                float scale = opaque ? 1f : level / 128f;
                canvas.DrawTextureRectRegion(texture, new Rect2(x, row + 240 - DisplayTop, width, 1), new Rect2(source, j, width, 1), new Color(scale, scale, scale));
            }
        }
    }
}

/// <summary>One blend layer of the title menu; the menu owns what it draws.</summary>
public partial class TitleMenuLayer : Node2D
{
    public TitleMenu Menu = null!;
    public TitleMenu.Layer Kind;

    public override void _Draw() => Menu.DrawLayer(this, Kind);
}
