// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// Converts a retail character (an SHP model with its SEQ animation sets) into a skinned glTF rig for the shared engine.
/// The model is rigid per bone: each vertex belongs to one bone, and faces may span bones. Each bone sits at minus its
/// parent's length along the parent's x axis; animations rotate bones locally by Rz·Ry·Rx with 8192 units per turn.
/// The rig keeps retail space and units inside a "Space" node that turns them into the engine's y-up metres.
/// </summary>
public sealed class CharacterConversion
{
    /// <summary>
    /// Retail animation frames per second: the animation interpreter (BATTLE.PRG 0x800AD714) steps a SEQ frame on each
    /// tick of the 30 Hz event clock (at the 15 Hz frame step, f1 4, two). So the clips' strides match their glides:
    /// the opening's knight walks 32 frames, a stride each half, in a second at his 130 units a second, and Ashley
    /// runs her 10-frame cycle at 18 units a tick.
    /// </summary>
    public const float FramesPerSecond = 30f;
    /// <summary>Event-clock ticks (30 Hz) per SEQ frame.</summary>
    public const int TicksPerFrame = 1;

    private readonly string _name;
    private readonly byte[] _model;
    private readonly List<(string Set, byte[] Data, int Offset)> _sequences;

    private int _bones;
    private (int Length, int Parent)[] _skeleton = Array.Empty<(int, int)>();
    /// <summary>Where the model's bones, vertex groups, vertices and faces start.</summary>
    private readonly int _bonesAt, _groupsAt, _verticesAt, _facesAt;
    /// <summary>For a weapon, its texture header and the material palette it is drawn with; -1 for a character.</summary>
    private readonly int _weaponTexture = -1, _weaponPalette = -1;


    /// <param name="sequences">Named animation sets: the SEQ data and the offset of the SEQ within it.</param>
    public CharacterConversion(string name, byte[] model, IEnumerable<(string Set, byte[] Data, int Offset)> sequences)
    {
        _name = name;
        _model = model;
        _sequences = new List<(string, byte[], int)>(sequences);
        if (model.Length < 0x138 || model[0] != (byte)'H') throw new InvalidDataException($"{name} is not a character model.");
        _bonesAt = 0x138;
        _groupsAt = (int)BitConverter.ToUInt32(model, 0x12C) + 0xF8;
        _verticesAt = (int)BitConverter.ToUInt32(model, 0x130) + 0xF8;
        _facesAt = (int)BitConverter.ToUInt32(model, 0x134) + 0xF8;
    }

    /// <summary>
    /// A weapon model (WEP): the SHP's counts at 4 to 11, then a model block (its size at 12) whose part offsets sit
    /// at 0x30 from its start, with the bones from 0x40. The texture follows the block: an 8-byte header (width/2 at
    /// 5, a format at 7 of 48 or 96 colours), a shared palette part of 16 (or 32) colours, seven material palettes of
    /// 32 (or 64), then the 8-bit image, 32 lines high. A material's palette is the shared part and its own, joined.
    /// </summary>
    private CharacterConversion(string name, byte[] wep, int palette)
    {
        _name = name;
        _model = wep;
        _sequences = new();
        if (wep.Length < 0x50 || wep[0] != (byte)'H') throw new InvalidDataException($"{name} is not a weapon model.");
        int block = 0x10, size = (int)BitConverter.ToUInt32(wep, 12);
        if (block + size + 8 > wep.Length) throw new InvalidDataException($"{name} is truncated.");
        _bonesAt = block + 0x40;
        _groupsAt = block + (int)BitConverter.ToUInt32(wep, block + 0x34);
        _verticesAt = block + (int)BitConverter.ToUInt32(wep, block + 0x38);
        _facesAt = block + (int)BitConverter.ToUInt32(wep, block + 0x3C);
        _weaponTexture = block + size;
        _weaponPalette = palette;
    }

    /// <summary>Converts a weapon model, drawn with one of its seven material palettes.</summary>
    public static CharacterConversion Weapon(string name, byte[] wep, int palette) => new(name, wep, palette);

    /// <summary>BATTLE.PRG's lengths of the animation-script operations (0x800E9298), which <see cref="Export"/> needs to read the scripts.</summary>
    public byte[]? ScriptLengths { get; init; }


