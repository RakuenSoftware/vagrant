// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The field's simple map in the bottom right (BATTLE.PRG 0x8008AC78 and 0x80098F8C), from the room's own map
/// (room sub-section 14: the room at the origin and its neighbours around it), on the 320 by 240 screen, scaled to the
/// window.
/// <list type="bullet">
/// <item>The map is turned to the camera, by 0x800 less its yaw about the vertical, then tipped 50 degrees toward
/// the viewer (cosine 2633/4096, sine 3138/4096), 256 units in front of the eye, and projected at H 512 about
/// (264, 196), clipped to (216, 156) 96 by 64.</item>
/// <item>Of the room Ashley stands in, its floor is drawn half-transparent in (0, 127, 63) and its lines in (0, 95,
/// 47); every room she has had the run of, hers among them, has its outline in (0, 127, 255). The rooms are drawn
/// last first, each its lines, outline then floor, so her room's floor lies over the rest.</item>
/// <item>Her room's markers are crosses of a pixel each way: a door grey (127, 127, 127), an exit to another area
/// (0, 127, 255), and the way she came in (flagged 0x10 as the room loads) (255, 0, 127), blinking every 16
/// frames; a marker whose lock is still shut is the HUD page's lock (8 by 8 at (216, 152)) instead.</item>
/// <item>The frame, (215, 155) to (313, 220), adds a quarter of its colour: green running (0, 128, 0) to (0, 255, 0)
/// round its corners, or with the weapon drawn a red that pulses over 16 frames.</item>
/// <item>Shown, the map slides in from the right a step a frame over eight frames (16 units each); hidden, it slides
/// back two steps a frame. Menus, the target sphere and events hide it.</item>
/// </list>
/// </summary>
public sealed partial class Minimap : RetailScreen
{
    private const float Cos = 2633f / 4096, Sin = 3138f / 4096, Depth = 256, H = 512;
    private static readonly Rect2 Area = new(216, 156, 96, 64);

    private readonly List<(GDictionary Room, bool Current)> _rooms = new();
    private Func<int, bool> _open = _ => true;
    private int _entered = -1;
    private Texture2D? _cursor;
    private Control _area = null!, _frame = null!;
    private float _yaw;
    private bool _shown, _drawn;
    private int _slide, _pulse, _blink, _ticks;

