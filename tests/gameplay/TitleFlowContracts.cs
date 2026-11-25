// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Wol.Games.Vagrant.Title;

internal static class TitleFlowContracts
{
    private sealed class Host : ITitleHost
    {
        public bool HasSave { get; set; }
        public bool MonoSound { get; set; }
        public bool Vibration { get; set; } = true;
        public bool MusicPlaying { get; private set; }
        public bool SettingsOpen { get; set; }
        public bool DemoFinished { get; set; }
        public string DemoFailure { get; set; } = "";
        public readonly List<int> Sounds = new();
        public readonly List<(int Zone, int Map)> Demos = new();
        public TitleCardResult CardResult;
        public TitlePad LastCardInput;
        public int Intros, Stops, SettingsOpened, Rumbles, LoadsOpened, ClearSavesOpened, ClearSavesClosed, Resumes, NewGames, DemosEnded;
        public void PlayIntro() { Intros++; MusicPlaying = true; }
        public void StopMusic() { Stops++; MusicPlaying = false; }
        public void PlaySound(int id, int voice, int pan, int volume)
        {
            Expect(voice == 1 && pan == 0x80 && volume == 0x7F, "the game omitted retail sound parameters");
            Sounds.Add(id);
        }
        public void PreviewVibration(float weak, float strong, float seconds)
        {
            Expect(weak == 0.5f && strong == 1f && seconds == 0.25f, "the game omitted the preview envelope");
            Rumbles++;
        }
        public void OpenSettings() { SettingsOpened++; SettingsOpen = true; }
        public void OpenLoad() => LoadsOpened++;
        public void OpenClearSave(int location)
        {
            Expect(location == 48, "the post-credits save location changed");
            ClearSavesOpened++;
        }
        public TitleCardResult StepCards(TitlePad pressed) { LastCardInput = pressed; return CardResult; }
        public void CloseClearSave() => ClearSavesClosed++;
        public void ResumeGame() => Resumes++;
        public void StartNewGame() => NewGames++;
        public void StartDemo(int zone, int map) { Demos.Add((zone, map)); DemoFinished = false; }
        public void EndDemo() => DemosEnded++;
    }

