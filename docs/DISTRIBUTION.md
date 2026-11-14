<!-- SPDX-License-Identifier: AGPL-3.0-only -->

# Desktop distribution

Players download a compiled player, not this repository or an engine checkout. The
player includes Vagrant's managed assembly (which also contains the consumed engine
managed code), Godot/.NET, the platform's compiled native engine library, resources
and notices. C# resources inside the PCK are newline stubs required by Godot, with
no source text. Managed binaries remain inspectable/decompilable.

Vagrant remains AGPL-3.0-only and its game source remains publicly downloadable.
This work does not change the licence. The engine is consumed from its private,
pinned repository; its source is not copied into player archives. Required Godot,
.NET, godot-cpp and SPU notices accompany the binaries. No retail assets, disc
images, BIOS, installed-disc files or decoded caches are distributed.

## Build

Use Godot 4.7.2 .NET, .NET SDK 10.0.403, Python 3.12+, SCons 4.11.1 and a C++17
compiler. Initialize the private engine and its pinned godot-cpp submodule:

```sh
git submodule update --init --recursive
python -m pip install scons==4.11.1
python tools/release.py linux-x64 --version development
# On a Windows build host:
python tools/release.py windows-x64 --version development
# On macOS, builds Intel + Apple Silicon native and managed runtimes:
python tools/release.py macos-universal --version development
```

The build downloads the matching official .NET editor and templates and checks
their SHA-512 against the release checksums. The engine owns native compilation
and qualification of the patched Godot API. Exports use that qualified editor and
Release API, with warnings treated as errors.

`--godot=/path/to/original/editor` uses an existing editor distribution, while
`--scons-python=/path/to/python` selects a SCons environment. `--skip-native` uses an
already-built native library and qualified API; it does not replace qualification.
Output defaults to `dist/<target>`. Use `--output` for another empty directory;
existing files are never silently removed.

A temporary project copies only game runtime/scenes/install decoders and engine
runtime/native binaries/API dependencies. Tests, local data, developer tools and
unrelated engine source never enter export staging. ExportRelease also excludes
test classes at compilation. The package audit rejects loose source/symbols/disc
files and verifies that exported GodotSharp assemblies match the qualified API.
On the matching host the actual exported player runs `--verify-package` against
an empty install: it checks compiled native audio presence, absence of test types,
and PCK resources, without a disc or source checkout.

Each archive has BUILD.json with game/engine commits, dirty-state flags, target,
Godot version and native hashes, plus a SHA-256 sidecar. Development archives may
record a dirty worktree; publish release tags from clean, reviewed commits. This
is integrity/provenance information, not a bit-for-bit reproducibility claim.

## CI and publication

`.github/workflows/desktop.yml` builds on native Linux, Windows and macOS runners
when dispatched or a `v*` tag is pushed. It uploads only audited player archives
and checksums, never the engine checkout, staging project or SDK cache. Automatic
reviews remain enabled. Configure a read-only `WOL_ENGINE_READ_TOKEN` with access
to the private engine; the default repository token usually cannot fetch a
separate private repository. No token value belongs in Git or an archive.

The workflow creates downloadable CI artifacts. It does not publish a GitHub
Release automatically. Promote the three verified archives/checksums to the
chosen public release after native job results are green. macOS development
exports use ad-hoc signing; public friction-free downloads require Developer ID
signing and notarization on the macOS release host. Signing credentials remain
outside Git. Linux release CI builds on Ubuntu 22.04 to avoid unnecessarily new
host-library requirements.

FFmpeg and ffprobe remain external first-install dependencies and must be on
PATH. They are not silently copied from a developer machine. The player needs
neither an editor, engine sources, .NET SDK nor build tools. Native macOS and
Windows qualification is performed by their CI jobs; a Linux export alone does
not establish those platforms work.

## Code hygiene

Debug and ExportRelease builds treat warnings as errors. The solution's Release
configuration maps to ExportRelease, instead of the historical Debug mapping.
Compiler symbol-hiding warnings and nullable shader/resource accesses are fixed
in their owning types. Native libraries and managed APIs are pinned; the addon
is a real Git submodule, not a machine-specific symlink. Disc data, exports, SDKs,
logs and Python caches are ignored. Godot C# UID sidecars are versioned as source
metadata. Gameplay and menu changes still require their focused contracts;
packaging checks are not an exhaustive architecture or retail-parity audit.
