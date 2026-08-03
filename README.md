# DragonSword Treasure Radar

Source code for an external treasure radar for DragonSword: Awakening.

The project uses a UE4SS Lua bridge to report player and map state to a
separate Windows overlay. The overlay places uncollected treasure markers over
the in-game minimap and world map, and reads the local save database in
read-only mode to hide collected treasures.

Release packages do not contain extracted game data, encryption keys, treasure
coordinates, save data, or game assets. Treasure-location metadata is generated
locally from the user's own game files during installation.

## Features

- Minimap and world-map treasure markers.
- Read-only save filtering for collected treasures.
- Live `.db` / `.bak` selection for save updates.
- Player and treasure Z-axis display for underground or elevated chests.
- Treasure acquisition-type labels derived from locally generated `UIDName`
  metadata.
- Distinct colors for MiniGame and Map treasure records.
- A magenta ring around the nearest treasure while preserving its type color.
- User-editable ignore and alias rules for duplicate or abandoned records.
- Detailed diagnostic logging when `debug_logging` is enabled.
- World-map recovery based on Pawn-loss detection instead of `LoadMap` hooks.

## Controls

- `F7`: enable and refresh the radar.
- `F8`: toggle the radar.

The keys can be changed in `scripts/config.lua`.

## Configuration

```lua
return {
    refresh_key = "F7",
    toggle_key = "F8",
    world_map_markers = true,

    -- Show player and treasure Z coordinates to help locate chests faster.
    show_height = true,
    show_treasure_types = true,
    debug_logging = false,
}
```

`show_height` controls the existing nearest-treasure coordinate pair and can
be disabled without changing its format:

```text
(player Z, treasure Z)
```

The pair helps distinguish underground, elevated, and overlapping treasure
locations more quickly.

`show_treasure_types` controls the short acquisition-type label. It is
independent from `debug_logging`.

## Treasure type labels

The short label combines an acquisition-type prefix with the identifier from
the locally generated `UIDName`.

| Prefix | UIDName category | Meaning |
|---|---|---|
| `U` | `Unlock` | Standard unlock or interaction treasure |
| `M` | `Monster` | Monster encounter treasure |
| `D` | `Dig` | Digging treasure |
| `K` | `Key` | Key-gated treasure |
| `MAP` | `Map` | Treasure-map record |
| `MG` | `MiniGame` | Minigame reward record |
| `PP` | `PressurePuzzle` | Pressure-plate puzzle treasure |
| `SP` | `StatuePuzzle` | Statue puzzle treasure |
| `TB` | Other or legacy name | Unclassified treasure record |

Examples:

```text
U_10222
K_10223
MG_11001
MAP_10101
PP_14001
```

These labels describe the acquisition mechanism, not item rarity.

## Marker colors

- White: all standard and unclassified treasure types.
- Green: `MiniGame` records.
- Orange: `Map` records.
- Magenta outer ring: the nearest treasure.

The nearest marker keeps its inner type color. For example, the nearest
minigame treasure uses a magenta ring with a green center.

## Treasure overrides

The installed mod includes `treasure_overrides.txt` in the mod root.

Ignore a treasure by its full save-state ID:

```text
ignore 10220122
```

Ignore by the short label shown by the radar:

```text
ignore U_10222
```

A short label can match multiple duplicate data rows. In that case, all
matching rows are ignored.

Map a duplicate source ID to the authoritative save-state ID:

```text
alias 11003 14016
```

Alias rules currently require full numeric IDs.

The default file includes two verified abandoned-record corrections:

```text
ignore 11003
ignore 10220122
```

`11003` is an offset duplicate that overlaps the authoritative `14016` chest
record. `10220122` is a known abandoned or inaccessible chest record.

The installer preserves an existing `treasure_overrides.txt` during upgrades.

## Debug logging

Set the following value in `scripts/config.lua`:

```lua
debug_logging = true,
```

The overlay writes `DragonSwordTreasureRadar.log` in the mod root. Diagnostic
entries include:

- selected save database and write time;
- opened-bit changes;
- short name, full save ID, UIDName, and GroupID;
- X, Y, and Z coordinates;
- horizontal and vertical distance;
- source and resolved save bits;
- nearby overlapping treasure records.

Debug logging does not control visible treasure type labels.

## Stability notes

The game can temporarily invalidate cached UObjects while loading a new world.
The Lua bridge therefore avoids `LoadMap` hooks. It detects temporary Pawn loss,
suspends world-map scanning, clears retained widget references, and resumes
after the configured delay.

The save database can also move between the active `.db` and `.bak` files. The
overlay checks both files in the active slot and reads the newest valid source.

## Repository layout

```text
src\installer\       Installer source
src\overlay\         External overlay source
src\resources\       Installed text resources
src\ue4ss\           UE4SS Lua source
third_party\ooz\     GPL-licensed ooz source
tools\               Dependency and build helpers
licenses\            Third-party license texts
```

## Building

1. Run `tools\get-sqlcipher.ps1`.
2. Run `tools\build-ooz.ps1`.
3. Run `build.ps1` from Windows PowerShell.

The build script produces the installer and release archive under `dist`.

## Third-party software

Third-party components and their licenses are documented in
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).

## Disclaimer

This is an unofficial project provided without warranty. It is not affiliated
with or endorsed by HOUND13 or the game's publishers.

## Link
https://www.nexusmods.com/dragonswordawakening/mods/63
I have not shared the GitHub URL anywhere other than Nexus Mods.
If you see it posted on any other site, please be aware that it was not shared by me.

## How to Build
### Requirements
- Windows PowerShell
- .NET Framework x64 C# compiler:
`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
  - A Windows x64 C++ compiler compatible with clang++The release build uses llvm-mingw

### Build Steps
- Clone the repository
- Download the Windows x64 SQLCipher runtime:
`powershell -ExecutionPolicy Bypass -File .\tools\get-sqlcipher.ps1`
- Build ooz.exe using a Windows x64 clang++ compiler:
```
powershell -ExecutionPolicy Bypass -File .\tools\build-ooz.ps1 `
    -CompilerPath "C:\llvm-mingw\bin\clang++.exe"
```
  - Replace the compiler path with the location of clang++.exe on your system.
- Build the overlay, installer, and release package:
`powershell -ExecutionPolicy Bypass -File .\build.ps1`
