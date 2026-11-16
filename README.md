# Project Vagrant

Project Vagrant is a clean-room reimplementation of Vagrant Story's gameplay,
with independently written code and content read from the player's own verified
disc. Its purpose is to preserve the game while making the technology used to
run and present it replaceable.

## Why this project exists

The game being preserved is its rules, combat, progression, events and behavior.
Rendering, audio, input and platform integration are responsibilities of the
underlying engine. The aim is for anyone to be able to implement a host engine
around the independently written gameplay, so that the game can outlive any
particular engine or generation of rendering technology.

A Godot implementation, an Unreal Engine 5 implementation, or a future custom
engine should be able to supply those engine capabilities without requiring
another independent reconstruction of Vagrant Story's gameplay. Better lighting,
higher-resolution rendering, new hardware and future graphics techniques should
be improvements to the engine hosting the game, with the game's behavior
preserved across those changes.

The intention is to keep that option open a year from now or a decade from now.
Project Vagrant is part of a broader effort to take the same approach to favorite
PS1 and PS2 games: preserve what makes each game itself, and let its engine evolve.

## Independent implementation and original content

The clean-room approach means writing new implementations of the game's behavior
and data formats. The project does not redistribute the original game executable,
decompiled game code or retail assets. Original content remains separate from
the implementation and comes from the player's own copy.

**You need your own US copy of Vagrant Story (SLUS-01040).** On first run the game
asks for your disc image (BIN/CUE), checks every file against the retail US disc,
and copies the files and the ending's unlisted XA sectors byte for byte. The game
decodes that content as it plays. Without a verified disc it does not start.
Modified, patched or other-region discs are rejected.

## Current engine implementation

