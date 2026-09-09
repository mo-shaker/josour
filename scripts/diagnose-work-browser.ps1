<#
.SYNOPSIS
  Why did a session end with browser_not_proxied?

.DESCRIPTION
  The guest's work browser must reach the local proxy on http://check.josour/ within the probe
  deadline, or the session is ended on purpose (better a dead session than one the user believes
  is tunnelled while it is not).

  This reproduces the browser half alone: a bare listener, the exact flags Josour uses, and a
  cold profile. It answers the one question the app's log cannot - whether the request arrives at
  all, and how long it takes - and prints the browser policy that could override the proxy.

  Nothing here touches Josour or your session. Run it while Josour is closed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\diagnose-work-browser.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Chrome', 'Edge')] [string] $Browser = 'Chrome',
    [int] $WaitSeconds = 40,
    [int] $ProbeDeadlineSeconds = 10
)

$ErrorActionPreference = 'Continue'

function Find-Browser([string] $kind) {
    $candidates = if ($kind -eq 'Chrome') {
        @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
          "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
          "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe")
    } else {
        @("$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
          "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe")
    }
    $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

Write-Output "== 1. environment =="
Write-Output ("windows            : " + (Get-CimInstance Win32_OperatingSystem).Caption + " build " + [Environment]::OSVersion.Version.Build)
$exe = Find-Browser $Browser
if (-not $exe) { Write-Output "$Browser            : NOT FOUND - this alone explains browser_not_proxied"; return }
Write-Output ("$Browser             : $exe")
Write-Output ("version            : " + (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion)

$running = @(Get-Process -Name ($(if ($Browser -eq 'Chrome') { 'chrome' } else { 'msedge' })) -ErrorAction SilentlyContinue)
Write-Output ("already running    : " + $(if ($running.Count) { "YES - $($running.Count) process(es). Close them and rerun; a live instance can swallow the launch." } else { 'no' }))

Write-Output ''
Write-Output "== 2. policy that could override --proxy-server =="
$key = if ($Browser -eq 'Chrome') { 'SOFTWARE\Policies\Google\Chrome' } else { 'SOFTWARE\Policies\Microsoft\Edge' }
$found = $false
foreach ($root in 'HKLM:', 'HKCU:') {
    foreach ($name in 'ProxySettings', 'ProxyMode', 'ProxyServer', 'UserDataDir') {
        $v = (Get-ItemProperty -Path "$root\$key" -Name $name -ErrorAction SilentlyContinue).$name
        if ($null -ne $v) { Write-Output "  $root\$key :: $name = $v"; $found = $true }
    }
}
if (-not $found) { Write-Output '  none - the command line is free to set the proxy' }

Write-Output ''
Write-Output "== 3. does the probe request actually arrive, and when? =="
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
Write-Output "  bare listener on 127.0.0.1:$port (accepts everything; no owner check)"

$profileDir = Join-Path $env:TEMP 'josour-browser-diagnosis'
if (Test-Path -LiteralPath $profileDir) { Remove-Item -LiteralPath $profileDir -Recurse -Force -ErrorAction SilentlyContinue }

$browserArgs = @(
    "--user-data-dir=$profileDir",
    "--proxy-server=http://127.0.0.1:$port",
    '--no-first-run', '--no-default-browser-check', '--disable-sync',
    '--disable-background-networking', '--disable-component-update', '--disable-quic',
    '--force-webrtc-ip-handling-policy=disable_non_proxied_udp',
    '--hide-crash-restore-bubble', '--new-window', 'http://check.josour/'
)
$clock = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $exe -ArgumentList $browserArgs -PassThru

$firstAny = -1
$firstProbe = -1
$total = 0
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline -and $firstProbe -lt 0) {
    if ($listener.Pending()) {
        $client = $listener.AcceptTcpClient()
        if ($firstAny -lt 0) { $firstAny = $clock.ElapsedMilliseconds }
        $total++
        $stream = $client.GetStream()
        $stream.ReadTimeout = 1200
        $buf = New-Object byte[] 1024
        try {
            $n = $stream.Read($buf, 0, 1024)
            if ($n -gt 0) {
                $line = ([Text.Encoding]::ASCII.GetString($buf, 0, $n) -split "`r`n")[0]
                if ($line -match 'check\.josour') { $firstProbe = $clock.ElapsedMilliseconds }
            }
        } catch { }
        $client.Close()
    } else {
        Start-Sleep -Milliseconds 100
    }
}
$listener.Stop()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Get-Process -Name ($(if ($Browser -eq 'Chrome') { 'chrome' } else { 'msedge' })) -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
if (Test-Path -LiteralPath $profileDir) { Remove-Item -LiteralPath $profileDir -Recurse -Force -ErrorAction SilentlyContinue }

Write-Output "  connections seen : $total"
Write-Output "  first connection : $(if ($firstAny -lt 0) { 'none' } else { "$firstAny ms" })"
Write-Output "  probe request    : $(if ($firstProbe -lt 0) { "NEVER within $WaitSeconds s" } else { "$firstProbe ms" })"

Write-Output ''
Write-Output '== verdict =='
if ($firstProbe -lt 0 -and $total -eq 0) {
    Write-Output '  The browser never used the proxy at all. Something overrides --proxy-server on this'
    Write-Output '  machine (see section 2), or the browser did not really start. Josour cannot fix this'
    Write-Output '  from its side, which is why it ends the session instead of pretending.'
} elseif ($firstProbe -lt 0) {
    Write-Output "  The browser used the proxy for its own traffic but never requested the probe page."
    Write-Output '  Send this whole output back: this is the interesting case and not one we have seen.'
} elseif ($firstProbe -gt ($ProbeDeadlineSeconds * 1000)) {
    Write-Output "  The probe DOES arrive, but after $firstProbe ms - past Josour's ${ProbeDeadlineSeconds}s deadline."
    Write-Output '  The browser leg works; the deadline is too short for this machine. That is a Josour'
    Write-Output '  setting to raise, not something to fix on this computer.'
} else {
    Write-Output "  The browser leg is healthy here: the probe arrived in $firstProbe ms, inside the"
    Write-Output "  ${ProbeDeadlineSeconds}s deadline. If a real session still fails, the difference is Josour's own proxy"
    Write-Output '  (its owner-PID check admits only processes inside the work browser job), so send the'
    Write-Output '  app log from the same attempt.'
}
