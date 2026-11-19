// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// A character on foot, on the retail 30 Hz clock in map units (y down, 4096 units per turn): Ashley under the
/// player's control, or an enemy closing in, as BATTLE.PRG moves every actor by one routine (0x800a5280) that its
/// commands drive (walk 9 and run 10 at its record's speeds, 0x80075554).
/// <list type="bullet">
/// <item>The stick or d-pad points her relative to the camera; she runs by default and walks while R2 is held. Her
/// facing turns toward the input over 12 frames.</item>
/// <item>The room's collision map is her floor. A rise in front of her is taken by its size: up to 64 units she steps
/// up over 8 frames; 65 to 192 she climbs after pushing into it for 8 frames (animation 36, or 39 above 128), over 8
/// frames a 64-unit level; 193 to 256 she grabs the ledge and pulls herself up (41 then 39, 28 frames); anything
/// higher is a wall. On a stair tile (attribute bit 5) a rise is never a wall.</item>
/// <item>A drop of 64 to 255 units she falls straight off; a deeper one only after pushing at the edge for 8 frames.</item>
/// <item>Square jumps: standing, she crouches (47) for 5 frames first; moving, she leaps at once at her walking or
/// running speed. She leaves the ground at 30 units a frame, which gravity takes 3 from every frame, so she rises 135
/// units in 9 frames. In the air the stick steers her at her ground speed (31 at most); a ledge she meets is climbed
/// at once; rising, her head meeting the ceiling turns her back down. The take-off, apex, fall and landing animations
/// are 44, 49, 45, 46 standing, 48, 49, 50, 51 walking and 53, 54, 55, 56 running.</item>
/// </list>
/// </summary>
public sealed class FieldPlayer
{
    public const float Radius = 53, StepHeight = 96, FallHeight = 64;
    /// <summary>Her base animations: standing, walking (two foot phases), running, and stopping from a walk.</summary>
    public const int Idle = 1, Walk = 13, Run = 19, Stop = 6, Crouch = 47;
    private static readonly int[][] JumpAnimations = { new[] { 44, 49, 45, 46 }, new[] { 48, 49, 50, 51 }, new[] { 53, 54, 55, 56 } };

    private enum State { Ground, Crouch, Air, Climb }

    private readonly EventActor _actor;
    private readonly Func<Vector3, float?> _floor;
    private readonly Func<Vector3, bool> _stairs;
    private float _heading = -1;
    private int _animation = -1;
    private State _state = State.Ground;
    private int _frames, _jumpType, _push, _edge;
    private Vector3 _velocity, _climbFrom, _climbTo;
    private int _climbFrames, _climbTotal, _airAnimation;

    /// <param name="floor">The floor height under a map position, or null where nothing can stand.</param>
    /// <param name="stairs">Whether a position is on a stair tile, where rises are never walls.</param>
    public FieldPlayer(EventActor actor, Func<Vector3, float?> floor, Func<Vector3, bool>? stairs = null)
    {
        _actor = actor;
        _floor = floor;
        _stairs = stairs ?? (_ => false);
    }

    /// <summary>Whether a step of an actor to a position would push into another character's body; null for none.</summary>
    public Func<EventActor, Vector3, bool>? Crowded;

    /// <summary>Her pace: 1, or less with crippled legs or slowing, more quickened.</summary>
    public float Pace = 1;

    /// <summary>Units a frame walking and running: Ashley's, or a character record's (bytes 0x25 and 0x27).</summary>
    public float WalkSpeed = 9, RunSpeed = 18;

    /// <summary>
    /// Whether rises of more than a step are climbed (BATTLE.PRG 0x800a76bc climbs levels 2 to 4 only for an actor with
    /// +8 bit 23 or +0x5ac bit 3); to others they are walls.
    /// </summary>
    public bool Climbs = true;

    /// <summary>The ceiling over a map position (y down; <see cref="FieldView.CeilingAt"/>), or none.</summary>
    public Func<int, int, int>? Ceiling;

    /// <summary>Whether it plays its own walking, running, climbing and falling animations; a fighter's are the battle's.</summary>
    public bool Animates = true;

    /// <summary>Whether walking turns it to its heading; an enemy's body turns by its own turn (BATTLE.PRG 0x800a6660).</summary>
    public bool Turns = true;

