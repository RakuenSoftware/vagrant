<!-- SPDX-License-Identifier: AGPL-3.0-only -->

# Desktop distribution

Project Vagrant includes the compiled wol-engine dependency in Git at
`addons/wol_engine`. This is an ordinary vendored directory, not a submodule.
A fresh clone contains the managed DLLs, native libraries, matching Godot API
DLLs and runtime resources for all three desktop targets. CI does not download
anything from private wol-engine and needs no engine access token.

`WolEngine.dll` contains managed .NET code. Native GDExtensions are `.so` on Linux,
`.dll` on Windows, and a universal Intel/Apple Silicon `.dylib` on macOS. The
managed/API assemblies in `platforms/<target>` retain the upstream platform
builds. Shared resources and native libraries keep stable Godot addon paths.
C# resource files are empty stubs; C# and C++ implementation sources are absent.
Managed assemblies remain inspectable/decompilable.

## Provenance and updates

`addons/wol_engine/VENDOR.json` records the upstream release URL, engine commit,
original SDK asset names/checksums, and hashes of every vendored file. Each
platform also retains its original RUNTIME.json from the SDK. Managed/native
binary bytes are copied unchanged. Vendored text file line endings
are normalized to LF, and C# resource stubs contain one newline.

The current SDK is the public `RakuenSoftware/wol-engine-release` release
`runtime-86b9cc7bad58554d6b5cccb96098649afdbbd2ed`. Native Linux, Windows and
universal macOS CI compiled and qualified each SDK from that exact private
engine tag. A separate job independently audited all three SDKs before
publication. Each archive and every member file has a verified SHA-256 record.
The game vendors the downloaded release assets; its CI never fetches or builds
private engine source.

This SDK plays PlayStation media natively from installed raw 2352-byte sectors:
`VideoStreamSTR` plays an STR movie's picture and XA sound in a `VideoStreamPlayer`, and
`AudioStreamXA` plays an XA ADPCM stream in an `AudioStreamPlayer`, so the game no longer
converts or bundles anything with FFmpeg. It retains the engine staying up when the window closes until the retired scenes are
freed: a scene still finishing its own work (the disc install) can hold its deletion,
and a room waits for its background "Rounded shapes" passes, superseded ones
included, before it is deleted, so quitting no longer crashes mid-pass. It retains
the installed content-pack set (`ContentPackSet`: packs are
re-verified on every scan, replace nothing until enabled, and a later enabled pack
wins) and whole-file pack assets (`ContentFormat.File`), which the game reads in
place of installed disc files while a pack is enabled. It also bounds the screen
echo, sends ordered materials and instance uniforms only when they change, and
rounds shapes edge by edge: "Rounded shapes" divides only the edges whose new
point changes the shape (1.6 times the original triangles over all rooms rather
than 7.5). It retains the portable disc/content-pack owners; the game does not use the
engine's `IMediaTools` boundary. Type forwarders preserve the previous
SDK’s disc/content-pack identities. It retains the host-neutral pixel contracts
and image/PNG codec API used by the portable content decoders. Its portable DLLs are
retained byte for byte from their respective platform archives. It retains controller motor
output, the reused-dialogue sizing fix and the four painted shader programs
in their existing compiled C# owners. There are no loose shader
implementations or owned debug symbols in the dependency update. Earlier
versions distributed those four shader files; removing them here does not
revoke existing downloads. Compiled assemblies, including their shader strings,
remain inspectable. Shared WoL artwork provides the startup splash, window icon
and native Windows/macOS export icons. The build-time editor, source checkouts,
SDK source caches and engine build tools are omitted from the vendored game.

The updater accepts exact `runtime-<commit>` release URLs from the public release
repository, retaining the original engine's exact runtime/resource release URLs
for older SDK provenance.

Only a maintainer updating the dependency downloads the SDKs. Download all three
SDK ZIPs and their adjacent `.zip.sha256` files from one published release. Preserve the old addon outside the checkout, then run:

```sh
python tools/release.py --vendor-runtime /path/to/linux-sdk.zip \
  /path/to/windows-sdk.zip /path/to/macos-sdk.zip
```

The updater checks archive/file hashes, matching commits and targets, compares
shared resources, rejects implementation source, and writes a new vendored tree.
Dependency updates reject loose shader programs as implementation source.
It refuses to overwrite an existing addon. Review and commit that directory and
VENDOR.json together. CI never runs this maintainer operation.

## Engine-independent gameplay

`gameplay/Vagrant.Gameplay.csproj` is an ordinary .NET 10 library. It references
only .NET and the host-neutral `WolEngine.Core` contracts. It does not reference
Godot or the `WolEngine` backend assembly. The application composes these rules
and decoded pixel planes with the host at its presentation boundary.

Vagrant owns the controller's two-channel envelopes and timing, and the retail
content decoders under `gameplay/installation`: title/loading art, fonts, HUD,
manuals, overlays, effects, map geometry/textures/skies, inventory, save pictures,
results, event-script translation, disc installation and ending
decompression. The retail digest catalog is embedded in the gameplay assembly
from `gameplay/installation/retail_digests.json`; it does not use Godot resource
loading. Images are decoded into byte-defined raster
planes. wol-engine owns hardware controller output, native image creation and
PNG cache encoding/decoding. Its portable core also owns generic disc reading,
digest verification and data-only content-pack installation. The game supplies
its retail catalog. The opening movie and the ending's XA music play natively
from their installed raw sectors through the engine's `VideoStreamSTR` and
`AudioStreamXA`; no external media tool is bundled or run. There is one decoder per format and one owned image
buffer; no parallel Godot decoder or second image cache remains.

