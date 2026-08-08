param(
    [string]$Version = "",
    [string]$SigningCertificateThumbprint = "",
    [string]$TimestampUrl = "https://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

$OutputRoot = Join-Path $PSScriptRoot "dist"
$compiler = Join-Path $env:WINDIR `
    "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
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
$archiveChecksum = $archive + ".sha256"
$generatedRoot = Join-Path $OutputRoot "generated"

function Resolve-BuildVersion {
    param([string]$RequestedVersion)

    $informationalVersion = $RequestedVersion.Trim()
    if ($informationalVersion.Length -eq 0) {
        $informationalVersion = (& git -C $PSScriptRoot describe `
            --tags --always --dirty 2>$null).Trim()
        if ($LASTEXITCODE -ne 0 `
            -or $informationalVersion.Length -eq 0) {
            throw "Specify the release version with -Version, for example -Version 1.6.2."
        }
    }

    $informationalVersion = $informationalVersion.TrimStart("v")
    if ($informationalVersion -notmatch `
        '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.(?<revision>\d+))?(?:[-+][0-9A-Za-z.+-]+)?$') {
        throw "Invalid build version: $informationalVersion"
    }

    $revision = if ($Matches.revision) {
        $Matches.revision
    }
    else {
        "0"
    }

    return [PSCustomObject]@{
        Assembly = "{0}.{1}.{2}.{3}" -f `
            $Matches.major,
            $Matches.minor,
            $Matches.patch,
            $revision
        Informational = $informationalVersion
    }
}

function Write-AssemblyInfo {
    param(
        [string]$Path,
        [string]$Title,
        [string]$Description,
        [string]$Product,
        [PSCustomObject]$BuildVersion
    )

    $source = @"
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("$Title")]
[assembly: AssemblyDescription("$Description")]
[assembly: AssemblyCompany("AInine")]
[assembly: AssemblyProduct("$Product")]
[assembly: AssemblyCopyright("Copyright (c) AInine")]
[assembly: AssemblyVersion("$($BuildVersion.Assembly)")]
[assembly: AssemblyFileVersion("$($BuildVersion.Assembly)")]
[assembly: AssemblyInformationalVersion("$($BuildVersion.Informational)")]
[assembly: ComVisible(false)]
"@

    [System.IO.File]::WriteAllText(
        $Path,
        $source,
        [System.Text.UTF8Encoding]::new($false))
}

function Find-SignTool {
    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path -LiteralPath $kitsRoot)) {
        return $null
    }

    return Get-ChildItem -LiteralPath $kitsRoot -Directory |
        Sort-Object Name -Descending |
        ForEach-Object {
            Join-Path $_.FullName "x64\signtool.exe"
        } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}

function Sign-Binary {
    param(
        [string]$SignTool,
        [string]$Thumbprint,
        [string]$Path
    )

    & $SignTool sign `
        /sha1 $Thumbprint `
        /fd SHA256 `
        /tr $TimestampUrl `
        /td SHA256 `
        $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed for $Path."
    }

    & $SignTool verify /pa $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification failed for $Path."
    }
}

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
    $scriptsRoot, $toolsRoot, $sourceRoot, $generatedRoot `
    -Force | Out-Null

$buildVersion = Resolve-BuildVersion $Version
$overlayAssemblyInfo = Join-Path $generatedRoot `
    "OverlayAssemblyInfo.cs"
$installerAssemblyInfo = Join-Path $generatedRoot `
    "InstallerAssemblyInfo.cs"

Write-AssemblyInfo `
    -Path $overlayAssemblyInfo `
    -Title "DragonSword Treasure Radar" `
    -Description "External treasure radar for DragonSword: Awakening" `
    -Product "DragonSword Treasure Radar" `
    -BuildVersion $buildVersion
Write-AssemblyInfo `
    -Path $installerAssemblyInfo `
    -Title "DragonSword Treasure Radar Installer" `
    -Description "Installer for DragonSword Treasure Radar" `
    -Product "DragonSword Treasure Radar" `
    -BuildVersion $buildVersion

$overlaySources = @(
    Get-ChildItem `
        -LiteralPath (Join-Path $PSScriptRoot "src\overlay") `
        -Filter "*.cs" `
        -Recurse |
        Sort-Object FullName |
        Select-Object -ExpandProperty FullName
    $overlayAssemblyInfo
)
$installerSources = @(
    Get-ChildItem `
        -LiteralPath (Join-Path $PSScriptRoot "src\installer") `
        -Filter "*.cs" `
        -Recurse |
        Sort-Object FullName |
        Select-Object -ExpandProperty FullName
    $installerAssemblyInfo
)

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

if (-not [String]::IsNullOrWhiteSpace(
    $SigningCertificateThumbprint)) {
    $signTool = Find-SignTool
    if (-not $signTool) {
        throw "signtool.exe was not found. Install the Windows SDK before signing."
    }

    Sign-Binary `
        -SignTool $signTool `
        -Thumbprint $SigningCertificateThumbprint `
        -Path (Join-Path $modRoot "DragonSwordTreasureRadar.exe")
    Sign-Binary `
        -SignTool $signTool `
        -Thumbprint $SigningCertificateThumbprint `
        -Path $installer
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

$archiveHash = (Get-FileHash `
    -LiteralPath $archive `
    -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(
    $archiveChecksum,
    "$archiveHash *$(Split-Path -Leaf $archive)`n",
    [System.Text.ASCIIEncoding]::new())

Write-Output "Built: $installer"
Write-Output "Archive: $archive"
Write-Output "Checksum: $archiveChecksum"
Write-Output "Version: $($buildVersion.Informational)"
if ([String]::IsNullOrWhiteSpace(
    $SigningCertificateThumbprint)) {
    Write-Warning "The executables are unsigned. Authenticode signing is recommended for public releases."
}
