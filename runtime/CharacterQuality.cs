// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;

namespace Wol.Games.Vagrant.Runtime;

/// <summary>
/// How characters are drawn: Original (their textures as the disc has them, each texel a sharp block, as on the
/// PlayStation) or Enhanced (each texture scaled up four times by an edge-following pixel-art scaler and drawn smooth,
/// so a face's eyes, mouth and hair edges are lines rather than blocks). The enhanced textures are made from the
/// installed ones as a rig is first shown, never stored. The choice is a saved preference.
/// </summary>
public partial class CharacterQuality : Node
{
    /// <summary>Whether characters are drawn enhanced.</summary>
    public static bool Enhanced { get; private set; }

    /// <summary>Raised when the choice changes, for the field to redraw the characters it shows.</summary>
    public static event Action? Changed;

    // Each original texture's enhanced copy, made once.
    private static readonly Dictionary<ulong, ImageTexture> Scaled = new();
    private const string Original = "vagrant_original_texture";

    public override void _Ready()
    {
        Enhanced = Wol.Engine.Display.DisplaySettings.Preference("characters", "enhanced", false);
    }

    /// <summary>Sets the choice, saves it (unless <paramref name="save"/> is false, as the checks have it), and has the field redraw its characters.</summary>
    public static void Choose(bool enhanced, bool save = true)
    {
        if (Enhanced == enhanced) return;
        Enhanced = enhanced;
        if (!save) { Changed?.Invoke(); return; }
        Wol.Engine.Display.DisplaySettings.SetPreference("characters", "enhanced", enhanced);
        Changed?.Invoke();
    }

    /// <summary>Has the field draw its characters again as chosen (after another setting has changed their filtering).</summary>
    public static void Refresh() => Changed?.Invoke();

