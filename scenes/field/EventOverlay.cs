// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The retail full-screen overlay: up to three image layers (location cards, the distant view, staff credits) drawn
/// in the 320x240 screen space. Each layer has a position about the screen centre, a scale and a brightness
/// (128 is full, 0 hides it), and can move to new values with an eased interpolation on the 30 Hz event clock.
/// A layer's mode picks how it is drawn (BATTLE.PRG's table at 0x800EC25C): 0 over the frame, 1 added while it fades
/// and over the frame at full brightness, and 3 added at
/// a quarter. Mode 2 is the staff credits' wipe (0x800CC204): the image is drawn whole and at full brightness on the
/// 320x240 screen, row by row between a left and a right edge (which cross the whole window when it is wider). Its scale is the wipe's progress: from 0 to 1 the
/// image is wiped on, from 1 to 2 wiped off, each row by its own width (320 plus a random walk of sines, eighths,
/// made as the layer is set up, 0x800C8550), so the edge is ragged. A brightness of 0 wipes from the left, any
/// other from the right; the layer shows whatever its brightness.
/// </summary>
public partial class EventOverlay : Control
{
    private sealed class Layer
    {
        public Texture2D? Texture;
        public Color? Fill;
        public Vector2 Position, StartPosition, EndPosition;
        public float Scale = 1, StartScale, EndScale;
        public float Brightness, StartBrightness, EndBrightness;
        public int Tick, Frames, Ease, Mode;
        public readonly int[] Widths = new int[240];
    }

    private readonly Random _random = new();

    private readonly Layer[] _layers = { new(), new(), new() };
    private readonly Control[] _canvases = new Control[3];
    private readonly bool[] _blended = new bool[3];

    /// <summary>Where a layer drawn behind the room goes (17's flag): a canvas layer the field shows as its background.</summary>
    public CanvasLayer? Behind { get; set; }

    /// <summary>Whether a layer with a picture is drawn in front of the room.</summary>
    public bool CoversRoom
    {
        get
        {
            for (int i = 0; i < _layers.Length; i++)
                if (_canvases[i]?.GetParent() == this && (_layers[i].Texture != null || _layers[i].Fill != null) && (_layers[i].Brightness > 0 || _layers[i].Mode == 2)) return true;
            return false;
        }
    }

    /// <summary>Whether any layer is drawn behind the room.</summary>
    public bool AnyBehind { get; private set; }

    public bool Idle
    {
        get
        {
            foreach (var layer in _layers) if (layer.Tick < layer.Frames) return false;
            return true;
        }
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest;
        AnchorRight = 1;
        AnchorBottom = 1;
        for (int i = 0; i < _canvases.Length; i++)
        {
            int index = i;
            var canvas = new Control { MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.Nearest, AnchorRight = 1, AnchorBottom = 1 };
            canvas.Draw += () => DrawLayer(canvas, _layers[index]);
            AddChild(canvas);
            _canvases[i] = canvas;
        }
    }

    /// <summary>Loads an overlay's layer images; every layer starts hidden.</summary>
    public void Load((Texture2D Texture, Color? Fill)[] layers)
    {
        for (int i = 0; i < _layers.Length; i++)
        {
            Place(i, false);
            _layers[i] = i < layers.Length ? new Layer { Texture = layers[i].Texture, Fill = layers[i].Fill } : new Layer();
            if (_canvases[i] != null) _canvases[i].Material = null;
            _blended[i] = false;
        }
        Redraw();
    }

