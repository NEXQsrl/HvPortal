using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Data;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>Host file explorer: permissions, folder rules, chunked download/upload with SHA-256, audit, protocol gate.</summary>
public sealed class HostFileTests : IAsyncLifetime
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

    private async Task<HttpClient> UserAsync(string name, Guid hostId, params string[] permissions)
    {
        var roleId = await _app.WithDbAsync(async db =>
        {
            var role = new Role { Name = "files-" + name };
            foreach (var p in permissions.Append(Permissions.InventoryRead))
            {
                role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
            }
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            return role.Id;
        });
        var user = await _app.CreateUserAsync(name);
        await _app.WithDbAsync(async db =>
        {
            db.RoleAssignments.Add(new RoleAssignment { PrincipalId = user.ToString("D"), RoleId = roleId, ScopeType = ScopeType.Host, ScopeId = hostId.ToString("D"), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        var c = _app.CreateBrowser();
        await c.LoginAsync(name);
        return c;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, Guid hostId, string folder, string name, byte[] data, bool overwrite = false)
    {
        await c.CsrfAsync();
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await c.PostAsync($"/api/v1/hosts/{hostId}/files/upload?folder={Uri.EscapeDataString(folder)}&name={Uri.EscapeDataString(name)}&overwrite={overwrite.ToString().ToLowerInvariant()}", content);
    }

    [Fact]
    public async Task Browse_and_download_need_host_files_read_and_respect_the_folder_rules()
    {
        var (hostId, _) = await _app.CreateHostAsync("files-host");
        var iso = RandomNumberGenerator.GetBytes(9 * 1024 * 1024 + 17); // 3 chunks
        _app.Agent.Files[@"D:\ISO\win.iso"] = iso;
        _app.Agent.Files[@"D:\Virtual Machines\db.vhdx"] = new byte[10];
        _app.Agent.FilesInUse.Add(@"D:\Virtual Machines\db.vhdx");

        var none = await UserAsync("files-none", hostId);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync($"/api/v1/hosts/{hostId}/files?path={Uri.EscapeDataString(@"D:\ISO")}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync($"/api/v1/hosts/{hostId}/files/download?path={Uri.EscapeDataString(@"D:\ISO\win.iso")}")).StatusCode);

        var reader = await UserAsync("files-reader", hostId, Permissions.HostFilesRead);
        var drives = await (await reader.GetAsync($"/api/v1/hosts/{hostId}/files")).JsonAsync();
        Assert.Contains(drives.GetProperty("drives").EnumerateArray(), d => d.GetProperty("path").GetString() == @"D:\");
        var listing = await (await reader.GetAsync($"/api/v1/hosts/{hostId}/files?path={Uri.EscapeDataString(@"D:\ISO")}")).JsonAsync();
        Assert.Equal("win.iso", listing.GetProperty("files")[0].GetProperty("name").GetString());
        Assert.Equal("location_not_allowed", await (await reader.GetAsync($"/api/v1/hosts/{hostId}/files?path={Uri.EscapeDataString(@"C:\Windows")}")).CodeAsync());

        var r = await reader.GetAsync($"/api/v1/hosts/{hostId}/files/download?path={Uri.EscapeDataString(@"D:\ISO\win.iso")}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("win.iso", r.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(iso, await r.Content.ReadAsByteArrayAsync());
        Assert.Equal(3, _app.Agent.OperationsReceived(Contracts.HyperV.HvOperation.ReadFile).Count);
        var sha = Convert.ToHexString(SHA256.HashData(iso)).ToLowerInvariant();
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "host.files.download" && a.DetailsJson!.Contains(sha))));

        // A disk open for writing (running VM) cannot be downloaded: no inconsistent copies.
        Assert.Equal("file_in_use", await (await reader.GetAsync($"/api/v1/hosts/{hostId}/files/download?path={Uri.EscapeDataString(@"D:\Virtual Machines\db.vhdx")}")).CodeAsync());
        Assert.Equal("location_not_allowed", await (await reader.GetAsync($"/api/v1/hosts/{hostId}/files/download?path={Uri.EscapeDataString(@"C:\Windows\System32\config\SAM")}")).CodeAsync());
        Assert.Equal("invalid_path", await (await reader.GetAsync($"/api/v1/hosts/{hostId}/files/download?path={Uri.EscapeDataString(@"D:\ISO\..\x")}")).CodeAsync());
    }

    [Fact]
    public async Task Upload_is_chunked_verified_and_never_overwrites_without_consent()
    {
        var (hostId, _) = await _app.CreateHostAsync("upload-host");
        var reader = await UserAsync("up-reader", hostId, Permissions.HostFilesRead);
        var data = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 3);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(reader, hostId, @"D:\ISO", "new.iso", data)).StatusCode);

        var writer = await UserAsync("up-writer", hostId, Permissions.HostFilesRead, Permissions.HostFilesWrite);
        var ok = await UploadAsync(writer, hostId, @"D:\ISO", "new.iso", data);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), (await ok.JsonAsync()).GetProperty("sha256").GetString());
        Assert.Equal(data, _app.Agent.Files[@"D:\ISO\new.iso"]);
        Assert.Equal(2, _app.Agent.OperationsReceived(Contracts.HyperV.HvOperation.WriteFileChunk).Count);

        Assert.Equal("file_exists", await (await UploadAsync(writer, hostId, @"D:\ISO", "new.iso", [1, 2, 3])).CodeAsync());
        Assert.Equal(data, _app.Agent.Files[@"D:\ISO\new.iso"]);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(writer, hostId, @"D:\ISO", "new.iso", [1, 2, 3], overwrite: true)).StatusCode);
        Assert.Equal([1, 2, 3], _app.Agent.Files[@"D:\ISO\new.iso"]);

        Assert.Equal("location_not_allowed", await (await UploadAsync(writer, hostId, @"C:\Windows\System32", "evil.dll", [1])).CodeAsync());
        Assert.Equal("location_not_allowed", await (await UploadAsync(writer, hostId, @"C:\ProgramData\Microsoft\Windows\Start Menu", "x.lnk", [1])).CodeAsync());
        Assert.Equal("invalid_file_name", await (await UploadAsync(writer, hostId, @"D:\ISO", "a:stream", [1])).CodeAsync());
        Assert.Equal("invalid_file_name", await (await UploadAsync(writer, hostId, @"D:\ISO", "..\\x.iso", [1])).CodeAsync());
        Assert.Equal("invalid_file_name", await (await UploadAsync(writer, hostId, @"D:\ISO", "CON.txt", [1])).CodeAsync());
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "host.files.upload" && a.Outcome == AuditOutcome.Success)));

        _app.Agent.ProtocolVersions[hostId] = 4;
        Assert.Equal("agent_outdated", await (await writer.GetAsync($"/api/v1/hosts/{hostId}/files?path={Uri.EscapeDataString(@"D:\ISO")}")).CodeAsync());
    }

    // ------------------------------------------------------------------ 0.9.4: rename and delete

    private static Task<HttpResponseMessage> RenameAsync(HttpClient c, Guid hostId, string path, string newName) =>
        c.PostJsonAsync($"/api/v1/hosts/{hostId}/files/rename", new { path, newName });

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient c, Guid hostId, string path, string? confirmName) =>
        c.PostJsonAsync($"/api/v1/hosts/{hostId}/files/delete", new { path, confirmName });

    private Task<int> AuditAsync(string action, AuditOutcome outcome) =>
        _app.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == action && a.Outcome == outcome));

    [Fact]
    public async Task Rename_and_delete_need_host_files_manage_an_explicit_confirmation_and_are_audited()
    {
        var (hostId, _) = await _app.CreateHostAsync("manage-host");
        _app.Agent.Files[@"D:\ISO\old.iso"] = [1, 2, 3];
        _app.Agent.Files[@"D:\ISO\keep.iso"] = [9];
        _app.Agent.Folders[@"D:\ISO"] = ["empty"];
        _app.Agent.Folders[@"D:\ISO\empty"] = [];

        // Upload rights are not enough: renaming and deleting is a permission of its own.
        var writer = await UserAsync("mg-writer", hostId, Permissions.HostFilesRead, Permissions.HostFilesWrite);
        Assert.Equal(HttpStatusCode.Forbidden, (await RenameAsync(writer, hostId, @"D:\ISO\old.iso", "new.iso")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteAsync(writer, hostId, @"D:\ISO\old.iso", "old.iso")).StatusCode);
        Assert.Equal(1, await AuditAsync("host.files.rename", AuditOutcome.Denied));
        Assert.Equal(1, await AuditAsync("host.files.delete", AuditOutcome.Denied));
        Assert.True(_app.Agent.Files.ContainsKey(@"D:\ISO\old.iso"));
        // Someone who cannot even see the host learns nothing about it.
        var (otherHost, _) = await _app.CreateHostAsync("manage-other");
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(writer, otherHost, @"D:\ISO\old.iso", "old.iso")).StatusCode);

        var manager = await UserAsync("mg-manager", hostId, Permissions.HostFilesRead, Permissions.HostFilesManage);
        // The host list tells the page who may rename and delete (the endpoints decide anyway).
        static async Task<bool> CanManageAsync(HttpClient c, Guid id) =>
            (await (await c.GetAsync("/api/v1/hosts")).JsonAsync()).EnumerateArray().Single(h => h.GetProperty("id").GetGuid() == id).GetProperty("canManageFiles").GetBoolean();
        Assert.False(await CanManageAsync(writer, hostId));
        Assert.True(await CanManageAsync(manager, hostId));
        var renamed = await RenameAsync(manager, hostId, @"D:\ISO\old.iso", "new.iso");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(@"D:\ISO\new.iso", (await renamed.JsonAsync()).GetProperty("newPath").GetString());
        Assert.Equal([1, 2, 3], _app.Agent.Files[@"D:\ISO\new.iso"]);
        Assert.False(_app.Agent.Files.ContainsKey(@"D:\ISO\old.iso"));
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "host.files.rename" && a.Outcome == AuditOutcome.Success && a.DetailsJson!.Contains("new.iso"))));

        // Never over an existing name; the failure is audited with its reason.
        Assert.Equal("file_exists", await (await RenameAsync(manager, hostId, @"D:\ISO\new.iso", "keep.iso")).CodeAsync());
        Assert.Equal([9], _app.Agent.Files[@"D:\ISO\keep.iso"]);
        Assert.True(await _app.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "host.files.rename" && a.Outcome == AuditOutcome.Failure && a.DetailsJson!.Contains("file_exists"))));
        Assert.Equal("same_name", await (await RenameAsync(manager, hostId, @"D:\ISO\new.iso", "new.iso")).CodeAsync());
        Assert.Equal("invalid_file_name", await (await RenameAsync(manager, hostId, @"D:\ISO\new.iso", @"..\x.iso")).CodeAsync());

        // Deleting needs the name again; without it nothing reaches the host.
        Assert.Equal("confirmation_required", await (await DeleteAsync(manager, hostId, @"D:\ISO\new.iso", null)).CodeAsync());
        Assert.Equal("confirmation_required", await (await DeleteAsync(manager, hostId, @"D:\ISO\new.iso", "keep.iso")).CodeAsync());
        Assert.Empty(_app.Agent.OperationsReceived(Contracts.HyperV.HvOperation.DeletePath));
        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(manager, hostId, @"D:\ISO\new.iso", "new.iso")).StatusCode);
        Assert.False(_app.Agent.Files.ContainsKey(@"D:\ISO\new.iso"));
        Assert.Equal(1, await AuditAsync("host.files.delete", AuditOutcome.Success));

        // Folders: only an empty one is deleted; a file in use is not.
        Assert.Equal("folder_not_empty", await (await DeleteAsync(manager, hostId, @"D:\ISO", "ISO")).CodeAsync());
        Assert.True(_app.Agent.Files.ContainsKey(@"D:\ISO\keep.iso"));
        var deletedFolder = await DeleteAsync(manager, hostId, @"D:\ISO\empty", "empty");
        Assert.Equal(HttpStatusCode.OK, deletedFolder.StatusCode);
        Assert.True((await deletedFolder.JsonAsync()).GetProperty("isFolder").GetBoolean());
        Assert.False(_app.Agent.Folders.ContainsKey(@"D:\ISO\empty"));
        _app.Agent.FilesInUse.Add(@"D:\ISO\keep.iso");
        Assert.Equal("file_in_use", await (await DeleteAsync(manager, hostId, @"D:\ISO\keep.iso", "keep.iso")).CodeAsync());
        Assert.Equal("not_found", await (await DeleteAsync(manager, hostId, @"D:\ISO\gone.iso", "gone.iso")).CodeAsync());

        // An agent before 0.9.4 is never asked.
        _app.Agent.ProtocolVersions[hostId] = 12;
        Assert.Equal("agent_outdated", await (await DeleteAsync(manager, hostId, @"D:\ISO\keep.iso", "keep.iso")).CodeAsync());
        Assert.Equal("agent_outdated", await (await RenameAsync(manager, hostId, @"D:\ISO\keep.iso", "k.iso")).CodeAsync());
    }

    [Fact]
    public async Task Hyper_v_files_system_folders_and_folders_the_portal_uses_are_never_renamed_or_deleted()
    {
        var (hostId, vms) = await _app.CreateHostAsync("protect-host", ("vm1", Contracts.HyperV.VmPowerState.Off));
        _app.Agent.Files[@"D:\Virtual Machines\vm1\Virtual Hard Disks\vm1.vhdx"] = new byte[10];
        _app.Agent.Files[@"D:\ISO\win.iso"] = [1];
        _app.Agent.Folders[@"D:\"] = ["Virtual Machines", "ISO", "Templates", "Staging"];
        _app.Agent.Folders[@"D:\Templates"] = [];
        _app.Agent.Folders[@"D:\Staging"] = [];
        _app.Agent.Folders[@"D:\Virtual Machines"] = ["vm1"];
        _app.Agent.Folders[@"D:\Virtual Machines\vm1"] = [];
        await _app.WithDbAsync(async db =>
        {
            var host = await db.Hosts.FirstAsync(h => h.Id == hostId);
            host.TemplateLibraryPath = @"D:\Templates";
            host.BackupStagingPath = @"D:\Staging\hvportal";
            host.IsoLibraryPathsJson = "[\"D:\\\\ISO\"]";
            var vm = await db.VirtualMachines.FirstAsync(v => v.Id == vms["vm1"]);
            vm.ConfigurationPath = @"D:\Virtual Machines\vm1";
            await db.SaveChangesAsync();
        });
        var manager = await UserAsync("pr-manager", hostId, Permissions.HostFilesRead, Permissions.HostFilesWrite, Permissions.HostFilesManage);

        // Virtual disks, checkpoints, VM configuration: refused by name, on the gateway, before the agent is asked.
        foreach (var name in new[] { "vm1.vhdx", "vm1_ABC.avhdx", "vm1.vmcx", "vm1.vmrs", "vm1.VHD" })
        {
            var path = @"D:\Virtual Machines\vm1\Virtual Hard Disks\" + name;
            Assert.Equal("protected_file", await (await DeleteAsync(manager, hostId, path, name)).CodeAsync());
            Assert.Equal("protected_file", await (await RenameAsync(manager, hostId, path, "x.bak")).CodeAsync());
        }
        Assert.Equal("protected_file", await (await RenameAsync(manager, hostId, @"D:\ISO\win.iso", "win.vhdx")).CodeAsync());
        // System folders, drives, traversal, streams.
        Assert.Equal("location_not_allowed", await (await DeleteAsync(manager, hostId, @"C:\Windows\System32\drivers\etc\hosts", "hosts")).CodeAsync());
        Assert.Equal("location_not_allowed", await (await RenameAsync(manager, hostId, @"C:\Program Files\HvPortal", "x")).CodeAsync());
        Assert.Equal("location_not_allowed", await (await DeleteAsync(manager, hostId, @"D:\", "D:")).CodeAsync());
        Assert.Equal("invalid_path", await (await DeleteAsync(manager, hostId, @"D:\ISO\..\Virtual Machines", "Virtual Machines")).CodeAsync());
        Assert.Equal("invalid_path", await (await DeleteAsync(manager, hostId, @"\\nas\share\x.iso", "x.iso")).CodeAsync());
        Assert.Equal("invalid_path", await (await DeleteAsync(manager, hostId, null!, null)).CodeAsync());

        // What the portal itself uses on the host, from its own records: the folder, or any folder that contains it.
        Assert.Equal("path_in_use", await (await RenameAsync(manager, hostId, @"D:\Templates", "Old templates")).CodeAsync());
        Assert.Equal("path_in_use", await (await DeleteAsync(manager, hostId, @"D:\Staging", "Staging")).CodeAsync());
        Assert.Equal("path_in_use", await (await RenameAsync(manager, hostId, @"D:\ISO", "Images")).CodeAsync());
        Assert.Equal("path_in_use", await (await RenameAsync(manager, hostId, @"D:\Virtual Machines", "VMs")).CodeAsync());
        Assert.Equal("path_in_use", await (await RenameAsync(manager, hostId, @"D:\Virtual Machines\vm1", "vm1-old")).CodeAsync());
        // An upload does not replace a Hyper-V file either (a new one, under a free name, is still accepted).
        _app.Agent.Folders[@"D:\Virtual Machines\vm1\Virtual Hard Disks"] = [];
        Assert.Equal("protected_file", await (await UploadAsync(manager, hostId, @"D:\Virtual Machines\vm1\Virtual Hard Disks", "vm1.vhdx", [7, 7], overwrite: true)).CodeAsync());
        Assert.Equal(10, _app.Agent.Files[@"D:\Virtual Machines\vm1\Virtual Hard Disks\vm1.vhdx"].Length);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(manager, hostId, @"D:\Virtual Machines\vm1\Virtual Hard Disks", "data.vhdx", [7, 7])).StatusCode);
        // A storage path given to a VM folder on this host is used by the portal too.
        await _app.WithDbAsync(async db =>
        {
            db.LogicalGroups.Add(new LogicalGroup { Name = "Tenant A", CreatedAt = DateTimeOffset.UtcNow,
                PolicyJson = System.Text.Json.JsonSerializer.Serialize(new { storage = new[] { new { hostId, path = @"D:\Tenants\A" } } }) });
            await db.SaveChangesAsync();
        });
        Assert.Equal("path_in_use", await (await RenameAsync(manager, hostId, @"D:\Tenants", "Customers")).CodeAsync());
        Assert.Equal("path_in_use", await (await DeleteAsync(manager, hostId, @"D:\Tenants\A", "A")).CodeAsync());
        // A file inside one of those folders is an ordinary file.
        Assert.Equal(HttpStatusCode.OK, (await RenameAsync(manager, hostId, @"D:\ISO\win.iso", "windows.iso")).StatusCode);

        Assert.Empty(_app.Agent.OperationsReceived(Contracts.HyperV.HvOperation.DeletePath));
        Assert.Single(_app.Agent.OperationsReceived(Contracts.HyperV.HvOperation.RenamePath));
        Assert.True(_app.Agent.Files.ContainsKey(@"D:\Virtual Machines\vm1\Virtual Hard Disks\vm1.vhdx"));
        Assert.True(await AuditAsync("host.files.delete", AuditOutcome.Denied) >= 7);
        Assert.True(await AuditAsync("host.files.rename", AuditOutcome.Denied) >= 11);
        // The built-in folder administrator may rename and delete inside the folder; read-only roles may not.
        Assert.Contains(Permissions.HostFilesManage, BuiltInRoles.All.Single(r => r.Key == BuiltInRoles.FolderAdmin).Permissions);
        Assert.DoesNotContain(Permissions.HostFilesManage, BuiltInRoles.All.Single(r => r.Key == BuiltInRoles.VmOperator).Permissions);
        Assert.Equal(StatusCodes.Status409Conflict, (HvPortal.Gateway.Api.HostFileEndpoints.MapError("folder_not_empty: x") as IStatusCodeHttpResult)?.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, (HvPortal.Gateway.Api.HostFileEndpoints.MapError("protected_content: x") as IStatusCodeHttpResult)?.StatusCode);
    }

    [Fact]
    public void Agent_error_codes_map_to_http_statuses()
    {
        Assert.Equal(StatusCodes.Status409Conflict, (HvPortal.Gateway.Api.HostFileEndpoints.MapError("file_exists: x") as IStatusCodeHttpResult)?.StatusCode);
        Assert.Equal(StatusCodes.Status507InsufficientStorage, (HvPortal.Gateway.Api.HostFileEndpoints.MapError("insufficient_space: x") as IStatusCodeHttpResult)?.StatusCode);
        Assert.Equal(StatusCodes.Status502BadGateway, (HvPortal.Gateway.Api.HostFileEndpoints.MapError("Something: else") as IStatusCodeHttpResult)?.StatusCode);
    }
}
