# GenTranslationBundle.ps1
# Merge local FuckDalamudCN full table with the committed machine-translation seed.
# FDCN wins on conflict; seed entries not present in FDCN are kept (e.g. uninstalled plugin descriptions).
# Called by FFXIV_Plugin_Localizer.csproj GenTranslationBundle target at build time.
param(
  [Parameter(Mandatory = $true)]
  [string]$BundlePath,
  [Parameter(Mandatory = $true)]
  [string]$FdcnPath
)

$ErrorActionPreference = 'Stop'

$seed = @{}
if (Test-Path -LiteralPath $BundlePath) {
  $seedRaw = Get-Content -LiteralPath $BundlePath -Raw -Encoding UTF8 | ConvertFrom-Json
  foreach ($p in $seedRaw.PSObject.Properties) {
    if ($p.Value) { $seed[$p.Name.Trim()] = $p.Value.Trim() }
  }
}

$fdcnRaw = Get-Content -LiteralPath $FdcnPath -Raw -Encoding UTF8 | ConvertFrom-Json
$map = @{}
foreach ($plugin in $fdcnRaw.PSObject.Properties) {
  foreach ($field in $plugin.Value.PSObject.Properties) {
    $en = $field.Value.Original
    $zh = $field.Value.Translated
    if ($en -and $zh -and $en.Trim() -ne $zh.Trim() -and -not $map.ContainsKey($en.Trim())) {
      $map[$en.Trim()] = $zh.Trim()
    }
  }
}

foreach ($k in $seed.Keys) {
  if (-not $map.ContainsKey($k)) { $map[$k] = $seed[$k] }
}

$tmp = $BundlePath + '.tmp'
$map | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $tmp -Encoding UTF8
Move-Item -LiteralPath $tmp -Destination $BundlePath -Force

Write-Host "[GenTranslationBundle] bundle generated: $($map.Count) entries (FDCN $($fdcnRaw.PSObject.Properties.Count) plugins + seed fill)"