    /// <summary>
    /// Each animation's script (BATTLE.PRG's interpreter at 0x800AD714): from the stream its header's halfword at 4 names,
    /// a frame (255 then n for 255 + n), an operation and its operands, again, until operation 0. The events the field
    /// plays are kept as [frame, operation, operands…]: 1 the clip starting over (0x800ADDA4 clears its frame and
    /// rewinds its script), 2 the blow landing, 4 and 5 a resident sound (4: its id, 88 for the weapon's swing; 5: its
    /// id and volume), and 12 an effect part.
    /// </summary>
    private List<JsonArray> Scripts(byte[] seq, byte[] lengths)
    {
        var scripts = new List<JsonArray>();
        int slots = BitConverter.ToUInt16(seq, 0), bones = seq[2];
        int table = (int)BitConverter.ToUInt32(seq, 12) + 8 + slots, stride = bones * 4 + 10, count = (table - slots - 16) / stride;
        for (int index = 0; index < count; index++)
        {
            var list = new JsonArray();
            int at = table + BitConverter.ToUInt16(seq, 16 + index * stride + 4);
            int Frame()
            {
                if (at >= seq.Length) return -1;
                int f = seq[at++];
                if (f == 255 && at < seq.Length) f = seq[at++] + 255;
                return f;
            }
            int frame = Frame();
            for (int guard = 0; guard < 256 && frame >= 0 && at < seq.Length; guard++)
            {
                int op = seq[at];
                if (op == 0 || op >= lengths.Length || lengths[op] == 0 || at + lengths[op] > seq.Length) break;
                // 32 to 35 show (32, 33) or hide (34, 35) the held weapon (BATTLE.PRG 0x800AAD4C); 33 and 35 at once.
                // 10 plays a sound from the model's own bank (0x800AE4FC); 58 marks a death played out.
                if (op is 1 or 2 or 4 or 5 or 10 or 12 or 58 or >= 32 and <= 35)
                {
                    var entry = new JsonArray { frame, op };
                    for (int k = 1; k < lengths[op]; k++) entry.Add((int)seq[at + k]);
                    list.Add(entry);
                }
                at += lengths[op];
                frame = Frame();
            }
            scripts.Add(list);
        }
        return scripts;
    }

    /// <summary>
    /// Writes a PS1 colour (0 transparent) as four bytes of RGBA, scaled by <paramref name="gain"/>. On semi-transparent
    /// faces only texels with the mask bit (0x8000) blend, as on the rooms' polygons: <paramref name="mask"/> 1 gives those
    /// half alpha and leaves the rest opaque, 2 keeps only the unmasked texels and 3 only the masked ones (the two draws
    /// of an additive or subtractive face); 0 ignores the bit.
    /// </summary>
    public static void Put(byte[] data, int at, ushort colour, int mask, float gain)
    {
        bool masked = (colour & 0x8000) != 0;
        var shown = TitleConversion.ToColor(colour, false);
        data[at] = (byte)(shown.R8 * gain);
        data[at + 1] = (byte)(shown.G8 * gain);
        data[at + 2] = (byte)(shown.B8 * gain);
        data[at + 3] = colour == 0 ? (byte)0 : mask switch { 1 => masked ? (byte)128 : (byte)255, 2 => masked ? (byte)0 : (byte)255, 3 => masked ? (byte)255 : (byte)0, _ => (byte)255 };
    }

    private static short BigSigned(byte[] data, int offset) => (short)((data[offset] << 8) | data[offset + 1]);

    /// <summary>
    /// The size retail draws models at: its model scale vector (BATTLE.PRG 0x800F4438, given to every actor at
    /// +0x183C, root offset and all), which INITBTL.PRG sets to 1820 (of 4096) as the field starts (0x800F9808, by way
    /// of 0x8009FB64). Weapons are mounted in the skeleton's own space and so take it from the character that holds
    /// them.
    /// </summary>
    public const float ModelScale = 1820f / 4096;

    /// <summary>The model as a binary glTF, once <see cref="Export"/> has built it.</summary>
    public byte[] Model { get; private set; } = Array.Empty<byte>();

    /// <summary>Builds the model (<see cref="Model"/>) and returns its definition: bones, mounts, limbs and clip events.</summary>
    /// <summary>
    /// Readies the conversion for <see cref="Clips"/> without building the rig: a rig already made (kept between
    /// sessions) takes event sets on its skeleton.
    /// </summary>
    public void Prepare() => BuildSkeleton().Free();

