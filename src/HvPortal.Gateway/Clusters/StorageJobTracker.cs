using System.Collections.Concurrent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Gateway.Clusters;

/// <summary>How a storage job that is no longer running ended, as far as the portal knows.</summary>
public static class StorageJobOutcomes
{
    /// <summary>Windows reported the job as Completed.</summary>
    public const string Completed = "Completed";
    /// <summary>Windows reported the job as Killed, Terminated or Exception.</summary>
    public const string Failed = "Failed";
    /// <summary>
    /// The job was running at one read and not listed at the next: Windows removes ended jobs after a while, so the
    /// portal does not know how it ended. Never shown as completed.
    /// </summary>
    public const string NotReported = "NotReported";
}

/// <summary>A storage job as last seen, and what became of it.</summary>
public sealed record TrackedStorageJob
{
    public required string Key { get; init; }
    public required ClusterStorageJobStatus Job { get; init; }
    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    /// <summary>Null while the job is listed and not ended; otherwise a value of <see cref="StorageJobOutcomes"/>.</summary>
    public string? Outcome { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
}

/// <summary>
/// Storage jobs of the clusters across reads (0.9.4). Get-StorageJob only lists what Windows remembers now: a job that
/// ended between two reads simply disappears. The tracker keeps the jobs it has seen so that the cluster page can say
/// "no longer reported, outcome not verified" instead of dropping the job (or calling it completed). In memory only:
/// after a restart of the gateway the history starts again from the next read. Reads of the same cluster from different
/// nodes describe the same jobs (same UniqueId), so nothing is counted twice.
/// </summary>
public sealed class StorageJobTracker
{
    public static readonly TimeSpan KeepEnded = TimeSpan.FromHours(24);
    public const int MaxEnded = 50;

    private readonly ConcurrentDictionary<Guid, List<TrackedStorageJob>> _clusters = new();

    /// <summary>The identity of a job across reads: its Windows id, else name + start time, else the name.</summary>
    public static string KeyOf(ClusterStorageJobStatus j) =>
        j.Id is { Length: > 0 } id ? "id:" + id.ToUpperInvariant()
        : j.StartedUtc is { } started ? $"name:{j.Name}|{started.UtcDateTime:O}"
        : "name:" + j.Name;

    /// <summary>True when the report says nothing reliable about storage jobs (section not read): nothing is concluded from it.</summary>
    public static bool JobsUnreadable(ClusterStatusReport report) =>
        report.Storage is null || report.Errors.Any(e => e.StartsWith("storageJobs:", StringComparison.OrdinalIgnoreCase)
                                                         || e.StartsWith("storage:", StringComparison.OrdinalIgnoreCase));

    /// <summary>Jobs known for the cluster: those going on first, then the ended ones, most recent first.</summary>
    public IReadOnlyList<TrackedStorageJob> Get(Guid clusterId) =>
        _clusters.TryGetValue(clusterId, out var list) ? Snapshot(list) : [];

    public void Forget(Guid clusterId) => _clusters.TryRemove(clusterId, out _);

    /// <summary>Records what a status report says about storage jobs and returns the updated list.</summary>
    public IReadOnlyList<TrackedStorageJob> Observe(Guid clusterId, ClusterStatusReport report, DateTimeOffset now)
    {
        var list = _clusters.GetOrAdd(clusterId, _ => []);
        lock (list)
        {
            if (!JobsUnreadable(report))
            {
                var next = Merge(list, report.Storage!.Jobs, now);
                list.Clear();
                list.AddRange(next);
            }
            return Snapshot(list);
        }
    }

    private static List<TrackedStorageJob> Snapshot(List<TrackedStorageJob> list)
    {
        lock (list)
        {
            return list.OrderBy(j => j.Outcome is null ? 0 : 1).ThenByDescending(j => j.EndedUtc ?? j.LastSeenUtc).ToList();
        }
    }

    /// <summary>Pure merge of the known jobs with the jobs of a new read (unit tested).</summary>
    public static List<TrackedStorageJob> Merge(IReadOnlyList<TrackedStorageJob> known, IReadOnlyList<ClusterStorageJobStatus> reported, DateTimeOffset now)
    {
        var result = new Dictionary<string, TrackedStorageJob>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in reported)
        {
            var key = KeyOf(job);
            if (!seen.Add(key))
            {
                continue; // the same job listed twice in one report
            }
            var before = known.FirstOrDefault(k => k.Key == key);
            var outcome = StorageJobStates.IsCompleted(job.State) ? StorageJobOutcomes.Completed
                : StorageJobStates.IsFailed(job.State) ? StorageJobOutcomes.Failed : null;
            result[key] = new TrackedStorageJob
            {
                Key = key,
                Job = job,
                FirstSeenUtc = before?.FirstSeenUtc ?? now,
                LastSeenUtc = now,
                Outcome = outcome,
                // The moment the portal first saw it ended (Windows gives no end time).
                EndedUtc = outcome is null ? null : before is { Outcome: not null, EndedUtc: { } endedBefore } && before.Outcome == outcome ? endedBefore : now,
            };
        }
        foreach (var k in known.Where(k => !seen.Contains(k.Key)))
        {
            // Not listed any more. What was already ended keeps its outcome; what was going on has an unknown end.
            result[k.Key] = k.Outcome is not null ? k : k with { Outcome = StorageJobOutcomes.NotReported, EndedUtc = now };
        }
        var active = result.Values.Where(j => j.Outcome is null);
        var ended = result.Values.Where(j => j.Outcome is not null && now - (j.EndedUtc ?? j.LastSeenUtc) <= KeepEnded)
            .OrderByDescending(j => j.EndedUtc ?? j.LastSeenUtc).Take(MaxEnded);
        return active.Concat(ended).ToList();
    }
}
