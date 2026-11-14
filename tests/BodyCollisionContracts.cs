// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Body overlap uses the owned SHP bounds, including vertical separation and escape from contact.</summary>
public partial class BodyCollisionContracts : Node
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
            battle.Combatants.Clear();
            battle.Combatants.Add(ashley.Actor.Id, ashley);
            // Deliberate geometry fixture: two instances of Ashley's owned body (radius 35, height 208).
            var node = new Node3D();
            AddChild(node);
            var actor = new EventActor("slot:15", 0, node);
            var other = battle.Enlist(actor, _view.Content.Data("player"), true, false, false);
            if (ashley.CollisionRadius != 35 || ashley.CollisionHeight != 208 || other.CollisionRadius != 35 || other.CollisionHeight != 208)
                throw new InvalidOperationException("the owned Ashley SHP body bounds did not decode");
            actor.Visible = true;
            actor.Position = Vector3.Zero;
            foreach (var (from, next, expected, why) in new[]
            {
                (new Vector3(70, -209, 0), new Vector3(69, -209, 0), false, "above the other body"),
                (new Vector3(70, -208, 0), new Vector3(69, -208, 0), true, "touching its top"),
                (new Vector3(70, 208, 0), new Vector3(69, 208, 0), true, "touching its bottom"),
                (new Vector3(70, 209, 0), new Vector3(69, 209, 0), false, "below the other body"),
                (new Vector3(71, 0, 0), new Vector3(70, 0, 0), false, "approaching contact"),
                (new Vector3(70, 0, 0), new Vector3(69, 0, 0), true, "moving inward from contact"),
                (new Vector3(70, 0, 70), new Vector3(69, 0, 69), true, "overlapping square corners"),
                (new Vector3(60, 0, 0), new Vector3(61, 0, 0), false, "escaping overlap"),
            })
            {
                ashley.Actor.Position = from;
                if (battle.Crowded(ashley.Actor, next) != expected)
                    throw new InvalidOperationException($"incorrect body collision when {why}");
            }
            ashley.Actor.Position = new Vector3(60, 0, 0);
            other.Dead = true;
            if (battle.Crowded(ashley.Actor, new Vector3(59, 0, 0))) throw new InvalidOperationException("a dead body blocked movement");
            other.Dead = false;
            actor.Visible = false;
            if (battle.Crowded(ashley.Actor, new Vector3(59, 0, 0))) throw new InvalidOperationException("an invisible body blocked movement");
            actor.Visible = true;
            ashley.PassingThrough = true;
            if (battle.Crowded(ashley.Actor, new Vector3(59, 0, 0))) throw new InvalidOperationException("passing through still collided");
            ashley.PassingThrough = false;
            if (battle.Crowded(actor, new Vector3(1, 0, 0))) throw new InvalidOperationException("an enemy was blocked by Ashley");
            GD.Print("BODY_COLLISION_CONTRACTS_OK: native body bounds, vertical contact, square corners, escape and exclusions");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("BODY_COLLISION_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