    public JsonObject Export()
    {
        var root = new Node3D { Name = "Character" };
        float size = _weaponTexture < 0 ? ModelScale : 1;
        var space = new Node3D { Name = "Space", Basis = new Basis(Vector3.Right, Mathf.Pi).Scaled(Vector3.One * size / MapConversion.UnitsPerMetre) };
        root.AddChild(space);
        space.Owner = root;
        var skeleton = BuildSkeleton();
        space.AddChild(skeleton);
        skeleton.Owner = root;
        var mesh = BuildMesh(skeleton);
        skeleton.AddChild(mesh);
        mesh.Owner = root;
        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        root.AddChild(player);
        player.Owner = root;
        var library = new AnimationLibrary();
        var names = new JsonArray();
        var events = new JsonObject();
        var movement = new JsonObject();
        foreach (var (set, data, offset) in _sequences)
            foreach (var (clip, animation) in Clips(set, data, offset, events, movement))
            {
                // A slot two sets of the same name fill takes the first's (a unit's room set before its own).
                if (library.HasAnimation(clip)) continue;
                library.AddAnimation(clip, animation);
                names.Add(clip);
            }
        // A model without animations (a prop: the opening's dead men, 0x73 to 0x76) still has one clip, the pose the field
        // draws it in. Its root stands half its bone 1 length above the actor's place, as the actor renderer lifts every
        // model (BATTLE.PRG 0x800AF620 adds half the model's word at +0x50, bone 1's length, to the drawn height),
        // with no animation to place it otherwise. Bone 1 takes the standing turn every humanoid animation set gives
        // the hip as its base, (2048, 0, 6144): modelled as they lie in the scene, unturned the props stand on end
        // (Episode 1 at 16:50, MAP172: the man slumped in the chair, the man spread on his back across the table).
        if (names.Count == 0)
        {
            // glTF drops animations without tracks, so the rest pose holds the root bone explicitly.
            var rest = new Animation { Length = 1 / FramesPerSecond };
            int track = rest.AddTrack(Animation.TrackType.Rotation3D);
            rest.TrackSetPath(track, "Space/Skeleton:b0");
            rest.RotationTrackInsertKey(track, 0, Quaternion.Identity);
            if (_bones >= 2)
            {
                track = rest.AddTrack(Animation.TrackType.Position3D);
                rest.TrackSetPath(track, "Space/Skeleton:b0");
                // The lift is in map units; the skeleton is in the model's own, ModelScale of them.
                var place = new Vector3(0, _skeleton[1].Length / 2, 0);
                // The man slumped in MAP172's chair (0x73) is drawn against the chair's back, 54 units from the place
                // his script gives him (561, 0, 1221, heading 1184): in his own frame, 53 ahead and 11 to one side.
                // This is measured, not read from retail: his place was fitted against Episode 1 at 16:51 (from beside
                // the chair) and 16:54 (over Ashley's shoulder), where the same fit leaves the man on the table (0x74)
                // where his script puts him. How retail comes by it was not found.
                if (_name == "character_73") place += new Vector3(-11, 0, 53);
                rest.PositionTrackInsertKey(track, 0, place / ModelScale);
                track = rest.AddTrack(Animation.TrackType.Rotation3D);
                rest.TrackSetPath(track, "Space/Skeleton:b1");
                rest.RotationTrackInsertKey(track, 0, Rotation(new Vector3(2048, 0, 6144)));
            }
            library.AddAnimation("rest", rest);
            names.Add("rest");
        }
        player.AddAnimationLibrary("", library);

        using var document = new GltfDocument();
        using var state = new GltfState();
        var error = document.AppendFromScene(root, state);
        if (error != Error.Ok) throw new InvalidDataException($"{_name} could not be converted ({error}).");
        Model = document.GenerateBuffer(state);
        if (Model.Length == 0) throw new InvalidDataException($"{_name} could not be built.");
        root.Free();
        // A bone record's byte 6 is its mount: 0xF0 holds the weapon (and 0xF1 the shield).
        int weaponBone = -1;
        int shieldBone = -1, headBone = -1;
        for (int i = 0; i < _bones && _weaponTexture < 0; i++)
        {
            if (_model[_bonesAt + i * 16 + 6] == 0xF0) weaponBone = i;
            if (_model[_bonesAt + i * 16 + 6] == 0xF1) shieldBone = i;
            // Byte 6 is also the bone's body part: 1 is the head, which head tracking turns.
            if (_model[_bonesAt + i * 16 + 6] == 1 && headBone < 0) headBone = i;
        }
        // Each limb is aimed at through the first bone whose record byte 7 has the limb's number, from 1, in its low nibble.
        var limbBones = new JsonArray();
        for (int limb = 0; limb < 6; limb++)
        {
            int bone = -1;
            for (int i = 0; i < _bones && _weaponTexture < 0 && bone < 0; i++) if ((_model[_bonesAt + i * 16 + 7] & 15) == limb + 1) bone = i;
            limbBones.Add(bone);
        }
        // Each limb's part, which names it (BATTLE.PRG's limb names: 1 HEAD, 2 R.ARM, 3 L.ARM, 4 LEGS, 6 BODY...): its
        // first bone's record byte 6, the bone's body part.
        var limbParts = new JsonArray();
        for (int limb = 0; limb < 6; limb++)
        {
            int part = 0;
            for (int i = 0; i < _bones && _weaponTexture < 0; i++)
                if ((_model[_bonesAt + i * 16 + 7] & 15) == limb + 1) { part = _model[_bonesAt + i * 16 + 6]; break; }
            limbParts.Add(part is >= 1 and <= 15 ? part : 0);
        }
        // Every bone's limb (its record's byte 7, low nibble: 1 to 6, 0 none), which target mode lights.
        var boneLimbs = new JsonArray();
        for (int i = 0; i < _bones && _weaponTexture < 0; i++) boneLimbs.Add(_model[_bonesAt + i * 16 + 7] & 15);
        // The radius of the shadow under it (the header's u16 at 0x64, which BATTLE.PRG keeps at the actor's 0x64C and
        // 0x800B4B14 draws by); a weapon has none.
        int shadow = _weaponTexture < 0 && _model.Length >= 0x66 ? BitConverter.ToUInt16(_model, 0x64) : 0;
        // Whether the room's light map lights it: bit 16 of the header's word at 0x60, which BATTLE.PRG keeps at the
        // actor's 0x648 and the actor renderer tests (0x800B371C) before taking the map's colour (0x8008C458); without
        // it the character is drawn at 0x80. 23 models have it clear, among them the Ghost (1A) and the dragons.
        bool roomLit = _weaponTexture >= 0 || _model.Length < 0x64 || (BitConverter.ToUInt32(_model, 0x60) & 0x10000) != 0;
        return new JsonObject { ["rig"] = _name, ["animations"] = names, ["events"] = events, ["movement"] = movement, ["weapon_bone"] = weaponBone, ["shield_bone"] = shieldBone, ["head_bone"] = headBone, ["limb_bones"] = limbBones, ["limb_parts"] = limbParts, ["bone_limbs"] = boneLimbs, ["shadow"] = shadow, ["room_lit"] = roomLit, ["bone_lengths"] = BoneLengths() };
    }

