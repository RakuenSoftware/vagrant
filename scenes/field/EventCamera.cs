// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The retail event camera: a look-at channel and an eye channel, each with a staged destination and an eased move,
/// plus roll, projection distance, clip planes and shake. It works in retail map units (y down) on the 30 Hz event
/// clock; <see cref="FieldView"/> converts the result for the shared field camera.
/// </summary>
public sealed class EventCamera
{
    public const int LookAt = 0, Eye = 1;
    /// <summary>The retail default projection distance (GTE H): about a 35 degree horizontal view on the 320-pixel screen.</summary>
    public const float DefaultProjection = 512f;

    private sealed class Channel
    {
        public Vector3 Current, Staged, Start, Other;
        public string Mode = "idle";
        public int Tick, Frames, Ease, YawEase, RadiusEase, HeightEase, Direction;
        public float Yaw, Height, RadiusScale;
        public Func<Vector3>? Track;
        /// <summary>Momentum (df): whether it is on, whether a finished move has left it moving, and the drift.</summary>
        public bool Follow, Following;
        public Vector3 FollowPosition, FollowDelta;
    }

    /// <summary>
    /// Retail df (0x800BE36C): momentum for the look-at and the eye, each cleared of any motion it carried. With it on,
    /// a move that ends keeps its last frame's motion (0x800BCA8C), not at rest but into the next move: that move
    /// starts from a point still drifting by it and eases onto its own path by curve 1 over its frames.
    /// </summary>
    public void Momentum(bool lookAt, bool eye)
    {
        (_channels[LookAt].Follow, _channels[LookAt].Following) = (lookAt, false);
        (_channels[Eye].Follow, _channels[Eye].Following) = (eye, false);
    }

    private readonly Channel[] _channels = { new(), new() };
    private float _roll, _rollStart, _rollEnd;
    private int _rollTick, _rollFrames, _rollEase;
    private float _projection = DefaultProjection, _projectionStart, _projectionEnd;
    private int _projectionTick, _projectionFrames, _projectionEase;
    private int _shakeTick, _shakeFrames, _shakeCycles, _shakeAxes, _shakeEnvelope;
    private float _shakeAmplitude;

    public Vector3 EyePosition => _channels[Eye].Current + Shake(Eye) + AnchorOffset;
    public Vector3 LookAtPosition => _channels[LookAt].Current + Shake(LookAt) + AnchorOffset;

    private Func<Vector3>? _anchor;
    private Vector3 AnchorOffset => _anchor?.Invoke() ?? Vector3.Zero;

    /// <summary>
    /// Retail e8: an actor whose position carries both camera points. Releasing it folds its last offset into the
    /// points, so the camera stays put.
    /// </summary>
    public void Anchor(Func<Vector3>? anchor)
    {
        if (anchor == null && _anchor != null)
        {
            var offset = _anchor();
            foreach (var c in _channels) { c.Current += offset; c.Staged += offset; }
        }
        _anchor = anchor;
    }

    /// <summary>Retail de (0x800BE3A0): stops both points where they are and takes their momentum off.</summary>
    public void Stop()
    {
        foreach (var c in _channels) { c.Mode = "idle"; c.Track = null; c.Follow = c.Following = false; }
    }
    public float Roll => _roll;
    public float Projection => _projection;
    /// <summary>The near clip and the far-texture depth (ec, 128 a step; not a clip), in map units.</summary>
    public float Near = 64, Far = 4096;
    /// <summary>Whether an event has placed the camera; until then the field view's own camera stays in charge.</summary>
    public bool Active { get; private set; }

    /// <summary>Idle mask in the bit order the retail wait uses: 1 look-at, 2 eye, 4 roll, 8 projection, 0x80 shake.</summary>
    public int IdleMask =>
        (_channels[LookAt].Mode == "idle" ? 1 : 0) | (_channels[Eye].Mode == "idle" ? 2 : 0) | (_rollTick >= _rollFrames ? 4 : 0)
        | (_projectionTick >= _projectionFrames ? 8 : 0) | 0x10 | 0x20 | 0x40 | (_shakeTick >= _shakeFrames ? 0x80 : 0);

    /// <summary>The four user curves (4 to 7) an event defines with 5f: a base shape and a power.</summary>
    private static readonly (int Shape, int Power)[] UserCurves = new (int, int)[4];

    /// <summary>Retail 5f: defines user curve 4 + <paramref name="slot"/>.</summary>
    public static void DefineCurve(int slot, int shape, int power) => UserCurves[slot & 3] = (shape & 3, power);

