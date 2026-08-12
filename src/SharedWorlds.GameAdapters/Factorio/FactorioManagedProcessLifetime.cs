using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharedWorlds.GameAdapters.Factorio;

/// <summary>
/// Binds every Factorio process that Steward accepts as managed to one Windows Job Object configured
/// with KILL_ON_JOB_CLOSE. Windows closes the job handle when Steward exits even after an ungraceful
/// process termination, so an authoritative managed game/server cannot continue running without the
/// Steward process that owns its save/capture responsibility.
/// </summary>
internal static class FactorioManagedProcessLifetime
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private static readonly Lazy<IntPtr> StewardJob = new(CreateStewardJob);

    public static void RequireAttached(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            // Steward's current managed desktop distribution is Windows. Keep the adapter assembly
            // buildable on other platforms without pretending this Windows lifetime guarantee exists.
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(StewardJob.Value, process.Handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Windows refused to bind managed Factorio process {process.Id} to Steward's lifetime job.");
            }
        }
        catch
        {
            // A process that could escape Steward's lifetime is not a valid managed session. Stop it
            // immediately rather than continuing with weaker authority/recovery guarantees.
            TryTerminate(process);
            throw;
        }
    }

    public static bool IsAttached(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (!IsProcessInJob(process.Handle, StewardJob.Value, out var isInJob))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Windows could not verify Steward lifetime ownership for Factorio process {process.Id}.");
        }

        return isInJob;
    }

    private static IntPtr CreateStewardJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not create Steward's managed-game lifetime job.");
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            _ = CloseHandle(job);
            throw new Win32Exception(
                error,
                "Windows could not configure Steward's managed-game lifetime job.");
        }

        // Intentionally retained for the full Steward process lifetime. The kernel closes the handle
        // on process exit; JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE then terminates any attached processes.
        return job;
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited while containment was being established.
        }
        catch (SystemException)
        {
            // Best-effort emergency termination. The original containment failure remains primary.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        IntPtr processHandle,
        IntPtr jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

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