    /// <summary>Puts a layer's canvas in front of everything or behind the room.</summary>
    private void Place(int layer, bool behind)
    {
        var canvas = _canvases[layer];
        if (canvas == null) return;
        Node parent = behind && Behind != null ? Behind : this;
        if (canvas.GetParent() != parent) canvas.Reparent(parent, false);
        // Layers stack by number, 0 at the back, wherever they have been: a canvas that went behind the room and came
        // back would otherwise be drawn over the others (EVT pages put a black panel on layer 0 under their text).
        int below = 0;
        for (int i = 0; i < layer; i++) if (_canvases[i]?.GetParent() == parent) below++;
        int first = 0;
        foreach (var child in parent.GetChildren()) { if (System.Array.IndexOf(_canvases, child) >= 0) break; first++; }
        parent.MoveChild(canvas, Math.Min(first + below, parent.GetChildCount() - 1));
        AnyBehind = false;
        foreach (var c in _canvases) AnyBehind |= c != null && c.GetParent() != this;
    }

    public void Clear()
    {
        for (int i = 0; i < _layers.Length; i++) { _layers[i] = new Layer(); Place(i, false); }
        foreach (var canvas in _canvases) if (canvas != null) canvas.Material = null;
        System.Array.Clear(_blended);
        Redraw();
    }

