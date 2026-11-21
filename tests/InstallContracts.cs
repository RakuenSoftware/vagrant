// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using Godot;
using Wol.Games.Vagrant.Installation;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Tests;

/// <summary>
/// Checks that only the player's genuine retail disc installs. Arguments: <c>--disc=BIN_OR_CUE</c> and
/// <c>--scratch=DIR</c> (receives a modified copy of the disc), plus <c>--install-root=</c> for the
/// disposable installation this check creates and deletes.
/// </summary>
public partial class InstallContracts : Node
{
    public override void _Ready()
    {
        string disc = "", scratch = "";
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--disc=", StringComparison.Ordinal)) disc = argument["--disc=".Length..];
            if (argument.StartsWith("--scratch=", StringComparison.Ordinal)) scratch = argument["--scratch=".Length..];
        }
        try
        {
            if (disc.Length == 0 || scratch.Length == 0) throw new InvalidOperationException("pass --disc= and --scratch=");
            Run(disc, Path.GetFullPath(scratch));
            GD.Print("INSTALL_CONTRACTS_OK: non-disc, CHD and modified images rejected; retail disc installs; rejection keeps the previous installation; files kept byte for byte");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr("INSTALL_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }

    private void Rejects(RetailInstallation installation, string image, string expected)
    {
        try
        {
            installation.Install(image, _ => { });
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            if (!error.Message.Contains(expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"{Path.GetFileName(image)} was rejected for the wrong reason: {error.Message}");
            return;
        }
        throw new InvalidOperationException($"{Path.GetFileName(image)} was accepted.");
    }

    private void Run(string disc, string scratch)
    {
        Directory.CreateDirectory(scratch);
        var content = GetNode<RetailContent>("/root/RetailContent");
        if (content.Root == RetailContent.DefaultRoot) throw new InvalidOperationException("pass a disposable --install-root=");
        string root = content.GlobalRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var installation = new RetailInstallation(root);

        string blank = Path.Combine(scratch, "blank.bin");
        File.WriteAllBytes(blank, new byte[Wol.Engine.Install.PlayStationDisc.RawSectorSize * 32]);
        Rejects(installation, blank, "no ISO 9660 volume");
        string chd = Path.Combine(scratch, "disc.chd");
        File.WriteAllBytes(chd, new byte[16]);
        Rejects(installation, chd, "chdman");

        content.Install(disc, _ => { });
        string before = File.ReadAllText(installation.RecordPath);
        if (new FileInfo(Path.Combine(installation.DiscPath, RetailInstallation.EndingStream)).Length != (long)RetailInstallation.EndingSectors * Wol.Engine.Install.PlayStationDisc.RawSectorSize)
            throw new InvalidOperationException("the installer omitted the unlisted XA sectors");
        installation.EndingMusic();
        foreach (string path in RetailDisc.Catalog.FileDigests.Keys)
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(installation.DiscPath, path)))) != RetailDisc.Catalog.FileDigests[path])
                throw new InvalidOperationException($"the installed {path} is not the disc's file byte for byte");
        foreach (string entry in Directory.GetFileSystemEntries(root))
            if (Path.GetFileName(entry) is not ("disc" or "cache" or RetailInstallation.RecordName))
                throw new InvalidOperationException($"the installation holds {Path.GetFileName(entry)} besides the disc's files and the cache");

        // One changed byte inside TITLE.PRG's data must reject the image even though its identity matches.
        string image = disc.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) ? Path.ChangeExtension(disc, ".bin") : disc;
        string modified = Path.Combine(scratch, "modified.bin");
        File.Copy(image, modified, true);
        using (var stream = new FileStream(modified, FileMode.Open, System.IO.FileAccess.ReadWrite))
        {
            const long titleOverlaySector = 256000;
            stream.Position = titleOverlaySector * Wol.Engine.Install.PlayStationDisc.RawSectorSize + 24 + 100;
            int value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0xFF));
        }
        try
        {
            Rejects(installation, modified, "differs from the retail disc");
            File.Copy(image, modified, true);
            using (var stream = new FileStream(modified, FileMode.Open, System.IO.FileAccess.ReadWrite))
            {
                stream.Position = (long)RetailInstallation.EndingSector * Wol.Engine.Install.PlayStationDisc.RawSectorSize + 24;
                int value = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(value ^ 1));
            }
            Rejects(installation, modified, "ending music does not match");
        }
        finally
        {
            File.Delete(modified);
        }
        if (File.ReadAllText(installation.RecordPath) != before || Directory.Exists(Path.Combine(root, "staging")))
            throw new InvalidOperationException("a rejected install replaced the installation or left a partial one behind");

        content.Reload();
        if (!content.Installed) throw new InvalidOperationException("the installation does not open: " + content.Failure);
        File.AppendAllText(Path.Combine(installation.DiscPath, "TITLE", "TITLE.PRG"), "x");
        content.Reload();
        if (content.Installed || !content.Failure.Contains("damaged", StringComparison.Ordinal))
            throw new InvalidOperationException("a damaged installed file was accepted: " + content.Failure);
        Directory.Delete(root, true);
    }
}
