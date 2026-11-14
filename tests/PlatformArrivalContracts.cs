// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Reflection;
using Godot;
using Wol.Engine.World;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>A platform-level door arrives at its receiving lift's door stop and returns along the next leg.</summary>
public partial class PlatformArrivalContracts : Node
{
    private FieldView _view = null!;
    private int _frames;

    public override void _Ready()
    {
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 22; _view.StartMap = 7; _view.KeepScene = true;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3600) throw new InvalidOperationException("the room never handed over control");
            if (!_view.Controlled) return;
            _view.ProcessMode = ProcessModeEnum.Disabled;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            // Isolated transition fixture: Marid's earned activation flag, then the actual decoded lift door.
            _view.Flags[271] = 1;
            typeof(FieldView).GetField("_door", flags)!.SetValue(_view, _view.Document["doors"].AsGodotArray()[1].AsGodotDictionary());
            _view.Field.SetScreenFade(ScreenFadeMode.Subtract, 1, 0);
            typeof(FieldView).GetMethod("StepDoor", flags)!.Invoke(_view, null);
            if ((_view.Zone, _view.Map) != (24, 7) || _view.Player is not { } actor)
                throw new InvalidOperationException("the decoded door did not reach Cracked Pleasures");
            var platform = _view.PlatformState(0)!.Value;
            var expected = new Vector3(1216, -64, 448);
            if (platform.Off || platform.Now.DistanceTo(expected) > 1 || actor.Position.DistanceTo(expected) > 1)
                throw new InvalidOperationException($"arrival missed the receiving door stop: Ashley {actor.Position}, platform {platform}");
            if (platform.Bound.DistanceTo(new Vector3(1216, -384, 448)) > 1 || platform.Waited != -1)
                throw new InvalidOperationException("the arriving lift did not immediately return along its native leg");
            var step = typeof(FieldView).GetMethod("StepPlatforms", flags)!;
            for (int tick = 0; tick < 40; tick++) step.Invoke(_view, null);
            if (actor.Position.DistanceTo(new Vector3(1216, -384, 448)) > 1 || _view.PlatformUnder(actor.Position) != 0)
                throw new InvalidOperationException("Ashley did not ride the receiving lift to the gallery");
            if ((_view.TileAttributes(0, 0, 2) & 0x4000) != 0)
                throw new InvalidOperationException("the receiving lift opened its return door at the wrong stop");
            // Follow the two actual connecting door records as transition fixtures, returning on platform 1.
            foreach (int exit in new[] { 1, 0 })
            {
                typeof(FieldView).GetField("_door", flags)!.SetValue(_view, _view.Document["doors"].AsGodotArray()[exit].AsGodotDictionary());
                _view.Field.SetScreenFade(ScreenFadeMode.Subtract, 1, 0);
                typeof(FieldView).GetMethod("StepDoor", flags)!.Invoke(_view, null);
            }
            if ((_view.Zone, _view.Map) != (24, 7) || _view.Player is not { } returning
                || _view.PlatformState(1) is not { Off: false } second
                || second.Now.DistanceTo(new Vector3(448, -1344, 1344)) > 1
                || returning.Position.DistanceTo(second.Now) > 1 || _view.PlatformUnder(returning.Position) != 1)
                throw new InvalidOperationException("a level-3 arrival did not select the second receiving lift");
            for (int tick = 0; tick < 104; tick++) step.Invoke(_view, null);
            if (returning.Position.DistanceTo(new Vector3(448, -512, 1344)) > 1)
                throw new InvalidOperationException("the second receiving lift ignored its return leg");
            GD.Print("PLATFORM_ARRIVAL_CONTRACTS_OK: levels 2/3, receiving stops, enabled lifts, return legs and riders");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("PLATFORM_ARRIVAL_CONTRACTS_FAILED: " + (error.InnerException ?? error).Message);
            GetTree().Quit(1);
        }
    }
}
