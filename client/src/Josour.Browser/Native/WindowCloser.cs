using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Josour.Browser.Native;

/// <summary>WINDOWS-ONLY: WM_CLOSE to every visible top-level window owned by the given processes (the polite close before killing the job).</summary>
[SupportedOSPlatform("windows")]
public static class WindowCloser
{
    private const uint WmClose = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Returns the number of windows WM_CLOSE was posted to.</summary>
    public static int PostCloseToWindowsOf(IReadOnlySet<int> pids)
    {
        ArgumentNullException.ThrowIfNull(pids);
        if (pids.Count == 0) return 0;
        var posted = 0;
        EnumWindowsProc callback = (hWnd, lParam) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindow(hWnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true; // owned windows (menus/tools) close with their owner
                var threadId = GetWindowThreadProcessId(hWnd, out var pid);
                if (threadId == 0) return true;
                if (pids.Contains(unchecked((int)pid)) && PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero)) posted++;
            }
            catch { /* one window */ }
            return true;
        };
        _ = EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return posted;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
