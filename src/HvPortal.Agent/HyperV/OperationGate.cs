using System.Collections.Concurrent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// How many PowerShell processes the agent runs at once for one host (0.9.4). Every typed operation that goes through
/// the operations script is a separate powershell.exe: without a limit, a burst of requests (many VMs changed at once,
/// a dashboard with many readers) starts as many processes as requests, each with its own copy of the Hyper-V module.
/// Agent:Operations:MaxConcurrent (default 8) and Agent:Operations:ReservedForShort (default 2) in the agent settings.
/// </summary>
public static class OperationLimits
{
    public const int DefaultMaxConcurrent = 8;
    public const int DefaultReservedForShort = 2;

    /// <summary>PowerShell processes running at once for one host (1-64).</summary>
    public static int MaxConcurrent { get; private set; } = DefaultMaxConcurrent;

    /// <summary>
    /// Slots that operations copying whole disks (backups, restores, templates, moves, node drain: hours) never take, so
    /// that reads and short changes are not stuck behind them. 0 = no reservation.
    /// </summary>
    public static int ReservedForShort { get; private set; } = DefaultReservedForShort;

    public static void Configure(int? maxConcurrent, int? reservedForShort)
    {
        MaxConcurrent = Math.Clamp(maxConcurrent ?? DefaultMaxConcurrent, 1, 64);
        ReservedForShort = Math.Clamp(reservedForShort ?? DefaultReservedForShort, 0, MaxConcurrent - 1);
    }
}

/// <summary>
/// Queue in front of the operations script of one host: at most <c>maxConcurrent</c> scripts run, the others wait their
/// turn. Long operations use at most <c>maxConcurrent - reservedForShort</c> of the slots. Waiting counts against the
/// time the operation was given: an operation that could not start in time is refused as "host_busy" (it did nothing),
/// never left pending. Cancellation (agent stopping) ends the wait.
/// </summary>
public sealed class OperationGate : IDisposable
{
    private readonly SemaphoreSlim _all;
    private readonly SemaphoreSlim? _long;
    private int _running;
    private int _waiting;
    private int _peakRunning;

    public OperationGate(int maxConcurrent, int reservedForShort)
    {
        MaxConcurrent = Math.Max(1, maxConcurrent);
        var longSlots = Math.Max(1, MaxConcurrent - Math.Max(0, reservedForShort));
        _all = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
        _long = longSlots < MaxConcurrent ? new SemaphoreSlim(longSlots, longSlots) : null;
        LongSlots = longSlots;
    }

    public int MaxConcurrent { get; }
    public int LongSlots { get; }
    public int Running => Volatile.Read(ref _running);
    public int Waiting => Volatile.Read(ref _waiting);
    /// <summary>Highest number of scripts that ran at once since the agent started.</summary>
    public int PeakRunning => Volatile.Read(ref _peakRunning);

    public sealed class Lease(OperationGate gate, bool longRunning) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release(longRunning);
            }
        }
    }

    /// <summary>A slot, or null when none became free within <paramref name="patience"/>.</summary>
    public async Task<Lease?> EnterAsync(bool longRunning, TimeSpan patience, CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        var holdsLong = false;
        try
        {
            var started = Environment.TickCount64;
            if (longRunning && _long is not null)
            {
                if (!await _long.WaitAsync(patience, ct))
                {
                    return null;
                }
                holdsLong = true;
            }
            var left = patience - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (!await _all.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, ct))
            {
                if (holdsLong)
                {
                    _long!.Release();
                }
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            if (holdsLong)
            {
                _long!.Release();
            }
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
        var now = Interlocked.Increment(ref _running);
        int peak;
        while (now > (peak = Volatile.Read(ref _peakRunning)) && Interlocked.CompareExchange(ref _peakRunning, now, peak) != peak)
        {
        }
        return new Lease(this, longRunning && _long is not null);
    }

    private void Release(bool longRunning)
    {
        Interlocked.Decrement(ref _running);
        _all.Release();
        if (longRunning)
        {
            _long!.Release();
        }
    }

    public void Dispose()
    {
        _all.Dispose();
        _long?.Dispose();
    }
}

