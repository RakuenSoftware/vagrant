// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using Wol.Engine;
using Wol.Engine.Display;
using Wol.Games.Vagrant.Runtime;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Title;

/// <summary>
/// The title sequence. It owns which screen is current, and presents each with the retail timing:
/// publisher and developer logos, the opening movie, and the title screen with its menu.
/// Time is counted in 60 Hz display frames, as the retail title counts vertical blanks.
/// </summary>
public partial class TitleSequence : Control
{
    // Retail title timings, in display frames.
    public const int LogoFrames = 364, LogoFade = 32;
    public const int IntroPictureFrames = 0x8F5;
    public const int IntroMusicFrames = 4401;
    public const int IdleFrames = 1000, IdleAfterMusic = 100;
    public const int LeaveFrames = 64;

    [Flags]
    public enum Pad
    {
        None = 0, Circle = 1, Cross = 2, Start = 4, Select = 8, Up = 16, Down = 32, Left = 64, Right = 128,
        L1 = 256, R1 = 512, L2 = 1024, R2 = 2048,
    }

    private static readonly (string Action, Pad Button)[] Buttons =
    {
        ("ps_circle", Pad.Circle), ("ps_cross", Pad.Cross), ("ps_start", Pad.Start), ("ps_select", Pad.Select),
        ("ps_up", Pad.Up), ("ps_down", Pad.Down), ("ps_left", Pad.Left), ("ps_right", Pad.Right),
        ("ps_l1", Pad.L1), ("ps_r1", Pad.R1), ("ps_l2", Pad.L2), ("ps_r2", Pad.R2),
    };

    private RetailContent _content = null!;
    // TITLE.PRG 0x8007138c increments 0x8004a528 once per title visit. Its low bit selects the idle demo.
    private static int _visits;
    private Scenes.Field.FieldView? _demo;
    public Scenes.Field.FieldView? Demo => _demo;
    /// <summary>The title's screens, in the order the sequence first reaches them.</summary>
    private enum Stage { Publisher, Developer, Intro, Menu, NewGame, Field, Continue, Attract }
    private Stage _stage;
    private IEnumerator<int>? _presentation;
    private int _wait;
    private long _frame;
    private long _musicStart = -1;
    // _raw is sampled every display frame; _held and _pressed are what the title last read, as the retail pad handler reports.
    private Pad _raw, _accumulated, _held, _pressed;

    private SubViewport _bootScreen = null!, _titleScreen = null!;
    private TextureRect _screen = null!;
    private Sprite2D _logo = null!, _copyright = null!, _panel = null!;
    private VideoStreamPlayer _video = null!;
    private AudioStreamPlayer _music = null!;
    private SoundDriver _driver = null!;

    /// <summary>How many of the menu's sounds have played, for the checks.</summary>
    public int MenuSounds { get; private set; }

    /// <summary>
    /// One of TITLE.PRG's menu sounds, a resident one on voices 1 to 6 in turn, centred and at full volume: 4 as the
    /// wheel turns or an option changes, 5 for a choice, 6 for leaving, and 1, 2 and 3 together for New Game.
    /// </summary>
    private void Sound(int id)
    {
        MenuSounds++;
        _driver.PlayResident(id, 1, 0x80, 0x7F);
    }
    private ColorRect _darken = null!;
    private Label _notice = null!;
    private Scenes.Field.MemoryCardMenu _cards = null!;
    /// <summary>The LOAD screens' stage, empty when they are not up (<see cref="Scenes.Field.MemoryCardMenu.Showing"/>).</summary>
    public string CardStage => _cards.Showing;
    private TitleMenu _menu = null!;
    private AudioSettings _audio = null!;
    private DisplaySettings _display = null!;
    private SettingsDialog _settings = null!;

