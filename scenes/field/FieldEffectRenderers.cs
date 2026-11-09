// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The PLG renderers: geometry an effect's code draws beside its particles, from an emitter record (block 5) read with
/// the effect's curves at the renderer's age. The installer names each renderer's template by the shape of its code;
/// these are the templates: rings closed (16 columns) and open (9), grids, hemispherical surfaces (plain, mirrored and
/// twisting), a split tetrahedron, spline trails and lightning strands. Positions are map units (y down), angles
/// 4096 a turn; a renderer's lifetime counts down to its end, 0 lasting until it is stopped.
/// </summary>
public sealed partial class FieldEffect
{
    private sealed class Renderer
    {
        public string Kind = "";
        public int[] E = Array.Empty<int>();
        public int Lifetime, Age, Param;
        public bool Alive = true;
        // The texture animation (a sprite program over a frame table).
        public int Table, Program, Pc, TimeLeft, Record = -1;
        // Rings and surfaces.
        public int Radius, RadiusSpeed, Height, HeightSpeed, Angle;
        public int[] Factors = Array.Empty<int>();
        // Grids.
        public int XSpacing, XSpeed, ZSpacing, ZSpeed, XPhase, ZPhase, UScroll, VScroll;
        // Meshes.
        public Vector3 Rotation;
        // Trails and strands: four spline points each.
        public List<Vector3[]> Splines = new();
        public List<(int Steps, int Samples, int Collapse, int Current, Vector3[] Points, long Progress, long Speed)> Trails = new();
        public int[] Fade = Array.Empty<int>();
        public int Repick;
        public readonly List<Triangle> Triangles = new();
    }

    private void StartRenderer(Instance instance, int index, int lifetime, int emitter, int parameter)
    {
        if (index >= _rendererKinds.Count || _rendererKinds[index].Length == 0 || emitter >= _emitters.Count) return;
        var e = _emitters[emitter];
        string kind = _rendererKinds[index];
        var r = new Renderer { Kind = kind, E = e, Lifetime = lifetime & 0xFF, Param = parameter };
        string template = kind.Split(':')[0];
        // Closed rings and surfaces take their texture animation from bytes 0xca and 0xcb; the others from 1 and 0.
        (r.Table, r.Program) = template is "closed_ring" or "surface" or "mirrored_surface" or "rotating_surface" ? (e[0xCA], e[0xCB]) : (e[1], e[0]);
        if (template is "surface" or "mirrored_surface" or "rotating_surface")
        {
            // Each row's brightness: the transparency curve spread down the rows, doubled, or full.
            int rows = template == "mirrored_surface" ? 9 : 5, curve = e[0xC7];
            r.Factors = new int[rows];
            for (int i = 0; i < rows; i++)
            {
                if (curve > 0 && curve <= _curves.Count)
                {
                    int length = _curves[curve - 1].Length, last = length >= 2 ? length - 1 : length;
                    int value = _curves[curve - 1][Math.Min(_curves[curve - 1].Length - 1, last * i / (rows - 1))] * 2;
                    r.Factors[i] = value == 256 ? 255 : value;
                }
                else r.Factors[i] = 255;
            }
        }
        if (template == "trail")
        {
            int samples = kind.EndsWith(":5") ? 5 : 9, curve = e[0xC7];
            r.Fade = new int[samples];
            for (int j = 0; j < samples; j++)
                r.Fade[j] = curve > 0 && curve <= _curves.Count && _curves[curve - 1].Length > 0
                    ? _curves[curve - 1][Math.Min(_curves[curve - 1].Length - 1, j * Math.Max(1, _curves[curve - 1].Length - 1) / (samples - 1))]
                    : 128;
        }
        instance.Renderers.Add(r);
    }

    private void TickRenderers(Instance instance)
    {
        var camera = GetViewport()?.GetCamera3D();
        foreach (var r in instance.Renderers.ToArray())
        {
            r.Triangles.Clear();
            StepTexture(r);
            switch (r.Kind.Split(':')[0])
            {
                case "closed_ring": case "ring": RenderRing(instance, r); break;
                case "grid": RenderGrid(instance, r); break;
                case "surface": case "mirrored_surface": RenderSurface(instance, r); break;
                case "rotating_surface": RenderRotating(instance, r); break;
                case "mesh": RenderMesh(instance, r); break;
                case "trail": RenderTrail(instance, r, camera); break;
                case "strands": RenderStrands(instance, r, camera); break;
            }
            r.Age++;
            if (r.Lifetime > 0 && --r.Lifetime == 0) r.Alive = false;
            if (!r.Alive) instance.Renderers.Remove(r);
        }
    }

