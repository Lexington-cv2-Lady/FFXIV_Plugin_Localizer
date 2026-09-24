# DalamudPackager 15.0.0 builds latest.zip but does not include images/icon.png
# (the icon is copied to the staging folder, but the zip only gets dll/deps/manifest).
# This runs after DefaultDalamudPackagerRelease and deterministically adds the icon.
param(
    [Parameter(Mandatory=$true)][string]$ZipPath,
    [Parameter(Mandatory=$true)][string]$IconSrc
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ZipPath)) { Write-Host "[AddIconToZip] zip not found, skip: $ZipPath"; exit 0 }
if (-not (Test-Path -LiteralPath $IconSrc)) { Write-Host "[AddIconToZip] icon not found, skip: $IconSrc"; exit 0 }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$fs = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite)
try {
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Update)
    $exists = $false
    foreach ($e in $zip.Entries) { if ($e.FullName -eq 'images/icon.png') { $exists = $true } }
    if ($exists) {
        Write-Host "[AddIconToZip] images/icon.png already in zip, skip"
    } else {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $IconSrc, 'images/icon.png',
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        Write-Host "[AddIconToZip] added images/icon.png -> latest.zip"
    }
    $zip.Dispose()
} finally {
    $fs.Close()
}
