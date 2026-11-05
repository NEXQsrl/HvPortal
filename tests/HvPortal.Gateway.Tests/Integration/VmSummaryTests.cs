using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;
using HvPortal.Gateway.Authorization;
using HvPortal.Gateway.Security;
using HvPortal.Gateway.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HvPortal.Gateway.Tests.Integration;

/// <summary>VM summary details from the agent (0.3.4+): creation time, path, Hyper-V notes, replication, memory demand.</summary>
public sealed class VmSummaryTests : IAsyncLifetime
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

    private async Task IngestAsync(Guid hostId, VmSummary vm)
    {
        using var scope = _app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InventoryIngestor>().ApplyAsync(hostId, new HostInventory
        {
            CollectedAtUtc = DateTimeOffset.UtcNow,
            Access = HyperVAccessStatus.Ok,
            Host = new HostFacts { ComputerName = "H", AgentVersion = "0.3.4", VirtualSwitches = [] },
            Vms = [vm],
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Details_are_stored_and_demand_is_computed_for_dynamic_memory()
    {
        var (hostId, vms) = await _app.CreateHostAsync("sum-host", ("ca01", VmPowerState.Running));
        var hv = await _app.WithDbAsync(db => db.VirtualMachines.Where(v => v.Id == vms["ca01"]).Select(v => v.HyperVId).FirstAsync());
        var created = new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var summary = new VmSummary
        {
            VmId = hv, Name = "ca01", PowerState = VmPowerState.Running, DynamicMemoryEnabled = true, MemoryStartupMb = 8192, MemoryAssignedMb = 8192,
            MemoryAvailablePercent = 60, MemoryBufferPercent = 20, CreatedUtc = created, Notes = "Certification Authority\nDo not restart",
            ConfigurationPath = @"D:\Hyper-V\CA01", ReplicationMode = 0, ReplicationState = 0, ReplicationHealth = 0,
        };
        await IngestAsync(hostId, summary);
        await _app.CreateUserAsync("ro-sum", BuiltInRoles.ReadOnly);
        var c = _app.CreateBrowser();
        await c.LoginAsync("ro-sum");
        var vm = (await (await c.GetAsync($"/api/v1/vms/{vms["ca01"]}")).JsonAsync()).GetProperty("vm");
        Assert.Equal("Certification Authority\nDo not restart", vm.GetProperty("notes").GetString());
        Assert.Equal(@"D:\Hyper-V\CA01", vm.GetProperty("configurationPath").GetString());
        Assert.Equal(created, vm.GetProperty("hyperVCreatedAt").GetDateTimeOffset());
        Assert.Equal(3276, vm.GetProperty("memoryDemandMb").GetInt64()); // 8192 x 40 %
        Assert.Equal(0, vm.GetProperty("replicationHealth").GetInt32());

        // Notes changed in Hyper-V Manager: synchronized and recorded in the VM history.
        await IngestAsync(hostId, summary with { Notes = "Moved to new PKI" });
        Assert.True(await _app.WithDbAsync(db => db.InventoryEvents.AnyAsync(e => e.VmId == vms["ca01"] && e.Kind == "vm.notes.changed")));
        // An older agent (no notes, no path) does not erase what is known.
        await IngestAsync(hostId, summary with { Notes = null, ConfigurationPath = null, CreatedUtc = null });
        var stored = await _app.WithDbAsync(db => db.VirtualMachines.FirstAsync(v => v.Id == vms["ca01"]));
        Assert.Equal("Moved to new PKI", stored.HyperVNotes);
        Assert.Equal(@"D:\Hyper-V\CA01", stored.ConfigurationPath);
    }
}