Run the deliberate-input contracts with the .NET SDK and vendored portable DLLs:

```sh
dotnet run --project tests/gameplay/GameplayContracts.csproj -c Release
```

CI runs these checks on Linux, Windows and macOS in sparse checkouts containing
only this library, its tests, the pinned .NET version, target-selection props
and each SDK’s `portable` binaries.
Godot, the backend SDK, native libraries and a disc installation are absent.
The checks reject backend dependencies both in gameplay and transitively in the
portable core, and exercise palette keys, transparency passes, indexed/direct
art, save-picture palette columns, glyph metrics, controller envelopes, install rejection preserving previous
files/cache, and XA integrity.
Native player builds wait for these checks and retain the published core DLL
byte for byte. Vendoring preserves each target’s portable core bytes, alongside its other
managed/API assemblies. `Directory.Build.props` selects the native target for
both the host and the plain gameplay project. The core has no Godot dependency;
platform SDKs may differ in .NET generator build metadata.

The host still prepares the installation’s decoded room/rig cache with the
existing orchestration; this step constructs rendering resources. No renderer is
needed to verify/copy disc files in the portable installer.

Other game systems, including character rendering, audio playback and scenes,
still contain Godot dependencies and remain to be extracted. This is an
incremental boundary, not a claim that the whole application is portable yet.

## Build and CI

Use Python 3.12+ and the pinned .NET SDK 10.0.403 (`global.json` uses
`latestPatch`; earlier 10.0 feature bands do not qualify). No engine credentials,
SCons, C compiler or media toolchain are needed.

The preflight asks the .NET resolver to select the SDK in the project directory
before downloading Godot, creating player output or exporting. It reports all
missing prerequisites together.

Both Git checkouts and GitHub source ZIPs are supported. Checkout provenance is
read before the expensive work; missing Git or unreadable checkout metadata
fails early. Archive builds use `game_source: "archive"`, `game_commit: null`
and `game_worktree_dirty: null` rather than claiming a known revision or failing
at the end. Git builds use `game_source: "git"`, their actual commit and a boolean
dirty state. Source archives are not treated as their parent directory's checkout.

On the native host:

```sh
python tools/release.py linux-x64 --version development
# Windows host: windows-x64; macOS host: macos-universal.
```

The release owner verifies vendored file hashes, downloads the matching official
Godot 4.7.2 .NET editor and export templates with SHA-512 verification, and
replaces the editor's API with the vendored API DLLs. macOS preserves the public
editor's own entitlements and ad-hoc signs the modified app. Engine source is
never compiled or fetched. `--prepare` performs this setup without exporting.
Direct `dotnet build` selects the vendored assemblies for the host OS.

Exports use an isolated temporary project containing only game source and the
selected binary engine dependencies. Output defaults to `dist/<target>`;
`--output` must name an empty directory. Debug and ExportRelease compiler
warnings are errors; solution Release maps to ExportRelease.

`.github/workflows/desktop.yml` builds and tests on native hosted Linux, Windows
and macOS runners for PRs into main, pushes to main, manual runs and `v*` tags.
Checkout is an ordinary non-recursive clone using its repository token. PR and
manual runs upload game archives/checksums as CI artifacts. Every merge to main
also publishes a preview named `v0.1.0-preview.main.<workflow run number>`, with
its release tag pointing to the exact merged commit. These automatic previews
use a separate name from manually versioned `v*` releases, which remain supported.
Publication requires all three native jobs and downloaded checksum checks to
pass. A failed platform prevents publication. PR checks do not publish releases. Releases inherit
the repository visibility; this workflow does not make a private repository public.
Automatic reviews remain enabled.

## Player contents and verification

Players receive the game, compiled managed/native engine, self-contained
Godot/.NET runtime, resources and notices. They need no .NET installation, SDK,
Godot editor or engine checkout. The editor, unused platform dependencies,
tests, source, symbols and developer caches are excluded from the player.

The audit verifies the exact vendored Release WolEngine.dll and matching
GodotSharp API. It rejects retail formats derived from the installer catalog,
known retail file hashes, PS-X executable headers and raw CD-sector signatures,
including files renamed to innocuous extensions. The exported-player check
applies corresponding checks inside the PCK; Godot's generated UID index is
allowed as metadata. These checks do not establish legal clearance or detect
every possible modified/decoded asset. Each native CI job runs the actual exported executable's
`--verify-package` check with an empty installation, verifies the separate
compiled engine/native audio, and rejects test types and implementation source
in the PCK. Archives contain BUILD.json with source provenance, game/engine revisions, upstream
SDK checksum, native hashes and game dirty state, plus a SHA-256 sidecar. Archive
builds explicitly mark unknown game revision/dirty state with null values. These are
provenance checks, not a bit-for-bit reproducibility claim.

No retail assets, disc images, BIOS, installed files or decoded caches are
bundled. A verified owned US disc is required. Project Vagrant remains AGPL-3.0-only;
its game source is publicly downloadable. macOS packages are ad-hoc signed and need
Developer ID signing/notarization for public friction-free downloads. Credentials
stay outside Git. Linux CI builds on Ubuntu 22.04 for host-library compatibility.

The display name is Project Vagrant. Archives use `ProjectVagrant-<version>-<target>.zip`;
Linux and Windows launchers are `ProjectVagrant.x86_64` and `ProjectVagrant.exe`,
and the macOS application is `Project Vagrant.app`. The original user-data directory
and macOS bundle identifier are retained so existing installations, saves and
preferences remain available after upgrading. Internal C# assembly names remain stable.

