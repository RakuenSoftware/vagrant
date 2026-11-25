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

`Wol.Engine.RiggedModel3D` owns skeleton/skin, mesh, texture, material and animation
resource construction and glTF export. The shared contract and backend live in
wol-engine; there is no second model implementation in Vagrant. `FieldContent`
currently composes decoding and backend construction at the existing cache and
late-loaded animation boundaries. Existing cache and field-loading behavior is
preserved while that host integration is extracted in later changes.

This change requires a wol-engine SDK containing `RiggedModel` and
`RiggedModel3D`. The previously pinned SDK does not contain them. Qualify and
publish the paired engine change, then vendor its three desktop SDKs with the
existing release tool before merging/releasing the consumer. Do not relabel old
SDK binaries or edit their provenance manifests. During development, an unpacked
new SDK can be selected with both `WolEngineSdkRoot` and `WolEngineCoreRoot`:

```sh
dotnet build -m:1 -p:WolEngineSdkRoot=/path/to/sdk -p:WolEngineCoreRoot=/path/to/sdk/portable
dotnet run --project tests/gameplay/GameplayContracts.csproj -p:WolEngineCoreRoot=/path/to/sdk/portable
godot-dotnet --headless --path . res://tests/animation_script_contracts.tscn
```

Use the qualified editor/API for that SDK and its normal resource identities
under `addons/wol_engine`. The portable check uses synthetic models and sequences;
it needs no renderer, installation or disc. The headless integration check
verifies the existing phase handoffs and glTF round trip.

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

1. Separate `FieldContent`'s disc naming, lookup and decoded metadata from backend
   resources and cache preparation. It still returns Godot animations today.
2. Extract the title/menu rules, frame timing and input decisions from their
   Godot controls; shared presentation and input operations belong to wol-engine.
3. Separate authoritative actors, battle rules, movement and event state from
   `scenes/field` presentation. Replace live Dictionary/Variant state with explicit
   game types at each boundary, deleting displaced execution paths.
4. Move reusable settings, audio output, camera/UI drawing and remaining Godot
   scene composition into engine owners. Keep the choices, layouts and other game
   information in Vagrant as typed data.

Each extraction must replace its production call sites and have a plain .NET
check plus a focused engine integration check. The full separation is not yet
complete; the model decoder is the next completed boundary.
