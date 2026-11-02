# Remnant 2 Weakspot Helper

An external overlay for **Remnant 2** that shows every enemy's weakspots, plus what has to break before
a weakspot can be hit (armor plates, covers, cores). Optional extras: in-game outlines, enemy
silhouettes, aim assist, recoil control, and a few practice tools.

It runs as a separate window on top of the game. It does not modify game files, and every change it
makes inside the running game is undone when you turn the feature off or exit. Weakspot shapes are
read from the running game itself; no game data is included.

> **For educational purposes only.** This project was made entirely with AI, as an exploration of
> game memory structures, runtime reflection and overlay rendering. Use it in solo play or with friends
> who agree to it. It is an unofficial tool, not affiliated with Gunfire Games or Gearbox. Use at your
> own risk.

## Features

| Tab | Feature | Hotkey | What it does |
|---|---|---|---|
| Visuals | Show weakspots | F6 | Draws each weakspot's real hit shape. Plates or covers that must break first are drawn in orange. |
| Visuals | Outline weakspots in game | F10 | Uses the game's own outline effect on weakspots (three styles). |
| Visuals | Enemy silhouettes | F9 | Shows enemies through walls using the game's silhouette effect. |
| Combat | Every hit is a weakspot hit | F7 | Your hits count as weakspot hits. Starts off every launch. |
| Combat | Freeze enemies | F3 | Stops enemies in place. Starts off every launch. |
| Combat | Recoil control | F4 | Counters weapon recoil while you fire. |
| Combat | Auto-release charge weapons | | Releases fire at full charge (e.g. Monorail). |
| Aim | Aim assist | F5 | Turns your view toward a target's weakspot while the aim key (right mouse by default) is held. |

**Insert** shows or hides the menu. **End** restores the game and exits.
Hotkeys only work while the game is focused. A short notice appears on screen whenever a hotkey
turns something on or off.

## Requirements

- Windows 10 or 11 (64-bit)
- [.NET 10](https://dotnet.microsoft.com/download): the SDK to build, or the runtime to run a build
- Remnant 2 (Steam), **the exact game version this tool supports**. The tool checks the game
  executable on start and refuses to attach to any other version, because it reads memory at
  version-specific locations.
- The game should run in **borderless windowed** or **windowed** mode, so the overlay can sit on top.

## Building

The project has no package dependencies. With the .NET 10 SDK installed:

```powershell
.\build.ps1          # dist\WshOverlay and dist\WshOverlay-<version>.zip
```

or `dotnet build src\WshOverlay -c Release`.

## Usage

1. Start Remnant 2.
2. Run `WshOverlay.exe`. The menu opens in the top-left corner of the game.
3. Pick what you want in the **Visuals**, **Combat** and **Aim** tabs, then press **Insert** to hide the menu.
4. Press **End** when you are done. Everything the tool changed in the game is restored.

Settings are saved next to the exe: `ui_config.json` (visuals, window, performance), `aim_config.json`
(aim assist and recoil) and `logs\` (the 20 most recent session logs). To reset everything, delete
these files while the tool is closed.

## Troubleshooting

| Problem | Fix |
|---|---|
| "This version of Remnant 2 is not supported" | The game was updated. Wait for an updated version of the tool. |
| "Could not access the Remnant 2 process" | If the game runs as administrator, run the tool as administrator too. |
| The overlay is not visible | Switch the game to borderless windowed mode. |
| The game stutters | Settings → Performance: lower **Scan rate** and **Overlay FPS**. |

If the tool is killed instead of closed with End, start it again and press End. Leftover outlines are
removed at startup, and "every hit is a weakspot hit" turns itself off within two seconds of the tool
stopping.

## License

Public domain ([The Unlicense](LICENSE)): copy, modify, publish, use, sell or distribute it for any
purpose, with or without credit.
