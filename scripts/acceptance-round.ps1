<#
.SYNOPSIS
  Collect the evidence for the manual acceptance round, in three phases around one session (client 2.x).

.DESCRIPTION
  docs/acceptance-checklist.md has criteria that no automated test can close: they are about what
  the rest of the machine looks like while a session runs, and what it looks like afterwards.

  This collects every part of that which a machine can collect, so the only thing left for a human
  is the part that genuinely needs eyes. Each phase appends to one evidence file; run the three
  phases on the GUEST around a single real session, then send the file.

    before : with no session running. Records the untouched proxy settings and your own address.
    during : with a session live. The system proxy must point at Josour (ADR-0015), and what
             follows it - this script included - must leave from the host's address.
    after  : once the session has ended. The proxy settings must be exactly what `before` saw.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\acceptance-round.ps1 -Phase before
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('before', 'during', 'after')] [string] $Phase,
    [string] $EvidenceFile
)

$ErrorActionPreference = 'Continue'
$root = Join-Path $env:LOCALAPPDATA 'Josour'
$dir = Join-Path $root 'acceptance'
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
if (-not $EvidenceFile) {
    $EvidenceFile = Join-Path $dir ("round-{0:yyyyMMdd}-{1}.txt" -f (Get-Date), $env:COMPUTERNAME)
}
# What `before` saw, for `during` and `after` to compare against.
$baselineFile = Join-Path $dir 'baseline.json'

$lines = [System.Collections.Generic.List[string]]::new()
function Say([string] $text) { Write-Output $text; $lines.Add($text) }
function Section([string] $title) { Say ''; Say "== $title ==" }

Say ("#### phase {0} on {1} at {2:yyyy-MM-dd HH:mm:ss zzz}" -f $Phase.ToUpper(), $env:COMPUTERNAME, (Get-Date))

# ---------------------------------------------------------------- the system proxy (10, 12, 13)

Section 'system proxy (criteria 10, 12 and 13)'
$isKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$is = Get-ItemProperty -Path $isKey -ErrorAction SilentlyContinue
$proxy = [ordered]@{}
foreach ($name in 'ProxyEnable', 'ProxyServer', 'ProxyOverride', 'AutoConfigURL', 'AutoDetect') {
    $v = $is.$name
    $proxy[$name] = if ($null -eq $v) { '(absent)' } else { "$v" }
    Say ("  {0,-14}: {1}" -f $name, $proxy[$name])
}
$winhttp = (netsh winhttp show proxy) 2>&1 | Where-Object { $_ -match '\S' } | Select-Object -Last 2
foreach ($l in $winhttp) { Say ("  winhttp       : " + $l.Trim()) }

$marker = Join-Path $root 'system-proxy.json'
$markerPresent = Test-Path -LiteralPath $marker
Say ("  marker        : {0}" -f $(if ($markerPresent) { 'system-proxy.json present (a session holds the proxy)' } else { 'none' }))

$baseline = $null
if (Test-Path -LiteralPath $baselineFile) {
    try { $baseline = Get-Content -LiteralPath $baselineFile -Raw | ConvertFrom-Json } catch { $baseline = $null }
}

switch ($Phase) {
    'before' {
        if ($markerPresent -or $proxy.ProxyServer -match '^(http=)?127\.0\.0\.1:') {
            Say '  WARNING       : the proxy already points at this machine. End any session (or use'
            Say '                  "Fix internet settings" in the tray menu) and run `before` again.'
        }
    }
    'during' {
        $ours = $proxy.ProxyEnable -eq '1' -and $proxy.ProxyServer -match '127\.0\.0\.1:(\d+)'
        Say ("  VERDICT       : {0}" -f $(if ($ours) { "PASS - the system proxy points at Josour (port $($Matches[1]))" }
                                         else { 'FAIL - no session holds the system proxy. Is the session live?' }))
    }
    'after' {
        if (-not $baseline) {
            Say '  no baseline - run the `before` phase before the next session'
        } else {
            $diff = @(foreach ($name in $proxy.Keys) { if ("$($baseline.proxy.$name)" -ne $proxy[$name]) { $name } })
            Say ("  VERDICT       : {0}" -f $(if (-not $diff.Count -and -not $markerPresent) {
                    'PASS - the proxy settings are exactly what they were before the session, and the marker is gone' }
                 elseif ($diff.Count) { "FAIL - changed since before: $($diff -join ', ')" }
                 else { 'FAIL - the settings are back but system-proxy.json is still there' }))
        }
    }
}

