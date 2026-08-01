# DragonSword Treasure Radar

Source code for an external treasure radar for DragonSword: Awakening.

The project uses a UE4SS Lua script to report game and map state to a separate
Windows overlay. The overlay positions uncollected treasure markers over the
in-game minimap and world map, and reads the local save database in read-only
mode to hide collected treasures.

Compiled packages are distributed through GitHub Releases. The repository and
release packages do not contain extracted game data, encryption keys, treasure
coordinates, save data, or game assets. Treasure-location data is generated
locally from the user's own game files during installation.

## Repository layout

```text
src\installer\       Installer source
src\overlay\         External overlay source
src\ue4ss\           UE4SS Lua source
third_party\ooz\     GPL-licensed ooz source
tools\               Dependency and build helpers
licenses\            Third-party license texts
```

## Third-party software

Third-party components and their licenses are documented in
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).

## Disclaimer

This is an unofficial project provided without warranty. It is not affiliated
with or endorsed by HOUND13 or the game's publishers.
