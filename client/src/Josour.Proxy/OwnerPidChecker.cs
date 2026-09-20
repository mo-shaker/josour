using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Josour.Proxy;

/// <summary>Returns the PID of the process that owns the inbound connection's end (the client's local port = the remote port we see). null = unknown.</summary>
public interface IOwnerPidChecker
{
    /// <param name="remoteEndPoint">The client's end as we see it (its local port).</param>
    /// <param name="localEndPoint">The proxy's end (the listening port).</param>
    int? GetOwnerPid(IPEndPoint remoteEndPoint, IPEndPoint localEndPoint);
}

/// <summary>
/// Which platforms can answer "which process opened this connection", and the checker for the running one.
/// <para>
/// This is the control that keeps the product from being a device-wide proxy: the local CONNECT proxy listens on
/// loopback, so any program on the machine can reach it, and only the owner check tells the work browser apart from
/// everything else. Acceptance criterion 10 — Teams and Outlook and the normal browser stay on the user's own
/// connection — rests entirely on it.
/// </para>
/// <para>
/// So a platform without an implementation has no guest role, and says so. It must never quietly become a platform
/// where the check passes for everybody.
/// </para>
/// </summary>
public static class OwnerPidCheckers
{
    /// <summary>True where a connection's owning process can actually be identified.</summary>
    public static bool SupportedOnThisPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>
    /// The checker this machine has. Off Windows it is <see cref="PermissiveOwnerPidChecker"/>, which identifies
    /// nobody — which is why <see cref="ConnectProxyOptions.RejectUnknownOwner"/> defaults to refusing, and why a
    /// proxy wired for owner checks on such a platform refuses to start rather than admitting everything.
    /// </summary>
    public static IOwnerPidChecker ForCurrentPlatform() => true switch
    {
        _ when OperatingSystem.IsWindows() => WindowsOwnerPidChecker.Instance,
        _ when OperatingSystem.IsMacOS() => MacOwnerPidChecker.Instance,
        _ => PermissiveOwnerPidChecker.Instance,
    };
}

/// <summary>It knows nothing (off Windows, and the tests). Accepting the connection then rests on RejectUnknownOwner.</summary>
public sealed class PermissiveOwnerPidChecker : IOwnerPidChecker
{
    public static readonly PermissiveOwnerPidChecker Instance = new();
    public int? GetOwnerPid(IPEndPoint remoteEndPoint, IPEndPoint localEndPoint) => null;
}

/// <summary>
/// WINDOWS-ONLY: iphlpapi!GetExtendedTcpTable(TCP_TABLE_OWNER_PID_ALL) for both the IPv4 and IPv6 tables; it looks for the row whose local port =
/// the inbound connection's remote port and whose remote port = the proxy's port. It has not been run on Windows yet (a code review only).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOwnerPidChecker : IOwnerPidChecker
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;

    public static readonly WindowsOwnerPidChecker Instance = new();

    public int? GetOwnerPid(IPEndPoint remoteEndPoint, IPEndPoint localEndPoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(localEndPoint);
        if (!OperatingSystem.IsWindows()) return null;
        var clientPort = remoteEndPoint.Port;
        var proxyPort = localEndPoint.Port;
        var v6 = remoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6 && !remoteEndPoint.Address.IsIPv4MappedToIPv6;
        try
        {
            return v6 ? FindV6(clientPort, proxyPort) ?? FindV4(clientPort, proxyPort) : FindV4(clientPort, proxyPort) ?? FindV6(clientPort, proxyPort);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    private static int? FindV4(int clientPort, int proxyPort)
    {
        var table = ReadTable(AfInet);
        if (table == IntPtr.Zero) return null;
        try
        {
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var p = table + 4;
            for (var i = 0; i < count; i++, p += rowSize)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(p);
                if (TcpTableFormat.PortFromDword(row.LocalPort) == clientPort && TcpTableFormat.PortFromDword(row.RemotePort) == proxyPort) return (int)row.OwningPid;
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private static int? FindV6(int clientPort, int proxyPort)
    {
        var table = ReadTable(AfInet6);
        if (table == IntPtr.Zero) return null;
        try
        {
            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
            var p = table + 4;
            for (var i = 0; i < count; i++, p += rowSize)
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(p);
                if (TcpTableFormat.PortFromDword(row.LocalPort) == clientPort && TcpTableFormat.PortFromDword(row.RemotePort) == proxyPort) return (int)row.OwningPid;
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    /// <summary>The table is allocated with AllocHGlobal; the caller frees it. IntPtr.Zero on failure.</summary>
    private static IntPtr ReadTable(int family)
    {
        var size = 0;
        var rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
        if (rc != ErrorInsufficientBuffer && rc != NoError) return IntPtr.Zero;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            rc = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0);
            if (rc == NoError) return buffer;
            Marshal.FreeHGlobal(buffer);
            if (rc != ErrorInsufficientBuffer) return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

}

/// <summary>
/// Byte conversions for the TCP tables. Pure logic with no system call, so it lives outside the Windows-specific class
/// to stay testable on any platform.
/// </summary>
public static class TcpTableFormat
{
    /// <summary>The port sits in the DWORD in network order in the lowest 16 bits.</summary>
    public static int PortFromDword(uint dword) => (int)(((dword & 0xFF) << 8) | ((dword >> 8) & 0xFF));
}