    /// <summary>The texture animation (0x800D6D24): the frame table's entry the program shows, its first record.</summary>
    private void StepTexture(Renderer r)
    {
        if (r.TimeLeft != 0) { r.TimeLeft -= 2; if (r.TimeLeft > 0) return; }
        if (r.Program >= _programs.Length) return;
        var words = _programs[r.Program];
        for (int guard = 0; guard < 64 && r.Pc < words.Length; guard++)
        {
            uint word = words[r.Pc];
            switch (word >> 28)
            {
                case 0:
                    if (r.Table < _frames.Length && _frames[r.Table] is var table && (word & 0xFFFF) < table.Length)
                        r.Record = table[(int)(word & 0xFFFF)][1];
                    r.TimeLeft = (int)(word >> 16) & 0x3FFF;
                    r.Pc++;
                    return;
                case 2: r.Pc = 0; continue;
                case 3: return;
                default: r.Pc++; continue;
            }
        }
    }

    // Emitter-record reads, as the renderers make them: a halfword range lerped by a curve at the renderer's age.
    private static int S16(int[] e, int at) => (short)(e[at] | e[at + 1] << 8);
    private int At(Renderer r, int at, int selector, int time = -1) => Lerp(S16(r.E, at), S16(r.E, at + 2), Curve(r.E[selector], time < 0 ? r.Age : time));
    private Vector3 Triple(Renderer r, int at, int selector, int time = -1) => new(At(r, at, selector, time), At(r, at + 4, selector, time), At(r, at + 8, selector, time));
    private (int, int) Pair(Renderer r, int at, int selector, int time = -1) => (At(r, at, selector, time), At(r, at + 4, selector, time));
    private int RandomBetween(int a, int b) => a == b ? a : _random.RandiRange(Math.Min(a, b), Math.Max(a, b));
    private static float Sin(float angle) => Mathf.Sin(angle * Mathf.Tau / 4096f);
    private static float Cos(float angle) => Mathf.Cos(angle * Mathf.Tau / 4096f);

    /// <summary>
    /// The renderer's centre: its origin range plus, by the record's flags, the target or its anchor; with flag 0x20000
    /// it is drawn in the anchor's frame (here, moved with the anchor).
    /// </summary>
    private Vector3 Centre(Instance instance, Renderer r, int time = -1)
    {
        var centre = Triple(r, 0x18, 8, time);
        int flags = r.E[4] | r.E[5] << 8 | r.E[6] << 16;
        if ((flags & 7) == 1) centre += instance.Target.Position();
        if ((flags & 7) == 2 || (flags & 0x20000) != 0) centre += AnchorAt(instance, r.E[0xC8]);
        return centre;
    }

    /// <summary>A colour channel of the record's colour curves (0xc4 to 0xc6): the curve doubled (to 255), or 128 without one.</summary>
    private int Doubled(Renderer r, int channel) => r.E[0xC4 + channel] == 0 ? 128 : Math.Min(255, Curve(r.E[0xC4 + channel], r.Age) * 2);

    /// <summary>A row colour: a brightness times each colour curve over 128 (the curve absent, the brightness whole).</summary>
    private Color RowColour(Renderer r, int factor)
    {
        float Channel(int c) => (r.E[0xC4 + c] == 0 ? factor : factor * Curve(r.E[0xC4 + c], r.Age) / 128) / 128f;
        return new Color(Channel(0), Channel(1), Channel(2));
    }

    private int RecordBlend(int record) => record >= 0 && record < _records.Length ? _records[record].Blend : 1;

    /// <summary>
    /// Adds a lattice of rows by columns as quads: each row its colour, texture fractions per point; closed lattices
    /// join their last column to the first.
    /// </summary>
    /// <param name="uv">A corner's texture place by its row, its quad's column and its side (0 left, 1 right).</param>
    private void Lattice(Renderer r, Vector3[,] points, Color[] rows, Func<int, int, int, Vector2> uv, bool closed, int texture = 0, int mode = -1)
    {
        int rowCount = points.GetLength(0), columns = points.GetLength(1), blend = mode >= 0 ? mode : RecordBlend(r.Record);
        if (texture == 0 && r.Record < 0) return;
        for (int row = 0; row + 1 < rowCount; row++)
            for (int column = 0; column < (closed ? columns : columns - 1); column++)
            {
                int next = (column + 1) % columns;
                var p = new[] { EventActor.RetailSpace * points[row, column], EventActor.RetailSpace * points[row, next], EventActor.RetailSpace * points[row + 1, column], EventActor.RetailSpace * points[row + 1, next] };
                var f = new[] { uv(row, column, 0), uv(row, column, 1), uv(row + 1, column, 0), uv(row + 1, column, 1) };
                Vector2[] coordinates = texture == 0 ? Fractions(r.Record, f) : Page(texture, f);
                Color top = Tint(blend, rows[row]), bottom = Tint(blend, rows[row + 1]);
                r.Triangles.Add(new Triangle(texture, blend, p[0], p[1], p[2], coordinates[0], coordinates[1], coordinates[2], top, top, bottom));
                r.Triangles.Add(new Triangle(texture, blend, p[1], p[3], p[2], coordinates[1], coordinates[3], coordinates[2], top, bottom, bottom));
            }
    }

