param(
    [Parameter(Mandatory = $true)][string]$CompilerPath,
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'dist\inprocess')
)
$ErrorActionPreference = 'Stop'
$modRoot = Join-Path $OutputRoot 'Mods\DragonSwordTreasureNative'
$scriptsRoot = Join-Path $modRoot 'scripts'
New-Item -ItemType Directory -Path $scriptsRoot -Force | Out-Null
$ooz = Join-Path $PSScriptRoot 'third_party\ooz'
& $CompilerPath -shared -O2 -static -std=c++17 -Wno-writable-strings -Wno-format -include sys/stat.h `
    (Join-Path $PSScriptRoot 'src\inprocess\bridge.cpp') `
    (Join-Path $ooz 'kraken.cpp') (Join-Path $ooz 'bitknit.cpp') `
    (Join-Path $ooz 'lzna.cpp') (Join-Path $ooz 'stdafx.cpp') `
    -o (Join-Path $modRoot 'radar_native.dll')
if ($LASTEXITCODE -ne 0) { throw 'Native DLL compilation failed.' }
$sources = @(Get-ChildItem (Join-Path $PSScriptRoot 'src\installer') -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName)
foreach ($folder in @('SaveData','Configuration','Data','Models')) {
    $sources += @(Get-ChildItem (Join-Path $PSScriptRoot "src\overlay\$folder") -Filter *.cs | Select-Object -ExpandProperty FullName)
}
foreach ($file in @('Platform\NativeMethods.cs','Platform\GameProcessFinder.cs','Diagnostics\ErrorLog.cs')) {
    $sources += Join-Path $PSScriptRoot "src\overlay\$file"
}
$sources += Join-Path $PSScriptRoot 'src\inprocess\InProcessEntry.cs'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /define:INPROCESS /target:library /platform:x64 /optimize+ `
    "/out:$(Join-Path $modRoot 'DragonSwordRadar.Managed.dll')" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:System.Security.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Managed DLL compilation failed.' }
Copy-Item (Join-Path $PSScriptRoot 'vendor\e_sqlcipher.dll') $modRoot
Copy-Item (Join-Path $PSScriptRoot 'src\resources\treasure_overrides.txt') $modRoot
Get-ChildItem (Join-Path $PSScriptRoot 'src\inprocess\scripts') -Filter *.lua | Copy-Item -Destination $scriptsRoot
Copy-Item (Join-Path $PSScriptRoot 'src\inprocess\assets') -Destination $modRoot -Recurse -Force
Set-Content -LiteralPath (Join-Path $modRoot 'enabled.txt') -Value ''
Write-Output "Built EXE-free mod: $modRoot"