    /// <summary>Whether she is moving under the player's input this frame.</summary>
    public bool Moving { get; private set; }

    /// <summary>Whether she is off the ground or climbing, and so not free for other actions.</summary>
    public bool Busy => _state != State.Ground;

    /// <summary>The animation set she moves by: her common set, or her weapon's battle set while it is drawn.</summary>
    public string Set
    {
        get => _set;
        set { if (_set != value) { _set = value; _animation = -1; } }
    }
    private string _set = "base";

    /// <summary>Stops her where she stands, for actions that take over her animation.</summary>
    public void Halt()
    {
        Moving = false;
        // A turn under way stops where it is.
        _actor.TurnOver(_actor.Facing, 0);
        _heading = -1;
        _animation = -1;
        _push = _edge = 0;
    }

    /// <summary>
    /// Advances one frame. <paramref name="heading"/> is the facing the input points to, or null with no input;
    /// <paramref name="walk"/> holds her to walking pace; <paramref name="jump"/> is a fresh Square press;
    /// <paramref name="facing"/> is a facing to turn to other than the heading (an enemy facing its target as it goes).
    /// </summary>
    public void Step(float? heading, bool walk, bool jump = false, float? facing = null)
    {
        Moving = heading != null;
        float speed = (walk ? WalkSpeed : RunSpeed) * Pace;
        Vector3 Motion(float direction, float pace)
        {
            float angle = direction * Mathf.Tau / 4096f;
            return new Vector3(-Mathf.Sin(angle), 0, -Mathf.Cos(angle)) * pace;
        }
        switch (_state)
        {
            case State.Climb:
                StepClimb();
                return;
            case State.Crouch:
                if (--_frames <= 0) Launch(0, Vector3.Zero);
                return;
            case State.Air:
                if (heading is { } steer) { Face(steer); _velocity = Motion(steer, Math.Min(31, _jumpType == 1 ? WalkSpeed * Pace : speed)); }
                else _velocity = Vector3.Zero;
                StepAir();
                return;
        }
        if (heading is { } target && Turns) Face(facing ?? target);
        if (jump)
        {
            if (heading is { } leap) { Launch(walk ? 1 : 2, Motion(leap, speed)); return; }
            _state = State.Crouch;
            _frames = 5;
            if (Animates) _actor.Animate($"{_set}_{Crouch}", 4, once: true);
            _animation = -1;
            return;
        }
        if (heading is { } direction) Move(Motion(direction, speed), direction);
        else _push = _edge = 0;
        if (_state != State.Ground) return;
        Settle();
        Animate(walk);
    }

    private void Launch(int type, Vector3 velocity)
    {
        _state = State.Air;
        _jumpType = type;
        _velocity = velocity;
        _frames = 0;
        _airVertical = -30;
        _airFloor = _actor.Position.Y;
        _airAnimation = 0;
        if (Animates) _actor.Animate($"{_set}_{JumpAnimations[type][0]}", 4, once: true);
        _animation = -1;
    }

    private float _airVertical;

    /// <summary>The floor last found under her in the air: where she left the ground, until she passes over another.</summary>
    private float _airFloor;

