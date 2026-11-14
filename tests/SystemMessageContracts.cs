// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>A one-display-frame press between event ticks acknowledges the owned workshop system message.</summary>
public partial class SystemMessageContracts : Node
{
    private FieldView _view = null!;
    private int _frames, _sent;
    private long _previousEventFrame;
    private bool _otherPhase;

    public override void _Ready()
    {
        _otherPhase = Array.IndexOf(OS.GetCmdlineUserArgs(), "--other-phase") >= 0;
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.StartZone = 44;
        _view.StartMap = 0;
        _view.KeepScene = true;
        AddChild(_view);
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            Input.ActionRelease("ps_circle");
            if (++_frames > 3600) throw new InvalidOperationException("the workshop never showed its message");
            // Exercise each display phase separately; one was previously missed by the 30 Hz system-message wait.
            if (_sent == 0 && _view.SystemBox.Text.Length > 0 && (_view.EventFrame == _previousEventFrame) != _otherPhase)
            {
                Input.ActionPress("ps_circle");
                _sent = _frames;
            }
            _previousEventFrame = _view.EventFrame;
            if (_sent != 0 && _frames - _sent > 120) throw new InvalidOperationException("the system message lost a press between event ticks");
            if (!_view.Controlled) return;
            if (_sent == 0) throw new InvalidOperationException("the workshop skipped its awaited system message");
            GD.Print("SYSTEM_MESSAGE_CONTRACTS_OK: a single display-frame press between event ticks acknowledged the workshop message");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr("SYSTEM_MESSAGE_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }
}
