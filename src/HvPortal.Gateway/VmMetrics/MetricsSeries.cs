using HvPortal.Gateway.Data;

namespace HvPortal.Gateway.VmMetrics;

/// <summary>
/// Turns stored samples into the series and the summary of the resource use tab (ADR-039). Disk and network use of a
/// period is the difference between the Hyper-V totals of two consecutive samples divided by the time Hyper-V measured
/// between them; a total that goes back means the counters were reset (the period then starts from zero). Periods with
/// no sample for more than three intervals (host or gateway offline) are gaps: nothing is invented for them.
/// </summary>
public static class MetricsSeries
{
    public const int MaxPoints = 600;

    public sealed record Point(
        DateTimeOffset At,
        double? CpuAvg, double? CpuMax,
        long? MemAssigned, long? MemAssignedMax, long? MemDemand, long? MemMaximum, long? MemStartup, double? MemPressure,
        double? Iops, double? ReadMBps, double? WriteMBps, double? NetInMbps, double? NetOutMbps,
        bool GapBefore);

    public sealed record Summary(
        int Samples, DateTimeOffset? From, DateTimeOffset? To,
        double? CpuAvg, double? CpuMax, DateTimeOffset? CpuMaxAt, double? CpuP95, double CpuHoursAbove80,
        long? MemAssignedMax, long? MemDemandMax, DateTimeOffset? MemDemandMaxAt, long? MemDemandAvg, double? MemPressureMax, long? MemMaximum,
        bool? DynamicMemory, int MemStatusWarnings,
        double? IopsAvg, double? IopsMax, double? ReadMb, double? WriteMb, double? ReadMaxMBps, double? WriteMaxMBps,
        double? NetInMb, double? NetOutMb, double? NetInMaxMbps, double? NetOutMaxMbps,
        long? LatencyAvgMs, long? TotalDiskMb, long? MaximumMemorySinceResetMb);

    /// <param name="samples">Ascending by time; may start with one sample before <paramref name="from"/> (used only for differences).</param>
    public static (IReadOnlyList<Point> Points, Summary Summary) Build(IReadOnlyList<VmMetricSample> samples, DateTimeOffset from, int intervalSeconds)
    {
        var points = new List<Point>();
        var deltas = new List<(double? Iops, double? Read, double? Write, double? In, double? Out, double ReadMb, double WriteMb, double InMb, double OutMb)>();
        double readMb = 0, writeMb = 0, inMb = 0, outMb = 0;
        bool anyDisk = false, anyNet = false;
        VmMetricSample? prev = null;
        foreach (var c in samples)
        {
            var gap = prev is not null && (c.At - prev.At).TotalSeconds > intervalSeconds * 3;
            double? iops = null, read = null, write = null, netIn = null, netOut = null;
            if (prev is not null && !gap && c.At >= from)
            {
                var reset = c.MeteringSeconds is { } cm && prev.MeteringSeconds is { } pm && cm < pm
                            || Less(c.NormalizedIoCount, prev.NormalizedIoCount) || Less(c.DiskReadMb, prev.DiskReadMb)
                            || Less(c.NetworkInboundMb, prev.NetworkInboundMb);
                var seconds = reset
                    ? c.MeteringSeconds ?? (c.At - prev.At).TotalSeconds
                    : c.MeteringSeconds is { } a && prev.MeteringSeconds is { } b && a - b > 0 ? a - b : (c.At - prev.At).TotalSeconds;
                if (seconds > 0)
                {
                    iops = Rate(c.NormalizedIoCount, reset ? 0 : prev.NormalizedIoCount, seconds, 1);
                    var dRead = Diff(c.DiskReadMb, reset ? 0 : prev.DiskReadMb);
                    var dWrite = Diff(c.DiskWrittenMb, reset ? 0 : prev.DiskWrittenMb);
                    var dIn = Diff(c.NetworkInboundMb, reset ? 0 : prev.NetworkInboundMb);
                    var dOut = Diff(c.NetworkOutboundMb, reset ? 0 : prev.NetworkOutboundMb);
                    read = dRead / seconds;
                    write = dWrite / seconds;
                    // MB -> megabits per second.
                    netIn = dIn * 8 / seconds;
                    netOut = dOut * 8 / seconds;
                    if (dRead is not null || dWrite is not null) { anyDisk = true; readMb += dRead ?? 0; writeMb += dWrite ?? 0; }
                    if (dIn is not null || dOut is not null) { anyNet = true; inMb += dIn ?? 0; outMb += dOut ?? 0; }
                }
            }
            if (c.At >= from)
            {
                double? pressure = c.MemoryDemandMb is { } d && c.MemoryAssignedAvgMb is { } asg and > 0 ? Math.Round(d * 100.0 / asg, 1) : null;
                points.Add(new Point(c.At, c.CpuAvgPercent, c.CpuMaxPercent, c.MemoryAssignedAvgMb, c.MemoryAssignedMaxMb, c.MemoryDemandMb,
                    c.DynamicMemory == true ? c.MemoryMaximumMb : null, c.MemoryStartupMb, pressure,
                    Round(iops), Round(read, 2), Round(write, 2), Round(netIn, 2), Round(netOut, 2), gap));
            }
            prev = c;
        }

        var inRange = samples.Where(s => s.At >= from).ToList();
        var last = inRange.LastOrDefault();
        var cpuAvgs = points.Where(p => p.CpuAvg is not null).Select(p => p.CpuAvg!.Value).ToList();
        var cpuMaxPoint = points.Where(p => p.CpuMax is not null).MaxBy(p => p.CpuMax);
        var demandMax = points.Where(p => p.MemDemand is not null).MaxBy(p => p.MemDemand);
        var demands = points.Where(p => p.MemDemand is not null).Select(p => p.MemDemand!.Value).ToList();
        var iopsList = points.Where(p => p.Iops is not null).Select(p => p.Iops!.Value).ToList();
        var summary = new Summary(
            inRange.Count, inRange.FirstOrDefault()?.At, last?.At,
            cpuAvgs.Count > 0 ? Math.Round(cpuAvgs.Average(), 1) : null,
            cpuMaxPoint?.CpuMax, cpuMaxPoint?.At,
            cpuAvgs.Count > 0 ? Percentile(cpuAvgs, 0.95) : null,
            Math.Round(cpuAvgs.Count(x => x >= 80) * intervalSeconds / 3600.0, 1),
            points.Max(p => p.MemAssignedMax), demandMax?.MemDemand, demandMax?.At,
            demands.Count > 0 ? (long)demands.Average() : null,
            points.Max(p => p.MemPressure), last?.DynamicMemory == true ? last.MemoryMaximumMb : null,
            last?.DynamicMemory, inRange.Count(s => s.MemoryStatus is { Length: > 0 } st && !string.Equals(st, "OK", StringComparison.OrdinalIgnoreCase)),
            iopsList.Count > 0 ? Math.Round(iopsList.Average(), 1) : null, iopsList.Count > 0 ? iopsList.Max() : null,
            anyDisk ? Math.Round(readMb, 0) : null, anyDisk ? Math.Round(writeMb, 0) : null,
            points.Max(p => p.ReadMBps), points.Max(p => p.WriteMBps),
            anyNet ? Math.Round(inMb, 0) : null, anyNet ? Math.Round(outMb, 0) : null,
            points.Max(p => p.NetInMbps), points.Max(p => p.NetOutMbps),
            last?.AverageLatencyMs, last?.TotalDiskMb, last?.MaximumMemoryMb);
        return (Downsample(points), summary);
    }

