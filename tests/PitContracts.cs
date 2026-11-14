// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>A pit does not catch a body above it, but catches a fall within the same tile.</summary>
public partial class PitContracts : Node
{
    private FieldView _view = null!;
    private int _frames, _started, _returned, _hp;

    public override void _Ready()
    {
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 50;
        _view.StartMap = 18;
        _view.StartAt = (3, 9, 0, false);
        _view.KeepScene = true;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3600) throw new InvalidOperationException("the room never handed over control");
            if (!_view.Controlled || _view.Battle?.Ashley is not { } ashley) return;
            if (_started == 0)
            {
                if ((_view.TileAttributes(3, 8) & 1 << 17) == 0) throw new InvalidOperationException("the fixture tile is not a native pit");
                // Position is a deliberate fixture; the real field control then handles its fall.
                ashley.Actor.Position = new Vector3(448, -512, 1088);
                _hp = ashley.Hp;
                _started = _frames;
                return;
            }
            if (_returned > 0)
            {
                if (ashley.Hp < _hp - 20 || ashley.Actor.Position.DistanceTo(new Vector3(448, -384, 1216)) > 1)
                    throw new InvalidOperationException($"the pit return changed after {_frames - _returned} frames: HP {ashley.Hp}/{_hp - 20}, position {ashley.Actor.Position}");
                if (_frames - _returned < 120) return;
                _view.ProcessMode = ProcessModeEnum.Disabled;
                GD.Print("PIT_CONTRACTS_OK: one 20-HP fall returns to the arrival and remains stable afterward");
                GetTree().Quit();
                return;
            }
            if (ashley.Hp < _hp)
            {
                if (_frames - _started < 20 || ashley.Hp != _hp - 20)
                    throw new InvalidOperationException("pit damage occurred above the native fall threshold or had the wrong cost");
                if (ashley.Actor.Position.DistanceTo(new Vector3(448, -384, 1216)) > 1)
                    throw new InvalidOperationException("the pit did not return Ashley to her arrival tile");
                _returned = _frames;
                return;
            }
            if (Math.Abs(ashley.Actor.Position.X - 448) > 1 || Math.Abs(ashley.Actor.Position.Z - 1088) > 1)
                throw new InvalidOperationException("the falling body left its fixture tile");
            if (_frames - _started > 120) throw new InvalidOperationException("the fall was never caught after staying in the same tile");
        }
        catch (Exception error)
        {
            GD.PrintErr("PIT_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
