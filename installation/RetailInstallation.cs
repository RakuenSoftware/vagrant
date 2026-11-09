// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wol.Engine.Install;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// Installs presentation content from the player's own verified retail disc.
/// A complete content generation is written before the root manifest is atomically replaced,
/// so a failed or rejected installation leaves any previous installation untouched.
/// </summary>
public sealed class RetailInstallation
{
    public const int Schema = 12;
    public const string ManifestName = "manifest.json";

    /// <summary>Video frames and duration the converted intro must have; anything else is rejected.</summary>
    private const int IntroVideoFrames = 593;

    public string Root { get; }

    public RetailInstallation(string root) => Root = Path.GetFullPath(root);

    public string ManifestPath => Path.Combine(Root, ManifestName);

    /// <summary>Installs from <paramref name="discPath"/> (BIN or CUE). Throws with a player-facing message on rejection.</summary>
    public JsonObject Install(string discPath, Action<string> progress)
    {
        string ffmpeg = Executable("ffmpeg"), ffprobe = Executable("ffprobe");
        Directory.CreateDirectory(Root);
        string content = Path.Combine(Root, "content");
        Directory.CreateDirectory(content);
        string generation = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(content, generation + ".partial");
        Directory.CreateDirectory(stage);
        try
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            JsonObject source, layout;
            progress("Verifying your Vagrant Story disc…");
            using (var disc = PlayStationDisc.Open(discPath, RetailDisc.Catalog))
            {
                progress("Converting the title screen…");
                layout = new TitleConversion(disc.ReadFile("TITLE/TITLE.PRG")).Export(stage, files);
                TitleConversion.ExportLoading(disc.ReadFile("SLUS_010.40"), stage, files);
                File.WriteAllText(Path.Combine(stage, "title_layout.json"), layout.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                files["title_layout"] = "title_layout.json";

                progress("Converting the dialogue font…");
                var dialogue = new DialogueConversion(disc.ReadFile("BATTLE/SYSTEM.DAT"), disc.ReadFile("BATTLE/BATTLE.PRG"));
                File.WriteAllText(Path.Combine(stage, "dialogue_font.json"), dialogue.Export(stage, files).ToJsonString());
                files["dialogue_font"] = "dialogue_font.json";
                File.WriteAllText(Path.Combine(stage, "system_font.json"), dialogue.ExportPlain(stage, files).ToJsonString());
                files["system_font"] = "system_font.json";
                File.WriteAllText(Path.Combine(stage, "battle_text.json"), dialogue.ExportBattleText().ToJsonString());
                files["battle_text"] = "battle_text.json";

                progress("Converting the HUD…");
                var hud = new HudConversion(disc.ReadFile("BATTLE/SYSTEM.DAT"), disc.ReadFile("BATTLE/BATTLE.PRG")).Export(stage, files);
                File.WriteAllText(Path.Combine(stage, "hud.json"), hud.ToJsonString());
                files["hud"] = "hud.json";

                progress("Converting the opening movie…");
                // The stream is kept as the disc holds it (its raw sectors, video and XA audio), beside its conversion.
                string stream = Path.Combine(stage, "disc", "MOV", "TITLE.STR");
                Directory.CreateDirectory(Path.GetDirectoryName(stream)!);
                using (var output = File.Create(stream)) disc.CopyStream("MOV/TITLE.STR", output);
                files["disc/MOV/TITLE.STR"] = "disc/MOV/TITLE.STR";
                // The movie converts in the background while the field content does.
                // The retail intro's picture ends long before its audio, which continues as the title music.
                // The two are kept as separate tracks so the music can outlive the picture.
                var movie = System.Threading.Tasks.Task.Run(() =>
                {
                    var picture = System.Threading.Tasks.Task.Run(() => Run(ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", stream,
                        "-map", "0:v:0", "-an", "-c:v", "libtheora", "-q:v", "8", "-pix_fmt", "yuv420p", Path.Combine(stage, "intro_video.ogv")));
                    Run(ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", stream,
                        "-map", "0:a:0", "-vn", "-c:a", "libvorbis", "-q:a", "6", Path.Combine(stage, "intro_music.ogg"));
                    picture.GetAwaiter().GetResult();
                    var video = JsonNode.Parse(Run(ffprobe, "-v", "error", "-count_frames", "-select_streams", "v:0",
                        "-show_entries", "stream=width,height,nb_read_frames", "-of", "json", Path.Combine(stage, "intro_video.ogv")))!["streams"]![0]!;
                    if ((int)video["width"]! != 320 || (int)video["height"]! != 224 || int.Parse((string)video["nb_read_frames"]!) != IntroVideoFrames)
                        throw new InvalidDataException("The converted opening movie does not match the retail picture.");
                    var audio = JsonNode.Parse(Run(ffprobe, "-v", "error", "-show_entries", "stream=channels,sample_rate", "-of", "json",
                        Path.Combine(stage, "intro_music.ogg")))!["streams"]![0]!;
                    if ((int)audio["channels"]! != 2) throw new InvalidDataException("The converted title music is not stereo.");
                });

                // The rest of the game's data is kept as the disc holds it, each file verified against the retail digest;
                // the game decodes rooms, events, models, effects and sound from them as it plays, as the console does.
                progress("Copying your disc's game data…");
                var sectors = new JsonObject();
                var located = disc.Sectors;
                foreach (string path in RetailDisc.Catalog.FileDigests.Keys)
                {
                    string file = "disc/" + path;
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(stage, file))!);
                    File.WriteAllBytes(Path.Combine(stage, file), disc.ReadFile(path));
                    files[file] = file;
                    sectors[path] = located[path];
                }
                File.WriteAllText(Path.Combine(stage, "disc_sectors.json"), sectors.ToJsonString());
                files["disc_sectors"] = "disc_sectors.json";

                // The rooms, and Ashley's rig, are converted now, from the files just verified; the rest (characters,
                // events, effects) the field converts as it first needs them.
                progress("Converting the rooms…");
                var bySector = new Dictionary<long, string>();
                foreach (var (path, sector) in sectors) bySector[(long)sector!] = path;
                Runtime.FieldContent Content() => new(path => File.ReadAllBytes(Path.Combine(stage, "disc", path)), path => File.Exists(Path.Combine(stage, "disc", path)),
                    sector => bySector.TryGetValue(sector, out var path) ? path : throw new InvalidDataException($"No file on the disc starts at sector {sector}."), RetailDisc.Catalog.FileDigests.Keys);
                Directory.CreateDirectory(Path.Combine(stage, "rooms"));
                var rooms = Content().RoomNames();
                var converted = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                int skipped = 0;
                System.Threading.Tasks.Parallel.ForEach(rooms, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, Content, (room, _, field) =>
                {
                    // A zone's list may name a room the disc has no file for (sector 0); it has nothing to convert.
                    (JsonObject Document, List<(string Name, Godot.Image Image)> Images)? made;
                    try { made = field.ConvertRoom(room); }
                    catch (InvalidDataException) { System.Threading.Interlocked.Increment(ref skipped); return field; }
                    if (made is not { } done) return field;
                    var madeRoom = done;
                    File.WriteAllText(Path.Combine(stage, "rooms", $"field_{room}.json"), madeRoom.Document.ToJsonString());
                    converted[$"field_{room}"] = $"rooms/field_{room}.json";
                    foreach (var (name, image) in madeRoom.Images)
                    {
                        image.SavePng(Path.Combine(stage, "rooms", name + ".png"));
                        converted[name] = $"rooms/{name}.png";
                    }
                    return field;
                }, _ => { });
                foreach (var (name, file) in converted) files[name] = file;
                progress($"Converted {rooms.Count - skipped} rooms ({skipped} listed without a file).");
                if (Content().Character("character_00") is { } ashley)
                {
                    File.WriteAllBytes(Path.Combine(stage, "rooms", "character_00.glb"), ashley.Model);
                    File.WriteAllText(Path.Combine(stage, "rooms", "character_00.json"), ashley.Definition.ToJsonString());
                    files["rig_character_00"] = "rooms/character_00.glb";
                    files["rig_character_00_definition"] = "rooms/character_00.json";
                }
                movie.GetAwaiter().GetResult();
                files["intro_video"] = "intro_video.ogv";
                files["intro_music"] = "intro_music.ogg";

                source = new JsonObject { ["volume"] = RetailDisc.VolumeIdentifier, ["files"] = new JsonArray() };
                foreach (string verified in disc.VerifiedFiles) source["files"]!.AsArray().Add(verified);
            }

