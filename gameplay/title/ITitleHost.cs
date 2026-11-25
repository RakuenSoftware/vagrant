// SPDX-License-Identifier: AGPL-3.0-only
namespace Wol.Games.Vagrant.Title;

[System.Flags]
public enum TitlePad
{
    None = 0, Circle = 1, Cross = 2, Start = 4, Select = 8, Up = 16, Down = 32, Left = 64, Right = 128,
    L1 = 256, R1 = 512, L2 = 1024, R2 = 2048,
}

public enum TitleStage { Publisher, Developer, Intro, Menu, NewGame, Field, Continue, Attract, ClearSave }
public enum TitleCanvas { Boot, Title }
public enum TitleLogo { None, Publisher, Developer }
public enum TitleCardResult { Pending, Cancelled, Loaded, Saved }

/// <summary>
/// Vagrant's title ports. Any host can implement these operations without Godot types.
/// The game owns timing, selection and the mutable presentation data in TitleFlow and TitleMenuState.
/// Device playback, settings UI, save UI and scene lifetime remain host operations. Calls and reads
/// are synchronous on the game thread; saved-game payloads stay with the card/field owner.
/// </summary>
public interface ITitleHost
{
    bool HasSave { get; }
    bool MonoSound { get; set; }
    bool Vibration { get; set; }
    bool MusicPlaying { get; }
    void PlayIntro();
    void StopMusic();
    void PlaySound(int id, int voice, int pan, int volume);
    void PreviewVibration(float weak, float strong, float seconds);
    bool SettingsOpen { get; }
    void OpenSettings();
    void OpenLoad();
    void OpenClearSave(int location);
    TitleCardResult StepCards(TitlePad pressed);
    void CloseClearSave();
    void ResumeGame();
    void StartNewGame();
    void StartDemo(int zone, int map);
    bool DemoFinished { get; }
    string DemoFailure { get; }
    void EndDemo();
}
