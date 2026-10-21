using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Josour.Core.Net;
using Microsoft.Win32;

namespace Josour.Infrastructure.SystemProxy;

/// <summary>
/// WINDOWS: the current user's WinINet settings ("Internet Options", which Edge, Chrome and Firefox in its default
/// "use system proxy settings" mode all follow), changed per connection through <c>InternetSetOptionW</c> with no
/// administrator rights: the LAN settings and every dial-up/VPN connection, which WinINet uses instead while it is up.
/// <para>
/// Applied: a fixed proxy (<c>http=127.0.0.1:P;https=127.0.0.1:P</c>), the bypass list, automatic detection and the PAC
/// script off — a fixed proxy rather than a PAC with a DIRECT fallback, so a dead app stops browsing instead of leaking the
/// guest's own address (ADR-0015 point 2). Every running WinINet client is then told the settings changed.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemProxyController : ISystemProxyController
{
    public const string PlatformName = "windows-wininet";

    private const string ConnectionsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>What never goes to the proxy: plain names, loopback, private and link-local ranges, mDNS names.</summary>
    public static readonly IReadOnlyList<string> BaseBypass = BuildBaseBypass();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    public bool IsSupported => true;

    public Task<SystemProxySnapshot> CaptureAsync(CancellationToken ct) => Task.Run(() =>
    {
        var saved = new List<SavedConnection>();
        foreach (var connection in ConnectionNames())
        {
            var now = WinInet.Query(connection)
                ?? throw new SystemProxyException("read_failed", $"the proxy settings of {Label(connection)} could not be read");
            saved.Add(new SavedConnection(connection, now.Flags, now.ProxyServer, now.ProxyBypass));
        }
        return new SystemProxySnapshot(PlatformName, JsonSerializer.Serialize(saved, Json));
    }, ct);

    public Task ApplyAsync(SystemProxyTarget target, CancellationToken ct) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(target);
        if (PolicyForcesMachineSettings())
        {
            throw new SystemProxyException("policy_locked",
                "Group Policy keeps the proxy settings per machine (ProxySettingsPerUser=0), which only an administrator can change");
        }

        var server = ServerString(target.Port);
        var bypass = BypassString(target.ExtraBypass);
        foreach (var connection in ConnectionNames())
        {
            var error = WinInet.Set(connection, WinInet.ProxyTypeDirect | WinInet.ProxyTypeProxy, server, bypass);
            if (error != 0)
            {
                throw new SystemProxyException("apply_failed", $"setting the proxy on {Label(connection)} failed (Win32 error {error})");
            }
        }
        WinInet.NotifyAll();
    }, ct);

    public Task<bool> IsAppliedAsync(SystemProxyTarget target, CancellationToken ct) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(target);
        var lan = WinInet.Query(null);
        return lan is not null && IsOurs(lan, target.Port)
            && (lan.Flags & (WinInet.ProxyTypeAutoProxyUrl | WinInet.ProxyTypeAutoDetect)) == 0;
    }, ct);

    public Task RestoreAsync(SystemProxySnapshot snapshot, int port, CancellationToken ct) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Platform != PlatformName)
        {
            throw new SystemProxyException("restore_failed", $"a {snapshot.Platform} snapshot cannot be restored on Windows");
        }

        var saved = JsonSerializer.Deserialize<List<SavedConnection>>(snapshot.Data, Json) ?? new List<SavedConnection>();
        var failures = new List<string>();
        foreach (var s in saved)
        {
            var now = WinInet.Query(s.Connection);
            // Only a connection that still points at us is put back: anything else is a change the user made since.
            if (now is null || !IsOurs(now, port)) continue;
            var error = WinInet.Set(s.Connection, s.Flags, s.ProxyServer, s.ProxyBypass);
            if (error != 0) failures.Add($"{Label(s.Connection)} (Win32 error {error})");
        }
        WinInet.NotifyAll();
        if (failures.Count > 0)
        {
            throw new SystemProxyException("restore_failed", "the proxy settings could not be put back on " + string.Join(", ", failures));
        }
    }, ct);

    public static string ServerString(int port)
    {
        var endpoint = SystemProxyTarget.LoopbackHost + ":" + port.ToString(CultureInfo.InvariantCulture);
        return $"http={endpoint};https={endpoint}";
    }

    public static string BypassString(IEnumerable<string> extra)
        => string.Join(';', BaseBypass.Concat(extra.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim())).Distinct(StringComparer.OrdinalIgnoreCase));

    private static bool IsOurs(WinInet.Settings settings, int port)
        => (settings.Flags & WinInet.ProxyTypeProxy) != 0
           && string.Equals(settings.ProxyServer, ServerString(port), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> BuildBaseBypass()
    {
        var list = new List<string> { "<local>", "localhost", "127.*", "[::1]", "10.*", "192.168.*", "169.254.*", "*.local" };
        for (var second = 16; second <= 31; second++) list.Add($"172.{second}.*");
        return list;
    }

    private static bool PolicyForcesMachineSettings()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return key?.GetValue("ProxySettingsPerUser") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>null (the LAN settings) and the name of every dial-up/VPN connection that has settings of its own.</summary>
    private static IEnumerable<string?> ConnectionNames()
    {
        yield return null;
        string[] names;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ConnectionsKey);
            if (key is null) yield break;
            names = key.GetValueNames()
                .Where(n => n.Length > 0 && n != "DefaultConnectionSettings" && n != "SavedLegacySettings"
                            && key.GetValueKind(n) == RegistryValueKind.Binary)
                .ToArray();
        }
        catch
        {
            yield break;
        }
        foreach (var name in names) yield return name;
    }

    private static string Label(string? connection) => connection is null ? "the LAN settings" : $"\"{connection}\"";

    private sealed record SavedConnection(
        [property: JsonPropertyName("connection")] string? Connection,
        [property: JsonPropertyName("flags")] int Flags,
        [property: JsonPropertyName("proxy_server")] string? ProxyServer,
        [property: JsonPropertyName("proxy_bypass")] string? ProxyBypass);

    /// <summary>INTERNET_OPTION_PER_CONNECTION_OPTION, as measured in the S2 spike script (struct sizes 16/32 on x64).</summary>
    private static class WinInet
    {
        public const int ProxyTypeDirect = 1;
        public const int ProxyTypeProxy = 2;
        public const int ProxyTypeAutoProxyUrl = 4;
        public const int ProxyTypeAutoDetect = 8;

        private const int OptionRefresh = 37;
        private const int OptionSettingsChanged = 39;
        private const int OptionProxySettingsChanged = 95;
        private const int OptionPerConnection = 75;

        private const int PerConnFlags = 1;
        private const int PerConnProxyServer = 2;
        private const int PerConnProxyBypass = 3;
        private const int PerConnFlagsUi = 10;

        public sealed record Settings(int Flags, string? ProxyServer, string? ProxyBypass);

        [StructLayout(LayoutKind.Sequential)]
        private struct OptionList
        {
            public int dwSize;
            public IntPtr pszConnection;
            public int dwOptionCount;
            public int dwOptionError;
            public IntPtr pOptions;
        }

        // The union of INTERNET_PER_CONN_OPTION: a DWORD, a string pointer or a FILETIME (two DWORDs, so 4-byte aligned on x86).
        [StructLayout(LayoutKind.Explicit)]
        private struct OptionValue
        {
            [FieldOffset(0)] public int dwValue;
            [FieldOffset(0)] public IntPtr pszValue;
            [FieldOffset(0)] public int ftLow;
            [FieldOffset(4)] public int ftHigh;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Option
        {
            public int dwOption;
            public OptionValue Value;
        }

        [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetQueryOptionW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InternetQueryOption(IntPtr hInternet, int dwOption, ref OptionList lpBuffer, ref int lpdwBufferLength);

        [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, ref OptionList lpBuffer, int dwBufferLength);

        [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InternetSetOptionNoBuffer(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        public static Settings? Query(string? connection)
        {
            var o = Options(PerConnFlagsUi, PerConnProxyServer, PerConnProxyBypass);
            if (Call(set: false, connection, o) != 0)
            {
                // The documented fallback when FLAGS_UI is not there: ask for FLAGS.
                o = Options(PerConnFlags, PerConnProxyServer, PerConnProxyBypass);
                if (Call(set: false, connection, o) != 0) return null;
            }
            return new Settings(o[0].Value.dwValue, TakeString(o[1]), TakeString(o[2]));
        }

        /// <summary>Writes the connection type with FLAGS (Microsoft's guidance: read FLAGS_UI, write FLAGS). Returns 0 or the Win32 error.</summary>
        public static int Set(string? connection, int flags, string? proxyServer, string? proxyBypass)
        {
            var o = Options(PerConnFlags, PerConnProxyServer, PerConnProxyBypass);
            var server = proxyServer is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(proxyServer);
            var bypass = proxyBypass is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(proxyBypass);
            try
            {
                o[0].Value.dwValue = flags;
                o[1].Value.pszValue = server;
                o[2].Value.pszValue = bypass;
                return Call(set: true, connection, o);
            }
            finally
            {
                if (server != IntPtr.Zero) Marshal.FreeHGlobal(server);
                if (bypass != IntPtr.Zero) Marshal.FreeHGlobal(bypass);
            }
        }

        /// <summary>With a null handle the notification reaches every WinINet client of this user.</summary>
        public static void NotifyAll()
        {
            InternetSetOptionNoBuffer(IntPtr.Zero, OptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOptionNoBuffer(IntPtr.Zero, OptionProxySettingsChanged, IntPtr.Zero, 0);
            InternetSetOptionNoBuffer(IntPtr.Zero, OptionRefresh, IntPtr.Zero, 0);
        }

        private static Option[] Options(params int[] ids)
        {
            var options = new Option[ids.Length];
            for (var i = 0; i < ids.Length; i++) options[i].dwOption = ids[i];
            return options;
        }

        private static int Call(bool set, string? connection, Option[] options)
        {
            var size = Marshal.SizeOf<Option>();
            var array = Marshal.AllocHGlobal(size * options.Length);
            var conn = connection is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(connection);
            try
            {
                for (var i = 0; i < options.Length; i++) Marshal.StructureToPtr(options[i], array + i * size, false);
                var list = new OptionList
                {
                    dwSize = Marshal.SizeOf<OptionList>(),
                    pszConnection = conn,
                    dwOptionCount = options.Length,
                    pOptions = array,
                };
                var length = list.dwSize;
                var ok = set
                    ? InternetSetOption(IntPtr.Zero, OptionPerConnection, ref list, length)
                    : InternetQueryOption(IntPtr.Zero, OptionPerConnection, ref list, ref length);
                if (!ok) return Marshal.GetLastWin32Error();
                if (!set)
                {
                    for (var i = 0; i < options.Length; i++) options[i] = Marshal.PtrToStructure<Option>(array + i * size);
                }
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(array);
                if (conn != IntPtr.Zero) Marshal.FreeHGlobal(conn);
            }
        }

        // WinINet allocates the strings it returns; the caller frees them with GlobalFree.
        private static string? TakeString(Option option)
        {
            if (option.Value.pszValue == IntPtr.Zero) return null;
            var text = Marshal.PtrToStringUni(option.Value.pszValue);
            GlobalFree(option.Value.pszValue);
            return text;
        }
    }
}
