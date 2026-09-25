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

# ── 稳定输出（2026-09-26 修正）──
# 原写法 `$map | ConvertTo-Json | Set-Content` 把 [hashtable] 直接序列化：hashtable 的枚举顺序
# 受插入顺序影响，而 seed 又是从「上一次生成的包」读回来的 —— 于是「上次输出顺序 → 决定这次
# seed 顺序 → 决定这次输出顺序」形成自反馈振荡，每次构建条目顺序都在漂，git 工作区反复变脏。
# 修法两条：
#   ① 写出前按键做 Ordinal（二进制序、与区域文化无关）稳定排序 → 输出与输入顺序彻底解耦；
#   ② 内容未变就不重写文件，避免无谓的 mtime 抖动再触发下游增量构建。
# ⚠ 只改「顺序与写盘时机」，不动合并语义（hashtable 仍保持原有的大小写不敏感去重行为）。
$keys = [string[]]@($map.Keys)
[Array]::Sort($keys, [System.StringComparer]::Ordinal)
$sorted = [ordered]@{}
foreach ($k in $keys) { $sorted[$k] = $map[$k] }

$json = $sorted | ConvertTo-Json -Depth 3
$existing = $null
if (Test-Path -LiteralPath $BundlePath) {
  $existing = Get-Content -LiteralPath $BundlePath -Raw -Encoding UTF8
}

# 只比内容：行尾 / 尾随换行的差异不算变化
if ($null -ne $existing -and $existing.TrimEnd() -eq $json.TrimEnd()) {
  Write-Host "[GenTranslationBundle] bundle unchanged: $($map.Count) entries (skipped write)"
} else {
  $tmp = $BundlePath + '.tmp'
  Set-Content -LiteralPath $tmp -Value $json -Encoding UTF8
  Move-Item -LiteralPath $tmp -Destination $BundlePath -Force
  Write-Host "[GenTranslationBundle] bundle generated: $($map.Count) entries (FDCN $($fdcnRaw.PSObject.Properties.Count) plugins + seed fill, ordinal-sorted)"
}
