<!-- SPDX-License-Identifier: AGPL-3.0-only -->

# Desktop distribution

Vagrant vendors a pinned **binary wol-engine release**. It does not compile the
engine's sources. `WolEngine.dll` is managed .NET code shared by the desktop
platforms; each platform's SDK also provides its native GDExtension: `.so` on
Linux, `.dll` on Windows, and a universal Intel/Apple Silicon `.dylib` on macOS.

The game references the precompiled engine DLL. Godot resources keep their addon
paths through empty C# resource stubs, shaders and scenes. The first game autoload
registers the engine's script types before Godot creates engine autoloads. The
engine implementation is absent from both the binary SDK and the player archive.
Managed assemblies remain inspectable/decompilable.

## Engine release

The source gitlink at `addons/wol_engine` identifies the required engine commit;
Vagrant CI never initializes that submodule. Engine CI owns compilation and
publishes a release named `runtime-<full-engine-commit>` when that tag is pushed.
The release is published only after all three native SDK jobs succeed. Each job
also tests a consumer containing no engine implementation sources before uploading.

Each release contains `wol-engine-<commit>-<target>.zip` plus its SHA-256 sidecar,
for `linux-x64`, `windows-x64`, and `macos-universal`. SDKs contain the managed
Debug/Release assembly, native library, resource stubs, matching API binaries,
qualified build-time editor, notices, and a hashed RUNTIME.json manifest. The
editor is a build dependency and is excluded from the shipped game.

The private engine's release downloads need an Actions secret
`WOL_ENGINE_READ_TOKEN` with Contents: read access to wol-engine. This credential
is used to download release assets, not to fetch an engine source checkout.
The normal Vagrant repository token handles the game checkout. No credentials
belong in a source file or a player archive.

## Game build

Vagrant needs Python 3.12+, .NET SDK 10.0.403 and the binary SDK for the native
host platform. It does not need SCons, a C++ toolchain, or an engine source checkout.
For example, after downloading the pinned Linux SDK and its checksum from the
engine release:

```sh
python tools/release.py linux-x64 --version development \
  --engine-package /path/to/wol-engine-<commit>-linux-x64.zip
```

Use `windows-x64` on Windows or `macos-universal` on macOS. The release tool checks
the archive checksum, per-file manifest, pinned commit, target and Godot version.
It exports from an isolated temporary project containing game source and binary
engine dependencies. It downloads verified official .NET export templates;
compilation and export use the SDK's qualified editor/API pair. Output defaults
to `dist/<target>`; `--output` must name an empty directory.

`.github/workflows/desktop.yml` performs this download and build on native hosted
Linux, Windows and macOS runners for PRs into main, manual runs and `v*` tags.
It uploads only game archives/checksums. Engine SDKs and private checkout data
are never uploaded as Vagrant artifacts. There is no automatic public game Release
publication. Automatic reviews remain enabled.

## Player contents and verification

Players receive the game, compiled managed/native engine, self-contained
Godot/.NET runtime, resources and notices. They need no .NET installation, SDK,
Godot editor or engine checkout. No retail assets, disc images, BIOS, installed
files or decoded caches are bundled. A verified owned US disc is still required.
Vagrant remains AGPL-3.0-only and its game source remains publicly downloadable.

The audit rejects loose implementation source, symbols and disc data. On each
native CI host the actual exported executable runs `--verify-package` with an
empty installation, verifies the separate compiled engine and native audio,
and rejects test types and implementation sources in the PCK. C# resource stubs
are whitespace only. Archives include BUILD.json with game/engine revisions,
engine-package checksum, native hashes and game dirty state, plus a SHA-256
sidecar. This is provenance information, not a bit-for-bit reproducibility claim.

FFmpeg and ffprobe remain external first-install dependencies on PATH. macOS
packages use ad-hoc signing; public friction-free downloads require Developer ID
signing and notarization on the release host. Credentials remain outside Git.
Linux release CI builds on Ubuntu 22.04 for host-library compatibility.

## Code hygiene

Debug and ExportRelease builds treat compiler warnings as errors; solution
Release maps to ExportRelease. Packaging excludes tests, developer output and
private disc data. Godot C# UID sidecars are source metadata. Focused contracts
cover gameplay changes; package checks are not an exhaustive architecture or
retail-parity audit. CI qualification is only complete when all native jobs pass.