    private Vector2[] Fractions(int record, Vector2[] f)
    {
        var result = new Vector2[f.Length];
        for (int i = 0; i < f.Length; i++) result[i] = RecordUv(record, f[i].X, f[i].Y, f[i].X, f[i].Y)[0];
        return result;
    }

    private Vector2[] Page(int texture, Vector2[] texels)
    {
        var size = _pageTextures[texture - 1]?.GetSize() ?? Vector2.One;
        var result = new Vector2[texels.Length];
        for (int i = 0; i < texels.Length; i++) result[i] = texels[i] / size;
        return result;
    }

    private Color[] StandardRows(Renderer r)
    {
        var colour = _standardColours.Count > 0 ? _standardColours[r.E[0xC7] & 7] : new[] { 255, 255, 255 };
        return new[] { RowColour(r, colour[0]), RowColour(r, colour[1]), RowColour(r, colour[2]) };
    }

    /// <summary>
    /// Rings (closed: 16 columns all round; open: 9 over an arc): three rows that grow outward and upward by the
    /// record's speeds and accelerations, tilted by its angle, the middle row at its inner fraction; they turn by a
    /// random step each tick. A row's brightness is the standard colour's component for it.
    /// </summary>
    private void RenderRing(Instance instance, Renderer r)
    {
        bool closed = r.Kind.StartsWith("closed", StringComparison.Ordinal), transposed = r.Kind.EndsWith("transposed"), tiled = r.Kind.EndsWith("tiled");
        int textureColumns = r.Kind.EndsWith(":4") ? 4 : closed ? 16 : 8, columns = closed ? 16 : 9;
        float fraction = At(r, 0x30, 0x17) / 4096f;
        var centre = Centre(instance, r);
        var extent = Triple(r, 0x24, 9);
        var growth = Triple(r, 0x4C, 0xD);
        var acceleration = Triple(r, 0x64, 0xE);
        var tilt = Triple(r, 0x34, 0xA);
        var (slow, fast) = Pair(r, 0xA4, 0xC);
        r.RadiusSpeed += (int)acceleration.X;
        r.Radius += (int)growth.X + r.RadiusSpeed;
        r.HeightSpeed += (int)acceleration.Y;
        r.Height += (int)growth.Y + r.HeightSpeed;
        float r0 = r.Radius + extent.X, r2 = r.Height + extent.Y, s = Sin(tilt.X), c = Cos(tilt.X);
        float h2 = r2 * s, r1 = r2 * fraction, h1 = r1 * s;
        r1 = r1 * c + r0;
        r2 = r2 * c + r0;
        float[] radius = { r0, r1, r2 }, height = { 0, h1, h2 };
        r.Angle += RandomBetween(slow, fast);
        var span = closed ? Vector3.Zero : Triple(r, 0x40, 0xB);
        var points = new Vector3[3, columns];
        for (int k = 0; k < columns; k++)
        {
            float angle = closed ? r.Angle + k * 256 : r.Angle + span.X * k / 8 - span.Y / 2 + span.Y * k / 8;
            for (int row = 0; row < 3; row++) points[row, k] = centre + new Vector3(radius[row] * Cos(angle), height[row], radius[row] * Sin(angle));
        }
        float[] rowV = { 0, fraction, 1 };
        // The texture runs round the ring (repeating every 4 columns where the template says), along its rows when
        // transposed, or whole over each quad when tiled.
        int spread = closed ? 16 : 8;
        Lattice(r, points, StandardRows(r), (row, quad, side) =>
            tiled ? new Vector2(side, rowV[row]) :
            transposed ? new Vector2(rowV[row], (quad + side) / (float)spread) :
            new Vector2((quad % textureColumns + side) / (float)textureColumns, rowV[row]), closed);
        if (r.E[3] > 0) Fire(instance, r.E[3] - 1, 0, new Particle { Position = centre * 4096 }, r.Age);
    }

