<#
.SYNOPSIS
  Spike S2 for ADR-0015: point the Windows system proxy at 127.0.0.1, read it back, and put it back.

.DESCRIPTION
  The guest app would do this at the start of every session (docs/decisions/0015-system-proxy-instead-of-work-browser.md),
  through the WinINet per-connection options and without administrator rights. This measures it on real machines
  before any of it is built:

    1. reads the LAN settings and those of every dial-up/VPN (RAS) connection, and saves them to snapshot.json
       BEFORE changing anything;
    2. sets "http=127.0.0.1:<port>;https=127.0.0.1:<port>" with a bypass list, auto-config and auto-detect off,
       through InternetSetOptionW(INTERNET_OPTION_PER_CONNECTION_OPTION), then tells running WinINet clients with
       SETTINGS_CHANGED, PROXY_SETTINGS_CHANGED and REFRESH;
    3. reads everything back;
    4. waits (Enter, or -HoldSeconds) while you look at the browsers;
    5. puts back exactly what it saved - in a finally block, so Ctrl+C restores too - and reads it back again.

  It also reports the Group Policy values that lock or override the proxy, ProxySettingsPerUser=0 first.

  While the proxy is applied, a dummy listener answers on 127.0.0.1:<port>. It forwards NOTHING: it writes down
  each request and answers 502, except for http://check.josour/ (a name that does not exist, so it only loads
  through the proxy) and its own test pages on http://127.0.0.1:<port>/. Apps that honour the system proxy have no
  internet while it is applied, and anything that still loads is a leak (S3).

  Results go to %USERPROFILE%\josour-spike-results\s2-windows-<timestamp>\; summary.txt is the file to send back.
  The steps and the results table: docs/spike-runbook.md, "The system proxy spikes".

  The file is plain ASCII on purpose: Windows PowerShell 5.1 reads a script without a BOM as ANSI, and one UTF-8
  dash turns into a stray quote character there.

.PARAMETER Port
  The loopback port of the dummy listener and of the proxy setting (default 18923).
.PARAMETER Bypass
  Extra bypass entries, for instance the Josour server and relay host names.
.PARAMETER HoldSeconds
  Stay applied this long instead of waiting for Enter.
.PARAMETER LingerSeconds
  Keep the listener up this long after the restore, to see who still uses the proxy (default 20).
.PARAMETER DryRun
  Read and print the current settings and policies; change nothing.
.PARAMETER RestoreFrom
  Put back the snapshot.json of a run whose window was closed before it could restore.
.PARAMETER LanOnly
  Leave the dial-up/VPN connections alone and touch the LAN settings only.
.PARAMETER ApplyEvenIfLocked
  Apply even when ProxySettingsPerUser=0, without asking (to observe what WinINet does under the lock).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\spikes\system-proxy-windows.ps1 -DryRun
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\spikes\system-proxy-windows.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\spikes\system-proxy-windows.ps1 -HoldSeconds 120
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\spikes\system-proxy-windows.ps1 -RestoreFrom "$env:USERPROFILE\josour-spike-results\s2-windows-20261015-101500\snapshot.json"
#>
[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)] [int] $Port = 18923,
    [string[]] $Bypass = @(),
    [ValidateRange(0, 86400)] [int] $HoldSeconds = 0,
    [ValidateRange(0, 3600)] [int] $LingerSeconds = 20,
    [string] $Out = '',
    [switch] $DryRun,
    [string] $RestoreFrom = '',
    [switch] $LanOnly,
    [switch] $ApplyEvenIfLocked
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This is the Windows spike; on macOS run scripts/spikes/system-proxy-macos.sh.'
}

# C# 5 on purpose: Add-Type in Windows PowerShell 5.1 compiles with the .NET Framework compiler.
$winInetSource = @'
using System;
using System.Runtime.InteropServices;

namespace JosourSpike
{
    public sealed class ConnSettings
    {
        public string Connection { get; set; }        // null = the LAN settings
        public int Flags { get; set; }                // PROXY_TYPE_*, as the Internet Options dialog shows them
        public string FlagsSource { get; set; }       // FLAGS_UI, or FLAGS before IE8
        public int LegacyFlags { get; set; }          // INTERNET_PER_CONN_FLAGS, for the record (-1 if unreadable)
        public string ProxyServer { get; set; }
        public string ProxyBypass { get; set; }
        public string AutoConfigUrl { get; set; }
        public int AutoDiscoveryFlags { get; set; }
    }

    public static class WinInet
    {
        public const int PROXY_TYPE_DIRECT = 1;
        public const int PROXY_TYPE_PROXY = 2;
        public const int PROXY_TYPE_AUTO_PROXY_URL = 4;
        public const int PROXY_TYPE_AUTO_DETECT = 8;

        public const int INTERNET_OPTION_REFRESH = 37;
        public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        public const int INTERNET_OPTION_PROXY_SETTINGS_CHANGED = 95;
        const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;

        const int INTERNET_PER_CONN_FLAGS = 1;
        const int INTERNET_PER_CONN_PROXY_SERVER = 2;
        const int INTERNET_PER_CONN_PROXY_BYPASS = 3;
        const int INTERNET_PER_CONN_AUTOCONFIG_URL = 4;
        const int INTERNET_PER_CONN_AUTODISCOVERY_FLAGS = 5;
        const int INTERNET_PER_CONN_FLAGS_UI = 10;

        [StructLayout(LayoutKind.Sequential)]
        struct OptionList
        {
            public int dwSize;
            public IntPtr pszConnection;
            public int dwOptionCount;
            public int dwOptionError;
            public IntPtr pOptions;
        }

