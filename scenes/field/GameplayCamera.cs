// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The retail field camera while Ashley is under control. It orbits a look-at point 90 units above her at a fixed
/// pitch: yaw, pitch and a 3D distance in retail map units (y down, 4096 units per turn). L1 and R1 turn it by octants,
/// accelerating and settling on each eighth of a turn, and Select switches between the near and far distance. The
/// look-at follows Ashley at a capped rate, with a vertical dead zone, and stays inside the room's camera bounds.
/// It steps on the 30 Hz clock.
/// </summary>
public sealed class GameplayCamera
{
    public const float Near = 0x600, Far = 0x900, DefaultPitch = -0x180, LookHeight = 90;

    public const float DefaultProjection = 512;

    public float Yaw = 0x800, Pitch = DefaultPitch, Distance = Near, TargetDistance = Near;
    /// <summary>The projection distance (the screen's 240 lines stand this far from the eye): 512, more for a close action view.</summary>
    public float Projection = DefaultProjection;
    public Vector3 LookAt;

    /// <summary>
    /// An action's view (BATTLE.PRG 0x80070xxx's zoom by the action record's byte 13 bits 4-5): 0 keeps the view, 1
    /// closes in (the projection rises by 64 a frame to 768 and a distance over 0x600 falls by 192 a frame to it),
    /// 2 draws back (the distance rises by 192 a frame to 0x900); the look-at steps to the point between the actor
    /// and its target by at most 40 units a frame. Null while no action holds the camera.
    /// </summary>
    private (Vector3 Look, int View)? _action;

    /// <summary>Whether an action's view has been reached: the look-at on its point, the zoom done.</summary>
    public bool Settled => _action is not { } action || LookAt == action.Look && Projection == (action.View == 1 ? 768 : DefaultProjection)
        && (action.View != 1 || Distance <= Near) && (action.View != 2 || Distance >= Far);

    /// <summary>Holds the camera on an action: <paramref name="look"/> is halfway between the actor and its target.</summary>
    public void Act(Vector3 look, int view) => _action = (look with { Y = Math.Min(look.Y, 0) }, view);

    /// <summary>Lets the camera go back to Ashley: the projection and distance step back as they came (0x800793C0).</summary>
    public void EndAction()
    {
        if (_action == null) return;
        _action = null;
        _returning = true;
    }

    private bool _returning;

    public bool Acting => _action != null;
    /// <summary>The room's camera bounds in tiles: min x, min z, max x, max z.</summary>
    public Rect2 Bounds;

    private int _turning;     // 0 idle, -1 toward lower yaw (R1), 1 toward higher yaw (L1)
    private float _turnTarget, _turnSpeed, _zoomSpeed;

    /// <summary>The eye: the look-at plus the spherical offset. Yaw 0 puts the eye on the +z side, a negative pitch above.</summary>
    public Vector3 Eye => LookAt + Distance * new Vector3(Mathf.Cos(Angle(Pitch)) * Mathf.Sin(Angle(Yaw)), Mathf.Sin(Angle(Pitch)), Mathf.Cos(Angle(Pitch)) * Mathf.Cos(Angle(Yaw)));

    private static float Angle(float units) => units * Mathf.Tau / 4096f;

    /// <summary>Where the look-at wants to be for Ashley at <paramref name="ashley"/>: above her, inside the bounds.</summary>
    public Vector3 Target(Vector3 ashley, float distance)
    {
        var target = ashley with { Y = ashley.Y - LookHeight };
        // The margin keeps the screen edge, not just the look-at, near the room: 0.15625 of the distance.
        float margin = distance * 360 / 2304;
        float Clamp(float value, float min, float max) =>
            max - min < margin * 2 ? (min + max) / 2 : Math.Clamp(value, min + margin, max - margin);
        if (Bounds.Size != Vector2.Zero)
        {
            target.X = MathF.Truncate(Clamp(target.X, Bounds.Position.X * 128, Bounds.End.X * 128));
            target.Z = MathF.Truncate(Clamp(target.Z, Bounds.Position.Y * 128, Bounds.End.Y * 128));
        }
        return target;
    }

    /// <summary>Places the camera on Ashley at once.</summary>
    public void Place(Vector3 ashley)
    {
        LookAt = Target(ashley, Distance);
        _action = null;
        _returning = false;
        Projection = DefaultProjection;
        _turning = 0;
        _turnSpeed = _zoomSpeed = 0;
    }

