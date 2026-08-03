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
