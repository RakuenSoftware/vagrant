// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The map screen (MENU/MENU5.PRG): the area's rooms Ashley has visited, in perspective over the map's backdrop.
/// <list type="bullet">
/// <item>The area map's vertices are scaled by 4 and moved so the chosen room's middle (its x and z, and its lowest y)
/// is the origin; choosing another room eases the map there by an eighth a frame (at least 8 units).</item>
/// <item>The eye stands <c>Distance</c> from the origin (768; R2 with up and down from 512 to 1024, 16 a frame) at a
/// yaw and pitch (L2 with the pad, 64 a frame, the pitch from level to 1024; at first 568, and the yaw 2048 less the
/// field camera's), looking at it; points are projected at a distance of 512 about (144, 136) and the pad's offset
/// (4 a frame, 96 either way across and 80 up and down). A room with a point nearer than 256 or beyond 8188 is not
/// drawn.</item>
/// <item>Colours fade to black with depth as the GTE's depth cue sets them (SetFogNear(768): none to 768, wholly at
/// five times it). Floors are (0, 0x40, 0x80), the chosen room's (0, 0x80, 0x40); an outline edge is (0, 0x80, 0xFF)
/// drawn four times half over what is under it a pixel each way, then whole (the chosen room's (0, 0xFF, 0x80)); a
/// thin line is (0, 0x40, 0x80), the chosen room's (0, 0xFF, 0x80), half over. Nearer things are drawn over farther.</item>
/// <item>In the chosen room a marker is a cross of three pixels each way: a door (0xC0, 0xC0, 0xC0), an exit to
/// another area (0, 0xC0, 0xC0) under its icon and named at the right, the way Ashley came in pink (0xFF, 0, 0x80)
/// on 16 frames of 32; a save point, workshop or container (0xA0, 0xA0, 0) under its icons in turn, 32 frames each,
/// and Ashley's own mark at the anchor of the room she is in, pulsing.</item>
/// <item>The legend at the top left (L1 BACK, R1 NEXT, L2 ROTATION, R2 ZOOM, Circle MENU, lit as they can be used)
/// and the compass at (62, 184) turned by the yaw slide in from the left with the room's name, which rises from y 224
/// to 160 at x 60; Select hides them.</item>
/// </list>
/// </summary>
public sealed partial class MapCanvas : Control
{
    private const float Projection = 512, CentreX = 144, CentreY = 136, FogNear = 768;

    private readonly List<GDictionary> _rooms = new();
    private readonly List<bool> _visited = new();
    private readonly List<Vector3[]> _points = new();
    private int _chosen, _ashleyRoom = -1, _entered = -1, _frames, _slide = -384, _ui = -128;
    private Vector3 _centre;
    private float _yaw, _pitch = 568, _distance = 768, _panX, _panY;
    private bool _hidden;
    private float _openedYaw;

    /// <summary>The legend's state (retail's command state): 0 all lit, 2 zoom, 3 rotation, 5 none.</summary>
    private int _commands = 5;

    private Texture2D? _backdrop, _buttons, _icons, _ashleyIcon, _labels;
    private Texture2D? _compassRim, _compassNorth, _compassMark, _compassMarkLit, _compassRing, _compassNeedle;
    private Font? _system;
    private int[] _widths = Array.Empty<int>(), _columns = Array.Empty<int>(), _kerning = Array.Empty<int>();
    private string[] _legend = Array.Empty<string>();
    private GArray _connecting = new();
    private List<int> _areasVisited = new();
    private GArray _areaNames = new();

    /// <summary>Whether MENU's list of the areas visited is up (Circle), and the one pointed at.</summary>
    private bool _picking;
    private int _pick;
    private Texture2D? _cursor;

    /// <summary>The area shown, by scene id.</summary>
    public int Scene { get; private set; }

    /// <summary>The chosen room's name, as its box shows it.</summary>
    public string RoomName => _chosen >= 0 && _chosen < _rooms.Count ? _rooms[_chosen].GetValueOrDefault("name", "").AsString() : "";

