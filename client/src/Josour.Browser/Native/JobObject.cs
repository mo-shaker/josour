using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Josour.Browser.Native;

/// <summary>
/// WINDOWS-ONLY: Job Object بـ JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: إغلاق المقبض (أو انهيار تطبيقنا) يقتل المتصفح وكل عملياته الفرعية
/// (Fail-closed، خطة 8.5). لم يُشغَّل على Windows بعد (مراجعة كود فقط).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JobObject : IDisposable
{
    private const int JobObjectBasicProcessIdListClass = 3;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private IntPtr _handle;

    private JobObject(IntPtr handle) => _handle = handle;

    public bool IsOpen => _handle != IntPtr.Zero;

    public static JobObject Create()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        try
        {
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return new JobObject(handle);
        }
        catch
        {
            CloseHandle(handle);
            throw;
        }
    }

    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
    }

    /// <summary>معرّفات العمليات الحية داخل الـ Job (QueryInformationJobObject / JobObjectBasicProcessIdList).</summary>
    public IReadOnlyList<int> ProcessIds()
    {
        if (_handle == IntPtr.Zero) return Array.Empty<int>();
        var capacity = 256;
        for (var attempt = 0; attempt < 4; attempt++, capacity *= 4)
        {
            var size = 8 + capacity * IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!QueryInformationJobObject(_handle, JobObjectBasicProcessIdListClass, buffer, (uint)size, out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 234 /* ERROR_MORE_DATA */) continue;
                    return Array.Empty<int>();
                }
                var assigned = Marshal.ReadInt32(buffer, 0);
                var inList = Marshal.ReadInt32(buffer, 4);
                if (inList < assigned && attempt < 3) continue;
                var ids = new List<int>(inList);
                for (var i = 0; i < inList; i++)
                {
                    var value = Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
                    ids.Add(unchecked((int)value.ToInt64()));
                }
                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return Array.Empty<int>();
    }

    /// <summary>إغلاق المقبض = قتل كل ما بقي في الـ Job.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength, out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
