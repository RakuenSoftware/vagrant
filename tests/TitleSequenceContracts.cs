// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Wol.Games.Vagrant.Title;
using Wol.Engine;
using Wol.Games.Vagrant.Runtime;
using Wol.Games.Vagrant.Scenes.Title;

namespace Wol.Games.Vagrant.Tests;

/// <summary>
/// Drives the installed title through the shared engine flow with pad input: logos, intro skip,
/// menu rotation, both option pickers, New Game, Continue and the idle timeout loop.
/// Requires an installation (<c>--install-root=</c>). <c>--capture=DIR</c> saves frames when rendering.
/// </summary>
public partial class TitleSequenceContracts : Node
{
    private TitleSequence _title = null!;
    private IEnumerator<int>? _script;
    private int _wait;
    private string _capture = "";
    private bool _menuOnly;
    private readonly List<string> _screens = new();

    public override void _Ready()
    {
        // The check's own memory cards, empty, never the player's, and apart from other runs'.
        Scenes.Field.FieldView.IsolateSaves("user://contract_saves_title");
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--capture=", StringComparison.Ordinal)) _capture = argument["--capture=".Length..];
            if (argument == "--menu-only") _menuOnly = true;
        }
        var content = GetNode<RetailContent>("/root/RetailContent");
        if (!content.Installed) Finish("installation unavailable: " + content.Failure);
        else
        {
            // The contract keeps running while the scene manager pauses the tree for New Game's scene change.
            ProcessMode = ProcessModeEnum.Always;
            GetNode<SceneManager>("/root/SceneManager").SetLoadingImage(content.LoadingImage(), new Vector2(320, 240));
            _title = GD.Load<PackedScene>("res://scenes/title/title.tscn").Instantiate<TitleSequence>();
            _title.ProcessMode = ProcessModeEnum.Pausable;
            AddChild(_title);
            _script = Script();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_script == null) return;
        if (_title.Failure.Length != 0) { Finish("title failed: " + _title.Failure); return; }
        if (_screens.Count == 0 || _screens[^1] != _title.Screen) _screens.Add(_title.Screen);
        if (_wait > 0 && --_wait > 0) return;
        try
        {
            if (!_script.MoveNext()) { _script = null; return; }
            _wait = _script.Current;
        }
        catch (Exception error)
        {
            Finish(error.Message);
        }
    }

    private void Finish(string error)
    {
        _script = null;
        foreach (string action in new[] { "ps_circle", "ps_cross", "ps_start", "ps_up", "ps_down" }) Input.ActionRelease(action);
        // The menu's walk turns the wheel and picks, so its sounds must have played.
        if (error.Length == 0 && IsInstanceValid(_title) && _title.MenuSounds == 0) error = "the title menu played no sounds";
        if (IsInstanceValid(_title)) GD.Print($"TITLE: {_title.MenuSounds} menu sounds played");
        if (error.Length == 0) GD.Print("TITLE_SEQUENCE_OK: " + string.Join(" > ", _screens));
        else GD.PrintErr("TITLE_SEQUENCE_FAILED: " + error + " (screens " + string.Join(" > ", _screens) + ")");
        Scenes.Field.FieldView.ReleaseSaves();
        GetTree().Quit(error.Length == 0 ? 0 : 1);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private IEnumerable<int> Until(Func<bool> condition, int limit, string what)
    {
        for (int i = 0; !condition(); i++)
        {
            Expect(i < limit, $"timed out waiting for {what} (screen {_title.Screen}, frame {_title.Frame})");
            yield return 1;
        }
    }

    /// <summary>Holds a button for four display frames, long enough for the title's 30 Hz pad reads.</summary>
    private IEnumerable<int> Press(string action)
    {
        Input.ActionPress(action);
        yield return 4;
        Input.ActionRelease(action);
        yield return 1;
    }

    private void Capture(string name) => FieldContracts.Capture(this, _capture, name);

    private IEnumerator<int> Script()
    {
        foreach (int f in Until(() => _title.Screen == "publisher", 10, "the publisher logo")) yield return f;
        long start = _title.Frame;
        yield return 120;
        Capture("01_publisher");
        foreach (int f in Until(() => _title.Screen == "developer", 400, "the developer logo")) yield return f;
        long publisher = _title.Frame - start;
        Expect(Math.Abs(publisher - TitleFlow.LogoFrames) <= 2, $"publisher logo lasted {publisher} frames");
        yield return 60;
        Capture("02_developer");
        start = _title.Frame;
        foreach (int f in Press("ps_circle")) yield return f;
        foreach (int f in Until(() => _title.Screen == "intro", 60, "a skipped developer logo")) yield return f;
        Expect(_title.Frame - start < 45, "holding a button did not shorten the developer logo");

        yield return 450;
        Capture("03_intro_movie");
        Expect(_title.MusicPlaying, "the intro music is not playing");
        foreach (int f in Press("ps_start")) yield return f;
        foreach (int f in Until(() => _title.Screen == "menu", 10, "the title screen after skipping")) yield return f;
        Expect(_title.MusicPlaying, "skipping the picture must not stop the title music");
        double heard = _title.MusicPosition;

        yield return 240;
        // The movie's player is hidden with its picture; its sound must keep playing under the title screen.
        Expect(_title.MusicPosition - heard > 3, $"the title music stalled with the hidden movie ({heard:0.00} s → {_title.MusicPosition:0.00} s)");
        Capture("04_title_menu");
        Expect(_title.SelectedItem == TitleMenuState.NewGame, "the cursor should start on New Game");
        Expect(_title.Menu[TitleMenuState.NewGame].Row == TitleMenuState.Selected && _title.Menu[TitleMenuState.NewGame].Brightness == 128, "New Game is not lit in the selected slot");
        Expect(_title.Menu[TitleMenuState.NewGame].Warmth == 16, "New Game did not warm to its lit palette");

        foreach (int f in Press("ps_down")) yield return f;
        yield return 40;
        Capture("05_continue");
        Expect(_title.SelectedItem == TitleMenuState.Continue, "Down did not select Continue");
        Expect(_title.Menu[TitleMenuState.Continue].Row == TitleMenuState.Selected, "Continue did not rotate into the selected slot");
        foreach (int f in Press("ps_down")) yield return f;
        yield return 40;
        Expect(_title.SelectedItem == TitleMenuState.Vibration, "Down did not select Vibration");

        Expect(_title.Vibration, "vibration should default to on");
        foreach (int f in Press("ps_circle")) yield return f;
        yield return 90;
        Capture("06_vibration_options");
        Expect(_title.Menu[TitleMenuState.VibrationOn].Enabled && _title.Menu[TitleMenuState.VibrationOff].Enabled, "the vibration options did not open");
        foreach (int f in Press("ps_down")) yield return f;
        yield return 30;
        foreach (int f in Press("ps_circle")) yield return f;
        yield return 80;
        Expect(!_title.Vibration, "choosing Off did not disable vibration");
        Expect(!_title.Menu[TitleMenuState.VibrationOn].Enabled, "the vibration options did not close");
        Expect(_title.Menu[TitleMenuState.Vibration].Row == TitleMenuState.Selected, "Vibration did not return to the selected slot");

        foreach (int f in Press("ps_down")) yield return f;
        yield return 40;
        Expect(_title.SelectedItem == TitleMenuState.Sound, "Down did not select Sound");
        foreach (int f in Press("ps_circle")) yield return f;
        yield return 90;
        Capture("07_sound_options");
        foreach (int f in Press("ps_down")) yield return f;
        yield return 30;
        foreach (int f in Press("ps_circle")) yield return f;
        yield return 80;
        Expect(_title.MonoSound, "choosing Mono did not select mono sound");
        foreach (int f in Press("ps_circle")) yield return f;
        yield return 90;
        foreach (int f in Press("ps_cross")) yield return f;
        yield return 80;
        Expect(_title.MonoSound, "cancelling with Cross changed the sound setting");

        foreach (int f in Press("ps_up")) yield return f;
        yield return 40;
        Expect(_title.SelectedItem == TitleMenuState.Vibration, "Up did not select Vibration");
        foreach (int f in Press("ps_up")) yield return f;
        yield return 40;
        foreach (int f in Press("ps_up")) yield return f;
        yield return 40;
        Expect(_title.SelectedItem == TitleMenuState.NewGame, "Up did not return to New Game");

        // The added Settings word sits between Sound and New Game on the wheel.
        foreach (int f in Press("ps_up")) yield return f;
        yield return 40;
        Expect(_title.SelectedItem == TitleMenuState.Settings, "Up from New Game did not select Settings");
        Capture("08_settings_selected");
        foreach (int f in Press("ps_circle")) yield return f;
        foreach (int f in Until(() => _title.SettingsOpen, 10, "the Settings dialog")) yield return f;
        yield return 10;
        Capture("09_settings_dialog");
        var audio = GetNode<AudioSettings>("/root/AudioSettings");
        float volume = audio.Volume;
        _title.VolumeSlider.Value = 50;
        Expect(Math.Abs(audio.Volume - 0.5f) < 0.001f, "the volume slider did not change the master volume");
        _title.VolumeSlider.Value = volume * 100;
        // The shared controls panel: Triangle rebinds to J through it, and Reset brings the project's keys back. The
        // panel saves as it goes, so the player's preferences are put back afterwards.
        byte[]? preferences = Godot.FileAccess.FileExists(Wol.Engine.Display.DisplaySettings.Preferences) ? Godot.FileAccess.GetFileAsBytes(Wol.Engine.Display.DisplaySettings.Preferences) : null;
        var controls = _title.GetNode<Wol.Engine.Controls.ControlsSettings>("Settings/Tabs/Controls/Controls");
        bool HasKey(string action, Key key) => System.Linq.Enumerable.Any(InputMap.ActionGetEvents(action), e => e is InputEventKey k && k.PhysicalKeycode == key);
        var defaults = new List<long>();
        foreach (var e in InputMap.ActionGetEvents("ps_triangle")) if (e is InputEventKey k) defaults.Add((long)k.PhysicalKeycode);
        controls.GetNode<Button>("Bindings/ps_triangle_Keyboard_0").EmitSignal(BaseButton.SignalName.Pressed);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.J, PhysicalKeycode = Key.J, Pressed = true });
        yield return 2;
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.J, PhysicalKeycode = Key.J, Pressed = false });
        yield return 2;
        Expect(!controls.Waiting && HasKey("ps_triangle", Key.J) && _title.SettingsOpen, "the controls panel did not rebind Triangle to J");
        var tabs = _title.GetNode<TabContainer>("Settings/Tabs");
        tabs.CurrentTab = 1;
        // The captured frame lags what was just set by a few frames.
        yield return 30;
        Capture("09b_controls_rebound");
        tabs.CurrentTab = 0;
        controls.GetNode<Button>("Reset").EmitSignal(BaseButton.SignalName.Pressed);
        Expect(!HasKey("ps_triangle", Key.J) && defaults.TrueForAll(key => HasKey("ps_triangle", (Key)key)), "Reset did not restore Triangle's keys");
        if (preferences != null) using (var file = Godot.FileAccess.Open(Wol.Engine.Display.DisplaySettings.Preferences, Godot.FileAccess.ModeFlags.Write)) file.StoreBuffer(preferences);
        long frozen = _title.Menu[TitleMenuState.Settings].Row;
        yield return 60;
        Expect(_title.SettingsOpen && _title.Screen == "menu", "the title did not wait while Settings was open");
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true });
        yield return 3;
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false });
        foreach (int f in Until(() => !_title.SettingsOpen, 10, "Settings to close")) yield return f;
        yield return 30;
        Expect(!_title.SettingsOpen && _title.SelectedItem == TitleMenuState.Settings && _title.Menu[TitleMenuState.Settings].Row == frozen,
            "closing Settings disturbed the title menu");
        foreach (int f in Press("ps_down")) yield return f;
        yield return 40;
        Expect(_title.SelectedItem == TitleMenuState.NewGame, "Down from Settings did not return to New Game");
        // Continue opens the LOAD screens: the empty card says so, and Cross leaves them for the title loop; idle then
        // loops back through the attract exit.
        foreach (int f in Press("ps_down")) yield return f;
        yield return 40;
        foreach (int f in Press("ps_circle")) yield return f;
        foreach (int f in Until(() => _title.CardStage == "Slots", 200, "Continue's LOAD screens")) yield return f;
        Expect(_title.Screen == "continue", "Circle on Continue did not leave for Continue");
        Capture("05b_load");
        foreach (int f in Press("ps_circle")) yield return f;
        foreach (int f in Until(() => _title.CardStage == "NoData", 200, "the empty card's message")) yield return f;
        Capture("05c_no_data");
        foreach (int f in Press("ps_circle")) yield return f;
        foreach (int f in Until(() => _title.CardStage == "Slots", 200, "the slots again")) yield return f;
        foreach (int f in Press("ps_cross")) yield return f;
        foreach (int f in Until(() => _title.Screen == "intro", 200, "the intro after Continue")) yield return f;
        if (_menuOnly) { Finish(""); yield break; }
        foreach (int f in Until(() => _title.Screen == "menu", TitleFlow.IntroPictureFrames + 10, "the unskipped intro to end")) yield return f;
        foreach (int f in Until(() => _title.Screen == "attract", TitleFlow.IntroMusicFrames + TitleFlow.IdleFrames + 200, "the idle timeout")) yield return f;
        foreach (int zone in new[] { 20, 2 })
        {
            foreach (int f in Until(() => _title.Demo != null, TitleFlow.LeaveFrames + 10, "the field demo")) yield return f;
            Expect(_title.Demo!.Zone == zone && _title.Demo.Map == (zone == 20 ? 0 : 1), "the idle demos did not alternate in retail order");
            Expect(_title.Demo.Flags[13] == 1 && _title.Demo.Flags[14] == 1, "the field demo flags were not set");
            GD.Print($"TITLE: retail demo {zone},{_title.Demo.Map} started");
            yield return 120;
            Capture($"demo_{zone:d2}");
            foreach (int f in Until(() => _title.Screen == "intro", 60 * 600, "the field demo to finish without input")) yield return f;
            Expect(_title.Demo == null, "the demo field survived its return to the title");
            GD.Print($"TITLE: retail demo {zone} completed without input");
            foreach (int f in Until(() => _title.Screen == "menu", TitleFlow.IntroPictureFrames + 10, "the intro after the demo")) yield return f;
            foreach (int f in Until(() => _title.Screen == "attract", TitleFlow.IntroMusicFrames + TitleFlow.IdleFrames + 200, "the next idle demo")) yield return f;
        }
        foreach (int f in Until(() => _title.Demo != null, TitleFlow.LeaveFrames + 10, "the interruptible demo")) yield return f;
        foreach (int f in Press("ps_l1")) yield return f;
        foreach (int f in Until(() => _title.Screen == "intro", 10, "a shoulder button to leave the demo")) yield return f;
        Expect(!_screens.GetRange(1, _screens.Count - 1).Contains("publisher"), "the logos played again after the first title visit");

        // New Game darkens the title and hands over to the opening field.
        foreach (int f in Press("ps_start")) yield return f;
        foreach (int f in Until(() => _title.Screen == "menu", 10, "the title screen")) yield return f;
        yield return 200;
        Expect(_title.SelectedItem == TitleMenuState.NewGame, "the title did not return to New Game");
        Capture("10_new_game");
        foreach (int f in Press("ps_circle")) yield return f;
        foreach (int f in Until(() => _title.Screen == "new_game", 10, "New Game")) yield return f;
        Expect(!_title.MusicPlaying, "New Game must stop the title music");
        foreach (int f in Until(() => _title.Screen == "field", TitleFlow.LeaveFrames + 10, "the opening field")) yield return f;
        var scenes = GetNode<SceneManager>("/root/SceneManager");
        Expect(scenes.IsTransitioning, "New Game did not start the scene change");
        // While the field loads, retail's own NOW LOADING picture is to show (the scene change then replaces this scene).
        Expect(scenes.HasLoadingImage, "the scene change has no loading picture");
        Finish("");
    }
}
