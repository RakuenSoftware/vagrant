// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Checks full-category refusals and partial GET/ALL transactions through the gadget bag.</summary>
public partial class BagCapacityContracts : Node
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
            if (!_view.Controlled) return;
            _view.ProcessMode = ProcessModeEnum.Disabled;
            // Deliberate capacity fixture: every armour slot and MISC type slot is occupied.
            _view.Equipment.Clear();
            _view.Items.Clear();
            for (int i = 0; i < GadgetBag.Limits[4]; i++)
                _view.Equipment.Add(new GDictionary { ["kind"] = "armour", ["record"] = new GDictionary { ["id"] = 195, ["category"] = 5 } });
            for (int i = 0; i < GadgetBag.Limits[6]; i++) _view.Items[323 + i] = 2;
            var contents = new GArray
            {
                new GDictionary { ["kind"] = "armour", ["record"] = new GDictionary { ["id"] = 196, ["category"] = 5 } },
                new GDictionary { ["kind"] = "gem", ["record"] = new GDictionary { ["id"] = 269 } },
                new GDictionary { ["kind"] = "item", ["id"] = 323, ["count"] = 3 },
                new GDictionary { ["kind"] = "item", ["id"] = 470, ["count"] = 1 },
            };
            var chest = new GDictionary { ["index"] = 63, ["contents"] = contents };
            _view.Flags[0x3ff] = 0;
            _view.Flags[0x1B6 + 470] = 0;
            var menu = _view.GetNode<FieldMenu>("Hud/Menu");
            menu.OpenChest(chest);
            menu.Step(key => key == "ps_circle");
            if (contents.Count != 4 || _view.BagCounts()[4] != 16)
                throw new InvalidOperationException("GET exceeded the armour limit or removed a refused entry");
            menu.Step(key => key == "ps_square");
            if (contents.Count != 2 || _view.BagCounts()[4] != 16 || _view.BagCounts()[5] != 1
                || _view.BagCounts()[6] != 64 || _view.Items[323] != 5 || _view.Items.ContainsKey(470) || _view.Flags[0x1B6 + 470] != 0 || _view.Flags[0x3ff] != 0)
                throw new InvalidOperationException("ALL failed to skip full categories, add a stack, or preserve uncollected loot");
            menu.Step(key => key == "ps_square");
            if (contents.Count != 2 || !menu.Open) throw new InvalidOperationException("ALL did not leave refused entries available");
            _view.Equipment.RemoveAt(0);
            menu.Step(key => key == "ps_circle");
            if (contents.Count != 1 || _view.BagCounts()[4] != 16 || _view.Flags[0x3ff] != 0)
                throw new InvalidOperationException("GET did not use the freed armour slot");
            _view.Items.Remove(324);
            menu.Step(key => key == "ps_circle");
            if (contents.Count != 0 || menu.Open || _view.Items[470] != 1 || _view.BagCounts()[6] != 64 || _view.Flags[0x3ff] != 1 || _view.Flags[0x1B6 + 470] != 1)
                throw new InvalidOperationException("the final GET did not collect and close the chest");
            // A collected sigil is state 1; opening its lock must consume it and advance to state 2.
            _view.Document["conditions"] = new GArray();
            var gate = new GDictionary { ["tile"] = new GArray { 0, 0 }, ["kind"] = 0, ["lock"] = 1, ["key"] = 470 };
            _view.Document["locks"] = new GArray { gate };
            var actor = _view.Battle!.Ashley!.Actor;
            actor.Position = new Vector3(64, 0, 192);
            actor.Facing = 0;
            _view.Interact(actor);
            if (_view.Items.ContainsKey(470) || _view.Flags[0x1B6 + 470] != 2 || gate["lock"].AsInt32() != 0)
                throw new InvalidOperationException("a collected sigil opened its gate without being consumed");
            GD.Print("BAG_CAPACITY_CONTRACTS_OK: GET refuses full categories; ALL skips refusals, permits existing stacks, terminates, and collects only when empty");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("BAG_CAPACITY_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