    public void Set(int layer, int mode, Vector2 position, float scale, float brightness, bool behind = false)
    {
        var l = _layers[Math.Min(layer, 2)];
        Place(Math.Min(layer, 2), behind);
        l.Mode = mode;
        _canvases[Math.Min(layer, 2)].Material = mode is 1 or 3 ? new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add } : null;
        _blended[Math.Min(layer, 2)] = mode is 1 or 3;
        if (mode == 2)
        {
            // The wipe's edge: an angle walks toward random targets by random steps, each row 320 plus its sine in eighths.
            int angle = _random.Next(512), target = _random.Next(1024);
            for (int y = 0; y < 240; y++)
            {
                l.Widths[y] = (int)(Math.Sin(angle * Math.Tau / 4096) * 4096) / 8 + 320;
                int step = _random.Next(256) - 64, next = _random.Next(1024);
                if (target < angle) { angle -= step; if (target >= angle) target = next; }
                else { angle += step; if (angle >= target) target = next; }
                if (angle < 0) { angle = 128; target = _random.Next(1024); }
            }
        }
        l.Position = position;
        l.Scale = scale;
        l.Brightness = brightness;
        l.Tick = l.Frames = 0;
        Redraw();
    }

    public void Move(int layer, Vector2 position, float scale, float brightness, int ease, int frames)
    {
        var l = _layers[Math.Min(layer, 2)];
        l.StartPosition = l.Position;
        l.StartScale = l.Scale;
        l.StartBrightness = l.Brightness;
        l.EndPosition = position;
        l.EndScale = scale;
        l.EndBrightness = brightness;
        l.Ease = ease;
        l.Tick = 0;
        l.Frames = frames;
    }

    /// <summary>Advances every layer's interpolation by one event frame.</summary>
    public void Step()
    {
        foreach (var l in _layers)
        {
            if (l.Tick >= l.Frames) continue;
            l.Tick++;
            float t = EventCamera.Ease(l.Ease, l.Tick / (float)l.Frames);
            l.Position = l.StartPosition.Lerp(l.EndPosition, t);
            l.Scale = Mathf.Lerp(l.StartScale, l.EndScale, t);
            l.Brightness = Mathf.Lerp(l.StartBrightness, l.EndBrightness, t);
        }
        Redraw();
    }

    private void Redraw()
    {
        // Mode 1 adds the layer only while it fades: at full brightness it is drawn over the frame as mode 0 is
        // (BATTLE.PRG 0x800CC148).
        for (int i = 0; i < _layers.Length; i++)
            if (_canvases[i] != null && _blended[i])
            {
                bool add = _layers[i].Mode == 3 || _layers[i].Brightness != 128;
                if (add != _canvases[i].Material is CanvasItemMaterial) _canvases[i].Material = add ? new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add } : null;
            }
        foreach (var canvas in _canvases) canvas?.QueueRedraw();
    }

    private static void DrawLayer(Control canvas, Layer l)
    {
        if (l.Texture == null || l.Brightness <= 0 && l.Mode != 2) return;
        // The retail screen is 320x240, of which the top 224 lines are seen; the overlay keeps that frame centred in the window.
        var size = canvas.GetRect().Size;
        var unit = FieldView.Unit(size);
        var origin = (size - new Vector2(320, FieldView.VisibleLines) * unit) / 2;
        // A picture drawn over the room in a window wider (or taller) than 4:3 stands between black bars, as a
        // pre-rendered screen; one behind the room, or one added over it, leaves the room to show at the sides.
        if (l.Mode == 0 && canvas.Material == null && canvas.GetParent() is EventOverlay && (origin.X > 0 || origin.Y > 0))
        {
            canvas.DrawRect(new Rect2(0, 0, origin.X, size.Y), Colors.Black);
            canvas.DrawRect(new Rect2(size.X - origin.X, 0, origin.X, size.Y), Colors.Black);
            canvas.DrawRect(new Rect2(0, 0, size.X, origin.Y), Colors.Black);
            canvas.DrawRect(new Rect2(0, size.Y - origin.Y, size.X, origin.Y), Colors.Black);
        }
        if (l.Mode == 2)
        {
            float progress = l.Scale;
            bool fromRight = l.Brightness != 0;
            var image = l.Texture.GetSize();
            for (int y = 0; y < 240; y++)
            {
                int left = 0, right = 320;
                if (progress < 1)
                {
                    int column = (int)(l.Widths[y] * progress);
                    if (fromRight) left = 320 - column; else right = column;
                }
                else if (progress > 1)
                {
                    int column = (int)(l.Widths[y] * (progress - 1));
                    if (fromRight) right = 320 - column; else left = column;
                }
                left = Math.Clamp(left, 0, 320);
                right = Math.Clamp(right, 0, 320);
                if (right <= left) continue;
                // The wipe crosses the whole screen, as it does the retail one: in a window wider than 4:3 its edges run
                // across the window's width, so a card wiped on is black from edge to edge, the room not showing at
                // the sides. The row's cells without a tile take the layer's fill (0x800CC204); drawn first, the
                // tiles cover it. The picture keeps its 4:3 place and shows where the wipe has passed over it.
                float windowLeft = left / 320f * size.X, windowRight = right / 320f * size.X;
                float top = origin.Y + y * unit.Y;
                if (l.Fill is { } fill) canvas.DrawRect(new Rect2(windowLeft, top, windowRight - windowLeft, unit.Y), fill);
                if (y >= image.Y) continue;
                float from = Math.Max(0, (windowLeft - origin.X) / unit.X), to = Math.Min(image.X, (windowRight - origin.X) / unit.X);
                if (to <= from) continue;
                canvas.DrawTextureRectRegion(l.Texture, new Rect2(origin.X + from * unit.X, top, (to - from) * unit.X, unit.Y), new Rect2(from, y, to - from, 1));
            }
            return;
        }
        var extent = l.Texture.GetSize() * l.Scale;
        var centre = new Vector2(160, 120) + l.Position;
        float level = Math.Min(2f, l.Brightness / 128f) * (l.Mode == 3 ? 0.25f : 1f);
        var area = new Rect2(origin + (centre - extent / 2) * unit, extent * unit);
        // The layer's grid is drawn whole: cells without a tile take its fill (colour 0 of its palette), so the title
        // and the epigraph stand on black over the whole screen.
        // A layer covering the whole retail screen (a credit card, the title) covers the whole window: its fill runs
        // out to the window's edges, so the room does not show beside it in a window wider than 4:3.
        var screen = new Rect2(origin, new Vector2(320, FieldView.VisibleLines) * unit);
        if (l.Fill is { } cells) canvas.DrawRect(area.Encloses(screen) ? new Rect2(Vector2.Zero, size) : area, new Color(cells.R * level, cells.G * level, cells.B * level));
        canvas.DrawTextureRect(l.Texture, area, false, new Color(level, level, level));
    }
}
