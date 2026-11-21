// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using Wol.Engine.Install;
using Wol.Games.Vagrant.Installation;

internal static class InstallationContracts
{
    public static void Check()
    {
        if (typeof(RetailDisc).Assembly != typeof(RetailInstallation).Assembly || typeof(RetailDisc).Assembly != typeof(Wol.Games.Vagrant.Runtime.VibrationDriver).Assembly
            || !RetailDisc.Catalog.FileDigests.TryGetValue("SLUS_010.40", out var digest) || digest.Length != 64 || RetailDisc.Catalog.VolumeIdentifier != "SLUS-01040")
            throw new InvalidOperationException("The embedded retail catalog is not available in portable gameplay.");
        string root = Path.Combine(Path.GetTempPath(), "vagrant-install-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var installation = new RetailInstallation(Path.Combine(root, "previous installation"));
            Directory.CreateDirectory(installation.DiscPath); Directory.CreateDirectory(installation.CachePath);
            File.WriteAllText(installation.RecordPath, "previous record");
            File.WriteAllText(Path.Combine(installation.DiscPath, "original"), "previous verified data");
            File.WriteAllText(Path.Combine(installation.CachePath, "original"), "previous cache");
            string blank = Path.Combine(root, "blank.bin"), compressed = Path.Combine(root, "compressed.chd");
            File.WriteAllBytes(blank, new byte[32 * PlayStationDisc.RawSectorSize]); File.WriteAllBytes(compressed, [0]);
            foreach (var (path, reason) in new[] { (blank, "no ISO 9660 volume"), (compressed, "chdman") })
            {
                bool rejected = false;
                try { installation.Install(path, _ => { }); } catch (InvalidDataException error) when (error.Message.Contains(reason, StringComparison.Ordinal)) { rejected = true; }
                if (!rejected || Directory.Exists(Path.Combine(installation.Root, "staging")) || File.ReadAllText(installation.RecordPath) != "previous record"
                    || File.ReadAllText(Path.Combine(installation.DiscPath, "original")) != "previous verified data" || File.ReadAllText(Path.Combine(installation.CachePath, "original")) != "previous cache")
                    throw new InvalidOperationException("Disc rejection changed an existing installation or left staging behind.");
            }
            // The ending music is played from its verified raw sectors: a missing, truncated or corrupt stream is refused.
            var ending = new RetailInstallation(Path.Combine(root, "ending"));
            Directory.CreateDirectory(Path.Combine(ending.DiscPath, "ENDING"));
            string raw = Path.Combine(ending.DiscPath, RetailInstallation.EndingStream);
            // The full-size fixture is sparse zeros.
            foreach (var (length, reason) in new[] { (-1L, "older installation"), (1L, "truncated"), ((long)RetailInstallation.EndingSectors * PlayStationDisc.RawSectorSize, "does not match") })
            {
                if (length >= 0) { using var file = File.Create(raw); file.SetLength(length); }
                bool rejected = false;
                try { _ = ending.EndingMusic(); } catch (InvalidDataException error) when (error.Message.Contains(reason, StringComparison.Ordinal)) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Invalid raw XA was accepted: " + reason);
            }
            Console.WriteLine("GAME_INSTALLATION_CONTRACTS_OK: embedded catalog, atomic rejection and XA integrity without Godot");
        }
        finally { Directory.Delete(root, true); }
    }
}
