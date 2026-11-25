// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;

namespace Wol.Games.Vagrant.Title;

/// <summary>
/// Vagrant's title rules. Tick samples one 60 Hz display frame, preserving retail pad accumulation,
/// 30 Hz menu updates, waits, transitions and timing. The host renders this same mutable state.
/// </summary>
public sealed class TitleFlow
{
    // Retail title timings, in display frames.
    public const int LogoFrames = 364, LogoFade = 32;
    public const int IntroPictureFrames = 0x8F5;
    public const int IntroMusicFrames = 4401;
    public const int IdleFrames = 1000, IdleAfterMusic = 100;
    public const int LeaveFrames = 64;

    private readonly ITitleHost _host;
    // TITLE.PRG increments this once per title visit; its low bit selects the idle demo.
    private static int _visits;
    private IEnumerator<int>? _presentation;
    private int _wait;
    private long _frame, _musicStart = -1;
    private TitlePad _raw, _accumulated, _held, _pressed;
    private readonly TitleMenuState _menu;
    public TitleMenuState Menu => _menu;
    public TitleStage Stage { get; private set; }
    public TitleCanvas Canvas { get; private set; }
    public TitleLogo Logo { get; private set; }
    public float LogoLevel { get; private set; }
    public bool VideoVisible { get; private set; }
    public float CopyrightAlpha { get; private set; }
    public float PanelAlpha { get; private set; }
    public float Darkening { get; private set; }
    public bool Visible { get; private set; } = true;
    public int MenuSounds { get; private set; }
    public int SelectedItem { get; private set; }
    public long Frame => _frame;
    public string Failure { get; private set; } = "";
    public bool MusicPlaying => _musicStart >= 0 && _host.MusicPlaying;

    public TitleFlow(ITitleHost host, ushort[][] palettes, bool clearSave = false)
    {
        _host = host;
        _menu = new TitleMenuState(palettes);
        ++_visits;
        Enter(clearSave ? TitleStage.ClearSave : _visits == 1 ? TitleStage.Publisher : TitleStage.Intro);
    }

    /// <summary>Retail resident sound selection, also used by the card screens.</summary>
    public void PlaySound(int id) { MenuSounds++; _host.PlaySound(id); }
    private void Fail(string detail) { Failure = detail; _presentation = null; }

    /// <summary>Moves to a screen and starts presenting it.</summary>
    private void Enter(TitleStage stage)
    {
        Stage = stage;
        _presentation = stage switch
        {
            TitleStage.Publisher => ShowLogo(TitleLogo.Publisher, false),
            TitleStage.Developer => ShowLogo(TitleLogo.Developer, true),
            TitleStage.Intro => Intro(),
            TitleStage.Menu => TitleScreen(),
            TitleStage.NewGame => Leave(),
            TitleStage.Field => StartField(),
            TitleStage.Continue => LoadScreens(),
            TitleStage.ClearSave => ClearSaveScreens(),
            _ => Leave(),
        };
        _wait = 0;
    }

    /// <summary>The current screen has finished: the logos lead to the movie, the movie to the title screen, and leaving it back to the movie (or on to the field for New Game).</summary>
    private void Finished()
    {
        Enter(Stage switch
        {
            TitleStage.Publisher => TitleStage.Developer,
            TitleStage.Developer => TitleStage.Intro,
            TitleStage.Intro => TitleStage.Menu,
            TitleStage.NewGame => TitleStage.Field,
            _ => TitleStage.Intro,
        });
    }

    public void Tick(TitlePad raw)
    {
        _frame++;
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
        _accumulated = TitlePad.None;
    }

    private TitlePad TakePressed()
    {
        TitlePad pressed = _pressed;
        _pressed = TitlePad.None;
        return pressed;
    }

    private void StopMusic()
    {
        _host.StopMusic();
        _musicStart = -1;
    }

