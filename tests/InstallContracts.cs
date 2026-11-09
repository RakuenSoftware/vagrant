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
            GD.Print("INSTALL_CONTRACTS_OK: non-disc, CHD and modified images rejected; retail disc installs; rejection keeps the previous installation");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr("INSTALL_CONTRACTS_FAILED: " + error.Message);
            GetTree().Quit(1);
        }
    }

    private static void Rejects(RetailInstallation installation, string image, string expected)
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

        var manifest = installation.Install(disc, _ => { });
        string before = File.ReadAllText(installation.ManifestPath);
        string generation = (string)manifest["content_dir"]!;

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
        }
        finally
        {
            File.Delete(modified);
        }
        if (File.ReadAllText(installation.ManifestPath) != before) throw new InvalidOperationException("a rejected install replaced the manifest");
        if (Directory.GetDirectories(Path.Combine(root, "content")).Length != 1 || !Directory.Exists(Path.Combine(root, generation)))
            throw new InvalidOperationException("a rejected install left partial content behind or removed the previous generation");

        File.AppendAllText(Path.Combine(root, generation, "publisher.png"), "x");
        content.Reload();
        if (content.Installed || !content.Failure.Contains("damaged", StringComparison.Ordinal))
            throw new InvalidOperationException("a damaged installed asset was accepted: " + content.Failure);
        Directory.Delete(root, true);
    }
}