    /// <summary>
    /// One animation set's clips for this model (once <see cref="Export"/> has built its skeleton), named
    /// "&lt;set&gt;_&lt;number&gt;" by each SEQ slot's animation number; their animation scripts go into <paramref name="events"/>.
    /// A set whose bone count differs is for another body (an id or alias shared across models) and has none.
    /// </summary>
    public List<(string Clip, Animation Animation)> Clips(string set, byte[] data, int offset, JsonObject events, JsonObject? movement = null)
    {
        var clips = new List<(string, Animation)>();
        var seq = data[offset..];
        if (seq.Length < 3 || seq[2] != _bones) return clips;
        var animations = new List<Animation>(Animations(seq));
        var scripts = ScriptLengths == null ? null : Scripts(seq, ScriptLengths);
        int slots = BitConverter.ToUInt16(seq, 0), table = (int)BitConverter.ToUInt32(seq, 12) + 8;
        for (int number = 0; number < slots; number++)
        {
            int index = seq[table + number];
            if (index == 0xFF || index >= animations.Count) continue;
            string clip = $"{set}_{number}";
            clips.Add((clip, animations[index]));
            if (scripts != null && index < scripts.Count && scripts[index].Count > 0) events[clip] = scripts[index].DeepClone();
            // A clip that moves its actor keeps how far, by frame (x, y, z each).
            if (movement != null && animations[index].HasMeta("movement"))
            {
                var steps = new JsonArray();
                foreach (var step in animations[index].GetMeta("movement").AsGodotArray()) { var v = step.AsVector3(); steps.Add(new JsonArray { (int)v.X, (int)v.Y, (int)v.Z }); }
                movement[clip] = steps;
            }
            // A clip whose script starts it over (operation 1) loops; one without holds its last frame.
            bool loops = false;
            if (scripts != null && index < scripts.Count) foreach (var entry in scripts[index]) loops |= (int)entry![1]! == 1;
            // Event clips loop only so; every other set's clips loop.
            animations[index].LoopMode = !set.StartsWith("esq", StringComparison.Ordinal) || loops ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        }
        return clips;
    }

    /// <summary>Each bone's length (rig units, along its own -x, where its children stand), which weapon trails reach to.</summary>
    private JsonArray BoneLengths()
    {
        var lengths = new JsonArray();
        foreach (var (length, _) in _skeleton) lengths.Add(length);
        return lengths;
    }

    private Skeleton3D BuildSkeleton()
    {
        _bones = _model[4];
        _skeleton = new (int, int)[_bones];
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        for (int i = 0; i < _bones; i++)
        {
            int length = (int)BitConverter.ToUInt32(_model, _bonesAt + i * 16);
            int parent = (sbyte)_model[_bonesAt + i * 16 + 4];
            if (parent < 0 || parent >= i) parent = -1;
            _skeleton[i] = (length, parent);
            skeleton.AddBone($"b{i}");
            if (parent >= 0)
            {
                skeleton.SetBoneParent(i, parent);
                skeleton.SetBoneRest(i, new Transform3D(Basis.Identity, new Vector3(-_skeleton[parent].Length, 0, 0)));
            }
            skeleton.ResetBonePose(i);
        }
        return skeleton;
    }