    /// <summary>At most MaxPoints: averages per bucket, peaks kept as peaks; a bucket with a gap keeps the gap mark.</summary>
    internal static IReadOnlyList<Point> Downsample(List<Point> points)
    {
        if (points.Count <= MaxPoints)
        {
            return points;
        }
        var size = (int)Math.Ceiling(points.Count / (double)MaxPoints);
        var result = new List<Point>(MaxPoints);
        for (var i = 0; i < points.Count; i += size)
        {
            var b = points.GetRange(i, Math.Min(size, points.Count - i));
            result.Add(new Point(b[^1].At,
                Avg(b.Select(p => p.CpuAvg)), b.Max(p => p.CpuMax),
                (long?)Avg(b.Select(p => (double?)p.MemAssigned)), b.Max(p => p.MemAssignedMax), (long?)Avg(b.Select(p => (double?)p.MemDemand)),
                b[^1].MemMaximum, b[^1].MemStartup, b.Max(p => p.MemPressure),
                Avg(b.Select(p => p.Iops)), Avg(b.Select(p => p.ReadMBps)), Avg(b.Select(p => p.WriteMBps)),
                Avg(b.Select(p => p.NetInMbps)), Avg(b.Select(p => p.NetOutMbps)), b.Any(p => p.GapBefore)));
        }
        return result;
    }

    private static bool Less(long? current, long? previous) => current is { } c && previous is { } p && c < p;

    private static double? Diff(long? current, long? previous) => current is { } c && previous is { } p && c >= p ? c - p : null;

    private static double? Rate(long? current, long? previous, double seconds, int factor) =>
        Diff(current, previous) is { } d ? d * factor / seconds : null;

    private static double? Round(double? v, int digits = 1) => v is { } x ? Math.Round(x, digits) : null;

    private static double? Avg(IEnumerable<double?> values)
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return list.Count > 0 ? Math.Round(list.Average(), 2) : null;
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