/// <summary>What one run of the operations script cost.</summary>
public sealed record OperationRun(HvOperation Operation, bool Ok, bool TimedOut, long QueueMs, long RunMs, long? StartupMs, long? ScriptMs, long? PeakMemoryBytes);

/// <summary>
/// Running totals of the operations script per operation (0.9.4), kept in memory and written to the agent log: how often,
/// how long, how long in the queue, how long powershell.exe took to start, how much memory. For deciding with numbers
/// which operations are worth moving from PowerShell to in-process code (docs/performance-powershell.md).
/// </summary>
public sealed class OperationMetrics
{
    public sealed record Totals(string Operation, int Count, int Failed, int TimedOut, int Busy, long AverageMs, long MaxMs, long AverageQueueMs, long MaxQueueMs,
        long? AverageStartupMs, long? MaxPeakMemoryBytes);

    private sealed class Counter
    {
        public int Count, Failed, TimedOut, Busy, StartupCount;
        public long TotalMs, MaxMs, TotalQueueMs, MaxQueueMs, TotalStartupMs, MaxPeakBytes;
    }

    private readonly ConcurrentDictionary<HvOperation, Counter> _counters = new();
    private int _runs;

    /// <summary>Records a run; true every <paramref name="summaryEvery"/> runs (time to log the totals).</summary>
    public bool Record(OperationRun run, int summaryEvery = 100)
    {
        var c = _counters.GetOrAdd(run.Operation, _ => new Counter());
        lock (c)
        {
            c.Count++;
            if (!run.Ok)
            {
                c.Failed++;
            }
            if (run.TimedOut)
            {
                c.TimedOut++;
            }
            c.TotalMs += run.RunMs;
            c.MaxMs = Math.Max(c.MaxMs, run.RunMs);
            c.TotalQueueMs += run.QueueMs;
            c.MaxQueueMs = Math.Max(c.MaxQueueMs, run.QueueMs);
            if (run.StartupMs is { } startup)
            {
                c.StartupCount++;
                c.TotalStartupMs += startup;
            }
            if (run.PeakMemoryBytes is { } peak)
            {
                c.MaxPeakBytes = Math.Max(c.MaxPeakBytes, peak);
            }
        }
        return Interlocked.Increment(ref _runs) % Math.Max(1, summaryEvery) == 0;
    }

    /// <summary>An operation that never started because the host was busy for all the time it was given.</summary>
    public void RecordBusy(HvOperation operation, long queueMs)
    {
        var c = _counters.GetOrAdd(operation, _ => new Counter());
        lock (c)
        {
            c.Busy++;
            c.MaxQueueMs = Math.Max(c.MaxQueueMs, queueMs);
        }
    }

    /// <summary>Totals per operation, the most expensive (total time) first.</summary>
    public IReadOnlyList<Totals> Snapshot()
    {
        var list = new List<(long Total, Totals Row)>();
        foreach (var (op, c) in _counters)
        {
            lock (c)
            {
                list.Add((c.TotalMs, new Totals(op.ToString(), c.Count, c.Failed, c.TimedOut, c.Busy, c.Count > 0 ? c.TotalMs / c.Count : 0, c.MaxMs,
                    c.Count > 0 ? c.TotalQueueMs / c.Count : 0, c.MaxQueueMs, c.StartupCount > 0 ? c.TotalStartupMs / c.StartupCount : null,
                    c.MaxPeakBytes > 0 ? c.MaxPeakBytes : null)));
            }
        }
        return list.OrderByDescending(x => x.Total).Select(x => x.Row).ToList();
    }

    public static string Format(IReadOnlyList<Totals> totals, int top = 12) =>
        string.Join("; ", totals.Take(top).Select(t =>
            $"{t.Operation} x{t.Count} avg {t.AverageMs} ms max {t.MaxMs} ms"
            + (t.AverageStartupMs is { } s ? $" start {s} ms" : "")
            + (t.MaxQueueMs > 0 ? $" queue max {t.MaxQueueMs} ms" : "")
            + (t.MaxPeakMemoryBytes is { } m ? $" mem {m / (1024 * 1024)} MB" : "")
            + (t.Failed > 0 ? $" failed {t.Failed}" : "") + (t.TimedOut > 0 ? $" timeout {t.TimedOut}" : "") + (t.Busy > 0 ? $" busy {t.Busy}" : "")));
}
