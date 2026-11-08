using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>
/// Backups (ADR-031..033): repositories, jobs, runs, restore points, retention, restores, restore tests, file restore,
/// notifications settings and read-only API tokens. The agent is the test double: what Hyper-V really does is in the
/// real-host test plan.
/// </summary>
public sealed class BackupTests : IAsyncLifetime
{
    private GatewayFactory _app = null!;

    public async Task InitializeAsync()
    {
        _app = new GatewayFactory();
        _ = _app.Server;
        await _app.SetMfaPolicyAsync(MfaRequirement.None);
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private async Task<HttpClient> UserAsync(string name, string role)
    {
        await _app.CreateUserAsync(name, role);
        var c = _app.CreateBrowser();
        await c.LoginAsync(name);
        return c;
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient c, string url, object body)
    {
        await c.CsrfAsync();
        return await c.PutAsJsonAsync(url, body, GatewayFactory.Json);
    }

    private async Task SetStagingAsync(Guid hostId, string path) => await _app.WithDbAsync(async db =>
    {
        var h = await db.Hosts.FirstAsync(x => x.Id == hostId);
        h.BackupStagingPath = path;
        await db.SaveChangesAsync();
    });

    private static object Job(string name, Guid repositoryId, Guid[] vmIds, int retention = 7, Guid[]? groupIds = null) => new
    {
        name, repositoryId, enabled = true, scheduleKind = "daily", daysMask = 0x7F, times = new[] { "22:00" }, intervalHours = 6, timeZoneId = "UTC",
        retentionPoints = retention, checkpointType = "Production", verifyAfterCopy = true, restoreTestDays = 0, notifyOn = "warning", vmIds, groupIds = groupIds ?? [],
    };

    private async Task<Guid> RepositoryAsync(HttpClient c, string name = "NAS", bool credentials = false)
    {
        var r = await c.PostJsonAsync("/api/v1/backup/repositories", credentials
            ? new { name, path = @"\\nas01\backups\hv", userName = @"NAS01\backup", password = "Nas-Pa55word!" }
            : new { name, path = @"\\nas01\backups\hv" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.JsonAsync()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Repositories_are_managed_by_backup_managers_and_their_password_never_leaves_the_gateway_except_inside_a_command()
    {
        var (hostId, vms) = await _app.CreateHostAsync("bk-host", ("web01", VmPowerState.Running));
        var admin = await UserAsync("bk-admin", BuiltInRoles.BackupOperator);
        var reader = await UserAsync("bk-reader", BuiltInRoles.ReadOnly);

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostJsonAsync("/api/v1/backup/repositories", new { name = "X", path = @"\\nas01\x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/backup/repositories")).StatusCode);
        Assert.Equal("host_required", await (await admin.PostJsonAsync("/api/v1/backup/repositories", new { name = "Local", path = @"D:\Backups" })).CodeAsync());
        Assert.Equal("invalid_repository_path", await (await admin.PostJsonAsync("/api/v1/backup/repositories", new { name = "Bad", path = @"\\nas01\c$" })).CodeAsync());
        Assert.Equal("credentials_only_for_shares", await (await admin.PostJsonAsync("/api/v1/backup/repositories",
            new { name = "Local", path = @"D:\Backups", hostId, userName = "u", password = "p" })).CodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJsonAsync("/api/v1/backup/repositories", new { name = "Local", path = @"D:\Backups", hostId })).StatusCode);
        var repoId = await RepositoryAsync(admin, credentials: true);
        var listed = await (await admin.GetAsync("/api/v1/backup/repositories")).ReadAsStringAsync2();
        Assert.DoesNotContain("Nas-Pa55word!", listed, StringComparison.Ordinal);
        Assert.Contains("\"hasCredentials\":true", listed, StringComparison.Ordinal);

        // A share with credentials needs a staging folder on the host (Hyper-V cannot use the credentials).
        var vm = vms["web01"];
        Assert.Equal("staging_required", await (await admin.PostJsonAsync($"/api/v1/vms/{vm}/backup", new { repositoryId = repoId })).CodeAsync());
        // A rescan says the same (not "no connected host"), and works once the host has a staging folder.
        Assert.Equal("staging_required", await (await admin.PostJsonAsync($"/api/v1/backup/repositories/{repoId}/rescan", new { })).CodeAsync());
        await SetStagingAsync(hostId, @"D:\Staging");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJsonAsync($"/api/v1/backup/repositories/{repoId}/rescan", new { })).StatusCode);
        var accepted = await admin.PostJsonAsync($"/api/v1/vms/{vm}/backup", new { repositoryId = repoId });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var jobId = (await accepted.JsonAsync()).GetProperty("jobId").GetGuid();
        // The stored job has no credentials; the command sent to the agent has them.
        var stored = await _app.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId));
        Assert.DoesNotContain("Nas-Pa55word!", stored.ParametersJson!, StringComparison.Ordinal);
        await _app.RunJobsAsync();
        var sent = HvOperations.FromElement<BackupVmRequest>(_app.Agent.OperationsReceived(HvOperation.BackupVm).Single().OperationArgs)!;
        Assert.Equal(("Nas-Pa55word!", @"NAS01\backup", @"D:\Staging"), (sent.Target.Password, sent.Target.UserName, sent.Target.StagingPath));
        var auditDetails = await _app.WithDbAsync(db => db.AuditEntries.Select(a => a.DetailsJson ?? "").ToListAsync());
        Assert.DoesNotContain(auditDetails, d => d.Contains("Nas-Pa55word!", StringComparison.Ordinal));
        var done = await _app.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId));
        Assert.Equal(JobStatus.Succeeded, done.Status);
        Assert.DoesNotContain("Nas-Pa55word!", done.ResultJson ?? "", StringComparison.Ordinal);

        // The repository cannot be deleted while it holds restore points.
        Assert.Equal("repository_has_points", await (await admin.DeleteWithCsrfAsync($"/api/v1/backup/repositories/{repoId}")).CodeAsync());
    }

    [Fact]
    public async Task A_job_backs_up_its_vms_and_groups_closes_the_run_and_keeps_the_newest_points()
    {
        var (hostId, vms) = await _app.CreateHostAsync("job-host", ("web01", VmPowerState.Running), ("web02", VmPowerState.Off), ("db01", VmPowerState.Running));
        var admin = await UserAsync("job-admin", BuiltInRoles.InfrastructureAdmin);
        var repoId = await RepositoryAsync(admin);
        var groupId = (await (await admin.PostJsonAsync("/api/v1/groups", new { name = "Web" })).JsonAsync()).GetProperty("id").GetGuid();
        await admin.PostJsonAsync($"/api/v1/groups/{groupId}/members", new { vmIds = new[] { vms["web01"], vms["web02"] } });

        // Who saves a job must be allowed to back up what it protects (runs execute as that user).
        var operatorClient = await UserAsync("job-vmop", BuiltInRoles.VmOperator);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.PostJsonAsync("/api/v1/backup/jobs", Job("Nope", repoId, [vms["db01"]]))).StatusCode);
        Assert.Equal("invalid_schedule", await (await admin.PostJsonAsync("/api/v1/backup/jobs", new { name = "Bad", repositoryId = repoId, scheduleKind = "daily", times = new[] { "25:00" }, daysMask = 1, retentionPoints = 3, vmIds = new[] { vms["db01"] } })).CodeAsync());

        var created = await admin.PostJsonAsync("/api/v1/backup/jobs", Job("Nightly", repoId, [vms["db01"]], retention: 2, groupIds: [groupId]));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var job = (await created.JsonAsync()).GetProperty("id").GetGuid();
        Assert.NotNull(await _app.WithDbAsync(db => db.BackupJobs.Where(j => j.Id == job).Select(j => j.NextRunAt).FirstAsync()));

        for (var i = 0; i < 3; i++)
        {
            var run = await admin.PostJsonAsync($"/api/v1/backup/jobs/{job}/run");
            Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
            Assert.Equal(3, (await run.JsonAsync()).GetProperty("total").GetInt32());
            Assert.Equal("run_in_progress", await (await admin.PostJsonAsync($"/api/v1/backup/jobs/{job}/run")).CodeAsync());
            await _app.RunJobsAsync();
            await _app.Scheduler.TickAsync(CancellationToken.None);
            await _app.RunJobsAsync(); // retention removals
            await Task.Delay(20);
        }
        var runs = await _app.WithDbAsync(db => db.BackupRuns.AsNoTracking().Where(r => r.BackupJobId == job).ToListAsync());
        Assert.Equal(3, runs.Count);
        Assert.All(runs, r => Assert.Equal((BackupRunStatus.Success, 3, 3), (r.Status, r.Succeeded, r.Total)));
        // Retention 2: per VM the two newest good points stay, the oldest was deleted by a job.
        var points = await _app.WithDbAsync(db => db.BackupPoints.AsNoTracking().Where(p => p.BackupJobId == job).ToListAsync());
        foreach (var perVm in points.GroupBy(p => p.VmId))
        {
            Assert.Equal(2, perVm.Count(p => p.Status == BackupPointStatus.Ok));
            Assert.Equal(1, perVm.Count(p => p.Status == BackupPointStatus.Deleted));
            Assert.Equal(perVm.Where(p => p.Status == BackupPointStatus.Deleted).Single().CreatedAt, perVm.Min(p => p.CreatedAt));
        }
        var ok = points.First(p => p.Status == BackupPointStatus.Ok);
        Assert.True(ok.VerifyOk);
        Assert.Equal("Production", ok.CheckpointTypeUsed);
        Assert.Single(JsonSerializer.Deserialize<BackupManifest>(ok.ManifestJson!, AgentProtocol.Json)!.Disks);

        var overview = await (await admin.GetAsync("/api/v1/backup/overview")).JsonAsync();
        Assert.Equal(3, overview.GetProperty("protectedVms").GetInt32());
        Assert.Equal(3, overview.GetProperty("last24h").GetProperty("success").GetInt32());
        var vmPoints = await (await admin.GetAsync($"/api/v1/vms/{vms["web01"]}/backups")).JsonAsync();
        Assert.Equal(2, vmPoints.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task A_failed_backup_makes_a_failed_point_and_a_failed_run_and_a_lost_result_is_never_ok()
    {
        var (_, vms) = await _app.CreateHostAsync("fail-host", ("web01", VmPowerState.Running));
        var admin = await UserAsync("fail-admin", BuiltInRoles.InfrastructureAdmin);
        var repoId = await RepositoryAsync(admin);
        _app.Agent.BackupFailure = "Checkpoints are disabled for this VM";
        var jobId = (await (await admin.PostJsonAsync($"/api/v1/vms/{vms["web01"]}/backup", new { repositoryId = repoId })).JsonAsync()).GetProperty("jobId").GetGuid();
        await _app.RunJobsAsync();
        await _app.Scheduler.TickAsync(CancellationToken.None);
        var point = await _app.WithDbAsync(db => db.BackupPoints.AsNoTracking().SingleAsync(p => p.JobId == jobId));
        Assert.Equal(BackupPointStatus.Failed, point.Status);
        Assert.Contains("Checkpoints are disabled", point.Error);
        Assert.Equal(BackupRunStatus.Failed, (await _app.WithDbAsync(db => db.BackupRuns.AsNoTracking().SingleAsync(r => r.Id == point.RunId))).Status);
        _app.Agent.BackupFailure = null;

        // The agent channel drops after the command: the outcome is unknown, the point is "Unverified", not Ok.
        _app.Agent.OperationHandler = (_, c) => c.Operation == HvOperation.BackupVm ? throw new HvPortal.Gateway.Agents.AgentChannelLostException(Guid.Empty) : null!;
        var lost = (await (await admin.PostJsonAsync($"/api/v1/vms/{vms["web01"]}/backup", new { repositoryId = repoId })).JsonAsync()).GetProperty("jobId").GetGuid();
        await _app.RunJobsAsync();
        await _app.Runner.ReconcileAsync(CancellationToken.None);
        _app.Agent.OperationHandler = null;
        var lostPoint = await _app.WithDbAsync(db => db.BackupPoints.AsNoTracking().SingleAsync(p => p.JobId == lost));
        Assert.Equal(BackupPointStatus.Unverified, lostPoint.Status);
        Assert.Equal(JobStatus.Unknown, (await _app.WithDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == lost))).Status);
    }

    [Fact]
    public async Task Restores_create_a_new_vm_or_replace_the_original_with_confirmation_and_restore_tests_are_reported_honestly()
    {
        var (hostId, vms) = await _app.CreateHostAsync("rs-host", ("web01", VmPowerState.Running));
        var admin = await UserAsync("rs-admin", BuiltInRoles.InfrastructureAdmin);
        var repoId = await RepositoryAsync(admin);
        await admin.PostJsonAsync($"/api/v1/vms/{vms["web01"]}/backup", new { repositoryId = repoId });
        await _app.RunJobsAsync();
        var point = await _app.WithDbAsync(db => db.BackupPoints.AsNoTracking().SingleAsync(p => p.VmId == vms["web01"]));
        Assert.Equal(BackupPointStatus.Ok, point.Status);

        // A user with no backup permission does not even see the restore point; one who reads it without backup.restore is refused and audited.
        var vmop = await UserAsync("rs-vmop", BuiltInRoles.VmOperator);
        Assert.Equal(HttpStatusCode.NotFound, (await vmop.PostJsonAsync($"/api/v1/backup/points/{point.Id}/restore", new { mode = "NewVm", name = "web01-r" })).StatusCode);
        var runner = await _app.CreateUserAsync("rs-runner", BuiltInRoles.ReadOnly);
        await _app.WithDbAsync(async db =>
        {
            // A role with backup.run but not backup.restore, on this VM only.
            var role = new Role { Name = "Backup runner", Permissions = [new RolePermission { Permission = Permissions.InventoryRead }, new RolePermission { Permission = Permissions.BackupRun }] };
            db.Roles.Add(role);
            db.RoleAssignments.Add(new RoleAssignment { PrincipalId = runner.ToString("D"), RoleId = role.Id, ScopeType = ScopeType.Vm, ScopeId = vms["web01"].ToString("D"), Effect = AssignmentEffect.Allow, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        var runnerClient = _app.CreateBrowser();
        await runnerClient.LoginAsync("rs-runner");
        Assert.Equal(HttpStatusCode.Forbidden, (await runnerClient.PostJsonAsync($"/api/v1/backup/points/{point.Id}/restore", new { mode = "NewVm", name = "web01-r" })).StatusCode);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "backup.restore.request" && a.Outcome == AuditOutcome.Denied)));

        // As a new VM on the same network.
        var newVm = await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/restore", new { mode = "NewVm", name = "web01-restored", network = "Same", location = @"D:\Restored" });
        Assert.Equal(HttpStatusCode.Accepted, newVm.StatusCode);
        await _app.RunJobsAsync();
        var restore = HvOperations.FromElement<RestoreBackupRequest>(_app.Agent.OperationsReceived(HvOperation.RestoreBackup).Last().OperationArgs)!;
        Assert.Equal((RestoreModes.NewVm, "web01-restored", @"D:\Restored", point.Folder), (restore.Mode, restore.NewName, restore.Location, restore.Folder));
        Assert.Equal("vm_name_exists", await (await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/restore", new { mode = "NewVm", name = "web01" })).CodeAsync());

        // Over the original: the name must be typed.
        Assert.Equal("confirmation_required", await (await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/restore", new { mode = "Replace", confirmName = "nope" })).CodeAsync());
        var replace = await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/restore", new { mode = "Replace", confirmName = "web01" });
        Assert.Equal(HttpStatusCode.Accepted, replace.StatusCode);
        await _app.RunJobsAsync();
        var replaced = HvOperations.FromElement<RestoreBackupRequest>(_app.Agent.OperationsReceived(HvOperation.RestoreBackup).Last().OperationArgs)!;
        Assert.Equal(await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["web01"]).Select(v => v.HyperVId).FirstAsync()), replaced.ReplaceVmId);

        // Restore test: always isolated; a test that fails makes a failed job and a point marked "test failed".
        await SetStagingAsync(hostId, @"D:\Staging");
        _app.Agent.RestoreTestPasses = false;
        var test = await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/restore", new { mode = "Test", network = "Same", startAfter = true });
        var testJob = (await test.JsonAsync()).GetProperty("jobId").GetGuid();
        await _app.RunJobsAsync();
        var testArgs = HvOperations.FromElement<RestoreBackupRequest>(_app.Agent.OperationsReceived(HvOperation.RestoreBackup).Last().OperationArgs)!;
        Assert.Equal((RestoreNetworks.Disconnected, false, @"D:\Staging\restore-tests"), (testArgs.Network, testArgs.StartAfter, testArgs.Location));
        var job = await _app.WithDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == testJob));
        Assert.Equal((JobStatus.Failed, "restore_test_failed"), (job.Status, job.ErrorCode));
        var tested = await _app.WithDbAsync(db => db.BackupPoints.AsNoTracking().SingleAsync(p => p.Id == point.Id));
        Assert.False(tested.TestPassed);
        _app.Agent.RestoreTestPasses = true;
        await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/restore", new { mode = "Test" });
        await _app.RunJobsAsync();
        Assert.True((await _app.WithDbAsync(db => db.BackupPoints.AsNoTracking().SingleAsync(p => p.Id == point.Id))).TestPassed);

        // Verify and restore of a single disk attached to a VM (needs vm.config.storage on that VM).
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/verify")).StatusCode);
        await _app.RunJobsAsync();
        Assert.True((await _app.WithDbAsync(db => db.BackupPoints.AsNoTracking().SingleAsync(p => p.Id == point.Id))).VerifyOk);
        var disk = Assert.Single(JsonSerializer.Deserialize<BackupManifest>(point.ManifestJson!, AgentProtocol.Json)!.Disks);
        Assert.Equal("invalid_disk", await (await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/restore-disk", new { disk = @"..\x.vhdx", destinationFolder = @"D:\R" })).CodeAsync());
        var restoreDisk = await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/restore-disk", new { disk, destinationFolder = @"D:\Restored", attachVmId = vms["web01"] });
        Assert.Equal(HttpStatusCode.Accepted, restoreDisk.StatusCode);
        await _app.RunJobsAsync();
        Assert.NotNull(HvOperations.FromElement<RestoreDiskRequest>(_app.Agent.OperationsReceived(HvOperation.RestoreBackupDisk).Single().OperationArgs)!.AttachVmId);

        // Deleting a restore point: backup managers only, the VM name typed.
        Assert.Equal(HttpStatusCode.Forbidden, (await runnerClient.PostJsonAsync($"/api/v1/backup/points/{point.Id}/delete", new { confirmName = "web01" })).StatusCode);
        Assert.Equal("confirmation_required", await (await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/delete", new { confirmName = "x" })).CodeAsync());
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/delete", new { confirmName = "web01" })).StatusCode);
        await _app.RunJobsAsync();
        Assert.Equal(BackupPointStatus.Deleted, (await _app.WithDbAsync(db => db.BackupPoints.AsNoTracking().SingleAsync(p => p.Id == point.Id))).Status);
    }

    [Fact]
    public async Task Files_of_a_restore_point_are_browsed_only_by_who_attached_the_disk_and_downloads_are_audited()
    {
        var (_, vms) = await _app.CreateHostAsync("fr-host", ("file01", VmPowerState.Running));
        var admin = await UserAsync("fr-admin", BuiltInRoles.InfrastructureAdmin);
        var repoId = await RepositoryAsync(admin);
        await admin.PostJsonAsync($"/api/v1/vms/{vms["file01"]}/backup", new { repositoryId = repoId });
        await _app.RunJobsAsync();
        var point = await _app.WithDbAsync(db => db.BackupPoints.AsNoTracking().SingleAsync(p => p.VmId == vms["file01"]));
        var disk = JsonSerializer.Deserialize<BackupManifest>(point.ManifestJson!, AgentProtocol.Json)!.Disks[0];
        var mount = await (await admin.PostJsonAsync($"/api/v1/backup/points/{point.Id}/mount", new { disk })).JsonAsync();
        var mountId = mount.GetProperty("mountId").GetGuid();
        Assert.Equal(@"F:\", mount.GetProperty("volumes")[0].GetProperty("root").GetString());
        var root = await (await admin.GetAsync($"/api/v1/backup/mounts/{mountId}/files?path={Uri.EscapeDataString(@"F:\")}")).JsonAsync();
        Assert.Contains(root.GetProperty("files").EnumerateArray(), f => f.GetProperty("name").GetString() == "report.docx");
        // Outside the attached volumes: refused by the gateway (and by the agent).
        Assert.Equal("invalid_path", await (await admin.GetAsync($"/api/v1/backup/mounts/{mountId}/files?path={Uri.EscapeDataString(@"C:\Windows")}")).CodeAsync());
        var download = await admin.GetAsync($"/api/v1/backup/mounts/{mountId}/download?path={Uri.EscapeDataString(@"F:\report.docx")}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await download.Content.ReadAsByteArrayAsync());
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "backup.files.download" && a.Outcome == AuditOutcome.Success)));
        // Another user (even an administrator) cannot use someone else's attached disk.
        var other = await UserAsync("fr-other", BuiltInRoles.PlatformAdmin);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/backup/mounts/{mountId}/files?path={Uri.EscapeDataString(@"F:\")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostJsonAsync($"/api/v1/backup/mounts/{mountId}/dismount")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/backup/mounts/{mountId}/files")).StatusCode);

        // Attaching the disk on ANOTHER host needs write access to the files of that host (a backup operator has none).
        var (otherHost, _) = await _app.CreateHostAsync("fr-other-host");
        var operatorUser = await UserAsync("fr-operator", BuiltInRoles.BackupOperator);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorUser.PostJsonAsync($"/api/v1/backup/points/{point.Id}/mount", new { disk, hostId = otherHost })).StatusCode);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "backup.files.mount" && a.Outcome == AuditOutcome.Denied)));
        // The list of the user's attached disks is really returned (not an empty 200).
        var listed = await operatorUser.GetAsync("/api/v1/backup/mounts");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(JsonValueKind.Array, (await listed.JsonAsync()).ValueKind);
    }

    [Fact]
    public async Task Api_tokens_read_backup_status_only_and_are_shown_once_hashed_and_revocable()
    {
        var (_, vms) = await _app.CreateHostAsync("tok-host", ("web01", VmPowerState.Running));
        var admin = await UserAsync("tok-admin", BuiltInRoles.PlatformAdmin);
        var repoId = await RepositoryAsync(admin);
        await admin.PostJsonAsync("/api/v1/backup/jobs", Job("Nightly", repoId, [vms["web01"]]));
        await admin.PostJsonAsync($"/api/v1/vms/{vms["web01"]}/backup", new { repositoryId = repoId });
        await _app.RunJobsAsync();

        // Creating a token needs a recent authentication (the sign-in counts; later a password re-entry).
        await admin.ReauthAsync();
        Assert.Equal("invalid_scope", await (await admin.PostJsonAsync("/api/v1/api-tokens", new { name = "PRTG", scopes = new[] { "vm.power" } })).CodeAsync());
        var created = await (await admin.PostJsonAsync("/api/v1/api-tokens", new { name = "PRTG", expiresInDays = 30 })).JsonAsync();
        var token = created.GetProperty("token").GetString()!;
        Assert.StartsWith("hvp_", token, StringComparison.Ordinal);
        Assert.DoesNotContain(token, await (await admin.GetAsync("/api/v1/api-tokens")).ReadAsStringAsync2(), StringComparison.Ordinal);
        Assert.False(await _app.WithDbAsync(db => db.ApiTokens.AnyAsync(t => t.TokenHash == token)));

        var monitor = _app.CreateBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await monitor.GetAsync("/api/v1/public/backup/status")).StatusCode);
        monitor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var status = await (await monitor.GetAsync("/api/v1/public/backup/status")).JsonAsync();
        Assert.Equal("ok", status.GetProperty("status").GetString());
        Assert.Equal("web01", Assert.Single(status.GetProperty("vms").EnumerateArray()).GetProperty("vm").GetString());
        Assert.DoesNotContain(@"\\nas01", status.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await monitor.GetAsync("/api/v1/public/backup/runs")).StatusCode);
        // The token opens nothing else.
        Assert.Equal(HttpStatusCode.Unauthorized, (await monitor.GetAsync("/api/v1/backup/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await monitor.GetAsync("/api/v1/vms")).StatusCode);

        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostJsonAsync($"/api/v1/api-tokens/{id}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await monitor.GetAsync("/api/v1/public/backup/status")).StatusCode);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "api_tokens.use" && a.Outcome == AuditOutcome.Denied)));
        var reader = await UserAsync("tok-reader", BuiltInRoles.ReadOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/api-tokens")).StatusCode);
    }

    [Fact]
    public async Task Notification_settings_are_validated_and_their_secrets_are_write_only()
    {
        var admin = await UserAsync("not-admin", BuiltInRoles.PlatformAdmin);
        var reader = await UserAsync("not-reader", BuiltInRoles.ReadOnly);
        var body = new
        {
            email = new { enabled = true, host = "smtp.contoso.local", port = 587, security = "starttls", userName = "hvportal", password = "Smtp-Secret-1", from = "hvportal@contoso.local", to = new[] { "ops@contoso.local" } },
            webhooks = new[] { new { name = "Teams", url = "https://contoso.webhook.office.com/webhookb2/very-secret-path", format = "teams" } },
            restoreTestFailed = true, agentsOutdated = true,
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(reader, "/api/v1/settings/notifications", body)).StatusCode);
        Assert.Equal("invalid_webhook_url", await (await PutAsync(admin, "/api/v1/settings/notifications", body with { webhooks = new[] { new { name = "Plain", url = "http://x.local/hook", format = "generic" } } })).CodeAsync());
        Assert.Equal("invalid_mail_address", await (await PutAsync(admin, "/api/v1/settings/notifications",
            body with { email = body.email with { to = new[] { "not an address" } } })).CodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, "/api/v1/settings/notifications", body)).StatusCode);
        var read = await (await admin.GetAsync("/api/v1/settings/notifications")).ReadAsStringAsync2();
        Assert.DoesNotContain("Smtp-Secret-1", read, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret-path", read, StringComparison.Ordinal);
        Assert.Contains("\"hasPassword\":true", read, StringComparison.Ordinal);
        var stored = await _app.WithDbAsync(db => db.Settings.Where(s => s.Key == "notifications").Select(s => s.Value).FirstAsync());
        Assert.DoesNotContain("Smtp-Secret-1", stored, StringComparison.Ordinal);
        // The webhook address carries its secret: it is sealed in the database too, and shown back masked.
        Assert.DoesNotContain("very-secret-path", stored, StringComparison.Ordinal);

        // The masked address sent back with the same name keeps the stored one; under another name it is refused.
        var masked = (await (await admin.GetAsync("/api/v1/settings/notifications")).JsonAsync()).GetProperty("webhooks")[0].GetProperty("url").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, "/api/v1/settings/notifications", body with { webhooks = new[] { new { name = "Teams", url = masked, format = "teams" } } })).StatusCode);
        Assert.Equal("webhook_url_masked", await (await PutAsync(admin, "/api/v1/settings/notifications",
            body with { webhooks = new[] { new { name = "Renamed", url = masked, format = "teams" } } })).CodeAsync());
        // No webhook to the gateway itself or to link-local (cloud metadata) addresses.
        foreach (var local in new[] { "https://localhost/hook", "https://127.0.0.1/hook", "https://169.254.169.254/latest", "https://[::1]/hook" })
        {
            Assert.Equal("invalid_webhook_url", await (await PutAsync(admin, "/api/v1/settings/notifications",
                body with { webhooks = new[] { new { name = "Local", url = local, format = "generic" } } })).CodeAsync());
        }
        // A password is never sent over SMTP without TLS.
        Assert.Equal("smtp_credentials_need_tls", await (await PutAsync(admin, "/api/v1/settings/notifications",
            body with { email = body.email with { security = "none" } })).CodeAsync());
    }
}

internal static class ResponseExtensions
{
    public static Task<string> ReadAsStringAsync2(this HttpResponseMessage r) => r.Content.ReadAsStringAsync();
}
