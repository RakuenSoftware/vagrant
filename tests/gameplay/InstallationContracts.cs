// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            var media = new RecordingMedia();
            string blank = Path.Combine(root, "blank.bin"), compressed = Path.Combine(root, "compressed.chd");
            File.WriteAllBytes(blank, new byte[32 * PlayStationDisc.RawSectorSize]); File.WriteAllBytes(compressed, [0]);
            foreach (var (path, reason) in new[] { (blank, "no ISO 9660 volume"), (compressed, "chdman") })
            {
                bool rejected = false;
                try { installation.Install(path, media, _ => { }); } catch (InvalidDataException error) when (error.Message.Contains(reason, StringComparison.Ordinal)) { rejected = true; }
                if (!rejected || Directory.Exists(Path.Combine(installation.Root, "staging")) || File.ReadAllText(installation.RecordPath) != "previous record"
                    || File.ReadAllText(Path.Combine(installation.DiscPath, "original")) != "previous verified data" || File.ReadAllText(Path.Combine(installation.CachePath, "original")) != "previous cache")
                    throw new InvalidOperationException("Disc rejection changed an existing installation or left staging behind.");
            }
            if (!media.Resolved.SequenceEqual(new[] { "ffmpeg", "ffprobe", "ffmpeg", "ffprobe" }) || media.Calls.Count != 0)
                throw new InvalidOperationException("Installation did not resolve both media tools before copying, or ran a converter for a rejected disc.");
            var missing = new RecordingMedia { Missing = true };
            var untouched = new RetailInstallation(Path.Combine(root, "no tools"));
            bool toolRejected = false;
            try { untouched.Install(blank, missing, _ => { }); } catch (FileNotFoundException) { toolRejected = true; }
            if (!toolRejected || Directory.Exists(untouched.Root)) throw new InvalidOperationException("Missing tools changed the installation before rejection.");

            // Cached movie tracks are usable without resolving or launching external tools.
            File.WriteAllText(Path.Combine(installation.CachePath, "intro_video.ogv"), "synthetic cached picture");
            File.WriteAllText(Path.Combine(installation.CachePath, "intro_music.ogg"), "synthetic cached audio");
            _ = installation.IntroMovie(missing);
            if (missing.Resolved.Count != 1) throw new InvalidOperationException("A cached intro attempted media discovery.");

            // A conversion uses the explicit STR demuxer and publishes only validated video/audio tracks.
            var fresh = new RetailInstallation(Path.Combine(root, "movie conversion"));
            media = new RecordingMedia();
            var tracks = fresh.IntroMovie(media);
            if (!File.Exists(tracks.Video) || !File.Exists(tracks.Music) || media.Calls.Count != 4)
                throw new InvalidOperationException("Media conversion did not validate and publish both tracks.");
            foreach (var call in media.Calls.Where(c => c.Executable == "ffmpeg"))
            {
                int format = Array.IndexOf(call.Arguments, "-f"), input = Array.IndexOf(call.Arguments, "-i");
                if (format < 0 || call.Arguments[format + 1] != "psxstr" || input < 0 || call.Arguments[input + 1] != Path.Combine(fresh.DiscPath, "MOV", "TITLE.STR"))
                    throw new InvalidOperationException("The intro no longer supplies its STR format and input as separate process arguments.");
            }
            var invalid = new RetailInstallation(Path.Combine(root, "invalid conversion"));
            bool shapeRejected = false;
            try { _ = invalid.IntroMovie(new RecordingMedia { VideoFrames = 592 }); } catch (InvalidDataException) { shapeRejected = true; }
            if (!shapeRejected || File.Exists(Path.Combine(invalid.CachePath, "intro_video.ogv")) || File.Exists(Path.Combine(invalid.CachePath, "intro_music.ogg")))
                throw new InvalidOperationException("A movie with the wrong retail frame count was published.");

            // Cached audio cannot hide missing, truncated or corrupt raw XA. The full-size fixture is sparse zeros.
            var ending = new RetailInstallation(Path.Combine(root, "ending"));
            Directory.CreateDirectory(Path.Combine(ending.DiscPath, "ENDING")); Directory.CreateDirectory(ending.CachePath);
            File.WriteAllText(Path.Combine(ending.CachePath, "ending_music.ogg"), "synthetic cached audio");
            string raw = Path.Combine(ending.DiscPath, RetailInstallation.EndingStream);
            foreach (var (length, reason) in new[] { (-1L, "older installation"), (1L, "truncated"), ((long)RetailInstallation.EndingSectors * PlayStationDisc.RawSectorSize, "does not match") })
            {
                if (length >= 0) { using var file = File.Create(raw); file.SetLength(length); }
                bool rejected = false;
                try { _ = ending.EndingMusic(missing); } catch (InvalidDataException error) when (error.Message.Contains(reason, StringComparison.Ordinal)) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Invalid raw XA was accepted because a cached decode exists.");
            }
            Console.WriteLine("GAME_INSTALLATION_CONTRACTS_OK: embedded catalog, atomic rejection, tool preflight, cached intro, STR arguments, validated tracks and XA integrity without Godot");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class RecordingMedia : IMediaTools
    {
        public readonly List<string> Resolved = new();
        public readonly List<(string Executable, string[] Arguments)> Calls = new();
        public bool Missing;
        public int VideoFrames = 593;
        public string Executable(string name)
        {
            Resolved.Add(name);
            if (Missing) throw new FileNotFoundException("Synthetic missing media tool.");
            return name;
        }
        public string Run(string executable, params string[] arguments)
        {
            lock (Calls) Calls.Add((executable, arguments));
            if (executable == "ffmpeg") { File.WriteAllText(arguments[^1], "synthetic converted track"); return ""; }
            return arguments.Contains("-count_frames") ? $"{{\"streams\":[{{\"width\":320,\"height\":224,\"nb_read_frames\":\"{VideoFrames}\"}}]}}" : "{\"streams\":[{\"channels\":2}]}";
        }
    }
}