    /// <summary>
    /// A frame in the air: gravity, steering, a ledge met in flight (climbed at once if no more than 255 above), and
    /// landing when she reaches the floor below her. Retail's air floor (BATTLE.PRG 0x800a6ee8) is the collision map's
    /// height whatever the tile's blocking class, so she always comes down: where there is no floor to stand on (a
    /// blocked tile, or off the map) she goes no further across, as on the ground, and falls to the floor she was last
    /// over rather than stopping where she is.
    /// </summary>
    private void StepAir()
    {
        _airVertical += 3;
        var position = _actor.Position;
        // Rising, her head (192 above her feet) meeting the ceiling turns her back down (0x800ac8f8); she keeps going
        // across.
        if (_airVertical < 0 && Ceiling != null && position.Y + _airVertical - 192 < Ceiling((int)position.X, (int)position.Z))
            _airVertical = -_airVertical;
        var next = position + _velocity;
        if (_velocity != Vector3.Zero)
        {
            float? ahead = _floor(next + _velocity.Normalized() * Radius);
            if (ahead is not { } floorAhead || position.Y - floorAhead > 32 && !_stairs(next))
            {
                if (ahead is { } ledge && position.Y - ledge is > 32 and <= 255 && Climb(next, ledge, _velocity.Normalized())) return;
                next = position;
            }
        }
        next.Y = position.Y + _airVertical;
        if (_floor(next) == null) next = position with { Y = next.Y };
        float floor = _airFloor = _floor(next) ?? _airFloor;
        int phase = Math.Abs(_airVertical) <= 9 ? 1 : _airVertical > 9 ? 2 : 0;
        if (phase != _airAnimation && phase > 0) { _airAnimation = phase; if (Animates) _actor.Animate($"{_set}_{JumpAnimations[_jumpType][phase]}", phase == 1 ? 16 : 8, once: true); }
        if (_airVertical > 0 && next.Y >= floor)
        {
            _actor.Position = next with { Y = floor };
            _state = State.Ground;
            if (Animates) _actor.Animate($"{_set}_{JumpAnimations[_jumpType][3]}", 8, once: true);
            if (Animates) _actor.Queue($"{_set}_{Idle}", 8);
            _animation = Idle;
            return;
        }
        _actor.Position = next;
    }

    /// <summary>
    /// Starts climbing onto a ledge at <paramref name="ledge"/> ahead of <paramref name="at"/>: over 8 frames a 64-unit
    /// level for a climb (36, or 39 for the third level), or a grab and pull-up for the fourth (41, then 39). She
    /// comes up a little past her radius, but only onto the ledge itself: retail places the climb by the ledge's tile
    /// edge (0x800a9530) and gives up a grab with no room above it (0x800a6798), so she is set down on the point of the
    /// ledge nearest that, and where none lies ahead of her within reach (only a blocked tile or the map's edge) she
    /// does not climb.
    /// </summary>
    private bool Climb(Vector3 at, float ledge, Vector3 direction)
    {
        float rise = _actor.Position.Y - ledge;
        if (rise > 256 || !Climbs) return false;
        // The point of the ledge nearest a little past her radius, no further than her reach over it.
        float reach = 0;
        for (float d = 0; d <= Radius + 4 && reach == 0; d += 4)
            foreach (float r in new[] { Radius + 8 - d, Radius + 8 + d })
                if (r > 0 && r <= Radius + 40 && _floor((at + direction * r) with { Y = ledge }) == ledge) { reach = r; break; }
        if (reach == 0) return false;
        int level = (int)Math.Ceiling(rise / 64);
        _climbFrom = _actor.Position;
        _climbTo = (at + direction * reach) with { Y = ledge };
        _climbTotal = _climbFrames = level >= 4 ? 28 : level * 8;
        _state = State.Climb;
        if (Animates) _actor.Animate($"{_set}_{(level >= 4 ? 41 : level == 3 ? 39 : 36)}", 4, once: true);
        if (level >= 4 && Animates) _actor.Queue($"{_set}_39", 4);
        _animation = -1;
        return true;
    }

    private void StepClimb()
    {
        _climbFrames--;
        float t = 1 - _climbFrames / (float)_climbTotal;
        // She rises first, then moves onto the ledge.
        var position = _climbFrom.Lerp(_climbTo, Math.Clamp(t * 2 - 1, 0, 1));
        position.Y = Mathf.Lerp(_climbFrom.Y, _climbTo.Y, Math.Min(1, t * 2));
        _actor.Position = position;
        if (_climbFrames > 0) return;
        _actor.Position = _climbTo;
        _state = State.Ground;
        _push = 0;
        if (Animates) _actor.Animate($"{_set}_{Idle}", 8);
        _animation = Idle;
    }

    /// <summary>Starts a turn to a new facing: the shortest way round, evenly over 12 frames (the engine's turn).</summary>
    private void Face(float heading)
    {
        if (heading == _heading) return;
        _heading = heading;
        _actor.TurnOver(heading, 12);
    }

