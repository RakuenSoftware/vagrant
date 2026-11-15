# WoL engine branding

These are the existing WoL shield/sword emblem and startup artwork previously
used by Xeno, based on RakuenSoftware/X's original `icon.svg`. The shared engine
owns the runtime copies so source and binary SDK consumers use the same assets.
The PNGs are deliberately retained: Godot reads the boot image before scripts
or imported resources are available.

Add these settings under `[application]` in a consumer's `project.godot`:

```ini
config/icon="res://addons/wol_engine/runtime/branding/wol_icon.png"
boot_splash/show_image=true
boot_splash/image="res://addons/wol_engine/runtime/branding/wol_splash.png"
boot_splash/bg_color=Color(0, 0, 0, 1)
boot_splash/stretch_mode=1
boot_splash/use_filter=true
```

Set Windows and macOS export presets' `application/icon` to the same icon PNG
so the exported executable/app also uses the emblem. Standalone engine projects
receive the equivalent root-relative paths from SConstruct. A game retains its
own application name, main scene and title sequence.

`wol.svg` is the emblem source; `wol_splash.svg` references it and adds the engine
name. To regenerate the existing PNGs with librsvg:

```sh
rsvg-convert -w 256 -h 256 wol.svg -o wol_icon.png
rsvg-convert -w 1920 -h 1080 wol_splash.svg -o wol_splash.png
```
