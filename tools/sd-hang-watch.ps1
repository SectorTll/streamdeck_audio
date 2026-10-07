# Watches StreamDeck.exe. When it stops responding for 10 s, writes a full minidump (built-in comsvcs
# MiniDump, no extra tools) and notes the state of our plugins. Runs hidden from a logon scheduled task.
# Log and dumps: %LOCALAPPDATA%\DeckKeys\watch
$dir = Join-Path $env:LOCALAPPDATA 'DeckKeys\watch'
$dumps = Join-Path $dir 'dumps'
New-Item -ItemType Directory -Force $dumps | Out-Null
$log = Join-Path $dir 'sd-watch.log'
function L($m) { Add-Content $log ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m) }
# single instance
$me = $PID
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'sd-hang-watch' -and $_.ProcessId -ne $me -and $_.Name -match 'powershell' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
L "watcher started pid $PID (boot $((Get-CimInstance Win32_OperatingSystem).LastBootUpTime))"
$hungSince = $null; $dumped = $false; $lastPid = 0
while ($true) {
    $p = Get-Process -Name StreamDeck -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) {
        if ($lastPid) { L "StreamDeck gone (pid $lastPid$(if ($hungSince) { ", was hung since $hungSince" }))"; $lastPid = 0 }
        $hungSince = $null; $dumped = $false; Start-Sleep 5; continue
    }
    if ($p.Id -ne $lastPid) { L "StreamDeck running pid $($p.Id), started $($p.StartTime)"; $lastPid = $p.Id }
    if ($p.Responding) { if ($hungSince) { L "StreamDeck responding again (hung since $hungSince)" }; $hungSince = $null; $dumped = $false; Start-Sleep 5; continue }
    if (-not $hungSince) { $hungSince = Get-Date; L "StreamDeck NOT responding (pid $($p.Id))" }
    if (-not $dumped -and ((Get-Date) - $hungSince).TotalSeconds -ge 10) {
        $f = Join-Path $dumps ("StreamDeck-hang-{0:yyyyMMdd-HHmmss}.dmp" -f (Get-Date))
        L "writing dump $f"
        try { & rundll32.exe comsvcs.dll, MiniDump $p.Id $f full; Start-Sleep 5; L ("dump size " + ((Get-Item $f -ErrorAction SilentlyContinue).Length)) } catch { L "dump failed: $_" }
        foreach ($n in 'AudioKeys','HAKeys') { $q = Get-Process -Name $n -ErrorAction SilentlyContinue; L ("  {0}: {1}" -f $n, $(if ($q) { "pid $($q.Id) responding=$($q.Responding) threads=$($q.Threads.Count)" } else { 'not running' })) }
        $dumped = $true
    }
    Start-Sleep 5
}
