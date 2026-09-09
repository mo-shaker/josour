<#
.SYNOPSIS
  Collect the evidence for the manual acceptance round, in three phases around one session.

.DESCRIPTION
  docs/acceptance-checklist.md has criteria that no automated test can close: they are about what
  the rest of the machine looks like while a session runs, and what it looks like afterwards.

  This collects every part of that which a machine can collect, so the only thing left for a human
  is the part that genuinely needs eyes. Each phase appends to one evidence file; run the three
  phases around a single real session, then send the file.

    before : with Josour closed. Records the untouched machine.
    during : with a session live and the work browser open.
    after  : once the session has ended and the work browser has closed.

  What it deliberately does NOT do: drive the work browser's proxy. The proxy only accepts the
  work browser's own process tree, so a connection from here is refused by design - and the
  `during` phase records that refusal, because it is itself the proof of criterion 10.

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
if (-not $EvidenceFile) {
    $dir = Join-Path $root 'acceptance'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $EvidenceFile = Join-Path $dir ("round-{0:yyyyMMdd}-{1}.txt" -f (Get-Date), $env:COMPUTERNAME)
}

$lines = [System.Collections.Generic.List[string]]::new()
function Say([string] $text) { Write-Output $text; $lines.Add($text) }
function Section([string] $title) { Say ''; Say "== $title ==" }

Say ("#### phase {0} on {1} at {2:yyyy-MM-dd HH:mm:ss zzz}" -f $Phase.ToUpper(), $env:COMPUTERNAME, (Get-Date))

# ---------------------------------------------------------------- the machine around the session

Section 'system proxy (criteria 10 and 13)'
$isKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$is = Get-ItemProperty -Path $isKey -ErrorAction SilentlyContinue
foreach ($name in 'ProxyEnable', 'ProxyServer', 'ProxyOverride', 'AutoConfigURL') {
    $v = $is.$name
    Say ("  {0,-14}: {1}" -f $name, $(if ($null -eq $v) { '(absent)' } else { $v }))
}
$winhttp = (netsh winhttp show proxy) 2>&1 | Where-Object { $_ -match '\S' } | Select-Object -Last 2
foreach ($l in $winhttp) { Say ("  winhttp       : " + $l.Trim()) }

Section 'processes'
foreach ($n in 'Josour', 'chrome', 'msedge', 'Teams', 'ms-teams', 'OUTLOOK') {
    $c = @(Get-Process -Name $n -ErrorAction SilentlyContinue).Count
    if ($c -or $n -in 'Josour', 'chrome') { Say ("  {0,-14}: {1}" -f $n, $c) }
}

# --------------------------------------------------------- what the rest of the machine sees (10)

Section 'public IP as seen by everything that is NOT the work browser (criteria 9 and 10)'
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
if ($Phase -eq 'during' -and $ip) {
    Say ''
    Say '  This number is what Teams, Outlook and your ordinary browser are using RIGHT NOW,'
    Say '  while the session is live. It must be YOUR address, not the host''s. The work browser'
    Say '  must show a different one - that pair is criterion 9.'
}

# --------------------------------------------------------------- the proxy, and who may use it

