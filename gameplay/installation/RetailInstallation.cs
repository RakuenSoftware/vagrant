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

    /// <summary>Video frames the intro movie has; anything else is rejected.</summary>
    private const int IntroVideoFrames = 593;
    // ENDING.PRG 0x8006b9b4 starts XA at 270000; its EOF audio sector is 303263.
    public const string EndingStream = "ENDING/ENDING.XA";
    public const int EndingSector = 270000, EndingSectors = 33264;
    private const string EndingDigest = "288d0f48787290c10ae74a04555fd2becde2f03e609839abf40bcedd194ea6bd";

    public string Root { get; }

    public RetailInstallation(string root) => Root = Path.GetFullPath(root);

    public string RecordPath => Path.Combine(Root, RecordName);
    public string DiscPath => Path.Combine(Root, "disc");
    public string CachePath => Path.Combine(Root, "cache");

    /// <summary>Installs from <paramref name="discPath"/> (BIN or CUE). Throws with a player-facing message on rejection.</summary>
    public void Install(string discPath, IMediaTools media, Action<string> progress)
    {
        // Fail before copying the disc if an incomplete player package omitted its tools.
        _ = media.Executable("ffmpeg");
        _ = media.Executable("ffprobe");
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

    /// <summary>
    /// The intro movie's picture and sound (Theora and Vorbis), made from the disc's MOV/TITLE.STR
    /// into the cache the first time they are asked for. The retail intro's picture ends long before its audio, which
    /// continues as the title music, so they are separate tracks.
    /// </summary>
    public (string Video, string Music) IntroMovie(IMediaTools media)
    {
        string video = Path.Combine(CachePath, "intro_video.ogv"), music = Path.Combine(CachePath, "intro_music.ogg");
        if (File.Exists(video) && File.Exists(music)) return (video, music);
        string ffmpeg = media.Executable("ffmpeg"), ffprobe = media.Executable("ffprobe"), stream = Path.Combine(DiscPath, "MOV", "TITLE.STR");
        Directory.CreateDirectory(CachePath);
        string partVideo = video + ".partial.ogv", partMusic = music + ".partial.ogg";
        var picture = System.Threading.Tasks.Task.Run(() => media.Run(ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "psxstr", "-i", stream,
            "-map", "0:v:0", "-an", "-c:v", "libtheora", "-q:v", "8", "-pix_fmt", "yuv420p", partVideo));
        media.Run(ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "psxstr", "-i", stream, "-map", "0:a:0", "-vn", "-c:a", "libvorbis", "-q:a", "6", partMusic);
        picture.GetAwaiter().GetResult();
        var shape = JsonNode.Parse(media.Run(ffprobe, "-v", "error", "-count_frames", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,nb_read_frames", "-of", "json", partVideo))!["streams"]![0]!;
        if ((int)shape["width"]! != 320 || (int)shape["height"]! != 224 || int.Parse((string)shape["nb_read_frames"]!) != IntroVideoFrames)
            throw new InvalidDataException("The opening movie could not be decoded as the retail picture.");
        var audio = JsonNode.Parse(media.Run(ffprobe, "-v", "error", "-show_entries", "stream=channels", "-of", "json", partMusic))!["streams"]![0]!;
        if ((int)audio["channels"]! != 2) throw new InvalidDataException("The title music could not be decoded in stereo.");
        File.Move(partVideo, video, true);
        File.Move(partMusic, music, true);
        return (video, music);
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

    /// <summary>Decodes the owned disc's unlisted XA music, leaving its raw sectors byte for byte in disc/.</summary>
    public string EndingMusic(IMediaTools media)
    {
        string raw = Path.Combine(DiscPath, EndingStream);
        if (!File.Exists(raw)) throw new InvalidDataException("This older installation omits the ending music. Install again from your US disc.");
        string music = Path.Combine(CachePath, "ending_music.ogg");
        Directory.CreateDirectory(CachePath);
        string stream = Path.Combine(CachePath, "ending_audio.partial.xa");
        using (var input = File.OpenRead(raw))
        using (var decodedInput = File.Exists(music) ? null : File.Create(stream))
        using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            if (input.Length != (long)EndingSectors * PlayStationDisc.RawSectorSize) throw new InvalidDataException("The ending music is truncated.");
            var sector = new byte[PlayStationDisc.RawSectorSize];
            for (int i = 0; i < EndingSectors; i++)
            {
                input.ReadExactly(sector);
                digest.AppendData(sector, 16, sector.Length - 16);
                // Each 37.8 kHz stereo XA packet occupies four identical consecutive sectors. At 75 sectors/s,
                // the CD's audio decoder consumes one packet per four sectors; feeding all copies repeats samples.
                // Take the fourth copy, including the last packet's EOF submode.
                if (i % 4 == 3) decodedInput?.Write(sector);
            }
            if (!Convert.ToHexString(digest.GetHashAndReset()).Equals(EndingDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed ending music does not match the retail US disc.");
        }
        if (File.Exists(music)) return music;
        string part = music + ".partial.ogg";
        media.Run(media.Executable("ffmpeg"), "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "psxstr", "-i", stream,
            "-map", "0:a:0", "-vn", "-c:a", "libvorbis", "-q:a", "6", part);
        var audio = JsonNode.Parse(media.Run(media.Executable("ffprobe"), "-v", "error", "-show_entries", "stream=channels,sample_rate,duration", "-of", "json", part))!["streams"]![0]!;
        if ((int)audio["channels"]! != 2 || int.Parse((string)audio["sample_rate"]!) != 37800
            || Math.Abs(double.Parse((string)audio["duration"]!, System.Globalization.CultureInfo.InvariantCulture) - 443.52) > 0.05)
            throw new InvalidDataException("The ending music could not be decoded as the retail stereo stream.");
        File.Move(part, music, true);
        File.Delete(stream);
        return music;
    }

    /// <summary>Exercises the packaged encoders, probe, and game-input decoder availability without retail assets.</summary>
    public static void VerifyMediaTools(IMediaTools media)
    {
        string ffmpeg = media.Executable("ffmpeg"), ffprobe = media.Executable("ffprobe");
        string decoders = media.Run(ffmpeg, "-hide_banner", "-decoders");
        if (!decoders.Contains(" mdec ", StringComparison.Ordinal) || !decoders.Contains(" adpcm_xa ", StringComparison.Ordinal))
            throw new InvalidDataException("The bundled media tool cannot decode PlayStation video and XA audio.");
        string temporary = Path.Combine(Path.GetTempPath(), "project-vagrant-media-" + Guid.NewGuid().ToString("N") + ".ogv");
        string xa = temporary + ".str", decoded = temporary + ".wav";
        try
        {
            // Eight independently constructed Mode 2 XA sectors encode stereo silence:
            // 18 sound groups per sector, 112 samples per channel per group at 37.8 kHz.
            // This exercises real STR probing, demuxing and ADPCM decoding without game data.
            using (var fixture = File.Create(xa))
            {
                for (int index = 0; index < 8; index++)
                {
                    var sector = new byte[2352];
                    for (int sync = 1; sync <= 10; sync++) sector[sync] = 0xFF;
                    sector[13] = 2; sector[14] = (byte)index; sector[15] = 2;
                    sector[18] = sector[22] = index == 7 ? (byte)0xE4 : (byte)0x64;
                    sector[19] = sector[23] = 1; // Stereo, 37.8 kHz, four-bit ADPCM.
                    fixture.Write(sector);
                }
            }
            var input = JsonNode.Parse(media.Run(ffprobe, "-v", "error", "-show_entries", "format=format_name:stream=codec_name,channels,sample_rate", "-of", "json", xa))!;
            if ((string?)input["format"]?["format_name"] != "psxstr"
                || (string?)input["streams"]?[0]?["codec_name"] != "adpcm_xa")
                throw new InvalidDataException("The bundled tools cannot recognize a PlayStation STR/XA stream.");
            media.Run(ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", xa,
                "-map", "0:a:0", "-c:a", "pcm_s16le", decoded);
            using (var wave = new BinaryReader(File.OpenRead(decoded)))
            {
                if (new string(wave.ReadChars(4)) != "RIFF") throw new InvalidDataException("XA decode did not produce PCM audio.");
                wave.ReadUInt32();
                if (new string(wave.ReadChars(4)) != "WAVE") throw new InvalidDataException("XA decode did not produce a WAVE stream.");
                bool samples = false;
                while (wave.BaseStream.Position + 8 <= wave.BaseStream.Length)
                {
                    string chunk = new(wave.ReadChars(4));
                    uint length = wave.ReadUInt32();
                    if (chunk == "data")
                    {
                        if (length != 8 * 18 * 112 * 2 * 2) throw new InvalidDataException("XA decode produced an unexpected sample count.");
                        for (uint i = 0; i < length; i++)
                            if (wave.ReadByte() != 0) throw new InvalidDataException("The synthetic XA silence did not decode correctly.");
                        samples = true;
                        break;
                    }
                    wave.BaseStream.Seek(length + (length & 1), SeekOrigin.Current);
                }
                if (!samples) throw new InvalidDataException("The XA decoder produced no PCM samples.");
            }
            media.Run(ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=size=32x32:rate=15:duration=0.2",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=37800:duration=0.2",
                "-c:v", "libtheora", "-pix_fmt", "yuv420p", "-c:a", "libvorbis", "-ac", "2", temporary);
            var streams = JsonNode.Parse(media.Run(ffprobe, "-v", "error", "-show_entries", "stream=codec_name,channels,width,height", "-of", "json", temporary))!["streams"]!.AsArray();
            bool video = false, audio = false;
            foreach (var stream in streams)
            {
                if ((string?)stream!["codec_name"] == "theora" && (int?)stream["width"] == 32 && (int?)stream["height"] == 32) video = true;
                if ((string?)stream["codec_name"] == "vorbis" && (int?)stream["channels"] == 2) audio = true;
            }
            if (!video || !audio) throw new InvalidDataException("The bundled tools did not produce playable Theora video and stereo Vorbis audio.");
        }
        finally
        {
            foreach (string file in new[] { temporary, xa, decoded })
                if (File.Exists(file)) File.Delete(file);
        }
    }

}