# --------------------------------------------------------------- the address the world sees (9, 10)

Section 'public IP as seen through the system proxy (criteria 9 and 10)'
# Invoke-RestMethod follows the system (WinINet) proxy, like any browser: during a session it goes
# through the host, before and after it does not.
$ip = $null
foreach ($svc in 'https://api.ipify.org', 'https://ifconfig.me/ip') {
    try {
        $ip = (Invoke-RestMethod -Uri $svc -TimeoutSec 12 -ErrorAction Stop).ToString().Trim()
        Say ("  {0,-24}: {1}" -f $svc, $ip)
        break
    } catch {
        Say ("  {0,-24}: unreachable ({1})" -f $svc, $_.Exception.Message)
    }
}
if ($ip -and $baseline -and $baseline.ip) {
    switch ($Phase) {
        'during' {
            Say ("  VERDICT       : {0}" -f $(if ($ip -ne $baseline.ip) { "PASS - the host's address, not yours ($($baseline.ip))" }
                                             else { 'FAIL - still your own address: the traffic is not going through the host' }))
        }
        'after' {
            Say ("  VERDICT       : {0}" -f $(if ($ip -eq $baseline.ip) { 'PASS - your own address again' }
                                             else { "CHECK - $ip is not the $($baseline.ip) recorded before (did your network change?)" }))
        }
    }
}

if ($Phase -eq 'before') {
    [ordered]@{ proxy = $proxy; ip = $ip; at = (Get-Date).ToString('o') } | ConvertTo-Json |
        Out-File -FilePath $baselineFile -Encoding utf8
    Say ("  baseline saved: {0}" -f $baselineFile)
}

# ------------------------------------------------- what the local proxy refuses (D14, criterion 15)

if ($Phase -eq 'during' -and $proxy.ProxyServer -match '127\.0\.0\.1:(\d+)') {
    $port = [int] $Matches[1]
    Section 'what the local proxy refuses (ADR-0015 point 3, criterion 15)'
    # These must all be refused before anything leaves the machine: a port other than 80/443, an
    # address literal, and the host's own network.
    foreach ($target in 'portquiz.net:8080', '93.184.215.14:443', '192.168.1.1:80', 'localhost:80') {
        $reply = '(no reply)'
        try {
            $c = [System.Net.Sockets.TcpClient]::new()
            if (-not $c.ConnectAsync('127.0.0.1', $port).Wait(3000)) { throw 'connect timed out' }
            $s = $c.GetStream()
            $req = [Text.Encoding]::ASCII.GetBytes("CONNECT $target HTTP/1.1`r`nHost: $target`r`n`r`n")
            $s.Write($req, 0, $req.Length)
            $s.ReadTimeout = 15000
            $buf = New-Object byte[] 256
            $n = try { $s.Read($buf, 0, $buf.Length) } catch { 0 }
            if ($n -gt 0) { $reply = ([Text.Encoding]::ASCII.GetString($buf, 0, $n) -split "`r`n")[0] }
            $c.Close()
        } catch { $reply = "error: $($_.Exception.Message)" }
        $refused = $reply -notmatch '^HTTP/1\.[01] 2\d\d'
        Say ("  {0,-20}: {1,-40} {2}" -f $target, $reply, $(if ($refused) { 'PASS (refused)' } else { 'FAIL - it was let through' }))
    }
}

# --------------------------------------------------------------------- what the session recorded