Section 'the work browser proxy'
$log = Get-ChildItem -Path (Join-Path $root 'logs') -Filter 'app-*.log' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $log) {
    Say '  no log file - has the app ever run on this machine?'
} else {
    Say ("  log           : {0} ({1:HH:mm:ss}, {2:N0} lines)" -f $log.Name, $log.LastWriteTime,
         (Get-Content -LiteralPath $log.FullName | Measure-Object -Line).Lines)
    $launch = Select-String -LiteralPath $log.FullName -Pattern 'Work browser launched.*?(\d+\.\d+\.\d+\.\d+):(\d+)' |
              Select-Object -Last 1
    if (-not $launch) {
        Say '  proxy         : no work browser launch in this log'
    } else {
        $addr = $launch.Matches[0].Groups[1].Value
        $port = [int] $launch.Matches[0].Groups[2].Value
        Say ("  proxy         : {0}:{1}  (from {2})" -f $addr, $port, ($launch.Line -split ' ')[1])

        $listening = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue).Count -gt 0
        Say ("  listening     : {0}" -f $(if ($listening) { 'yes' } else { 'no - the session is over' }))

        if ($listening) {
            # Not a smoke test: this connection MUST be refused. The proxy admits the work
            # browser's process tree and nothing else, so a refusal here is criterion 10 passing.
            try {
                $c = [System.Net.Sockets.TcpClient]::new()
                $null = $c.ConnectAsync($addr, $port).Wait(3000)
                $s = $c.GetStream()
                $req = [Text.Encoding]::ASCII.GetBytes("CONNECT example.com:443 HTTP/1.1`r`nHost: example.com:443`r`n`r`n")
                $s.Write($req, 0, $req.Length)
                $s.ReadTimeout = 4000
                $buf = New-Object byte[] 256
                $n = try { $s.Read($buf, 0, $buf.Length) } catch { 0 }
                $reply = if ($n -gt 0) { ([Text.Encoding]::ASCII.GetString($buf, 0, $n) -split "`r`n")[0] } else { '(closed with no reply)' }
                $c.Close()
                Say ("  outsider says : {0}" -f $reply)
                Say ("  VERDICT       : {0}" -f $(if ($n -eq 0 -or $reply -match '40[0-9]') {
                        'PASS - the proxy refuses a process that is not the work browser' }
                     else { 'FAIL - an outside process was served. Report this.' }))
            } catch {
                Say ("  outsider says : refused at connect ({0})" -f $_.Exception.Message)
                Say '  VERDICT       : PASS - the proxy refuses a process that is not the work browser'
            }
        }
    }

    # ------------------------------------------------------------------ what the session recorded

    Section 'sessions in this log (criteria 6, 11 and 12)'
    $ends = Select-String -LiteralPath $log.FullName -Pattern 'session\.end.*?reason=(\w+)'
    if (-not $ends) { Say '  no session.end yet' }
    foreach ($e in ($ends | Select-Object -Last 6)) {
        Say ("  {0}  {1}" -f ($e.Line -split ' ')[1], ($e.Line -replace '.*?(reason=.*)$', '$1'))
    }
    $expired = @($ends | Where-Object { $_.Matches[0].Groups[1].Value -eq 'expired' }).Count
    Say ("  reason=expired: {0} {1}" -f $expired, $(if ($expired) { '- criterion 12 is closed' } else { '- criterion 12 still open' }))

    $conn = Select-String -LiteralPath $log.FullName -Pattern 'winner="?(\w+)"?.*?connect_ms=(\d+)' | Select-Object -Last 3
    foreach ($c in $conn) {
        Say ("  connected     : winner={0} connect_ms={1}" -f $c.Matches[0].Groups[1].Value, $c.Matches[0].Groups[2].Value)
    }

    # --------------------------------------------------- no browsing content in the log (section 14)

    Section 'no browsing content in the client log (security review)'
    $suspect = Select-String -LiteralPath $log.FullName -Pattern 'https?://(?!check\.josour)[a-z0-9.-]+\.[a-z]{2,}' |
               Where-Object { $_.Line -notmatch 'rg\.mohamedshaker\.net|api\.|ipify|Relay|relay' }
    Say ("  URLs that are not the server or the probe page: {0}" -f @($suspect).Count)
    foreach ($u in ($suspect | Select-Object -First 5)) { Say ("    {0}" -f $u.Line.Trim()) }
}

# ------------------------------------------------------------- did the browser exit cleanly (13)

if ($Phase -eq 'after') {
    Section 'work browser profile after the session (criterion 13)'
    $prefs = Join-Path $root 'BrowserProfile\Default\Preferences'
    if (-not (Test-Path -LiteralPath $prefs)) {
        Say '  no profile - the work browser has not run on this machine'
    } else {
        try {
            $p = Get-Content -LiteralPath $prefs -Raw | ConvertFrom-Json
            $exit = $p.profile.exit_type
            Say ("  exit_type     : {0}" -f $(if ($exit) { $exit } else { '(absent)' }))
            Say ("  VERDICT       : {0}" -f $(if ($exit -eq 'Normal' -or -not $exit) {
                    'PASS - no "Chrome did not shut down correctly" bar on the next launch' }
                 else { "FAIL - Chrome will show the restore bar (exit_type=$exit)" }))
        } catch { Say ("  could not read Preferences: {0}" -f $_.Exception.Message) }
    }
}

# --------------------------------------------------------------------- what only eyes can answer

Section 'left for you'
switch ($Phase) {
    'before' {
        Say '  Nothing. Open Josour, sign in, and run the session.'
        Say '  On the HOST machine, when the request window appears, read it before accepting:'
        Say '    criterion 4 - does it say the guest may browse ANY site through your connection?'
        Say '    criterion 5 - press Reject once on a first request, then leave a second one'
        Say '                  untouched for 60 seconds. Both must be tried before you accept one.'
        Say '    criterion 12 - the shortest the app offers is 15 minutes, so ask for 15 and let it'
        Say '                   run out. Do the `during` checks in the first few minutes and leave it;'
        Say '                   one session then closes 4, 5, 9, 10, 12, 13 and 15 together.'
        Say '                   (A one-minute run is possible only through the spike tool:'
        Say '                    Josour.Spike session --role guest --host-device <id> --minutes 1)'
    }
    'during' {
        Say '  In the WORK browser, one tab each:'
        Say '    1. https://api.ipify.org        -> must show the HOST address'
        Say '    2. http://192.168.1.1/          -> must be refused'
        Say '    3. http://localhost/            -> must be refused'
        Say '    4. http://[::1]/                -> must be refused'
        Say '    5. the host public address      -> must be refused'
        Say '  (2-5 are criterion 15. "Refused" may read as ERR_TUNNEL_CONNECTION_FAILED.)'
        Say '  Then open Teams or Outlook and confirm it still works - criterion 10.'
        Say '  A single screenshot with the work browser and your ordinary browser side by side,'
        Say '  both on api.ipify.org showing two different numbers, closes criterion 9 on its own.'
    }
    'after' {
        Say '  Open Windows proxy settings and confirm nothing is set, then open ordinary Chrome'
        Say '  and confirm there is no "did not shut down correctly" bar - criterion 13.'
    }
}

Say ''
$lines | Out-File -FilePath $EvidenceFile -Append -Encoding utf8
Write-Output ''
Write-Output "evidence appended to: $EvidenceFile"
