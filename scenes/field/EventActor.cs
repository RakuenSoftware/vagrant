// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// One actor as a retail event drives it: position and facing in map units (y down, 4096 units per turn), the
/// teleport, walk, glide and turn motions with their retail speed rules, animation by bank and number, and attachment
/// to another actor's bone. It steps on the 30 Hz event clock and writes its node's transform for the field view.
/// </summary>
public sealed class EventActor
{
    /// <summary>Waiting bits, as the retail actor wait tests them: walking a route, moving, turning, scaling, animation not finished.</summary>
    public const int Routing = 0x01, Moving = 0x08, Turning = 0x10, Scaling = 0x20, Animating = 0x80;
    /// <summary>Route walking pace in units a frame: the walk speed. The retail path walker was not read, so this is an inference.</summary>
    public const float RouteSpeed = 9;
    /// <summary>Default walk speed (frames per 128 units) and turn speed (frames per full turn) when a script leaves them unset.</summary>
    public const int DefaultMoveSpeed = 16, DefaultTurnSpeed = 32;

    public readonly string Id;
    public readonly int Character;
    public readonly Node3D Node;
    public readonly Skeleton3D? Skeleton;
    public readonly AnimationPlayer? Player;

    public Vector3 Position;
    public float Facing;
    public Vector3 Angles;
    /// <summary>
    /// The scale opcode 2e sets (4096 is 1), easing over its frames, which status waits see. Retail keeps it at the
    /// actor's +0x2C (0x800AABD0) and draws with it: 0x800B28A8 scales the model's matrix by it, and the root offset
    /// is scaled with it, so the actor looks that much bigger or smaller about its place; its size box (0x800B00B0) takes it
    /// too.
    /// </summary>
    public Vector3 Scale = Vector3.One;
    public bool Visible;
    /// <summary>The light colour the actor is drawn with (128 is unity), and whether an event set it explicitly.</summary>
    public Vector3 Light = -Vector3.One;
    /// <summary>How far its light is pushed toward <see cref="GlowTint"/> (0 to 1), as Ashley glows while the attack sphere opens.</summary>
    public float Glow;
    public Vector3 GlowTint = Vector3.One;
    public bool Tinted;
    /// <summary>The radius of the shadow under it (its model's), and the disc that draws it; none at 0.</summary>
    public int ShadowRadius;
    public MeshInstance3D? Shadow;
    /// <summary>Whether its shadow is drawn: 23's bit 6 (kept at +8 bit 15, which 0x800B390C tests before 0x800B4B14), on until a script clears it.</summary>
    public bool ShadowShown = true;
    public EventActor? Parent;
    public int ParentBone;

    /// <summary>Whether its clip's script has shown (true) or hidden (false) its held weapon; null leaves it to the battle.</summary>
    public bool? WeaponOut;

    /// <summary>The clip whose script events have been run, and the last of its frames they were run to.</summary>
    public string ScriptClip = "";
    /// <summary>The clip and SEQ frame up to which its sound operations (10) have been played.</summary>
    public string SoundClip = "";
    public int SoundFrame = -1;
    public int ScriptFrame = -1;
    /// <summary>The clip and SEQ frame up to which its movement stream has moved the actor.</summary>
    public string MoveClip = "";
    public int MoveFrame;

    private Vector3 _moveTarget;
    private int _moveFrames;
    private float _turnTarget;
    private int _turnFrames;
    private Vector3 _scaleStart, _scaleEnd;
    private int _scaleTick, _scaleFrames;
    private Vector3 _anglesStart, _anglesEnd;
    private int _anglesTick, _anglesFrames;
    // A walk is a turn toward the destination, a straight glide and a final turn.
    private int _walkPhase;
    private float _walkHeading, _walkSpeed, _walkTurnSpeed;
    private Vector3 _walkTarget;
    private bool _animationFinished = true;
    private (string Clip, int Blend)? _queued;
    private readonly System.Collections.Generic.List<Vector3> _route = new();

