using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Clusters;
using HvPortal.Gateway.Updates;

namespace HvPortal.Gateway.Tests.Unit;

/// <summary>
/// 0.9.4: storage jobs of Windows across reads (StorageJobTracker), their rules in the cluster status, and the outcome
/// of an update of the portal (PortalUpdateService.InstallStatusOf). Pure rules, no host involved.
/// </summary>
public sealed class StorageJobAndUpdateRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);

    private static ClusterStorageJobStatus Job(string? id, string name, string state, int? percent = null) =>
        new() { Id = id, Name = name, State = state, PercentComplete = percent };

    private static ClusterStatusReport Report(IReadOnlyList<ClusterStorageJobStatus> jobs, bool s2d = true, params string[] errors) => new()
    {
        CollectedUtc = T0, Name = "CL01", Nodes = [new ClusterNodeStatus { Name = "HV01", State = "Up" }, new ClusterNodeStatus { Name = "HV02", State = "Up" }],
        Quorum = new ClusterQuorumStatus { WitnessType = "FileShare", WitnessName = "W", WitnessState = "Online" },
        Storage = new ClusterStorageStatus { S2dEnabled = s2d, Jobs = jobs }, Errors = errors,
    };

    private static List<ClusterIssue> Evaluate(ClusterStatusReport report) => ClusterRules.Evaluate(report, [], ClusterRules.Capacity([], report), null);

    [Fact]
    public void A_job_keeps_its_identity_and_what_is_no_longer_listed_has_an_unknown_end()
    {
        var first = StorageJobTracker.Merge([], [Job("a", "Repair", "Running", 10), Job("b", "Rebalance", "Running")], T0);
        Assert.All(first, j => Assert.Null(j.Outcome));

        var second = StorageJobTracker.Merge(first, [Job("A", "Repair", "Running", 55)], T0.AddMinutes(2));
        var repair = second.Single(j => j.Job.Name == "Repair");
        Assert.Null(repair.Outcome);
        Assert.Equal(55, repair.Job.PercentComplete);
        Assert.Equal(T0, repair.FirstSeenUtc); // the same job (id compared without case), not a new one
        var rebalance = second.Single(j => j.Job.Name == "Rebalance");
        Assert.Equal(StorageJobOutcomes.NotReported, rebalance.Outcome);
        Assert.Equal(T0.AddMinutes(2), rebalance.EndedUtc);
        Assert.Null(rebalance.Job.PercentComplete); // what was last read, nothing invented

        // Completed and failed come only from what Windows says.
        var third = StorageJobTracker.Merge(second, [Job("a", "Repair", "Completed", 100), Job("c", "Regeneration", "Exception")], T0.AddMinutes(4));
        Assert.Equal(StorageJobOutcomes.Completed, third.Single(j => j.Job.Name == "Repair").Outcome);
        Assert.Equal(StorageJobOutcomes.Failed, third.Single(j => j.Job.Name == "Regeneration").Outcome);
        Assert.Equal(StorageJobOutcomes.NotReported, third.Single(j => j.Job.Name == "Rebalance").Outcome);
        // Listed again as completed at the next read: the moment it ended is not moved.
        var fourth = StorageJobTracker.Merge(third, [Job("a", "Repair", "Completed", 100)], T0.AddMinutes(6));
        Assert.Equal(T0.AddMinutes(4), fourth.Single(j => j.Job.Name == "Repair").EndedUtc);

        // Ended jobs are forgotten after a day; jobs going on never are.
        var later = StorageJobTracker.Merge(fourth, [Job("d", "Repair", "Running", 1)], T0.AddHours(30));
        Assert.Equal("d", Assert.Single(later).Job.Id);
    }

    [Fact]
    public void A_job_listed_twice_is_one_job_and_jobs_without_an_id_are_told_apart_by_name_and_start()
    {
        var merged = StorageJobTracker.Merge([], [Job("x", "Repair", "Running", 5), Job("x", "Repair", "Running", 5)], T0);
        Assert.Single(merged);
        var a = new ClusterStorageJobStatus { Name = "Repair", State = "Running", StartedUtc = T0 };
        var b = new ClusterStorageJobStatus { Name = "Repair", State = "Running", StartedUtc = T0.AddMinutes(1) };
        Assert.NotEqual(StorageJobTracker.KeyOf(a), StorageJobTracker.KeyOf(b));
        Assert.Equal(2, StorageJobTracker.Merge([], [a, b], T0).Count);
        Assert.Equal(StorageJobTracker.KeyOf(Job("ABC", "x", "Running")), StorageJobTracker.KeyOf(Job("abc", "y", "Running")));
    }

    [Fact]
    public void An_unreadable_storage_section_changes_nothing_in_the_tracker()
    {
        var tracker = new StorageJobTracker();
        var cluster = Guid.NewGuid();
        tracker.Observe(cluster, Report([Job("a", "Repair", "Running", 10)]), T0);
        // The section failed, or the whole storage part is missing: the job is neither ended nor "not reported".
        var kept = tracker.Observe(cluster, Report([], errors: "storageJobs: Access denied"), T0.AddMinutes(1));
        Assert.Null(Assert.Single(kept).Outcome);
        kept = tracker.Observe(cluster, Report([]) with { Storage = null }, T0.AddMinutes(2));
        Assert.Null(Assert.Single(kept).Outcome);
        Assert.True(StorageJobTracker.JobsUnreadable(Report([], errors: "storage: boom")));
        Assert.False(StorageJobTracker.JobsUnreadable(Report([], errors: "networks: boom")));
        // A good read without the job: now it is "not reported".
        var ended = tracker.Observe(cluster, Report([]), T0.AddMinutes(3));
        Assert.Equal(StorageJobOutcomes.NotReported, Assert.Single(ended).Outcome);
        Assert.Empty(tracker.Get(Guid.NewGuid()));
        tracker.Forget(cluster);
        Assert.Empty(tracker.Get(cluster));
    }

    [Fact]
    public void A_running_resynchronization_is_a_warning_and_a_failed_or_suspended_job_a_problem_of_its_own()
    {
        var running = Evaluate(Report([Job("a", "Repair", "Running", 30), Job("b", "Rebalance", "Running", 80)]));
        var sync = Assert.Single(running, i => i.Code == "storage_repair_running");
        Assert.Equal(HealthLevel.Warning, sync.Level);
        Assert.Equal("30", sync.Args!["percent"]);
        Assert.Equal("2", sync.Args["jobs"]);

        // No percentage from Windows: the problem says so instead of showing 0 %.
        var blind = Evaluate(Report([Job("a", "Repair", "Running")]));
        Assert.Contains(blind, i => i.Code == "storage_repair_running_no_progress" && i.Level == HealthLevel.Warning);
        Assert.DoesNotContain(blind, i => i.Args?.ContainsKey("percent") == true);
        Assert.Null(ClusterRules.SlowestPercent([Job("a", "Repair", "Running")]));

        var failed = Evaluate(Report([Job("a", "Repair", "Killed"), Job("b", "Rebalance", "Suspended", 12), Job("c", "Old", "Completed", 100)]));
        Assert.Contains(failed, i => i.Code == "storage_job_failed" && i.Level == HealthLevel.Critical);
        Assert.Contains(failed, i => i.Code == "storage_job_suspended" && i.Level == HealthLevel.Warning);
        Assert.DoesNotContain(failed, i => i.Code.StartsWith("storage_repair_running", StringComparison.Ordinal));
        Assert.Equal(HealthLevel.Critical, ClusterRules.Overall(failed));

        // Completed jobs are not problems; jobs are evaluated also without Storage Spaces Direct (agent 0.9.4).
        Assert.DoesNotContain(Evaluate(Report([Job("c", "Old", "Completed", 100)])), i => i.Code.StartsWith("storage_", StringComparison.Ordinal));
        Assert.Contains(Evaluate(Report([Job("a", "Repair", "Running", 5)], s2d: false)), i => i.Code == "storage_repair_running");
    }

    [Fact]
    public void A_storage_replica_copy_in_progress_is_a_warning_and_a_stopped_replication_is_critical()
    {
        ClusterStatusReport With(string status) => Report([]) with { Replication = [new StorageReplicaStatus { Name = "RG01", Status = status, BytesRemaining = 1024 }] };
        Assert.DoesNotContain(Evaluate(With("ContinuouslyReplicating")), i => i.Code.StartsWith("replication_", StringComparison.Ordinal));
        Assert.Contains(Evaluate(With("InitialBlockCopy")), i => i.Code == "replication_not_in_sync" && i.Level == HealthLevel.Warning);
        Assert.Contains(Evaluate(With("ReplicationSuspended")), i => i.Code == "replication_stopped" && i.Level == HealthLevel.Critical);
        Assert.Contains(Evaluate(With("Error")), i => i.Code == "replication_stopped" && i.Level == HealthLevel.Critical);
    }

    [Fact]
    public void An_update_is_installed_only_when_the_requested_version_is_the_one_running()
    {
        var requested = new PortalUpdateService.State { InstallRequestedVersion = "0.9.5", InstallRequestedAt = T0 };
        static PortalUpdateService.LastResult Result(DateTimeOffset at, string status, string message) => new(at, status, "0.9.5", message);

        Assert.Equal("none", PortalUpdateService.InstallStatusOf(new PortalUpdateService.State(), Result(T0, "failed", "x"), "0.9.4").Outcome);
        Assert.Equal("pending", PortalUpdateService.InstallStatusOf(requested, null, "0.9.4").Outcome);
        // The updater said "ok" but another version runs: not a verified update.
        Assert.Equal("pending", PortalUpdateService.InstallStatusOf(requested, Result(T0.AddMinutes(1), "ok", "Installed 0.9.5"), "0.9.4").Outcome);
        Assert.Equal("installed", PortalUpdateService.InstallStatusOf(requested, null, "0.9.5").Outcome);
        Assert.Equal("installed", PortalUpdateService.InstallStatusOf(requested, null, "0.9.5+build7").Outcome);

        var failed = PortalUpdateService.InstallStatusOf(requested, Result(T0.AddMinutes(1), "failed", "The setup is not signed by the publisher"), "0.9.4");
        Assert.Equal("failed", failed.Outcome);
        Assert.Contains("not signed", failed.Message, StringComparison.Ordinal);
        // A failure written before this request belongs to an earlier update.
        Assert.Equal("pending", PortalUpdateService.InstallStatusOf(requested, Result(T0.AddMinutes(-5), "failed", "old"), "0.9.4").Outcome);
        // The running version wins over a result file left behind.
        Assert.Equal("installed", PortalUpdateService.InstallStatusOf(requested, Result(T0.AddMinutes(1), "failed", "x"), "0.9.5").Outcome);
    }
}
