# DragonSword Treasure Radar

An unofficial treasure radar for DragonSword: Awakening.

**v2.0.0** displays uncollected treasure markers directly on the in-game minimap
and world map. It runs through UE4SS using DLLs and Lua; no separate overlay,
installer EXE, or companion process is required.

## Features

- White, green, and orange circle markers matching the original radar.
- A simple up/down arrow for the nearest treasure, hidden at similar height.
- Collected treasures disappear automatically using read-only save data.
- World-map markers follow panning and zooming, with updates every 50 ms.
- Treasure data is generated locally from your own game files and refreshed
  after game updates. Save-key discovery no longer depends on one fixed field offset.

## Requirements

- Windows x64 and DragonSword: Awakening (Steam).
- A working UE4SS installation compatible with the game.
- .NET Framework 4.x.

UE4SS is not bundled. The current release was tested on Steam build 25202218.
Future changes to the game, UE4SS, PAK files, or save format may require updates.

## Install or upgrade from v1.x

1. Close the game and the old external radar.
2. Disable the old mod in `DS\Binaries\Win64\Mods\mods.txt`:
   `DragonSwordTreasureMap : 0`. Stop using the old radar launcher.
3. Download `DragonSwordTreasureRadar-v2.0.0.zip` from
   [GitHub Releases](https://github.com/AInine9/DragonSwordTreasureRadar/releases/latest)
   and copy its `Mods` folder into `DS\Binaries\Win64`.
4. In `DS\Binaries\Win64\UE4SS-settings.ini`, set `GuiConsoleEnabled = 0`
   under `[Debug]`. The radar does not need the debug GUI; leaving it enabled
   can cause a UE4SS Live View freeze while moving.
5. Start the game, load a save, and open each region's world map for a few seconds
   on first use. Map settings are cached for later launches. Repeat this after
   a game update.

Press **F8** to show or hide markers. Distance labels from v1.x are not included.

To uninstall, close the game and remove `Mods\DragonSwordTreasureNative`.
Re-enable the old mod only when returning to v1.x.

## Compatibility

Minimap and world-map placement was checked at 1280×720, 1280×960, 1600×1000,
1920×810, 1920×1080, and 2560×1440. The world map was also checked with a
3840×2160 viewport, using internal coordinates and the visible screen area.
Other regions and long play sessions have not been exhaustively tested.

Save files are never modified. Release packages contain no extracted game data,
treasure coordinates, encryption keys, or user saves. Local cache files are
created on your computer.

## Source and licenses

See [BUILD.md](BUILD.md) for rebuilding the DLL-based release and
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) for third-party licenses.
Legacy installer and overlay sources remain in the repository; v2.0.0 uses
`src/inprocess` and shared data-reading code.

## Disclaimer

This is an unofficial community mod, not affiliated with or endorsed by HOUND13
or the game's publishers. It is provided without warranty. Use a vanilla game
installation for co-op.

## Nexus Mods

https://www.nexusmods.com/dragonswordawakening/mods/63

I have not shared the GitHub URL anywhere other than Nexus Mods.
If you see it posted on any other site, it was not shared by me.