    public EventActor(string id, int character, Node3D node)
    {
        Id = id;
        Character = character;
        Node = node;
        Skeleton = node.FindChildren("*", "Skeleton3D", true, false) is { Count: > 0 } skeletons ? (Skeleton3D)skeletons[0] : null;
        Player = node.FindChildren("*", "AnimationPlayer", true, false) is { Count: > 0 } players ? (AnimationPlayer)players[0] : null;
        // The shared field advances a rig's player on its presentation clock (its process mode is manual); advancing it
        // in the idle step as well would play every clip at twice its speed.
        if (Player != null) Player.AnimationFinished += _ => _animationFinished = true;
    }

    // The rig's meshes as built, each surface's arrays with its corners' bones, for drawing the rig as retail's GTE
    // and GPU would show it near the camera.
    private sealed class Surfaces
    {
        public required MeshInstance3D Instance;
        public required Godot.Collections.Array[] Arrays;
        public required Vector3[][] Vertices;
        public required int[][] Bones, Faces;
        public MeshInstance3D? Stand;
        // The mesh these were read from, and each corner's normal turned by its bone's pose, this frame.
        public Mesh? Source;
        // Each skin bind's piece: the bounds of its corners in its bone's own frame.
        public System.Collections.Generic.Dictionary<int, Aabb> Pieces = new();
        // Read once as the rig is first worked: each surface's normals, each skin bind's bone and bind pose.
        public Vector3[]?[] Normals = Array.Empty<Vector3[]?>();
        public int[] BindBones = Array.Empty<int>();
        public Transform3D[] BindPoses = Array.Empty<Transform3D>();
        public Vector3[][]? Turned;
        // Whether this hid the mesh (for the stand-in, or the whole model), so it gives back only what it took.
        public bool Hidden;
    }

    private System.Collections.Generic.List<Surfaces>? _surfaces;
    private Transform3D[] _poses = Array.Empty<Transform3D>();