    /// <summary>Clears the user curves, as a new event does.</summary>
    public static void ResetCurves() => Array.Clear(UserCurves);

    /// <summary>
    /// The retail easing curves, in fractions: linear, ease-out, ease-in-out, ease-in. A user curve (4 to 7) first
    /// squares the fraction as many times as its power, or for a negative power squares its complement and mirrors
    /// it, then applies its base shape.
    /// </summary>
    public static float Ease(int curve, float t)
    {
        int shape = curve & 3;
        if (curve >= 4)
        {
            var (userShape, power) = UserCurves[(curve - 4) & 3];
            shape = userShape;
            for (int i = 0; i < Math.Abs(power); i++) t = power > 0 ? t * t : 1 - (1 - t) * (1 - t);
        }
        return shape switch
        {
            1 => Mathf.Sin(t * Mathf.Pi / 2),
            2 => (1 - Mathf.Cos(t * Mathf.Pi)) / 2,
            3 => 1 - Mathf.Cos(t * Mathf.Pi / 2),
            _ => t,
        };
    }

    public void Stage(int channel, Vector3 position) => _channels[channel].Staged = position;

    public void Snap(int channel)
    {
        var c = _channels[channel];
        c.Current = c.Staged;
        c.Mode = "idle";
        c.Track = null;
        Active = true;
    }

    /// <summary>Starts a move of one channel. <paramref name="args"/> carries the translated retail parameters.</summary>
    public void Move(int channel, GDictionary args, Func<Vector3>? track = null)
    {
        var c = _channels[channel];
        c.Mode = args["mode"].AsString();
        c.Start = c.Current;
        c.Other = _channels[1 - channel].Current;
        c.Tick = 0;
        c.Frames = Math.Max(1, args["frames"].AsInt32());
        c.Ease = args.GetValueOrDefault("ease", 0).AsInt32();
        c.YawEase = args.GetValueOrDefault("yaw_ease", 0).AsInt32();
        c.RadiusEase = args.GetValueOrDefault("radius_ease", 0).AsInt32();
        c.HeightEase = args.GetValueOrDefault("height_ease", 0).AsInt32();
        c.Direction = args.GetValueOrDefault("direction", 0).AsInt32();
        c.Yaw = args.GetValueOrDefault("yaw", 0).AsSingle();
        c.Height = args.GetValueOrDefault("height", 0).AsSingle();
        c.RadiusScale = args.GetValueOrDefault("radius_scale", 64).AsSingle() / 64f;
        c.Track = track;
        Active = true;
    }

    public void SetRoll(int angle, int direction, int ease, int frames)
    {
        float target = angle;
        // Direction 0 forces the negative way round, 2 the positive way; anything else takes the shortest.
        float delta = Mathf.PosMod(target - _roll, 4096);
        if (direction == 0) delta -= 4096;
        else if (direction != 2 && delta > 2048) delta -= 4096;
        _rollStart = _roll;
        _rollEnd = _roll + delta;
        _rollEase = ease;
        _rollTick = 0;
        _rollFrames = frames;
        if (frames == 0) _roll = Mathf.PosMod(_rollEnd, 4096);
    }

    public void SetLens(string property, int value, int ease, int frames)
    {
        switch (property)
        {
            case "projection":
                _projectionStart = _projection;
                _projectionEnd = value == 0 ? DefaultProjection : value * 4;
                _projectionEase = ease;
                _projectionTick = 0;
                _projectionFrames = frames;
                if (frames == 0) _projection = _projectionEnd;
                break;
            case "near": Near = value == 0 ? 64 : value; break;
            case "far": Far = value == 0 ? 4096 : value * 128; break;
        }
    }

    public void SetShake(int cycles, int amplitude, int axes, int envelope, int frames)
    {
        _shakeCycles = cycles;
        _shakeAmplitude = amplitude;
        _shakeAxes = axes;
        _shakeEnvelope = envelope;
        _shakeTick = 0;
        _shakeFrames = amplitude == 0 ? 0 : frames;
    }

