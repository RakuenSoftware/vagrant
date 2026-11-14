// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Container selections must refer to individual entries, including equal copies.</summary>
public partial class ContainerContracts : Node
{
    private FieldView _view = null!;
    private int _frames;
    public override void _Ready()
    {
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 13; _view.StartMap = 1; _view.KeepScene = true;
        AddChild(_view);
    }
    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3600) throw new InvalidOperationException("the field did not hand over control");
            if (!_view.Controlled) return;
            _view.ProcessMode = ProcessModeEnum.Disabled;
            _view.Equipment.Clear(); _view.Stored.Clear(); _view.Items.Clear(); _view.StoredItems.Clear();
            var first = new GDictionary { ["kind"] = "armour", ["record"] = new GDictionary { ["id"] = 195, ["category"] = 5 } };
            var second = new GDictionary { ["kind"] = "armour", ["record"] = new GDictionary { ["id"] = 196, ["category"] = 5 } };
            _view.Equipment.Add(first); _view.Equipment.Add(first.Duplicate(true));
            _view.Equipment.Add(second); _view.Equipment.Add(second.Duplicate(true));
            var menu = _view.GetNode<FieldMenu>("Hud/Menu");
            menu.OpenContainer(); menu.Step(key => key == "ps_circle");
            for (int n = 0; _view.ContainerAccessing && n < 1000; n++) menu.Step(_ => false);
            if (_view.ContainerAccessing) throw new InvalidOperationException("container card access did not finish");
            // STORE, then the armour category to the right of STORED.
            menu.Step(key => key == "ps_down"); menu.Step(key => key == "ps_circle");
            for (int n = 0; n < 5; n++) menu.Step(key => key == "ps_right");
            for (int n = 0; n < 4; n++) { menu.Step(key => key == "ps_circle"); menu.Step(key => key == "ps_circle"); }
            menu.Step(key => key == "ps_cross"); menu.Step(key => key == "ps_circle");
            if (_view.Stored.Count != 4 || _view.Equipment.Count != 0)
                throw new InvalidOperationException($"STORE duplicated a selected entry or skipped its equal copy: stored {_view.Stored.Count}, carried {_view.Equipment.Count}");
            // REMOVE all four, proving selections are rebuilt for the other inventory.
            menu.Step(key => key == "ps_up"); menu.Step(key => key == "ps_circle");
            for (int n = 0; n < 5; n++) menu.Step(key => key == "ps_right");
            for (int n = 0; n < 4; n++) { menu.Step(key => key == "ps_circle"); menu.Step(key => key == "ps_circle"); }
            menu.Step(key => key == "ps_cross"); menu.Step(key => key == "ps_circle");
            if (_view.Stored.Count != 0 || _view.Equipment.Count != 4)
                throw new InvalidOperationException("REMOVE did not return each stored entry exactly once");
            // Discard an unpicked entry while another is pending; later selection indices must still refer
            // to the intended pieces. Then cancel a removal without moving anything.
            menu.Step(key => key == "ps_down"); menu.Step(key => key == "ps_circle");
            for (int n = 0; n < 5; n++) menu.Step(key => key == "ps_right");
            menu.Step(key => key == "ps_circle"); menu.Step(key => key == "ps_circle");
            menu.Step(key => key == "ps_circle"); menu.Step(key => key == "ps_down"); menu.Step(key => key == "ps_circle");
            menu.Step(key => key == "ps_circle"); menu.Step(key => key == "ps_circle");
            menu.Step(key => key == "ps_cross"); menu.Step(key => key == "ps_circle");
            int a = 0, b = 0;
            foreach (var value in _view.Stored)
                if (value.AsGodotDictionary()["record"].AsGodotDictionary()["id"].AsInt32() == 195) a++; else b++;
            if (a != 1 || b != 1 || _view.Equipment.Count != 1)
                throw new InvalidOperationException("discarding a candidate changed the pending transfer's entries");
            menu.Step(key => key == "ps_up"); menu.Step(key => key == "ps_circle");
            for (int n = 0; n < 5; n++) menu.Step(key => key == "ps_right");
            menu.Step(key => key == "ps_circle"); menu.Step(key => key == "ps_circle");
            menu.Step(key => key == "ps_triangle");
            if (_view.Stored.Count != 2 || _view.Equipment.Count != 1)
                throw new InvalidOperationException("cancelling the pending removal moved equipment");
            // Discarding one item keeps the rest of its stack selectable in the same transaction.
            _view.Items[323] = 3;
            menu.Step(key => key == "ps_down"); menu.Step(key => key == "ps_circle");
            menu.Step(key => key == "ps_left");
            menu.Step(key => key == "ps_circle"); menu.Step(key => key == "ps_down"); menu.Step(key => key == "ps_circle");
            menu.Step(key => key == "ps_circle"); menu.Step(key => key == "ps_circle");
            menu.Step(key => key == "ps_cross"); menu.Step(key => key == "ps_circle");
            if (_view.Items.ContainsKey(323) || _view.StoredItems.GetValueOrDefault(323) != 2)
                throw new InvalidOperationException("discarding one item hid or duplicated the remaining stack");
            menu.Step(key => key == "ps_up"); menu.Step(key => key == "ps_circle");
            menu.Step(key => key == "ps_left");
            menu.Step(key => key == "ps_circle"); menu.Step(key => key == "ps_circle");
            menu.Step(key => key == "ps_cross"); menu.Step(key => key == "ps_circle");
            if (_view.Items.GetValueOrDefault(323) != 2 || _view.StoredItems.ContainsKey(323))
                throw new InvalidOperationException("REMOVE did not return the remaining item stack");
            GD.Print("CONTAINER_CONTRACTS_OK: distinct and equal equipment entries transfer exactly once; discard and cancellation preserve the selection");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PrintErr("CONTAINER_CONTRACTS_FAILED: " + error.Message); GetTree().Quit(1); }
    }
}