The playable implementation currently uses C#/.NET on Godot 4.7.2 through the
shared wol-engine. Its compiled [engine SDK](https://github.com/RakuenSoftware/wol-engine-release)
binaries and runtime resources are vendored at `addons/wol_engine`.

Engine independence is the architectural direction. The current code still has
Godot-specific types and engine interfaces that another host would need to adapt
or port. A separate Unreal Engine 5 backend is not currently supplied. The work
here establishes the gameplay behavior that future engine implementations can
preserve and reuse.

## Acknowledgements

Thanks to [ser-pounce/rood-reverse](https://github.com/ser-pounce/rood-reverse) and
its contributors for documenting Vagrant Story's data structures and file formats.
Their work provides a reference for Project Vagrant's independent implementations.

## Current state

The title presents the publisher and developer logos, opening movie, title screen and menu wheel. New Game enters the prologue and then field play; Continue loads a memory-card save. The retail Sound and Vibration options and the added Settings panel control audio, controller vibration, display and graphics settings.

The field runtime decodes the player's disc at runtime: rooms, characters, event scripts, dialogue, music, sound effects, battle effects, menus, results and ending credits. Combat and enemy planning share one action resolver. Enemy movement includes steering, jumping, flight, moving platforms and scripted walks. Installation, title, event, effect and field contracts exercise these paths; visual fidelity still requires comparison with retail footage.

The earned normal New Game checkpoint lineage completes the main campaign, both final bosses, epilogue and credits.
The post-credits SAVE writes Game Clear Data, and Continue starts New Game+ with carried state. See
[docs/COMPLETION.md](docs/COMPLETION.md) for the checked routes and the limits of retail comparison.

Older file-only installations need reinstalling from the owned US disc to include the ending music. No disc image
is needed during play once installed.

See [docs/TITLE.md](docs/TITLE.md) for the title and [docs/FIELD.md](docs/FIELD.md) for the field runtime.

## Player downloads and desktop packages

Players receive a compiled archive containing Project Vagrant, wol-engine and Godot/.NET;
no engine source checkout or build tools are required. The release targets are
Linux x86-64, Windows x86-64 and macOS Universal (Intel + Apple Silicon).
See [distribution and native CI instructions](docs/DISTRIBUTION.md).
FFmpeg and ffprobe are bundled for first-time disc conversion; player downloads need no PATH configuration.

## Build

The wol-engine binaries are already checked into this repository. No private
engine access, submodule initialization, SCons or C++ compiler is needed. Install
Python 3.12+ and the pinned .NET SDK **10.0.403**. Release builds use
`global.json`'s SDK resolver; an earlier .NET 10 feature band such as 10.0.301
does not satisfy this pin. The build script reports an incompatible SDK before
it downloads Godot or creates player output.

Building bundled FFmpeg/ffprobe also needs a native C toolchain:

- **Windows:** install [MSYS2](https://www.msys2.org/), open its **MINGW64** terminal
  and run `pacman -S --needed make pkgconf diffutils mingw-w64-x86_64-gcc`.
  The default location is `C:/msys64`; set `MSYS2_ROOT` to your installation root
  if it is elsewhere. Git Bash alone does not supply this toolchain.
- **Linux:** install `build-essential pkg-config diffutils curl` (Debian/Ubuntu
  names, or your distribution's equivalents).
- **macOS:** install Xcode Command Line Tools (`xcode-select --install`) and
  `pkg-config` (for example with Homebrew). The build uses the native tools to
  produce both architectures.

Git clones and GitHub **Download ZIP** source archives are supported. ZIP builds
need no Git installation; `BUILD.json` records their revision and dirty state as
unknown. A Git checkout requires Git on PATH to record its actual provenance.
Missing prerequisites are listed together before export or media compilation.
Then build a player on its native host:

```sh
python tools/release.py linux-x64 --version development
# Use windows-x64 on Windows or macos-universal on macOS.
```

For development, `dotnet build` references the vendored assemblies automatically.
`python tools/release.py linux-x64 --prepare` downloads the public Godot editor
and templates, installs the vendored matching API, and prints the qualified
editor path. The `godot-dotnet` commands below refer to that prepared editor.
See [the vendored release contract](docs/DISTRIBUTION.md) for provenance and updates.

To install headlessly from a disc image: `godot-dotnet --headless --path . -- --install-disc=/path/to/disc.cue`. `--install-root=DIR` selects another installation directory (default `user://vagrant-story`).

## Checks

The synthetic attached-rig projection check needs no disc or installation and runs on all three
native CI platforms: `python tools/release.py linux-x64 --verify-projection` (select the native target).

```sh
# Only a genuine retail disc installs; a rejected install keeps the previous one.
godot-dotnet --headless --path . res://tests/install_contracts.tscn -- --disc=/path/disc.cue --scratch=/tmp/vs-scratch --install-root=/tmp/vs-scratch/root
# Every event script on the disc translates and the flow runtime accepts it (seconds; no installation needed).
godot-dotnet --headless --path . res://tests/event_contracts.tscn -- --disc=/path/disc.cue
# The prologue played through, battle, doors and rooms; --room=ZONE,MAP plays one room's event alone.
godot-dotnet --headless --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation
# In-time chain abilities update the saved maximum-chain score.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=9,1 --fight
# Movement timing and distance, followed separately by the final boss's swoop and event handoff.
godot-dotnet --headless --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=13,1 --movement
godot-dotnet --headless --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=27,1 --fight --swoop
# Disk resume and room/zone loads: gear, stats, inventory, progression and native variable lifetimes.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/save_resume_contracts.tscn -- --install-root=/path/to/installation
# Native cleared-game record: story reset, clear cap, titles, best time/map and carried state.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/clear_record_contracts.tscn -- --install-root=/path/to/installation
# Audio stall regression: use a real backend routed to a silent virtual sink, never speakers.
# Example on Linux: create a PulseAudio null sink named vagrant_hit_audit, then:
PULSE_SINK=vagrant_hit_audit godot-dotnet --display-driver headless --audio-driver PulseAudio --path . res://tests/hit_audio_contracts.tscn -- --install-root=/path/to/installation
# Owned-disc ending XA installation/decode and corruption rejection (use disposable scratch).
godot-dotnet --headless --path . res://tests/ending_audio_contracts.tscn -- --install-root=/path/to/installation --disc=/path/disc.cue --scratch=/tmp/vs-ending-audio
# Cancelling the post-credits SAVE preserves the existing card (isolated fixture using an earned record).
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/title_clear_save_contracts.tscn -- --install-root=/path/to/installation --source=/path/to/earned-save.json
# Gadget bag GET/ALL: capacity, partial collection, key possession flags and sigil consumption.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/bag_capacity_contracts.tscn -- --install-root=/path/to/installation
# Teleportation: discovery/paling flags, MP, menu cancellation and arrival at the native circle (isolated fixture).
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/teleport_contracts.tscn -- --install-root=/path/to/installation
# Container selections: identical pieces, multiple transfers, discard, cancellation and item stacks.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/container_contracts.tscn -- --install-root=/path/to/installation
# Awaited system messages preserve a single display-frame button edge on either event-clock phase.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/system_message_contracts.tscn -- --install-root=/path/to/installation
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/system_message_contracts.tscn -- --install-root=/path/to/installation --other-phase
# Native body collision bounds, vertical separation, contact, escape and exclusions (isolated fixture).
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/body_collision_contracts.tscn -- --install-root=/path/to/installation
# Pit height threshold: crossing above a pit and falling without changing tiles.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/pit_contracts.tscn -- --install-root=/path/to/installation
# Cloudstone height limit and a running ledge grab across Revelation Shaft.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/platform_contracts.tscn -- --install-root=/path/to/installation
# Isolated earthquake scene: enemy-only actor flags must leave Ashley's animation running.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=50,20 --flag=1:1 --flag=130:34 --flag=187:13 --flag=200:20 --play=event_z050_m20_l0
# SETUP's Repair: YES/NO, Cross cancellation, all carried equipment, PP cost, accessories and storage.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=42,0 --workshop-repair
# COMBINE's preview/cancellation/confirmation and equipped/nested parts, shields and armour transactions.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=42,0 --workshop-combine
# Assembly component ownership, cancellation and naming; controller keyboard editing and confirmation.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=42,0 --workshop-assembly
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=42,0 --workshop-name
# The pad driver cycles past Sydney using the native AI relation (isolated Grissom fixture).
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=40,24 --flag=1:1 --flag=130:43 --flag=187:21 --battle-sides
# Guaranteed boss drops survive the reward cutaway and are collected through the gadget bag (isolated defeat fixture).
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=40,22 --flag=1:1 --flag=130:42 --flag=187:20 --boss-drops
# A field button held as a reward bag opens must not discard its guaranteed loot (isolated defeat fixture).
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=49,1 --drop-input
# Forest-entry cutscene workers stop before control returns (isolated event fixture).
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=40,21 --flag=1:1 --flag=130:39 --flag=187:19
# Continuous New Game exploration using pad controls and earned state; this driver is still being extended.
# An optional --campaign-checkpoint=ZONE,MAP guides the route and stops at that checkpoint, not a full-campaign verdict.
# --campaign-story=N additionally requires story flag 130 to reach N, so a boss checkpoint can wait for its scene.
# --campaign-flag=FLAG:MIN similarly waits for a local gate or encounter flag without changing it.
# --campaign-save=/tmp/checkpoint.json exports the earned state there; --campaign-load=PATH resumes it.
# This diagnostic save uses the real serializer; it does not test the save-point menu.
# --campaign-route=ZONE,MAP/ZONE,MAP/... supplies ordered doors, including timed corridors.
# --campaign-use=ID,ID,... consumes those already-owned items through ITEMS before continuing.
# --campaign-store uses the workshop container for spare weapons/armour; --campaign-rest waits for native HP/MP recovery.
# --campaign-retrieve=BLADE removes an owned weapon from storage; --campaign-equip=BLADE equips it through ITEMS.
# --campaign-use-at=ZONE,MAP delays the requested owned consumables until that room.
# --campaign-teleport=N uses MAGIC from a checkpoint standing on a circle; N is its native one-based destination.
# --campaign-repair uses SETUP's Repair at a workshop; --campaign-save-ui uses DATA's SAVE at a magic circle.
# --campaign-loot=ZONE,MAP approaches its chest, using an owned Unlock if needed; --campaign-loot-item=ID stops after that item.
# --campaign-item=ID makes the checkpoint require that owned item and allows surviving enemies there.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --campaign
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

AGPL-3.0-only. See [LICENSE](LICENSE).

Project Vagrant is independent and unofficial, and is not affiliated with or
endorsed by Square Enix. Vagrant Story and its original content and branding
belong to their respective rights holders. This project's code licence grants
no rights to the original game or its assets.
