SPDX-License-Identifier: AGPL-3.0-only

Vagrant — compiled desktop release

This archive includes Vagrant, its compiled wol-engine runtime, the matching
Godot/.NET runtime and required notices. No engine checkout, Godot editor,
.NET SDK or build tools are needed to play.

You must provide your own US Vagrant Story disc (SLUS-01040), as BIN/CUE.
No game files, images, music, BIOS or installed-disc cache are included.
The first launch verifies and installs your disc locally.

FFmpeg and ffprobe must be on PATH for the disc's video/audio conversion.
They are not bundled in this archive. Their absence is reported during install.
Keep the complete extracted folder together; do not move only the executable.

Linux: run Vagrant.x86_64 (chmod +x Vagrant.x86_64 if needed).
Windows: run Vagrant.exe.
macOS: unzip the application and run Vagrant.app. Public downloads should be
Developer ID signed and notarized; ad-hoc development packages are not notarized.

Vagrant source: https://github.com/RakuenSoftware/vagrant
Vagrant's licence is included as LICENSE. Third-party notices are in licenses/.
The binary download contains no wol-engine source. Managed binaries can be
inspected or decompiled; this package does not claim to prevent reverse engineering.
