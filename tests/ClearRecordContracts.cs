// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Isolated boundary fixtures for the native ending's persistent record and story reset.</summary>
public partial class ClearRecordContracts : Node
{
    private FieldView _view = null!;
    private int _frames;
    public override void _Ready()
    {
        FieldView.IsolateSaves("user://clear_record_contracts");
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 13; _view.StartMap = 1; _view.KeepScene = true;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3600) throw new InvalidOperationException("the record fixture did not load");
            if (!_view.Controlled || _view.Battle?.Ashley is not { } ashley) return;
            _view.ProcessMode = ProcessModeEnum.Disabled;
            var recordClear = typeof(FieldView).GetMethod("RecordClear", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var content = _view.Content;
            var results = new ResultsScreen();
            AddChild(results);
            results.Configure(content.Disc("SMALL/RANK.DIS"), content.Data("results"), null, null);
            foreach (int links in new[] { 0, 5, 6, 10, 11, 15, 16, 20, 21, 25, 26, 30, 31, 50, 51 })
            {
                int factor = links switch { <= 5 => 10, <= 10 => 20, <= 15 => 40, <= 20 => 80, <= 25 => 160, <= 30 => 320, <= 50 => 640, _ => 1280 };
                Expect(results.Reckon(Array.Empty<int>(), Array.Empty<int>(), links, 0, 0, 0, 0, 0) == links * factor, "the native maximum-chain score band changed");
            }
            _view.Clears = 1; SetFrames(600 * 3600);
            recordClear.Invoke(_view, null);
            Expect((_view.Titles & 2) == 0 && _view.TitleCount == 0, "600 minutes earned the under-ten-hours title, or high bits counted as rank titles");
            SetFrames(599 * 3600);
            _view.Flags[0xcc] = 1; _view.Flags[0x383] = 1;
            _view.Abilities.Clear();
            _view.Abilities.AddRange(Enumerable.Range(22, 32).Where(i => i is not (33 or 36 or 38 or 39 or 53)));
            _view.Arts.AddRange(Enumerable.Range(184, 39));
            recordClear.Invoke(_view, null);
            Expect((_view.Titles & 0xc000) == 0, "an incomplete ability/art collection earned a title");
            _view.Abilities.Add(53); _view.Arts.Add(223);
            recordClear.Invoke(_view, null);
            Expect((_view.Titles & 0xd012) == 0xd012 && _view.TitleCount == 5, "native clear titles were not recorded or counted");
            _view.Clears = 2; SetFrames(700 * 3600);
            recordClear.Invoke(_view, null);
            Expect(_view.BestClearMinutes == 599, "a slower second clear replaced the best time");
            _view.Acted(96); _view.CountAbilityPoint(true); _view.Acted(184);
            Expect(_view.Flags[0xb7] == 1 && _view.Flags[0xb8] == 1 && _view.Flags[0xb9] == 1, "used magic, chain/defense and Break Arts were not recorded for the clear titles");
            _view.Flags[0] = 99; _view.Flags[15] = 17; _view.Flags[16] = 99; _view.Flags[130] = 83; _view.Flags[279] = 2;
            _view.StoredItems[1] = 3;
            _view.BestMapPercent = 59;
            _view.Titles &= ~0x10000u;
            Expect(_view.SaveGame(1, 1, 1) == Error.Ok && _view.RunSaveCount == 1, "a normal save did not increment the current game's save count");
            recordClear.Invoke(_view, null);
            Expect((_view.Titles & 0x10000) == 0, "a game with a save earned the no-save title");
            int saves = _view.SaveCount, equipment = _view.Equipment.Count;
            var clear = _view.ClearedSave() ?? throw new InvalidOperationException("no clear snapshot");
            var flags = clear["flags"].AsGodotArray();
            Expect(clear["clear_count"].AsInt32() == 99 && flags[0].AsInt32() == 99 && flags[2].AsInt32() == 1 && flags[15].AsInt32() == 17, "clear cap or persistent mode flags changed");
            Expect(Enumerable.Range(16, flags.Count - 16).All(i => flags[i].AsInt32() == 0), "story variables survived the native clear reset");
            Expect(_view.Flags[130] == 83 && _view.Flags[279] == 2 && _view.SaveCount == saves, "preparing a clear changed the live field");
            Expect(FieldView.WriteCardFile(1, 2, 1, clear) == Error.Ok, "the cleared record could not be saved");
            FieldView.PendingSave = FieldView.ReadCardFile(1, 2)!;
            var resumed = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
            resumed.KeepScene = true; AddChild(resumed); resumed.ProcessMode = ProcessModeEnum.Disabled;
            Expect(resumed.Flags[0] == 99 && resumed.Flags[2] == 1 && resumed.Zone == 1 && resumed.Map == 0, "the clear did not restart at the opening");
            Expect(resumed.Titles == _view.Titles && resumed.BestClearMinutes == 599 && resumed.MapPercent() == 59, "persistent title/time/map records changed on disk resume");
            Expect(resumed.Equipment.Count == equipment && resumed.StoredItems.GetValueOrDefault(1) == 3 && resumed.Battle?.Ashley?.MaxHp == ashley.MaxHp, "carried equipment, storage or permanent stats changed");
            Expect(resumed.RunSaveCount == 0 && resumed.SaveCount > 0, "the cleared game confused per-game and lifetime saves");
            recordClear.Invoke(resumed, null);
            Expect((resumed.Titles & 0x10000) != 0, "lifetime saves prevented a later unsaved game's title");
            var legacy = clear.Duplicate(true);
            legacy.Remove("clear_use_tracked");
            legacy["score"].AsGodotDictionary()["titles"] = 0;
            FieldView.PendingSave = legacy;
            var old = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
            old.KeepScene = true; AddChild(old); old.ProcessMode = ProcessModeEnum.Disabled;
            recordClear.Invoke(old, null);
            Expect((old.Titles & 0xe0000) == 0, "missing pre-feature action history earned a no-use title");
            GD.Print("CLEAR_RECORD_CONTRACTS_OK: native clear reset/cap, title thresholds, best time/map, inventory/storage/stats and disk resume");
            FieldView.ReleaseSaves(); GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("CLEAR_RECORD_CONTRACTS_FAILED: " + error.Message);
            FieldView.ReleaseSaves(); GetTree().Quit(1);
        }
    }
    private void SetFrames(long value) => typeof(FieldView).GetProperty("PlayFrames")!.SetValue(_view, value);
    private static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
