// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using Godot;
using Wol.Games.Vagrant.Installation;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Owned-disc XA installation, decode shape, and rejection of a damaged stream even with a cached decode.</summary>
public partial class EndingAudioContracts : Node
{
    public override void _Ready()
    {
        try
        {
            string disc = "", scratch = "";
            foreach (string arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith("--disc=")) disc = arg[7..];
                if (arg.StartsWith("--scratch=")) scratch = arg[10..];
            }
            if (disc.Length == 0 || scratch.Length == 0) throw new InvalidOperationException("pass --disc= and a disposable --scratch=");
            var content = GetNode<RetailContent>("/root/RetailContent");
            var installation = new RetailInstallation(content.GlobalRoot);
            installation.InstallEndingMusic(disc);
            string decoded = installation.EndingMusic(content.MediaTools);
            using var music = AudioStreamOggVorbis.LoadFromFile(decoded);
            if (Math.Abs(music.GetLength() - 443.52) > 0.05) throw new InvalidOperationException("XA duration is not 443.52 seconds");
            string raw = Path.Combine(installation.DiscPath, RetailInstallation.EndingStream);
            var bad = new RetailInstallation(scratch);
            Directory.CreateDirectory(Path.Combine(bad.DiscPath, "ENDING"));
            Directory.CreateDirectory(bad.CachePath);
            string damaged = Path.Combine(bad.DiscPath, RetailInstallation.EndingStream);
            File.Copy(raw, damaged, true);
            File.Copy(decoded, Path.Combine(bad.CachePath, "ending_music.ogg"), true);
            using (var file = System.IO.File.Open(damaged, FileMode.Open, System.IO.FileAccess.ReadWrite))
            { file.Position = 24; int value = file.ReadByte(); file.Position = 24; file.WriteByte((byte)(value ^ 1)); }
            Reject(bad, "does not match");
            using (var file = System.IO.File.Open(damaged, FileMode.Open, System.IO.FileAccess.Write)) file.SetLength(2352);
            Reject(bad, "truncated");
            File.Delete(damaged);
            Reject(bad, "older installation");
            GD.Print("ENDING_AUDIO_CONTRACTS_OK: owned XA installed, stereo 37800 Hz / 443.52 s, corrupt/truncated/missing raw rejected despite cached audio");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PrintErr("ENDING_AUDIO_CONTRACTS_FAILED: " + error.Message); GetTree().Quit(1); }
    }
    private void Reject(RetailInstallation installation, string expected)
    {
        try { installation.EndingMusic(GetNode<RetailContent>("/root/RetailContent").MediaTools); }
        catch (InvalidDataException error) when (error.Message.Contains(expected)) { return; }
        throw new InvalidOperationException("damaged XA was accepted: " + expected);
    }
}