    /// <summary>
    /// Draws the rig as the PSX would where the camera stands close to it (BATTLE.PRG's character renderer through the
    /// GTE and GPU). The GTE divides the projection distance by a corner's depth only down to half of it, so a corner
    /// nearer than H / 2 (the depth after the view's scale, <paramref name="far"/> over 4096; behind the eye too) is
    /// projected as if it stood there, and its screen place is held within -1024 to 1023; the GPU then drops a face more
    /// than 1023 pixels across or 511 down (the stand in MAP023's wine cellar, 20 units from the eye at H 288, shows only
    /// a dark sliver at the frame's left). Each corner goes through the rig's current pose; a stand-in mesh with each
    /// corner where retail puts it on the screen, at its own depth, is drawn in the rig's place while any corner is that
    /// near. Faces facing away on the screen are culled as retail's normal clip does, by their materials. A model the
    /// eye stands inside is not drawn at all (footage: with the camera in the stand, nothing of it shows in the
    /// frame's middle, where its nearer faces would fall, while close-ups from outside a character show it; how
    /// retail tests it was not read).
    /// </summary>
    public void ProjectAsRetail(Camera3D camera, float projection, float far)
    {
        if (Skeleton == null) return;
        // A rig whose meshes have been swapped (the Settings' enhanced characters) is read again.
        // A rig whose meshes were replaced (a redrawn rig) is read again too.
        if (_surfaces != null && _surfaces.Exists(e => !GodotObject.IsInstanceValid(e.Instance) || e.Instance.Mesh != e.Source && e.Instance.Mesh != null))
        {
            foreach (var entry in _surfaces)
            {
                if (entry.Stand != null && GodotObject.IsInstanceValid(entry.Stand)) entry.Stand.QueueFree();
                entry.Stand = null;
                if (entry.Hidden && GodotObject.IsInstanceValid(entry.Instance)) entry.Instance.Visible = true;
                entry.Hidden = false;
            }
            _surfaces = null;
        }
        if (_surfaces == null)
        {
            _surfaces = new();
            foreach (var found in Node.FindChildren("*", "MeshInstance3D", true, false))
            {
                var instance = (MeshInstance3D)found;
                if (instance == Shadow || instance.Mesh is not ArrayMesh whole || instance.Skin == null) continue;
                int count = whole.GetSurfaceCount();
                var entry = new Surfaces { Instance = instance, Arrays = new Godot.Collections.Array[count], Vertices = new Vector3[count][], Bones = new int[count][], Faces = new int[count][] };
                for (int i = 0; i < count; i++)
                {
                    var arrays = whole.SurfaceGetArrays(i);
                    var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                    var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
                    int per = vertices.Length > 0 ? bones.Length / vertices.Length : 0;
                    // The rigs bind each corner to one bone: its heaviest.
                    var bone = new int[vertices.Length];
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        int best = 0;
                        for (int k = 1; k < per; k++) if (weights[v * per + k] > weights[v * per + best]) best = k;
                        bone[v] = per > 0 ? bones[v * per + best] : 0;
                    }
                    entry.Arrays[i] = arrays;
                    entry.Vertices[i] = vertices;
                    entry.Bones[i] = bone;
                    entry.Faces[i] = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
                        ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Range(0, vertices.Length))
                        : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                }
                entry.Source = whole;
                entry.Normals = new Vector3[]?[count];
                for (int i = 0; i < count; i++)
                    entry.Normals[i] = entry.Arrays[i][(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? null : entry.Arrays[i][(int)Mesh.ArrayType.Normal].AsVector3Array();
                int binds = instance.Skin.GetBindCount();
                entry.BindBones = new int[binds];
                entry.BindPoses = new Transform3D[binds];
                for (int b = 0; b < binds; b++)
                {
                    int bone = instance.Skin.GetBindBone(b);
                    entry.BindBones[b] = bone >= 0 ? bone : Skeleton.FindBone(instance.Skin.GetBindName(b));
                    entry.BindPoses[b] = instance.Skin.GetBindPose(b);
                }
                for (int i = 0; i < count; i++)
                    for (int v = 0; v < entry.Vertices[i].Length; v++)
                    {
                        int bind = entry.Bones[i][v];
                        var local = instance.Skin.GetBindPose(bind) * entry.Vertices[i][v];
                        entry.Pieces[bind] = entry.Pieces.TryGetValue(bind, out var box) ? box.Expand(local) : new Aabb(local, Vector3.Zero);
                    }
                _surfaces.Add(entry);
            }
        }
        float units = Installation.MapConversion.UnitsPerMetre;
        float least = projection / 2 * 4096 / far;
        var eye = camera.GlobalTransform.AffineInverse();
        var from = camera.GlobalPosition;
        void Restore()
        {
            foreach (var entry in _surfaces!)
            {
                if (entry.Stand != null) { entry.Stand.QueueFree(); entry.Stand = null; }
                if (entry.Hidden) { entry.Instance.Visible = true; entry.Hidden = false; }
            }
        }
        void Hide()
        {
            foreach (var entry in _surfaces!)
            {
                if (entry.Stand != null) { entry.Stand.QueueFree(); entry.Stand = null; }
                if (entry.Instance.Visible) { entry.Instance.Visible = false; entry.Hidden = true; }
            }
        }
        // A rig far from the eye is left as it is at once; only one some bone of which stands within reach of the limit
        // (a limb's length beyond it) is worked. Each bone's posed frame is read once a frame.
        if (!Node.Visible || Node.GlobalPosition.DistanceTo(from) * units > least + 2048) { Restore(); return; }
        int boneCount = Skeleton.GetBoneCount();
        if (_poses.Length != boneCount) _poses = new Transform3D[boneCount];
        float nearest = float.MaxValue;
        var rig = Skeleton.GlobalTransform;
        for (int b = 0; b < boneCount; b++)
        {
            _poses[b] = rig * Skeleton.GetBoneGlobalPose(b);
            nearest = Math.Min(nearest, -(eye * _poses[b].Origin).Z * units);
        }
        if (nearest > least + 160) { Restore(); return; }
        // Every corner in the camera's frame (retail units) through the current pose.
        var locals = new System.Collections.Generic.List<Vector3[][]>();
        bool any = false, front = false, below = false;
        foreach (var entry in _surfaces)
        {
            var instance = entry.Instance;
            var surfaces = new Vector3[entry.Arrays.Length][];
            entry.Turned = new Vector3[entry.Arrays.Length][];
            bool shown = instance.IsInsideTree() && (instance.Visible || entry.Hidden);
            for (int surface = 0; shown && surface < entry.Arrays.Length; surface++)
            {
                var vertices = entry.Vertices[surface];
                var local = surfaces[surface] = new Vector3[vertices.Length];
                var normals = entry.Normals[surface];
                var turned = entry.Turned[surface] = new Vector3[vertices.Length];
                for (int v = 0; v < vertices.Length; v++)
                {
                    int bind = entry.Bones[surface][v];
                    var frame = _poses[Math.Max(0, entry.BindBones[bind])] * entry.BindPoses[bind];
                    var world = frame * vertices[v];
                    if (normals != null) turned[v] = (frame.Basis * normals[v]).Normalized();
                    local[v] = eye * world * units;
                    any = true;
                    front |= local[v].Z < 0;
                    below |= -local[v].Z < least;
                }
            }
            locals.Add(surfaces);
        }
        if (!any) { Restore(); return; }
        // With every corner behind the eye the model is not drawn (the corners' depths, ORed, come back 0); nor is one
        // the eye stands inside.
        if (!front || Inside(from)) { Hide(); return; }
        if (!below) { Restore(); return; }
        float nearClip = Math.Max(camera.Near * units * 1.05f, 1);
        for (int m = 0; m < _surfaces.Count; m++)
        {
            var entry = _surfaces[m];
            var instance = entry.Instance;
            if (!instance.IsInsideTree() || !(instance.Visible || entry.Hidden) || System.Array.IndexOf(locals[m], null) >= 0) continue;
            var mesh = new ArrayMesh();
            for (int surface = 0; surface < entry.Arrays.Length; surface++)
            {
                var local = locals[m][surface];
                var placed = new Vector3[local.Length];
                var screen = new Vector2[local.Length];
                for (int v = 0; v < local.Length; v++)
                {
                    float depth = -local[v].Z, divisor = Math.Max(depth, least);
                    // Retail's screen place (9/8 across), held as the GTE holds it.
                    float x = Math.Clamp(projection * local[v].X * 9 / 8 / divisor, -1024 - 160, 1023 - 160), y = Math.Clamp(projection * local[v].Y / divisor, -1024 - 112, 1023 - 112);
                    screen[v] = new Vector2(x, y);
                    float shown = Math.Max(depth, nearClip);
                    placed[v] = camera.GlobalTransform * (new Vector3(x * 8 / 9 / projection * shown, y / projection * shown, -shown) / units);
                }
                var kept = new System.Collections.Generic.List<int>(entry.Faces[surface].Length);
                var faces = entry.Faces[surface];
                for (int f = 0; f + 2 < faces.Length; f += 3)
                {
                    Vector2 a = screen[faces[f]], b = screen[faces[f + 1]], c = screen[faces[f + 2]];
                    if (Math.Max(a.X, Math.Max(b.X, c.X)) - Math.Min(a.X, Math.Min(b.X, c.X)) > 1023 || Math.Max(a.Y, Math.Max(b.Y, c.Y)) - Math.Min(a.Y, Math.Min(b.Y, c.Y)) > 511) continue;
                    kept.Add(faces[f]); kept.Add(faces[f + 1]); kept.Add(faces[f + 2]);
                }
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                var source = entry.Arrays[surface];
                arrays[(int)Mesh.ArrayType.Vertex] = placed;
                foreach (var kind in new[] { Mesh.ArrayType.Color, Mesh.ArrayType.TexUV, Mesh.ArrayType.TexUV2 })
                    arrays[(int)kind] = source[(int)kind];
                // The stand-in stands in the world as posed, so its normals are the posed ones.
                if (source[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil && entry.Turned?[surface] is { } posedNormals) arrays[(int)Mesh.ArrayType.Normal] = posedNormals;
                arrays[(int)Mesh.ArrayType.Index] = kept.Count > 0 ? kept.ToArray() : new[] { 0, 0, 0 };
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(surface, instance.GetActiveMaterial(surface));
            }
            if (entry.Stand == null)
            {
                entry.Stand = new MeshInstance3D { Name = "RetailView", TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                instance.AddSibling(entry.Stand);
            }
            entry.Stand.GlobalTransform = Transform3D.Identity;
            entry.Stand.Mesh = mesh;
            if (instance.Visible) { instance.Visible = false; entry.Hidden = true; }
        }
    }

    /// <summary>
    /// Whether a point stands inside one of the rig's pieces: within the bounds of a bone's own corners, in that bone's
    /// posed frame (a box round the whole posed body would take a camera merely beside it, as the Duke's knight's
    /// close pass in MAP001 showed, hiding him in mid-shot).
    /// </summary>
    private bool Inside(Vector3 point)
    {
        if (Skeleton == null || _surfaces == null) return false;
        foreach (var entry in _surfaces)
        {
            if (!entry.Instance.IsInsideTree()) continue;
            foreach (var (bind, box) in entry.Pieces)
            {
                if (box.Size.X <= 0 || box.Size.Y <= 0 || box.Size.Z <= 0 || bind >= entry.BindBones.Length) continue;
                var frame = _poses[Math.Max(0, entry.BindBones[bind])];
                if (box.HasPoint(frame.AffineInverse() * point)) return true;
            }
        }
        return false;
    }

    public int Waiting =>
        (_route.Count > 0 ? Routing : 0) | (_moveFrames > 0 || _walkPhase is 1 or 2 ? Moving : 0) | (_turnFrames > 0 || _walkPhase != 0 ? Turning : 0)
        | (_scaleTick < _scaleFrames ? Scaling : 0) | (_animationFinished ? 0 : Animating);

    /// <summary>The facing that points from the actor toward a map position.</summary>
    public float FacingToward(Vector3 point)
    {
        float dx = point.X - Position.X, dz = point.Z - Position.Z;
        return Mathf.PosMod(3072 - Mathf.Atan2(dz, dx) * 4096 / Mathf.Tau, 4096);
    }

    /// <summary>Retail 26: with both speeds zero a teleport, otherwise turn, walk and turn to a final heading.</summary>
    public void Walk(Vector3 target, float heading, int speed, int turnSpeed)
    {
        if (speed == 0 && turnSpeed == 0)
        {
            Facing = heading >= 0 ? heading : (target - Position).LengthSquared() > 0 ? FacingToward(target) : Facing;
            Position = target;
            _moveFrames = _turnFrames = _walkPhase = 0;
            return;
        }
        _walkTarget = target;
        _walkHeading = heading;
        _walkSpeed = speed < 0 ? DefaultMoveSpeed : speed;
        _walkTurnSpeed = turnSpeed < 0 ? DefaultTurnSpeed : turnSpeed;
        _walkPhase = 1;
        TurnTo(FacingToward(target), 0, _walkTurnSpeed);
    }

    /// <summary>Retail 2a: walks a route of points, facing each in turn; an empty route ends at once.</summary>
    public void Follow(System.Collections.Generic.IEnumerable<Vector3> route)
    {
        _route.Clear();
        _route.AddRange(route);
    }

    /// <summary>Retail 28: a straight glide that keeps the facing.</summary>
    public void Glide(Vector3 target, int speed)
    {
        float rate = speed < 0 ? DefaultMoveSpeed : speed;
        _moveTarget = target;
        _moveFrames = (int)Mathf.Ceil((target - Position).Length() * rate / 128f);
        if (_moveFrames == 0) Position = target;
    }

    /// <summary>Retail 2b: turn to a heading the shortest way, or the way <paramref name="direction"/> forces.</summary>
    public void TurnTo(float heading, int direction, float speed)
    {
        float delta = Mathf.PosMod(heading - Facing, 4096);
        if (direction < 0 || direction == 0 && delta > 2048) delta -= 4096;
        _turnTarget = Facing + delta;
        _turnFrames = speed < 0 ? (int)Mathf.Ceil(Mathf.Abs(delta) * DefaultTurnSpeed / 4096f) : (int)Mathf.Ceil(Mathf.Abs(delta) * speed / 4096f);
        if (_turnFrames == 0) Facing = Mathf.PosMod(_turnTarget, 4096);
    }

    public void Rotate(Vector3 angles, bool relative, int speed)
    {
        _anglesStart = Angles;
        _anglesEnd = relative ? Angles + angles : angles;
        float largest = Math.Max(Math.Abs(_anglesEnd.X - Angles.X), Math.Max(Math.Abs(_anglesEnd.Y - Angles.Y), Math.Abs(_anglesEnd.Z - Angles.Z)));
        _anglesTick = 0;
        _anglesFrames = speed <= 0 ? 0 : (int)Mathf.Ceil(largest * speed / 4096f);
        if (_anglesFrames == 0) Angles = _anglesEnd;
    }

    public void ScaleTo(Vector3 scale, int frames)
    {
        _scaleStart = Scale;
        _scaleEnd = scale;
        _scaleTick = 0;
        _scaleFrames = frames;
        if (frames == 0) Scale = scale;
    }

    /// <summary>
    /// Plays a clip, from <paramref name="skipFrames"/> frames in (retail 35). Base and battle clips loop; an event
    /// clip loops when its animation script starts it over (operation 1, as a walk does) and otherwise holds its last
    /// frame and reports when it ends, as does any clip played <paramref name="once"/>. Playing a clip clears any
    /// queued one.
    /// </summary>
    public bool Animate(string clip, int blendFrames, int skipFrames = 0, bool once = false)
    {
        _queued = null;
        if (Player == null) return false;
        if (!HasClip(clip)) return false;
        var animation = Player.GetAnimation(clip);
        animation.LoopMode = once || clip.StartsWith("esq", StringComparison.Ordinal) && !animation.GetMeta("loops", false).AsBool() ? Animation.LoopModeEnum.None : Animation.LoopModeEnum.Linear;
        _animationFinished = animation.LoopMode != Animation.LoopModeEnum.None;
        Player.Play(clip, blendFrames >= 2 ? blendFrames / 30.0 : 0);
        if (skipFrames > 0) Player.Seek(Math.Min(animation.Length, skipFrames / Installation.CharacterConversion.FramesPerSecond), true);
        // The new clip's pose shows at once: left to the next advance, a character placed on a cut stood a frame in
        // the old clip's pose (its root as far off as that clip had carried it).
        else Player.Advance(0);
        return true;
    }

    /// <summary>Adds an animation set (an event's "esqnnn", Ashley's "btn" and "artna") to the actor's rig.</summary>
    public Action<EventActor, string>? LoadSet;

    /// <summary>Whether the rig has a clip; a set loaded on demand joins it the first time one of its clips is asked for.</summary>
    public bool HasClip(string clip)
    {
        if (Player == null) return false;
        if (!Player.HasAnimation(clip) && clip.IndexOf('_') is > 0 and int split && clip[..split] is var set && set is not ("base" or "battle")) LoadSet?.Invoke(this, set);
        return Player.HasAnimation(clip);
    }

    /// <summary>Retail 32: a clip that starts by itself when the current one reaches its last frame.</summary>
    public void Queue(string clip, int blendFrames) => _queued = (clip, blendFrames);

    /// <summary>Advances motion by one event frame.</summary>
    public void Step()
    {
        if (_queued is { } queued && Player != null && (!Player.IsPlaying() || Player.CurrentAnimationPosition >= Player.CurrentAnimationLength - 0.5 / Installation.CharacterConversion.FramesPerSecond))
            Animate(queued.Clip, queued.Blend);
        if (_turnFrames > 0)
        {
            Facing += (_turnTarget - Facing) / _turnFrames--;
            if (_turnFrames == 0) Facing = Mathf.PosMod(_turnTarget, 4096);
        }
        if (_moveFrames > 0)
        {
            Position += (_moveTarget - Position) / _moveFrames--;
        }
        if (_route.Count > 0)
        {
            var offset = _route[0] - Position;
            float distance = new Vector2(offset.X, offset.Z).Length();
            if (distance > 0.5f) Facing = FacingToward(_route[0]);
            if (distance <= RouteSpeed) { Position = _route[0]; _route.RemoveAt(0); }
            else Position += offset / distance * RouteSpeed;
        }
        if (_scaleTick < _scaleFrames) Scale = _scaleStart.Lerp(_scaleEnd, ++_scaleTick / (float)_scaleFrames);
        if (_anglesTick < _anglesFrames) Angles = _anglesStart.Lerp(_anglesEnd, ++_anglesTick / (float)_anglesFrames);
        switch (_walkPhase)
        {
            case 1 when _turnFrames == 0:
                _walkPhase = 2;
                Glide(_walkTarget, (int)_walkSpeed);
                break;
            case 2 when _moveFrames == 0:
                _walkPhase = 3;
                if (_walkHeading >= 0) TurnTo(_walkHeading, 0, _walkTurnSpeed);
                break;
            case 3 when _turnFrames == 0:
                _walkPhase = 0;
                break;
        }
    }

    /// <summary>
    /// Retail 5d: the body's idle sway. While on, a count climbs to the period (twice the script's byte) and falls
    /// back again, and half of it turns the body about y (BATTLE.PRG's root-bone pass near 0x800b2a40).
    /// </summary>
    public int SwayPeriod, SwayCount;
    /// <summary>The flags retail 23 sets (bits 0 and 1 both set draw it).</summary>
    public int Flags;

    /// <summary>
    /// Retail 34, 38 and 39: the head turns towards a point or another actor, at most 800 either way and by the speed
    /// a frame (0x1000 at once), while the actor plays its base idle or walk (BATTLE.PRG 0x8009fc60).
    /// </summary>
    public Vector3? LookPoint;
    public EventActor? LookTarget;
    public int LookSpeed = 0x1000;
    public HeadTurn? Head;

    public void StepLook()
    {
        if (Head == null) return;
        string clip = Player?.CurrentAnimation ?? "";
        bool free = clip == $"base_{FieldPlayer.Idle}" || clip == $"base_{FieldPlayer.Walk}";
        var point = LookTarget?.Position ?? LookPoint;
        float want = 0;
        if (free && point is { } target && (target - Position) is { } offset && new Vector2(offset.X, offset.Z).Length() > 1)
        {
            want = Mathf.Clamp(Mathf.Wrap(FacingToward(target) - Facing, -2048, 2048), -800, 800);
        }
        float step = LookSpeed <= 0 ? 0x1000 : LookSpeed;
        Head.Yaw = Mathf.MoveToward(Head.Yaw, want, step);
    }
    private bool _swayFalling;

    /// <summary>Steps the sway one event frame.</summary>
    public void StepSway()
    {
        if (SwayPeriod == 0) { SwayCount = 0; return; }
        if (!_swayFalling) { if (++SwayCount >= SwayPeriod) { SwayCount = SwayPeriod; _swayFalling = true; } }
        else if (--SwayCount <= 0) { SwayCount = 0; _swayFalling = false; }
    }

    /// <summary>Retail space for a node holding a converted rig: a half turn about x and 128 map units per engine unit.</summary>
    public static readonly Transform3D RetailSpace = new(new Basis(Vector3.Right, Mathf.Pi).Scaled(Vector3.One / Installation.MapConversion.UnitsPerMetre), Vector3.Zero);
    /// <summary>A character rig's own space: retail map space at the size models are drawn (half).</summary>
    public static readonly Transform3D ModelSpace = new(new Basis(Vector3.Right, Mathf.Pi).Scaled(Vector3.One * Installation.CharacterConversion.ModelScale / Installation.MapConversion.UnitsPerMetre), Vector3.Zero);

    /// <summary>The retail-space transform of the actor's own frame: position, facing (a turn about y) and extra angles.</summary>
    private Transform3D Local()
    {
        float unit = Mathf.Tau / 4096f;
        var basis = new Basis(Vector3.Up, (Facing + SwayCount / 2) * unit) * new Basis(Vector3.Back, Angles.Z * unit) * new Basis(Vector3.Up, Angles.Y * unit) * new Basis(Vector3.Right, Angles.X * unit);
        return new Transform3D(basis * Basis.FromScale(Scale), Position);
    }

    /// <summary>Writes the node's transform. An attached actor follows its parent's bone; its own motion is then bone-local.</summary>
    public void Apply()
    {
        Node.Visible = Visible;
        if (Parent?.Skeleton is { } skeleton && Parent.SkeletonBone(ParentBone) is >= 0 and var held)
        {
            var bone = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(held);
            Node.GlobalTransform = bone * Local() * ModelSpace.AffineInverse();
            return;
        }
        Node.Transform = RetailSpace * Local() * RetailSpace.AffineInverse();
    }

    /// <summary>
    /// The skeleton's index of the model's bone <paramref name="bone"/> (its record's place in the SHP, as scripts,
    /// effects and the rig's definitions number bones): glTF reorders a skeleton depth first, so a bone is found by its
    /// name, "b" and that number. -1 without one.
    /// </summary>
    public int SkeletonBone(int bone) => Skeleton == null || bone < 0 ? -1 : Skeleton.FindBone($"b{bone}");

    /// <summary>
    /// A frame on the held weapon: bones 0x40 to 0x4F are the weapon model's own bones, by the low nibble (BATTLE.PRG
    /// 0x800A1E4C takes the actor's weapon object for them), so an effect anchored on bone 64 sits on the blade. Null
    /// for other bones or without a weapon in hand.
    /// </summary>
    private Transform3D? WeaponFrame(int bone)
    {
        if ((bone & 0xF0) != 0x40 || Skeleton?.GetNodeOrNull<Node3D>("Weapon") is not { } mount || mount.GetChildCount() == 0) return null;
        if (mount.GetChild(0) is not Node3D model) return null;
        foreach (var found in model.FindChildren("*", "Skeleton3D", true, false))
        {
            var rig = (Skeleton3D)found;
            int index = rig.FindBone($"b{bone & 0xF}");
            if (index >= 0) return rig.GlobalTransform * rig.GetBoneGlobalPose(index);
        }
        return model.GlobalTransform;
    }

    /// <summary>A point in a model bone's own frame (rig units, turned and scaled with it), in retail map space.</summary>
    public Vector3 BonePoint(int bone, Vector3 local)
    {
        if (WeaponFrame(bone) is { } weapon) return RetailSpace.AffineInverse() * (weapon * local);
        int index = SkeletonBone(bone);
        if (index < 0) return BonePosition(bone) + local;
        return RetailSpace.AffineInverse() * (Skeleton!.GlobalTransform * (Skeleton.GetBoneGlobalPose(index) * local));
    }

    /// <summary>A model bone's frame in retail map space, its scale (the model's 1820/4096) kept as retail's bone matrices keep it, or the actor's facing when it has no such bone.</summary>
    public Basis BoneBasis(int bone)
    {
        var retail = RetailSpace.AffineInverse();
        if (WeaponFrame(bone) is { } weapon) return (retail.Basis * weapon.Basis).Orthonormalized();
        int index = SkeletonBone(bone);
        if (index < 0) return new Basis(Vector3.Up, -Facing * Mathf.Tau / 4096f);
        return retail.Basis * Skeleton!.GlobalTransform.Basis * Skeleton.GetBoneGlobalPose(index).Basis * RetailSpace.Basis;
    }

    /// <summary>A model bone's position in retail map units, or the actor's position when it has no such bone.</summary>
    public Vector3 BonePosition(int bone)
    {
        if (WeaponFrame(bone) is { } weapon) return RetailSpace.AffineInverse() * weapon.Origin;
        int index = SkeletonBone(bone);
        if (index < 0) return Position;
        var engine = Skeleton!.GlobalTransform * Skeleton.GetBoneGlobalPose(index).Origin;
        return RetailSpace.AffineInverse() * engine;
    }
}