            var assets = new JsonObject();
            // A disc file's digest is its verified retail digest.
            foreach (var (name, file) in files)
                assets[name] = new JsonObject
                {
                    ["file"] = file,
                    ["sha256"] = name.StartsWith("disc/", StringComparison.Ordinal) && RetailDisc.Catalog.FileDigests.TryGetValue(name[5..], out var digest) ? digest : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(stage, file)))),
                };
            var manifest = new JsonObject
            {
                ["schema"] = Schema,
                // The build that converted the rooms and Ashley's rig: another build converts them afresh.
                ["converter"] = typeof(RetailInstallation).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                ["content_dir"] = "content/" + generation,
                ["source"] = source,
                ["assets"] = assets,
            };
            string published = Path.Combine(content, generation);
            Directory.Move(stage, published);
            string pending = Path.Combine(Root, $"manifest-{generation}.tmp");
            File.WriteAllText(pending, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(pending, ManifestPath, true);
            progress("Installation complete.");
            return manifest;
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            throw;
        }
    }

    internal static string Executable(string name)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (string candidate in new[] { name, name + ".exe" })
            {
                string path = Path.Combine(directory, candidate);
                if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException($"Installing requires {name} (FFmpeg) on PATH to convert the opening movie.");
    }

    internal static string Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        var error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} failed: {error.Result.Trim()}");
        return output;
    }

    /// <summary>Deletes content generations that the current manifest no longer references.</summary>
    public void RemoveStaleGenerations(string activeContentDir)
    {
        string content = Path.Combine(Root, "content");
        if (!Directory.Exists(content)) return;
        string active = Path.GetFullPath(Path.Combine(Root, activeContentDir));
        foreach (string directory in Directory.GetDirectories(content))
            if (Path.GetFullPath(directory) != active) Directory.Delete(directory, true);
    }
}