    public void Configure(Wol.Games.Vagrant.Runtime.RetailContent content)
    {
        Texture2D? Page(string name)
        {
            try { return content.Texture(name); }
            catch (System.IO.InvalidDataException) { return null; }
            catch (System.IO.IOException) { return null; }
        }
        if (content.Has("map_menu"))
        {
            var menu = content.Data("map_menu");
            _legend = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(menu["legend"].AsGodotArray(), v => v.AsString()));
            _connecting = menu["connecting"].AsGodotArray();
            _backdrop = Page("map_backdrop");
        }
        _buttons = Page("hud_page_944");
        _cursor = Page("hud_cursor");
        if (content.Has("area_names")) _areaNames = content.Data("area_names")["list"].AsGodotArray();
        _icons = Page("hud_page_848");
        _ashleyIcon = Page("hud_page_976");
        _labels = Page("hud_labels");
        // The compass's pieces on the second HUD page, by their palettes (MENU5 0x801076B0).
        _compassRim = Page("hud_second_960_222");
        _compassNorth = Page("hud_second_960_221");
        _compassMark = Page("hud_second_1008_220");
        _compassMarkLit = Page("hud_second_976_222");
        _compassNeedle = Page("hud_second_992_222");
        _compassRing = _compassRim;
        _system = content.Has("system_font") ? content.BitmapFont("system_font") : null;
        if (content.Has("hud") && content.Data("hud").GetValueOrDefault("font", default).AsGodotDictionary() is { Count: > 0 } font)
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

    /// <summary>The areas with a room Ashley has visited, hers among them, by scene id.</summary>
    public static List<int> Areas(FieldView view)
    {
        var areas = new List<int>();
        int own = view.Document.GetValueOrDefault("scene", -1).AsInt32();
        for (int scene = 1; scene < 32; scene++)
        {
            if (!view.Content.Has($"area_{scene:d2}")) continue;
            foreach (var value in view.Content.Data($"area_{scene:d2}")["rooms"].AsGodotArray())
            {
                var room = value.AsGodotDictionary();
                if (scene == own || view.Visited.Contains((room["zone"].AsInt32(), room["map"].AsInt32()))) { areas.Add(scene); break; }
            }
        }
        return areas;
    }

    /// <summary>
    /// Opens the map on an area (Ashley's own when 0), the view set as MENU5 first sets it: the pitch 568, the yaw
    /// turned from the field camera's <paramref name="yaw"/>, the map sliding in from the left and centred on the
    /// room she is in (or the area's first visited).
    /// </summary>
    public void Open(FieldView view, int area, float yaw)
    {
        _yaw = _openedYaw = 2048 - yaw;
        _pitch = 568;
        _distance = 768;
        (_panX, _panY) = (0, 0);
        _slide = -384;
        _ui = -128;
        _frames = 0;
        _hidden = false;
        _picking = false;
        _commands = 5;
        Load(view, area);
    }

    /// <summary>Loads an area's rooms and centres the map on the room Ashley is in, else its first visited.</summary>
    private void Load(FieldView view, int area)
    {
        _rooms.Clear();
        _visited.Clear();
        _points.Clear();
        int own = view.Document.GetValueOrDefault("scene", -1).AsInt32();
        Scene = area > 0 ? area : own;
        _areasVisited = Areas(view);
        _ashleyRoom = -1;
        _chosen = 0;
        if (Scene <= 0 || !view.Content.Has($"area_{Scene:d2}")) { QueueRedraw(); return; }
        foreach (var value in view.Content.Data($"area_{Scene:d2}")["rooms"].AsGodotArray())
        {
            var room = value.AsGodotDictionary();
            var key = (room["zone"].AsInt32(), room["map"].AsInt32());
            bool here = key == (view.Zone, view.Map);
            if (here) _ashleyRoom = _rooms.Count;
            _rooms.Add(room);
            _visited.Add(here || view.Visited.Contains(key));
            var points = new List<Vector3>();
            foreach (var v in room["vertices"].AsGodotArray())
            {
                var p = v.AsGodotArray();
                points.Add(new Vector3(p[0].AsInt32(), p[1].AsInt32(), p[2].AsInt32()) * 4);
            }
            _points.Add(points.ToArray());
        }
        _chosen = _ashleyRoom >= 0 ? _ashleyRoom : Math.Max(0, _visited.IndexOf(true));
        _entered = Scene == own ? view.EnteredMarker : -1;
        _centre = Middle(_chosen);
        QueueRedraw();
    }