    private IEnumerator<int> ShowLogo(TitleLogo logo, bool skippable)
    {
        Canvas = TitleCanvas.Boot;
        VideoVisible = false;
        Logo = logo;
        for (int i = 0; i < LogoFrames; i++)
        {
            int fade = i < LogoFade ? (LogoFade - 1 - i) * 4 : i > LogoFrames - LogoFade - 1 ? (i - (LogoFrames - LogoFade - 1)) * 4 : 0;
            if (skippable && i >= LogoFade && i <= LogoFrames - LogoFade - 1 && _held != TitlePad.None) i = LogoFrames - LogoFade - 1;
            float level = (128 - fade) / 128f;
            LogoLevel = level;
            yield return 1;
            Poll();
            TakePressed();
        }
        Logo = TitleLogo.None;
        Finished();
    }

    private IEnumerator<int> Intro()
    {
        Canvas = TitleCanvas.Boot;
        Logo = TitleLogo.None;
        VideoVisible = true;
        _host.PlayIntro();
        _musicStart = _frame;
        TakePressed();
        while (true)
        {
            yield return 1;
            Poll();
            if (_frame - _musicStart >= IntroPictureFrames) break;
            if ((TakePressed() & (TitlePad.Circle | TitlePad.Start)) != 0) break;
        }
        // The picture ends here; the movie's sound continues under the title screen as its music.
        VideoVisible = false;
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
        Darkening = 0;
        CopyrightAlpha = 0;
        PanelAlpha = 0;
        Canvas = TitleCanvas.Title;
        // The cursor starts on Continue when a card holds a save, as retail's does, and on New Game otherwise.
        int[] wheel = TitleMenuState.Wheel;
        int count = wheel.Length, position = _host.HasSave ? Array.IndexOf(wheel, TitleMenuState.Continue) : 0;
        SelectedItem = wheel[position];
        _menu.Show(wheel[position], 64);
        _menu.Show(wheel[(position + 1) % count], 96);
        _menu.Show(wheel[(position + count - 1) % count], 32);
        for (int fade = 32; fade >= 0; fade -= 2)
        {
            yield return 2;
            Poll();
            CopyrightAlpha = (32 - fade) / 32f;
        }
        yield return 1;
        yield return 1;
        for (int fade = 32; fade >= 0; fade -= 4)
        {
            PanelAlpha = (32 - fade) / 32f;
            yield return 4;
            Poll();
            foreach (int frame in DrawMenu()) yield return frame;
        }
        for (int i = 0; i < 8; i++)
            foreach (int frame in DrawAndSync()) yield return frame;

        TakePressed();
        long lastMusic = _frame, lastInput = _frame;
        TitleStage choice;
        while (true)
        {
            yield return 1;
            Poll();
            if (MusicPlaying) lastMusic = _frame;
            if (_held != TitlePad.None) lastInput = _frame;
            if (_frame - lastInput > IdleFrames && _frame - lastMusic > IdleAfterMusic)
            {
                choice = TitleStage.Attract;
                break;
            }
            if ((_held & (TitlePad.Circle | TitlePad.Start)) != 0)
            {
                if (SelectedItem == TitleMenuState.NewGame) { PlaySound(1); PlaySound(2); PlaySound(3); choice = TitleStage.NewGame; break; }
                if (SelectedItem == TitleMenuState.Continue) { PlaySound(5); choice = TitleStage.Continue; break; }
                PlaySound(5);
                int above = wheel[(position + count - 1) % count], below = wheel[(position + 1) % count];
                var submenu = SelectedItem switch
                {
                    TitleMenuState.Vibration => Submenu(TitleMenuState.Vibration, above, below, TitleMenuState.VibrationOn, TitleMenuState.VibrationOff,
                        _host.Vibration ? TitleMenuState.VibrationOn : TitleMenuState.VibrationOff,
                        option => _host.Vibration = option == TitleMenuState.VibrationOn),
                    TitleMenuState.Sound => Submenu(TitleMenuState.Sound, above, below, TitleMenuState.Stereo, TitleMenuState.Mono, _host.MonoSound ? TitleMenuState.Mono : TitleMenuState.Stereo,
                        option => _host.MonoSound = option == TitleMenuState.Mono),
                    _ => Settings(),
                };
                foreach (int frame in submenu) yield return frame;
                lastInput = _frame;
            }
            int direction = 0;
            if ((_held & (TitlePad.Up | TitlePad.Left)) != 0) direction--;
            if ((_held & (TitlePad.Select | TitlePad.Right | TitlePad.Down)) != 0) direction++;
            if (direction != 0)
            {
                PlaySound(4);
                position = (position + direction + count) % count;
                SelectedItem = wheel[position];
                // Words resting off the drum (rows 0 and 128) are invisible; with a fifth word on the wheel they must
                // not keep travelling, so they are dropped before the incoming word appears.
                foreach (int word in wheel)
                    if (_menu[word].Enabled && (_menu[word].Row == 0 || _menu[word].Row == 128)) _menu[word].Enabled = false;
                _menu.Show(wheel[(position + direction + count) % count], direction > 0 ? 128 : 0);
                foreach (int word in wheel)
                {
                    _menu[word].State = direction > 0 ? TitleMenuState.Motion.Rising : TitleMenuState.Motion.Falling;
                    _menu[word].Target = (_menu[word].Row - 32 * direction) & 0xFF;
                }
                for (int i = 0; i < 10; i++)
                    foreach (int frame in DrawAndSync()) yield return frame;
            }
            foreach (int frame in DrawMenu()) yield return frame;
        }
        if (choice != TitleStage.Attract && MusicPlaying) StopMusic();
        Enter(choice);
    }

