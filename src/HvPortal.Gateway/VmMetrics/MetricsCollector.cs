using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Audit;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.VmMetrics;

/// <summary>
/// Resource use history of VMs (ADR-039). Every SampleIntervalSeconds, for each connected host (agent 0.8.0+) with at
/// least one VM whose resource metering is on: one GetVmMetrics (Get-VM + Measure-VM) for all those VMs, stored as one
/// sample per VM together with the CPU and memory statistics of the inventory reports of the period. Also keeps the
/// portal's view of "metering on" in line with Hyper-V, resets the Hyper-V counters every ResetDays and deletes samples
/// older than RetentionDays.
/// </summary>
public sealed class MetricsCollector(IServiceScopeFactory scopes, IAgentDispatcher agents, VmMetricsBuffer buffer, AuditLog audit, TimeProvider time,
    IOptions<GatewayOptions> options, ILogger<MetricsCollector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Metrics.CollectorEnabled)
        {
            return;
        }
        // Start keeping inventory readings at once; the first sample is stored after one full interval.
        await RefreshMeteredAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await CollectAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Resource metering pass failed");
            }
        }
    }

    private TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(options.Value.Metrics.SampleIntervalSeconds, 60, 3600));

    public async Task RefreshMeteredAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        buffer.SetMetered(await db.VmMeterings.AsNoTracking().Where(m => m.Enabled).Select(m => m.VmId).ToListAsync(ct));
    }

    /// <summary>One pass (also called by tests).</summary>
    public async Task CollectAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var now = time.GetUtcNow();
        var o = options.Value.Metrics;

        var meterings = await db.VmMeterings.Where(m => m.Enabled).ToListAsync(ct);
        // The VM may have moved to another host since metering was turned on: follow its current host.
        var ids = meterings.Select(m => m.VmId).ToList();
        var currentHost = await db.VirtualMachines.AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.HostId, ct);
        foreach (var m in meterings)
        {
            if (currentHost.TryGetValue(m.VmId, out var h) && h != m.HostId)
            {
                m.HostId = h;
            }
        }
        foreach (var hostGroup in meterings.Where(m => currentHost.ContainsKey(m.VmId)).GroupBy(m => m.HostId))
        {
            var hostId = hostGroup.Key;
            if (!agents.IsConnected(hostId) || agents.ProtocolVersionOf(hostId) < 10)
            {
                continue;
            }
            VmMetricsReport? report;
            try
            {
                var r = await agents.SendAsync(hostId, new AgentCommand
                {
                    CommandId = Guid.NewGuid(),
                    Kind = AgentCommandKind.HyperVOperation,
                    Operation = HvOperation.GetVmMetrics,
                    OperationArgs = HvOperations.ToElement(new VmMetricsRequest(null)),
                    TimeoutSeconds = 120,
                }, null, TimeSpan.FromSeconds(135), ct);
                report = r.Operation is { Ok: true } ok ? HvOperations.FromElement<VmMetricsReport>(ok.Result) : null;
                if (report is null)
                {
                    foreach (var m in hostGroup)
                    {
                        m.LastError = Truncate(r.Operation?.Error ?? r.Message ?? r.Status.ToString(), 1000);
                        m.LastErrorAt = now;
                    }
                    continue;
                }
            }
            catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException)
            {
                continue;
            }

            var vms = await db.VirtualMachines.AsNoTracking().Where(v => v.HostId == hostId).ToDictionaryAsync(v => v.HyperVId, ct);
            var byPortalId = hostGroup.ToDictionary(m => m.VmId);
            foreach (var s in report.Vms)
            {
                if (!vms.TryGetValue(s.VmId, out var vm))
                {
                    continue;
                }
                if (!byPortalId.TryGetValue(vm.Id, out var metering))
                {
                    // Turned on outside the portal (Hyper-V Manager, PowerShell): adopted, so its history is kept too.
                    metering = await db.VmMeterings.FirstOrDefaultAsync(m => m.VmId == vm.Id, ct);
                    if (metering is null)
                    {
                        metering = new VmMetering { VmId = vm.Id, HostId = hostId };
                        db.VmMeterings.Add(metering);
                    }
                    metering.Enabled = true;
                    metering.EnabledAt ??= now;
                    buffer.SetMetered(vm.Id, true);
                    byPortalId[vm.Id] = metering;
                }
                db.VmMetricSamples.Add(ToSample(vm.Id, s, buffer.Take(vm.Id), now));
                metering.LastSampleAt = now;
                metering.LastError = null;
                metering.LastErrorAt = null;
            }
            var reported = report.Vms.Select(s => vms.TryGetValue(s.VmId, out var v) ? v.Id : Guid.Empty).ToHashSet();
            var failed = report.Errors.Select(e => e.Split(':', 2)[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var m in byPortalId.Values.Where(m => !reported.Contains(m.VmId)))
            {
                var hv = vms.Values.FirstOrDefault(v => v.Id == m.VmId);
                if (hv is not null && failed.Contains(hv.HyperVId.ToString("D")))
                {
                    m.LastError = Truncate(report.Errors.First(e => e.StartsWith(hv.HyperVId.ToString("D"), StringComparison.OrdinalIgnoreCase)), 1000);
                    m.LastErrorAt = now;
                    continue;
                }
                if (hv is { IsMissing: false })
                {
                    // Hyper-V no longer meters it: turned off outside the portal.
                    m.Enabled = false;
                    m.LastError = "Resource metering was turned off outside the portal.";
                    m.LastErrorAt = now;
                    buffer.SetMetered(m.VmId, false);
                }
            }
            await db.SaveChangesAsync(ct);

            // Hyper-V totals cover at most ResetDays (the history is in the portal).
            var resetAfter = TimeSpan.FromDays(Math.Clamp(o.ResetDays, 1, 365));
            foreach (var m in byPortalId.Values.Where(m => m.Enabled && now - (m.LastResetAt ?? m.EnabledAt ?? now) >= resetAfter))
            {
                var hv = vms.Values.FirstOrDefault(v => v.Id == m.VmId);
                if (hv is null)
                {
                    continue;
                }
                await ResetAsync(db, hostId, hv, m, now, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        // Retention.
        var cutoff = now - TimeSpan.FromDays(Math.Clamp(o.RetentionDays, 1, 366));
        await db.VmMetricSamples.Where(s => s.At < cutoff).ExecuteDeleteAsync(ct);
        buffer.SetMetered(await db.VmMeterings.AsNoTracking().Where(m => m.Enabled).Select(m => m.VmId).ToListAsync(ct));
    }

    private async Task ResetAsync(PortalDbContext db, Guid hostId, VirtualMachine vm, VmMetering m, DateTimeOffset now, CancellationToken ct)
    {
        string? error;
        try
        {
            var r = await agents.SendAsync(hostId, new AgentCommand
            {
                CommandId = Guid.NewGuid(),
                Kind = AgentCommandKind.HyperVOperation,
                Operation = HvOperation.ResetVmResourceMetering,
                OperationArgs = HvOperations.ToElement(new VmRef(vm.HyperVId)),
                VmId = vm.HyperVId,
                TimeoutSeconds = 60,
            }, null, TimeSpan.FromSeconds(75), ct);
            error = r.Operation is { Ok: true } ? null : r.Operation?.Error ?? r.Message ?? r.Status.ToString();
        }
        catch (Exception ex) when (ex is HostNotConnectedException or AgentChannelLostException or TimeoutException)
        {
            // Unknown outcome: the counters are checked again at the next pass (a reset shows as totals going back).
            error = ex.Message;
        }
        if (error is null)
        {
            m.LastResetAt = now;
        }
        else
        {
            m.LastError = Truncate("Reset of the counters failed: " + error, 1000);
            m.LastErrorAt = now;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditRecord
        {
            Action = "vm.metering.reset", Outcome = error is null ? AuditOutcome.Success : AuditOutcome.Failure, AuthMethod = "system",
            ResourceType = "vm", ResourceId = vm.Id.ToString("D"),
            Details = new { vm = vm.Name, everyDays = options.Value.Metrics.ResetDays, error = Truncate(error, 300) },
        }, ct);
    }

    internal static VmMetricSample ToSample(Guid vmId, VmMetricsSample s, VmMetricsBuffer.Stats stats, DateTimeOffset now)
    {
        var running = string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase);
        return new VmMetricSample
        {
            VmId = vmId,
            At = now,
            ObservedSeconds = stats.ObservedSeconds,
            State = Truncate(s.State, 40),
            ProcessorCount = s.ProcessorCount,
            // Without inventory readings in the period (gateway just started), the reading of this moment.
            CpuAvgPercent = stats.CpuAvg ?? (running ? s.CpuUsagePercent : 0),
            CpuMaxPercent = stats.CpuMax ?? (running ? s.CpuUsagePercent : 0),
            MemoryAssignedAvgMb = stats.MemAvgMb ?? s.MemoryAssignedMb,
            MemoryAssignedMaxMb = Max(stats.MemMaxMb, s.MemoryAssignedMb),
            MemoryDemandMb = s.MemoryDemandMb,
            MemoryMaximumMb = s.MemoryMaximumMb,
            MemoryStartupMb = s.MemoryStartupMb,
            DynamicMemory = s.DynamicMemoryEnabled,
            MemoryStatus = Truncate(s.MemoryStatus, 40),
            MemoryAvailableMinPercent = stats.AvailableMinPercent,
            MeteringSeconds = s.MeteringSeconds,
            AverageCpuMhz = s.AverageCpuMhz,
            MaximumMemoryMb = s.MaximumMemoryMb,
            TotalDiskMb = s.TotalDiskMb,
            NormalizedIoCount = s.NormalizedIoCount,
            AverageLatencyMs = s.AverageLatencyMs,
            DiskReadMb = s.DiskReadMb,
            DiskWrittenMb = s.DiskWrittenMb,
            NetworkInboundMb = s.NetworkInboundMb,
            NetworkOutboundMb = s.NetworkOutboundMb,
        };
    }

    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