    /// <summary>A room's middle: its x and z halfway across, its lowest y (MENU5 0x80105D2C).</summary>
    private Vector3 Middle(int room)
    {
        if (room < 0 || room >= _points.Count || _points[room].Length == 0) return Vector3.Zero;
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var p in _points[room]) { lo = lo.Min(p); hi = hi.Max(p); }
        return new Vector3(MathF.Truncate((lo.X + hi.X) / 2), hi.Y, MathF.Truncate((lo.Z + hi.Z) / 2));
    }

    /// <summary>
    /// A frame of the map screen: the pad pans; L2 with it turns, R2 with up and down zooms; L1 and R1 step through the
    /// visited rooms by the area's order; Start sets the view back; Select hides the legend; Circle (MENU) lists the
    /// areas Ashley has been to, to show another. Returns how Cross or Triangle
    /// leaves: 1 back to the command list; 0 while it stays.
    /// </summary>
    public int Step(FieldView view, Func<string, bool> pressed, float fieldYaw)
    {
        _frames++;
        if (_slide < 0) _slide = Math.Min(0, _slide + 64);
        if (_ui < 0) _ui = Math.Min(0, _ui + 16);
        // Cross and Triangle both leave the map for the command list (MENU5's two ways out hand back to the menu).
        if (_picking)
        {
            // MENU: up and down choose an area, Circle shows it (the map slides out and the new one in), Cross and
            // Triangle put the list away.
            _commands = 5;
            var areas = Areas(view);
            int moved = (pressed("ps_down") ? 1 : 0) - (pressed("ps_up") ? 1 : 0);
            if (moved != 0 && areas.Count > 1) { _pick = (_pick + moved + areas.Count) % areas.Count; view.MenuSound(FieldView.MenuChange); }
            if (pressed("ps_circle") && _pick < areas.Count)
            {
                view.MenuSound(FieldView.MenuSelect);
                _picking = false;
                if (areas[_pick] != Scene) { Load(view, areas[_pick]); _slide = -384; }
            }
            else if (pressed("ps_cross") || pressed("ps_triangle")) { view.MenuSound(FieldView.MenuLeave); _picking = false; }
            QueueRedraw();
            return 0;
        }
        if (pressed("ps_cross") || pressed("ps_triangle")) { view.MenuSound(6); return 1; }
        if (pressed("ps_circle"))
        {
            // MENU (MENU5 0x80103B44: sound 0x7E 5) lists the areas visited, on the one shown.
            view.MenuSound(5);
            _picking = true;
            _pick = Math.Max(0, Areas(view).IndexOf(Scene));
            QueueRedraw();
            return 0;
        }
        if (pressed("ps_select")) _hidden = !_hidden;
        bool Held(string action) => Input.IsActionPressed(action);
        int was = _chosen;
        _commands = 0;
        if (Held("ps_l2"))
        {
            _commands = 3;
            if (Held("ps_left")) _yaw -= 64;
            else if (Held("ps_right")) _yaw += 64;
            if (Held("ps_down") && _pitch > 0) _pitch -= 64;
            else if (Held("ps_up") && _pitch < 1024) _pitch += 64;
        }
        else if (Held("ps_r2"))
        {
            _commands = 2;
            if (Held("ps_up") && _distance > 512) _distance -= 16;
            else if (Held("ps_down") && _distance < 1024) _distance += 16;
        }
        else if (pressed("ps_start"))
        {
            (_panX, _panY, _distance, _pitch, _yaw) = (0, 0, 768, 0x238, 2048 - fieldYaw);
            _chosen = _ashleyRoom >= 0 ? _ashleyRoom : _chosen;
            _centre = Middle(_chosen);
        }
        else
        {
            if (Held("ps_up") && _panY > -80) _panY -= 4;
            else if (Held("ps_down") && _panY < 80) _panY += 4;
            if (Held("ps_left") && _panX > -96) _panX -= 4;
            else if (Held("ps_right") && _panX < 96) _panX += 4;
            if (!(Held("ps_l1") && Held("ps_r1")) && _rooms.Count > 0)
            {
                int step = pressed("ps_r1") ? 1 : pressed("ps_l1") ? -1 : 0;
                if (step != 0)
                {
                    // Along the area's order to the next room visited (0x80105EF8), then eased to its middle.
                    int room = _chosen;
                    for (int n = 0; n < _rooms.Count; n++)
                    {
                        room = _rooms[room].GetValueOrDefault(step > 0 ? "next" : "prev", room).AsInt32();
                        if (room < 0 || room >= _rooms.Count) { room = _chosen; break; }
                        if (_visited[room]) break;
                    }
                    _chosen = room;
                    if (_chosen != was) view.MenuSound(0xB);
                }
            }
        }
        // The map eases to the chosen room's middle: an eighth of the way a frame, at least 8 units, the rest once less.
        var target = Middle(_chosen);
        var left = target - _centre;
        float Ease(float d) => Math.Abs(d) < 8 ? d : Math.Abs(d) > 128 ? MathF.Truncate(d / 8) : Math.Sign(d) * 8;
        _centre += new Vector3(Ease(left.X), Ease(left.Y), Ease(left.Z));
        QueueRedraw();
        return 0;
    }

    /// <summary>The eye's view of a point (MENU5 0x80104558): yaw then pitch, about the eye on its sphere.</summary>
    private Vector3 View(Vector3 point)
    {
        float a = _yaw * Mathf.Tau / 4096, b = _pitch * Mathf.Tau / 4096;
        var eye = new Vector3(Mathf.Sin(a) * _distance * Mathf.Cos(b), -Mathf.Sin(b) * _distance, -Mathf.Cos(a) * _distance * Mathf.Cos(b));
        var v = point - _centre - eye;
        // Turned by the yaw, then the pitch.
        var y = new Vector3(Mathf.Cos(a) * v.X + Mathf.Sin(a) * v.Z, v.Y, -Mathf.Sin(a) * v.X + Mathf.Cos(a) * v.Z);
        return new Vector3(y.X, Mathf.Cos(b) * y.Y - Mathf.Sin(b) * y.Z, Mathf.Sin(b) * y.Y + Mathf.Cos(b) * y.Z);
    }

    /// <summary>A colour after the depth cue at depth <paramref name="z"/>: whole to 768, then towards black, gone at five times it.</summary>
    private static Color Cue(Color colour, float z)
    {
        float p = Math.Clamp(1.25f * (1 - FogNear / Math.Max(1, z)), 0, 1);
        return new Color(colour.R * (1 - p), colour.G * (1 - p), colour.B * (1 - p), colour.A);
    }

    public override void _Draw()
    {
        var (unit, middle) = FieldView.Pin(Size, 1);
        DrawRect(new Rect2(Vector2.Zero, Size), Colors.Black);
        DrawSetTransform(middle);
        // The backdrop, brightening in to five eighths as the map slides in (MENU5: (brightness + 128) / 2).
        float shade = _slide < 0 ? 1 - 0.375f * (1 + _slide / 384f) : 0.625f;
        if (_backdrop != null) DrawTextureRect(_backdrop, new Rect2(Vector2.Zero, new Vector2(320, 240) * unit), false, new Color(shade, shade, shade));
        DrawRooms(unit);
        DrawLegend(unit);
        DrawAreas(unit);
        DrawSetTransform(Vector2.Zero);
    }

    private void DrawRooms(Vector2 unit)
    {
        var shapes = new List<(float Depth, Action Draw)>();
        var origin = new Vector2(CentreX + _panX + _slide, CentreY + _panY);
        for (int r = 0; r < _rooms.Count; r++)
        {
            if (!_visited[r]) continue;
            var room = _rooms[r];
            var points = _points[r];
            var screen = new Vector2[points.Length];
            var depth = new float[points.Length];
            bool kept = true;
            for (int i = 0; i < points.Length && kept; i++)
            {
                var v = View(points[i]);
                // RotTransPers's otz is a quarter of the depth; outside 64 to 2046 the room is not drawn.
                if (v.Z / 4 < 64 || v.Z / 4 - 64 >= 0x7BF) kept = false;
                depth[i] = v.Z;
                screen[i] = v.Z > 0 ? origin + new Vector2(v.X, v.Y) * Projection / v.Z : origin;
            }
            if (!kept) continue;
            bool chosen = r == _chosen;
            var floor = chosen ? Color.Color8(0, 0x80, 0x40) : Color.Color8(0, 0x40, 0x80);
            bool Fits(GArray e, int n) { for (int k = 0; k < n; k++) if (e[k].AsInt32() >= points.Length) return false; return true; }
            foreach (var value in room["triangles"].AsGodotArray())
            {
                var t = value.AsGodotArray();
                if (!Fits(t, 3)) continue;
                int a = t[0].AsInt32(), b = t[1].AsInt32(), c = t[2].AsInt32();
                var corners = new[] { screen[a] * unit, screen[b] * unit, screen[c] * unit };
                var colours = new[] { Cue(floor, depth[a]), Cue(floor, depth[b]), Cue(floor, depth[c]) };
                shapes.Add((Math.Max(depth[a], Math.Max(depth[b], depth[c])), () => DrawPrimitive(corners, colours, null)));
            }
            foreach (var value in room["quads"].AsGodotArray())
            {
                var q = value.AsGodotArray();
                if (!Fits(q, 4)) continue;
                // The GPU takes a quad's corners a, b, d, c.
                int a = q[0].AsInt32(), b = q[1].AsInt32(), c = q[2].AsInt32(), d = q[3].AsInt32();
                var corners = new[] { screen[a] * unit, screen[b] * unit, screen[c] * unit, screen[d] * unit };
                var colours = new[] { Cue(floor, depth[a]), Cue(floor, depth[b]), Cue(floor, depth[c]), Cue(floor, depth[d]) };
                shapes.Add((Math.Max(Math.Max(depth[a], depth[b]), Math.Max(depth[c], depth[d])), () =>
                {
                    DrawPrimitive(new[] { corners[0], corners[1], corners[2] }, new[] { colours[0], colours[1], colours[2] }, null);
                    DrawPrimitive(new[] { corners[0], corners[2], corners[3] }, new[] { colours[0], colours[2], colours[3] }, null);
                }));
            }
            foreach (var value in room["edges"].AsGodotArray())
            {
                var e = value.AsGodotArray();
                if (!Fits(e, 2)) continue;
                int a = e[0].AsInt32(), b = e[1].AsInt32();
                var glow = Color.Color8(0, 0x80, 0xFF);
                var line = chosen ? Color.Color8(0, 0xFF, 0x80) : glow;
                Color ga = Cue(glow, depth[a]), gb = Cue(glow, depth[b]), la = Cue(line, depth[a]), lb = Cue(line, depth[b]);
                var from = screen[a];
                var to = screen[b];
                shapes.Add((Math.Max(depth[a], depth[b]) - 4, () =>
                {
                    foreach (var off in new[] { new Vector2(-1, 0), new Vector2(0, -1), new Vector2(1, 0), new Vector2(0, 1) })
                        DrawPolyline(new[] { (from + off) * unit, (to + off) * unit }, new Color(ga, 0.5f), unit.Y);
                    DrawLine(from * unit, to * unit, la.Lerp(lb, 0.5f), unit.Y);
                }));
            }
            foreach (var value in room["lines"].AsGodotArray())
            {
                var e = value.AsGodotArray();
                if (!Fits(e, 2)) continue;
                int a = e[0].AsInt32(), b = e[1].AsInt32();
                var colour = chosen ? Color.Color8(0, 0xFF, 0x80) : Color.Color8(0, 0x40, 0x80);
                var from = screen[a] * unit;
                var to = screen[b] * unit;
                var c = Cue(colour, (depth[a] + depth[b]) / 2);
                shapes.Add((Math.Max(depth[a], depth[b]) - 4, () => DrawLine(from, to, new Color(c, 0.5f), unit.Y)));
            }
            if (chosen) Markers(r, room, screen, depth, shapes, unit);
        }
        // Farther first, as the ordering table draws them.
        shapes.Sort((x, y) => y.Depth.CompareTo(x.Depth));
        foreach (var (_, draw) in shapes) draw();
    }

    /// <summary>The icons' cells on the HUD page through (848, 223): an exit, a workshop, a save point, a container (MENU5 0x80108CE4).</summary>
    private static readonly Rect2[] Icons = { new(104, 144, 18, 8), new(144, 216, 44, 7), new(128, 56, 22, 8), new(218, 144, 36, 8) };

    /// <summary>Ashley's mark's brightness by frame, sixteen frames round (MENU5 0x80102C00).</summary>
    private static readonly int[] Pulse = { 0x40, 0x50, 0x60, 0x70, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x70, 0x60, 0x50, 0x40 };

    private void Markers(int r, GDictionary room, Vector2[] screen, float[] depth, List<(float, Action)> shapes, Vector2 unit)
    {
        var markers = room["markers"].AsGodotArray();
        var labels = new List<(Vector2 At, int Area)>();
        for (int index = 0; index < markers.Count; index++)
        {
            var m = markers[index].AsGodotArray();
            int vertex = m[0].AsInt32(), label = m[1].AsInt32(), flags = m[2].AsInt32();
            if (vertex >= screen.Length) continue;
            var at = screen[vertex];
            Color? cross = null;
            bool entered = r == _ashleyRoom && index == _entered;
            if ((flags & 4) != 0)
            {
                shapes.Add((0, () => Icon(0, at, unit)));
                labels.Add((at, label));
                cross = entered ? ((_frames & 0x10) != 0 ? Color.Color8(0xFF, 0, 0x80) : null) : Color.Color8(0, 0xC0, 0xC0);
            }
            if (flags == 0) cross = entered ? ((_frames & 0x10) != 0 ? Color.Color8(0xFF, 0, 0x80) : null) : Color.Color8(0xC0, 0xC0, 0xC0);
            // Ashley's mark at her room's anchor, save point and workshop; the room's save point, workshop and
            // container icons; each shown in turn for 32 frames.
            var turns = new List<int>();
            if ((flags & 0xB) != 0 && r == _ashleyRoom) turns.Add(0);
            if ((flags & 2) != 0) turns.Add(1);
            if ((flags & 8) != 0) { turns.Add(2); turns.Add(1); turns.Add(3); }
            if ((flags & 0x10) != 0) turns.Add(3);
            if (turns.Count > 0)
            {
                cross = Color.Color8(0xA0, 0xA0, 0);
                int shown = turns[(_frames >> 5) % turns.Count];
                if (shown == 0 && _ashleyIcon != null)
                {
                    float bright = Pulse[_frames & 15] / 128f;
                    shapes.Add((0, () => DrawTextureRectRegion(_ashleyIcon, new Rect2((at - new Vector2(8, 8)) * unit, new Vector2(16, 16) * unit), new Rect2(64, 128, 16, 16), new Color(bright, bright, bright))));
                }
                else if (shown > 0) shapes.Add((0, () => Icon(shown switch { 1 => 2, 2 => 1, _ => 3 }, at, unit)));
            }
            if (cross is { } colour)
            {
                float d = depth[vertex] - 4;
                shapes.Add((d, () =>
                {
                    DrawRect(new Rect2((at + new Vector2(-1, 0)) * unit, new Vector2(3, 1) * unit), colour);
                    DrawRect(new Rect2((at + new Vector2(0, -1)) * unit, new Vector2(1, 3) * unit), colour);
                }));
            }
        }
        // The exits' areas down the right from y 188, each joined to its exit by a line bent towards x 170
        // (MENU5 0x80108100); an area not yet visited reads as the table's last name.
        if (_hidden || _commands == 5) return;
        float y = 188;
        foreach (var (at, area) in labels)
        {
            float pivot = 170 - Math.Max(0, (170 - at.X) / 4);
            var from = at;
            float row = y;
            int name = _areasVisited.Contains(area) ? area : _connecting.Count - 1;
            string text = name >= 0 && name < _connecting.Count ? _connecting[name].AsGodotDictionary()["text"].AsString() : "";
            shapes.Add((-1, () =>
            {
                DrawLine(from * unit, new Vector2(pivot, row + 8) * unit, Colors.White, unit.Y);
                DrawLine(new Vector2(pivot, row + 8) * unit, new Vector2(194, row + 8) * unit, Colors.White, unit.Y);
                DrawPolygon(new[] { new Vector2(178, row) * unit, new Vector2(322, row) * unit, new Vector2(322, row + 12) * unit, new Vector2(178, row + 12) * unit },
                    new[] { MenuList.BarDark, MenuList.BarLight, MenuList.BarLight, MenuList.BarDark });
                if (_system != null)
                {
                    DrawSetTransform(FieldView.Pin(Size, 1).Offset, 0, unit);
                    DrawString(_system, new Vector2(184, row + 13), text, HorizontalAlignment.Left, -1, 12);
                    DrawSetTransform(FieldView.Pin(Size, 1).Offset);
                }
            }));
            y -= 16;
        }
    }

    /// <summary>
    /// The area's name on a header bar at the top right (MENU5 0x80103A64: a row sliding in to (180, 34), 140 wide), and
    /// MENU's list of the areas visited beneath it while it is up: a row each from y 50, 16 apart, from x 194, the
    /// pointer at the chosen one.
    /// </summary>
    private void DrawAreas(Vector2 unit)
    {
        var pin = FieldView.Pin(Size, 2).Offset;
        string Name(int scene) => scene >= 0 && scene < _areaNames.Count ? _areaNames[scene].AsString() : "";
        void Row(float x, float y, float width, Color from, Color to, string text)
        {
            DrawSetTransform(pin);
            DrawPolygon(new[] { new Vector2(x, y) * unit, new Vector2(x + width, y) * unit, new Vector2(x + width, y + 12) * unit, new Vector2(x, y + 12) * unit }, new[] { from, to, to, from });
            if (_system != null)
            {
                DrawSetTransform(pin, 0, unit);
                DrawString(_system, new Vector2(x + 6, y + 13), text, HorizontalAlignment.Left, -1, 12);
            }
            DrawSetTransform(FieldView.Pin(Size, 1).Offset);
        }
        if (Scene > 0) Row(180, 34, 140, MenuList.HeaderLight, MenuList.HeaderDark, Name(Scene));
        if (!_picking) return;
        for (int i = 0; i < _areasVisited.Count; i++)
        {
            bool chosen = i == _pick;
            Row(194, 50 + 16 * i, 126, chosen ? MenuList.ChosenLight : MenuList.BarDark, chosen ? MenuList.ChosenDark : MenuList.BarLight, Name(_areasVisited[i]));
        }
        if (_cursor != null)
        {
            DrawSetTransform(pin);
            DrawTextureRectRegion(_cursor, new Rect2(new Vector2(180, 50 + 16 * _pick - 10) * unit, new Vector2(17, 20) * unit), new Rect2(31, 44, 17, 20));
            DrawSetTransform(FieldView.Pin(Size, 1).Offset);
        }
    }

    /// <summary>An icon centred on x, its top 14 above y (MENU5 0x80107598).</summary>
    private void Icon(int id, Vector2 at, Vector2 unit)
    {
        if (_icons == null) return;
        var cell = Icons[id];
        DrawTextureRectRegion(_icons, new Rect2(new Vector2(at.X - MathF.Floor(cell.Size.X / 2), at.Y - 14) * unit, cell.Size * unit), cell);
    }

    /// <summary>
    /// The legend (MENU5 0x801071F0): the L1, R1, L2, R2 and Circle buttons at (8, 15), (64, 15), (8, 33), (8, 52)
    /// and (8, 70), their words at (28, 18), (84, 18), (28, 36), (28, 54) and (28, 72) lit (0x80) where they can be
    /// used and dim (0x20) otherwise, each row's olive band, the compass, and the chosen room's name.
    /// </summary>
    private void DrawLegend(Vector2 unit)
    {
        if (_hidden) return;
        float x = _ui;
        // The bands: olive lines shortening by one a row, and a black slant beneath, from x 16.
        foreach (var (top, width) in new[] { (18f, 114), (36f, 96), (54f, 78), (72f, 60) })
        {
            DrawPolygon(new[] { new Vector2(x + 18, top + 2) * unit, new Vector2(x + 18 + width - 1, top + 2) * unit, new Vector2(x + 18 + width - 12, top + 13) * unit, new Vector2(x + 18, top + 13) * unit }, new[] { Colors.Black, Colors.Black, Colors.Black, Colors.Black });
            for (int i = 0; i < 11; i++)
            {
                var from = Color.Color8(0x40, 0x38, 0x20);
                var to = Color.Color8(0x10, 0x10, 8);
                DrawPolyline(new[] { new Vector2(x + 16, top + i + 0.5f) * unit, new Vector2(x + 16 + width - 1 - i, top + i + 0.5f) * unit }, from.Lerp(to, 0.5f), unit.Y);
            }
        }
        void Button(int id, float bx, float by)
        {
            if (_buttons != null) DrawTextureRectRegion(_buttons, new Rect2(new Vector2(bx, by) * unit, new Vector2(16, 16) * unit), new Rect2((id & 3) * 16, (id & 4) * 4 + 128, 16, 16));
        }
        Button(4, x + 8, 15);
        Button(5, x + 64, 15);
        Button(6, x + 8, 33);
        Button(7, x + 8, 52);
        Button(2, x + 8, 70);
        // Which words are lit by the state: BACK and NEXT, ROTATION, ZOOM, MENU.
        bool[] lit = _commands switch
        {
            0 => new[] { true, true, true, true, true },
            2 => new[] { false, false, false, true, true },
            3 => new[] { false, false, true, false, true },
            _ => new[] { false, false, false, false, false },
        };
        if (_labels != null && _widths.Length > 0 && _legend.Length >= 5)
        {
            var dim = new Color(0.25f, 0.25f, 0.25f);
            BattleHud.Letter(this, _labels, _widths, _columns, _kerning, _legend[0], new Vector2(x + 28, 18), unit, lit[0] ? null : dim);
            BattleHud.Letter(this, _labels, _widths, _columns, _kerning, _legend[1], new Vector2(x + 84, 18), unit, lit[1] ? null : dim);
            BattleHud.Letter(this, _labels, _widths, _columns, _kerning, _legend[2], new Vector2(x + 28, 36), unit, lit[2] ? null : dim);
            BattleHud.Letter(this, _labels, _widths, _columns, _kerning, _legend[3], new Vector2(x + 28, 54), unit, lit[3] ? null : dim);
            BattleHud.Letter(this, _labels, _widths, _columns, _kerning, _legend[4], new Vector2(x + 28, 72), unit, lit[4] ? null : dim);
        }
        DrawCompass(new Vector2(x + 62, 184), unit);
        // The chosen room's name in its box, rising with the legend from y 224 to 160 at x 60.
        if (_system != null && RoomName.Length > 0)
        {
            float y = 160 + -_ui / 2f;
            DrawSetTransform(FieldView.Pin(Size, 1).Offset, 0, unit);
            DrawString(_system, new Vector2(65, y + 3 + 12), RoomName, HorizontalAlignment.Left, -1, 12);
            DrawSetTransform(FieldView.Pin(Size, 1).Offset);
        }
    }

    /// <summary>
    /// The compass (MENU5 0x801076B0) at <paramref name="at"/>, turned by the yaw: eight spokes 86 by 6 (the four main
    /// ones from row 128 of the second HUD page, the others from 132), four letters at the corners of a square 78 wide
    /// turned an eighth further (N lit), each on its marker, and the needle 30 by 34 in the middle.
    /// </summary>
    private void DrawCompass(Vector2 at, Vector2 unit)
    {
        float angle = _yaw * Mathf.Tau / 4096;
        void Quad(Texture2D? texture, Vector2 centre, float halfW, float halfH, float turn, Rect2 source, Color? tint = null)
        {
            if (texture == null) return;
            var right = new Vector2(Mathf.Cos(turn), Mathf.Sin(turn));
            var down = new Vector2(-Mathf.Sin(turn), Mathf.Cos(turn));
            var corners = new[] { centre - right * halfW - down * halfH, centre + right * halfW - down * halfH, centre + right * halfW + down * halfH, centre - right * halfW + down * halfH };
            var size = texture.GetSize();
            var uvs = new[] { source.Position / size, new Vector2(source.End.X, source.Position.Y) / size, source.End / size, new Vector2(source.Position.X, source.End.Y) / size };
            var c = tint ?? Colors.White;
            DrawPrimitive(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(corners, p => p * unit)), new[] { c, c, c, c }, uvs, texture);
        }
        for (int i = 0; i < 8; i++)
            Quad(_compassRim, at, 21.5f, 1.5f, angle + i * Mathf.Tau / 16, (i & 3) == 0 ? new Rect2(2, 128, 43, 3) : new Rect2(2, 132, 43, 3), new Color(1, 1, 1, 0.5f));
        float[] letters = { 0x30, 0x40, 0x48, 0x38 };
        for (int i = 0; i < 4; i++)
        {
            var corner = at + Rotate(new Vector2(-19.5f, -19.5f), angle + Mathf.Tau / 8 + i * Mathf.Tau / 4);
            Quad(i == 0 ? _compassMarkLit : _compassRing, corner, 6.5f, 6.5f, 0, new Rect2(1, 137, 13, 13), new Color(1, 1, 1, 0.5f));
            Quad(i == 0 ? _compassNorth : _compassMark, corner, 3.5f, 3.5f, 0, new Rect2(letters[i], 128, 7, 7));
        }
        // The needle stands at a corner of a square 15 wide about the middle, turned from the view the map opened on.
        float needle = (_yaw - _openedYaw) * Mathf.Tau / 4096;
        Quad(_compassNeedle, at + Rotate(new Vector2(-7.5f, -7.5f), needle + Mathf.Tau / 8), 7.5f, 8.5f, needle, new Rect2(0x14, 0x88, 15, 17), new Color(1, 1, 1, 0.5f));
    }

    private static Vector2 Rotate(Vector2 v, float turn) => new(v.X * Mathf.Cos(turn) - v.Y * Mathf.Sin(turn), v.X * Mathf.Sin(turn) + v.Y * Mathf.Cos(turn));
}