    private MeshInstance3D BuildMesh(Skeleton3D skeleton)
    {
        int groups = _model[5];
        int faces = BitConverter.ToUInt16(_model, 6) + BitConverter.ToUInt16(_model, 8) + BitConverter.ToUInt16(_model, 10);
        int groupPointer = _groupsAt, vertexPointer = _verticesAt, facePointer = _facesAt;
        int vertices = BitConverter.ToUInt16(_model, groupPointer + (groups - 1) * 4 + 2);
        var positions = new Vector3[vertices];
        var owners = new int[vertices];
        for (int group = 0, first = 0; group < groups; group++)
        {
            int bone = BitConverter.ToInt16(_model, groupPointer + group * 4), last = BitConverter.ToUInt16(_model, groupPointer + group * 4 + 2);
            if (bone < 0 || bone >= _bones || last > vertices || last < first) throw new InvalidDataException($"{_name} has a malformed vertex group.");
            // Rest poses have no rotation, so a bone's rest origin is the sum of its ancestors' offsets.
            var origin = skeleton.GetBoneGlobalRest(bone).Origin;
            for (int v = first; v < last; v++)
            {
                positions[v] = origin + new Vector3(BitConverter.ToInt16(_model, vertexPointer + v * 8), BitConverter.ToInt16(_model, vertexPointer + v * 8 + 2), BitConverter.ToInt16(_model, vertexPointer + v * 8 + 4));
                owners[v] = bone;
            }
            first = last;
        }

        var textures = new Dictionary<(int, float), ImageTexture?> { [(0, 1f)] = Texture(out int width, out int height, 0, 1) };
        // One-sided faces (BATTLE.PRG 0x800B3CF0 draws a face only when its screen winding, nclip, is positive) and
        // two-sided ones (a plain face's byte 2, bit 0, as the rooms' type bit 0) go to their own surfaces, and so do
        // semi-transparent faces by how they blend: byte 2 bit 4 makes a face semi-transparent and bits 5 and 6 are its
        // blend (half and half, add, subtract, add a quarter), which the draw hands the GPU (0x800B4088 to 0x800B40C0).
        // As on the rooms' polygons, a half-and-half face carries its masked texels at half alpha, and an additive or
        // subtractive one is drawn twice: its unmasked texels opaque and its masked ones blended.
        var surfaces = new Dictionary<(int Side, string Blend, int Mask, float Gain), SurfaceTool>();
        SurfaceTool Surface((int, string, int, float) key)
        {
            if (surfaces.TryGetValue(key, out var made)) return made;
            made = new SurfaceTool();
            made.SetSkinWeightCount(SurfaceTool.SkinWeightCount.Skin4Weights);
            made.Begin(Mesh.PrimitiveType.Triangles);
            return surfaces[key] = made;
        }
        int cursor = facePointer;
        // Most models' faces open with their primitive type and size (0x24 triangles, 0x2C quads) and are lit by the
        // field. Props whose faces open with a vertex index are Gouraud-shaded instead: indices, then each corner's
        // colour word (the primitive code, 0x34 or 0x3C, in the first word's top byte and the size in the second's),
        // with texture coordinates after the colours (a triangle's first one sits between its indices and colours).
        // A plain face's type comes with its size (16 for 0x24, 20 for 0x2C); a shaded face's first index may begin
        // with either type byte, so the size tells them apart.
        bool coloured = !(_model[cursor] == 0x24 && _model[cursor + 1] == 16 || _model[cursor] == 0x2C && _model[cursor + 1] == 20);
        // The head's own faces are drawn from both sides. Culled one-sided, the layers at the back of some heads leave
        // cracks that the PSX's 320 by 240 hides under a pixel but a higher resolution shows (the Duke's knight, model
        // 01: the wall through the back of his hair, closed only by faces 214 to 216 together).
        // A layered head (a hood over its lining: the hooded soldier of zone 3, 17 of its 88 edges shared by more than two
        // faces) keeps its faces one-sided, as both sides of its layers would cover each other.
        int headBone = -1;
        for (int i = 0; i < _bones && _weaponTexture < 0 && headBone < 0; i++) if (_model[_bonesAt + i * 16 + 6] == 1) headBone = i;
        if (headBone >= 0 && !coloured && Layered(positions, owners, facePointer, faces, vertices, headBone)) headBone = -1;
        for (int face = 0; face < faces; face++)
        {
            int type = coloured ? _model[cursor + 11] : _model[cursor], size = coloured ? _model[cursor + 15] : _model[cursor + 1];
            bool quad = type == (coloured ? 0x3C : 0x2C);
            if (type != (coloured ? 0x34 : 0x24) && !quad) throw new InvalidDataException($"{_name} has an unknown face type {type:x2}.");
            int corners = quad ? 4 : 3;
            var index = new int[corners];
            var uv = new Vector2[corners];
            var colour = new Color[corners];
            for (int c = 0; c < corners; c++)
            {
                index[c] = BitConverter.ToUInt16(_model, cursor + (coloured ? 0 : 4) + c * 2) / 4;
                if (index[c] >= vertices) throw new InvalidDataException($"{_name} has a face outside its vertices.");
                // A triangle's texture coordinates are rotated one corner against its vertices, as the rooms' are:
                // corner c takes pair c + 1. A quad's are in order.
                int pair = !coloured && !quad ? (c + 1) % 3 : c;
                int at = !coloured ? cursor + 4 + corners * 2 + pair * 2 : quad ? cursor + 24 + c * 2 : c == 0 ? cursor + 6 : cursor + 18 + c * 2;
                uv[c] = new Vector2(_model[at] / (float)width, _model[at + 1] / (float)height);
                int shade = cursor + 8 + c * 4;
                colour[c] = coloured ? Color.Color8(_model[shade], _model[shade + 1], _model[shade + 2]) : Colors.White;
            }
            // Gouraud-shaded props keep both sides: where their side flag sits was not read.
            bool head = headBone >= 0 && Array.TrueForAll(index, i => owners[i] == headBone);
            int side = coloured || head || (_model[cursor + 2] & 1) != 0 ? 1 : 0;
            int flags = coloured ? 0 : _model[cursor + 2], rate = (flags >> 5) & 3;
            string blend = (flags & 0x10) == 0 ? "opaque" : rate switch { 0 => "mix", 2 => "subtract", _ => "add" };
            var passes = blend switch
            {
                "opaque" => new[] { (side, blend, 0, 1f) },
                "mix" => new[] { (side, blend, 1, 1f) },
                _ => new[] { (side, "opaque", 2, 1f), (side, blend, 3, rate == 3 ? 0.25f : 1f) },
            };
            foreach (var pass in passes)
            {
                var surface = Surface(pass);
                foreach (int c in quad ? new[] { 0, 1, 2, 1, 3, 2 } : new[] { 0, 1, 2 })
                {
                    surface.SetUV(uv[c]);
                    if (coloured) surface.SetColor(colour[c]);
                    surface.SetBones(new[] { owners[index[c]], 0, 0, 0 });
                    surface.SetWeights(new[] { 1f, 0, 0, 0 });
                    surface.AddVertex(positions[index[c]]);
                }
            }
            cursor += size;
        }
        var mesh = new ArrayMesh();
        // Opaque surfaces first, as the field draws its blended ones after them.
        foreach (var ((side, blend, mask, gain), surface) in surfaces.OrderBy(entry => entry.Key.Blend != "opaque").ThenBy(entry => entry.Key.Side))
        {
            if (!textures.TryGetValue((mask, gain), out var texture)) textures[(mask, gain)] = texture = Texture(out _, out _, mask, gain);
            // glTF keeps a material's name but not how it blends; the field restores that from the name.
            var material = new StandardMaterial3D
            {
                ResourceName = blend,
                AlbedoTexture = texture,
                Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
                AlphaScissorThreshold = 0.5f,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = side == 0 ? BaseMaterial3D.CullModeEnum.Back : BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            };
            if (blend != "opaque")
            {
                material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                material.BlendMode = blend switch { "add" => BaseMaterial3D.BlendModeEnum.Add, "subtract" => BaseMaterial3D.BlendModeEnum.Sub, _ => BaseMaterial3D.BlendModeEnum.Mix };
                material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled;
            }
            // A Gouraud colour of 0x80 leaves the texel as it is, so shaded props double the stored colour.
            if (coloured)
            {
                material.VertexColorUseAsAlbedo = true;
                material.VertexColorIsSrgb = true;
                material.AlbedoColor = new Color(2, 2, 2);
            }
            surface.SetMaterial(material);
            surface.Commit(mesh);
        }
        var instance = new MeshInstance3D { Name = "Mesh", Mesh = mesh, Skeleton = new NodePath("..") };
        instance.Skin = skeleton.CreateSkinFromRestTransforms();
        return instance;
    }

