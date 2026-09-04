using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RouteBridge.Browser.Native;

/// <summary>WINDOWS-ONLY: WM_CLOSE إلى كل نافذة علوية مرئية تملكها عمليات معيّنة (الإغلاق المهذب قبل قتل الـ Job).</summary>
[SupportedOSPlatform("windows")]
public static class WindowCloser
{
    private const uint WmClose = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>يعيد عدد النوافذ التي أُرسل إليها WM_CLOSE.</summary>
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
                if (GetWindow(hWnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true; // نوافذ مملوكة (قوائم/أدوات) تُغلق مع مالكها
                var threadId = GetWindowThreadProcessId(hWnd, out var pid);
                if (threadId == 0) return true;
                if (pids.Contains(unchecked((int)pid)) && PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero)) posted++;
            }
            catch { /* نافذة واحدة */ }
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
