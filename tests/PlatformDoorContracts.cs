// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Reflection;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>The Temple exit belongs to the lift's upper stop, rather than the static tile beneath it.</summary>
public partial class PlatformDoorContracts : Node
{
    private FieldView _view = null!;
    private int _frames;

    public override void _Ready()
    {
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 31; _view.StartMap = 1; _view.KeepScene = true;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3600) throw new InvalidOperationException("the room never handed over control");
            if (!_view.Controlled || _view.Player is not { } actor) return;
            _view.ProcessMode = ProcessModeEnum.Disabled;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var step = typeof(FieldView).GetMethod("StepPlatforms", flags)!;
            var control = typeof(FieldView).GetMethod("StepControl", flags)!;
            var door = typeof(FieldView).GetField("_door", flags)!;
            if ((_view.TileAttributes(4, 14) & 0x4000) != 0)
                throw new InvalidOperationException("the exit was incorrectly attached to the static floor");
            // Isolated boarding fixture. Subsequent movement is the owner's actual lift and player control.
            var start = _view.PlatformState(0)!.Value.Now;
            actor.Position = start + new Vector3(0, 1, 0);
            control.Invoke(_view, null);
            for (int tick = 1; tick <= 80; tick++)
            {
                step.Invoke(_view, null);
                control.Invoke(_view, null);
                if (tick < 80 && door.GetValue(_view) != null)
                    throw new InvalidOperationException($"the exit opened before arrival, tick {tick}");
            }
            if (Math.Abs(actor.Position.Y + 1279) > 2 || _view.PlatformUnder(actor.Position) != 0)
                throw new InvalidOperationException($"Ashley did not ride to the upper stop: {actor.Position}");
            if (door.GetValue(_view) is not Godot.Collections.Dictionary exit
                || exit["zone"].AsInt32() != 39 || exit["map"].AsInt32() != 0)
                throw new InvalidOperationException("the lift did not trigger its level-2 exit without a tile change");
            GD.Print("PLATFORM_DOOR_CONTRACTS_OK: no early exit; upper-stop door without horizontal movement");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("PLATFORM_DOOR_CONTRACTS_FAILED: " + (error.InnerException ?? error).Message);
            GetTree().Quit(1);
        }
    }
}
