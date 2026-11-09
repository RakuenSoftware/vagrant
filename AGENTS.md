# Project scope

- Vagrant is a C# game consumer of `RakuenSoftware/wol-engine`, pinned at `addons/wol_engine`. Runtime code is C# on Godot 4.7.2 .NET; no GDScript. Reusable engine behaviour belongs in wol-engine with one owner; game rules, content adapters and scenes belong here.
- Only players with their own US Vagrant Story disc (SLUS-01040) can play. The installer converts content from the player's disc at install time and verifies every consumed file against the retail digest. Never commit, ship, download or bundle anything from the disc, and never add fallback content that lets the game run without a verified disc.
- This is not a decompilation project. ser-pounce/rood-reverse is reference material for formats and behaviour only: do not copy its code, tooling, data, tables or text. Write independent implementations.
- Roadmap order: the title (done), every title menu item plus a Settings item like Xeno's, the opening scene playing and rendering correctly, then gameplay after it. Verify each step before starting the next.
- Do not add helper, utility or wrapper layers; put behaviour in its owning type.
- Logic checks run headless. Rendering and UI checks run on an isolated display (`gamescope --backend headless …`), never on the user's desktop.
- Disc images, installations, captures and run output stay local and ignored.
- License: AGPL-3.0-only. New files carry `SPDX-License-Identifier: AGPL-3.0-only`.