    /// <summary>
    /// Grids: 9 by 9 points about the centre, spaced by growing spacings, rippled by sines along each row and column,
    /// drawn flat-coloured with a scrolling window of a VRAM page (the record's page, palette row and depth; the window
    /// from 0xac and 0xb0, its size at 0x94 and 0x98, its scroll speed at 0x9c).
    /// </summary>
    private void RenderGrid(Instance instance, Renderer r)
    {
        var e = r.E;
        var centre = Centre(instance, r);
        var extent = Triple(r, 0x24, 9);
        var growth = Triple(r, 0x4C, 0xD);
        var step = Triple(r, 0x58, 0xD);
        var acceleration = Triple(r, 0x64, 0xE);
        var jerk = Triple(r, 0x70, 0xE);
        var ripple = Triple(r, 0x34, 0xA);
        var phaseStep = Triple(r, 0x40, 0xB);
        var (rowPhaseX, rowPhaseZ) = Pair(r, 0xA4, 0xC);
        var (scrollU, scrollV) = Pair(r, 0x9C, 0x13);
        r.XSpeed += (int)acceleration.X;
        r.XSpacing += (int)growth.X + r.XSpeed;
        r.ZSpeed += (int)acceleration.Y;
        r.ZSpacing += (int)growth.Y + r.ZSpeed;
        r.XPhase += (int)phaseStep.X;
        r.ZPhase += (int)phaseStep.Y;
        float[] x = new float[9], z = new float[9];
        float offset = 0, spacing = extent.X + r.XSpacing, accel = 0;
        for (int i = 0; i < 5; i++) { x[4 - i] = centre.X - offset; x[4 + i] = centre.X + offset; offset += spacing; spacing += accel + step.X; accel += jerk.X; }
        offset = 0; spacing = extent.Y + r.ZSpacing; accel = 0;
        for (int i = 0; i < 5; i++) { z[4 - i] = centre.Z - offset; z[4 + i] = centre.Z + offset; offset += spacing; spacing += accel + step.Y; accel += jerk.Y; }
        var points = new Vector3[9, 9];
        float px = r.XPhase, pz = r.ZPhase;
        for (int row = 0; row < 9; row++)
        {
            float ax = px, az = pz;
            px += rowPhaseX; pz += rowPhaseZ;
            for (int column = 0; column < 9; column++)
            {
                points[row, column] = new Vector3(x[column] + Sin(ax) * ripple.X, centre.Y, z[row] + Cos(az) * ripple.Y);
                ax += phaseStep.X; az += phaseStep.Y;
            }
        }
        int width = Math.Max(1, S16(e, 0x94)), height = Math.Max(1, S16(e, 0x98));
        int u = S16(e, 0xAC) + r.UScroll, v = S16(e, 0xB0) + r.VScroll;
        r.UScroll = (r.UScroll + scrollU) % width;
        r.VScroll = (r.VScroll + scrollV) % height;
        string key = $"{e[0xCA] & 7}_{e[0xCB] & 7}_{(e[0xC7] >> 2) & 1}";
        if (!_pages.TryGetValue(key, out int texture)) return;
        var colour = new Color(Doubled(r, 0) / 128f, Doubled(r, 1) / 128f, Doubled(r, 2) / 128f);
        var rows = new Color[9];
        Array.Fill(rows, colour);
        Lattice(r, points, rows, (row, quad, side) => new Vector2(u + width * (quad + side) / 8f, v + height * row / 8f), false, texture, e[0xC7] & 3);
    }

    /// <summary>A hemisphere's point: ring <paramref name="ring"/> (0 the rim, 4 the pole, 22.5 degrees apart) and column k of 16.</summary>
    private static Vector3 Hemisphere(int ring, int k)
    {
        float elevation = ring * Mathf.Pi / 8, around = k * Mathf.Pi / 8;
        return new Vector3(Mathf.Cos(elevation) * Mathf.Cos(around), Mathf.Sin(elevation), Mathf.Cos(elevation) * Mathf.Sin(around));
    }

    /// <summary>
    /// Surfaces: a hemisphere of 5 rings by 16 (mirrored: 9 rings, its lower half reflected), scaled by a growing radius
    /// and height that step from ring to ring; a ring's brightness comes from the transparency curve spread down them.
    /// </summary>
    private void RenderSurface(Instance instance, Renderer r)
    {
        bool mirrored = r.Kind.StartsWith("mirrored", StringComparison.Ordinal);
        int rows = mirrored ? 9 : 5, textureColumns = r.Kind.EndsWith(":4") ? 4 : 16;
        var centre = Centre(instance, r);
        var extent = Triple(r, 0x24, 9);
        var growth = Triple(r, 0x4C, 0xD);
        var step = Triple(r, 0x58, 0xD);
        var acceleration = Triple(r, 0x64, 0xE);
        var jerk = Triple(r, 0x70, 0xE);
        r.HeightSpeed += (int)acceleration.Y;
        r.Height += (int)growth.Y + r.HeightSpeed;
        r.RadiusSpeed += (int)acceleration.X;
        r.Radius += (int)growth.X + r.RadiusSpeed;
        float height = r.Height + extent.Y, radius = r.Radius + extent.X, radiusStep = 0, heightStep = 0;
        var points = new Vector3[rows, 16];
        var colours = new Color[rows];
        for (int row = 0; row < rows; row++)
        {
            int ring = mirrored ? (row < 5 ? 4 - row : row - 4) : row;
            float sign = mirrored && row >= 5 ? -1 : 1;
            for (int k = 0; k < 16; k++)
            {
                var unit = Hemisphere(ring, k);
                points[row, k] = centre + new Vector3(unit.X * radius, sign * unit.Y * height, unit.Z * radius);
            }
            radiusStep += jerk.X; radius += radiusStep + step.X;
            heightStep += jerk.Y; height += heightStep + step.Y;
            colours[row] = RowColour(r, r.Factors[row]);
        }
        Lattice(r, points, colours, (row, quad, side) => new Vector2((quad % textureColumns + side) / (float)textureColumns, row / (float)(rows - 1)), true);
    }

