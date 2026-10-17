<#
.SYNOPSIS
    Print the CHANGELOG.md section for one version; fails if there is none, so a release can't
    ship without notes.
.EXAMPLE
    ./tools/changelog-section.ps1 -Version 0.1.0
#>
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = "Stop"
$lines = Get-Content (Join-Path $PSScriptRoot "..\CHANGELOG.md")
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^## \[$([regex]::Escape($Version))\]") { $start = $i + 1; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no '## [$Version]' section" }

$end = $lines.Count
for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## \[' -or $lines[$i] -match '^\[[^\]]+\]: ') { $end = $i; break }
}
# PowerShell ranges count down when end < start, so an empty section needs an explicit check.
if ($end -le $start) { throw "CHANGELOG.md section for $Version is empty" }
$section = ($lines[$start..($end - 1)] -join "`n").Trim()
if (-not $section) { throw "CHANGELOG.md section for $Version is empty" }
$section
