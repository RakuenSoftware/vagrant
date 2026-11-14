// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Revelation Shaft's Cloudstone is a grabbable ledge within 256 units above the feet.</summary>
public partial class PlatformContracts : Node
{
    private FieldView _view = null!;
    private int _frames;

    public override void _Ready()
    {
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 51;
        _view.StartMap = 28;
        _view.KeepScene = true;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3600) throw new InvalidOperationException("the room never handed over control");
            if (!_view.Controlled || _view.Player is not { } actor) return;
            _view.ProcessMode = ProcessModeEnum.Disabled;
            if (_view.PlatformState(0) is not { Off: false } platform) throw new InvalidOperationException("the native Cloudstone is unavailable");
            float top = platform.Now.Y;
            if (_view.FloorAt(platform.Now with { Y = top + 256 }) != top)
                throw new InvalidOperationException("the platform was excluded at the native 256-unit limit");
            if (_view.FloorAt(platform.Now with { Y = top + 257 }) == top)
                throw new InvalidOperationException("the platform was included beyond the native 256-unit limit");
            // Deliberate launch fixture at the front of the earned two-block perch. The real
            // player body must jump, grab and pull up; no position is supplied after take-off.
            actor.Position = new Vector3(1600, -640, 1282);
            var body = new FieldPlayer(actor, _view.FloorAt, _view.Stairs);
            body.Step(0, false, true);
            for (int i = 0; i < 100; i++) body.Step(actor.Position.Z > 840 ? 0 : null, false);
            if (_view.PlatformUnder(actor.Position) != 0 || Math.Abs(actor.Position.Y - top) > 1)
                throw new InvalidOperationException($"the running jump did not grab the Cloudstone: {actor.Position}");
            GD.Print("PLATFORM_CONTRACTS_OK: native height limit and running ledge grab");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("PLATFORM_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
