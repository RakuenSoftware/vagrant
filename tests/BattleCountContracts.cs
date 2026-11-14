// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>B7/4 counts enemies on entry so Malice's native victory condition can activate the upper bridge.</summary>
public partial class BattleCountContracts : Node
{
    private FieldView _view = null!;
    private int _frames;
    private bool _felled, _bridge;

    public override void _Ready()
    {
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 24; _view.StartMap = 9; _view.KeepScene = true;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            Input.ActionRelease("ps_circle");
            if (++_frames % 30 == 0) Input.ActionPress("ps_circle");
            if (_frames > 12000) throw new InvalidOperationException("Malice's native bridge event did not return");
            if (!_felled && _view.Controlled && _view.Battle is { } battle)
            {
                if (_view.Flags[177] != 3 || _view.Abilities.Contains(0))
                    throw new InvalidOperationException($"B7/4 did not report the three enemies: flag 177={_view.Flags[177]}");
                // Isolated defeat fixture: the earned campaign fights these actors through pad input separately.
                foreach (var c in battle.Combatants.Values)
                    if (c.Targetable) battle.Strike(c, c.Hp, flinch: false);
                _felled = true;
            }
            if ((_view.Zone, _view.Map) == (23, 4) && _view.PlatformState(0) is { Off: false }) _bridge = true;
            if (_felled && _bridge && (_view.Zone, _view.Map) == (24, 9) && _view.Controlled && _view.Settled)
            {
                if (_view.Flags[275] != 1) throw new InvalidOperationException("the bridge activation flag was lost on return");
                GD.Print("BATTLE_COUNT_CONTRACTS_OK: entry count, native victory condition, upper Cloudstone and return");
                GetTree().Quit();
            }
        }
        catch (Exception error)
        {
            GD.PrintErr("BATTLE_COUNT_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
