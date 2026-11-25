// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Title;
using static Wol.Games.Vagrant.Title.TitleMenuState;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Scenes.Title;

/// <summary>
/// The retail title's rotating word wheel, drawn natively in the 512x448 title display.
/// Words occupy 32-row slots on a 128-row drum (0 and 128 are hidden; 32 above, 64 selected, 96 below).
/// Brightness ramps by absolute drum row, the selected word warms from its dark to its lit palette,
/// and a soft glow is added behind it. TitleMenuState owns the counters and palettes; this node owns only drawing resources.
/// </summary>
public partial class TitleMenu : Node2D
{
    public const int DisplayTop = 16;
    public static readonly Rect2I Panel = new(176, 256 - DisplayTop, 160, 128);
    public enum Layer { Glow, Opaque, Additive }
    private TitleMenuState _state = null!;
    private RetailContent _content = null!;
    private readonly Node2D[] _layers = new Node2D[3];
    private readonly ImageTexture?[] _textures = new ImageTexture?[11];
    private readonly int[] _paletteVersions = new int[11];

    public void Bind(RetailContent content, TitleMenuState state)
    {
        _content = content;
        _state = state;
        foreach (Layer layer in Enum.GetValues<Layer>())
        {
            var node = new TitleMenuLayer { Menu = this, Kind = layer, Name = layer.ToString() };
            if (layer != Layer.Opaque) node.Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
            AddChild(node);
            _layers[(int)layer] = node;
        }
        Redraw();
    }

    public void Redraw()
    {
        foreach (var layer in _layers) layer?.QueueRedraw();
    }

    private ImageTexture Texture(int index)
    {
        var word = _state[index];
        var texture = _textures[index];
        if (texture != null && _paletteVersions[index] == word.PaletteVersion) return texture;
        Image indices = _content.TitleWords[index];
        var image = Image.CreateEmpty(indices.GetWidth(), indices.GetHeight(), false, Image.Format.Rgba8);
        for (int y = 0; y < indices.GetHeight(); y++)
            for (int x = 0; x < indices.GetWidth(); x++)
            {
                int texel = (int)Math.Round(indices.GetPixel(x, y).R * 255f);
                ushort colour = word.Palette[texel];
                image.SetPixel(x, y, texel == 0 || colour == 0 ? new Color(0, 0, 0, 0) : Wol.Engine.Display.DisplaySpace.FromRgba32(Installation.TitleConversion.ToColor(colour, false)));
            }
        if (texture == null) _textures[index] = texture = ImageTexture.CreateFromImage(image);
        else texture.Update(image);
        _paletteVersions[index] = word.PaletteVersion;
        return texture;
    }

    /// <summary>Draws one blend layer. The retail order is all glows, then all words, highest entry first.</summary>
    public void DrawLayer(CanvasItem canvas, Layer layer)
    {
        if (_content == null) return;
        for (int i = _state.Count - 1; i >= 0; i--)
        {
            var word = _state[i];
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
