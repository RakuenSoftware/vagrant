// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Godot;
using Wol.Games.Vagrant.Title;
using Wol.Engine;
using Wol.Engine.Display;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Scenes.Title;

/// <summary>
/// Godot resources and scene bindings for the portable TitleFlow. The game owns screen selection,
/// timing and menu state; this host samples input and applies that state to Godot resources.
/// </summary>
public partial class TitleSequence : Control, ITitleHost
{
    private static readonly (string Action, TitlePad Button)[] Buttons =
    {
        ("ps_circle", TitlePad.Circle), ("ps_cross", TitlePad.Cross), ("ps_start", TitlePad.Start), ("ps_select", TitlePad.Select),
        ("ps_up", TitlePad.Up), ("ps_down", TitlePad.Down), ("ps_left", TitlePad.Left), ("ps_right", TitlePad.Right),
        ("ps_l1", TitlePad.L1), ("ps_r1", TitlePad.R1), ("ps_l2", TitlePad.L2), ("ps_r2", TitlePad.R2),
    };

    private RetailContent _content = null!;
    private Scenes.Field.FieldView? _demo;
    public Scenes.Field.FieldView? Demo => _demo;
    private TitleFlow _flow = null!;
    private SubViewport _bootScreen = null!, _titleScreen = null!;
    private TextureRect _screen = null!;
    private Sprite2D _logo = null!, _copyright = null!, _panel = null!;
    private VideoStreamPlayer _video = null!;
    private SoundDriver _driver = null!;
    private ColorRect _darken = null!;
    private Label _notice = null!;
    private Scenes.Field.MemoryCardMenu _cards = null!;
    private TitleMenu _menu = null!;
    private AudioSettings _audio = null!;
    private DisplaySettings _display = null!;
    private SettingsDialog _settings = null!;
    public string CardStage => _cards.Showing;
    public string Screen => _flow.Stage switch
    {
        TitleStage.Publisher => "publisher", TitleStage.Developer => "developer", TitleStage.Intro => "intro", TitleStage.Menu => "menu",
        TitleStage.ClearSave => "clear_save", TitleStage.NewGame => "new_game", TitleStage.Field => "field", TitleStage.Continue => "continue", _ => "attract",
    };
    public int MenuSounds => _flow.MenuSounds;
    public bool MonoSound => _audio.Mono;
    public bool Vibration => VibrationDriver.Enabled;
    public int SelectedItem => _flow.SelectedItem;
    public bool SettingsOpen => _settings.Visible;
    public AcceptDialog SettingsDialog => _settings;
    public HSlider VolumeSlider => _settings.Volume;
    public bool MusicPlaying => _flow.MusicPlaying;
    public double MusicPosition => _video.StreamPosition;
    public bool NoticeVisible => _notice.Visible;
    public string Failure => _flow.Failure;
    public TitleMenuState Menu => _flow.Menu;
    public long Frame => _flow.Frame;
    public const string FieldScene = "res://scenes/field/field.tscn";

