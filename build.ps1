<#
.SYNOPSIS
    Test, publish and zip ThemeSwitch into dist/. The release workflow runs this same script.

.EXAMPLE
    ./build.ps1                      # tests + dist/ThemeSwitch-v0.2.0-dev-win-x64.zip
    ./build.ps1 -Version 1.2.0       # what CI does for tag v1.2.0
    ./build.ps1 -SkipTests -Install  # quick local build, copied to %LOCALAPPDATA%\ThemeSwitch
#>
param(
    [string]$Version = "0.2.0-dev",
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Install
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Invoke-Checked([scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "failed with exit code ${LASTEXITCODE}: $Command" }
}

if (-not $SkipTests) {
    Invoke-Checked { dotnet test ThemeSwitch.slnx -c $Configuration --nologo }
}

$name = "ThemeSwitch-v$Version-win-x64"
$out = Join-Path "dist" $name
if (Test-Path dist) { Remove-Item dist -Recurse -Force }

# Framework-dependent single file: ~200 KB instead of ~160 MB; needs the .NET 10 Desktop Runtime.
Invoke-Checked {
    dotnet publish src/ThemeSwitch/ThemeSwitch.csproj -c $Configuration -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=none -p:Version=$Version -o $out --nologo
}
Copy-Item README.md, LICENSE, CHANGELOG.md $out

$zip = "$out.zip"
Compress-Archive -Path "$out/*" -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline
Write-Host "built $zip"
Write-Host "sha256 $hash"

if ($Install) {
    $target = Join-Path $env:LOCALAPPDATA "ThemeSwitch"
    $running = Get-Process ThemeSwitch -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" }
    # Wait for the exit, or the copy below can hit the still-locked exe.
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $target | Out-Null
    # The exe can stay locked for a moment after the process exits (antivirus scans, the loader).
    for ($attempt = 1; ; $attempt++) {
        try { Copy-Item "$out/ThemeSwitch.exe" $target -Force -ErrorAction Stop; break }
        catch { if ($attempt -ge 10) { throw }; Start-Sleep -Seconds 1 }
    }
    Start-Process (Join-Path $target "ThemeSwitch.exe") -ArgumentList "--tray"
    Write-Host "installed and started $target\ThemeSwitch.exe"
}
