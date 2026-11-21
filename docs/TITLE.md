# Title sequence

## Installation

The shared engine's `PlayStationDisc` (wol-engine `runtime/install/PlayStationDisc.cs`) opens the player's raw MODE2/2352 image: a BIN, or the single data track a CUE sheet names. `installation/RetailDisc.cs` is Vagrant Story's catalog for it:
- the volume `SLUS-01040`;
- the US boot line;
- the retail US SHA-256 of every file the installer consumes.

The engine verifies each file against the catalog before handing it over. Streamed files are hashed over raw sector bytes 16–2352, excluding the address header.

`RetailInstallation` keeps one installation: every disc file byte for byte under `disc/` and the disc's sector record in `install.json`. A new install is verified and copied into `staging/` and replaces the old one only when complete, so a rejected disc keeps the previous installation. `FieldContent` re-verifies each disc file against the retail digest the first time a session reads it. The title's pictures, fonts and HUD are decoded from those files when first needed; the opening movie and the ending music play from their raw sectors as they are.

The ending's XA music lies outside the ISO filesystem. The installer also preserves raw sectors 270000–303263
as `disc/ENDING/ENDING.XA`, verified against the US SHA-256 over the same sector bytes as other streams. Four
consecutive sectors carry each audio packet; the engine's `AudioStreamXA` plays one per group (interleave 4), as
the drive's audio decoder does, giving stereo 37.8 kHz audio lasting 443.52 seconds. The game re-verifies the raw
sectors before playing them; nothing is decoded ahead of time.
Older file-only installations are asked to reinstall from the owned disc.

`TitleConversion` reproduces the retail VRAM upload of the title overlay (`TITLE/TITLE.PRG`):

- A run-length stream holds a 544×576 block. Its first 512 rows are uploaded at (480,0) and its last 64 rows at (0,448). Each u32 control word has two counts: the low half is zero pixel-pairs, the high half is literal pixel-pairs that follow.
- From that VRAM image it exports:
  - the 512×448 interlaced display area at (512,16);
  - the two 160×64 menu panel pages;
  - the ten 4-bit menu words (as palette indices);
  - the ten glow sprites, using glow CLUT row 394.
- From the overlay data itself it exports the publisher and developer logos, the 180×24 copyright image and the four menu palettes (word dark, word lit, glow blue, glow gold).

The opening movie (`MOV/TITLE.STR`, 320×224 MDEC at 15 fps, with XA audio) plays through the engine's `VideoStreamSTR` from its raw sectors, picture and sound together. Its picture lasts 39.5 s, but its audio runs 74.8 s and continues as the title music: the title hides the player when the picture ends and keeps it playing.

## Flow and presentation

`TitleSequence` owns which screen is current, as a typed stage, and the title options:

- `publisher` → `developer` → `intro` → `menu`;
- from `menu`, the player's choice leads to `new_game` (then `field`), `continue` or `attract` (idle timeout); Continue and the attract mode return to `intro`;
- after the ending, `clear_save` offers the existing card SAVE screens before `intro` (TITLE.PRG 0x8007136c calls 0x8006edbc). The saved location is 48, **Game Clear Data**. Cancelling preserves the existing card. SAVE and LOAD use the title owner's resident menu sounds;
- the Sound and Vibration pickers set the title's own options.

It counts 60 Hz display frames as the retail title counts vertical blanks:

| Screen | Retail timing |
|---|---|
| Logos | 364 frames each, fading in and out over 32 frames. Holding any button cuts the developer logo short after its fade-in. Both logos appear only on the first title visit. |
| Intro picture | 2293 frames, or until Circle or Start is pressed. The music continues and is stopped 4401 frames after the movie started. |
| Copyright | Fades in over 17 steps of 2 frames. |
| Menu panel | Fades in over 9 steps of 4 frames, each followed by a menu update. |
| Menu updates | Run at 30 Hz. Holding a direction rotates the wheel: one rotation takes 10 updates. |
| Idle timeout | 1000 frames without input, and at least 100 frames after the music stops. |
| Leaving | A frozen frame darkened by a constant 8/255 subtraction every frame for 64 frames. |

`TitleMenu` models the wheel:

- **Slots:** words occupy 32-row slots on a 128-row drum (0 and 128 hidden, 32 above, 64 selected, 96 below). Each word's x position follows its drum row, which gives the wheel its slant. Words are clipped to x 176–336.
- **Brightness:** each drawn row is lit by its absolute drum row: rows 32–63 ramp up, 64–95 are full, 96–127 ramp down. Full-brightness rows draw opaque; the others are additive.
- **Colour:** the selected word's palette blends in sixteenths from dark to lit, and its additive glow is confined to the menu panel.
- **Option pickers:** in Vibration and Sound the heading blends from lit to gold and rises one slot, and its two options appear below it. Up, Down or Select toggles between them, Circle or Start confirms and Cross cancels.

## Added Settings item

Settings follows Sound on the wheel, which therefore has five words; words resting off the drum (rows 0 and 128) are dropped before a rotation so that the fifth word never keeps travelling. Its lettering is composed from the disc's (`TitleConversion.ComposeSettings`):

- The capital S and the small E, T, I and N are the disc's own glyphs.
- The title has no small S or G, so these are the disc's capital S and G, area-reduced to small-capital height and set one column apart.

It shares New Game's glow. Confirming Settings opens a dialog with the engine's `GraphicsSettings` panel (backed by the shared `DisplaySettings` autoload) and the game's master volume (`AudioSettings`). The title waits while the dialog is open, and waits again for the closing button to be released. Display preferences and volume persist in `user://settings.cfg`. Sound and Vibration reset to Stereo and On at every launch, as they do on the original at power-on.

With HDR 2D active (the shared display owner enables it on Forward+), the window's buffer holds linear colour. The capture path in the title contracts therefore encodes captures to sRGB before saving them.

## Idle demonstrations

The idle timeout starts the retail field demonstrations, using the same room and event owners as the story.
TITLE.PRG increments its title-visit counter at 0x8007138c. At 0x80071748 it selects 4 minus the counter's low bit;
0x80071ce0 maps those choices to zone 20/map 0 first, then zone 2/map 1, with state flags 13 and 14 set. Returning
from a demo advances the title visit and starts the intro again. A pad press, including a shoulder button, leaves
the demonstration. Publisher and developer logos show only on the initial visit; Sound retains its session value.

The title contract runs both demonstrations to their ends without input, checks their order and flags, then
interrupts another with L1. It runs headlessly and on an isolated display with captures. Menu sounds use the game's
sound driver, and Continue opens the memory-card LOAD screens and resumes the selected save.
