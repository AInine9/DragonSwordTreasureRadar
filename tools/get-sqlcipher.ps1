param(
    [string] $Version = "2.1.10"
)

$ErrorActionPreference = "Stop"

$packageName = "sqlitepclraw.lib.e_sqlcipher"
$packageUrl = "https://api.nuget.org/v3-flatcontainer/" `
    + "$packageName/$Version/$packageName.$Version.nupkg"
$vendorRoot = Join-Path $PSScriptRoot "..\vendor"
$destination = Join-Path $vendorRoot "e_sqlcipher.dll"
$temporaryRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    ("DragonSwordTreasureRadar-" + [Guid]::NewGuid().ToString("N"))
$packagePath = Join-Path $temporaryRoot "package.nupkg"
$extractRoot = Join-Path $temporaryRoot "package"

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    Invoke-WebRequest -Uri $packageUrl -OutFile $packagePath
    [System.IO.Compression.ZipFile]::ExtractToDirectory(
        $packagePath,
        $extractRoot
    )

    $source = Join-Path `
        $extractRoot `
        "runtimes\win-x64\native\e_sqlcipher.dll"
    if (-not (Test-Path -LiteralPath $source)) {
        throw "The Windows x64 SQLCipher runtime was not found in the package."
    }

    New-Item -ItemType Directory -Path $vendorRoot -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
    Write-Output "Installed: $destination"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