    /// <summary>Whether a tenth or more of the edges of the faces wholly on <paramref name="bone"/> (by place) are shared by more than two faces.</summary>
    private bool Layered(Vector3[] positions, int[] owners, int facePointer, int faces, int vertices, int bone)
    {
        var edges = new Dictionary<(Vector3, Vector3), int>();
        int cursor = facePointer;
        for (int face = 0; face < faces; face++)
        {
            int type = _model[cursor], size = _model[cursor + 1];
            int corners = type == 0x2C ? 4 : 3;
            var index = new int[corners];
            for (int c = 0; c < corners; c++) index[c] = Math.Min(vertices - 1, BitConverter.ToUInt16(_model, cursor + 4 + c * 2) / 4);
            if (Array.TrueForAll(index, i => owners[i] == bone))
                foreach (var triangle in corners == 4 ? new[] { (0, 1, 2), (1, 3, 2) } : new[] { (0, 1, 2) })
                {
                    var (a, b, c) = triangle;
                    foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
                    {
                        Vector3 x = positions[index[p]], y = positions[index[q]];
                        if (x == y) continue;
                        var edge = x.X < y.X || x.X == y.X && (x.Y < y.Y || x.Y == y.Y && x.Z < y.Z) ? (x, y) : (y, x);
                        edges[edge] = edges.GetValueOrDefault(edge) + 1;
                    }
                }
            cursor += Math.Max(1, size);
        }
        int many = 0;
        foreach (int count in edges.Values) if (count > 2) many++;
        return edges.Count > 0 && many * 10 >= edges.Count;
    }

