using System.Collections.Concurrent;
using HvPortal.Agent.HyperV;
using HvPortal.Contracts.Agent;

namespace HvPortal.Agent.Commands;

/// <summary>One remote Hyper-V host managed by this (management) agent: its own WMI host, operations, console, executor.</summary>
public sealed class RemoteTargetContext : IDisposable
{
    public RemoteTargetContext(Guid hostId, HostTarget target, ILoggerFactory loggers)
    {
        HostId = hostId;
        Target = target;
        Files = new RemoteHostFileAccess(target);
        Host = new WmiHyperVHost(loggers.CreateLogger<WmiHyperVHost>(), target);
        Executor = new CommandExecutor(Host, loggers.CreateLogger<CommandExecutor>(),
            new PowerShellOperationRunner(loggers.CreateLogger<PowerShellOperationRunner>(), target, Files),
            new ConsoleService(loggers.CreateLogger<ConsoleService>(), target));
    }

    public Guid HostId { get; }
    public HostTarget Target { get; }
    public RemoteHostFileAccess Files { get; }
    public IHyperVHost Host { get; }
    public CommandExecutor Executor { get; }

    public void Dispose()
    {
        Files.Dispose();
        Target.Dispose();
    }
}

/// <summary>
/// Remote hosts assigned by the gateway (TargetsConfiguration). The configuration is kept in memory only: after a
/// restart the agent waits for the gateway to send it again. Unchanged targets keep their context (and console
/// sessions); changed credentials replace it.
/// </summary>
public sealed class RemoteTargetRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Guid, RemoteTargetContext> _targets = new();
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<RemoteTargetRegistry> _logger;
    private readonly object _gate = new();

    public RemoteTargetRegistry(ILoggerFactory loggers)
    {
        _loggers = loggers;
        _logger = loggers.CreateLogger<RemoteTargetRegistry>();
    }

    public IReadOnlyCollection<RemoteTargetContext> All => _targets.Values.ToList();

    public bool TryGet(Guid hostId, out RemoteTargetContext context) => _targets.TryGetValue(hostId, out context!);

    /// <summary>Replaces the set of targets. Invalid entries are skipped (and logged without credentials).</summary>
    public void Apply(TargetsConfiguration configuration)
    {
        lock (_gate)
        {
            var wanted = new Dictionary<Guid, RemoteTarget>();
            foreach (var t in configuration.Targets.Take(500))
            {
                if (t.HostId != Guid.Empty)
                {
                    wanted[t.HostId] = t;
                }
            }
            foreach (var (id, existing) in _targets)
            {
                if (!wanted.TryGetValue(id, out var t) || !existing.Target.SameAs(t))
                {
                    if (_targets.TryRemove(id, out var removed))
                    {
                        removed.Dispose();
                    }
                }
            }
            foreach (var (id, t) in wanted)
            {
                if (_targets.ContainsKey(id))
                {
                    continue;
                }
                try
                {
                    _targets[id] = new RemoteTargetContext(id, HostTarget.FromRemote(t), _loggers);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning("Skipping remote host {HostId}: {Message}", id, ex.Message);
                }
            }
            _logger.LogInformation("Managing {Count} remote host(s)", _targets.Count);
        }
    }

    public void Clear() => Apply(new TargetsConfiguration());

    public void Dispose() => Clear();
}
