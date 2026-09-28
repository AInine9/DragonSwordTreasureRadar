# Build v2.0.0

Requirements: Windows PowerShell, the Windows x64 .NET Framework 4 C# compiler,
and an x64 llvm-mingw toolchain (`clang++.exe`).

From the repository root:

```powershell
.\tools\get-sqlcipher.ps1
$env:PATH = "C:\llvm-mingw\bin;" + $env:PATH
.\build-inprocess.ps1 -CompilerPath 'C:\llvm-mingw\bin\clang++.exe'
```

Adjust the compiler path to your installation. Output:
`dist\inprocess\Mods\DragonSwordTreasureNative`.

When building the corresponding source included in a release archive, the
bundled SQLCipher runtime can be used without downloading it again:

```powershell
New-Item -ItemType Directory -Path vendor -Force
Copy-Item ..\Mods\DragonSwordTreasureNative\e_sqlcipher.dll vendor\
.\build-inprocess.ps1 -CompilerPath 'C:\llvm-mingw\bin\clang++.exe'
```

The build compiles the native bridge and included ooz sources into
`radar_native.dll`, compiles the shared catalog/save reader into
`DragonSwordRadar.Managed.dll`, and copies the Lua scripts and marker images.
It does not generate or bundle game-derived data.
