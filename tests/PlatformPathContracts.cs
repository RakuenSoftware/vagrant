// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Reflection;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>The four-waypoint Quarry Cloudstone completes its six-leg return path without teleporting.</summary>
public partial class PlatformPathContracts : Node
{
    private FieldView _view = null!;
    private int _frames;

    public override void _Ready()
    {
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 53;
        _view.StartMap = 28;
        _view.KeepScene = true;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3600) throw new InvalidOperationException("the field never handed over control");
            if (!_view.Controlled) return;
            _view.ProcessMode = ProcessModeEnum.Disabled;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var read = typeof(FieldView).GetMethod("ReadPlatforms", flags)!;
            var step = typeof(FieldView).GetMethod("StepPlatforms", flags)!;
            read.Invoke(_view, null);
            var start = _view.PlatformState(0)!.Value.Now;
            var before = start;
            for (int tick = 1; tick <= 540; tick++)
            {
                step.Invoke(_view, null);
                var now = _view.PlatformState(0)!.Value.Now;
                if (now.DistanceTo(before) > 9)
                    throw new InvalidOperationException($"the Cloudstone jumped {now.DistanceTo(before):F2} units on tick {tick}");
                if (tick % 270 == 0 && now.DistanceTo(start) > 1)
                    throw new InvalidOperationException("the six-leg path did not return to its start");
                before = now;
            }
            var record = _view.Document["platforms"].AsGodotArray()[0].AsGodotDictionary();
            if (record["legs"].AsGodotArray().Count != 6)
                throw new InvalidOperationException("the native four-waypoint platform did not decode all six legs");
            // Deliberate initial-state fixture: start at waypoint 1 travelling along native leg 1.
            record["start"] = new Godot.Collections.Array { 448, -128, 704 };
            record["target"] = 2; record["forward"] = true; record["leg"] = 1;
            read.Invoke(_view, null);
            step.Invoke(_view, null);
            if (_view.PlatformState(0)!.Value.Now.DistanceTo(new Vector3(440, -128, 704)) > 0.01f)
                throw new InvalidOperationException("the platform ignored its initial movement leg");
            GD.Print("PLATFORM_PATH_CONTRACTS_OK: two continuous six-leg cycles and the initial leg");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("PLATFORM_PATH_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