    public void Configure(Texture2D? cursor)
    {
        _cursor = cursor;
        _frame = new Control { Name = "Frame", MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1, Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add } };
        _frame.Draw += DrawFrame;
        AddChild(_frame);
        _area = new Control { Name = "Area", MouseFilter = MouseFilterEnum.Ignore, ClipContents = true, TextureFilter = TextureFilterEnum.Nearest };
        _area.Draw += DrawArea;
        AddChild(_area);
        Resized += Place;
        Place();
    }

    private Vector2 Unit => FieldView.Pin(Size, 2).Unit;

    private void Place()
    {
        if (_area == null) return;
        // It keeps to the window's right edge.
        _area.Position = FieldView.Pin(Size, 2).Offset + Area.Position * Unit;
        _area.Size = Area.Size * Unit;
    }

    /// <summary>
    /// The room's map (<paramref name="rooms"/>, the room first), the rooms Ashley has had the run of, and which of
    /// the state flags are set (a lock is open when its flag, 0x33F plus the lock, is), and the marker of the way she
    /// came in (-1 none).
    /// </summary>
    public void SetRooms(GArray? rooms, ICollection<(int Zone, int Map)> visited, Func<int, bool> open, int entered)
    {
        _rooms.Clear();
        _open = open;
        _entered = entered;
        if (rooms != null)
            for (int i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i].AsGodotDictionary();
                if (i == 0 || visited.Contains((room["zone"].AsInt32(), room["map"].AsInt32()))) _rooms.Add((room, i == 0));
            }
        _area.QueueRedraw();
    }

    /// <summary>The frame's state: whether the map shows, the camera's yaw and whether the weapon is drawn.</summary>
    public void Set(bool shown, float yaw, bool drawn)
    {
        if (yaw != _yaw) _area.QueueRedraw();
        if (drawn != _drawn) _frame.QueueRedraw();
        (_shown, _yaw, _drawn) = (shown, yaw, drawn);
    }

    /// <summary>Steps on the 30 Hz frame: the slide, the frame's pulse and the containers' blink.</summary>
    public override void _PhysicsProcess(double delta)
    {
        if ((++_ticks & 1) != 0) return;
        int slide = _slide;
        _slide = _shown ? Math.Min(8, _slide + 1) : Math.Max(0, _slide - 2);
        _pulse = _drawn ? _pulse + 1 : 0;
        bool blink = (_blink & 16) != 0;
        _blink++;
        if (slide != _slide || blink != ((_blink & 16) != 0)) _area.QueueRedraw();
        if (slide != _slide || _drawn) _frame.QueueRedraw();
    }

    private void DrawFrame()
    {
        if (_slide <= 0) return;
        var unit = Unit;
        Color[] colours;
        if (_drawn)
        {
            int shade = 128 - (int)(Math.Sin(_pulse * 256 * Math.Tau / 4096) * 4096) / 64;
            Color Red(int r) => Color.Color8((byte)Math.Clamp(r, 0, 255), 0, 0);
            colours = new[] { Red(shade), Red(shade + 32), Red(shade + 63), Red(shade + 32), Red(shade) };
        }
        else colours = new[] { Color.Color8(0, 128, 0), Color.Color8(0, 192, 0), Color.Color8(0, 255, 0), Color.Color8(0, 192, 0), Color.Color8(0, 128, 0) };
        // A quarter of the colour is added to what is under it.
        for (int i = 0; i < colours.Length; i++) colours[i] = new Color(colours[i].R / 4, colours[i].G / 4, colours[i].B / 4);
        var corners = new[] { new Vector2(215, 155), new Vector2(313, 155), new Vector2(313, 220), new Vector2(215, 220), new Vector2(215, 155) };
        var offset = FieldView.Pin(Size, 2).Offset;
        for (int i = 0; i < corners.Length; i++) corners[i] = offset + (corners[i] + new Vector2(0.5f, 0.5f)) * unit;
        _frame.DrawPolylineColors(corners, colours, unit.Y);
    }

    private void DrawArea()
    {
        if (_slide <= 0 || _rooms.Count == 0) return;
        var unit = Unit;
        float angle = (0x800 - _yaw) * Mathf.Tau / 4096, s = Mathf.Sin(angle), c = Mathf.Cos(angle);
        var centre = new Vector2(392 - _slide * 16, 196);
        // Screen points inside the clipped area, in window pixels from its corner.
        Vector2 Project(GArray v)
        {
            float x = v[0].AsSingle(), y = v[1].AsSingle(), z = v[2].AsSingle();
            float px = c * x + s * z, py = s * Sin * x + Cos * y - c * Sin * z, pz = -s * Cos * x + Sin * y + c * Cos * z + Depth;
            var screen = centre + new Vector2(px, py) * H / Math.Max(1, pz);
            return (screen.Floor() - Area.Position + new Vector2(0.5f, 0.5f)) * unit;
        }
        var floor = new Color(0, 127 / 255f, 63 / 255f, 0.5f);
        for (int r = _rooms.Count - 1; r >= 0; r--)
        {
            var (room, current) = _rooms[r];
            var vertices = room["vertices"].AsGodotArray();
            var points = new Vector2[vertices.Count];
            for (int i = 0; i < points.Length; i++) points[i] = Project(vertices[i].AsGodotArray());
            void Lines(GArray list, Color colour)
            {
                foreach (var value in list)
                {
                    var e = value.AsGodotArray();
                    if (e[0].AsInt32() < points.Length && e[1].AsInt32() < points.Length) _area.DrawLine(points[e[0].AsInt32()], points[e[1].AsInt32()], colour, unit.Y);
                }
            }
            if (current) Lines(room["lines"].AsGodotArray(), new Color(0, 95 / 255f, 47 / 255f, 0.5f));
            Lines(room["edges"].AsGodotArray(), new Color(0, 127 / 255f, 1, 0.5f));
            if (!current) continue;
            foreach (var value in room["quads"].AsGodotArray())
            {
                var q = value.AsGodotArray();
                int a = q[0].AsInt32(), b = q[1].AsInt32(), d = q[2].AsInt32(), e = q[3].AsInt32();
                if (Math.Max(Math.Max(a, b), Math.Max(d, e)) >= points.Length) continue;
                // A quad's corners run round its edge: 0x80098F8C hands them to the GPU as 0, 1, 3, 2.
                _area.DrawPrimitive(new[] { points[a], points[b], points[d] }, new[] { floor, floor, floor }, null);
                _area.DrawPrimitive(new[] { points[a], points[d], points[e] }, new[] { floor, floor, floor }, null);
            }
            foreach (var value in room["triangles"].AsGodotArray())
            {
                var t = value.AsGodotArray();
                if (t[0].AsInt32() < points.Length && t[1].AsInt32() < points.Length && t[2].AsInt32() < points.Length)
                    _area.DrawPrimitive(new[] { points[t[0].AsInt32()], points[t[1].AsInt32()], points[t[2].AsInt32()] }, new[] { floor, floor, floor }, null);
            }
            // The markers go over everything else.
            var markers = room["markers"].AsGodotArray();
            for (int index = 0; index < markers.Count; index++)
            {
                var m = markers[index].AsGodotArray();
                if (m[0].AsInt32() >= points.Length) continue;
                int flags = m[2].AsInt32() | (index == _entered ? 0x10 : 0), seal = m[3].AsInt32();
                var at = (points[m[0].AsInt32()] / unit - new Vector2(0.5f, 0.5f)).Round();
                if (seal != 0 && !_open(0x33F + seal))
                {
                    if (_cursor != null) _area.DrawTextureRectRegion(_cursor, new Rect2((at - new Vector2(3, 4)) * unit, new Vector2(8, 8) * unit), new Rect2(216, 152, 8, 8));
                    continue;
                }
                Color colour;
                if ((flags & 0x10) != 0) { if ((_blink & 16) == 0) continue; colour = Color.Color8(255, 0, 127); }
                else if ((flags & 4) != 0) colour = Color.Color8(0, 127, 255);
                else if ((flags & 0x1F) == 0) colour = Color.Color8(127, 127, 127);
                else continue;
                _area.DrawRect(new Rect2((at + new Vector2(0, -1)) * unit, new Vector2(1, 3) * unit), colour);
                _area.DrawRect(new Rect2((at + new Vector2(-1, 0)) * unit, new Vector2(3, 1) * unit), colour);
            }
        }
    }
}
