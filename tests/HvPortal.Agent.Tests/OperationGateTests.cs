using HvPortal.Agent.HyperV;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.Tests;

/// <summary>
/// 0.9.4: the queue in front of the operations script (at most N PowerShell processes per host, slots kept for short
/// operations, the wait counted against the operation's time) and the totals written to the agent log.
/// </summary>
public sealed class OperationGateTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task At_most_the_configured_number_of_operations_run_and_the_others_wait_their_turn()
    {
        using var gate = new OperationGate(maxConcurrent: 2, reservedForShort: 0);
        var a = await gate.EnterAsync(false, Long, CancellationToken.None);
        var b = await gate.EnterAsync(false, Long, CancellationToken.None);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(2, gate.Running);

        // The third waits; it starts as soon as one ends.
        var third = gate.EnterAsync(false, Long, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(third.IsCompleted);
        Assert.Equal(1, gate.Waiting);
        a!.Dispose();
        a.Dispose(); // releasing twice frees one slot, not two
        var c = await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(c);
        Assert.Equal(2, gate.Running);
        Assert.Equal(0, gate.Waiting);
        Assert.Null(await gate.EnterAsync(false, Short, CancellationToken.None));

        b!.Dispose();
        c!.Dispose();
        Assert.Equal(0, gate.Running);
        Assert.Equal(2, gate.PeakRunning);
    }

    [Fact]
    public async Task An_operation_that_cannot_start_in_its_time_is_refused_and_a_cancelled_wait_frees_nothing()
    {
        using var gate = new OperationGate(1, 0);
        using var running = await gate.EnterAsync(false, Long, CancellationToken.None);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await gate.EnterAsync(false, Short, CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));

        using var stop = new CancellationTokenSource();
        var waiting = gate.EnterAsync(false, Long, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, gate.Waiting);
        Assert.Equal(1, gate.Running);
        running!.Dispose();
        using var next = await gate.EnterAsync(false, Short, CancellationToken.None);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Long_copies_never_take_the_slots_kept_for_short_operations()
    {
        using var gate = new OperationGate(maxConcurrent: 3, reservedForShort: 2);
        Assert.Equal(1, gate.LongSlots);
        using var backup = await gate.EnterAsync(true, Long, CancellationToken.None);
        Assert.NotNull(backup);
        // A second long copy waits although two slots are free: they are for reads and short changes.
        Assert.Null(await gate.EnterAsync(true, Short, CancellationToken.None));
        using var read1 = await gate.EnterAsync(false, Short, CancellationToken.None);
        using var read2 = await gate.EnterAsync(false, Short, CancellationToken.None);
        Assert.NotNull(read1);
        Assert.NotNull(read2);
        Assert.Null(await gate.EnterAsync(false, Short, CancellationToken.None));
        // The refused long copy did not keep its "long" slot.
        backup!.Dispose();
        read1!.Dispose();
        using var restore = await gate.EnterAsync(true, Short, CancellationToken.None);
        Assert.NotNull(restore);
    }

    [Fact]
    public void Limits_are_clamped_and_the_long_operations_are_the_ones_that_copy_disks()
    {
        try
        {
            OperationLimits.Configure(null, null);
            Assert.Equal(8, OperationLimits.MaxConcurrent);
            Assert.Equal(2, OperationLimits.ReservedForShort);
            OperationLimits.Configure(0, 99);
            Assert.Equal(1, OperationLimits.MaxConcurrent);
            Assert.Equal(0, OperationLimits.ReservedForShort); // one slot: nothing can be reserved
            OperationLimits.Configure(1000, -3);
            Assert.Equal(64, OperationLimits.MaxConcurrent);
            Assert.Equal(0, OperationLimits.ReservedForShort);
            OperationLimits.Configure(4, 10);
            Assert.Equal(3, OperationLimits.ReservedForShort);
        }
        finally
        {
            OperationLimits.Configure(null, null);
        }
        Assert.True(PowerShellOperationRunner.IsLongRunning(HvOperation.BackupVm, null));
        Assert.True(PowerShellOperationRunner.IsLongRunning(HvOperation.MoveVmStorage, null));
        Assert.True(PowerShellOperationRunner.IsLongRunning(HvOperation.SetClusterNodeMaintenance, null));
        Assert.True(PowerShellOperationRunner.IsLongRunning(HvOperation.NewVm, HvOperations.ToElement(new NewVmRequest { Name = "vm", TemplateDisks = [@"D:\T\a.vhdx"] })));
        Assert.False(PowerShellOperationRunner.IsLongRunning(HvOperation.NewVm, HvOperations.ToElement(new NewVmRequest { Name = "vm" })));
        Assert.False(PowerShellOperationRunner.IsLongRunning(HvOperation.GetVmConfig, null));
        Assert.False(PowerShellOperationRunner.IsLongRunning(HvOperation.GetClusterStatus, null));
    }

    [Fact]
    public void Totals_per_operation_are_measured_values_only()
    {
        var metrics = new OperationMetrics();
        Assert.False(metrics.Record(new OperationRun(HvOperation.GetVmConfig, true, false, QueueMs: 0, RunMs: 1400, StartupMs: 700, ScriptMs: 600, PeakMemoryBytes: 140L * 1024 * 1024), summaryEvery: 3));
        Assert.False(metrics.Record(new OperationRun(HvOperation.GetVmConfig, false, true, QueueMs: 900, RunMs: 2000, StartupMs: null, ScriptMs: null, PeakMemoryBytes: null), summaryEvery: 3));
        // Every third run it is time to write the totals to the log.
        Assert.True(metrics.Record(new OperationRun(HvOperation.SetVmConfig, true, false, 10, 5000, 800, 4000, 150L * 1024 * 1024), summaryEvery: 3));
        metrics.RecordBusy(HvOperation.GetVmConfig, 30000);

        var totals = metrics.Snapshot();
        Assert.Equal("SetVmConfig", totals[0].Operation); // most total time first
        var read = totals.Single(t => t.Operation == "GetVmConfig");
        Assert.Equal(2, read.Count);
        Assert.Equal(1, read.Failed);
        Assert.Equal(1, read.TimedOut);
        Assert.Equal(1, read.Busy);
        Assert.Equal(1700, read.AverageMs);
        Assert.Equal(2000, read.MaxMs);
        Assert.Equal(30000, read.MaxQueueMs);
        Assert.Equal(700, read.AverageStartupMs); // the average of the runs that reported it, not of all runs
        Assert.Equal(140L * 1024 * 1024, read.MaxPeakMemoryBytes);
        var text = OperationMetrics.Format(totals);
        Assert.Contains("GetVmConfig x2 avg 1700 ms max 2000 ms start 700 ms", text, StringComparison.Ordinal);
        Assert.Contains("busy 1", text, StringComparison.Ordinal);

        // Nothing measured: nothing shown as if it had been.
        var empty = new OperationMetrics();
        empty.Record(new OperationRun(HvOperation.ListSwitches, true, false, 0, 10, null, null, null));
        var row = Assert.Single(empty.Snapshot());
        Assert.Null(row.AverageStartupMs);
        Assert.Null(row.MaxPeakMemoryBytes);
    }

    [Fact]
    public void The_operations_script_reports_its_own_timing_without_new_capabilities()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ops", "HvPortal.Ops.ps1"));
        Assert.Contains("timing = @{ startupMs = $script:StartupMs; moduleMs = $script:ModuleMs; scriptMs =", script, StringComparison.Ordinal);
        Assert.Contains("$script:ModuleMs = [int64]$script:Clock.ElapsedMilliseconds", script, StringComparison.Ordinal);
        // The storage jobs of a cluster are read with or without Storage Spaces Direct, completed ones included.
        var status = script[script.IndexOf("'GetClusterStatus' {", StringComparison.Ordinal)..script.IndexOf("'SetClusterNodeMaintenance' {", StringComparison.Ordinal)];
        Assert.Contains("Get-Command Get-StorageJob", status, StringComparison.Ordinal);
        Assert.Contains("Get-StorageJob -VirtualDisk $v", status, StringComparison.Ordinal);
        Assert.DoesNotContain("-ne 'Completed'", status, StringComparison.Ordinal);
        Assert.True(status.IndexOf("Get-Command Get-StorageJob", StringComparison.Ordinal) > status.IndexOf("Get-HealthFault -ErrorAction Stop", StringComparison.Ordinal));
    }
}
