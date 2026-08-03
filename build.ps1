$ErrorActionPreference = "Stop"

$OutputRoot = Join-Path $PSScriptRoot "dist"
$compiler = Join-Path $env:WINDIR `
    "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$overlaySources = Get-ChildItem `
    -LiteralPath (Join-Path $PSScriptRoot "src\overlay") `
    -Filter "*.cs" `
    -Recurse |
    Sort-Object FullName |
    Select-Object -ExpandProperty FullName
$installerSources = Get-ChildItem `
    -LiteralPath (Join-Path $PSScriptRoot "src\installer") `
    -Filter "*.cs" `
    -Recurse |
    Sort-Object FullName |
    Select-Object -ExpandProperty FullName
$luaSource = Join-Path $PSScriptRoot "src\ue4ss"
$overrideSource = Join-Path $PSScriptRoot `
    "src\resources\treasure_overrides.txt"
$sqlCipher = Join-Path $PSScriptRoot "vendor\e_sqlcipher.dll"
$ooz = Join-Path $PSScriptRoot "vendor\ooz.exe"
$payloadRoot = Join-Path $OutputRoot "payload"
$modRoot = Join-Path $payloadRoot "DragonSwordTreasureMap"
$scriptsRoot = Join-Path $modRoot "scripts"
$toolsRoot = Join-Path $OutputRoot "tools"
$sourceRoot = Join-Path $OutputRoot "source\ooz"
$installer = Join-Path $OutputRoot `
    "DragonSwordTreasureRadarInstaller.exe"
$archive = Join-Path $OutputRoot "DragonSwordTreasureRadar.zip"

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "The .NET Framework x64 C# compiler was not found: $compiler"
}
if (-not (Test-Path -LiteralPath $sqlCipher)) {
    throw "Run tools\get-sqlcipher.ps1 before building."
}
if (-not (Test-Path -LiteralPath $ooz)) {
    throw "Run tools\build-ooz.ps1 before building."
}
if (-not (Test-Path -LiteralPath $overrideSource)) {
    throw "The default treasure_overrides.txt file is missing."
}

if (Test-Path -LiteralPath $OutputRoot) {
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path `
    $scriptsRoot, $toolsRoot, $sourceRoot -Force | Out-Null

& $compiler `
    /nologo `
    /warn:4 `
    /target:winexe `
    /optimize+ `
    "/out:$(Join-Path $modRoot 'DragonSwordTreasureRadar.exe')" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    $overlaySources
if ($LASTEXITCODE -ne 0) {
    throw "Overlay compilation failed with exit code $LASTEXITCODE."
}

& $compiler `
    /nologo `
    /warn:4 `
    /target:winexe `
    /optimize+ `
    "/out:$installer" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Security.dll `
    /reference:System.Windows.Forms.dll `
    $installerSources
if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $sqlCipher -Destination $modRoot
Copy-Item -LiteralPath $ooz -Destination $toolsRoot
Copy-Item -LiteralPath `
    (Join-Path $luaSource "config.lua"), `
    (Join-Path $luaSource "main.lua"), `
    (Join-Path $luaSource "world_map.lua") `
    -Destination $scriptsRoot
Copy-Item -LiteralPath $overrideSource -Destination $modRoot
Copy-Item -LiteralPath `
    (Join-Path $PSScriptRoot "THIRD_PARTY_NOTICES.txt") `
    -Destination (Join-Path $modRoot "THIRD_PARTY_NOTICES.txt")
Copy-Item -LiteralPath `
    (Join-Path $PSScriptRoot "licenses\APACHE-2.0.txt") `
    -Destination (Join-Path $modRoot "LICENSE-APACHE-2.0.txt")
Copy-Item -LiteralPath `
    (Join-Path $PSScriptRoot "licenses\SQLCIPHER.txt") `
    -Destination (Join-Path $modRoot "LICENSE-SQLCIPHER.txt")
Copy-Item -LiteralPath `
    (Join-Path $PSScriptRoot "README.md"), `
    (Join-Path $PSScriptRoot "THIRD_PARTY_NOTICES.txt"), `
    (Join-Path $PSScriptRoot "licenses\GPL-3.0.txt") `
    -Destination $OutputRoot
Move-Item -LiteralPath `
    (Join-Path $OutputRoot "GPL-3.0.txt") `
    -Destination (Join-Path $OutputRoot "LICENSE-GPL-3.0.txt")
Copy-Item -Path `
    (Join-Path $PSScriptRoot "third_party\ooz\*") `
    -Destination $sourceRoot `
    -Recurse

if (Test-Path -LiteralPath `
    (Join-Path $OutputRoot "payload\DragonSwordTreasureMap\scripts\treasures.lua")) {
    throw "The release payload must not contain treasures.lua."
}

$archiveInputs = @(
    $installer
    (Join-Path $OutputRoot "README.md")
    (Join-Path $OutputRoot "THIRD_PARTY_NOTICES.txt")
    (Join-Path $OutputRoot "LICENSE-GPL-3.0.txt")
    $payloadRoot
    $toolsRoot
    (Join-Path $OutputRoot "source")
)
Compress-Archive `
    -LiteralPath $archiveInputs `
    -DestinationPath $archive `
    -CompressionLevel Optimal

Write-Output "Built: $installer"
Write-Output "Archive: $archive"