    /// <summary>
    /// Draws a rig as chosen: each of its textured materials takes its enhanced texture and smooth filtering, or its
    /// original texture and sharp texels back; enhanced, the faces on its head bone (<paramref name="head"/>, -1 for
    /// none) are rounded (<see cref="Rounded"/>).
    /// </summary>
    public static void Apply(Node rig, int head = -1)
    {
        foreach (var found in rig.FindChildren("*", "MeshInstance3D", true, false))
        {
            var instance = (MeshInstance3D)found;
            if (instance.Mesh is not ArrayMesh mesh || instance.Name == "RetailView") continue;
            var whole = instance.HasMeta(OriginalMesh) ? instance.GetMeta(OriginalMesh).As<ArrayMesh>() : mesh;
            if (!instance.HasMeta(OriginalMesh)) instance.SetMeta(OriginalMesh, whole);
            if (Enhanced)
            {
                if (!RoundedMeshes.TryGetValue(whole.GetRid().Id, out var rounded)) RoundedMeshes[whole.GetRid().Id] = rounded = Rounded(whole, instance.Skin != null ? head : -1);
                instance.Mesh = rounded;
            }
            else instance.Mesh = whole;
            for (int surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
                if (instance.Mesh.SurfaceGetMaterial(surface) is StandardMaterial3D material) Apply(material);
        }
    }

    private const string OriginalMesh = "vagrant_original_mesh";
    private static readonly Dictionary<ulong, ArrayMesh> RoundedMeshes = new();

    /// <summary>
    /// A copy of a rig's mesh with its head rounded. The models move as rigid pieces, one to a bone, so the one-sided
    /// faces wholly on the head bone can be divided without opening seams: each triangle is cut in four, twice, and
    /// each new point on an edge is lifted onto the curve the edge's two corners' normals give (the midpoint of the
    /// cubic edge of a curved triangle; a corner's normal is the average of the faces meeting there, corners at the
    /// same place counted as one). An edge on the region's border stays straight, so the head still meets the neck
    /// and hair where it did. Texture coordinates are halved along with the edges.
    /// </summary>
    public static ArrayMesh Rounded(ArrayMesh mesh, int head)
    {
        var result = new ArrayMesh();
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var arrays = mesh.SurfaceGetArrays(surface);
            var material = mesh.SurfaceGetMaterial(surface);
            bool skinned = arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil;
            var positions = new List<Vector3>(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array());
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? new List<Vector2>(new Vector2[positions.Count]) : new List<Vector2>(arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array());
            var owner = new List<int>();
            if (skinned)
            {
                var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                int per = positions.Count > 0 ? bones.Length / positions.Count : 4;
                for (int v = 0; v < positions.Count; v++) owner.Add(bones[v * per]);
            }
            else for (int v = 0; v < positions.Count; v++) owner.Add(0);
            var indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
                ? new List<int>(System.Linq.Enumerable.Range(0, positions.Count))
                : new List<int>(arrays[(int)Mesh.ArrayType.Index].AsInt32Array());
            // The head's faces are drawn two-sided with the cards and cloth; the head is rounded where it is one shell.
            bool rounds = skinned && head >= 0;
            // Only a head that is one shell is rounded: a layered one (a hood over a face, a helmet's brim) would
            // push its outer layer through its inner one (the hooded soldier in MAP003).
            rounds &= Closed(positions, owner, indices, head);
            for (int level = 0; rounds && level < 2; level++) Divide(positions, uvs, owner, indices, head);
            var output = new Godot.Collections.Array();
            output.Resize((int)Mesh.ArrayType.Max);
            output[(int)Mesh.ArrayType.Vertex] = positions.ToArray();
            output[(int)Mesh.ArrayType.Normal] = Normals(positions, owner, indices);
            output[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            if (skinned)
            {
                var newBones = new int[positions.Count * 4];
                var newWeights = new float[positions.Count * 4];
                for (int v = 0; v < positions.Count; v++) { newBones[v * 4] = owner[v]; newWeights[v * 4] = 1; }
                output[(int)Mesh.ArrayType.Bones] = newBones;
                output[(int)Mesh.ArrayType.Weights] = newWeights;
            }
            output[(int)Mesh.ArrayType.Index] = indices.ToArray();
            if (arrays[(int)Mesh.ArrayType.Color].VariantType != Variant.Type.Nil)
            {
                var colours = new List<Color>(arrays[(int)Mesh.ArrayType.Color].AsColorArray());
                while (colours.Count < positions.Count) colours.Add(Colors.White);
                output[(int)Mesh.ArrayType.Color] = colours.ToArray();
            }
            result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, output);
            result.SurfaceSetMaterial(result.GetSurfaceCount() - 1, material);
        }
        return result;
    }

