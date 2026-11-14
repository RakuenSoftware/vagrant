// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;
using Wol.Games.Vagrant.Scenes.Title;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Leaving the post-credits SAVE screens must retain existing files and return to the real title loop.</summary>
public partial class TitleClearSaveContracts : Node
{
    private TitleSequence _title = null!;
    private string _before = "";
    private int _frames, _menuFrames;
    private bool _cancelled;
    public override void _Ready()
    {
        try
        {
            FieldView.IsolateSaves("user://title_clear_save_contracts");
            string source = "";
            foreach (string arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith("--source=")) source = arg[9..];
            if (source.Length == 0) throw new InvalidOperationException("pass --source= with an earned save record");
            var record = Json.ParseString(System.IO.File.ReadAllText(source)).AsGodotDictionary();
            if (FieldView.WriteCardFile(1, 1, 34, record) != Error.Ok) throw new InvalidOperationException("cannot seed the isolated existing card");
            _before = Json.Stringify(FieldView.ReadCardFile(1, 1)!);
            FieldView.PendingClearSave = record.Duplicate(true);
            _title = GD.Load<PackedScene>(Wol.Games.Vagrant.Scenes.Startup.Startup.TitleScene).Instantiate<TitleSequence>();
            AddChild(_title);
        }
        catch (Exception error) { Fail(error.Message); }
    }
    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (++_frames > 3000) throw new InvalidOperationException("clear SAVE cancellation stalled");
            if (_title.Failure.Length != 0) throw new InvalidOperationException(_title.Failure);
            if (_frames % 30 == 1) { Input.ActionRelease("ps_cross"); Input.ActionRelease("ps_start"); }
            if (_title.Screen == "clear_save")
            {
                if (_title.CardStage == "Slots" && _frames % 30 == 0) { Input.ActionPress("ps_cross"); _cancelled = true; }
                return;
            }
            if (!_cancelled) throw new InvalidOperationException("the pending clear skipped its SAVE screens");
            if (_title.Screen != "menu") { if (_frames % 30 == 0) Input.ActionPress("ps_start"); return; }
            if (++_menuFrames < 120) return;
            if (Json.Stringify(FieldView.ReadCardFile(1, 1)!) != _before || FieldView.ReadCardFile(1, 2) != null || FieldView.PendingClearSave != null)
                throw new InvalidOperationException("cancelling SAVE changed the card or retained the pending clear");
            if (_title.MenuSounds == 0) throw new InvalidOperationException("the title's card screens omitted their resident sound");
            GD.Print("TITLE_CLEAR_SAVE_CONTRACTS_OK: cancelling the post-credits SAVE preserves the existing card and returns to the title menu");
            FieldView.ReleaseSaves(); GetTree().Quit();
        }
        catch (Exception error) { Fail(error.Message); }
    }
    private void Fail(string message) { GD.PrintErr("TITLE_CLEAR_SAVE_CONTRACTS_FAILED: " + message); FieldView.ReleaseSaves(); GetTree().Quit(1); }
}