        // The union of INTERNET_PER_CONN_OPTION: a DWORD, a string pointer or a FILETIME. The FILETIME is two DWORDs,
        // so the union is 4-byte aligned on x86; a long here would get the 32-bit layout wrong.
        [StructLayout(LayoutKind.Explicit)]
        struct OptionValue
        {
            [FieldOffset(0)] public int dwValue;
            [FieldOffset(0)] public IntPtr pszValue;
            [FieldOffset(0)] public int ftLow;
            [FieldOffset(4)] public int ftHigh;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Option
        {
            public int dwOption;
            public OptionValue Value;
        }

        [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetQueryOptionW")]
        static extern bool InternetQueryOption(IntPtr hInternet, int dwOption, ref OptionList lpBuffer, ref int lpdwBufferLength);

        [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
        static extern bool InternetSetOption(IntPtr hInternet, int dwOption, ref OptionList lpBuffer, int dwBufferLength);

        [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
        static extern bool InternetSetOptionNoBuffer(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr hMem);

        static Option[] Options(params int[] ids)
        {
            Option[] options = new Option[ids.Length];
            for (int i = 0; i < ids.Length; i++) options[i].dwOption = ids[i];
            return options;
        }

        // One query or set over an unmanaged array of options; returns 0 or the Win32 error.
        static int Call(bool set, string connection, Option[] options)
        {
            int size = Marshal.SizeOf(typeof(Option));
            IntPtr array = Marshal.AllocHGlobal(size * options.Length);
            IntPtr conn = connection == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(connection);
            try
            {
                for (int i = 0; i < options.Length; i++)
                    Marshal.StructureToPtr(options[i], new IntPtr(array.ToInt64() + i * size), false);
                OptionList list = new OptionList();
                list.dwSize = Marshal.SizeOf(typeof(OptionList));
                list.pszConnection = conn;
                list.dwOptionCount = options.Length;
                list.pOptions = array;
                int length = list.dwSize;
                bool ok = set
                    ? InternetSetOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, length)
                    : InternetQueryOption(IntPtr.Zero, INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, ref length);
                if (!ok) return Marshal.GetLastWin32Error();
                if (!set)
                    for (int i = 0; i < options.Length; i++)
                        options[i] = (Option)Marshal.PtrToStructure(new IntPtr(array.ToInt64() + i * size), typeof(Option));
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(array);
                if (conn != IntPtr.Zero) Marshal.FreeHGlobal(conn);
            }
        }

        // WinINet allocates the strings it returns; the caller frees them with GlobalFree.
        static string TakeString(Option option)
        {
            if (option.Value.pszValue == IntPtr.Zero) return null;
            string text = Marshal.PtrToStringUni(option.Value.pszValue);
            GlobalFree(option.Value.pszValue);
            return text;
        }

        public static ConnSettings Query(string connection)
        {
            ConnSettings s = new ConnSettings();
            s.Connection = connection;
            s.FlagsSource = "FLAGS_UI";
            Option[] o = Options(INTERNET_PER_CONN_FLAGS_UI, INTERNET_PER_CONN_PROXY_SERVER, INTERNET_PER_CONN_PROXY_BYPASS,
                                 INTERNET_PER_CONN_AUTOCONFIG_URL, INTERNET_PER_CONN_AUTODISCOVERY_FLAGS);
            int error = Call(false, connection, o);
            if (error != 0)
            {
                // The documented fallback when FLAGS_UI is not there (before IE8): ask for FLAGS.
                o = Options(INTERNET_PER_CONN_FLAGS, INTERNET_PER_CONN_PROXY_SERVER, INTERNET_PER_CONN_PROXY_BYPASS,
                            INTERNET_PER_CONN_AUTOCONFIG_URL, INTERNET_PER_CONN_AUTODISCOVERY_FLAGS);
                s.FlagsSource = "FLAGS";
                error = Call(false, connection, o);
                if (error != 0)
                    throw new InvalidOperationException("InternetQueryOption failed with Win32 error " + error);
            }
            s.Flags = o[0].Value.dwValue;
            s.ProxyServer = TakeString(o[1]);
            s.ProxyBypass = TakeString(o[2]);
            s.AutoConfigUrl = TakeString(o[3]);
            s.AutoDiscoveryFlags = o[4].Value.dwValue;
            Option[] legacy = Options(INTERNET_PER_CONN_FLAGS);
            s.LegacyFlags = Call(false, connection, legacy) == 0 ? legacy[0].Value.dwValue : -1;
            return s;
        }

        // Writes the connection type, the proxy server and the bypass list. The auto-config URL is left as it is:
        // taking PROXY_TYPE_AUTO_PROXY_URL out of the flags is what switches it off, and putting the flags back
        // switches it on again. Microsoft's guidance: query with FLAGS_UI, but write the connection type with FLAGS.
        public static int Set(string connection, int flags, string proxyServer, string proxyBypass)
        {
            Option[] o = Options(INTERNET_PER_CONN_FLAGS, INTERNET_PER_CONN_PROXY_SERVER, INTERNET_PER_CONN_PROXY_BYPASS);
            IntPtr server = proxyServer == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(proxyServer);
            IntPtr bypass = proxyBypass == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(proxyBypass);
            try
            {
                o[0].Value.dwValue = flags;
                o[1].Value.pszValue = server;
                o[2].Value.pszValue = bypass;
                return Call(true, connection, o);
            }
            finally
            {
                if (server != IntPtr.Zero) Marshal.FreeHGlobal(server);
                if (bypass != IntPtr.Zero) Marshal.FreeHGlobal(bypass);
            }
        }

        // With a null buffer the notification goes to every WinINet instance. Returns 0 or the Win32 error.
        public static int Notify(int option)
        {
            return InternetSetOptionNoBuffer(IntPtr.Zero, option, IntPtr.Zero, 0) ? 0 : Marshal.GetLastWin32Error();
        }
    }
}
'@
if (-not ('JosourSpike.WinInet' -as [type])) { Add-Type -TypeDefinition $winInetSource -Language CSharp }

# The dummy listener, run in a runspace of its own so the main thread stays free for the keyboard. Same routing as
# dummy-listener.pl, the macOS one: it binds 127.0.0.1 only and forwards nothing.
$listenerScript = {
    param($Listener, $State, $PagesDir, $LogPath)

    $gif = [Convert]::FromBase64String('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==')
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $utf8 = New-Object Text.UTF8Encoding $false

    function Get-Family([string] $Ua) {
        if ($Ua -match 'Edg/') { return 'edge' }
        if ($Ua -match 'Firefox/') { return 'firefox' }
        if ($Ua -match 'Chrome/') { return 'chrome' }
        if ($Ua -match 'Safari/') { return 'safari' }
        return 'other'
    }

    function Get-Page([string] $File) {
        $path = Join-Path $PagesDir $File
        if (-not (Test-Path -LiteralPath $path)) { return ,($utf8.GetBytes("missing page: $File")) }
        $token = -join (1..10 | ForEach-Object { 'abcdefghijklmnopqrstuvwxyz0123456789'[(Get-Random -Maximum 36)] })
        return ,($utf8.GetBytes([IO.File]::ReadAllText($path).Replace('{{TOKEN}}', $token)))
    }

    function Send-Reply($Stream, [string] $Status, [string] $Type, [byte[]] $Body) {
        $head = $latin1.GetBytes("HTTP/1.1 $Status`r`nContent-Type: $Type`r`nContent-Length: $($Body.Length)`r`nCache-Control: no-store`r`nX-Josour-Spike: listener`r`nConnection: close`r`n`r`n")
        $Stream.Write($head, 0, $head.Length)
        $Stream.Write($Body, 0, $Body.Length)
        $Stream.Flush()
    }

    $buffer = New-Object byte[] 4096
    $html = 'text/html; charset=utf-8'
    while (-not $State.Stop) {
        if (-not $Listener.Pending()) { Start-Sleep -Milliseconds 20; continue }
        $client = $null
        try {
            $client = $Listener.AcceptTcpClient()
            $client.ReceiveTimeout = 1500
            $stream = $client.GetStream()
            $head = ''
            $deadline = [DateTime]::UtcNow.AddSeconds(3)
            while ($head.IndexOf("`r`n`r`n") -lt 0 -and $head.Length -lt 16384 -and [DateTime]::UtcNow -lt $deadline) {
                $n = $stream.Read($buffer, 0, $buffer.Length)
                if ($n -le 0) { break }
                $head += $latin1.GetString($buffer, 0, $n)
            }
            if ($head.Length -eq 0) { continue }   # a speculative connection that never spoke

            $parts = @((($head -split "`r?`n", 2)[0]) -split ' ')
            $method = $parts[0]
            $uri = ''
            if ($parts.Count -gt 1) { $uri = $parts[1] }
            $ua = ''
            if ($head -match '(?im)^User-Agent:[ \t]*([^\r\n]*)') { $ua = $Matches[1] }
            $hostName = ''
            if ($head -match '(?im)^Host:[ \t]*([^\r\n]*)') { $hostName = $Matches[1] }

            $kind = 'direct'
            $path = $uri
            if ($method -eq 'CONNECT') {
                $kind = 'connect'; $hostName = $uri; $path = ''
            }
            elseif ($uri -match '^http://([^/?#]*)(.*)$') {
                $kind = 'proxy'; $hostName = $Matches[1]; $path = $Matches[2]
                if ($path -eq '') { $path = '/' }
            }
            $name = ($hostName.ToLowerInvariant() -replace ':\d+$', '') -replace '^\[(.*)\]$', '$1'

            $label = '-'
            if ($path -match '[?&]b=([A-Za-z0-9_.-]{1,40})') { $label = $Matches[1] }
            elseif ($kind -eq 'connect' -and $name -eq 'check.josour') { $label = (Get-Family $ua) + '-https' }

            $target = $hostName + $path
            if ($kind -eq 'connect') { $target = $hostName }
            $target = $target -replace '[\t\r\n]+', ' '
            if ($target.Length -gt 200) { $target = $target.Substring(0, 200) }
            $uaLog = $ua -replace '[\t\r\n]+', ' '
            if ($uaLog.Length -gt 200) { $uaLog = $uaLog.Substring(0, 200) }
            $line = [string]::Join("`t", @([string][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(), $kind, $label, $target, $uaLog))
            [void]$State.Hits.Add($line)
            [IO.File]::AppendAllText($LogPath, $line + "`n")

            $plain = $kind -ne 'connect'
            $isLocal = ($name -eq '127.0.0.1') -or ($name -eq 'localhost') -or ($name -eq '::1')
            if ($plain -and $name -eq 'check.josour' -and $path.StartsWith('/ping')) { Send-Reply $stream '200 OK' 'image/gif' $gif }
            elseif ($plain -and $name -eq 'check.josour') { Send-Reply $stream '200 OK' $html (Get-Page 'check.html') }
            elseif ($plain -and $isLocal -and $path.StartsWith('/webrtc')) { Send-Reply $stream '200 OK' $html (Get-Page 'webrtc.html') }
            elseif ($plain -and $isLocal -and $path.StartsWith('/dns')) { Send-Reply $stream '200 OK' $html (Get-Page 'dns.html') }
            elseif ($plain -and $isLocal -and $path -match '^/(index\.html)?(\?.*)?$') { Send-Reply $stream '200 OK' $html (Get-Page 'index.html') }
            else {
                Send-Reply $stream '502 Bad Gateway' 'text/plain; charset=utf-8' ($utf8.GetBytes("Josour spike listener: not forwarded. Nothing leaves this machine through it.`n"))
            }
        }
        catch { }
        finally { if ($null -ne $client) { $client.Close() } }
    }
}

# --- small helpers -----------------------------------------------------------------------------------------------

function Get-NowMs { return [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }

function Get-RegValue([string] $Path, [string] $Name) {
    $item = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    return $item.$Name
}

function Format-Flags([int] $Flags) {
    $names = @()
    if ($Flags -band 1) { $names += 'direct' }
    if ($Flags -band 2) { $names += 'proxy' }
    if ($Flags -band 4) { $names += 'auto-config' }
    if ($Flags -band 8) { $names += 'auto-detect' }
    return ('{0} ({1})' -f $Flags, ($names -join '+'))
}

function Format-Text($Value) {
    if ($null -eq $Value) { return '(none)' }
    return "'$Value'"
}

function Get-ConnectionLabel($Connection) {
    if ($null -eq $Connection) { return 'LAN' }
    return "RAS '$Connection'"
}

function Get-SettingsLine($S) {
    return ('flags {0}; proxy {1}; bypass {2}; auto-config url {3}' -f (Format-Flags $S.Flags), (Format-Text $S.ProxyServer), (Format-Text $S.ProxyBypass), (Format-Text $S.AutoConfigUrl))
}

function Test-SameText($A, $B) { return ([string]$A) -ceq ([string]$B) }   # null and '' count as the same

function Get-Win32Message([int] $Code) { return (New-Object ComponentModel.Win32Exception $Code).Message }

# --- reading the settings ----------------------------------------------------------------------------------------

function Get-ConnectionNames {
    # null stands for the LAN settings. The dial-up and VPN (RAS) connections have settings of their own, which
    # WinINet uses instead of the LAN ones while such a connection is up; their names are the binary values under
    # ...\Internet Settings\Connections, apart from the two that hold the LAN settings.
    $names = New-Object System.Collections.ArrayList
    [void]$names.Add($null)
    if (-not $LanOnly) {
        $key = Get-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections' -ErrorAction SilentlyContinue
        if ($null -ne $key) {
            foreach ($name in $key.GetValueNames()) {
                if ($name -eq '' -or $name -eq 'DefaultConnectionSettings' -or $name -eq 'SavedLegacySettings') { continue }
                if ($key.GetValueKind($name) -ne [Microsoft.Win32.RegistryValueKind]::Binary) { continue }
                [void]$names.Add($name)
            }
        }
    }
    return ,$names.ToArray()
}

function Read-Connection($Connection) { return [JosourSpike.WinInet]::Query($Connection) }

function Get-LegacyRegistryLine {
    # The values WinINet mirrors for the LAN settings, which older code and some apps still read directly.
    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $parts = foreach ($name in 'ProxyEnable', 'ProxyServer', 'ProxyOverride', 'AutoConfigURL', 'AutoDetect') {
        '{0}={1}' -f $name, (Format-Text (Get-RegValue $path $name))
    }
    return ($parts -join '  ')
}

function Get-PolicyReport {
    $checks = New-Object System.Collections.ArrayList
    [void]$checks.Add('HKLM:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings|ProxySettingsPerUser')
    foreach ($root in 'HKCU:', 'HKLM:') {
        foreach ($n in 'Proxy', 'AutoConfig', 'ConnectionsTab') {
            [void]$checks.Add("$root\SOFTWARE\Policies\Microsoft\Internet Explorer\Control Panel|$n")
        }
        foreach ($n in 'ProxyEnable', 'ProxyServer', 'ProxyOverride', 'AutoConfigURL') {
            [void]$checks.Add("$root\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings|$n")
        }
        foreach ($browser in 'Google\Chrome', 'Microsoft\Edge') {
            foreach ($n in 'ProxyMode', 'ProxyServer', 'ProxyPacUrl', 'ProxySettings') {
                [void]$checks.Add("$root\SOFTWARE\Policies\$browser|$n")
            }
        }
        foreach ($n in 'Mode', 'Locked') { [void]$checks.Add("$root\SOFTWARE\Policies\Mozilla\Firefox\Proxy|$n") }
    }
    $found = New-Object System.Collections.ArrayList
    foreach ($check in $checks) {
        $path, $name = $check -split '\|', 2
        $value = Get-RegValue $path $name
        if ($null -ne $value) { [void]$found.Add(('{0} :: {1} = {2}' -f $path, $name, ($value -join ','))) }
    }
    return ,$found.ToArray()
}

function Get-AccountInfo {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal $identity
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    # A filtered (non-elevated) admin token still lists Administrators, as deny-only; a standard account does not.
    $member = $false
    try {
        $rows = whoami /groups /fo csv /nh | ConvertFrom-Csv -Header Name, Type, Sid, Attributes
        $member = @($rows | Where-Object { $_.Sid -eq 'S-1-5-32-544' }).Count -gt 0
    }
    catch { }
    $account = 'standard'
    if ($member) { $account = 'admin' }
    return [pscustomobject]@{ Account = $account; Elevated = $elevated }
}

# --- changing them -----------------------------------------------------------------------------------------------

$script:steps = New-Object System.Collections.ArrayList

function Add-Step([string] $Phase, [string] $Target, [string] $What, [int] $Code, [long] $Ms) {
    $verdict = 'ok'
    $detail = ''
    if ($Code -ne 0) { $verdict = 'failed'; $detail = 'Win32 error {0}: {1}' -f $Code, (Get-Win32Message $Code) }
    [void]$script:steps.Add([pscustomobject]@{ Phase = $Phase; Target = $Target; What = $What; Verdict = $verdict; Ms = $Ms; Code = $Code; Detail = $detail })
    Write-Host ('    {0,-7} {1,-22} {2,-24} {3,6} ms  {4}' -f $verdict, $Target, $What, $Ms, $detail)
}

function Invoke-Notify([string] $Phase) {
    $notifications = [ordered]@{
        'SETTINGS_CHANGED'       = [JosourSpike.WinInet]::INTERNET_OPTION_SETTINGS_CHANGED
        'PROXY_SETTINGS_CHANGED' = [JosourSpike.WinInet]::INTERNET_OPTION_PROXY_SETTINGS_CHANGED
        'REFRESH'                = [JosourSpike.WinInet]::INTERNET_OPTION_REFRESH
    }
    foreach ($name in $notifications.Keys) {
        $t0 = Get-NowMs
        $code = [JosourSpike.WinInet]::Notify($notifications[$name])
        Add-Step $Phase 'all WinINet clients' $name $code ((Get-NowMs) - $t0)
    }
}

function Invoke-Apply {
    foreach ($s in $script:snapshot) {
        $t0 = Get-NowMs
        $code = [JosourSpike.WinInet]::Set($s.Connection, $script:ourFlags, $script:ourServer, $script:ourBypass)
        Add-Step 'apply' (Get-ConnectionLabel $s.Connection) 'set per-connection' $code ((Get-NowMs) - $t0)
    }
    Invoke-Notify 'apply'
}

function Invoke-Restore {
    # Only connections that differ from the snapshot are written, so a restore never undoes what is not ours to undo.
    foreach ($s in $script:snapshot) {
        $label = Get-ConnectionLabel $s.Connection
        $now = $null
        try { $now = Read-Connection $s.Connection } catch { }
        if ($null -ne $now -and $now.Flags -eq [int]$s.Flags -and (Test-SameText $now.ProxyServer $s.ProxyServer) -and (Test-SameText $now.ProxyBypass $s.ProxyBypass)) {
            Write-Host "    $label nothing to put back"
            continue
        }
        $t0 = Get-NowMs
        $code = [JosourSpike.WinInet]::Set($s.Connection, [int]$s.Flags, $s.ProxyServer, $s.ProxyBypass)
        Add-Step 'restore' $label 'set per-connection' $code ((Get-NowMs) - $t0)
    }
    Invoke-Notify 'restore'
}

function Test-Applied {
    $all = $true
    foreach ($s in $script:snapshot) {
        $label = Get-ConnectionLabel $s.Connection
        try { $now = Read-Connection $s.Connection }
        catch { Write-Host "    $label could not be read back: $($_.Exception.Message)"; $all = $false; continue }
        $ok = (($now.Flags -band 0x0F) -eq $script:ourFlags) -and (Test-SameText $now.ProxyServer $script:ourServer) -and (Test-SameText $now.ProxyBypass $script:ourBypass)
        if ($ok) { Write-Host "    $label reads back as set" }
        else {
            $all = $false
            $line = "$label reads back DIFFERENT: $(Get-SettingsLine $now)"
            Write-Host "    $line"
            [void]$script:applyDiff.Add($line)
        }
    }
    return $all
}

function Test-Restored {
    $all = $true
    $script:restoreDiff.Clear()
    foreach ($s in $script:snapshot) {
        $label = Get-ConnectionLabel $s.Connection
        try { $now = Read-Connection $s.Connection }
        catch {
            $all = $false
            $line = "$label could not be read back: $($_.Exception.Message)"
            Write-Host "    $line"; [void]$script:restoreDiff.Add($line)
            continue
        }
        $diffs = @()
        if ($now.Flags -ne [int]$s.Flags) { $diffs += "flags before $(Format-Flags $s.Flags), now $(Format-Flags $now.Flags)" }
        foreach ($field in 'ProxyServer', 'ProxyBypass', 'AutoConfigUrl') {
            if (-not (Test-SameText $now.$field $s.$field)) { $diffs += "$field before $(Format-Text $s.$field), now $(Format-Text $now.$field)" }
        }
        if ($diffs.Count -eq 0) { Write-Host "    $label exactly as before" }
        else {
            $all = $false
            $line = "$label NOT as before: $($diffs -join '; ')"
            Write-Host "    $line"
            [void]$script:restoreDiff.Add($line)
        }
    }
    return $all
}

# --- the listener and what reached it ----------------------------------------------------------------------------

$script:listener = $null
$script:listenerPs = $null
$script:listenerHandle = $null
$script:state = $null
$script:hitIndex = 0
$script:announced = @{}
$script:applyStartMs = 0
$script:applyDoneMs = 0
$script:restoreDoneMs = 0

function Start-Listener {
    $l = New-Object System.Net.Sockets.TcpListener -ArgumentList ([System.Net.IPAddress]::Loopback), $Port
    try { $l.Start() }
    catch { throw "Cannot listen on 127.0.0.1:${Port}: $($_.Exception.Message). Pick another -Port." }
    $script:listener = $l
    $script:state = [hashtable]::Synchronized(@{ Stop = $false; Hits = [System.Collections.ArrayList]::Synchronized((New-Object System.Collections.ArrayList)) })
    $script:listenerPs = [PowerShell]::Create()
    [void]$script:listenerPs.AddScript($listenerScript).AddArgument($l).AddArgument($script:state).AddArgument($script:pagesDir).AddArgument($script:listenerLog)
    $script:listenerHandle = $script:listenerPs.BeginInvoke()
}

function Stop-Listener {
    if ($null -eq $script:listenerPs) { return }
    $script:state.Stop = $true
    if (-not $script:listenerHandle.AsyncWaitHandle.WaitOne(5000)) { $script:listenerPs.Stop() }
    try { $script:listener.Stop() } catch { }
    foreach ($e in $script:listenerPs.Streams.Error) { Add-Content -LiteralPath (Join-Path $Out 'listener-errors.txt') -Value $e.ToString() }
    $script:listenerPs.Dispose()
    $script:listenerPs = $null
}

function Get-Hits {
    $rows = New-Object System.Collections.ArrayList
    if ($null -ne $script:state) {
        foreach ($line in $script:state.Hits.ToArray()) {
            $f = ([string]$line) -split "`t"
            if ($f.Count -ge 5) { [void]$rows.Add([pscustomobject]@{ Ms = [long]$f[0]; Kind = $f[1]; Label = $f[2]; Target = $f[3] }) }
        }
    }
    return ,$rows.ToArray()
}

function Show-NewHits {
    # Prints each label (browser, probe) the first time it reaches the listener after the apply began.
    if ($null -eq $script:state) { return }
    while ($script:hitIndex -lt $script:state.Hits.Count) {
        $f = ([string]$script:state.Hits[$script:hitIndex]) -split "`t"
        $script:hitIndex++
        if ($f.Count -lt 5 -or $f[2] -eq '-' -or [long]$f[0] -lt $script:applyStartMs -or $script:announced.ContainsKey($f[2])) { continue }
        $script:announced[$f[2]] = $true
        Write-Host ('  + {0,-18} reached the listener {1:+0.0;-0.0} s after the apply finished ({2})' -f $f[2], (([long]$f[0] - $script:applyDoneMs) / 1000.0), $f[3])
    }
}

function Get-HitTable {
    $lines = New-Object System.Collections.ArrayList
    $first = @{}; $last = @{}; $count = @{}
    $order = New-Object System.Collections.ArrayList
    foreach ($h in (Get-Hits)) {
        if ($h.Label -eq '-' -or $h.Ms -lt $script:applyStartMs) { continue }
        if (-not $first.ContainsKey($h.Label)) { $first[$h.Label] = $h.Ms; $count[$h.Label] = 0; [void]$order.Add($h.Label) }
        $last[$h.Label] = $h.Ms
        $count[$h.Label] = $count[$h.Label] + 1
    }
    if ($order.Count -eq 0) { [void]$lines.Add('   (no browser and no probe reached it: nothing picked the proxy up)') }
    foreach ($label in $order) {
        $line = '   {0,-18} first {1,6:+0.0;-0.0} s after the apply, {2,5} requests' -f $label, (($first[$label] - $script:applyDoneMs) / 1000.0), $count[$label]
        if ($script:restoreDoneMs -gt 0) { $line += (', last {0,6:+0.0;-0.0} s after the restore' -f (($last[$label] - $script:restoreDoneMs) / 1000.0)) }
        [void]$lines.Add($line)
    }
    return ,$lines.ToArray()
}

function Get-OtherHosts {
    $hosts = @{}
    foreach ($h in (Get-Hits)) {
        if ($h.Label -ne '-' -or $h.Kind -eq 'direct') { continue }
        $name = ($h.Target -split '/', 2)[0]
        $hosts[$name] = 1 + [int]$hosts[$name]
    }
    $rows = New-Object System.Collections.ArrayList
    foreach ($k in $hosts.Keys) { [void]$rows.Add([pscustomobject]@{ Count = $hosts[$k]; Host = $k }) }
    if ($rows.Count -eq 0) { return ,$rows.ToArray() }
    return ,@($rows | Sort-Object Count -Descending)
}

function Wait-Hold {
    $deadline = $null
    if ($HoldSeconds -gt 0) {
        $deadline = (Get-Date).AddSeconds($HoldSeconds)
        Write-Host "  Holding for $HoldSeconds s. Enter or Ctrl+C restores sooner."
    }
    else { Write-Host '  Press Enter to restore (Ctrl+C restores too).' }
    if ($null -eq $deadline -and -not $script:canPollKeys) {
        [void](Read-Host '  ')
        Show-NewHits
        return
    }
    while ($true) {
        Show-NewHits
        if ($null -ne $deadline -and (Get-Date) -ge $deadline) { break }
        if ($script:canPollKeys) {
            $enter = $false
            while ([Console]::KeyAvailable) { if ([Console]::ReadKey($true).Key -eq [ConsoleKey]::Enter) { $enter = $true } }
            if ($enter) { break }
        }
        Start-Sleep -Milliseconds 300
    }
    Show-NewHits
}

# --- the summary -------------------------------------------------------------------------------------------------

function Write-Summary([string] $Outcome) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('Josour spike S2: the Windows system proxy (ADR-0015)')
    $lines.Add("run      : $Outcome, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $lines.Add("windows  : $script:windows")
    $lines.Add("account  : $($script:accountInfo.Account), elevated: $($script:accountInfo.Elevated)")
    $lines.Add("shell    : PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition)), $([IntPtr]::Size * 8)-bit")
    $lines.Add("port     : $Port")
    $lines.Add("proxy set: $script:ourServer")
    $lines.Add("bypass   : $script:ourBypass")
    $lockText = 'not set'
    if ($null -ne $script:perUser) { $lockText = "$script:perUser" }
    if ($script:locked) { $lockText += ' (LOCKED: per machine, not per user)' }
    $lines.Add("policy   : ProxySettingsPerUser = $lockText")
    foreach ($p in $script:policy) { $lines.Add("           $p") }
    $lines.Add("touched  : $((@($script:snapshot | ForEach-Object { Get-ConnectionLabel $_.Connection })) -join ', ')")
    $lines.Add('')
    $lines.Add('1. WinINet calls')
    if ($script:steps.Count -eq 0) { $lines.Add('   (no calls made)') }
    foreach ($st in $script:steps) {
        $lines.Add(('   {0,-7} {1,-22} {2,-24} {3,-6} {4,6} ms  {5}' -f $st.Phase, $st.Target, $st.What, $st.Verdict, $st.Ms, $st.Detail))
    }
    $failed = @($script:steps | Where-Object { $_.Verdict -ne 'ok' })
    if ($script:steps.Count -eq 0) { $lines.Add('   => nothing was applied') }
    elseif ($failed.Count -eq 0) { $lines.Add("   => every call succeeded on this $($script:accountInfo.Account) account") }
    else { $lines.Add("   => $($failed.Count) call(s) FAILED on this $($script:accountInfo.Account) account") }
    $lines.Add('')
    $lines.Add('2. read-back')
    $applyText = 'not reached'
    if ($null -ne $script:applyOk) { if ($script:applyOk) { $applyText = 'as set' } else { $applyText = 'DIFFERENT from what was set' } }
    $lines.Add("   after the apply  : $applyText")
    foreach ($d in $script:applyDiff) { $lines.Add("     $d") }
    $restoreText = 'not reached'
    if ($null -ne $script:restoreOk) {
        if ($script:restoreOk) { $restoreText = 'exactly as before' }
        else { $restoreText = "NOT as before. Fix it in Settings > Network & internet > Proxy, or rerun with -RestoreFrom '$script:snapshotPath'" }
    }
    $lines.Add("   after the restore: $restoreText")
    foreach ($d in $script:restoreDiff) { $lines.Add("     $d") }
    $lines.Add("   registry before  : $script:registryBefore")
    $lines.Add("   registry applied : $script:registryApplied")
    $lines.Add("   registry after   : $script:registryAfter")
    $lines.Add('')
    $lines.Add('3. who used the proxy (the dummy listener; seconds after the end of the apply and of the restore)')
    foreach ($l in (Get-HitTable)) { $lines.Add($l) }
    $others = Get-OtherHosts
    $total = 0
    foreach ($o in $others) { $total += $o.Count }
    $lines.Add("   other requests that reached the listener: $total (by host: listener-hosts.txt)")
    $lines.Add('')
    $lines.Add('4. before: the snapshot the restore put back')
    foreach ($s in $script:snapshot) {
        $lines.Add(('   {0,-14} {1}' -f (Get-ConnectionLabel $s.Connection), (Get-SettingsLine $s)))
        $lines.Add(('   {0,-14} read with {1}; legacy FLAGS {2}; auto-discovery flags {3}' -f '', $s.FlagsSource, $s.LegacyFlags, $s.AutoDiscoveryFlags))
    }

    $summaryPath = Join-Path $Out 'summary.txt'
    Set-Content -LiteralPath $summaryPath -Value $lines -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Out 'listener-hosts.txt') -Value @($others | ForEach-Object { '{0,7}  {1}' -f $_.Count, $_.Host }) -Encoding UTF8
    if ($script:steps.Count -gt 0) { $script:steps | Export-Csv -LiteralPath (Join-Path $Out 'commands.tsv') -Delimiter "`t" -NoTypeInformation -Encoding UTF8 }
    Write-Host ''
    Write-Host "================ $summaryPath ================"
    foreach ($l in $lines) { Write-Host $l }
    Write-Host ''
    Write-Host 'Send back summary.txt. It shows your previous proxy settings and policies, and listener-hosts.txt'
    Write-Host 'lists the hosts other apps tried to reach; look through them before sharing.'
}

# --- the run -----------------------------------------------------------------------------------------------------

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$script:ourFlags = [JosourSpike.WinInet]::PROXY_TYPE_DIRECT -bor [JosourSpike.WinInet]::PROXY_TYPE_PROXY
$script:ourServer = 'http=127.0.0.1:{0};https=127.0.0.1:{0}' -f $Port
$bypassEntries = @('localhost', '127.*', '[::1]', '10.*') + @(16..31 | ForEach-Object { "172.$_.*" }) + @('192.168.*', '169.254.*') + @($Bypass) + @('<local>')
$script:ourBypass = (@($bypassEntries | Where-Object { $_ }) -join ';')
$script:applyDiff = New-Object System.Collections.ArrayList
$script:restoreDiff = New-Object System.Collections.ArrayList
$script:applyOk = $null
$script:restoreOk = $null
$script:snapshotPath = ''
$script:registryBefore = ''
$script:registryApplied = '(not applied)'
$script:registryAfter = '(not restored)'

$script:interactive = $false
try { $script:interactive = -not [Console]::IsInputRedirected } catch { }
$script:canPollKeys = $false
if ($script:interactive) { try { [void][Console]::KeyAvailable; $script:canPollKeys = $true } catch { } }

if ($RestoreFrom) {
    $doc = Get-Content -LiteralPath $RestoreFrom -Raw | ConvertFrom-Json
    $script:snapshot = @($doc.connections)
    $script:snapshotPath = $RestoreFrom
    $Out = Join-Path (Split-Path -Parent (Resolve-Path -LiteralPath $RestoreFrom).Path) "restore-$stamp"
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    Write-Host "== restoring from $RestoreFrom =="
    Invoke-Restore
    Write-Host ''
    Write-Host '== read back =='
    $ok = Test-Restored
    if ($script:steps.Count -gt 0) { $script:steps | Export-Csv -LiteralPath (Join-Path $Out 'commands.tsv') -Delimiter "`t" -NoTypeInformation -Encoding UTF8 }
    if ($ok) { Write-Host '  => exactly as before'; exit 0 }
    Write-Host '  => NOT as before; fix it in Settings > Network & internet > Proxy'
    exit 2
}

$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$caption = 'Windows'
if ($null -ne $os) { $caption = $os.Caption }
$ntKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$script:windows = '{0} {1} (build {2}.{3})' -f $caption, (Get-RegValue $ntKey 'DisplayVersion'), [Environment]::OSVersion.Version.Build, (Get-RegValue $ntKey 'UBR')
$script:accountInfo = Get-AccountInfo
$script:policy = Get-PolicyReport
$script:perUser = Get-RegValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings' 'ProxySettingsPerUser'
$script:locked = ($null -ne $script:perUser) -and ([int]$script:perUser -eq 0)

$script:snapshot = @()
foreach ($c in (Get-ConnectionNames)) {
    try { $script:snapshot += Read-Connection $c }
    catch {
        if ($null -eq $c) { throw "Cannot read the LAN proxy settings ($($_.Exception.Message)); nothing was changed." }
        Write-Warning "Skipping RAS connection '$c': $($_.Exception.Message)"
    }
}
$script:registryBefore = Get-LegacyRegistryLine

Write-Host '== Josour spike S2: the Windows system proxy =='
Write-Host "  $script:windows; $($script:accountInfo.Account) account, elevated: $($script:accountInfo.Elevated); PowerShell $($PSVersionTable.PSVersion), $([IntPtr]::Size * 8)-bit"
if ($script:accountInfo.Elevated) { Write-Warning 'This PowerShell is elevated. The app never is: run it from a normal window for the real answer.' }
if ($script:locked) { Write-Warning 'Group Policy sets ProxySettingsPerUser=0: proxy settings are per machine here, not per user.' }
if ($script:policy.Count -eq 0) { Write-Host '  policy: nothing that locks or overrides the proxy' }
else { Write-Host '  policy:'; foreach ($p in $script:policy) { Write-Host "    $p" } }
Write-Host '  current settings:'
foreach ($s in $script:snapshot) { Write-Host ('    {0,-14} {1}' -f (Get-ConnectionLabel $s.Connection), (Get-SettingsLine $s)) }
Write-Host "  registry (LAN mirror): $script:registryBefore"

if ($DryRun) {
    Write-Host ''
    Write-Host "  It would set $script:ourServer"
    Write-Host "  with the bypass list $script:ourBypass"
    $probe = New-Object System.Net.Sockets.TcpListener -ArgumentList ([System.Net.IPAddress]::Loopback), $Port
    try { $probe.Start(); $probe.Stop(); Write-Host "  port $Port is free" } catch { Write-Host "  port $Port is IN USE: pick another -Port" }
    Write-Host ''
    Write-Host 'Dry run: nothing was changed.'
    exit 0
}

$script:pagesDir = Join-Path $PSScriptRoot 'pages'
if (-not (Test-Path -LiteralPath (Join-Path $script:pagesDir 'index.html'))) {
    throw 'Copy the whole scripts\spikes folder: pages\ must sit next to this script.'
}
if (-not $Out) { $Out = Join-Path $env:USERPROFILE "josour-spike-results\s2-windows-$stamp" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path
$script:snapshotPath = Join-Path $Out 'snapshot.json'
$script:listenerLog = Join-Path $Out 'listener.log'
[ordered]@{ version = 1; port = $Port; taken = (Get-Date).ToString('o'); connections = @($script:snapshot) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $script:snapshotPath -Encoding UTF8
if (-not $script:interactive -and $HoldSeconds -eq 0) { $HoldSeconds = 60; Write-Host '  (no console to press Enter in: holding for 60 s)' }

$applyAllowed = $true
if ($script:locked -and -not $ApplyEvenIfLocked) {
    $applyAllowed = $false
    if ($script:interactive) {
        $answer = Read-Host '  Apply anyway, to see what WinINet does under the lock? [y/N]'
        $applyAllowed = $answer -match '^(y|yes)$'
    }
}

$applied = $false
$restored = $false
$outcome = 'complete'
try {
    if (-not $applyAllowed) {
        $outcome = 'not applied (ProxySettingsPerUser=0)'
    }
    else {
        Start-Listener
        Write-Host ''
        Write-Host '== 1. open the poller =='
        Write-Host "  Open http://127.0.0.1:$Port/ in Edge, Chrome and Firefox, side by side. Red is right for now."
        Write-Host '  For the .NET question, start scripts\spikes\proxy-probe in another window now (see the runbook).'
        Write-Host "  The snapshot is in $script:snapshotPath; if this window is ever closed mid-run, restore it with:"
        Write-Host "    powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -RestoreFrom `"$script:snapshotPath`""
        if ($script:interactive) { [void](Read-Host '  Press Enter to APPLY the proxy (Ctrl+C quits, with nothing changed)') }

        Write-Host ''
        Write-Host '== 2. apply =='
        $applied = $true   # before the first change, so an interruption half-way through still restores
        $script:applyStartMs = Get-NowMs
        Invoke-Apply
        $script:applyDoneMs = Get-NowMs
        $script:registryApplied = Get-LegacyRegistryLine

        Write-Host ''
        Write-Host '== 3. read back =='
        $script:applyOk = Test-Applied
        Write-Host "  registry (LAN mirror): $script:registryApplied"

        Write-Host ''
        Write-Host '== 4. look at the browsers =='
        Wait-Hold

        Write-Host ''
        Write-Host '== 5. restore =='
        Invoke-Restore
        $script:restoreDoneMs = Get-NowMs
        $restored = $true
        $script:registryAfter = Get-LegacyRegistryLine

        Write-Host ''
        Write-Host '== 6. read back =='
        $script:restoreOk = Test-Restored

        if ($LingerSeconds -gt 0) {
            Write-Host ''
            Write-Host "  The listener stays up $LingerSeconds s more, to catch anything still using the proxy after the restore."
            $end = (Get-Date).AddSeconds($LingerSeconds)
            while ((Get-Date) -lt $end) { Start-Sleep -Milliseconds 500 }
        }
    }
}
catch {
    $outcome = "error: $($_.Exception.Message)"
    Write-Warning $outcome
}
finally {
    if ($applied -and -not $restored) {
        if ($outcome -eq 'complete') { $outcome = 'interrupted' }
        Write-Host ''
        Write-Host '== restoring after an interruption =='
        Invoke-Restore
        $script:restoreDoneMs = Get-NowMs
        $restored = $true
        $script:registryAfter = Get-LegacyRegistryLine
        $script:restoreOk = Test-Restored
    }
    Stop-Listener
    Write-Summary $outcome
}
if ($script:restoreOk -eq $false) { exit 2 }
exit 0
