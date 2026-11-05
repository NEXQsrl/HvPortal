using System.Collections.Concurrent;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace HvPortal.Gateway.Realtime;

/// <summary>
/// Server -> browser notifications. Messages only carry identifiers and status; the browser re-reads data
/// through the authorized REST API. Each message is delivered only to users allowed to see the resource.
/// Clients cannot invoke any server method.
/// </summary>
[Authorize(Policy = AuthPolicies.Full)]
public sealed class EventsHub(ConnectedUsers users) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var id = Context.User!.UserId();
        users.Add(id);
        await Groups.AddToGroupAsync(Context.ConnectionId, ConnectedUsers.Group(id));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User is { } u && u.Identity?.IsAuthenticated == true)
        {
            users.Remove(u.UserId());
        }
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed class ConnectedUsers
{
    private readonly ConcurrentDictionary<Guid, int> _counts = new();

    public static string Group(Guid userId) => "u:" + userId.ToString("N");

    public void Add(Guid id) => _counts.AddOrUpdate(id, 1, (_, c) => c + 1);

    public void Remove(Guid id)
    {
        if (_counts.AddOrUpdate(id, 0, (_, c) => c - 1) <= 0)
        {
            _counts.TryRemove(new KeyValuePair<Guid, int>(id, 0));
        }
    }

    public IReadOnlyCollection<Guid> All => _counts.Keys.ToList();
}

public interface IRealtimeNotifier
{
    Task HostChangedAsync(Guid hostId);
    Task JobChangedAsync(Job job);
}

public sealed class RealtimeNotifier(IHubContext<EventsHub> hub, ConnectedUsers users, IServiceScopeFactory scopes, ILogger<RealtimeNotifier> logger)
    : IRealtimeNotifier
{
    public async Task HostChangedAsync(Guid hostId)
    {
        try
        {
            var targets = users.All;
            if (targets.Count == 0)
            {
                return;
            }
            using var scope = scopes.CreateScope();
            var access = scope.ServiceProvider.GetRequiredService<AccessEvaluator>();
            foreach (var u in targets)
            {
                if (await access.CanSeeHostAsync(u, hostId))
                {
                    await hub.Clients.Group(ConnectedUsers.Group(u)).SendAsync("inventoryChanged", new { hostId });
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Realtime host notification failed");
        }
    }

    public async Task JobChangedAsync(Job job)
    {
        try
        {
            var payload = new { jobId = job.Id, status = job.Status.ToString(), progress = job.Progress, targetId = job.TargetId };
            await hub.Clients.Group(ConnectedUsers.Group(job.RequestedByUserId)).SendAsync("jobChanged", payload);
            using var scope = scopes.CreateScope();
            var access = scope.ServiceProvider.GetRequiredService<AccessEvaluator>();
            foreach (var u in users.All.Where(u => u != job.RequestedByUserId))
            {
                if (await access.HasAsync(u, Permissions.JobsReadAll, ResourceRef.Global))
                {
                    await hub.Clients.Group(ConnectedUsers.Group(u)).SendAsync("jobChanged", payload);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Realtime job notification failed");
        }
    }
}

public static class AuthPolicies
{
    /// <summary>Authenticated, valid session, no pending restriction (password change / MFA enrollment).</summary>
    public const string Full = "full";
    /// <summary>Authenticated with a valid session, restrictions allowed (used by /me, logout, password change, MFA enrollment).</summary>
    public const string AnySession = "any-session";
}
