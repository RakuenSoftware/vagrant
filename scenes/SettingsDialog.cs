// SPDX-License-Identifier: AGPL-3.0-only
using Godot;
using Wol.Engine.Controls;
using Wol.Engine.Display;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Scenes;

/// <summary>
/// The settings window the title's Settings item and the field menu's Options open: the shared graphics panel, master
/// volume and the shared controls panel for the pad's buttons. Graphics and volume are saved when it closes, controls
/// as they change.
/// </summary>
public sealed partial class SettingsDialog : AcceptDialog
{
    private AudioSettings _audio = null!;
    private DisplaySettings _display = null!;
    private HSlider _volume = null!;
    private OptionButton _characters = null!, _lighting = null!;

    /// <summary>The master volume slider.</summary>
    public HSlider Volume => _volume;
    private GraphicsSettings _graphics = null!;
    private TabContainer _tabs = null!;

    public override void _Ready()
    {
        _audio = GetNode<AudioSettings>("/root/AudioSettings");
        _display = GetNode<DisplaySettings>("/root/DisplaySettings");
        Title = "Settings";
        OkButtonText = "Close";
        Exclusive = true;
        // Two tabs: General (graphics and sound) and Controls.
        _tabs = new TabContainer { Name = "Tabs", CustomMinimumSize = new Vector2(780, 560) };
        AddChild(_tabs);
        var scroll = new ScrollContainer { Name = "General", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _tabs.AddChild(scroll);
        var options = new VBoxContainer { Name = "Options", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        options.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(options);
        options.AddChild(new Label { Text = "Display and graphics" });
        // The shared engine panel; Vagrant Story's title and fields run at 60 frames per second.
        _graphics = GD.Load<PackedScene>("res://addons/wol_engine/runtime/display/graphics_settings.tscn").Instantiate<GraphicsSettings>();
        _graphics.Name = "Graphics";
        _graphics.OriginalFrameRate = 60;
        options.AddChild(_graphics);
        options.AddChild(new Label
        {
            Text = "Fullscreen uses the display's native resolution. The fields widen to fill the window; movies and pictures keep their 4:3.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        options.AddChild(new Label { Text = "Characters" });
        _characters = new OptionButton { Name = "Characters" };
        _characters.AddItem("Original");
        _characters.AddItem("Enhanced");
        _characters.ItemSelected += value => CharacterQuality.Choose(value == 1);
        options.AddChild(_characters);
        options.AddChild(new Label
        {
            Text = "Enhanced scales the characters' textures up with a pixel-art scaler and draws them smooth, rounds their heads and shades them softly: faces, hair and armour read as lines rather than blocks. Original shows them as on the PlayStation. A render scale above 100% sharpens their outlines further.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        options.AddChild(new Label { Text = "Lighting" });
        _lighting = new OptionButton { Name = "Lighting" };
        _lighting.AddItem("Original");
        _lighting.AddItem("Enhanced");
        _lighting.ItemSelected += value => FieldLighting.Choose(value == 1);
        options.AddChild(_lighting);
        options.AddChild(new Label
        {
            Text = "Enhanced makes bright things bloom (torches, burning windows, spells), draws fire and magic brighter (brighter still on an HDR display) and lets them light the characters near them when characters are Enhanced. Original lights the fields as on the PlayStation.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        options.AddChild(new Label { Text = "Master volume" });
        _volume = new HSlider { MinValue = 0, MaxValue = 100, Step = 1, Value = _audio.Volume * 100, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _volume.ValueChanged += value => _audio.Volume = (float)value / 100f;
        options.AddChild(_volume);
        options.AddChild(new Label
        {
            Text = "Sound (Stereo/Mono) and Vibration are on the title menu, as on the original.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        var controlsScroll = new ScrollContainer { Name = "Controls", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _tabs.AddChild(controlsScroll);
        controlsScroll.AddChild(new ControlsSettings
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Name = "Controls",
            Actions = new[] { "ps_up", "ps_down", "ps_left", "ps_right", "ps_circle", "ps_cross", "ps_triangle", "ps_square", "ps_l1", "ps_r1", "ps_l2", "ps_r2", "ps_r3", "ps_start", "ps_select" },
            Labels = new[] { "Up", "Down", "Left", "Right", "○ Circle", "× Cross", "△ Triangle", "□ Square", "L1", "R1", "L2", "R2", "R3", "Start", "Select" },
        });
        VisibilityChanged += () =>
        {
            if (Visible) return;
            _display.SavePreferences();
            _audio.SavePreferences();
            // The graphics panel's texture choice sets every field material, characters' too.
            CharacterQuality.Refresh();
        };
    }

    /// <summary>Opens the window with the current values, the fullscreen choice focused.</summary>
    public void Open()
    {
        _volume.SetValueNoSignal(_audio.Volume * 100);
        _characters.Selected = CharacterQuality.Enhanced ? 1 : 0;
        _lighting.Selected = FieldLighting.Enhanced ? 1 : 0;
        _graphics.Refresh();
        PopupCentered();
        _tabs.CurrentTab = 0;
        GetNode<Control>("Tabs/General/Options/Graphics/Fullscreen").GrabFocus();
    }
}
