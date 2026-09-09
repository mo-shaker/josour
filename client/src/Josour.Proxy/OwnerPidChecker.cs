using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Josour.Proxy;

/// <summary>يعيد PID العملية المالكة لطرف الاتصال الوارد (المنفذ المحلي للعميل = المنفذ البعيد الذي نراه). null = غير معروف.</summary>
public interface IOwnerPidChecker
{
    /// <param name="remoteEndPoint">طرف العميل كما نراه (منفذه المحلي).</param>
    /// <param name="localEndPoint">طرف الـ Proxy (منفذ الاستماع).</param>
    int? GetOwnerPid(IPEndPoint remoteEndPoint, IPEndPoint localEndPoint);
}

/// <summary>لا يعرف شيئًا (غير Windows والاختبارات). قبول الاتصال يعتمد حينها على RejectUnknownOwner.</summary>
public sealed class PermissiveOwnerPidChecker : IOwnerPidChecker
{
    public static readonly PermissiveOwnerPidChecker Instance = new();
    public int? GetOwnerPid(IPEndPoint remoteEndPoint, IPEndPoint localEndPoint) => null;
}

/// <summary>
/// WINDOWS-ONLY: iphlpapi!GetExtendedTcpTable(TCP_TABLE_OWNER_PID_ALL) لجدولي IPv4 وIPv6؛ يبحث عن الصف الذي منفذه المحلي =
/// المنفذ البعيد للاتصال الوارد ومنفذه البعيد = منفذ الـ Proxy. لم يُشغَّل على Windows بعد (مراجعة كود فقط).
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

    /// <summary>الجدول يُحجز بـ AllocHGlobal؛ المستدعي يحرره. IntPtr.Zero عند الفشل.</summary>
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
/// تحويلات بايتات جداول TCP. منطق خالص بلا استدعاء نظام، فهو خارج الصنف الخاص بـ Windows
/// ليبقى قابلًا للاختبار على أي منصة.
/// </summary>
public static class TcpTableFormat
{
    /// <summary>المنفذ في الـ DWORD بترتيب الشبكة في أدنى 16 بت.</summary>
    public static int PortFromDword(uint dword) => (int)(((dword & 0xFF) << 8) | ((dword >> 8) & 0xFF));
}
