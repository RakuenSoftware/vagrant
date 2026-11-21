// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using Godot;
using Wol.Games.Vagrant.Installation;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Tests;

/// <summary>Owned-disc XA installation, the native stream's length, and rejection of a damaged stream.</summary>
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
            string raw = installation.EndingMusic();
            using var music = content.EndingMusic();
            if (Math.Abs(music.GetLength() - 443.52) > 0.05) throw new InvalidOperationException($"XA duration is {music.GetLength()} s, not 443.52");
            var bad = new RetailInstallation(scratch);
            Directory.CreateDirectory(Path.Combine(bad.DiscPath, "ENDING"));
            string damaged = Path.Combine(bad.DiscPath, RetailInstallation.EndingStream);
            File.Copy(raw, damaged, true);
            using (var file = System.IO.File.Open(damaged, FileMode.Open, System.IO.FileAccess.ReadWrite))
            { file.Position = 24; int value = file.ReadByte(); file.Position = 24; file.WriteByte((byte)(value ^ 1)); }
            Reject(bad, "does not match");
            using (var file = System.IO.File.Open(damaged, FileMode.Open, System.IO.FileAccess.Write)) file.SetLength(2352);
            Reject(bad, "truncated");
            File.Delete(damaged);
            Reject(bad, "older installation");
            GD.Print("ENDING_AUDIO_CONTRACTS_OK: owned XA installed, played natively for 443.52 s, corrupt/truncated/missing raw rejected");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PrintErr("ENDING_AUDIO_CONTRACTS_FAILED: " + error.Message); GetTree().Quit(1); }
    }
    private void Reject(RetailInstallation installation, string expected)
    {
        try { installation.EndingMusic(); }
        catch (InvalidDataException error) when (error.Message.Contains(expected)) { return; }
        throw new InvalidOperationException("damaged XA was accepted: " + expected);
    }
}