    /// <summary>
    /// Slides along walls: the floor is sampled at eight points on her radius round where she is going, and motion
    /// toward any sample that is a wall is dropped on that axis. Pushing into a rise she can climb, for 8 frames,
    /// climbs it; pushing at the edge of a deep drop, for 8 frames, steps off it.
    /// </summary>
    private void Move(Vector3 motion, float heading)
    {
        var position = _actor.Position;
        // The ground solver probes the proposed position (0x800a7818), as the air solver below does. Probing only
        // the old position can leave the body blocked by a ledge before the climbing probe ever reaches it.
        var front = position + motion + motion.Normalized() * (Radius + 4);
        if (_floor(front) is { } ahead)
        {
            float rise = position.Y - ahead;
            if (rise is > StepHeight and <= 256 && !_stairs(front))
            {
                if (++_push >= 8 && Climb(position, ahead, motion.Normalized())) return;
            }
            else _push = 0;
            float drop = ahead - position.Y;
            if (drop >= 256 && ++_edge < 8) return;
            if (drop < 256) _edge = 0;
        }
        for (int pass = 0; pass < 2 && motion != Vector3.Zero; pass++)
        {
            var next = position + motion;
            bool blockedX = false, blockedZ = false;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.Pi / 4;
                var offset = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * Radius;
                if (!Wall(next + offset, next)) continue;
                if (Math.Abs(offset.X) > 1 && Math.Sign(offset.X) == Math.Sign(motion.X)) blockedX = true;
                if (Math.Abs(offset.Z) > 1 && Math.Sign(offset.Z) == Math.Sign(motion.Z)) blockedZ = true;
            }
            // Another character's body stops her as a wall does, along the axis that would close on it.
            if (Crowded != null && Crowded(_actor, next))
            {
                blockedX |= motion.X != 0 && Crowded(_actor, position + new Vector3(motion.X, 0, 0));
                blockedZ |= motion.Z != 0 && Crowded(_actor, position + new Vector3(0, 0, motion.Z));
                if (!blockedX && !blockedZ) blockedX = blockedZ = true;
            }
            if (!blockedX && !blockedZ) { _actor.Position = next with { Y = position.Y }; return; }
            if (blockedX) motion.X = 0;
            if (blockedZ) motion.Z = 0;
        }
    }

    /// <summary>A sample is a wall where nothing stands, or where it rises more than a step, unless both it and her own spot are stairs.</summary>
    private bool Wall(Vector3 point, Vector3 centre) =>
        _floor(point) is not { } floor || _actor.Position.Y - floor > StepHeight && !(_stairs(point) && _stairs(centre));

    /// <summary>
    /// Puts her on the floor: small rises she steps up over a few frames, small drops she steps down, and from 64
    /// down she falls (her crouch, 47, as she drops). A floor more than a step above her is overhead, not under her:
    /// retail's floor (BATTLE.PRG 0x800a6ee8, through 0x800a9378) takes a platform up to 256 above the feet, but only as
    /// a ledge she climbs or grabs (0x800a5280), so she is never lifted onto it where she stands.
    /// </summary>
    private void Settle()
    {
        if (_floor(_actor.Position) is not { } floor) return;
        float drop = floor - _actor.Position.Y;
        if (-drop > StepHeight) return;
        if (drop >= FallHeight && !_stairs(_actor.Position))
        {
            _state = State.Air;
            _jumpType = Moving ? 2 : 0;
            _airVertical = 0;
            _airFloor = floor;
            _airAnimation = 2;
            if (Animates) _actor.Animate($"{_set}_{Crouch}", 4, once: true);
            _animation = -1;
            return;
        }
        // A step up eases over about 8 frames (8 units a frame at least); a step down is taken at once.
        if (drop < 0)
        {
            float rise = -drop;
            _actor.Position = _actor.Position with { Y = _actor.Position.Y - Math.Min(rise, Math.Max(8, rise / 4)) };
        }
        else _actor.Position = _actor.Position with { Y = floor };
    }

    private void Animate(bool walk)
    {
        int next = Moving ? walk ? Walk : Run : _animation is Walk ? Stop : _animation is Stop or Idle ? _animation : Idle;
        if (next == _animation) return;
        if (Animates) _actor.Animate($"{_set}_{next}", next == Idle ? 10 : 4);
        if (next == Stop && Animates) _actor.Queue($"{_set}_{Idle}", 10);
        _animation = next;
    }
}
