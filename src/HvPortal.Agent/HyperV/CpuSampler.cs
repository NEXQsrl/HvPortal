using System.Management;
using System.Runtime.InteropServices;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// CPU utilization of a Hyper-V host.
///  - Preferred: hypervisor logical processor run time (Win32_PerfRawData_HvStats_HyperVHypervisorLogicalProcessor,
///    "% Total Run Time"): includes the root partition AND all guests, i.e. the real load of the physical CPUs.
///    Task Manager / Win32_Processor on a Hyper-V host only show the root partition.
///  - Fallback: GetSystemTimes (kernel32) — root partition only; reported with source "root-partition". Local host only:
///    for a remote target (management agent) the local machine's CPU would be reported, so there is no such fallback.
/// The value is the average since the previous sample (the inventory interval); the first call samples twice, 1 s apart.
/// </summary>
public sealed class CpuSampler
{
    private readonly object _gate = new();
    private (ulong Run, ulong Time, int Count)? _lastHv;
    private (ulong Idle, ulong Total)? _lastSys;
    private readonly HostTarget _target;

    public CpuSampler() : this(HostTarget.Local)
    {
    }

    public CpuSampler(HostTarget target) => _target = target;

    public (int? Percent, string? Source) Sample()
    {
        lock (_gate)
        {
            if (TrySampleHypervisor() is { } hv)
            {
                return (hv, "hypervisor");
            }
            if (_target.IsLocal && TrySampleSystem() is { } sys)
            {
                return (sys, "root-partition");
            }
            return (null, null);
        }
    }

    private int? TrySampleHypervisor()
    {
        try
        {
            var first = ReadHypervisor();
            if (first is null)
            {
                return null;
            }
            if (_lastHv is null || _lastHv.Value.Count != first.Value.Count)
            {
                _lastHv = first;
                Thread.Sleep(1000);
                first = ReadHypervisor();
                if (first is null)
                {
                    return null;
                }
            }
            var prev = _lastHv!.Value;
            _lastHv = first;
            var dRun = first.Value.Run - prev.Run;
            var dTime = first.Value.Time - prev.Time;
            if (dTime == 0 || first.Value.Run < prev.Run || first.Value.Time < prev.Time)
            {
                return null;
            }
            return Clamp(dRun * 100.0 / (dTime * (double)first.Value.Count));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private (ulong Run, ulong Time, int Count)? ReadHypervisor()
    {
        var scope = _target.ConnectWmi(@"root\cimv2");
        using var s = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT Name, PercentTotalRunTime, Timestamp_Sys100NS FROM Win32_PerfRawData_HvStats_HyperVHypervisorLogicalProcessor"));
        using var r = s.Get();
        ulong run = 0, time = 0;
        var count = 0;
        foreach (ManagementBaseObject o in r)
        {
            using (o)
            {
                if (o["Name"] is "_Total" || o["PercentTotalRunTime"] is not ulong pr || o["Timestamp_Sys100NS"] is not ulong ts)
                {
                    continue;
                }
                run += pr;
                time = ts;
                count++;
            }
        }
        return count == 0 ? null : (run, time, count);
    }

    private int? TrySampleSystem()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }
        var now = (Idle: ToUlong(idle), Total: ToUlong(kernel) + ToUlong(user)); // kernel time includes idle time
        if (_lastSys is null)
        {
            _lastSys = now;
            Thread.Sleep(1000);
            if (!GetSystemTimes(out idle, out kernel, out user))
            {
                return null;
            }
            now = (ToUlong(idle), ToUlong(kernel) + ToUlong(user));
        }
        var prev = _lastSys.Value;
        _lastSys = now;
        var dTotal = now.Total - prev.Total;
        return dTotal == 0 ? null : Clamp((1.0 - (now.Idle - prev.Idle) / (double)dTotal) * 100.0);
    }

    private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, 0, 100));

    private static ulong ToUlong(FILETIME f) => ((ulong)(uint)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);
}
