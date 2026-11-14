// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Continues a disk save in a fresh field, then crosses a room boundary with the same inventory.</summary>
public partial class SaveResumeContracts : Node
{
    private FieldView _view = null!;
    private int _phase, _frames, _dp, _pp, _hp, _mp, _strength, _maxHp, _maxMp, _equipment;

    public override void _Ready()
    {
        FieldView.IsolateSaves("user://save_resume_contracts");
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 13;
        _view.StartMap = 1;
        _view.StartAt = (2, 2, 0, false);
        _view.KeepScene = true;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3600) throw new InvalidOperationException($"phase {_phase} did not reach player control: {_view.EventNode}");
            if (_phase == 0 && !_view.Controlled || _view.Battle?.Ashley is not { } ashley) return;
            // Stop combat and recovery while examining the state at the handoff.
            _view.ProcessMode = ProcessModeEnum.Disabled;
            if (_phase == 0)
            {
                if (_view.Flags[1] != 1 || _view.Flags[0x11C] != 1) throw new InvalidOperationException("New Game's normal-play flags were not initialized");
                _view.Rename(_view.Weapons().Find(_view.Holds)!, "ABCDEFGHIJKLMNOPQRST");
                if (ashley.Blade.MaxDp < 8) throw new InvalidOperationException("the starting weapon cannot be worn");
                ashley.Blade.Wear(7);
                ashley.Blade.Gain(11);
                for (int stat = 0; stat < 5; stat++) _view.Grow(stat, stat + 2);
                ashley.Hp -= 23;
                ashley.Mp -= 7;
                (_dp, _pp, _hp, _mp, _strength, _maxHp, _maxMp) = (ashley.Blade.Dp, ashley.Blade.Pp, ashley.Hp, ashley.Mp, ashley.Strength, ashley.MaxHp, ashley.MaxMp);
                _equipment = _view.Equipment.Count;
                _view.Flags[700] = 37;
                _view.Flags[60] = 9;
                _view.Flags[126] = 23;
                _view.Options = 0xc0;
                _view.Visited.Add((24, 5));
                _view.StoredItems[1] = 3;
                if (_view.SaveGame(1, 1, 1) != Error.Ok) throw new InvalidOperationException("the save could not be written");
                FieldView.PendingSave = FieldView.ReadCardFile(1, 1) ?? throw new InvalidOperationException("the save could not be read");
                RemoveChild(_view);
                _view.QueueFree();
                _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
                _view.KeepScene = true;
                _phase = 1;
                AddChild(_view);
                _view.ProcessMode = ProcessModeEnum.Disabled;
                return;
            }
            if (ashley.Blade.Dp != _dp || ashley.Blade.Pp != _pp)
                throw new InvalidOperationException($"phase {_phase}: starting weapon DP/PP {_dp}/{_pp} became {ashley.Blade.Dp}/{ashley.Blade.Pp}");
            var held = _view.Weapons().Find(_view.Holds) ?? throw new InvalidOperationException("no carried weapon is equipped");
            if (held["name"].AsString() != "ABCDEFGHIJKLMNOPQRST") throw new InvalidOperationException("the twenty-character weapon name changed on resume");
            var blade = held["blade"].AsGodotDictionary();
            if (blade["dp"].AsInt32() != _dp || blade["pp"].AsInt32() != _pp)
                throw new InvalidOperationException("battle's weapon condition differs from the carried inventory");
            if (ashley.Hp != _hp || ashley.Mp != _mp || ashley.Strength != _strength || ashley.MaxHp != _maxHp || ashley.MaxMp != _maxMp)
                throw new InvalidOperationException($"phase {_phase}: HP/MP/STR/max HP/max MP {_hp}/{_mp}/{_strength}/{_maxHp}/{_maxMp} became {ashley.Hp}/{ashley.Mp}/{ashley.Strength}/{ashley.MaxHp}/{ashley.MaxMp}");
            if (_view.Equipment.Count != _equipment || _view.StoredItems.GetValueOrDefault(1) != 3 || _view.Flags[700] != 37 || _view.Options != 0xc0 || !_view.Visited.Contains((24, 5)))
                throw new InvalidOperationException($"phase {_phase}: inventory, story, map or options changed");
            if (_view.Flags[60] != (_phase == 1 ? 9 : 0) || _view.Flags[126] != (_phase <= 2 ? 23 : 0))
                throw new InvalidOperationException($"phase {_phase}: room/zone variables did not follow their native resume and load boundaries");
            if (_phase == 1)
            {
                // Later wear must update the carried weapon too, after JSON has broken object identity.
                ashley.Blade.Wear(3);
                _dp = ashley.Blade.Dp;
                _view.ProcessMode = ProcessModeEnum.Inherit;
                _phase = 2;
                _view.LoadRoom(13, 2);
                _view.ProcessMode = ProcessModeEnum.Disabled;
                return;
            }
            if (_phase is 2 or 3)
            {
                _view.Flags[126] = 47;
                _view.ProcessMode = ProcessModeEnum.Inherit;
                if (_phase++ == 2) _view.LoadRoom(13, 2, reloadZone: true);
                else _view.LoadRoom(42, 0);
                _view.ProcessMode = ProcessModeEnum.Disabled;
                return;
            }
            GD.Print("SAVE_RESUME_CONTRACTS_OK: weapon wear/PP, permanent stats, HP/MP, inventory, storage, story flags, map and options survive disk resume; room variables reset on room loads; zone variables survive same-zone doors and reset on ZND loads");
            FieldView.ReleaseSaves();
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.Print("SAVE_RESUME_CONTRACTS_FAILED: " + error.Message);
            FieldView.ReleaseSaves();
            GetTree().Quit(1);
        }
    }
}
