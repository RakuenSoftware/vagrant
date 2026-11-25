# Extracting the engine boundary

Vagrant owns Vagrant Story's rules, formats, game information and authoritative
state. wol-engine owns the game engine and Godot, including resource construction,
rendering, audio devices, input and platform integration. The intended destination
is game code that consumes plain .NET contracts and contains no Godot code.

## Current extraction

`gameplay/installation/CharacterConversion.cs` now decodes SHP/WEP models and SEQ
animations in `Vagrant.Gameplay`, alongside the other portable content decoders.
It returns the shared `Wol.Engine.Models.RiggedModel` contract, with no Godot or
backend references. Retail bone lengths, texture decoding and pass selection,
mounts, limb metadata, animation scripts, root movement and clip timing stay here.

`gameplay/FieldContent.cs` owns verified-disc lookups, game tables, model and
texture decoding, animation selection, and cache preparation. It receives the
plain .NET `IAssetBackend` interface. Another engine can implement that interface
without referencing Godot: compile `RiggedModel`, encode/decode `RasterImage`,
and supply a stable artifact compatibility identity. Cached model/image bytes
are opaque to Vagrant. Backend identities have separate cache directories, so
switching engines neither reads incompatible bytes nor deletes compatible caches.
Animation sets return typed `RigClip` data, including timing and loop flags.

`GodotAssetBackend` owns Godot model artifacts, template lifetime and actor
instances. `RiggedModel3D` constructs resources and binds late animation clips.
Vagrant's host selects the character or weapon and requested animation set; it
no longer imports glTF or repairs backend material/animation resources. Retail
vertex-colour gain is explicit `RigSurface.ColourMultiplier` data from the game
decoder, rather than a hidden engine convention.

`gameplay/title/TitleFlow.cs` owns the title's 60 Hz clock, accumulated pad edges,
logo/movie/menu timing, options, idle demo selection, card-result decisions and
field handoffs. `TitleMenuState` owns the retail wheel, brightness, warmth, glow
and palette arithmetic. The Godot scene reads that same mutable state and owns
only resources and device/scene bindings; it no longer runs another title flow.

`ITitleHost` is a Vagrant-owned presentation port that any engine can implement.
It uses typed buttons, card results and simple playback/UI/lifecycle operations;
no Godot node, resource, Dictionary or save payload crosses it. It is intentionally
specific to Vagrant's title. Reusable renderer/device capabilities belong in
wol-engine; title timing, menu choices and demo locations remain game information.
The renderer-free title tests cover these decisions without disc or renderer.
The installed `title_sequence_contracts.tscn` still checks the real Godot host.

This is an asset capability interface, not the complete game host interface.
Together with the title port, it proves content decoding and title decisions can
run under another engine; running the whole game also requires the remaining scene/input/audio boundaries below.

The vendored SDK contains `IAssetBackend`, `RiggedModel`, `GodotAssetBackend`
and `RiggedModel3D` on all three desktop targets. `addons/wol_engine/VENDOR.json` records the qualified upstream release
and archive checksums; ordinary builds use these binaries directly.

For future contract changes, qualify and publish the engine change, then vendor
its three desktop SDKs with the existing release tool before merging/releasing
the consumer. Do not relabel old SDK binaries or edit their provenance manifests.
During development, an unpacked new SDK can be selected with both
`WolEngineSdkRoot` and `WolEngineCoreRoot`:

```sh
dotnet build -m:1 -p:WolEngineSdkRoot=/path/to/sdk -p:WolEngineCoreRoot=/path/to/sdk/portable
dotnet run --project tests/gameplay/GameplayContracts.csproj -p:WolEngineCoreRoot=/path/to/sdk/portable
godot-dotnet --headless --path . res://tests/animation_script_contracts.tscn
# This integration check needs the player's verified installation.
godot-dotnet --headless --path . res://tests/title_sequence_contracts.tscn -- --install-root=/path/to/installation
```

Use the qualified editor/API for that SDK and its normal resource identities
under `addons/wol_engine`. The portable checks use a renderer-free backend and
need no installation or disc.
An optional `--disc=/path/to/disc.cue` (or `--installation=/path`) argument tests
real verified content, cold/warm caches, typed late clips and backend cache
isolation. It never supplies fallback content to the game. The headless engine
integration verifies model instances, material preservation and clip binding.

## Other consumers inspected

| Consumer | Existing boundary | Work still needed |
| --- | --- | --- |
| Wildside | `gameplay/Wild2.Gameplay.csproj` separates installation, formats and sound decoding from the backend. | `runtime/field/FieldRoomView.cs`, `FieldCameraRig.cs`, sound output and title/menu presentation still use Godot. |
| Xeno | Typed game state and shared flow execution are established; offline disc translation remains game-owned. | `Xeno.csproj` still compiles engine sources into the host; scene bindings and presentation expose Godot. |
| Saga | Its pinned portable core is already a separate assembly. | `installation/TitleConversion.cs`, `SystemSounds.cs`, `runtime/RetailContent.cs` and title presentation still mix content with Godot construction. |

All four need the same ownership rule. They can share model/device/presentation
contracts while keeping their formats, rules and information in their own games.
They do not need a universal game class or title-specific code inside wol-engine.

## Remaining Vagrant boundaries

1. Move shared title drawing, resource composition and input/device operations
   from the Godot host into engine presentation owners. The title rules and
   timing already live in the portable gameplay library.
2. Separate authoritative actors, battle rules, movement and event state from
   `scenes/field` presentation. Replace live Dictionary/Variant state with explicit
   game types at each boundary, deleting displaced execution paths.
3. Move reusable settings, audio output, camera/UI drawing and remaining Godot
   scene composition into engine owners. Keep the choices, layouts and other game
   information in Vagrant as typed data.

Each extraction must replace its production call sites and have a plain .NET
check plus a focused engine integration check. The full separation is not yet
complete; the content, asset and title-rule boundaries are implemented independently of Godot.
