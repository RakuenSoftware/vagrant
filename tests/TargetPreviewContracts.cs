// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>The targeting readout estimates damage, independently of a healthy limb's remaining HP.</summary>
public partial class TargetPreviewContracts : Node
{
    private FieldView _view = null!;
    private int _frames;

    public override void _Ready()
    {
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 13;
        _view.StartMap = 1;
        _view.KeepScene = true;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3600) throw new InvalidOperationException("the field never handed over control");
            if (!_view.Controlled || _view.Battle?.Ashley is not { } ashley) return;
            _view.ProcessMode = ProcessModeEnum.Disabled;
            var battle = _view.Battle;
            var enemy = battle.Combatants.Values.First(c => c.Hostile && c.Limbs.Count > 0);
            // Explicit combat fixture: enough health to avoid damage capping or a crippled limb.
            enemy.Hp = enemy.MaxHp = enemy.Limbs[0].Hp = enemy.Limbs[0].MaxHp = 1000;
            ashley.Drawn = true;
            ashley.Strength = 150;
            ashley.Blade.Strength = 40;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var targets = (List<(FieldBattle.Combatant Who, List<int> Parts)>)typeof(FieldBattle).GetField("_targets", flags)!.GetValue(battle)!;
            targets.Clear(); targets.Add((enemy, new List<int> { 0 }));
            var show = typeof(FieldBattle).GetMethod("ShowTarget", flags)!;
            int hp = ashley.Hp, mp = ashley.Mp, risk = ashley.Risk, dp = ashley.Blade.Dp, pp = ashley.Blade.Pp;
            show.Invoke(battle, null);
            int first = int.Parse(Regex.Match(_view.SystemBox.Text, @" HP (\d+)/").Groups[1].Value);
            enemy.Hp = enemy.Limbs[0].Hp = 999;
            show.Invoke(battle, null);
            int wounded = int.Parse(Regex.Match(_view.SystemBox.Text, @" HP (\d+)/").Groups[1].Value);
            if (first != wounded) throw new InvalidOperationException($"remaining health changed the damage estimate: {first} to {wounded}");
            ashley.Blade.Strength += 16;
            show.Invoke(battle, null);
            int stronger = int.Parse(Regex.Match(_view.SystemBox.Text, @" HP (\d+)/").Groups[1].Value);
            if (stronger <= first) throw new InvalidOperationException($"a stronger blade did not increase the estimate: {first} to {stronger}");
            if (ashley.Hp != hp || ashley.Mp != mp || ashley.Risk != risk || ashley.Blade.Dp != dp || ashley.Blade.Pp != pp || enemy.Hp != 999 || enemy.Limbs[0].Hp != 999)
                throw new InvalidOperationException("previewing committed combat state");
            GD.Print($"TARGET_PREVIEW_CONTRACTS_OK: damage {first}, remaining health {wounded}, stronger blade {stronger}; no combat state committed");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("TARGET_PREVIEW_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
