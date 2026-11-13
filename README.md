# Vagrant

Vagrant Story on the shared C# [wol-engine](https://github.com/RakuenSoftware/wol-engine), consumed at `addons/wol_engine`.

**You need your own US copy of Vagrant Story (SLUS-01040).** The game contains nothing from the disc. On first run it asks for your disc image (BIN/CUE), checks every file against the retail US disc, and copies the files as the disc holds them; the game decodes everything from them as it plays. Without a verified disc it does not start. Modified, patched or other-region discs are rejected.

## Current state

The title presents the publisher and developer logos, opening movie, title screen and menu wheel. New Game enters the prologue and then field play; Continue loads a memory-card save. The retail Sound and Vibration options and the added Settings panel control audio, controller vibration, display and graphics settings.

The field runtime decodes the player's disc at runtime: rooms, characters, event scripts, dialogue, music, sound effects, battle effects, menus, results and ending credits. Combat and enemy planning share one action resolver. Enemy movement includes steering, jumping, flight, moving platforms and scripted walks. Installation, title, event, effect and field contracts exercise these paths; visual fidelity still requires comparison with retail footage.

See [docs/TITLE.md](docs/TITLE.md) for the title and [docs/FIELD.md](docs/FIELD.md) for the field runtime.

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
# Movement timing and distance, followed separately by the final boss's swoop and event handoff.
godot-dotnet --headless --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=13,1 --movement
godot-dotnet --headless --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=27,1 --fight --swoop
# Every visual effect's records, programs, frame tables and vibration envelopes.
godot-dotnet --headless --path . res://tests/effect_contracts.tscn -- --disc=/path/disc.cue --scratch=/tmp/vs-effects
# The title flow, timings, menu wheel and both option pickers, driven with pad input.
godot-dotnet --headless --fixed-fps 60 --path . res://tests/title_sequence_contracts.tscn -- --install-root=/path/to/installation
# GPU blend rules over a known background, checked through both LDR and HDR compositing.
gamescope --backend headless -w 640 -h 448 -W 640 -H 448 -- godot-dotnet --fixed-fps 60 --audio-driver Dummy --path . res://tests/retail_screen_contracts.tscn
# The same run on an isolated display, saving frames.
gamescope --backend headless -w 1280 -h 960 -W 1280 -H 960 -- godot-dotnet --fixed-fps 60 --path . res://tests/title_sequence_contracts.tscn -- --install-root=/path/to/installation --capture=/tmp/vs-captures
```

Run checks sharing an installation one at a time: each build replaces that installation’s decoded cache. Keep captures and logs outside the repository. Rendering checks require an isolated display.

## License

AGPL-3.0-only. See [LICENSE](LICENSE). Vagrant Story is © Square Enix; this project contains none of its data.
