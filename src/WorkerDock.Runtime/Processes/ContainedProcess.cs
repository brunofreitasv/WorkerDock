using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace WorkerDock.Runtime.Processes;

/// <summary>Owns an unnamed, noninheritable Windows Job Object for exactly one attempt tree.</summary>
internal sealed class ContainedProcess : IAsyncDisposable
{
    private readonly SafeFileHandle job;
    private readonly Process process;
    private readonly object lifecycle = new();
    private Task? termination;
    public ContainedProcess(Process process)
    {
        this.process = process;
        job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new ExecutionProcessException("ContainmentUnavailable");
        try
        {
            // No breakaway flags. Do not share or inherit this handle into the child.
            var limits = new ExtendedLimits { BasicLimitInformation = new BasicLimits { LimitFlags = 0x2000 } };
            var size = Marshal.SizeOf<ExtendedLimits>(); var memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, memory, false);
                if (!SetInformationJobObject(job, 9, memory, (uint)size) || !AssignProcessToJobObject(job, process.SafeHandle))
                    throw new ExecutionProcessException("ContainmentUnavailable");
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        catch { job.Dispose(); throw; }
    }
    public ValueTask TerminateAsync(CancellationToken ct)
    {
        Task operation;
        lock (lifecycle) operation = termination ??= TerminateAndCloseAsync();
        // A caller may stop waiting, but it cannot cancel the owner's bounded cleanup.
        return new ValueTask(operation.WaitAsync(ct));
    }
    private async Task TerminateAndCloseAsync()
    {
        try
        {
            if (!TerminateJobObject(job, 1)) throw new ExecutionProcessException("LocalTerminationUnconfirmed");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                if (!QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero))
                    throw new ExecutionProcessException("LocalTerminationUnconfirmed");
                if (accounting.ActiveProcesses == 0) break;
                await Task.Delay(10, timeout.Token);
            }
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) { throw new ExecutionProcessException("LocalTerminationUnconfirmed"); }
        finally { job.Dispose(); }
    }
    public ValueTask DisposeAsync() => TerminateAsync(default);
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out BasicAccounting information, uint length, IntPtr returnLength);
}