    /// <summary>
    /// Advances one frame. <paramref name="turn"/> is the held turn: 1 for L1, -1 for R1, 0 for neither;
    /// <paramref name="zoom"/> is a fresh Select press.
    /// </summary>
    public void Step(Vector3 ashley, int turn, bool zoom)
    {
        if (_action is { } action)
        {
            var offset = action.Look - LookAt;
            LookAt = offset.Length() <= 40 ? action.Look : LookAt + offset.Normalized() * 40;
            if (action.View == 1)
            {
                Projection = Math.Min(768, Projection + 64);
                if (Distance > Near) Distance = Math.Max(Near, Distance - 192);
            }
            else if (action.View == 2) Distance = Math.Min(Far, Distance + 192);
            return;
        }
        if (_returning)
        {
            Projection = Approach(Projection, DefaultProjection, 64);
            Distance = Approach(Distance, TargetDistance, 192);
            _returning = Projection != DefaultProjection || Distance != TargetDistance;
        }
        StepTurn(turn);
        if (zoom && !_returning) TargetDistance = TargetDistance == Near ? Far : Near;
        if (!_returning) StepZoom();
        var target = Target(ashley, Distance);
        // Each horizontal axis closes on its target by at most 18 units a frame.
        LookAt.X = Approach(LookAt.X, target.X, 18);
        LookAt.Z = Approach(LookAt.Z, target.Z, 18);
        // Height has a 96-unit dead zone and never goes below the map's zero plane.
        if (LookAt.Y - target.Y > 96) LookAt.Y = target.Y + 96;
        else if (target.Y - LookAt.Y > 96) LookAt.Y = target.Y - 96;
        LookAt.Y = Math.Min(LookAt.Y, 0);
    }

    private static float Approach(float value, float target, float step) =>
        Math.Abs(target - value) > step ? value + Math.Sign(target - value) * step : target;

    /// <summary>
    /// Octant turning: a press aims at the next eighth of a turn; speed rises by 8 a frame to 40, and within 120 units
    /// of the octant falls by 8 a frame to 8. Holding the button near the octant carries on to the next one.
    /// </summary>
    private void StepTurn(int held)
    {
        if (_turning == 0)
        {
            if (held == 0) return;
            _turning = held;
            _turnTarget = NextOctant(Yaw, held);
            _turnSpeed = 0;
        }
        float remaining = Math.Abs(_turnTarget - Yaw);
        if (remaining <= 120 && held == _turning)
        {
            _turnTarget = NextOctant(_turnTarget, held);
            remaining = Math.Abs(_turnTarget - Yaw);
        }
        _turnSpeed = remaining <= 120 ? Math.Max(8, _turnSpeed - 8) : Math.Min(40, _turnSpeed + 8);
        if (remaining <= _turnSpeed)
        {
            Yaw = Mathf.PosMod(_turnTarget, 4096);
            _turning = 0;
            _turnSpeed = 0;
            return;
        }
        Yaw += _turning * _turnSpeed;
        // Keep the yaw and its target in one turn's range together.
        if (Yaw < 0 || Yaw >= 4096)
        {
            float wrap = Yaw < 0 ? 4096 : -4096;
            Yaw += wrap;
            _turnTarget += wrap;
        }
    }

    private static float NextOctant(float yaw, int direction) =>
        direction > 0 ? Mathf.Floor(yaw / 512 + 1) * 512 : Mathf.Ceil(yaw / 512 - 1) * 512;

    /// <summary>Distance eases toward its target: speed rises by 16 a frame to 64, and within 160 units falls to 16.</summary>
    private void StepZoom()
    {
        float remaining = TargetDistance - Distance;
        if (remaining == 0) { _zoomSpeed = 0; return; }
        _zoomSpeed = Math.Abs(remaining) <= 160 ? Math.Max(16, _zoomSpeed - 16) : Math.Min(64, _zoomSpeed + 16);
        Distance = Math.Abs(remaining) <= _zoomSpeed ? TargetDistance : Distance + Math.Sign(remaining) * _zoomSpeed;
    }

    /// <summary>
    /// The retail facing the stick points in (x right, y down as Godot reports it), relative to the camera: up faces
    /// away from the eye, which is the camera's yaw, and right is a quarter turn on.
    /// </summary>
    public float Heading(Vector2 stick) => Mathf.PosMod(Yaw + Mathf.Atan2(stick.X, -stick.Y) * 4096 / Mathf.Tau, 4096);
}
