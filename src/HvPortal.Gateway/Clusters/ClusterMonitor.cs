using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Configuration;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.VmBackup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HvPortal.Gateway.Clusters;

/// <summary>
/// Reads every cluster (through any connected node agent, ADR-041) every Gateway:Clusters:MonitorIntervalSeconds, keeps
/// the last status on the cluster record (shown in the tree and on the dashboard without waiting for a live read) and
/// sends a notification (e-mail / webhooks, when configured and enabled) when the status of a cluster gets worse and
/// when it is back to normal. Read-only: it never changes anything in the cluster.
/// </summary>
public sealed class ClusterMonitor(IServiceScopeFactory scopes, IAgentDispatcher agents, NotificationService notifications, TimeProvider time,
    IOptions<GatewayOptions> options, ILogger<ClusterMonitor> logger, Realtime.IRealtimeNotifier notifier) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Clusters.MonitorEnabled)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Value.Clusters.MonitorIntervalSeconds, 30, 3600)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Cluster monitor pass failed");
            }
        }
    }

    /// <summary>One pass over all clusters (also called by tests).</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ClusterService>();
        var clusters = await db.Clusters.AsNoTracking().ToListAsync(ct);
        foreach (var cluster in clusters)
        {
            var nodes = await db.Hosts.AsNoTracking().Where(h => h.ClusterId == cluster.Id && h.Status == HostStatus.Active).Select(h => h.Id).ToListAsync(ct);
            if (!nodes.Any(agents.IsConnected))
            {
                continue; // nothing can be read; the nodes' own status (disconnected) is visible anyway
            }
            var before = cluster.LastStatusLevel;
            var read = await service.ReadStatusAsync(cluster, ct);
            var after = await db.Clusters.AsNoTracking().Where(c => c.Id == cluster.Id).Select(c => c.LastStatusLevel).FirstOrDefaultAsync(ct);
            if (after is not null && before != after)
            {
                // The dashboards and trees that are open read the clusters again (their problems changed).
                await notifier.ClustersChangedAsync();
                await NotifyAsync(service, cluster, before, after, read, ct);
            }
        }
    }

    private static int Rank(string? level) => level switch
    {
        HealthLevel.Critical => 3,
        HealthLevel.Warning => 2,
        HealthLevel.Unknown => 1,
        _ => 0,
    };

    private async Task NotifyAsync(ClusterService service, HostCluster cluster, string? before, string after, ClusterService.ClusterRead read, CancellationToken ct)
    {
        var settings = await notifications.LoadAsync(ct);
        if (!settings.ClusterProblems)
        {
            return;
        }
        var worse = Rank(after) > Rank(before);
        var recovered = after == HealthLevel.Ok && Rank(before) >= 2;
        if (!worse && !recovered)
        {
            return;
        }
        string text;
        if (recovered)
        {
            text = $"Cluster {cluster.Name}: no problems found any more (was {before}).";
        }
        else
        {
            var hosts = await service.NodesAsync(cluster.Id, ct);
            var facts = await service.NodeFactsAsync(hosts, read.Report, ct);
            var issues = ClusterRules.Evaluate(read.Report, facts, ClusterRules.Capacity(facts, read.Report), read.Error)
                .Where(i => i.Level is HealthLevel.Critical or HealthLevel.Warning or HealthLevel.Unknown).Take(20).ToList();
            text = $"Cluster {cluster.Name}: {after} (was {before ?? "not read"}), read {time.GetUtcNow():u}"
                   + (read.Report?.ReadByNode is { } node ? $" through node {node}" : "") + ".\n"
                   + string.Join("\n", issues.Select(i => $"- [{i.Level}] {i.Message}"));
        }
        await notifications.SendAsync(new NotificationService.Message("cluster.status", recovered ? "info" : after == HealthLevel.Critical ? "critical" : "warning",
            $"Cluster {cluster.Name}: {after}", text, new { clusterId = cluster.Id, cluster.Name, before, after }), ct, settings);
    }
}
