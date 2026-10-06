# Builds both plugins and installs them into the Stream Deck plugins folder.
#   .\build.ps1                 build + install both (restarts Stream Deck)
#   .\build.ps1 -NoRestart      build + install only
#   .\build.ps1 -Only audio     (or ha) build + install one plugin
param([switch]$NoRestart, [string]$Only = '')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# prefer a user-local SDK (installed with dotnet-install.ps1) when the machine-wide dotnet has no SDK
$dotnet = 'dotnet'
if (-not (& dotnet --list-sdks 2>$null) -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }

$plugins = @(
    @{ Name = 'audio'; Project = 'src\AudioKeys\AudioKeys.csproj'; Exe = 'AudioKeys.exe'; Src = 'plugin-audio'; Target = 'com.deniss.audiokeys.sdPlugin'; Proc = 'AudioKeys' },
    @{ Name = 'ha';    Project = 'src\HAKeys\HAKeys.csproj';       Exe = 'HAKeys.exe';    Src = 'plugin-ha';    Target = 'com.deniss.hakeys.sdPlugin';    Proc = 'HAKeys' }
)
if ($Only) { $plugins = $plugins | Where-Object { $_.Name -eq $Only } }

foreach ($p in $plugins) {
    $out = Join-Path $root "out\$($p.Name)"
    Write-Host "== publish $($p.Name) ($dotnet)"
    & $dotnet publish (Join-Path $root $p.Project) -c Release -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "build failed: $($p.Name)" }
    Write-Host "== icons $($p.Name)"
    # the plugins are WinExe (no console): Start-Process -Wait, otherwise the copy below races the icon writer
    Start-Process -FilePath (Join-Path $out $p.Exe) -ArgumentList @('--icons', "`"$(Join-Path $root "$($p.Src)\imgs")`"") -Wait -NoNewWindow
}

if (-not $NoRestart) {
    Write-Host "== stopping Stream Deck"
    Stop-Process -Name StreamDeck -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

foreach ($p in $plugins) {
    Get-Process -Name $p.Proc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $target = Join-Path $env:APPDATA "Elgato\StreamDeck\Plugins\$($p.Target)"
    $out = Join-Path $root "out\$($p.Name)"
    Write-Host "== install -> $target"
    New-Item -ItemType Directory -Force (Join-Path $target 'bin') | Out-Null
    Copy-Item (Join-Path $root "$($p.Src)\manifest.json") $target -Force
    Copy-Item (Join-Path $root "$($p.Src)\imgs") $target -Recurse -Force
    Copy-Item (Join-Path $root "$($p.Src)\ui") $target -Recurse -Force
    Copy-Item (Join-Path $out '*') (Join-Path $target 'bin') -Recurse -Force
}

if (-not $NoRestart) {
    Write-Host "== starting Stream Deck"
    Start-Process 'C:\Program Files\Elgato\StreamDeck\StreamDeck.exe' -ArgumentList '--runinbk'
}
Write-Host "done"
