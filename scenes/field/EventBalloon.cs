// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// A retail speech balloon, drawn in the dialogue layer's 320-pixel screen space behind the text. Its shape comes
/// from the field engine's balloon table: an outline scaled across the box, one tail part chosen by the box, and
/// polygons filled with the paper tile (repeating every 64 screen pixels), with a grey outline and a black shadow
/// two pixels down and right. It opens and closes by scaling about its centre, as the retail box does.
/// </summary>
public partial class EventBalloon : Control
{
    private static readonly Color Outline = Color.Color8(48, 48, 48);

    private readonly List<Vector2> _points = new();
    private readonly List<int[]> _polygons = new();
    private readonly List<(int From, int To)> _edges = new();
    private Texture2D _paper = null!;
    private Rect2 _box;
    private int _tick, _frames;
    public bool Closing;

    /// <summary>Whether the balloon has finished opening, so its text may print.</summary>
    public bool Open => !Closing && _tick >= _frames;
    public bool Closed => Closing && _tick <= 0;

    /// <summary>
    /// Sets how many frames the balloon takes to open or close (1d), keeping how far it has gone: a script that
    /// closes a box and then gives it one frame has it gone at once (MAP010's last line, Ep. 3 6:23).
    /// </summary>
    public void Retime(int frames)
    {
        frames = Math.Max(1, frames);
        _tick = (int)MathF.Ceiling(_tick * frames / (float)_frames);
        _frames = frames;
    }

    /// <param name="shape">The balloon shape: parts, vertices and per-part polygons.</param>
    /// <param name="flags">The retail box flags: flip (bits 6-7), shape (8-11) and tail part (12-15).</param>
    public void Configure(GDictionary shape, Texture2D paper, Rect2 box, int flags, int openFrames)
    {
        MouseFilter = MouseFilterEnum.Ignore;
        TextureRepeat = TextureRepeatEnum.Enabled;
        TextureFilter = TextureFilterEnum.Nearest;
        _paper = paper;
        _box = box;
        _frames = Math.Max(1, openFrames);
        int flip = (flags >> 6) & 3, tail = (flags >> 12) & 15;
        var parts = shape["parts"].AsGodotArray();
        var vertices = shape["vertices"].AsGodotArray();
        var polygons = shape["polygons"].AsGodotArray();
        int total = vertices.Count, outline = parts[0].AsGodotArray()[2].AsInt32();
        // Flip bit 0 mirrors y and bit 1 mirrors x, in the shape's 0-255 box space.
        Vector2I Flip(int x, int y) => new((flip & 2) != 0 ? 255 - x : x, (flip & 1) != 0 ? 255 - y : y);
        for (int i = 0; i < outline; i++)
        {
            var v = vertices[i].AsGodotArray();
            var p = Flip(v[0].AsInt32(), v[1].AsInt32());
            _points.Add(new Vector2(p.X * box.Size.X / 256f, p.Y * box.Size.Y / 256f));
        }
        bool hasTail = tail >= 1 && tail < parts.Count;
        int attach = hasTail ? parts[tail].AsGodotArray()[0].AsInt32() : -1;
        // Tail vertices are signed offsets from the attach vertex, also in box space.
        var anchor = attach >= 0 ? vertices[attach].AsGodotArray() : null;
        for (int i = outline; i < total; i++)
        {
            var v = vertices[i].AsGodotArray();
            var o = Flip(v[0].AsInt32(), v[1].AsInt32());
            int dx = (sbyte)(byte)o.X, dy = (sbyte)(byte)o.Y;
            var a = anchor != null ? Flip(anchor[0].AsInt32(), anchor[1].AsInt32()) : Vector2I.Zero;
            _points.Add(new Vector2((dx + a.X) * box.Size.X / 256f, (dy + a.Y) * box.Size.Y / 256f));
        }
        foreach (var polygon in polygons[0].AsGodotArray()) _polygons.Add(Indices(polygon.AsGodotArray()));
        if (hasTail) foreach (var polygon in polygons[tail].AsGodotArray()) _polygons.Add(Indices(polygon.AsGodotArray()));

        // The outline runs round the body; at the tail's attach vertex it detours through the tail's vertices.
        int tailStart = outline;
        for (int part = 1; part < tail && hasTail; part++) tailStart += parts[part].AsGodotArray()[2].AsInt32();
        int resume = hasTail ? parts[tail].AsGodotArray()[1].AsInt32() : -1;
        int tailCount = hasTail ? parts[tail].AsGodotArray()[2].AsInt32() : 0;
        for (int n = 0; n < outline; n++)
        {
            int from = n;
            if (n == attach)
            {
                for (int k = 0; k < tailCount; k++) { _edges.Add((from, tailStart + k)); from = tailStart + k; }
                _edges.Add((from, resume));
                n = resume;
                if (n == 0) break;
                from = n;
            }
            _edges.Add((from, (n + 1) % outline));
        }
        Position = box.Position;
        Size = box.Size;
        QueueRedraw();
    }

    private static int[] Indices(GArray polygon)
    {
        var indices = new List<int> { polygon[0].AsInt32(), polygon[1].AsInt32(), polygon[2].AsInt32() };
        if (polygon[3].AsInt32() != 0xFF) indices.Add(polygon[3].AsInt32());
        return indices.ToArray();
    }

    /// <summary>Advances the opening or closing scale by one event frame.</summary>
    public void Step()
    {
        _tick = Closing ? Math.Max(0, _tick - 1) : Math.Min(_frames, _tick + 1);
        QueueRedraw();
    }

    public override void _Draw()
    {
        float scale = Mathf.Sin(_tick / (float)_frames * Mathf.Pi / 2);
        if (scale <= 0) return;
        var centre = _box.Size / 2;
        Vector2 Place(int index) => centre + (_points[index] - centre) * scale;
        foreach (var polygon in _polygons)
        {
            var shadow = new Vector2[polygon.Length];
            for (int i = 0; i < polygon.Length; i++) shadow[i] = Place(polygon[i]) + new Vector2(2, 2);
            DrawColoredPolygon(Fan(shadow), Colors.Black);
        }
        foreach (var polygon in _polygons)
        {
            var points = new Vector2[polygon.Length];
            var uv = new Vector2[polygon.Length];
            for (int i = 0; i < polygon.Length; i++)
            {
                points[i] = Place(polygon[i]);
                // The paper repeats every 64 screen pixels, aligned to the screen.
                uv[i] = (points[i] + _box.Position) / 64f;
            }
            var fan = Fan(points);
            var fanUv = Fan(uv);
            DrawPolygon(fan, new[] { Colors.White }, fanUv, _paper);
        }
        foreach (var (from, to) in _edges) DrawLine(Place(from), Place(to), Outline, 1);
    }

    /// <summary>The retail quad order (0, 1, 3, 2 around the edge) as a polygon outline; triangles pass through.</summary>
    private static Vector2[] Fan(Vector2[] points) => points.Length == 4 ? new[] { points[0], points[1], points[3], points[2] } : points;
}
