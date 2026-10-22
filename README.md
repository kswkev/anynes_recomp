# AnyNES

Turns an NES ROM (`.nes`, or a `.zip` containing one) into a standalone Windows `.exe` that plays the game.

## Build

```powershell
.\build.ps1          # -> build\AnyNesBuilder.exe (+ build\AnyNesPlayer.exe, the stub it embeds)
.\build.ps1 -Tools   # also builds build\RomTest.exe (headless regression runner)
```

No SDK needed: it uses the C# compiler that ships with Windows (.NET Framework 4.x), and the
generated games run on any Windows 10/11 machine without installing anything.

## Use

Run `AnyNesBuilder.exe`, pick or drag in a ROM, adjust the title / icon frame, press **Build EXE**.
"Convert a folder..." builds every ROM in a directory.

Command line:

```
AnyNesBuilder --build <rom.nes|rom.zip> [out.exe] [title]
AnyNesBuilder --batch <rom dir> <out dir>
```

## How it works

The generated exe is a copy of the player runtime (`src/Player`) with three Win32 resources written
into it: the ROM package (`RCDATA "ANYNESGAME"`), an icon rendered from a title-screen capture, and
version info carrying the game title. At startup the player reads the package back out of its own
resources and runs it on the emulator core (`src/Core`): a cycle-stepped 6502, a dot-based PPU,
the APU plus expansion audio (MMC5 pulses + PCM, Namco 163 wavetable, Sunsoft 5B), and 30 mappers
covering every board in a typical complete USA set.

Player controls: arrows, Z = B, X = A, Right Shift = Select, Enter = Start (rebindable under
Options > Controls; XInput pads work automatically). F5/F8 save/load state, 1-4 pick a slot,
F11 or Alt+Enter fullscreen, F9 cycles the CRT scanline filter (View > Scanlines: off / light /
medium / strong; needs 2x or larger). Battery saves, states and settings live in `%LOCALAPPDATA%\AnyNES`.

Light-gun games: the Zapper sits in port 2 and is aimed with the mouse (a crosshair replaces the
cursor over the picture). Left-click shoots, right-click shoots away from the screen. Known Zapper
games (Duck Hunt, Hogan's Alley, Wild Gunman, Gumshoe, ...) turn it on automatically; for others
(Operation Wolf, Bayou Billy, Laser Invasion, ...) tick "Port 2: Zapper" in the builder, or use
Options > Zapper in the game window.

NTSC and PAL consoles are both emulated (PAL: 50 Hz, 312 lines, 3.2 PPU dots per CPU cycle, PAL APU
tables, swapped red/green emphasis). The region comes from an NES 2.0 header, else from the file
name: "(Europe)", "(Australia)" or a European country means PAL; "(USA, Europe)" and "(World)" stay
NTSC. It can be changed in the builder or under Options > Console region in the game window.