    public static void Check()
    {
        if (typeof(TitleFlow).Assembly != typeof(Wol.Games.Vagrant.Runtime.FieldContent).Assembly)
            throw new InvalidOperationException("Title rules escaped the portable gameplay assembly.");
        ushort[][] palettes = {
            new ushort[16], Enumerable.Repeat((ushort)0xFFFF, 16).ToArray(),
            new ushort[16], Enumerable.Repeat((ushort)0x801F, 16).ToArray(),
        };
        var wheel = new TitleMenuState(palettes);
        wheel.Show(0, 64);
        for (int i = 0; i < 19; i++) wheel.Step();
        Expect(wheel[0].Brightness == 128 && wheel[0].Warmth == 16, "the selected word's fade/warmth timing changed");
        wheel.Recolour(0, 8, TitleMenuState.Dark, TitleMenuState.Lit);
        Expect(wheel[0].Palette[0] == 0 && wheel[0].Palette[1] == 0xBDEF, "retail 5-bit palette interpolation changed");
        wheel.Show(1, 0); wheel[1].State = TitleMenuState.Motion.Rising; wheel[1].Target = 224;
        for (int i = 0; i < 8; i++) wheel.Step();
        Expect(wheel[1].Row == 224 && wheel[1].State == TitleMenuState.Motion.Resting, "the eight-bit drum no longer wraps");

        var host = new Host();
        var flow = new TitleFlow(host, palettes);
        void Tick(int count, TitlePad pad = TitlePad.None) { for (int i = 0; i < count; i++) flow.Tick(pad); }
        void Until(Func<bool> condition, int limit, string what)
        {
            for (int i = 0; i < limit && !condition(); i++) flow.Tick(TitlePad.None);
            Expect(condition(), "timed out waiting for " + what);
        }
        void Press(TitlePad pad, int settle = 40) { Tick(4, pad); Tick(settle); }
        void Menu()
        {
            Until(() => flow.Stage == TitleStage.Intro, 80, "intro");
            Press(TitlePad.Start, 240);
            Expect(flow.Stage == TitleStage.Menu, "skipping the movie failed to reach the menu");
        }

        Tick(TitleFlow.LogoFrames, TitlePad.Circle);
        Expect(flow.Stage == TitleStage.Publisher, "publisher logo became skippable or too short");
        Tick(1);
        Expect(flow.Stage == TitleStage.Developer && flow.Logo == TitleLogo.Developer, "publisher duration or developer presentation changed");
        Tick(60); Press(TitlePad.Circle, 40);
        Expect(flow.Stage == TitleStage.Intro && host.Intros == 1, "developer skip failed");
        Tick(450); Press(TitlePad.Start, 240);
        Expect(flow.Stage == TitleStage.Menu && flow.MusicPlaying && !flow.VideoVisible && flow.Canvas == TitleCanvas.Title,
            "skipping the picture stopped the music or retained the movie canvas");
        Expect(flow.SelectedItem == TitleMenuState.NewGame && flow.Menu[0].Row == 64 && flow.Menu[0].Warmth == 16,
            "the initial cursor/wheel presentation changed");
        Press(TitlePad.Down);
        Expect(flow.SelectedItem == TitleMenuState.Continue && flow.Menu[1].Row == 64, "Down did not rotate Continue into the selected slot");
        Press(TitlePad.Down); Press(TitlePad.Circle, 90);
        Expect(flow.Menu[TitleMenuState.VibrationOn].Enabled && flow.Menu[TitleMenuState.VibrationOff].Enabled, "vibration submenu failed to open");
        // A one-frame edge must survive a 30 Hz pad-read gap, and a held edge must not keep toggling.
        Tick(1, TitlePad.Down); Tick(30);
        Press(TitlePad.Circle, 80);
        Expect(!host.Vibration && host.Rumbles == 0 && !flow.Menu[TitleMenuState.VibrationOn].Enabled,
            "confirming vibration Off or short-press accumulation changed");
        Press(TitlePad.Circle, 90); Press(TitlePad.Down, 30); Press(TitlePad.Circle, 80);
        Expect(host.Vibration && host.Rumbles == 1, "confirming vibration On did not request exactly one preview");
        Press(TitlePad.Down); Press(TitlePad.Circle, 90); Press(TitlePad.Down, 30); Press(TitlePad.Circle, 80);
        Expect(host.MonoSound, "confirming Mono failed");
        Press(TitlePad.Circle, 90); Press(TitlePad.Down, 30); Press(TitlePad.Cross, 80);
        Expect(host.MonoSound, "cancelling Sound changed the selected device mode");
        Press(TitlePad.Down);
        Expect(flow.SelectedItem == TitleMenuState.Settings, "Settings is missing from the wheel");
        Press(TitlePad.Circle, 10);
        int row = flow.Menu[TitleMenuState.Settings].Row, warmth = flow.Menu[TitleMenuState.Settings].Warmth;
        Tick(3000, TitlePad.Circle);
        Expect(host.SettingsOpen && flow.Stage == TitleStage.Menu && flow.Menu[TitleMenuState.Settings].Row == row
            && flow.Menu[TitleMenuState.Settings].Warmth == warmth, "Settings advanced the wheel or triggered idle playback");
        host.SettingsOpen = false;
        Tick(10, TitlePad.Circle); Tick(30);
        Expect(host.SettingsOpened == 1 && !host.SettingsOpen && flow.SelectedItem == TitleMenuState.Settings,
            "closing Settings leaked a held confirmation to the menu");
        Press(TitlePad.Down); Press(TitlePad.Down);
        Expect(flow.SelectedItem == TitleMenuState.Continue, "the five-word wheel did not wrap");
        Press(TitlePad.Circle, 1);
        Until(() => host.LoadsOpened == 1, 100, "LOAD screens");
        Tick(1, TitlePad.Cross);
        Expect(host.LastCardInput == TitlePad.Cross, "card UI did not receive typed pressed buttons");
        host.CardResult = TitleCardResult.Cancelled;
        Tick(1); host.CardResult = TitleCardResult.Pending;
        Expect(flow.Stage == TitleStage.Intro, "cancelling LOAD did not return to the title loop");
        long intro = flow.Frame;
        Tick(TitleFlow.IntroPictureFrames - 1);
        Expect(flow.Stage == TitleStage.Intro, "the unskipped intro picture ended early");
        Tick(1);
        Expect(flow.Stage == TitleStage.Menu && flow.Frame - intro == TitleFlow.IntroPictureFrames, "intro picture timing changed");
        Until(() => flow.Stage == TitleStage.Attract, TitleFlow.IntroMusicFrames + TitleFlow.IdleFrames, "idle attract mode");
        long leave = flow.Frame;
        Tick(TitleFlow.LeaveFrames - 1);
        Expect(host.Demos.Count == 0, "attract fade ended early");
        Tick(1);
        Expect(host.Demos.SequenceEqual(new[] { (20, 0) }) && !flow.Visible && flow.Frame - leave == TitleFlow.LeaveFrames,
            "the first demo or its 64-frame fade changed");
        host.DemoFinished = true; Tick(1);
        Expect(flow.Stage == TitleStage.Intro && flow.Visible && host.DemosEnded == 1, "a completed demo did not release its host scene");
        Tick(TitleFlow.IntroPictureFrames);
        Until(() => flow.Stage == TitleStage.Attract, TitleFlow.IntroMusicFrames + TitleFlow.IdleFrames, "second idle demo");
        Until(() => host.Demos.Count == 2, 80, "second demo scene");
        Expect(host.Demos[1] == (2, 1), "the retail demo starts did not alternate");
        Tick(1, TitlePad.L1); Tick(1);
        Expect(flow.Stage == TitleStage.Intro && host.DemosEnded == 2, "a shoulder press did not interrupt the demo");
        Menu();
        Press(TitlePad.Circle, 1);
        Expect(flow.Stage == TitleStage.NewGame && !flow.MusicPlaying, "New Game failed to stop the title music");
        Until(() => host.NewGames == 1, 80, "opening field");
        Tick(10);
        Expect(flow.Stage == TitleStage.Field && host.NewGames == 1 && flow.Darkening == 1,
            "the field handoff repeated or its fade did not finish");
        Expect(host.Sounds.TakeLast(3).SequenceEqual(new[] { 1, 2, 3 }) && flow.MenuSounds == host.Sounds.Count,
            "New Game sound selection or counting changed");

        var saved = new Host { HasSave = true };
        var load = new TitleFlow(saved, palettes);
        Expect(load.Stage == TitleStage.Intro, "a later title visit replayed boot logos");
        load.Tick(TitlePad.None); load.Tick(TitlePad.Start);
        for (int i = 0; i < 240; i++) load.Tick(TitlePad.None);
        Expect(load.SelectedItem == TitleMenuState.Continue, "an existing save did not select Continue");
        for (int i = 0; i < 4; i++) load.Tick(TitlePad.Circle);
        for (int i = 0; i < 80; i++) load.Tick(TitlePad.None);
        saved.CardResult = TitleCardResult.Loaded; load.Tick(TitlePad.None);
        for (int i = 0; i < 10; i++) load.Tick(TitlePad.None);
        Expect(saved.Resumes == 1 && saved.NewGames == 0, "LOAD did not hand off exactly once");
        var clear = new Host();
        var cleared = new TitleFlow(clear, palettes, clearSave: true);
        cleared.Tick(TitlePad.None);
        Expect(cleared.Stage == TitleStage.ClearSave && clear.ClearSavesOpened == 1, "post-credits SAVE was skipped");
        cleared.PlaySound(6);
        clear.CardResult = TitleCardResult.Cancelled; cleared.Tick(TitlePad.Cross);
        Expect(clear.ClearSavesClosed == 1 && cleared.Stage == TitleStage.Intro && cleared.MenuSounds == 1,
            "post-credits SAVE cancellation did not clean up and return to the title");
        var savedClearHost = new Host();
        var savedClear = new TitleFlow(savedClearHost, palettes, clearSave: true);
        savedClear.Tick(TitlePad.None);
        savedClearHost.CardResult = TitleCardResult.Saved; savedClear.Tick(TitlePad.Circle);
        Expect(savedClearHost.ClearSavesClosed == 1 && savedClear.Stage == TitleStage.Intro && savedClearHost.Resumes == 0,
            "a completed post-credits SAVE did not close before returning to the title loop");
        var brokenHost = new Host();
        var broken = new TitleFlow(brokenHost, palettes);
        for (int i = 0; i < 7000 && brokenHost.Demos.Count == 0; i++) broken.Tick(TitlePad.None);
        Expect(brokenHost.Demos.Count == 1, "idle demo setup failed in the failure check");
        brokenHost.DemoFailure = "disc content is damaged";
        broken.Tick(TitlePad.None);
        long failedAt = broken.Frame;
        broken.Tick(TitlePad.Start);
        Expect(broken.Failure == brokenHost.DemoFailure && broken.Visible && brokenHost.DemosEnded == 1
            && broken.Frame == failedAt + 1 && brokenHost.Intros == 1,
            "a failed demo did not clean up and stop title transitions");
        Console.WriteLine("TITLE_FLOW_CONTRACTS_OK: renderer-free logos/movie/menu timing, wheel/palettes, pad accumulation, settings suppression, options, cards, demo alternation and field handoffs");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