    /// <summary>The screen currently presented (publisher, developer, intro, menu, new_game, field, continue, attract).</summary>
    public string Screen => _stage switch
    {
        Stage.Publisher => "publisher", Stage.Developer => "developer", Stage.Intro => "intro", Stage.Menu => "menu",
        Stage.NewGame => "new_game", Stage.Field => "field", Stage.Continue => "continue", _ => "attract",
    };
    public bool MonoSound { get; private set; }
    public bool Vibration { get => VibrationDriver.Enabled; private set => VibrationDriver.Enabled = value; }
    public int SelectedItem { get; private set; }
    public bool SettingsOpen => _settings.Visible;
    public AcceptDialog SettingsDialog => _settings;
    public HSlider VolumeSlider => _settings.Volume;
    public bool MusicPlaying => _musicStart >= 0;
    public bool NoticeVisible => _notice.Visible;
    public string Failure { get; private set; } = "";
    public TitleMenu Menu => _menu;
    public long Frame => _frame;

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
        _menu.Bind(_content);
        // The retail exit darkens the frozen frame by subtracting a constant grey every frame.
        _darken = new ColorRect { Size = new Vector2(512, 448), Color = new Color(0, 0, 0), Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Sub } };
        _titleScreen.AddChild(_darken);

        _music = new AudioStreamPlayer { Stream = _content.IntroMusic };
        AddChild(_music);
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
        // Continue's LOAD screens, over everything.
        var cardsLayer = new CanvasLayer { Layer = 20, Name = "CardsLayer" };
        AddChild(cardsLayer);
        _cards = new Scenes.Field.MemoryCardMenu { Name = "MemoryCard" };
        cardsLayer.AddChild(_cards);
        _cards.Configure(_content, null);

        MonoSound = _audio.Mono;
        Enter(++_visits == 1 ? Stage.Publisher : Stage.Intro);
    }

    public override void _ExitTree()
    {
        _demo?.QueueFree();
        _music.Stop();
        _music.Stream = null;
        _video.Stop();
        _video.Stream = null;
    }

    private void Fail(string detail)
    {
        Failure = detail;
        GD.PushError(detail);
        _presentation = null;
        _notice.Text = "This development build cannot continue.\n\n" + detail;
        _notice.Show();
    }

    /// <summary>Moves to a screen and starts presenting it.</summary>
    private void Enter(Stage stage)
    {
        _stage = stage;
        _presentation = stage switch
        {
            Stage.Publisher => Logo(_content.Publisher, _content.PublisherOrigin, false),
            Stage.Developer => Logo(_content.Developer, _content.DeveloperOrigin, true),
            Stage.Intro => Intro(),
            Stage.Menu => TitleScreen(),
            Stage.NewGame => Leave("", false),
            Stage.Field => StartField(),
            Stage.Continue => LoadScreens(),
            _ => Leave("", false),
        };
        _wait = 0;
    }

    /// <summary>The current screen has finished: the logos lead to the movie, the movie to the title screen, and leaving it back to the movie (or on to the field for New Game).</summary>
    private void Finished()
    {
        Enter(_stage switch
        {
            Stage.Publisher => Stage.Developer,
            Stage.Developer => Stage.Intro,
            Stage.Intro => Stage.Menu,
            Stage.NewGame => Stage.Field,
            _ => Stage.Intro,
        });
    }

    public override void _PhysicsProcess(double delta)
    {
        _frame++;
        Pad raw = Pad.None;
        foreach (var (action, button) in Buttons)
            if (Input.IsActionPressed(action)) raw |= button;
        _accumulated |= raw & ~_raw;
        _raw = raw;
        if (_musicStart >= 0 && _frame - _musicStart >= IntroMusicFrames) StopMusic();
        if (_presentation == null) return;
        if (_wait > 0 && --_wait > 0) return;
        while (_presentation != null)
        {
            var current = _presentation;
            bool more = current.MoveNext();
            if (_presentation != current) continue;
            if (!more)
            {
                _presentation = null;
                break;
            }
            _wait = current.Current;
            if (_wait > 0) break;
        }
    }

    /// <summary>Reads the pad as the retail pad handler does: held buttons, and buttons pressed since the last read.</summary>
    private void Poll()
    {
        _held = _raw;
        _pressed = _accumulated;
        _accumulated = Pad.None;
    }

    private Pad TakePressed()
    {
        Pad pressed = _pressed;
        _pressed = Pad.None;
        return pressed;
    }

    private void Present(SubViewport screen) => _screen.Texture = screen.GetTexture();

    private void StopMusic()
    {
        _music.Stop();
        _musicStart = -1;
    }

    private IEnumerator<int> Logo(Texture2D texture, Vector2I origin, bool skippable)
    {
        Present(_bootScreen);
        _video.Hide();
        _logo.Texture = texture;
        _logo.Position = origin;
        _logo.Show();
        for (int i = 0; i < LogoFrames; i++)
        {
            int fade = i < LogoFade ? (LogoFade - 1 - i) * 4 : i > LogoFrames - LogoFade - 1 ? (i - (LogoFrames - LogoFade - 1)) * 4 : 0;
            if (skippable && i >= LogoFade && i <= LogoFrames - LogoFade - 1 && _held != Pad.None) i = LogoFrames - LogoFade - 1;
            float level = (128 - fade) / 128f;
            _logo.Modulate = new Color(level, level, level);
            yield return 1;
            Poll();
            TakePressed();
        }
        _logo.Hide();
        Finished();
    }

    private IEnumerator<int> Intro()
    {
        Present(_bootScreen);
        _logo.Hide();
        _video.Stream = _content.IntroVideo;
        _video.Show();
        _video.Play();
        _music.Play();
        _musicStart = _frame;
        TakePressed();
        while (true)
        {
            yield return 1;
            Poll();
            if (_frame - _musicStart >= IntroPictureFrames) break;
            if ((TakePressed() & (Pad.Circle | Pad.Start)) != 0) break;
        }
        // The picture ends here; the music continues under the title screen.
        _video.Stop();
        _video.Hide();
        Finished();
    }

    /// <summary>One menu update and the frame it is displayed on.</summary>
    private IEnumerable<int> DrawMenu()
    {
        _menu.Step();
        yield return 1;
    }

    private IEnumerable<int> DrawAndSync()
    {
        foreach (int frame in DrawMenu()) yield return frame;
        yield return 1;
        Poll();
    }

    private IEnumerator<int> TitleScreen()
    {
        _menu.Reset();
        _darken.Color = new Color(0, 0, 0);
        _copyright.Modulate = new Color(1, 1, 1, 0);
        _panel.Modulate = new Color(1, 1, 1, 0);
        Present(_titleScreen);
        // The cursor starts on Continue when a card holds a save, as retail's does, and on New Game otherwise.
        int[] wheel = TitleMenu.Wheel;
        int count = wheel.Length, position = Scenes.Field.FieldView.ReadSave() != null ? Array.IndexOf(wheel, TitleMenu.Continue) : 0;
        SelectedItem = wheel[position];
        _menu.Show(wheel[position], 64);
        _menu.Show(wheel[(position + 1) % count], 96);
        _menu.Show(wheel[(position + count - 1) % count], 32);
        for (int fade = 32; fade >= 0; fade -= 2)
        {
            yield return 2;
            Poll();
            _copyright.Modulate = new Color(1, 1, 1, (32 - fade) / 32f);
        }
        yield return 1;
        yield return 1;
        for (int fade = 32; fade >= 0; fade -= 4)
        {
            _panel.Modulate = new Color(1, 1, 1, (32 - fade) / 32f);
            yield return 4;
            Poll();
            foreach (int frame in DrawMenu()) yield return frame;
        }
        for (int i = 0; i < 8; i++)
            foreach (int frame in DrawAndSync()) yield return frame;

        TakePressed();
        long lastMusic = _frame, lastInput = _frame;
        string choice;
        while (true)
        {
            yield return 1;
            Poll();
            if (MusicPlaying) lastMusic = _frame;
            if (_held != Pad.None) lastInput = _frame;
            if (_frame - lastInput > IdleFrames && _frame - lastMusic > IdleAfterMusic)
            {
                choice = "timeout";
                break;
            }
            if ((_held & (Pad.Circle | Pad.Start)) != 0)
            {
                if (SelectedItem == TitleMenu.NewGame) { Sound(1); Sound(2); Sound(3); choice = "new_game"; break; }
                if (SelectedItem == TitleMenu.Continue) { Sound(5); choice = "continue"; break; }
                Sound(5);
                int above = wheel[(position + count - 1) % count], below = wheel[(position + 1) % count];
                var submenu = SelectedItem switch
                {
                    TitleMenu.Vibration => Submenu(TitleMenu.Vibration, above, below, TitleMenu.VibrationOn, TitleMenu.VibrationOff,
                        Vibration ? TitleMenu.VibrationOn : TitleMenu.VibrationOff,
                        option => Vibration = option == TitleMenu.VibrationOn),
                    TitleMenu.Sound => Submenu(TitleMenu.Sound, above, below, TitleMenu.Stereo, TitleMenu.Mono, MonoSound ? TitleMenu.Mono : TitleMenu.Stereo,
                        option => _audio.Mono = MonoSound = option == TitleMenu.Mono),
                    _ => Settings(),
                };
                foreach (int frame in submenu) yield return frame;
                lastInput = _frame;
            }
            int direction = 0;
            if ((_held & (Pad.Up | Pad.Left)) != 0) direction--;
            if ((_held & (Pad.Select | Pad.Right | Pad.Down)) != 0) direction++;
            if (direction != 0)
            {
                Sound(4);
                position = (position + direction + count) % count;
                SelectedItem = wheel[position];
                // Words resting off the drum (rows 0 and 128) are invisible; with a fifth word on the wheel they must
                // not keep travelling, so they are dropped before the incoming word appears.
                foreach (int word in wheel)
                    if (_menu[word].Enabled && (_menu[word].Row == 0 || _menu[word].Row == 128)) _menu[word].Enabled = false;
                _menu.Show(wheel[(position + direction + count) % count], direction > 0 ? 128 : 0);
                foreach (int word in wheel)
                {
                    _menu[word].State = direction > 0 ? TitleMenu.Motion.Rising : TitleMenu.Motion.Falling;
                    _menu[word].Target = (_menu[word].Row - 32 * direction) & 0xFF;
                }
                for (int i = 0; i < 10; i++)
                    foreach (int frame in DrawAndSync()) yield return frame;
            }
            foreach (int frame in DrawMenu()) yield return frame;
        }
        if (choice != "timeout" && MusicPlaying) StopMusic();
        Enter(choice switch { "new_game" => Stage.NewGame, "continue" => Stage.Continue, _ => Stage.Attract });
    }

    /// <summary>The Vibration and Sound option pickers: the heading rises and its two options appear below it.</summary>
    private IEnumerable<int> Submenu(int heading, int above, int below, int upper, int lower, int current, Action<int> confirm)
    {
        _menu[above].State = TitleMenu.Motion.Rising;
        _menu[above].Target = 0;
        _menu[below].State = TitleMenu.Motion.Falling;
        _menu[below].Target = 128;
        _menu[heading].State = TitleMenu.Motion.Opened;
        _menu.Show(upper, 64);
        _menu.Show(lower, 96);
        _menu[upper].State = TitleMenu.Motion.Opened;
        _menu[lower].State = TitleMenu.Motion.Opened;
        _menu.Recolour(lower, 0, TitleMenu.Dark, TitleMenu.Dark);
        _menu.Recolour(upper, 0, TitleMenu.Dark, TitleMenu.Dark);
        for (int i = 1; i < 9; i++)
        {
            _menu.Recolour(heading, i * 2, TitleMenu.Lit, TitleMenu.Gold);
            _menu[heading].Row = (_menu[heading].Row - 4) & 0xFF;
            foreach (int frame in DrawAndSync()) yield return frame;
        }
        foreach (int word in TitleMenu.Wheel)
            if (word != heading) _menu[word].State = TitleMenu.Motion.Resting;
        for (int i = 1; i < 9; i++)
            foreach (int frame in DrawAndSync()) yield return frame;
        int option = current;
        for (int i = 0; i < 8; i++)
        {
            _menu.Recolour(option, i * 2, TitleMenu.Dark, TitleMenu.Lit);
            _menu[option].Glow = i < 4 ? i * 64 : (_menu[option].Glow - 16) & 0xFF;
            foreach (int frame in DrawAndSync()) yield return frame;
        }
        TakePressed();
        while (true)
        {
            foreach (int frame in DrawAndSync()) yield return frame;
            Pad pressed = TakePressed();
            if ((_held & (Pad.Start | Pad.Circle)) != 0)
            {
                Sound(5);
                confirm(option);
                if (option == TitleMenu.VibrationOn)
                    foreach (int device in Input.GetConnectedJoypads()) Input.StartJoyVibration(device, 0.5f, 1f, 0.25f);
                break;
            }
            if ((_held & Pad.Cross) != 0) { Sound(6); break; }
            if ((pressed & (Pad.Up | Pad.Down | Pad.Select)) != 0)
            {
                Sound(4);
                option = 11 - option;
                for (int i = 1; i < 8; i++)
                {
                    _menu.Recolour(option, i * 2, TitleMenu.Dark, TitleMenu.Lit);
                    _menu.Recolour(11 - option, i * 2, TitleMenu.Lit, TitleMenu.Dark);
                    _menu[option].Glow = i < 4 ? i * 64 : (_menu[option].Glow - 16) & 0xFF;
                    _menu[11 - option].Glow = (_menu[11 - option].Glow - 16) & 0xFF;
                    if (i == 7) break;
                    foreach (int frame in DrawAndSync()) yield return frame;
                }
            }
        }
        _menu.Show(above, 0);
        _menu.Show(below, 128);
        _menu[above].State = TitleMenu.Motion.Falling;
        _menu[above].Target = 32;
        _menu[below].State = TitleMenu.Motion.Rising;
        _menu[below].Target = 96;
        for (int i = 1; i < 9; i++)
        {
            _menu.Recolour(heading, i * 2, TitleMenu.Gold, TitleMenu.Lit);
            _menu[heading].Row = (_menu[heading].Row + 4) & 0xFF;
            foreach (int choice in new[] { lower, upper })
            {
                _menu[choice].Brightness = Math.Max(0, _menu[choice].Brightness - 24);
                if (_menu[choice].Glow != 0) _menu[choice].Glow = (_menu[choice].Glow - 16) & 0xFF;
            }
            foreach (int frame in DrawAndSync()) yield return frame;
        }
        _menu[lower].Enabled = false;
        _menu[upper].Enabled = false;
        _menu[heading].GlowRow = 64;
        _menu[heading].Glow = 64;
        foreach (int frame in DrawAndSync()) yield return frame;
        _menu[heading].Glow = 128;
        foreach (int frame in DrawAndSync()) yield return frame;
        _menu[heading].Glow = 192;
        foreach (int frame in DrawMenu()) yield return frame;
        _menu[heading].Warmth = 16;
        _menu[heading].State = TitleMenu.Motion.Resting;
    }

    /// <summary>The added Settings item: the shared graphics panel and master volume. The title waits while it is open.</summary>
    private IEnumerable<int> Settings()
    {
        _settings.Open();
        while (_settings.Visible) yield return 1;
        // The button that closed the dialog must not reach the title menu as a fresh confirmation.
        while (_raw != Pad.None) yield return 1;
        Poll();
        TakePressed();
    }

    /// <summary>The scene New Game opens: zone 1, map 0.</summary>
    public const string FieldScene = "res://scenes/field/field.tscn";

    /// <summary>Hands over to the opening field once the title has darkened.</summary>
    /// <summary>Continue: the field resumes the saved game.</summary>
    /// <summary>
    /// Continue: the title darkens, then the LOAD screens (TITLE.PRG's file menu on the menus' crystal). A loaded save
    /// resumes in its room; leaving them returns to the title loop.
    /// </summary>
    private IEnumerator<int> LoadScreens()
    {
        for (int i = 1; i <= LeaveFrames; i++)
        {
            yield return 1;
            Poll();
            float level = Math.Min(255, i * 8) / 255f;
            _darken.Color = new Color(level, level, level);
        }
        _cards.OpenLoad(fadeOut: true);
        TakePressed();
        int result = 0;
        while (result == 0)
        {
            yield return 1;
            Poll();
            var pressed = TakePressed();
            result = _cards.Step(action => Array.Find(Buttons, b => b.Action == action) is var (_, button) && button != Pad.None && (pressed & button) != 0);
        }
        if (result > 0 && _cards.Loaded is { } save)
        {
            var resume = Continue(save);
            while (resume.MoveNext()) yield return resume.Current;
            yield break;
        }
        Finished();
    }

    private IEnumerator<int> Continue(Godot.Collections.Dictionary save)
    {
        Scenes.Field.FieldView.PendingSave = save;
        // NOW LOADING shows only while the room has still to be converted.
        var content = GetNode<RetailContent>("/root/RetailContent");
        _ = GetNode<SceneManager>("/root/SceneManager").ChangeScene(FieldScene, showLoading: !content.Converted(save["zone"].AsInt32(), save["map"].AsInt32()));
        yield break;
    }

    private IEnumerator<int> StartField()
    {
        // NOW LOADING shows only while the opening room has still to be converted.
        var content = GetNode<RetailContent>("/root/RetailContent");
        _ = GetNode<SceneManager>("/root/SceneManager").ChangeScene(FieldScene, showLoading: !content.Converted(1, 0));
        yield break;
    }

    /// <summary>The retail exit: the frozen title darkens over 64 frames before leaving the title.</summary>
    private IEnumerator<int> Leave(string unavailable, bool needsConfirmation)
    {
        for (int i = 1; i <= LeaveFrames; i++)
        {
            yield return 1;
            Poll();
            float level = Math.Min(255, i * 8) / 255f;
            _darken.Color = new Color(level, level, level);
        }
        if (_stage == Stage.Attract)
        {
            // 0x80071748 selects 4 - (visits & 1); 0x80071ce0 starts those demos at zone 20/map 0 and zone 2/map 1,
            // with the demo and automatic-scene flags (13 and 14) set. The room and its chained events own the show.
            _demo = GD.Load<PackedScene>(FieldScene).Instantiate<Scenes.Field.FieldView>();
            _demo.StartZone = (_visits & 1) != 0 ? 20 : 2;
            _demo.StartMap = (_visits & 1) != 0 ? 0 : 1;
            _demo.Flags[13] = _demo.Flags[14] = 1;
            _demo.KeepScene = true;
            Hide();
            GetParent().AddChild(_demo);
            TakePressed();
            while (true)
            {
                yield return 1;
                Poll();
                if (_demo.Failure.Length != 0) { string failure = _demo.Failure; _demo.QueueFree(); _demo = null; Show(); Fail(failure); yield break; }
                if (TakePressed() != Pad.None || _demo.Settled || _demo.GameEnded) break;
            }
            _demo.GetParent().RemoveChild(_demo);
            _demo.QueueFree();
            _demo = null;
            _visits++;
            Show();
        }
        if (needsConfirmation)
        {
            // These destinations are outside this build; say so plainly and return to the title loop.
            _notice.Text = unavailable + "\n\nPress Circle or Start to return to the title.";
            _notice.Show();
            TakePressed();
            while ((TakePressed() & (Pad.Circle | Pad.Start | Pad.Cross)) == 0)
            {
                yield return 1;
                Poll();
            }
            _notice.Hide();
        }
        Finished();
    }
}