    private Vector3 Shake(int channel)
    {
        if (_shakeTick >= _shakeFrames || (_shakeAxes >> channel & 1) == 0) return Vector3.Zero;
        float t = _shakeTick / (float)_shakeFrames;
        float envelope = _shakeEnvelope switch { 1 => Mathf.Cos(t * Mathf.Pi / 2), 2 => Mathf.Sin(t * Mathf.Pi), 3 => Mathf.Sin(t * Mathf.Pi / 2), _ => 1 };
        float wave = Mathf.Sin(_shakeCycles * Mathf.Tau * t) * _shakeAmplitude * envelope;
        int Factor(int shift) => ((_shakeAxes >> shift) & 3) - 1;
        return new Vector3(Factor(2), Factor(4), Factor(6)) * wave;
    }

    /// <summary>Advances every channel by one event frame.</summary>
    public void Step()
    {
        for (int i = 0; i < 2; i++) StepChannel(_channels[i]);
        if (_rollTick < _rollFrames)
        {
            _rollTick++;
            _roll = Mathf.PosMod(Mathf.Lerp(_rollStart, _rollEnd, Ease(_rollEase, _rollTick / (float)_rollFrames)), 4096);
        }
        if (_projectionTick < _projectionFrames)
        {
            _projectionTick++;
            _projection = Mathf.Lerp(_projectionStart, _projectionEnd, Ease(_projectionEase, _projectionTick / (float)_projectionFrames));
        }
        if (_shakeTick < _shakeFrames) _shakeTick++;
    }

    private void StepChannel(Channel c)
    {
        if (c.Mode == "idle") return;
        var previous = c.Current;
        c.Tick = Math.Min(c.Tick + 1, c.Frames);
        float t = c.Tick / (float)c.Frames;
        switch (c.Mode)
        {
            case "line":
                c.Current = c.Start.Lerp(c.Staged, Ease(c.Ease, t));
                break;
            case "radius":
            {
                // The point travels in a straight line, but its distance from the other point eases from start to end.
                var direction = (c.Start.Lerp(c.Staged, t) - c.Other).Normalized();
                float radius = Mathf.Lerp((c.Start - c.Other).Length(), (c.Staged - c.Other).Length(), Ease(c.Ease, t));
                c.Current = c.Other + direction * radius;
                break;
            }
            case "orbit": case "turn":
            {
                var from = c.Start - c.Other;
                float startYaw = Mathf.Atan2(from.X, from.Z), startRadius = new Vector2(from.X, from.Z).Length();
                float endYaw, endRadius, endHeight;
                if (c.Mode == "orbit")
                {
                    var to = c.Staged - c.Other;
                    endYaw = Mathf.Atan2(to.X, to.Z);
                    endRadius = new Vector2(to.X, to.Z).Length();
                    endHeight = to.Y;
                    float delta = Mathf.PosMod(endYaw - startYaw, Mathf.Tau);
                    if (c.Direction == 0xf) delta -= Mathf.Tau;
                    else if (c.Direction != 1 && delta > Mathf.Pi) delta -= Mathf.Tau;
                    if (Mathf.IsZeroApprox(delta) && c.Direction is 1 or 0xf) delta = c.Direction == 1 ? Mathf.Tau : -Mathf.Tau;
                    endYaw = startYaw + delta;
                }
                else
                {
                    endYaw = startYaw + c.Yaw * Mathf.Tau / 4096f;
                    endRadius = startRadius * c.RadiusScale;
                    endHeight = c.Height - c.Other.Y;
                }
                float yaw = Mathf.Lerp(startYaw, endYaw, Ease(c.YawEase, t));
                float radius = Mathf.Lerp(startRadius, endRadius, Ease(c.RadiusEase, t));
                float height = Mathf.Lerp(from.Y, endHeight, Ease(c.HeightEase, t));
                c.Current = c.Other + new Vector3(Mathf.Sin(yaw) * radius, height, Mathf.Cos(yaw) * radius);
                break;
            }
            case "track":
                if (c.Track != null) c.Current = c.Track();
                break;
            case "follow":
            {
                // Keeps the offset from the other point that this point had when the move began.
                int other = ReferenceEquals(c, _channels[LookAt]) ? Eye : LookAt;
                c.Current = _channels[other].Current + (c.Start - c.Other);
                break;
            }
        }
        if (c.Follow && c.Following)
        {
            // The drift carries on, and the path is eased in from it.
            var computed = c.Current;
            c.FollowPosition += c.FollowDelta;
            c.Current = c.FollowPosition + (computed - c.FollowPosition) * Ease(1, t);
        }
        if (c.Tick >= c.Frames)
        {
            c.Mode = "idle";
            c.Track = null;
            if (c.Follow) { c.Following = true; c.FollowPosition = c.Current; c.FollowDelta = c.Current - previous; }
        }
    }
}
