using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>
/// VM templates (ADR-028): created from a VM that is off, used to create VMs (resources chosen at creation), deleted
/// with their files; who may do what; limits of the folders; what the template shows when its job fails or is lost.
/// </summary>
public sealed class TemplateTests : IAsyncLifetime
{
    private const string Library = @"D:\Templates";
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

    private async Task<HttpClient> LoginAsync(string user)
    {
        var c = _app.CreateBrowser();
        await c.LoginAsync(user);
        return c;
    }

    private async Task<HttpClient> AdminAsync(string user)
    {
        await _app.CreateUserAsync(user, BuiltInRoles.PlatformAdmin);
        return await LoginAsync(user);
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient c, string url, object body)
    {
        await c.CsrfAsync();
        return await c.PutAsJsonAsync(url, body, GatewayFactory.Json);
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient c, string url, object body)
    {
        await c.CsrfAsync();
        return await c.PatchAsJsonAsync(url, body, GatewayFactory.Json);
    }

    private async Task<Job> JobAsync(HttpResponseMessage accepted)
    {
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await accepted.JsonAsync()).GetProperty("jobId").GetGuid();
        await _app.RunJobsAsync();
        return await _app.WithDbAsync(db => db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id));
    }

    private Task<VmTemplate?> TemplateAsync(string name) => _app.WithDbAsync(db => db.VmTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Name == name));

    private Task<int> AuditCountAsync(string action, AuditOutcome outcome) =>
        _app.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == action && a.Outcome == outcome));

    /// <summary>A host with a template library and a ready template "Golden" made from the VM "golden" (64 GB boot disk).</summary>
    private async Task<(Guid HostId, Dictionary<string, Guid> Vms, HttpClient Admin, VmTemplate Template)> WithTemplateAsync(string host, string admin, int dataDiskGb = 0)
    {
        var (hostId, vms) = await _app.CreateHostAsync(host, ("golden", VmPowerState.Off), ("busy", VmPowerState.Running));
        var c = await AdminAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(c, $"/api/v1/hosts/{hostId}/template-library", new { path = Library })).StatusCode);
        _app.Agent.TemplateDataDiskGb = dataDiskGb;
        var job = await JobAsync(await c.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Golden", description = "Windows Server, patched" }));
        _app.Agent.TemplateDataDiskGb = 0;
        Assert.Equal(JobStatus.Succeeded, job.Status);
        return (hostId, vms, c, (await TemplateAsync("Golden"))!);
    }

    [Fact]
    public async Task A_template_is_made_from_a_vm_that_is_off_and_new_vms_get_its_disks_with_the_resources_asked()
    {
        var (hostId, vms) = await _app.CreateHostAsync("tpl-host", ("golden", VmPowerState.Off), ("busy", VmPowerState.Running));
        var c = await AdminAsync("tpl-admin");

        // The host needs a template library folder first; system folders and network paths are refused.
        Assert.Equal("template_library_not_set", await (await c.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Golden" })).CodeAsync());
        Assert.Equal("invalid_template_library", await (await PutAsync(c, $"/api/v1/hosts/{hostId}/template-library", new { path = @"C:\Windows\Templates" })).CodeAsync());
        Assert.Equal("invalid_template_library", await (await PutAsync(c, $"/api/v1/hosts/{hostId}/template-library", new { path = @"\\nas\templates" })).CodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(c, $"/api/v1/hosts/{hostId}/template-library", new { path = Library })).StatusCode);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "hosts.template_library.update")));

        // Only a VM that is off; a name is required.
        Assert.Equal("invalid_state", await (await c.PostJsonAsync($"/api/v1/vms/{vms["busy"]}/template", new { name = "Busy" })).CodeAsync());
        Assert.Equal("invalid_template_name", await (await c.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = " " })).CodeAsync());
        Assert.Empty(_app.Agent.OperationsReceived(HvOperation.NewTemplate));

        var accepted = await c.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Golden", description = "Windows Server, patched" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var creating = (await TemplateAsync("Golden"))!;
        Assert.Equal(TemplateStatus.Creating, creating.Status);
        Assert.Equal("template_name_exists", await (await c.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Golden" })).CodeAsync());
        var captured = await JobAsync(accepted);
        Assert.Equal(JobStatus.Succeeded, captured.Status);
        Assert.Equal("template.capture", captured.Type);

        // The agent was told to copy into <library>\<template id>; the VM itself is untouched.
        var sent = HvOperations.FromElement<NewTemplateRequest>(Assert.Single(_app.Agent.OperationsReceived(HvOperation.NewTemplate)).OperationArgs)!;
        Assert.Equal($@"{Library}\{creating.Id:D}", sent.Folder);
        Assert.Equal([Library], sent.Roots);
        Assert.Equal(await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["golden"]).Select(v => v.HyperVId).FirstAsync()), sent.VmId);
        var template = (await TemplateAsync("Golden"))!;
        Assert.Equal(TemplateStatus.Ready, template.Status);
        Assert.Equal(2, template.Generation);
        Assert.Equal("golden", template.SourceVmName);
        Assert.True(template.SizeBytes > 0);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "template.capture.request")));
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "template.capture.create" && a.Outcome == AuditOutcome.Success)));

        var listed = Assert.Single((await (await c.GetAsync("/api/v1/templates")).JsonAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal("Ready", listed.GetProperty("status").GetString());
        Assert.Equal(64, listed.GetProperty("bootDiskGb").GetInt32());
        Assert.True(listed.GetProperty("can").GetProperty("deploy").GetBoolean());
        Assert.Equal(template.FolderPath, listed.GetProperty("folderPath").GetString());

        // A VM from the template: name and resources are the caller's; disks, their location and the generation are
        // the template's, whatever the request says.
        var created = await JobAsync(await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new
        {
            vm = new
            {
                name = "web01", templateId = template.Id, processorCount = 4, memoryStartupMb = 8192, switchName = "vSwitch", diskSizeGb = 0, generation = 1,
                templateDisks = new[] { @"D:\Elsewhere\other.vhdx" }, templateRoots = new[] { @"D:\Elsewhere" },
            },
        }));
        Assert.Equal(JobStatus.Succeeded, created.Status);
        Assert.Equal("FromTemplate", created.Operation);
        var request = Assert.Single(_app.Agent.CreatedFromTemplate.Values);
        Assert.Equal([$@"{template.FolderPath}\disk0.vhdx"], request.TemplateDisks);
        Assert.Equal([Library], request.TemplateRoots);
        Assert.Equal(2, request.Generation);
        Assert.Equal(4, request.ProcessorCount);
        Assert.Equal(8192, request.MemoryStartupMb);
        Assert.Equal("vSwitch", request.SwitchName);
        Assert.Equal(64, request.DiskSizeGb);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "vm.create.fromtemplate" && a.Outcome == AuditOutcome.Success)));

        // A larger boot disk can be asked for, a smaller one cannot.
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "web02", templateId = template.Id, diskSizeGb = 100 } }))).Status);
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "web03", templateId = template.Id, diskSizeGb = 10 } }))).Status);
        Assert.Equal([64, 64, 100], _app.Agent.CreatedFromTemplate.Values.Select(r => r.DiskSizeGb).Order());

        // A creation without a template never reaches the agent with template disks.
        var plain = await JobAsync(await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new
        {
            vm = new { name = "plain01", templateDisks = new[] { $@"{template.FolderPath}\disk0.vhdx" }, templateRoots = new[] { Library }, templateExtraDiskGb = 5 },
        }));
        Assert.Equal(JobStatus.Succeeded, plain.Status);
        Assert.Equal("New", plain.Operation);
        Assert.Equal(3, _app.Agent.CreatedFromTemplate.Count);
        Assert.All(_app.Agent.OperationsReceived(HvOperation.NewVm).Select(o => HvOperations.FromElement<NewVmRequest>(o.OperationArgs)!).Where(r => r.Name == "plain01"),
            r => Assert.True(r.TemplateDisks.Count == 0 && r.TemplateRoots.Count == 0 && r.TemplateExtraDiskGb == 0));

        // Rename, then delete with the typed name: the files go with it.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(c, $"/api/v1/templates/{template.Id}", new { name = "Golden 2022", description = "" })).StatusCode);
        Assert.Equal("Golden 2022", (await _app.WithDbAsync(db => db.VmTemplates.AsNoTracking().FirstAsync(t => t.Id == template.Id))).Name);
        Assert.Equal("confirmation_mismatch", await (await c.PostJsonAsync($"/api/v1/templates/{template.Id}/delete", new { confirmName = "Golden" })).CodeAsync());
        Assert.True(_app.Agent.TemplateFoldersOf(hostId).ContainsKey(template.FolderPath));
        var deleted = await JobAsync(await c.PostJsonAsync($"/api/v1/templates/{template.Id}/delete", new { confirmName = "Golden 2022" }));
        Assert.Equal(JobStatus.Succeeded, deleted.Status);
        Assert.False(_app.Agent.TemplateFoldersOf(hostId).ContainsKey(template.FolderPath));
        Assert.Equal(0, await _app.WithDbAsync(db => db.VmTemplates.CountAsync()));
        Assert.Equal("unknown_template", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "web04", templateId = template.Id } })).CodeAsync());
    }

    /// <summary>Library folders (templates, ISO) are text typed by an administrator: the rule answers at once and keeps its meaning.</summary>
    [Fact]
    public void Library_folder_rules_answer_at_once_and_refuse_system_and_network_paths()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(HvPortal.Gateway.Jobs.OperationPolicy.IsValidLibraryPath(@"D:\" + new string('a', 200) + ":"));
        Assert.False(HvPortal.Gateway.Templates.TemplateRules.IsValidLibraryPath(@"D:\" + new string('a', 180) + "|"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        foreach (var ok in new[] { @"D:\Templates", @"D:\Hyper-V\Templates\", @"E:\t" })
        {
            Assert.True(HvPortal.Gateway.Templates.TemplateRules.IsValidLibraryPath(ok), ok);
        }
        foreach (var bad in new[] { @"C:\Windows\Templates", @"C:\Users\Public", @"\\nas\templates", @"D:\Templates\..\Windows", "D:", @"D:\\x", "Templates", null })
        {
            Assert.False(HvPortal.Gateway.Templates.TemplateRules.IsValidLibraryPath(bad), bad ?? "null");
        }
    }

    [Fact]
    public async Task Templates_need_their_permission_to_be_managed_and_vm_create_to_be_used()
    {
        var (hostId, vms, admin, template) = await WithTemplateAsync("perm-host", "perm-admin");
        var (otherHost, _) = await _app.CreateHostAsync("perm-other");
        await _app.CreateUserAsync("perm-operator", BuiltInRoles.VmOperator);
        // Creates VMs on the host (role held on the host) but does not manage templates: FolderAdmin has vm.create, not templates.manage.
        await _app.CreateUserAsync("perm-creator", BuiltInRoles.FolderAdmin, ScopeType.Host, hostId.ToString("D"));
        await _app.CreateUserAsync("perm-elsewhere", BuiltInRoles.InfrastructureAdmin, ScopeType.Host, otherHost.ToString("D"));

        // An operator sees no template and cannot make, change or delete one, nor choose the library folder.
        var operatorClient = await LoginAsync("perm-operator");
        Assert.Empty((await (await operatorClient.GetAsync("/api/v1/templates")).JsonAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Mine" })).StatusCode);
        Assert.Equal(1, await AuditCountAsync("template.capture.request", AuditOutcome.Denied));
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(operatorClient, $"/api/v1/templates/{template.Id}", new { name = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await operatorClient.PostJsonAsync($"/api/v1/templates/{template.Id}/delete", new { confirmName = "Golden" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(operatorClient, $"/api/v1/hosts/{hostId}/template-library", new { path = @"D:\Other" })).StatusCode);
        Assert.Equal(1, await AuditCountAsync("hosts.template_library.update", AuditOutcome.Denied));
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "nope", templateId = template.Id } })).StatusCode);

        // An administrator of another host does not see it either.
        var elsewhere = await LoginAsync("perm-elsewhere");
        Assert.Empty((await (await elsewhere.GetAsync("/api/v1/templates")).JsonAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await elsewhere.PostJsonAsync($"/api/v1/templates/{template.Id}/delete", new { confirmName = "Golden" })).StatusCode);

        // Who creates VMs on the host uses the template but cannot change or delete it, and is not told where its files are.
        var creator = await LoginAsync("perm-creator");
        var seen = Assert.Single((await (await creator.GetAsync($"/api/v1/templates?hostId={hostId}")).JsonAsync()).GetProperty("items").EnumerateArray());
        Assert.True(seen.GetProperty("can").GetProperty("deploy").GetBoolean());
        Assert.False(seen.GetProperty("can").GetProperty("manage").GetBoolean());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("folderPath").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(creator, $"/api/v1/templates/{template.Id}", new { name = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await creator.PostJsonAsync($"/api/v1/templates/{template.Id}/delete", new { confirmName = "Golden" })).StatusCode);
        Assert.Equal(1, await AuditCountAsync("template.delete.request", AuditOutcome.Denied));
        Assert.Equal(1, await AuditCountAsync("templates.update", AuditOutcome.Denied));
        Assert.Equal(HttpStatusCode.Forbidden, (await creator.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Copy" })).StatusCode);
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(await creator.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "from-creator", templateId = template.Id } }))).Status);
        Assert.Equal("Golden", (await _app.WithDbAsync(db => db.VmTemplates.AsNoTracking().FirstAsync(t => t.Id == template.Id))).Name);
        Assert.True(_app.Agent.TemplateFoldersOf(hostId).ContainsKey(template.FolderPath));

        // A template of one host cannot be used on another.
        Assert.Equal("unknown_template", await (await admin.PostJsonAsync($"/api/v1/hosts/{otherHost}/vms", new { vm = new { name = "wrong-host", templateId = template.Id } })).CodeAsync());

        // Agents older than 0.6.0 never receive template commands.
        _app.Agent.ProtocolVersions[hostId] = 7;
        Assert.Equal("agent_outdated", await (await admin.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Old agent" })).CodeAsync());
        Assert.Equal("agent_outdated", await (await admin.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "old-agent", templateId = template.Id } })).CodeAsync());
        Assert.Equal("agent_outdated", await (await admin.PostJsonAsync($"/api/v1/templates/{template.Id}/delete", new { confirmName = "Golden" })).CodeAsync());
        Assert.Single(_app.Agent.OperationsReceived(HvOperation.NewTemplate));
        Assert.Empty(_app.Agent.OperationsReceived(HvOperation.RemoveTemplate));
    }

    [Fact]
    public async Task Inside_a_folder_every_disk_of_the_template_counts_against_the_limits()
    {
        // Template with a 64 GB boot disk and a 40 GB data disk.
        var (hostId, _, admin, template) = await WithTemplateAsync("quota-host", "quota-admin", dataDiskGb: 40);
        var created = await admin.PostJsonAsync("/api/v1/groups", new { name = "Tenant T" });
        var folder = (await created.JsonAsync()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, $"/api/v1/groups/{folder}/policy", new { maxDiskGb = 100, hosts = new[] { hostId } })).StatusCode);
        await _app.CreateUserAsync("quota-tenant", BuiltInRoles.FolderAdmin, ScopeType.Group, folder.ToString("D"));
        var tenant = await LoginAsync("quota-tenant");

        // The folder opens the host to its users: they see its templates when they say which folder they work in.
        Assert.Empty((await (await tenant.GetAsync("/api/v1/templates")).JsonAsync()).GetProperty("items").EnumerateArray());
        var seen = Assert.Single((await (await tenant.GetAsync($"/api/v1/templates?groupId={folder}")).JsonAsync()).GetProperty("items").EnumerateArray());
        Assert.True(seen.GetProperty("can").GetProperty("deploy").GetBoolean());

        // 64 + 40 GB do not fit in 100 GB.
        var refused = await tenant.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { groupId = folder, vm = new { name = "t-vm1", templateId = template.Id } });
        Assert.Equal("quota_exceeded", await refused.CodeAsync());
        Assert.Empty(_app.Agent.CreatedFromTemplate);

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, $"/api/v1/groups/{folder}/policy", new { maxDiskGb = 150, hosts = new[] { hostId } })).StatusCode);
        var ok = await JobAsync(await tenant.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { groupId = folder, vm = new { name = "t-vm1", templateId = template.Id } }));
        Assert.Equal(JobStatus.Succeeded, ok.Status);
        var request = Assert.Single(_app.Agent.CreatedFromTemplate.Values);
        Assert.Equal(2, request.TemplateDisks.Count);
        Assert.Equal(40, request.TemplateExtraDiskGb);
        // The first VM uses 104 GB: a second one does not fit in 150 GB.
        Assert.Equal("quota_exceeded", await (await tenant.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { groupId = folder, vm = new { name = "t-vm2", templateId = template.Id } })).CodeAsync());
        // Outside the folder the tenant has no right on the host.
        Assert.Equal(HttpStatusCode.Forbidden, (await tenant.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "t-vm3", templateId = template.Id } })).StatusCode);
    }

    [Fact]
    public async Task A_template_is_never_shown_as_ready_when_its_job_failed_or_its_outcome_is_unknown()
    {
        var (hostId, vms) = await _app.CreateHostAsync("fail-host", ("golden", VmPowerState.Off));
        var c = await AdminAsync("fail-admin");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(c, $"/api/v1/hosts/{hostId}/template-library", new { path = Library })).StatusCode);

        // The copy fails on the host: the template stays, marked failed, and cannot be used; deleting it cleans up.
        _app.Agent.OperationHandler = (_, cmd) => cmd.Operation == HvOperation.NewTemplate
            ? new HvOperationOutcome { Ok = false, Error = "Not enough free space in the template library." }
            : null!;
        var failedJob = await JobAsync(await c.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Broken" }));
        Assert.Equal(JobStatus.Failed, failedJob.Status);
        var broken = (await TemplateAsync("Broken"))!;
        Assert.Equal(TemplateStatus.Failed, broken.Status);
        Assert.Contains("free space", broken.Error);
        Assert.Equal("template_not_ready", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "x1", templateId = broken.Id } })).CodeAsync());
        _app.Agent.OperationHandler = null;
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(await c.PostJsonAsync($"/api/v1/templates/{broken.Id}/delete", new { confirmName = "Broken" }))).Status);
        Assert.Null(await TemplateAsync("Broken"));

        // The result of the copy is lost (channel dropped): unknown is not success.
        _app.Agent.OperationHandler = (host, cmd) => cmd.Operation == HvOperation.NewTemplate ? throw new AgentChannelLostException(host) : null!;
        var lost = await JobAsync(await c.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Lost" }));
        Assert.Equal(JobStatus.Reconciling, lost.Status);
        Assert.Equal(TemplateStatus.Creating, (await TemplateAsync("Lost"))!.Status);
        await _app.Runner.ReconcileAsync(CancellationToken.None);
        var unverified = (await TemplateAsync("Lost"))!;
        Assert.Equal(TemplateStatus.Unverified, unverified.Status);
        Assert.Equal("template_not_ready", await (await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "x2", templateId = unverified.Id } })).CodeAsync());
        var listed = Assert.Single((await (await c.GetAsync("/api/v1/templates")).JsonAsync()).GetProperty("items").EnumerateArray());
        Assert.Equal("Unverified", listed.GetProperty("status").GetString());
        Assert.False(listed.GetProperty("can").GetProperty("deploy").GetBoolean());
        _app.Agent.OperationHandler = null;

        // The requester loses the permission before the job runs: nothing reaches the host and no template remains.
        await _app.CreateUserAsync("fail-temp", BuiltInRoles.InfrastructureAdmin);
        var temp = await LoginAsync("fail-temp");
        var queued = await temp.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Revoked" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        await _app.WithDbAsync(async db =>
        {
            var userId = await db.Users.Where(u => u.UserName == "fail-temp").Select(u => u.Id).FirstAsync();
            db.RoleAssignments.RemoveRange(db.RoleAssignments.Where(a => a.PrincipalId == userId.ToString()));
            await db.SaveChangesAsync();
            return 0;
        });
        var received = _app.Agent.OperationsReceived(HvOperation.NewTemplate).Count;
        var revoked = await JobAsync(queued);
        Assert.Equal(JobStatus.Failed, revoked.Status);
        Assert.Equal("authorization_revoked", revoked.ErrorCode);
        Assert.Equal(received, _app.Agent.OperationsReceived(HvOperation.NewTemplate).Count);
        Assert.Null(await TemplateAsync("Revoked"));

        // A template that disappears while a creation from it is queued: the job fails, no empty VM is created.
        var good = await JobAsync(await c.PostJsonAsync($"/api/v1/vms/{vms["golden"]}/template", new { name = "Good" }));
        Assert.Equal(JobStatus.Succeeded, good.Status);
        var goodTemplate = (await TemplateAsync("Good"))!;
        var pending = await c.PostJsonAsync($"/api/v1/hosts/{hostId}/vms", new { vm = new { name = "late", templateId = goodTemplate.Id } });
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        await _app.WithDbAsync(async db =>
        {
            var t = await db.VmTemplates.FirstAsync(x => x.Id == goodTemplate.Id);
            t.Status = TemplateStatus.Unverified;
            await db.SaveChangesAsync();
            return 0;
        });
        var late = await JobAsync(pending);
        Assert.Equal(JobStatus.Failed, late.Status);
        Assert.Equal("template_not_available", late.ErrorCode);
        Assert.DoesNotContain(_app.Agent.OperationsReceived(HvOperation.NewVm), o => HvOperations.FromElement<NewVmRequest>(o.OperationArgs)!.Name == "late");
    }
}