    /// <summary>
    /// Rotating surfaces: five rings of 16 stacked by a height that steps from ring to ring, each turned further by
    /// the record's angle, speed and acceleration, a tube twisting as it grows.
    /// </summary>
    private void RenderRotating(Instance instance, Renderer r)
    {
        int textureColumns = r.Kind.EndsWith(":4") ? 4 : 16;
        var centre = Centre(instance, r);
        var extent = Triple(r, 0x24, 9);
        var growth = Triple(r, 0x4C, 0xD);
        var step = Triple(r, 0x58, 0xD);
        var acceleration = Triple(r, 0x64, 0xE);
        var jerk = Triple(r, 0x70, 0xE);
        var twist = Triple(r, 0x40, 0xB);
        var (slow, fast) = Pair(r, 0xA4, 0xC);
        r.HeightSpeed += (int)acceleration.Y;
        r.Height += (int)growth.Y + r.HeightSpeed;
        r.RadiusSpeed += (int)acceleration.X;
        r.Radius += (int)growth.X + r.RadiusSpeed;
        float height = r.Height + extent.Y, radius = r.Radius + extent.X;
        r.Angle += RandomBetween(slow, fast);
        float angle = r.Angle, angleSpeed = 0, angleAccel = 0, heightStep = 0, radiusStep = 0, heightAt = 0;
        var points = new Vector3[5, 16];
        var colours = new Color[5];
        for (int row = 0; row < 5; row++)
        {
            for (int k = 0; k < 16; k++) points[row, k] = centre + new Vector3(radius * Cos(angle + k * 256), heightAt, radius * Sin(angle + k * 256));
            heightStep += jerk.Y; height += heightStep + step.Y; heightAt += height;
            radiusStep += jerk.X; radius += radiusStep + step.X;
            angleAccel += twist.Z; angleSpeed += angleAccel + twist.Y; angle += angleSpeed + twist.X;
            colours[row] = RowColour(r, r.Factors[row]);
        }
        Lattice(r, points, colours, (row, quad, side) => new Vector2((quad % textureColumns + side) / (float)textureColumns, row / 4f), true);
    }

