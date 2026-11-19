// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>
/// Ashley's jump arc against retail's (BATTLE.PRG 0x800ac690): she leaves the ground at 30 units a frame and gravity
/// takes 3 from that before each frame's move, on the 30 Hz clock, so she rises 135 in 9 frames and is 19 frames in
/// the air; and she always comes back down, even where her jump crosses a spot with no floor to stand on. Rising, her
/// head (192 above her feet) meeting the ceiling turns her back down; a platform overhead is a ledge, never lifting her
/// where she stands; and a climb sets her down only on the ledge she climbs. Rendering under an isolated display,
/// <c>--capture=DIR</c> saves each display frame of the field loop's jump; <c>--interpolation=off</c> draws them without
/// the engine's frame interpolation, to compare with it on (the default).
/// </summary>
public partial class JumpContracts : Node
{
    private FieldView _view = null!;
    private int _frames, _pressedAt = -1;
    private float _ground;
    private string _capture = "";
    private bool? _interpolation;
    private readonly List<float> _display = new();

    public override void _Ready()
    {
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        // Struggle for the Soul: a flat gallery, a 64-unit step down and blocked tiles at its edges.
        (_view.StartZone, _view.StartMap) = (22, 8);
        _view.KeepScene = true;
        foreach (string argument in OS.GetCmdlineUserArgs())
            if (argument.StartsWith("--capture=", StringComparison.Ordinal)) _capture = argument["--capture=".Length..];
            else if (argument.StartsWith("--interpolation=", StringComparison.Ordinal)) _interpolation = argument["--interpolation=".Length..] != "off";
        AddChild(_view);
    }

    /// <summary>Retail's heights above the take-off floor, a frame at a time, until she lands on it.</summary>
    private static List<float> RetailArc()
    {
        var heights = new List<float>();
        int velocity = -30, height = 0;
        do { velocity += 3; height = Math.Min(0, height + velocity); heights.Add(-height); } while (height < 0);
        return heights;
    }

    /// <summary>Retail's heights under a ceiling <paramref name="headroom"/> above the floor: the speed flips when her head would pass it.</summary>
    private static List<float> RetailArc(int headroom)
    {
        var heights = new List<float>();
        int velocity = -30, height = 0;
        do
        {
            velocity += 3;
            if (velocity < 0 && height + velocity - 192 < -headroom) velocity = -velocity;
            height = Math.Min(0, height + velocity);
            heights.Add(-height);
        } while (height < 0);
        return heights;
    }

    /// <summary>Her heights over the take-off floor, a frame at a time, until she is back on the ground.</summary>
    private List<float> Jump(EventActor actor, Vector3 at, float? heading, Func<Vector3, float?>? floor = null, Func<int, int, int>? ceiling = null)
    {
        actor.Position = at;
        var body = new FieldPlayer(actor, floor ?? _view.FloorAt, _view.Stairs) { Ceiling = ceiling ?? _view.CeilingAt };
        var heights = new List<float>();
        body.Step(heading, false, true);
        for (int i = 0; i < 120 && body.Busy; i++)
        {
            body.Step(heading, false);
            heights.Add(at.Y - actor.Position.Y);
        }
        if (body.Busy) throw new InvalidOperationException($"the jump from {at} never landed: {actor.Position}");
        return heights;
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3600) throw new InvalidOperationException("the room never handed over control");
            if (!_view.Controlled || _view.Player is not { } actor) return;
            // Through the field's own loop first: Square, then her height at every display frame until she lands.
            if (_pressedAt < 0)
            {
                if (_interpolation is { } interpolate) GetNode<Wol.Engine.SceneManager>("/root/SceneManager").PresentationInterpolation = interpolate;
                _ground = actor.Position.Y;
                if (_view.FloorAt(actor.Position) != _ground) throw new InvalidOperationException($"the arrival is not on the floor: {actor.Position}");
                Input.ActionPress("ps_square");
                _pressedAt = _frames;
                return;
            }
            if (_frames == _pressedAt + 2) Input.ActionRelease("ps_square");
            float lift = _ground - actor.Position.Y;
            if (lift > 0 || _display.Count > 0) _display.Add(lift);
            FieldContracts.Capture(this, _capture, $"jump_{_frames - _pressedAt:000}_{lift:000}");
            if (_frames - _pressedAt > 120) throw new InvalidOperationException("the jump through the field loop never landed");
            if (_display.Count == 0 || lift > 0) return;
            _view.ProcessMode = ProcessModeEnum.Disabled;

            var arc = RetailArc();
            // Each retail frame shows for two display frames at 60 Hz.
            var shown = _display.Where((_, i) => i % 2 == 0).ToList();
            if (_display.Count != arc.Count * 2 - 1 || !shown.SequenceEqual(arc) || !_display.Where((_, i) => i % 2 == 1).SequenceEqual(arc.Take(arc.Count - 1)))
                throw new InvalidOperationException($"the field loop's jump is not retail's 30 Hz arc: {string.Join(' ', _display)}");