$log = Get-ChildItem -Path (Join-Path $root 'logs') -Filter 'app-*.log' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $log) {
    Section 'the app log'
    Say '  no log file - has the app ever run on this machine?'
} else {
    Section 'sessions in this log (criteria 6, 11, 12 and 13)'
    Say ("  log           : {0} ({1:HH:mm:ss})" -f $log.Name, $log.LastWriteTime)
    foreach ($e in (Select-String -LiteralPath $log.FullName -Pattern 'System proxy (pointed at|settings put back)' | Select-Object -Last 4)) {
        Say ("  {0}  {1}" -f ($e.Line -split ' ')[1], ($e.Line -replace '.*?(System proxy.*)$', '$1'))
    }
    $ends = Select-String -LiteralPath $log.FullName -Pattern 'session\.end.*?reason=(\w+)'
    if (-not $ends) { Say '  no session.end yet' }
    foreach ($e in ($ends | Select-Object -Last 6)) {
        Say ("  {0}  {1}" -f ($e.Line -split ' ')[1], ($e.Line -replace '.*?(reason=.*)$', '$1'))
    }
    $expired = @($ends | Where-Object { $_.Matches[0].Groups[1].Value -eq 'expired' }).Count
    Say ("  reason=expired: {0} {1}" -f $expired, $(if ($expired) { '- criterion 12 is closed' } else { '- criterion 12 still open' }))
    foreach ($c in (Select-String -LiteralPath $log.FullName -Pattern 'winner="?(\w+)"?.*?connect_ms=(\d+)' | Select-Object -Last 3)) {
        Say ("  connected     : winner={0} connect_ms={1}" -f $c.Matches[0].Groups[1].Value, $c.Matches[0].Groups[2].Value)
    }

    Section 'no browsing content in the client log (security review)'
    $server = $null
    try { $server = ([uri] (Get-Content -LiteralPath (Join-Path $root 'settings.json') -Raw | ConvertFrom-Json).serverUrl).Host } catch { }
    $suspect = Select-String -LiteralPath $log.FullName -Pattern 'https?://(?!check\.josour)[a-z0-9.-]+\.[a-z]{2,}' |
               Where-Object { $_.Line -notmatch 'ipify|ifconfig\.me|[Rr]elay' -and (-not $server -or $_.Line -notmatch [regex]::Escape($server)) }
    Say ("  URLs that are not the server or the probe page: {0}" -f @($suspect).Count)
    foreach ($u in ($suspect | Select-Object -First 5)) { Say ("    {0}" -f $u.Line.Trim()) }
}

# --------------------------------------------------------------------- what only eyes can answer

Section 'left for you'
switch ($Phase) {
    'before' {
        Say '  Start the session. On the HOST machine, when the request window appears, read it first:'
        Say '    criterion 4 - does it say every program on the guest that uses the system proxy will'
        Say '                  leave from your address?'
        Say '    criterion 5 - press Reject once on a first request, then leave a second one'
        Say '                  untouched for 60 seconds. Both must be tried before you accept one.'
        Say '    criterion 12 - ask for 15 minutes (the shortest) and let it run out. Do the `during`'
        Say '                   checks in the first few minutes; one session closes 4, 5, 9, 10, 12, 13 and 15.'
    }
    'during' {
        Say '  In ANY browser on this machine, one tab each:'
        Say '    1. https://api.ipify.org        -> must show the HOST address'
        Say '    2. http://192.168.1.1/          -> must be refused'
        Say '    3. http://localhost/            -> must be refused (it is in the bypass list: it'
        Say '                                       reaches THIS machine, never the host)'
        Say '    4. the host public address      -> must be refused'
        Say '  A screenshot of api.ipify.org now and again after the session, two different numbers,'
        Say '  closes criterion 9.'
    }
    'after' {
        Say '  Open Settings -> Network & internet -> Proxy and confirm it looks as it did before.'
        Say '  Then reload https://api.ipify.org in a browser: your own address.'
    }
}

Say ''
$lines | Out-File -FilePath $EvidenceFile -Append -Encoding utf8
Write-Output ''
Write-Output "evidence appended to: $EvidenceFile"