    /// <summary>
    /// Meshes: a tetrahedron (apex up, three corners below) whose corners scale by the record's extent and whose edge
    /// midpoints by its second extent (0x88), each face split in four; it turns by the record's angles and speeds. The
    /// corners take the colour curves, the midpoints those at 0x4c, 0x50 and 0x54.
    /// </summary>
    private void RenderMesh(Instance instance, Renderer r)
    {
        if (r.Record < 0) return;
        var centre = Centre(instance, r);
        var corners = Triple(r, 0x24, 9);
        var middles = Triple(r, 0x88, 0x10);
        var baseAngles = Triple(r, 0x34, 0xA);
        r.Rotation += Triple(r, 0x40, 0xB);
        var angles = r.Rotation + baseAngles;
        float rim = 0.7698f;
        var tetra = new[]
        {
            new Vector3(0, -1, 0), new Vector3(rim, 1 / 3f, 0), new Vector3(rim * Cos(1365.33f), 1 / 3f, rim * Sin(1365.33f)), new Vector3(rim * Cos(2730.67f), 1 / 3f, rim * Sin(2730.67f)),
        };
        Vector3 Corner(int i) => tetra[i] * corners;
        Vector3 Middle(int a, int b) => (tetra[a] + tetra[b]) / 2 * middles;
        var basis = new Basis(Vector3.Forward, angles.Z * Mathf.Tau / 4096) * new Basis(Vector3.Up, angles.Y * Mathf.Tau / 4096) * new Basis(Vector3.Right, angles.X * Mathf.Tau / 4096);
        Vector3 Place(Vector3 local) => EventActor.RetailSpace * (centre + basis * local);
        var cornerColour = new Color(Doubled(r, 0) / 128f, Doubled(r, 1) / 128f, Doubled(r, 2) / 128f);
        float Middling(int at) => (S16(r.E, at) == 0 ? 128 : Math.Min(255, Curve(S16(r.E, at), r.Age) * 2)) / 128f;
        var middleColour = new Color(Middling(0x4C), Middling(0x50), Middling(0x54));
        int blend = RecordBlend(r.Record);
        Color cc = Tint(blend, cornerColour), mc = Tint(blend, middleColour);
        var uv = Fractions(r.Record, new[] { new Vector2(0.5f, 0), new Vector2(0.25f, 0.5f), new Vector2(0.75f, 0.5f), new Vector2(0, 1), new Vector2(0.5f, 1), new Vector2(1, 1) });
        foreach (var (a, b, c) in new[] { (0, 1, 2), (0, 2, 3), (0, 3, 1), (1, 3, 2) })
        {
            Vector3 pa = Place(Corner(a)), pb = Place(Corner(b)), pc = Place(Corner(c)), ab = Place(Middle(a, b)), bc = Place(Middle(b, c)), ca = Place(Middle(c, a));
            r.Triangles.Add(new Triangle(0, blend, pa, ab, ca, uv[0], uv[1], uv[2], cc, mc, mc));
            r.Triangles.Add(new Triangle(0, blend, ab, bc, ca, uv[1], uv[4], uv[2], mc, mc, mc));
            r.Triangles.Add(new Triangle(0, blend, ab, pb, bc, uv[1], uv[3], uv[4], mc, cc, mc));
            r.Triangles.Add(new Triangle(0, blend, ca, bc, pc, uv[2], uv[4], uv[5], mc, mc, cc));
        }
        if (r.E[3] > 0) Fire(instance, r.E[3] - 1, 0, new Particle { Position = centre * 4096 }, r.Age);
    }

    /// <summary>
    /// The start points of trails and strands, by the record's spawn shape (flags 0x38): an ellipsoid, a box's faces
    /// or corners, or points round a polygon; then the end points, at the target range plus the target anchor or aim
    /// point (flags 0x1c0), give or take its jitter.
    /// </summary>
    private void PickEnds(Instance instance, Renderer r, int count, int time)
    {
        int flags = r.E[4] | r.E[5] << 8 | r.E[6] << 16;
        var centre = Centre(instance, r, time);
        var extent = Triple(r, 0x24, 9, time);
        var target = Triple(r, 0x7C, 0xF, time);
        var jitter = Triple(r, 0x88, 0x10, time);
        if ((flags & 0x1C0) == 0xC0) target += AnchorAt(instance, r.E[0xC9]);
        else if ((flags & 0x1C0) == 0x80) target += instance.Target.Position();
        r.Splines.Clear();
        for (int i = 0; i < count; i++)
        {
            Vector3 start;
            switch (flags & 0x38)
            {
                case 0:
                {
                    float a = _random.Randf() * 4096, b = _random.Randf() * 4096;
                    start = new Vector3(extent.X * Cos(b) * Cos(a), extent.Y * Cos(b) * Sin(a), extent.Z * Sin(b));
                    break;
                }
                case 8:
                {
                    start = new Vector3(Rand(-extent.X, extent.X), Rand(-extent.Y, extent.Y), Rand(-extent.Z, extent.Z));
                    int axis = _random.RandiRange(0, 2);
                    start[axis] = (_random.Randf() < 0.5f ? -1 : 1) * extent[axis];
                    break;
                }
                default:
                {
                    int sides = (flags & 0x38) == 48 ? Math.Max(1, r.E[0xCA]) : 4;
                    float angle = _random.RandiRange(0, sides - 1) * 4096f / sides + 512;
                    start = new Vector3(Cos(angle) * extent.X, 0, Sin(angle) * extent.Z);
                    break;
                }
            }
            var end = target + new Vector3(Rand(-jitter.X, jitter.X), Rand(-jitter.Y, jitter.Y), Rand(-jitter.Z, jitter.Z));
            r.Splines.Add(new[] { centre + start, Vector3.Zero, Vector3.Zero, end });
        }
    }