    /// <summary>The model's texture: an 8-byte header, two palettes, then an 8-bit or 4-bit image. The first palette is the character's own.</summary>
    private ImageTexture? Texture(out int width, out int height, int mask, float gain)
    {
        if (_weaponTexture >= 0) return WeaponTexture(out width, out height, mask, gain);
        int magic = (int)BitConverter.ToUInt32(_model, 0xF4) + 0xF8;
        int at = magic + 8 + (int)BitConverter.ToUInt32(_model, magic + 4);
        // Byte 4 is the depth: 1 an 8-bit image, 0x10 a 4-bit one (two texels a byte, low nibble first; the wyvern,
        // 3A). Bytes 5 and 6 are the image's size in bytes, halved.
        bool packed = _model[at + 4] == 0x10;
        int rowBytes = _model[at + 5] * 2;
        width = packed ? rowBytes * 2 : rowBytes;
        height = _model[at + 6] * 2;
        // A model with an empty image is drawn in its colours alone.
        if (width == 0 || height == 0) { width = height = 1; return null; }
        int colours = _model[at + 7];
        int pixels = at + 8 + colours * 4;
        if (pixels + rowBytes * height > _model.Length) throw new InvalidDataException($"{_name}'s texture is truncated.");
        var data = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = packed ? (_model[pixels + y * rowBytes + x / 2] >> (x & 1) * 4) & 15 : _model[pixels + y * rowBytes + x];
                ushort colour = index < colours ? BitConverter.ToUInt16(_model, at + 8 + index * 2) : (ushort)0;
                Put(data, (y * width + x) * 4, colour, mask, gain);
            }
        return ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rgba8, data));
    }

    private ImageTexture WeaponTexture(out int width, out int height, int mask, float gain)
    {
        int at = _weaponTexture;
        width = _model[at + 5] * 2;
        height = 32;
        bool wide = _model[at + 7] == 96;
        int shared = wide ? 32 : 16, own = wide ? 64 : 32;
        int palettes = at + 8, image = palettes + (shared + own * 7) * 2;
        if (_weaponPalette is < 0 or >= 7 || image + width * height > _model.Length) throw new InvalidDataException($"{_name}'s texture is malformed.");
        ushort Colour(int index) => (ushort)(index < shared ? BitConverter.ToUInt16(_model, palettes + index * 2)
            : index < shared + own ? BitConverter.ToUInt16(_model, palettes + shared * 2 + (_weaponPalette * own + index - shared) * 2) : 0);
        var data = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) Put(data, (y * width + x) * 4, Colour(_model[image + y * width + x]), mask, gain);
        return ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rgba8, data));
    }

    /// <summary>
    /// Decodes one SEQ set. Each bone stream is a big-endian base rotation (half units) followed by velocity keys
    /// covering the animation's frames; an axis a key omits keeps its previous velocity.
    /// </summary>
    private IEnumerable<Animation> Animations(byte[] seq)
    {
        int slots = BitConverter.ToUInt16(seq, 0), bones = seq[2];
        if (bones != _bones) throw new InvalidDataException($"{_name}'s animations are for another skeleton ({bones} bones, not {_bones}).");
        int table = (int)BitConverter.ToUInt32(seq, 12) + 8 + slots;
        int stride = bones * 4 + 10, count = (table - slots - 16) / stride;
        for (int index = 0; index < count; index++)
        {
            int header = 16 + index * stride;
            int frames = BitConverter.ToUInt16(seq, header);
            int other = (sbyte)seq[header + 2];
            var animation = new Animation { Length = Math.Max(1, frames - 1) / FramesPerSecond, LoopMode = Animation.LoopModeEnum.Linear };
            // The translation stream places the root bone above the actor's floor position: its first value in map
            // units, and its keyed deltas in half units. An animation built on a base animation starts from the base's
            // first value, as its rotations do.
            {
                int stream = table + BitConverter.ToUInt16(seq, header + 6);
                int baseStream = other >= 0 && other < count ? table + BitConverter.ToUInt16(seq, 16 + other * stride + 6) : stream;
                var position = new Vector3(BigSigned(seq, baseStream), BigSigned(seq, baseStream + 2), BigSigned(seq, baseStream + 4));
                int track = animation.AddTrack(Animation.TrackType.Position3D);
                animation.TrackSetPath(track, "Space/Skeleton:b0");
                animation.PositionTrackInsertKey(track, 0, position);
                // Its own stream is then only keys; a stream of its own opens with its first value.
                int cursor = baseStream == stream ? stream + 6 : stream, frame = 0;
                var velocity = Vector3.Zero;
                while (frame < frames - 1)
                {
                    var (x, y, z, span, next) = Key(seq, cursor);
                    if (span == 0) break;
                    cursor = next;
                    velocity = new Vector3(x ?? velocity.X, y ?? velocity.Y, z ?? velocity.Z);
                    position += velocity * span / 2;
                    frame += span;
                    animation.PositionTrackInsertKey(track, frame / FramesPerSecond, position);
                }
            }
            // The movement stream (header +8, the clip's own; BATTLE.PRG 0x800AFA28) moves the actor itself: it opens with a
            // value (doubled, as the others), each frame adds its keyed step, and each frame the actor moves by a quarter
            // of the value, in map axes. Kept as the distance moved by each frame's end.
            int moving = BitConverter.ToUInt16(seq, header + 8);
            if (moving != 0)
            {
                int at = table + moving;
                var value = new Vector3(BigSigned(seq, at), BigSigned(seq, at + 2), BigSigned(seq, at + 4)) * 2;
                int cursor = at + 6, frame = 0;
                var velocity = Vector3.Zero;
                var moved = Vector3.Zero;
                var moves = new Godot.Collections.Array();
                while (frame < frames - 1)
                {
                    var (x, y, z, span, next) = Key(seq, cursor);
                    if (span == 0) break;
                    cursor = next;
                    velocity = new Vector3(x ?? velocity.X, y ?? velocity.Y, z ?? velocity.Z);
                    for (int k = 0; k < span && frame < frames - 1; k++, frame++)
                    {
                        value += velocity;
                        moved += new Vector3(MathF.Truncate(value.X / 4), MathF.Truncate(value.Y / 4), MathF.Truncate(value.Z / 4));
                        moves.Add(moved);
                    }
                }
                animation.SetMeta("movement", moves);
            }
            for (int bone = 0; bone < bones; bone++)
            {
                int stream = table + BitConverter.ToUInt16(seq, header + 10 + bone * 2);
                int baseStream = other >= 0 && other < count ? table + BitConverter.ToUInt16(seq, 16 + other * stride + 10 + bone * 2) : stream;
                var rotation = new Vector3(BigSigned(seq, baseStream), BigSigned(seq, baseStream + 2), BigSigned(seq, baseStream + 4)) * 2;
                int track = animation.AddTrack(Animation.TrackType.Rotation3D);
                animation.TrackSetPath(track, $"Space/Skeleton:b{bone}");
                animation.RotationTrackInsertKey(track, 0, Rotation(rotation));
                int cursor = baseStream == stream ? stream + 6 : stream, frame = 0;
                var velocity = Vector3.Zero;
                while (frame < frames - 1)
                {
                    var (x, y, z, span, next) = Key(seq, cursor);
                    if (span == 0) break;
                    cursor = next;
                    velocity = new Vector3(x ?? velocity.X, y ?? velocity.Y, z ?? velocity.Z);
                    rotation += velocity * span;
                    frame += span;
                    animation.RotationTrackInsertKey(track, frame / FramesPerSecond, Rotation(rotation));
                }
            }
            // Each bone's scale (64 is whole), after its rotation streams (BATTLE.PRG 0x800ACFA0 sets it as the clip
            // starts, 0x800AFA28 steps it; 0x800B28A8 scales the bone's matrix, so its children follow): with header
            // byte 3's bit 0, the clip (or its base) gives each a starting scale, three bytes, or after a 254, 1 a
            // keyed value; with bit 1 a stream of keys then steps it, as rotations step.
            int startHeader = other >= 0 && other < count ? 16 + other * stride : header;
            bool starts = (seq[startHeader + 3] & 1) != 0, steps = (seq[header + 3] & 2) != 0;
            for (int bone = 0; bone < bones && (starts || steps); bone++)
            {
                var scale = new Vector3(64, 64, 64);
                int offset = 0;
                if (starts)
                {
                    int at = table + BitConverter.ToUInt16(seq, startHeader + 10 + (bones + bone) * 2);
                    if (seq[at] == 254 && seq[at + 1] == 1) { scale = new Vector3(BigSigned(seq, at + 2), BigSigned(seq, at + 4), BigSigned(seq, at + 6)); offset = 8; }
                    else { scale = new Vector3(seq[at], seq[at + 1], seq[at + 2]); offset = 3; }
                }
                int track = animation.AddTrack(Animation.TrackType.Scale3D);
                animation.TrackSetPath(track, $"Space/Skeleton:b{bone}");
                // A bone scaled to nothing is hidden (Callo's idle starts bone 26 at 0); the skeleton takes a scale a
                // hair above zero, as a zero one breaks its pose.
                animation.ScaleTrackInsertKey(track, 0, (scale / 64).Max(Vector3.One / 4096));
                if (!steps) continue;
                int stream = table + BitConverter.ToUInt16(seq, header + 10 + (bones + bone) * 2);
                int cursor = startHeader == header ? stream + offset : stream, frame = 0;
                var velocity = Vector3.Zero;
                while (frame < frames - 1)
                {
                    var (x, y, z, span, next) = Key(seq, cursor);
                    if (span == 0) break;
                    cursor = next;
                    velocity = new Vector3(x ?? velocity.X, y ?? velocity.Y, z ?? velocity.Z);
                    scale += velocity * span;
                    frame += span;
                    animation.ScaleTrackInsertKey(track, frame / FramesPerSecond, (scale / 64).Max(Vector3.One / 4096));
                }
            }
            yield return animation;
        }
    }

    private static (float? X, float? Y, float? Z, int Frames, int Next) Key(byte[] seq, int at)
    {
        int code = seq[at++];
        if (code == 0) return (null, null, null, 0, at);
        float? x = null, y = null, z = null;
        int frames;
        if ((code & 0xE0) != 0)
        {
            frames = code & 0x1F;
            frames = frames == 0x1F ? 0x20 + seq[at++] : frames + 1;
        }
        else
        {
            frames = code & 3;
            frames = frames == 3 ? 4 + seq[at++] : frames + 1;
            code <<= 3;
            int half = BigSigned(seq, at);
            at += 2;
            if ((half & 4) != 0)
            {
                x = half >> 3; code &= 0x60;
                if ((half & 2) != 0) { y = BigSigned(seq, at); at += 2; code &= 0xA0; }
                if ((half & 1) != 0) { z = BigSigned(seq, at); at += 2; code &= 0xC0; }
            }
            else if ((half & 2) != 0)
            {
                y = half >> 3; code &= 0xA0;
                if ((half & 1) != 0) { z = BigSigned(seq, at); at += 2; code &= 0xC0; }
            }
            else if ((half & 1) != 0)
            {
                z = half >> 3; code &= 0xC0;
            }
        }
        if ((code & 0x80) != 0) x = (sbyte)seq[at++];
        if ((code & 0x40) != 0) y = (sbyte)seq[at++];
        if ((code & 0x20) != 0) z = (sbyte)seq[at++];
        return (x, y, z, frames, at);
    }

    /// <summary>Rz·Ry·Rx for retail angles in 8192 units per turn.</summary>
    private static Quaternion Rotation(Vector3 angles)
    {
        var turn = angles * (Mathf.Pi / 4096f);
        var basis = new Basis(Vector3.Back, turn.Z) * new Basis(Vector3.Up, turn.Y) * new Basis(Vector3.Right, turn.X);
        return basis.GetRotationQuaternion();
    }
}