            // A standing jump crouches 5 frames, then follows the arc.
            var standing = Jump(actor, new Vector3(640, -640, 1280), null);
            if (!standing.Take(5).All(h => h == 0) || !standing.Skip(5).SequenceEqual(arc))
                throw new InvalidOperationException($"the standing jump is not retail's: {string.Join(' ', standing)}");
            // A running jump leaps at once and carries her 18 units a frame.
            var running = Jump(actor, new Vector3(1000, -640, 1280), 1024);
            if (!running.SequenceEqual(arc) || Math.Abs(actor.Position.X - (1000 - 18 * arc.Count)) > 0.5f)
                throw new InvalidOperationException($"the running jump is not retail's: {string.Join(' ', running)} to {actor.Position}");
            // Gravity takes her off a 64-unit step from rest: 3, 6, 9, ... a frame, onto the floor below.
            actor.Position = new Vector3(448, -640, 300);
            var walker = new FieldPlayer(actor, _view.FloorAt, _view.Stairs);
            var fall = new List<float>();
            for (int i = 0; i < 30 && (fall.Count == 0 || walker.Busy); i++)
            {
                walker.Step(2048, false);
                if (walker.Busy || fall.Count > 0) fall.Add(actor.Position.Y + 640);
            }
            if (!fall.SequenceEqual(new float[] { 0, 3, 9, 18, 30, 45, 63, 64 }))
                throw new InvalidOperationException($"the fall off the step is not retail's: {string.Join(' ', fall)}");

            // Cutting across the corner of a tile with no floor, she stops at its edge and still comes down.
            var corner = Jump(actor, new Vector3(624, -640, 144), 3840);
            if (actor.Position.Y != -640 || _view.FloorAt(actor.Position) != -640 || !corner.SequenceEqual(arc))
                throw new InvalidOperationException($"the jump past a blocked corner stopped in the air: {actor.Position}, {string.Join(' ', corner)}");
            // Taking off over a blocked tile, she lands where she left the ground.
            var blocked = new Vector3(100, -640, 410);
            if (_view.FloorAt(blocked) != null) throw new InvalidOperationException("the fixture spot has a floor");
            var stranded = Jump(actor, blocked, null);
            if (actor.Position.Y != -640 || !stranded.Skip(5).SequenceEqual(arc))
                throw new InvalidOperationException($"the jump off a blocked tile stopped in the air: {actor.Position}, {string.Join(' ', stranded)}");

            // A ceiling 256 over the floor turns her back down when her head reaches it; the room's own is far above.
            if (_view.CeilingAt(640, 1280) != -2032) throw new InvalidOperationException($"the gallery's ceiling is not retail's none: {_view.CeilingAt(640, 1280)}");
            var low = Jump(actor, new Vector3(0, 0, 0), null, _ => 0, (_, _) => -256);
            if (!low.Skip(5).SequenceEqual(RetailArc(256)) || low.Max() > 64)
                throw new InvalidOperationException($"the jump under a low ceiling is not retail's: {string.Join(' ', low)}");

            // A platform 200 over her is a ledge: standing under it she stays on her floor; one a step up she steps onto.
            foreach (var (top, expected) in new[] { (-200f, 0f), (-64f, -64f) })
            {
                actor.Position = Vector3.Zero;
                var under = new FieldPlayer(actor, p => Math.Abs(p.X) < 64 ? top : 0, _view.Stairs);
                for (int i = 0; i < 20; i++) under.Step(null, false);
                if (actor.Position.Y != expected) throw new InvalidOperationException($"under a floor at {top} she stands at {actor.Position.Y}, not {expected}");
            }

            // A climb onto a 12-unit lip, with nothing to stand on beyond it, sets her down on the lip.
            actor.Position = Vector3.Zero;
            var climber = new FieldPlayer(actor, p => p.X < 100 ? 0 : p.X <= 112 ? -128 : null, _view.Stairs);
            bool climbed = false;
            for (int i = 0; i < 60; i++)
            {
                climber.Step(3072, false);
                climbed |= actor.Position.Y < 0;
                if (!climber.Busy && actor.Position.Y != (actor.Position.X < 100 ? 0 : actor.Position.X <= 112 ? -128 : float.NaN))
                    throw new InvalidOperationException($"the climb left her standing over no ledge: {actor.Position}");
            }
            if (!climbed || actor.Position.Y != -128) throw new InvalidOperationException($"she never climbed onto the lip: {actor.Position}");
            // The gallery's running jump into its eastern climb ends on the floor it climbs.
            Jump(actor, new Vector3(1200, -640, 400), 2304);
            if (_view.FloorAt(actor.Position) != actor.Position.Y)
                throw new InvalidOperationException($"the climb out of the running jump left her over no floor: {actor.Position}");

            GD.Print($"JUMP_CONTRACTS_OK: {arc.Count} frames aloft, apex {arc.Max()}, at 30 Hz through the field loop, standing, running, falling, over no floor, under a ceiling, under a platform and climbing");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("JUMP_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
