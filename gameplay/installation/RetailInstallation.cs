// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Wol.Engine.Install;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// The one installation: every file of the player's own retail disc, verified against the retail digest and kept
/// byte for byte as the disc holds it (<c>disc/</c>), with the disc's own record of where each file starts
/// (<c>install.json</c>, which game tables that address files by sector need). Nothing is converted here; the game
/// decodes what it needs from these files as it plays, keeping what it decodes in <c>cache/</c>, which it can always
/// make again. A new installation is staged beside the old and replaces it only once complete, so a rejected disc
/// leaves the installation as it was.
/// </summary>
public sealed class RetailInstallation
{
    public const string RecordName = "install.json";

    // ENDING.PRG 0x8006b9b4 starts XA at 270000; its EOF audio sector is 303263.
    public const string EndingStream = "ENDING/ENDING.XA";
    public const int EndingSector = 270000, EndingSectors = 33264;
    // Each 37.8 kHz stereo XA packet occupies four identical consecutive sectors: at 75 sectors a second the drive's
    // audio decoder takes one packet in four (443.52 s); playing every copy would repeat samples.
    public const int EndingInterleave = 4;
    private const string EndingDigest = "288d0f48787290c10ae74a04555fd2becde2f03e609839abf40bcedd194ea6bd";

    public string Root { get; }

    public RetailInstallation(string root) => Root = Path.GetFullPath(root);

    public string RecordPath => Path.Combine(Root, RecordName);
    public string DiscPath => Path.Combine(Root, "disc");
    public string CachePath => Path.Combine(Root, "cache");

    /// <summary>Installs from <paramref name="discPath"/> (BIN or CUE). Throws with a player-facing message on rejection.</summary>
    public void Install(string discPath, Action<string> progress)
    {
        Directory.CreateDirectory(Root);
        string stage = Path.Combine(Root, "staging");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        try
        {
            var sectors = new JsonObject();
            progress("Verifying and copying your Vagrant Story disc…");
            using (var disc = PlayStationDisc.Open(discPath, RetailDisc.Catalog))
            {
                var located = disc.Sectors;
                foreach (string path in RetailDisc.Catalog.FileDigests.Keys)
                {
                    string file = Path.Combine(stage, "disc", path);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllBytes(file, disc.ReadFile(path));
                    sectors[path] = located[path];
                }
                // A streamed file (the movie: video and XA audio) is kept as its raw sectors.
                foreach (string path in RetailDisc.Catalog.StreamDigests.Keys)
                {
                    string file = Path.Combine(stage, "disc", path);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    using (var output = File.Create(file)) disc.CopyStream(path, output);
                    sectors[path] = located[path];
                }
                using (var output = File.Create(Path.Combine(stage, "disc", EndingStream))) CopyEnding(disc, output);
                sectors[EndingStream] = EndingSector;
            }
            File.WriteAllText(Path.Combine(stage, RecordName), new JsonObject { ["volume"] = RetailDisc.VolumeIdentifier, ["sectors"] = sectors }.ToJsonString());

            // The disc verified: it replaces the installation, and what was decoded from the old one goes with it.
            foreach (string old in new[] { DiscPath, CachePath, Path.Combine(Root, "content") })
                if (Directory.Exists(old)) Directory.Delete(old, true);
            foreach (string old in new[] { RecordPath, Path.Combine(Root, "manifest.json") })
                if (File.Exists(old)) File.Delete(old);
            Directory.Move(Path.Combine(stage, "disc"), DiscPath);
            File.Move(Path.Combine(stage, RecordName), RecordPath);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    /// <summary>Adds the verified unlisted XA sectors to an older file-only installation, atomically.</summary>
    public void InstallEndingMusic(string discPath)
    {
        if (!File.Exists(RecordPath)) throw new InvalidDataException("Install the game from your disc first.");
        string target = Path.Combine(DiscPath, EndingStream), part = target + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            using (var disc = PlayStationDisc.Open(discPath, RetailDisc.Catalog))
            using (var output = File.Create(part)) CopyEnding(disc, output);
            File.Move(part, target, true);
            var record = JsonNode.Parse(File.ReadAllText(RecordPath))!.AsObject();
            record["sectors"]![EndingStream] = EndingSector;
            File.WriteAllText(RecordPath + ".partial", record.ToJsonString());
            File.Move(RecordPath + ".partial", RecordPath, true);
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }

    private static void CopyEnding(PlayStationDisc disc, Stream output)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < EndingSectors; i++)
        {
            byte[] sector = disc.ReadSector(EndingSector + i);
            digest.AppendData(sector, 16, sector.Length - 16);
            output.Write(sector);
        }
        if (!Convert.ToHexString(digest.GetHashAndReset()).Equals(EndingDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The ending music does not match the retail US disc.");
    }

    /// <summary>
    /// The owned disc's unlisted XA music, verified against the retail digest: its raw sectors as the disc holds them,
    /// which the game plays as they are (<see cref="EndingInterleave"/>).
    /// </summary>
    public string EndingMusic()
    {
        string raw = Path.Combine(DiscPath, EndingStream);
        if (!File.Exists(raw)) throw new InvalidDataException("This older installation omits the ending music. Install again from your US disc.");
        using (var input = File.OpenRead(raw))
        using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            if (input.Length != (long)EndingSectors * PlayStationDisc.RawSectorSize) throw new InvalidDataException("The ending music is truncated.");
            var sector = new byte[PlayStationDisc.RawSectorSize];
            for (int i = 0; i < EndingSectors; i++)
            {
                input.ReadExactly(sector);
                digest.AppendData(sector, 16, sector.Length - 16);
            }
            if (!Convert.ToHexString(digest.GetHashAndReset()).Equals(EndingDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed ending music does not match the retail US disc.");
        }
        return raw;
    }
}