    /// <summary>
    /// The control points between each spline's ends: along the line by fractions of its length (0xa4, with a random
    /// share of 0x4c), out from it by radii (0x94 and 0x98, plus random) at angles (0x34 give or take 0x40).
    /// </summary>
    private void PickControls(Renderer r, int time)
    {
        foreach (var points in r.Splines)
        {
            var line = points[3] - points[0];
            float length = line.Length();
            var (along0, along1) = Pair(r, 0xA4, 0xC, time);
            var (radius0, spread0) = Pair(r, 0x94, 0x12, time);
            var (radius1, spread1) = Pair(r, 0x98, 0x13, time);
            var angles = Triple(r, 0x34, 0xA, time);
            var spreads = Triple(r, 0x40, 0xB, time);
            var shares = Triple(r, 0x4C, 0xD, time);
            float z0 = length * (along0 + (shares.X != 0 ? Rand(0, shares.X) - shares.X / 2 : 0)) / 256;
            float z1 = length + length * (along1 + (shares.Y != 0 ? Rand(0, shares.Y) - shares.Y / 2 : 0)) / 256;
            float a0 = angles.X + (spreads.X != 0 ? Rand(-spreads.X, spreads.X) : 0), a1 = angles.Y + (spreads.Y != 0 ? Rand(-spreads.Y, spreads.Y) : 0);
            float rr0 = radius0 + (spread0 != 0 ? Rand(0, spread0) : 0), rr1 = radius1 + (spread1 != 0 ? Rand(0, spread1) : 0);
            var forward = length > 0 ? line / length : Vector3.Forward;
            var side = forward.Cross(Mathf.Abs(forward.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            var up = side.Cross(forward);
            points[1] = points[0] + side * (Cos(a0) * rr0) + up * (Sin(a0) * rr0) + forward * z0;
            points[2] = points[0] + side * (Cos(a1) * rr1) + up * (Sin(a1) * rr1) + forward * z1;
        }
    }

    /// <summary>The spline through four points (0x800D1390): cubic, through them at t = -1, 0, 1 and 2.</summary>
    private static Vector3 Spline(Vector3[] p, float t)
    {
        float w0 = -t * (t - 1) * (t - 2) / 6, w1 = (t + 1) * (t - 1) * (t - 2) / 2, w2 = -(t + 1) * t * (t - 2) / 2, w3 = (t + 1) * t * (t - 1) / 6;
        return p[0] * w0 + p[1] * w1 + p[2] * w2 + p[3] * w3;
    }

    /// <summary>A ribbon along points, facing the camera, of a half width in screen pixels (of retail's 240 lines).</summary>
    private void Ribbon(Renderer r, Camera3D? camera, IList<Vector3> retailPoints, IList<float> halfWidths, IList<float> texture, IList<Color> colours, int mode, bool textured = true)
    {
        if (camera == null || retailPoints.Count < 2 || textured && r.Record < 0) return;
        var points = new Vector3[retailPoints.Count];
        for (int i = 0; i < points.Length; i++) points[i] = EventActor.RetailSpace * retailPoints[i];
        float pixel(Vector3 p) => 2 * Mathf.Max(0.01f, camera.GlobalPosition.DistanceTo(p)) * Mathf.Tan(Mathf.DegToRad(camera.Fov) / 2) / 240;
        var view = camera.GlobalBasis.Z;
        Vector3 Side(int i)
        {
            var along = points[Math.Min(points.Length - 1, i + 1)] - points[Math.Max(0, i - 1)];
            var side = along.Cross(view);
            return side.LengthSquared() > 0 ? side.Normalized() * halfWidths[i] * pixel(points[i]) : Vector3.Zero;
        }
        for (int i = 0; i + 1 < points.Length; i++)
        {
            Vector3 s0 = Side(i), s1 = Side(i + 1);
            var p = new[] { points[i] + s0, points[i + 1] + s1, points[i] - s0, points[i + 1] - s1 };
            var f = new[] { new Vector2(texture[i], 0), new Vector2(texture[i + 1], 0), new Vector2(texture[i], 1), new Vector2(texture[i + 1], 1) };
            var uv = textured ? Fractions(r.Record, f) : new Vector2[4];
            Color c0 = Tint(mode, colours[i]), c1 = Tint(mode, colours[i + 1]);
            int sheet = textured ? 0 : -1;
            r.Triangles.Add(new Triangle(sheet, mode, p[0], p[1], p[2], uv[0], uv[1], uv[2], c0, c1, c0));
            r.Triangles.Add(new Triangle(sheet, mode, p[1], p[3], p[2], uv[1], uv[3], uv[2], c1, c1, c0));
        }
    }

    /// <summary>
    /// Trails (5 or 9 samples, up to 8 of them, the count at 0xc0): each runs from its start point along its spline to
    /// its end over a random number of steps (0xb4 to 0xb8), easing from a start speed (0x30), leaving its last
    /// samples behind it as a ribbon that fades by the transparency curve; when it arrives it collapses into its head.
    /// </summary>
    private void RenderTrail(Instance instance, Renderer r, Camera3D? camera)
    {
        int samples = r.Fade.Length;
        if (r.Trails.Count == 0 && r.Age == 0)
        {
            int count = Math.Clamp(S16(r.E, 0xC0), 1, 8);
            PickEnds(instance, r, count, 0);
            PickControls(r, 0);
            var (low, high) = (Lerp(S16(r.E, 0xB4), S16(r.E, 0xB6), 0), Lerp(S16(r.E, 0xB8), S16(r.E, 0xBA), 0));
            long speed = (long)Lerp(S16(r.E, 0x30), S16(r.E, 0x32), 0) * 4096;
            foreach (var spline in r.Splines)
            {
                int steps = Math.Max(1, RandomBetween(low, high));
                var points = new Vector3[samples];
                points[0] = spline[0];
                r.Trails.Add((steps, 1, samples - 1, 0, points, 0, speed));
            }
        }
        const long One = 4096;
        for (int i = 0; i < r.Trails.Count; i++)
        {
            var t = r.Trails[i];
            if (t.Collapse == 0) continue;
            if (r.Age >= t.Steps)
            {
                t.Collapse--;
                if (t.Collapse < t.Steps) t.Samples = Math.Max(0, t.Samples - 1);
            }
            else
            {
                long left = t.Steps - r.Age;
                t.Speed += (6 * One * One - t.Progress * 2) / (left * left) - t.Speed * 2 / left;
                t.Progress += t.Speed;
                t.Current = (t.Current + 1) % samples;
                t.Points[t.Current] = Spline(r.Splines[i], t.Progress / 4096f / 4096f - 1);
                if (t.Samples < samples) t.Samples++;
            }
            r.Trails[i] = t;
        }
        float width = Triple(r, 0x64, 0xE).X / 2;
        var colour = new Color(Doubled(r, 0) / 128f, Doubled(r, 1) / 128f, Doubled(r, 2) / 128f);
        int mode = RecordBlend(r.Record);
        foreach (var t in r.Trails)
        {
            if (t.Samples < 2) continue;
            var points = new List<Vector3>();
            var widths = new List<float>();
            var texture = new List<float>();
            var colours = new List<Color>();
            for (int j = 0, sample = t.Current; j < t.Samples; j++, sample = (sample + samples - 1) % samples)
            {
                points.Add(t.Points[sample]);
                widths.Add(width);
                int fade = Math.Max(0, t.Collapse - j);
                texture.Add(fade / (float)(samples - 1));
                colours.Add(colour * (r.Fade[Math.Min(samples - 1, fade)] / 128f));
            }
            Ribbon(r, camera, points, widths, texture, colours, mode);
            if (r.E[3] > 0) Fire(instance, r.E[3] - 1, 0, new Particle { Position = t.Points[t.Current] * 4096 }, r.Age);
        }
    }

    /// <summary>
    /// Strands (lightning): up to 8 (the count range at 0xc0), each a spline from a start point to an end point re-picked
    /// every so many ticks (0x74) with fresh control points each tick, drawn as 8 segments at jittered places along the
    /// spline (the jitter 0x30) as a textured ribbon of a random width (0x64), or as plain coloured lines.
    /// </summary>
    private void RenderStrands(Instance instance, Renderer r, Camera3D? camera)
    {
        bool lines = r.Kind.EndsWith("lines");
        int count = Math.Clamp(At(r, 0xC0, 0x16), 0, 8);
        if (r.Repick <= 0 || r.Splines.Count != count)
        {
            PickEnds(instance, r, count, r.Age);
            r.Repick = S16(r.E, 0x74);
        }
        r.Repick--;
        PickControls(r, r.Age);
        int jitter = Math.Max(1, At(r, 0x30, 0x17));
        var widthRange = Triple(r, 0x64, 0xE);
        var colour = new Color(Doubled(r, 0) / 128f, Doubled(r, 1) / 128f, Doubled(r, 2) / 128f);
        int mode = lines ? 1 : RecordBlend(r.Record);
        foreach (var spline in r.Splines)
        {
            var points = new List<Vector3> { spline[0] };
            var places = new List<float> { 0 };
            for (int s = 1; s < 8; s++)
            {
                float t = s * 3f / 8 + (_random.RandiRange(0, jitter - 1) - jitter / 2) / 4096f;
                points.Add(Spline(spline, t - 1));
                places.Add(t / 3);
            }
            points.Add(spline[3]);
            places.Add(1);
            var widths = new List<float>();
            var colours = new List<Color>();
            for (int i = 0; i < points.Count; i++)
            {
                widths.Add(lines ? 0.5f : widthRange.X / 2 + (widthRange.Y != 0 ? Rand(0, widthRange.Y) / 2 : 0));
                colours.Add(colour);
            }
            Ribbon(r, camera, points, widths, places, colours, mode, !lines);
        }
    }
}
