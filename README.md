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
# In-time chain abilities update the saved maximum-chain score.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=9,1 --fight
# Movement timing and distance, followed separately by the final boss's swoop and event handoff.
godot-dotnet --headless --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=13,1 --movement
godot-dotnet --headless --audio-driver Dummy --path . res://tests/field_contracts.tscn -- --install-root=/path/to/installation --room=27,1 --fight --swoop
# Disk resume and room/zone loads: gear, stats, inventory, progression and native variable lifetimes.
godot-dotnet --headless --fixed-fps 60 --audio-driver Dummy --path . res://tests/save_resume_contracts.tscn -- --install-root=/path/to/installation
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

AGPL-3.0-only. See [LICENSE](LICENSE). Vagrant Story is © Square Enix; this project contains none of its data.