    /// <summary>The Vibration and Sound option pickers: the heading rises and its two options appear below it.</summary>
    private IEnumerable<int> Submenu(int heading, int above, int below, int upper, int lower, int current, Action<int> confirm)
    {
        _menu[above].State = TitleMenuState.Motion.Rising;
        _menu[above].Target = 0;
        _menu[below].State = TitleMenuState.Motion.Falling;
        _menu[below].Target = 128;
        _menu[heading].State = TitleMenuState.Motion.Opened;
        _menu.Show(upper, 64);
        _menu.Show(lower, 96);
        _menu[upper].State = TitleMenuState.Motion.Opened;
        _menu[lower].State = TitleMenuState.Motion.Opened;
        _menu.Recolour(lower, 0, TitleMenuState.Dark, TitleMenuState.Dark);
        _menu.Recolour(upper, 0, TitleMenuState.Dark, TitleMenuState.Dark);
        for (int i = 1; i < 9; i++)
        {
            _menu.Recolour(heading, i * 2, TitleMenuState.Lit, TitleMenuState.Gold);
            _menu[heading].Row = (_menu[heading].Row - 4) & 0xFF;
            foreach (int frame in DrawAndSync()) yield return frame;
        }
        foreach (int word in TitleMenuState.Wheel)
            if (word != heading) _menu[word].State = TitleMenuState.Motion.Resting;
        for (int i = 1; i < 9; i++)
            foreach (int frame in DrawAndSync()) yield return frame;
        int option = current;
        for (int i = 0; i < 8; i++)
        {
            _menu.Recolour(option, i * 2, TitleMenuState.Dark, TitleMenuState.Lit);
            _menu[option].Glow = i < 4 ? i * 64 : (_menu[option].Glow - 16) & 0xFF;
            foreach (int frame in DrawAndSync()) yield return frame;
        }
        TakePressed();
        while (true)
        {
            foreach (int frame in DrawAndSync()) yield return frame;
            TitlePad pressed = TakePressed();
            if ((_held & (TitlePad.Start | TitlePad.Circle)) != 0)
            {
                PlaySound(5);
                confirm(option);
                if (option == TitleMenuState.VibrationOn)
                    _host.PreviewVibration();
                break;
            }
            if ((_held & TitlePad.Cross) != 0) { PlaySound(6); break; }
            if ((pressed & (TitlePad.Up | TitlePad.Down | TitlePad.Select)) != 0)
            {
                PlaySound(4);
                option = 11 - option;
                for (int i = 1; i < 8; i++)
                {
                    _menu.Recolour(option, i * 2, TitleMenuState.Dark, TitleMenuState.Lit);
                    _menu.Recolour(11 - option, i * 2, TitleMenuState.Lit, TitleMenuState.Dark);
                    _menu[option].Glow = i < 4 ? i * 64 : (_menu[option].Glow - 16) & 0xFF;
                    _menu[11 - option].Glow = (_menu[11 - option].Glow - 16) & 0xFF;
                    if (i == 7) break;
                    foreach (int frame in DrawAndSync()) yield return frame;
                }
            }
        }
        _menu.Show(above, 0);
        _menu.Show(below, 128);
        _menu[above].State = TitleMenuState.Motion.Falling;
        _menu[above].Target = 32;
        _menu[below].State = TitleMenuState.Motion.Rising;
        _menu[below].Target = 96;
        for (int i = 1; i < 9; i++)
        {
            _menu.Recolour(heading, i * 2, TitleMenuState.Gold, TitleMenuState.Lit);
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
        _menu[heading].State = TitleMenuState.Motion.Resting;
    }

    /// <summary>The added Settings item: the shared graphics panel and master volume. The title waits while it is open.</summary>
    private IEnumerable<int> Settings()
    {
        _host.OpenSettings();
        while (_host.SettingsOpen) yield return 1;
        // The button that closed the dialog must not reach the title menu as a fresh confirmation.
        while (_raw != TitlePad.None) yield return 1;
        Poll();
        TakePressed();
    }

    private IEnumerator<int> LoadScreens()
    {
        for (int i = 1; i <= LeaveFrames; i++)
        {
            yield return 1;
            Poll();
            Darkening = Math.Min(255, i * 8) / 255f;
        }
        _host.OpenLoad();
        TakePressed();
        TitleCardResult result = TitleCardResult.Pending;
        while (result == TitleCardResult.Pending)
        {
            yield return 1;
            Poll();
            result = _host.StepCards(TakePressed());
        }
        if (result == TitleCardResult.Loaded) { _host.ResumeGame(); yield break; }
        Finished();
    }

    private IEnumerator<int> ClearSaveScreens()
    {
        _host.OpenClearSave();
        TakePressed();
        TitleCardResult result = TitleCardResult.Pending;
        while (result == TitleCardResult.Pending)
        {
            yield return 1;
            Poll();
            result = _host.StepCards(TakePressed());
        }
        _host.CloseClearSave();
        Finished();
    }

    private IEnumerator<int> StartField() { _host.StartNewGame(); yield break; }

    private IEnumerator<int> Leave()
    {
        for (int i = 1; i <= LeaveFrames; i++)
        {
            yield return 1;
            Poll();
            Darkening = Math.Min(255, i * 8) / 255f;
        }
        if (Stage == TitleStage.Attract)
        {
            // TITLE.PRG chooses alternating demos, with the demo and automatic-scene flags set by the field host.
            _host.StartDemo((_visits & 1) != 0 ? 20 : 2, (_visits & 1) != 0 ? 0 : 1);
            Visible = false;
            TakePressed();
            while (true)
            {
                yield return 1;
                Poll();
                if (_host.DemoFailure.Length != 0)
                {
                    string failure = _host.DemoFailure;
                    _host.EndDemo();
                    Visible = true;
                    Fail(failure);
                    yield break;
                }
                if (TakePressed() != TitlePad.None || _host.DemoFinished) break;
            }
            _host.EndDemo();
            _visits++;
            Visible = true;
        }
        Finished();
    }
}
