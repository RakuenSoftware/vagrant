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

The source is the published `runtime-562646427f09ed44b7eff0a1f67f8e2e5742b148`
release. Its Linux, Windows and universal macOS SDKs passed native CI builds and
source-free consumer smoke tests before publication. The runtime payloads here
were extracted from those SDK archives; the build-time editor was omitted.
No private engine source checkout, SDK source cache or build tools are vendored.

Only a maintainer updating the dependency needs access to private engine
releases. Download all three SDK ZIPs and their adjacent `.zip.sha256` files from
one published release. Preserve the old addon outside the checkout, then run:

```sh
python tools/release.py --vendor-runtime /path/to/linux-sdk.zip \
  /path/to/windows-sdk.zip /path/to/macos-sdk.zip
```

The updater checks archive/file hashes, matching commits and targets, compares
shared resources, rejects implementation source, and writes a new vendored tree.
It refuses to overwrite an existing addon. Review and commit that directory and
VENDOR.json together. CI never runs this maintainer operation.

## Build and CI

Use Python 3.12+ and .NET SDK 10.0.403. No engine credentials, SCons or C++
compiler are needed. On the native host:

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
and macOS runners for PRs into main, manual runs and `v*` tags. Checkout is an
ordinary non-recursive clone using its repository token. PR and manual runs upload game archives/checksums as CI artifacts. A `v*` tag
publishes a versioned preview under GitHub Releases only after all three native
jobs pass and the downloaded archive checksums are verified. Releases inherit
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
in the PCK. Archives contain BUILD.json with game/engine revisions, upstream SDK
checksum, native hashes and game dirty state, plus a SHA-256 sidecar. These are
provenance checks, not a bit-for-bit reproducibility claim.

No retail assets, disc images, BIOS, installed files or decoded caches are
bundled. A verified owned US disc is required. Project Vagrant remains AGPL-3.0-only;
its game source is publicly downloadable. FFmpeg and ffprobe remain external
first-install dependencies on PATH. macOS packages are ad-hoc signed and need
Developer ID signing/notarization for public friction-free downloads. Credentials
stay outside Git. Linux CI builds on Ubuntu 22.04 for host-library compatibility.

The display name is Project Vagrant. Archives use `ProjectVagrant-<version>-<target>.zip`;
Linux and Windows launchers are `ProjectVagrant.x86_64` and `ProjectVagrant.exe`,
and the macOS application is `Project Vagrant.app`. The original user-data directory
and macOS bundle identifier are retained so existing installations, saves and
preferences remain available after upgrading. Internal C# assembly names remain stable.

## Bundled media conversion

Each native CI runner builds FFmpeg 9.0.2, libogg 1.3.6, libvorbis 1.3.7 and
libtheora 1.2.0 from the exact source archives pinned by SHA-256 in
`distribution/media-sources.json`. `tools/build_media.sh` owns their compiler
configuration. GPL, nonfree, networking and unrelated codecs are disabled;
FFmpeg is LGPL-2.1-or-later and the Xiph libraries retain their BSD-style licences.
Windows uses MSYS2's MinGW compiler with static runtime linkage; macOS builds
Intel and Apple Silicon executables and combines them with lipo.

`ffmpeg` and `ffprobe` live in `media/` beside the player executable, including
inside `Contents/MacOS/` in the macOS app. The app is re-signed after adding them.
The installer resolves this location first; only development/editor sessions may
fall back to PATH. Packaged child processes run with PATH cleared. Native CI
verifies that both game-input decoders are present and actually encodes synthetic
Theora video/stereo Vorbis audio, then probes the result. No game files are used.

Every release also provides `ProjectVagrant-media-sources.zip` with all four
original source tarballs, their digest manifest, the exact build scripts and
instructions. Tool licences and configuration are included in `media/licenses/`,
and `media/MEDIA.json` records source and binary hashes. The separate media-source
archive contains no engine implementation or retail game data. Rebuild just the
tools with `python tools/release.py <native-target> --media-only --output <dir>`.
