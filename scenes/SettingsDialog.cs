// SPDX-License-Identifier: AGPL-3.0-only
using Godot;
using Wol.Engine.Controls;
using Wol.Engine.Display;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Scenes;

/// <summary>
/// The settings window the title's Settings item and the field menu's Options open: the shared graphics panel, master
/// volume and the shared controls panel for the pad's buttons. Graphics and volume are saved when it closes, controls
/// as they change. Opened from the field it also holds retail's own OPTIONS settings the port keeps (MENU8.PRG): WEAPON
/// STATUS and ARMOR STATUS, named, explained and chosen ON or OFF in retail's words. Its Packs tab lists the installed
/// content packs: each is switched on or off (or removed), and a pack ZIP can be installed; the choice applies when the
/// window closes, from the title only (<see cref="PacksApplied"/>: the title starts again on the content as the packs leave it).
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
    /// <summary>Retail's OPTIONS settings, shown only when the window is opened from the field.</summary>
    private VBoxContainer _game = null!;
    private RetailContent _content = null!;
    private VBoxContainer _packList = null!;
    private Label _packStatus = null!;
    private Button _installPack = null!;
    private FileDialog _packFile = null!;
    /// <summary>The packs the player has switched on in this window, in priority order; null while unchanged.</summary>
    private System.Collections.Generic.List<string>? _enabling;
    private readonly System.Collections.Generic.List<string> _removing = new();
    private bool _fromField;

    /// <summary>
    /// The OPTIONS settings shown: the bit each sets in the options word ([0x80060020]), the string of its name (its
    /// help follows), and the string of its first choice, ON (its help, then OFF and OFF's help, follow). The choice
    /// sets the bit for ON and clears it for OFF (MENU8.PRG 0x80103b50, 0x80103b94: bit = (2 - choice) &amp; 1).
    /// </summary>
    private static readonly (uint Bit, int Name, int Choices)[] GameOptions = { (0x40, 4, 30), (0x80, 6, 34) };

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
        _game = new VBoxContainer { Name = "Game", Visible = false };
        _game.AddThemeConstantOverride("separation", 10);
        options.AddChild(_game);
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
        _content = GetNode<RetailContent>("/root/RetailContent");
        var packsScroll = new ScrollContainer { Name = "Packs", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _tabs.AddChild(packsScroll);
        var packs = new VBoxContainer { Name = "Packs", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        packs.AddThemeConstantOverride("separation", 10);
        packsScroll.AddChild(packs);
        packs.AddChild(new Label
        {
            Text = "Content packs (a translation, say) give game files the game reads in place of your disc's. Your installation is never changed: switch a pack off, or remove it, and the game reads your disc's files again.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        _packList = new VBoxContainer { Name = "List" };
        packs.AddChild(_packList);
        _installPack = new Button { Name = "Install", Text = "Install pack…", SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        packs.AddChild(_installPack);
        _packStatus = new Label { Name = "Status", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        packs.AddChild(_packStatus);
        _packFile = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, Filters = new[] { "*.zip ; Content pack" }, UseNativeDialog = true };
        AddChild(_packFile);
        _installPack.Pressed += () => _packFile.PopupCentered(new Vector2I(900, 600));
        _packFile.FileSelected += path =>
        {
            try { _packStatus.Text = "Installed pack: " + _content.InstallPack(path) + ". Switch it on to use it."; }
            catch (System.Exception error) when (error is System.IO.InvalidDataException or System.IO.IOException or System.UnauthorizedAccessException or System.Text.Json.JsonException)
            { _packStatus.Text = "The pack was not installed: " + error.Message; }
            ShowPacks();
        };
        VisibilityChanged += () =>
        {
            if (Visible) return;
            ApplyPacks();
            _display.SavePreferences();
            _audio.SavePreferences();
            // The graphics panel's texture choice sets every field material, characters' too.
            CharacterQuality.Refresh();
        };
    }

    /// <summary>
    /// Opens the window with the current values, the fullscreen choice focused; with <paramref name="field"/>, retail's
    /// OPTIONS settings too, on its options word.
    /// </summary>
    public void Open(Field.FieldView? field = null)
    {
        _fromField = field != null;
        _enabling = null;
        _removing.Clear();
        _packStatus.Text = "";
        ShowPacks();
        foreach (var child in _game.GetChildren()) { _game.RemoveChild(child); child.QueueFree(); }
        _game.Visible = field != null && field.Content.Has("options_menu");
        if (_game.Visible) ShowOptions(field!);
        _volume.SetValueNoSignal(_audio.Volume * 100);
        _characters.Selected = CharacterQuality.Enhanced ? 1 : 0;
        _lighting.Selected = FieldLighting.Enhanced ? 1 : 0;
        _graphics.Refresh();
        PopupCentered();
        _tabs.CurrentTab = 0;
        GetNode<Control>("Tabs/General/Options/Graphics/Fullscreen").GrabFocus();
    }

    /// <summary>The installed packs, each with its switch and Remove; disabled (shown only) when opened from the field.</summary>
    private void ShowPacks()
    {
        foreach (var child in _packList.GetChildren()) { _packList.RemoveChild(child); child.QueueFree(); }
        var enabled = _enabling ?? new System.Collections.Generic.List<string>(_content.Packs.Enabled);
        foreach (var pack in _content.Packs.Packs)
        {
            if (_removing.Contains(pack.Id)) continue;
            var row = new HBoxContainer { Name = pack.Id };
            var toggle = new CheckButton { Name = "Enabled", Text = $"{pack.Name} ({pack.Id})", ButtonPressed = enabled.Contains(pack.Id), Disabled = _fromField, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            string id = pack.Id;
            toggle.Toggled += on =>
            {
                _enabling ??= new System.Collections.Generic.List<string>(_content.Packs.Enabled);
                _enabling.Remove(id);
                if (on) _enabling.Add(id);
            };
            row.AddChild(toggle);
            var remove = new Button { Name = "Remove", Text = "Remove", Disabled = _fromField };
            remove.Pressed += () =>
            {
                _removing.Add(id);
                _enabling ??= new System.Collections.Generic.List<string>(_content.Packs.Enabled);
                _enabling.Remove(id);
                ShowPacks();
            };
            row.AddChild(remove);
            _packList.AddChild(row);
        }
        if (_content.Packs.Packs.Count == _removing.Count) _packList.AddChild(new Label { Text = "No content packs installed." });
        foreach (string error in _content.Packs.Errors) _packList.AddChild(new Label { Text = error, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        if (_fromField) _packList.AddChild(new Label { Text = "Packs are switched on and off from the title screen." });
        _installPack.Disabled = _fromField;
    }

    /// <summary>On closing: removes the packs marked for removal and enables the chosen set; the title starts again on
    /// the content as the packs now leave it.</summary>
    private void ApplyPacks()
    {
        if (_fromField || (_enabling == null && _removing.Count == 0)) return;
        foreach (string id in _removing) _content.RemovePack(id);
        if (_enabling != null && !System.Linq.Enumerable.SequenceEqual(_enabling, _content.Packs.Enabled)) _content.EnablePacks(_enabling);
        _enabling = null;
        _removing.Clear();
        PacksApplied?.Invoke();
    }

    /// <summary>Raised when the window closed with a changed set of packs, the content already reloaded through them.</summary>
    public event System.Action? PacksApplied;

    /// <summary>
    /// Retail's OPTIONS settings (MENU8.PRG): each its name and help, as the options list and its help line show them,
    /// and its two choices, ON and OFF, the one in force chosen (0x801005e0 marks it), with the chosen one's help under
    /// it as retail's help line gives it.
    /// </summary>
    private void ShowOptions(Field.FieldView field)
    {
        var strings = field.Content.Data("options_menu")["strings"].AsGodotArray();
        foreach (var (bit, name, choices) in GameOptions)
        {
            _game.AddChild(new Label { Text = Field.SystemBox.Entry(strings, name) });
            _game.AddChild(new Label { Text = Field.SystemBox.Entry(strings, name + 1), AutowrapMode = TextServer.AutowrapMode.WordSmart });
            var choice = new OptionButton { Name = Field.SystemBox.Entry(strings, name).Replace(' ', '_') };
            choice.AddItem(Field.SystemBox.Entry(strings, choices));
            choice.AddItem(Field.SystemBox.Entry(strings, choices + 2));
            var help = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            void Chosen(long index)
            {
                field.Options = index == 0 ? field.Options | bit : field.Options & ~bit;
                help.Text = Field.SystemBox.Entry(strings, choices + 1 + 2 * (int)index);
            }
            choice.Selected = (field.Options & bit) != 0 ? 0 : 1;
            Chosen(choice.Selected);
            choice.ItemSelected += Chosen;
            _game.AddChild(choice);
            _game.AddChild(help);
        }
    }
}
