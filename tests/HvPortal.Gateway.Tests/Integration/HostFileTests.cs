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

    [Fact]
    public void Agent_error_codes_map_to_http_statuses()
    {
        Assert.Equal(StatusCodes.Status409Conflict, (HvPortal.Gateway.Api.HostFileEndpoints.MapError("file_exists: x") as IStatusCodeHttpResult)?.StatusCode);
        Assert.Equal(StatusCodes.Status507InsufficientStorage, (HvPortal.Gateway.Api.HostFileEndpoints.MapError("insufficient_space: x") as IStatusCodeHttpResult)?.StatusCode);
        Assert.Equal(StatusCodes.Status502BadGateway, (HvPortal.Gateway.Api.HostFileEndpoints.MapError("Something: else") as IStatusCodeHttpResult)?.StatusCode);
    }
}
