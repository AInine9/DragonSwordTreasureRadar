param(
    [Parameter(Mandatory = $true)]
    [string] $CompilerPath
)

$ErrorActionPreference = "Stop"
$sourceRoot = Join-Path $PSScriptRoot "..\third_party\ooz"
$outputPath = Join-Path $PSScriptRoot "..\vendor\ooz.exe"

& $CompilerPath `
    -O2 `
    -static `
    -std=c++14 `
    -Wno-writable-strings `
    -Wno-format `
    -include sys/stat.h `
    (Join-Path $sourceRoot "kraken.cpp") `
    (Join-Path $sourceRoot "bitknit.cpp") `
    (Join-Path $sourceRoot "lzna.cpp") `
    (Join-Path $sourceRoot "stdafx.cpp") `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw "ooz compilation failed with exit code $LASTEXITCODE."
}

Write-Output "Built: $outputPath"
