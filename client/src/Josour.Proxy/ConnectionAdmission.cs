using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Josour.Proxy;

/// <summary>
/// Who may use the local proxy (ADR-0015 point 4). The listener is on loopback only, so the question is never "which
/// machine" but "which local process": once the system proxy points at us, every application of the signed-in user
/// is meant to come through, and nobody else's.
/// </summary>
public interface IConnectionAdmission
{
    /// <param name="remoteEndPoint">The client's end as we see it (its local port).</param>
    /// <param name="localEndPoint">The proxy's end (the listening port).</param>
    bool Admit(IPEndPoint remoteEndPoint, IPEndPoint localEndPoint);
}

/// <summary>
/// WINDOWS: a connection is refused when its process belongs to another Windows account (a second user signed in on
/// the same machine, a service). The owning process comes from the TCP table (<see cref="WindowsOwnerPidChecker"/>),
/// its account from its token.
/// <para>
/// A connection whose owner cannot be identified is admitted: the boundary that matters is the loopback listener, and
/// this check only narrows it. Refusing the unknown is what once made the proxy refuse every connection the browser
/// made with no visible cause, which a check that only narrows must never do.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SameUserAdmission : IConnectionAdmission
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;

    private readonly IOwnerPidChecker _owners;
    private readonly SecurityIdentifier? _self;

    public SameUserAdmission(IOwnerPidChecker? owners = null)
    {
        _owners = owners ?? WindowsOwnerPidChecker.Instance;
        try { _self = WindowsIdentity.GetCurrent().User; }
        catch { _self = null; }
    }

    public bool Admit(IPEndPoint remoteEndPoint, IPEndPoint localEndPoint)
    {
        if (_self is null) return true;
        int? pid;
        try { pid = _owners.GetOwnerPid(remoteEndPoint, localEndPoint); }
        catch { pid = null; }
        if (pid is null) return true;
        if (pid.Value == Environment.ProcessId) return true;
        var owner = ProcessUser(pid.Value);
        // A process we may not even query (another account's, or a protected one) is not ours.
        return owner is not null && owner.Equals(_self);
    }

    private static SecurityIdentifier? ProcessUser(int pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process.IsInvalid) return null;
        if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
        using (token)
        {
            GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out var size);
            if (size <= 0) return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenUserClass, buffer, size, out _)) return null;
                // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the PSID.
                var sid = Marshal.ReadIntPtr(buffer);
                return new SecurityIdentifier(sid);
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, int length, out int returnLength);
}
