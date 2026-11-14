// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Native teleport discovery, paling, MP, menu cancellation and faded arrival at a circle.</summary>
public partial class TeleportContracts : Node
{
    private FieldView _view = null!;
    private int _frames, _stage;

    public override void _Ready()
    {
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 32;
        _view.StartMap = 15;
        _view.KeepScene = true;
        _view.Flags[130] = 58;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 7200) throw new InvalidOperationException("teleport did not return control");
            if (!_view.Controlled || _view.Battle?.Ashley is not { } ashley) return;
            if (_stage == 0)
            {
                _view.ProcessMode = ProcessModeEnum.Disabled;
                // Deliberate eligibility fixture. The separate campaign check supplies earned state and pad movement.
                _view.Flags[181] = 0;
                _view.Flags[362] = 1;
                _view.Flags[368] = 1;
                ashley.Mp = 50;
                if (_view.CanTeleport(20)) throw new InvalidOperationException("teleport was available before learning it");
                _view.Flags[181] = 1;
                if (_view.CanTeleport(20)) throw new InvalidOperationException("teleport was available outside a circle");
                var circle = _view.Document["traps"].AsGodotArray().Select(v => v.AsGodotDictionary()).First(t => t["type"].AsInt32() == 16);
                var tile = circle["tile"].AsGodotArray();
                var at = new Vector3(tile[0].AsInt32() * 128 + 64, 0, tile[1].AsInt32() * 128 + 64);
                at.Y = _view.FloorAt(at) ?? 0;
                ashley.Actor.Position = at;
                _view.StepOnTrap((tile[0].AsInt32(), tile[1].AsInt32()));
                if (_view.SavePoint != 27 || _view.TeleportCost(20) != 39 || !_view.CanTeleport(20))
                    throw new InvalidOperationException("the native Rue Lejour to Crumbling Market cost was not 39 MP");
                _view.Flags[362] = 0;
                if (_view.CanTeleport(20) || _view.TeleportChoices.Contains(20)) throw new InvalidOperationException("an undiscovered destination was offered");
                _view.Flags[362] = 2;
                if (_view.CanTeleport(20) || !_view.TeleportChoices.Contains(20)) throw new InvalidOperationException("a paling destination was not listed but disabled");
                _view.Flags[362] = 1;
                _view.Flags[181] = 2;
                if (_view.CanTeleport(20)) throw new InvalidOperationException("the spirit paling allowed teleportation");
                _view.Flags[181] = 1;
                ashley.Mp = 0;
                if (!_view.TeleportAvailable || _view.CanTeleport(20)) throw new InvalidOperationException("an MP shortage hid the destination list or allowed casting");
                if (_view.TeleportCost(26) != 15 || _view.CanTeleport(-1) || _view.CanTeleport(48)) throw new InvalidOperationException("the current circle cost or destination bounds were incorrect");
                ashley.Mp = 38;
                if (_view.CanTeleport(20) || _view.Teleport(20) || ashley.Mp != 38) throw new InvalidOperationException("insufficient MP allowed or charged a teleport");
                ashley.Mp = 50;
                ashley.Status |= 1u << FieldBattle.StatusBit(FieldBattle.Silence);
                if (_view.CanTeleport(20)) throw new InvalidOperationException("silence allowed teleportation");
                ashley.Status = 0;
                var menu = _view.GetNode<FieldMenu>("Hud/Menu");
                menu.Show(true);
                for (int i = 0; i < 10 && menu.Row != 0; i++) menu.Step(key => key == "ps_up");
                menu.Step(key => key == "ps_circle");
                if (menu.Page != 0) throw new InvalidOperationException("the MAGIC page did not open");
                for (int i = 0; i < 4; i++) menu.Step(key => key == "ps_down");
                menu.Step(key => key == "ps_circle");
                menu.Step(key => key == "ps_cross");
                if (ashley.Mp != 50 || !_view.Controlled) throw new InvalidOperationException("cancelling the destination list spent MP or left the room");
                menu.Step(key => key == "ps_circle");
                menu.Step(key => key == "ps_up");
                menu.Step(key => key == "ps_circle");
                if (_view.Controlled || ashley.Mp != 11 || menu.Open) throw new InvalidOperationException("confirming the destination did not spend exactly 39 MP and leave");
                _stage = 1;
                _view.ProcessMode = ProcessModeEnum.Inherit;
                return;
            }
            if ((_view.Zone, _view.Map) != (48, 21)) throw new InvalidOperationException("teleport loaded the wrong native room");
            if (ashley.Mp != 11 || new Vector2(ashley.Actor.Position.X - 576, ashley.Actor.Position.Z - 320).Length() > 1)
                throw new InvalidOperationException($"teleport did not arrive at the native circle with its MP charge: {ashley.Actor.Position}, MP {ashley.Mp}");
            GD.Print("TELEPORT_CONTRACTS_OK: discovery, paling, MP, cancellation and native circle arrival");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("TELEPORT_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
