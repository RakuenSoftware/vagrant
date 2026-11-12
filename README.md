# Vagrant

Vagrant Story on the shared C# [wol-engine](https://github.com/RakuenSoftware/wol-engine), consumed at `addons/wol_engine`.

**You need your own US copy of Vagrant Story (SLUS-01040).** The game contains nothing from the disc. On first run it asks for your disc image (BIN/CUE), checks every file against the retail US disc, and copies the files as the disc holds them; the game decodes everything from them as it plays. Without a verified disc it does not start. Modified, patched or other-region discs are rejected.

## Current state

The title runs as on the retail disc: the publisher and developer logos, the opening movie (its music continues under the title), and the title screen. The menu wheel has the retail New Game, Continue, Vibration (On/Off) and Sound (Stereo/Mono), plus an added Settings item between Sound and New Game. Settings opens the shared engine's display and graphics panel and a master volume slider. Its word is composed from the disc's own menu lettering. The title loops back through the intro after leaving or idling. New Game and Continue go no further yet; they show a notice and return to the title. Menu sound effects are not converted yet.

`TitleSequence` owns the title's screens and settings and presents each screen with the retail frame timing. See [docs/TITLE.md](docs/TITLE.md).

## Build

Godot 4.7.2 .NET (`godot-dotnet`), the .NET 10 SDK, SCons 4.11.1, a C++17 toolchain, and FFmpeg/ffprobe on `PATH` (the game decodes the opening movie with them, once, into its cache).

```sh
git submodule update --init --recursive
python -m SCons -C addons/wol_engine game_root=../.. platform=linux target=template_release arch=x86_64 precision=single -j8
dotnet build
godot-dotnet --headless --editor --import --path . --quit
godot-dotnet --path .
```

To install headlessly from a disc image: `godot-dotnet --headless --path . -- --install-disc=/path/to/disc.cue`. `--install-root=DIR` selects another installation directory (default `user://vagrant-story`).

## Checks

```sh
# Only a genuine retail disc installs; a rejected install keeps the previous one.
godot-dotnet --headless --path . res://tests/install_contracts.tscn -- --disc=/path/disc.cue --scratch=/tmp/vs-scratch --install-root=/tmp/vs-scratch/root
# Every event script on the disc translates and the flow runtime accepts it (seconds; no installation needed).
godot-dotnet --headless --path . res://tests/event_contracts.tscn -- --disc=/path/disc.cue
# The prologue played through, battle, doors and rooms; --room=ZONE,MAP plays one room's event alone.
godot-dotnet --headless --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation
# The title flow, timings, menu wheel and both option pickers, driven with pad input.
godot-dotnet --headless --fixed-fps 60 --path . res://tests/title_sequence_contracts.tscn -- --install-root=/path/to/installation
# The same run on an isolated display, saving frames.
gamescope --backend headless -w 1280 -h 960 -W 1280 -H 960 -- godot-dotnet --fixed-fps 60 --path . res://tests/title_sequence_contracts.tscn -- --install-root=/path/to/installation --capture=/tmp/vs-captures
```

## License

AGPL-3.0-only. See [LICENSE](LICENSE). Vagrant Story is © Square Enix; this project contains none of its data.