    public override void _Ready()
    {
        _content = GetNode<RetailContent>("/root/RetailContent");
        _audio = GetNode<AudioSettings>("/root/AudioSettings");
        _display = GetNode<DisplaySettings>("/root/DisplaySettings");
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1 });
        // Retail's screen pixels show narrower than square (FieldView.PixelAspect), the logos and the movie with them.
        var frame = new AspectRatioContainer { Ratio = 4f / 3f * Scenes.Field.FieldView.PixelAspect, StretchMode = AspectRatioContainer.StretchModeEnum.Fit, AnchorRight = 1, AnchorBottom = 1 };
        AddChild(frame);
        _screen = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, TextureFilter = TextureFilterEnum.Nearest };
        frame.AddChild(_screen);
        // The upscaled title follows the shared "Interface and 2D smoothing" preference.
        _screen.AddToGroup("wol_ui");
        _screen.TextureFilter = _display.SmoothUi ? TextureFilterEnum.Linear : TextureFilterEnum.Nearest;

        _bootScreen = new SubViewport { Size = new Vector2I(320, 224), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Disable3D = true };
        AddChild(_bootScreen);
        _bootScreen.AddChild(new ColorRect { Color = Colors.Black, Size = new Vector2(320, 224) });
        _logo = new Sprite2D { Centered = false, Visible = false };
        _bootScreen.AddChild(_logo);
        _video = new VideoStreamPlayer { Size = new Vector2(320, 224), Expand = true, Visible = false };
        _bootScreen.AddChild(_video);

        _titleScreen = new SubViewport { Size = new Vector2I(512, 448), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Disable3D = true };
        AddChild(_titleScreen);
        _titleScreen.AddChild(new Sprite2D { Centered = false, Texture = _content.TitleBackground });
        _copyright = new Sprite2D { Centered = false, Texture = _content.TitleCopyright, Position = _content.CopyrightOrigin - new Vector2I(0, TitleMenu.DisplayTop) };
        _titleScreen.AddChild(_copyright);
        _panel = new Sprite2D { Centered = false, Texture = _content.TitlePanel, Position = _content.PanelOrigin - new Vector2I(0, TitleMenu.DisplayTop) };
        _titleScreen.AddChild(_panel);
        _menu = new TitleMenu { Name = "Menu" };
        _titleScreen.AddChild(_menu);
        // The retail exit darkens the frozen frame by subtracting a constant grey every frame.
        _darken = new ColorRect { Size = new Vector2(512, 448), Color = new Color(0, 0, 0), Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Sub } };
        _titleScreen.AddChild(_darken);

        // The menu's sounds are the resident ones, through the game's own driver as in the field.
        _driver = new SoundDriver { Name = "SoundDriver" };
        AddChild(_driver);
        try { _content.Boot(_driver); }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException) { GD.PushWarning($"The title's sounds are off: {error.Message}"); }
        _notice = new Label
        {
            Visible = false, AnchorRight = 1, AnchorBottom = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _notice.AddThemeFontSizeOverride("font_size", 28);
        AddChild(_notice);
        _settings = new SettingsDialog { Name = "Settings" };
        AddChild(_settings);
        // Switching packs reloads the content; the title starts again on it.
        _settings.PacksApplied += () => GetTree().ReloadCurrentScene();
        // Continue's LOAD screens, over everything.
        var cardsLayer = new CanvasLayer { Layer = 20, Name = "CardsLayer" };
        AddChild(cardsLayer);
        _cards = new Scenes.Field.MemoryCardMenu { Name = "MemoryCard" };
        cardsLayer.AddChild(_cards);
        _cards.Configure(_content, null);

        bool clearSave = Scenes.Field.FieldView.PendingClearSave != null;
        if (Scenes.Field.FieldView.PendingClearSave is { } cleared)
        {
            Scenes.Field.FieldView.PendingClearSave = null;
            _cards.SaveData = cleared;
        }
        _flow = new TitleFlow(this, _content.TitlePalettes, clearSave);
        _cards.Sound = _flow.PlaySound;
        _menu.Bind(_content, _flow.Menu);
    }

    public override void _ExitTree()
    {
        _demo?.QueueFree();
        _video.Stop();
        _video.Stream = null;
    }

    public override void _PhysicsProcess(double delta)
    {
        TitlePad raw = TitlePad.None;
        foreach (var (action, button) in Buttons)
            if (Input.IsActionPressed(action)) raw |= button;
        _flow.Tick(raw);
        _screen.Texture = (_flow.Canvas == TitleCanvas.Boot ? _bootScreen : _titleScreen).GetTexture();
        _logo.Visible = _flow.Logo != TitleLogo.None;
        if (_flow.Logo != TitleLogo.None)
        {
            bool publisher = _flow.Logo == TitleLogo.Publisher;
            _logo.Texture = publisher ? _content.Publisher : _content.Developer;
            _logo.Position = publisher ? _content.PublisherOrigin : _content.DeveloperOrigin;
        }
        float level = _flow.LogoLevel;
        _logo.Modulate = new Color(level, level, level);
        _video.Visible = _flow.VideoVisible;
        _copyright.Modulate = new Color(1, 1, 1, _flow.CopyrightAlpha);
        _panel.Modulate = new Color(1, 1, 1, _flow.PanelAlpha);
        _darken.Color = new Color(_flow.Darkening, _flow.Darkening, _flow.Darkening);
        Visible = _flow.Visible;
        _menu.Redraw();
        if (_flow.Failure.Length != 0 && !_notice.Visible)
        {
            GD.PushError(_flow.Failure);
            _notice.Text = "This development build cannot continue.\n\n" + _flow.Failure;
            _notice.Show();
        }
    }

    bool ITitleHost.HasSave => Scenes.Field.FieldView.ReadSave() != null;
    bool ITitleHost.MonoSound { get => _audio.Mono; set => _audio.Mono = value; }
    bool ITitleHost.Vibration { get => VibrationDriver.Enabled; set => VibrationDriver.Enabled = value; }
    bool ITitleHost.MusicPlaying => _video.IsPlaying();
    bool ITitleHost.SettingsOpen => _settings.Visible;
    void ITitleHost.OpenSettings() => _settings.Open();
    void ITitleHost.PlayIntro() { _video.Stream = _content.IntroVideo; _video.Play(); }
    void ITitleHost.StopMusic() => _video.Stop();
    void ITitleHost.PlaySound(int id, int voice, int pan, int volume) => _driver.PlayResident(id, voice, pan, volume);
    void ITitleHost.PreviewVibration(float weak, float strong, float seconds)
    {
        foreach (int device in Input.GetConnectedJoypads()) Input.StartJoyVibration(device, weak, strong, seconds);
    }
    void ITitleHost.OpenLoad() => _cards.OpenLoad(fadeOut: true);
    void ITitleHost.OpenClearSave(int location) => _cards.OpenSave(location);
    TitleCardResult ITitleHost.StepCards(TitlePad pressed)
    {
        int result = _cards.Step(action => Array.Find(Buttons, b => b.Action == action) is var (_, button)
            && button != TitlePad.None && (pressed & button) != 0);
        return result == 0 ? TitleCardResult.Pending : result < 0 ? TitleCardResult.Cancelled
            : _cards.Loaded != null ? TitleCardResult.Loaded : TitleCardResult.Saved;
    }
    void ITitleHost.CloseClearSave() => _cards.SaveData = null;
    void ITitleHost.ResumeGame()
    {
        var save = _cards.Loaded ?? throw new InvalidOperationException("The card UI reported a load without a saved game.");
        Scenes.Field.FieldView.PendingSave = save;
        _ = GetNode<SceneManager>("/root/SceneManager").ChangeScene(FieldScene, showLoading: !_content.Converted(save["zone"].AsInt32(), save["map"].AsInt32()));
    }
    void ITitleHost.StartNewGame() => _ = GetNode<SceneManager>("/root/SceneManager").ChangeScene(FieldScene, showLoading: !_content.Converted(1, 0));
    void ITitleHost.StartDemo(int zone, int map)
    {
        _demo = GD.Load<PackedScene>(FieldScene).Instantiate<Scenes.Field.FieldView>();
        _demo.StartZone = zone;
        _demo.StartMap = map;
        _demo.Flags[13] = _demo.Flags[14] = 1;
        _demo.KeepScene = true;
        GetParent().AddChild(_demo);
    }
    bool ITitleHost.DemoFinished => _demo != null && (_demo.Settled || _demo.GameEnded);
    string ITitleHost.DemoFailure => _demo?.Failure ?? "";
    void ITitleHost.EndDemo()
    {
        if (_demo == null) return;
        _demo.GetParent().RemoveChild(_demo);
        _demo.QueueFree();
        _demo = null;
    }
}