    /// <summary>
    /// Smooth normals for soft shading: each corner's is the area-weighted average of the faces meeting where it stands
    /// on its own bone (pieces on different bones keep their own), so faces shade into each other within a piece.
    /// </summary>
    private static Vector3[] Normals(List<Vector3> positions, List<int> owner, List<int> indices)
    {
        (int, int, int, int) Key(int v) => ((int)MathF.Round(positions[v].X * 64), (int)MathF.Round(positions[v].Y * 64), (int)MathF.Round(positions[v].Z * 64), owner[v]);
        var sums = new Dictionary<(int, int, int, int), Vector3>();
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            Vector3 a = positions[indices[t]], b = positions[indices[t + 1]], c = positions[indices[t + 2]];
            // Godot's front faces wind clockwise, so the outward normal is (c - a) x (b - a).
            var normal = (c - a).Cross(b - a);
            for (int k = 0; k < 3; k++) sums[Key(indices[t + k])] = sums.GetValueOrDefault(Key(indices[t + k])) + normal;
        }
        var normals = new Vector3[positions.Count];
        for (int v = 0; v < positions.Count; v++)
            normals[v] = sums.TryGetValue(Key(v), out var n) && n.LengthSquared() > 0 ? n.Normalized() : Vector3.Up;
        return normals;
    }

    /// <summary>
    /// The light enhanced characters are shaded by, for a field's world: a light from above, to the camera's left
    /// and in front of the characters, that turns with the camera (<see cref="Follow"/>), and an even fill, balanced so
    /// a character is about as bright on the whole as drawn unshaded (lit from the front at 1.25, in shade at 0.55).
    /// Rooms and effects are unshaded, so only characters take it.
    /// </summary>
    public static DirectionalLight3D Light(Node3D parent)
    {
        var light = new DirectionalLight3D { Name = "CharacterLight", LightEnergy = 0.7f, ShadowEnabled = false, LightSpecular = 0 };
        parent.AddChild(light);
        return light;
    }

    /// <summary>
    /// Turns the character light with the camera (from above, to its left, toward the characters) and keeps the fill
    /// on the room's environment, which a room may make anew.
    /// </summary>
    public static void Follow(DirectionalLight3D light, Camera3D camera, WorldEnvironment environment)
    {
        light.Visible = Enhanced;
        var settings = environment.Environment;
        if (settings.AmbientLightSource != Godot.Environment.AmbientSource.Color || settings.AmbientLightEnergy != 0.55f)
        {
            settings.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            settings.AmbientLightColor = Colors.White;
            settings.AmbientLightEnergy = 0.55f;
        }
        var basis = camera.GlobalBasis;
        var from = (basis.Z * 0.6f - basis.X * 0.5f + Vector3.Up * 0.8f).Normalized();
        light.GlobalTransform = new Transform3D(Basis.LookingAt(-from, Mathf.Abs(from.Dot(Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up), Vector3.Zero);
    }

    /// <summary>
    /// Whether the faces wholly on the head bone make one shell that can be rounded: at least 20 edges (by place),
    /// at most one in 40 shared by more than three faces (layers: a hood over a face, a brim over a helmet, have one in
    /// 6 to 30; Ashley's head 2 of 101), and at most three tenths bordering a single face (the openings at the neck and
    /// under the hair).
    /// </summary>
    private static bool Closed(List<Vector3> positions, List<int> owner, List<int> indices, int head)
    {
        (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 64), (int)MathF.Round(p.Y * 64), (int)MathF.Round(p.Z * 64));
        var edges = new Dictionary<((int, int, int), (int, int, int)), int>();
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            if (owner[indices[t]] != head || owner[indices[t + 1]] != head || owner[indices[t + 2]] != head) continue;
            for (int k = 0; k < 3; k++)
            {
                var a = Key(positions[indices[t + k]]);
                var b = Key(positions[indices[t + (k + 1) % 3]]);
                if (a == b) continue;
                var edge = a.CompareTo(b) < 0 ? (a, b) : (b, a);
                edges[edge] = edges.GetValueOrDefault(edge) + 1;
            }
        }
        if (edges.Count < 20) return false;
        int open = 0, layered = 0;
        foreach (int count in edges.Values)
        {
            if (count > 2) layered++;
            if (count == 1) open++;
        }
        return layered * 40 <= edges.Count && open * 10 <= edges.Count * 3;
    }

    /// <summary>One level of <see cref="Rounded"/>'s division over the triangles wholly on <paramref name="head"/>.</summary>
    private static void Divide(List<Vector3> positions, List<Vector2> uvs, List<int> owner, List<int> indices, int head)
    {
        (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 64), (int)MathF.Round(p.Y * 64), (int)MathF.Round(p.Z * 64));
        bool OnHead(int t) => owner[indices[t]] == head && owner[indices[t + 1]] == head && owner[indices[t + 2]] == head;
        var normals = new Dictionary<(int, int, int), Vector3>();
        var edges = new Dictionary<((int, int, int), (int, int, int)), int>();
        ((int, int, int), (int, int, int)) Edge((int, int, int) a, (int, int, int) b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            if (!OnHead(t)) continue;
            Vector3 a = positions[indices[t]], b = positions[indices[t + 1]], c = positions[indices[t + 2]];
            var normal = (b - a).Cross(c - a);
            foreach (var p in new[] { a, b, c }) normals[Key(p)] = normals.GetValueOrDefault(Key(p)) + normal;
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) }) edges[Edge(Key(p), Key(q))] = edges.GetValueOrDefault(Edge(Key(p), Key(q))) + 1;
        }
        Vector3 Normal(Vector3 p) => normals.TryGetValue(Key(p), out var n) && n.LengthSquared() > 0 ? n.Normalized() : Vector3.Zero;
        var made = new Dictionary<(int, int), int>();
        int Middle(int i, int j)
        {
            if (made.TryGetValue((Math.Min(i, j), Math.Max(i, j)), out int found)) return found;
            Vector3 a = positions[i], b = positions[j], na = Normal(a), nb = Normal(b);
            var middle = (a + b) / 2;
            // An interior edge is lifted onto the curved triangle's edge; a border edge stays straight.
            if (edges.GetValueOrDefault(Edge(Key(a), Key(b))) == 2)
                middle -= ((b - a).Dot(na) * na + (a - b).Dot(nb) * nb) / 8;
            positions.Add(middle);
            uvs.Add((uvs[i] + uvs[j]) / 2);
            owner.Add(head);
            return made[(Math.Min(i, j), Math.Max(i, j))] = positions.Count - 1;
        }
        var next = new List<int>(indices.Count * 2);
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            int a = indices[t], b = indices[t + 1], c = indices[t + 2];
            if (!OnHead(t)) { next.Add(a); next.Add(b); next.Add(c); continue; }
            int ab = Middle(a, b), bc = Middle(b, c), ca = Middle(c, a);
            next.AddRange(new[] { a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca });
        }
        indices.Clear();
        indices.AddRange(next);
    }

    private static void Apply(StandardMaterial3D material)
    {
        if (material.AlbedoTexture is not Texture2D current)
        {
            material.ShadingMode = Enhanced ? BaseMaterial3D.ShadingModeEnum.PerPixel : BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled;
            return;
        }
        var original = material.HasMeta(Original) ? material.GetMeta(Original).As<Texture2D>() : current;
        if (!material.HasMeta(Original)) material.SetMeta(Original, original);
        if (Enhanced)
        {
            if (!Scaled.TryGetValue(original.GetRid().Id, out var scaled))
            {
                var image = original.GetImage();
                if (image == null || image.IsEmpty()) return;
                var larger = Enlarge(image);
                larger.GenerateMipmaps();
                Scaled[original.GetRid().Id] = scaled = ImageTexture.CreateFromImage(larger);
            }
            material.AlbedoTexture = scaled;
            material.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
            // Soft shading: lit per pixel by the field's character light and fill (see Light), without highlights.
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel;
            material.DiffuseMode = BaseMaterial3D.DiffuseModeEnum.LambertWrap;
            material.SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled;
            material.Roughness = 1;
            material.Metallic = 0;
        }
        else
        {
            material.AlbedoTexture = original;
            material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        }
    }

    /// <summary>
    /// Scales an image up four times. It is doubled twice by the edge-following scaler (so lines lose their steps),
    /// then each texel of the result is blended with its neighbours that look alike (so a face's shading runs smooth,
    /// while a texel never takes colour across an edge: an eye, a brow, the mouth, a cut-out's outline), and lastly
    /// sharpened a little against that blend, which keeps the features crisp at the larger size.
    /// </summary>
    public static Image Enlarge(Image source)
    {
        var image = (Image)source.Duplicate();
        if (image.IsCompressed()) image.Decompress();
        image.Convert(Image.Format.Rgba8);
        var doubled = Upscale(Upscale(image));
        int width = doubled.GetWidth(), height = doubled.GetHeight();
        byte[] from = doubled.GetData();
        int At(int x, int y) => (Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)) * 4;
        // Two passes of a 5 by 5 blend over alike neighbours, weighted by distance.
        byte[] Smooth(byte[] data)
        {
            var result = new byte[data.Length];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int p = At(x, y);
                    float r = 0, g = 0, b = 0, total = 0;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int q = At(x + dx, y + dy);
                            if (!Alike(data, p, q, 12)) continue;
                            float w = 1f / (1 + dx * dx + dy * dy);
                            r += data[q] * w; g += data[q + 1] * w; b += data[q + 2] * w; total += w;
                        }
                    result[p] = (byte)Math.Round(r / total); result[p + 1] = (byte)Math.Round(g / total); result[p + 2] = (byte)Math.Round(b / total);
                    result[p + 3] = data[p + 3];
                }
            return result;
        }
        var smooth = Smooth(from);
        // Unsharp: push each texel a little away from its alike neighbourhood's average.
        var blurred = Smooth(smooth);
        var to = new byte[from.Length];
        for (int i = 0; i < to.Length; i += 4)
        {
            for (int k = 0; k < 3; k++) to[i + k] = (byte)Math.Clamp(smooth[i + k] + 0.6f * (smooth[i + k] - blurred[i + k]), 0, 255);
            to[i + 3] = from[i + 3];
        }
        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, to);
    }

    /// <summary>Whether two texels look alike: both clear, or both opaque and close in brightness and colour.</summary>
    private static bool Alike(byte[] data, int a, int b, float brightness = 48)
    {
        bool clearA = data[a + 3] < 128, clearB = data[b + 3] < 128;
        if (clearA || clearB) return clearA == clearB;
        float r1 = data[a], g1 = data[a + 1], b1 = data[a + 2], r2 = data[b], g2 = data[b + 1], b2 = data[b + 2];
        float y1 = 0.299f * r1 + 0.587f * g1 + 0.114f * b1, y2 = 0.299f * r2 + 0.587f * g2 + 0.114f * b2;
        float u1 = -0.169f * r1 - 0.331f * g1 + 0.5f * b1, u2 = -0.169f * r2 - 0.331f * g2 + 0.5f * b2;
        float v1 = 0.5f * r1 - 0.419f * g1 - 0.081f * b1, v2 = 0.5f * r2 - 0.419f * g2 - 0.081f * b2;
        return Math.Abs(y1 - y2) <= brightness && Math.Abs(u1 - u2) <= 7 && Math.Abs(v1 - v2) <= 6;
    }

    /// <summary>
    /// Doubles an image the way pixel art is best doubled: each texel becomes four, and a quarter takes a neighbour's
    /// colour where that neighbour and the next one round agree across the corner while the opposite pair differ, so a
    /// staircase becomes a diagonal and a lone texel stays square. Colours count as the same when they are close to
    /// the eye (brightness within 48, the two colour differences within 7 and 6, of 255), so a face's soft shading is
    /// not mistaken for edges; a transparent texel is only ever the same as another transparent one.
    /// </summary>
    public static Image Upscale(Image source)
    {
        var image = (Image)source.Duplicate();
        if (image.IsCompressed()) image.Decompress();
        image.Convert(Image.Format.Rgba8);
        int width = image.GetWidth(), height = image.GetHeight();
        byte[] from = image.GetData(), to = new byte[width * height * 16];
        int At(int x, int y) => (Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)) * 4;
        bool Same(int a, int b) => Alike(from, a, b);
        void Put(int x, int y, int source) => Array.Copy(from, source, to, (y * width * 2 + x) * 4, 4);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = At(x, y), up = At(x, y - 1), left = At(x - 1, y), right = At(x + 1, y), down = At(x, y + 1);
                Put(x * 2, y * 2, Same(left, up) && !Same(left, down) && !Same(up, right) ? up : p);
                Put(x * 2 + 1, y * 2, Same(up, right) && !Same(up, left) && !Same(right, down) ? right : p);
                Put(x * 2, y * 2 + 1, Same(down, left) && !Same(down, right) && !Same(left, up) ? left : p);
                Put(x * 2 + 1, y * 2 + 1, Same(right, down) && !Same(right, up) && !Same(down, left) ? down : p);
            }
        return Image.CreateFromData(width * 2, height * 2, false, Image.Format.Rgba8, to);
    }
}
