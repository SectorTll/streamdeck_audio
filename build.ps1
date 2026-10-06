# Builds the plugin and installs it into the Stream Deck plugins folder.
#   .\build.ps1            build + install (restarts Stream Deck)
#   .\build.ps1 -NoRestart build + install only
param([switch]$NoRestart)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'out'
$target = Join-Path $env:APPDATA 'Elgato\StreamDeck\Plugins\com.deniss.audiokeys.sdPlugin'

Write-Host "== dotnet publish"
dotnet publish (Join-Path $root 'src\AudioKeys.csproj') -c Release -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "== icons"
& (Join-Path $out 'AudioKeys.exe') --icons (Join-Path $root 'plugin\imgs')

if (-not $NoRestart) {
    Write-Host "== stopping Stream Deck"
    Stop-Process -Name StreamDeck -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}
Get-Process -Name AudioKeys -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "== install -> $target"
New-Item -ItemType Directory -Force (Join-Path $target 'bin') | Out-Null
Copy-Item (Join-Path $root 'plugin\manifest.json') $target -Force
Copy-Item (Join-Path $root 'plugin\imgs') $target -Recurse -Force
Copy-Item (Join-Path $root 'plugin\ui') $target -Recurse -Force
Copy-Item (Join-Path $out '*') (Join-Path $target 'bin') -Recurse -Force

if (-not $NoRestart) {
    Write-Host "== starting Stream Deck"
    Start-Process 'C:\Program Files\Elgato\StreamDeck\StreamDeck.exe' -ArgumentList '--runinbk'
}
Write-Host "done"
