using System.Collections.Concurrent;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;
using HvPortal.Gateway.Agents;

namespace HvPortal.Gateway.Tests.Infrastructure;

/// <summary>
/// TEST DOUBLE. Simulates agents in memory so gateway behavior (authorization, jobs, reconciliation) can be tested
/// without Hyper-V. It lives only in the test project and is never registered in production.
/// </summary>
public sealed class FakeAgentDispatcher : IAgentDispatcher
{
    /// <summary>Hyper-V ids of VMs deleted with DeleteDisks (their disk files deleted too).</summary>
    public System.Collections.Concurrent.ConcurrentBag<Guid> DeletedWithDisks { get; } = [];

    private readonly ConcurrentDictionary<Guid, bool> _connected = new();
    private readonly ConcurrentDictionary<Guid, (Guid HostId, string Name, VmPowerState State, long UptimeMs)> _vms = new();

    public ConcurrentQueue<AgentCommand> Received { get; } = new();

    /// <summary>Size of the VMs created through NewVm (vCPU, memory, dynamic maximum, disk), reported by inventory and disk listings.</summary>
    private readonly ConcurrentDictionary<Guid, (int Processors, long MemoryMb, long? MaximumMb, bool Dynamic, long DiskGb)> _specs = new();

    /// <summary>Configuration folder of the VMs created through NewVm with a location (reported by inventory).</summary>
    private readonly ConcurrentDictionary<Guid, string> _paths = new();

    /// <summary>Virtual switches created through NewSwitch, per host (every host also has the external switch "vSwitch").</summary>
    public ConcurrentDictionary<Guid, List<SwitchInfo>> Switches { get; } = new();

    /// <summary>Disk size reported for a VM by ListHostDisks (default 64 GB).</summary>
    public void SetDiskGb(Guid hyperVId, long gb) =>
        _specs[hyperVId] = _specs.TryGetValue(hyperVId, out var s) ? s with { DiskGb = gb } : (2, 4096, null, false, gb);

    /// <summary>When set, a VmPower command is "sent" and then the channel drops (outcome unknown to the gateway).</summary>
    public bool DropChannelAfterPowerCommand { get; set; }

    /// <summary>When true, the dropped command was nonetheless executed on the host.</summary>
    public bool ExecuteBeforeDrop { get; set; }

    public void Connect(Guid hostId) => _connected[hostId] = true;
    public void DisconnectHost(Guid hostId) => _connected[hostId] = false;
    public void AddVm(Guid hostId, Guid hyperVId, string name, VmPowerState state) => _vms[hyperVId] = (hostId, name, state, 600_000);
    public VmPowerState StateOf(Guid hyperVId) => _vms[hyperVId].State;
    public void SetState(Guid hyperVId, VmPowerState state) => _vms[hyperVId] = _vms[hyperVId] with { State = state };
    public bool Exists(Guid hyperVId) => _vms.ContainsKey(hyperVId);
    public int PowerCommandsFor(Guid hyperVId) => Received.Count(c => c.Kind == AgentCommandKind.VmPower && c.VmId == hyperVId);

    public bool IsConnected(Guid hostId) => _connected.TryGetValue(hostId, out var c) && c;

    /// <summary>Simulated agent protocol per host (default: current). Mirrors the gateway guard of AgentConnectionManager.</summary>
    public ConcurrentDictionary<Guid, int> ProtocolVersions { get; } = new();

    public int ProtocolVersionOf(Guid hostId) =>
        !IsConnected(hostId) ? 0 : ProtocolVersions.TryGetValue(hostId, out var v) ? v : AgentProtocol.Version;

    /// <summary>Folder tree returned by BrowseFolders (path -> sub-folder names); drives are the keys of length 3.</summary>
    public Dictionary<string, string[]> Folders { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\"] = ["Hyper-V"],
        [@"D:\"] = ["Virtual Machines", "ISO"],
        [@"D:\ISO"] = [],
        [@"D:\Virtual Machines"] = [],
    };

    public void Disconnect(Guid hostId, string reason) => _connected[hostId] = false;

    // ---- management agents: only the calls are recorded (routing is covered by AgentConnectionManager's own logic).
    private readonly ConcurrentDictionary<Guid, bool> _managementConnected = new();
    public ConcurrentQueue<Guid> Reconfigured { get; } = new();
    public ConcurrentQueue<Guid> ManagementDisconnected { get; } = new();

    public void ConnectManagementAgent(Guid agentId) => _managementConnected[agentId] = true;

    public bool IsManagementAgentConnected(Guid agentId) => _managementConnected.TryGetValue(agentId, out var c) && c;

    public Task ReconfigureManagementAgentAsync(Guid agentId, CancellationToken ct)
    {
        Reconfigured.Enqueue(agentId);
        return Task.CompletedTask;
    }

    public void DisconnectManagementAgent(Guid agentId, string reason)
    {
        _managementConnected[agentId] = false;
        ManagementDisconnected.Enqueue(agentId);
    }

    /// <summary>Simulated agent versions (host or management agent id); default "0.7.0" with the current protocol when connected.</summary>
    public ConcurrentDictionary<Guid, string> AgentVersions { get; } = new();

    /// <summary>When set, an UpdateAgent command makes the agent "come back" with this version (as the updater task would).</summary>
    public string? VersionAfterUpdate { get; set; }

    public (int Protocol, string? Version)? AgentOf(Guid hostOrAgentId) =>
        IsConnected(hostOrAgentId) || IsManagementAgentConnected(hostOrAgentId)
            ? (ProtocolVersions.TryGetValue(hostOrAgentId, out var p) ? p : AgentProtocol.Version, AgentVersions.GetValueOrDefault(hostOrAgentId, "0.7.0"))
            : null;

    public Task<AgentCommandResult> SendToManagementAgentAsync(Guid agentId, AgentCommand command, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsManagementAgentConnected(agentId))
        {
            throw new HostNotConnectedException(agentId);
        }
        Received.Enqueue(command);
        return Task.FromResult(UpdateAgent(agentId, command));
    }

    private AgentCommandResult UpdateAgent(Guid id, AgentCommand command)
    {
        if (command.Update is null)
        {
            return new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Rejected };
        }
        if (VersionAfterUpdate is { } v)
        {
            AgentVersions[id] = v;
        }
        return new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Succeeded, Message = "staged" };
    }

    public Task<AgentCommandResult> SendAsync(Guid hostId, AgentCommand command, Action<AgentCommandProgress>? onProgress, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsConnected(hostId))
        {
            throw new HostNotConnectedException(hostId);
        }
        // Same local guard as AgentConnectionManager: nothing is sent to an agent whose protocol is too old.
        if (ProtocolVersionOf(hostId) < AgentProtocol.RequiredVersion(command))
        {
            return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Rejected, Message = "update the agent" });
        }
        Received.Enqueue(command);
        switch (command.Kind)
        {
            case AgentCommandKind.GetVm:
                return Task.FromResult(_vms.TryGetValue(command.VmId!.Value, out var v)
                    ? new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Succeeded, Vm = Summary(command.VmId.Value, v) }
                    : new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.NotFound });

            case AgentCommandKind.GetVmDetails:
                return Task.FromResult(_vms.ContainsKey(command.VmId!.Value)
                    ? new AgentCommandResult
                    {
                        CommandId = command.CommandId,
                        Status = AgentCommandStatus.Succeeded,
                        Details = new VmDetails
                        {
                            VmId = command.VmId.Value,
                            CollectedAtUtc = DateTimeOffset.UtcNow,
                            Disks = [new VmDisk { Kind = "VirtualHardDisk", Path = @"D:\VMs\disk.vhdx", Format = "VHDX", MaxSizeBytes = 64L << 30 }],
                            NetworkAdapters = [new NetworkAdapterInfo { Name = "Network Adapter", IpAddresses = ["10.0.0.10"], SwitchName = "vSwitch" }],
                        },
                    }
                    : new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.NotFound });

            case AgentCommandKind.DeleteVm:
                var delId = command.VmId!.Value;
                if (!_vms.TryGetValue(delId, out var victim))
                {
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.AlreadyInState });
                }
                if (victim.State is not (VmPowerState.Off or VmPowerState.Saved))
                {
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.InvalidState });
                }
                _vms.TryRemove(delId, out _);
                if (command.DeleteDisks == true)
                {
                    DeletedWithDisks.Add(delId);
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Succeeded,
                        Message = "VM deleted. Disk files deleted: 1." });
                }
                return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Succeeded, Message = "deleted" });

            case AgentCommandKind.VmPower:
                var id = command.VmId!.Value;
                if (!_vms.TryGetValue(id, out var vm) || vm.HostId != hostId)
                {
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.NotFound });
                }
                var op = command.PowerOperation!.Value;
                if (DropChannelAfterPowerCommand)
                {
                    if (ExecuteBeforeDrop && VmPowerRules.IsAllowed(op, vm.State))
                    {
                        _vms[id] = vm with { State = VmPowerRules.ExpectedState(op), UptimeMs = 10 };
                    }
                    throw new AgentChannelLostException(hostId);
                }
                if (VmPowerRules.IsAlreadyInTargetState(op, vm.State))
                {
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.AlreadyInState, Message = "already" });
                }
                if (!VmPowerRules.IsAllowed(op, vm.State))
                {
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.InvalidState });
                }
                onProgress?.Invoke(new AgentCommandProgress { CommandId = command.CommandId, Percent = 50 });
                _vms[id] = vm with { State = VmPowerRules.ExpectedState(op), UptimeMs = 10 };
                return Task.FromResult(new AgentCommandResult
                {
                    CommandId = command.CommandId,
                    Status = AgentCommandStatus.Succeeded,
                    Vm = Summary(id, _vms[id]),
                    HyperVCode = 0,
                });

            case AgentCommandKind.HyperVOperation:
                var outcome = OperationHandler?.Invoke(hostId, command) ?? DefaultOperation(hostId, command);
                return Task.FromResult(new AgentCommandResult
                {
                    CommandId = command.CommandId,
                    Status = outcome.Ok ? AgentCommandStatus.Succeeded : AgentCommandStatus.Failed,
                    Message = outcome.Error,
                    Operation = outcome,
                });

            case AgentCommandKind.CollectInventory:
                return Task.FromResult(new AgentCommandResult
                {
                    CommandId = command.CommandId,
                    Status = AgentCommandStatus.Succeeded,
                    Inventory = new HostInventory
                    {
                        CollectedAtUtc = DateTimeOffset.UtcNow,
                        Access = HyperVAccessStatus.Ok,
                        Host = new HostFacts { ComputerName = "FAKEHOST", AgentVersion = "test", VirtualSwitches = ["vSwitch"] },
                        Vms = _vms.Where(kv => kv.Value.HostId == hostId).Select(kv => Summary(kv.Key, kv.Value)).ToList(),
                    },
                });

            case AgentCommandKind.ConsoleFrame:
                var fr = command.FrameRequest!;
                if (!_vms.TryGetValue(fr.VmId, out var cvm))
                {
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.NotFound });
                }
                if (cvm.State != VmPowerState.Running)
                {
                    return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.InvalidState, Message = "The VM is not running." });
                }
                var first = _framesSent.TryAdd(fr.VmId, true);
                return Task.FromResult(new AgentCommandResult
                {
                    CommandId = command.CommandId,
                    Status = AgentCommandStatus.Succeeded,
                    Frame = first || fr.ForceFull
                        ? new ConsoleFrame { Width = 4, Height = 2, GuestWidth = 4, GuestHeight = 2, MouseAvailable = true, Data = Convert.ToBase64String(TinyFrame()) }
                        : new ConsoleFrame { Width = 4, Height = 2, GuestWidth = 4, GuestHeight = 2, MouseAvailable = true, Unchanged = true },
                });

            case AgentCommandKind.ConsoleInput:
                return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Succeeded });

            case AgentCommandKind.UpdateAgent:
                return Task.FromResult(UpdateAgent(hostId, command));

            default:
                return Task.FromResult(new AgentCommandResult { CommandId = command.CommandId, Status = AgentCommandStatus.Rejected });
        }
    }

    private readonly ConcurrentDictionary<Guid, bool> _framesSent = new();

    /// <summary>zlib-compressed 4x2 RGB565 frame.</summary>
    private static byte[] TinyFrame()
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(new byte[4 * 2 * 2]);
        }
        return ms.ToArray();
    }

    /// <summary>Optional override for typed Hyper-V operations.</summary>
    public Func<Guid, AgentCommand, HvOperationOutcome>? OperationHandler { get; set; }

    public IReadOnlyList<AgentCommand> OperationsReceived(HvOperation op) =>
        Received.Where(c => c.Kind == AgentCommandKind.HyperVOperation && c.Operation == op).ToList();

    public int InputsReceived => Received.Count(c => c.Kind == AgentCommandKind.ConsoleInput);

    private HvOperationOutcome DefaultOperation(Guid hostId, AgentCommand command)
    {
        HvOperationOutcome Ok(object? result, params string[] applied) =>
            new() { Ok = true, Result = result is null ? null : HvOperations.ToElement(result), Applied = applied };
        switch (command.Operation)
        {
            case HvOperation.GetVmConfig:
                var r = HvOperations.FromElement<VmRef>(command.OperationArgs)!;
                if (!_vms.TryGetValue(r.VmId, out var v))
                {
                    return new HvOperationOutcome { Ok = false, Error = "VM not found on this host." };
                }
                return Ok(new
                {
                    name = v.Name, notes = "", generation = 2, state = v.State.ToString(), version = "12.0", processorCount = 2,
                    memoryStartupBytes = 4L << 30, dynamicMemoryEnabled = false, checkpointType = "Production", secureBootEnabled = true,
                    secureBootTemplate = "MicrosoftWindows", tpmEnabled = false, dvdDrives = Array.Empty<object>(),
                    networkAdapters = new[] { new { id = "Microsoft:NIC1", name = "Network Adapter", switchName = "vSwitch", vlanId = 0 } },
                    hardDisks = new[] { new { controllerType = "SCSI", controllerNumber = 0, controllerLocation = 0, path = @"D:\VMs\disk.vhdx" } },
                });
            case HvOperation.SetVmConfig:
                return Ok(new { applied = true }, "processor");
            case HvOperation.NewVm:
                var req = HvOperations.FromElement<NewVmRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(req) is { } invalidVm)
                {
                    return new HvOperationOutcome { Ok = false, Error = invalidVm };
                }
                // From a template: every disk file must exist in a template folder of this host (they are copied).
                var templateFolders = TemplateFoldersOf(hostId);
                if (req.TemplateDisks.FirstOrDefault(d => !templateFolders.TryGetValue(System.IO.Path.GetDirectoryName(d)!, out var files)
                        || !files.Contains(System.IO.Path.GetFileName(d))) is { } missingDisk)
                {
                    return new HvOperationOutcome { Ok = false, Error = "Template disk not found: " + missingDisk };
                }
                var newId = Guid.NewGuid();
                if (req.TemplateDisks.Count > 0)
                {
                    CreatedFromTemplate[newId] = req;
                }
                _vms[newId] = (hostId, req.Name, req.StartAfterCreate ? VmPowerState.Running : VmPowerState.Off, 0);
                _specs[newId] = (req.ProcessorCount, req.MemoryStartupMb, req.MemoryMaximumMb, req.DynamicMemory, req.DiskSizeGb + req.TemplateExtraDiskGb);
                if (req.Location is not null)
                {
                    _paths[newId] = req.Location + @"\" + req.Name;
                }
                return Ok(new NewVmResult(newId, req.Name, req.Location is null ? @"D:\VMs" : req.Location + @"\" + req.Name, @"D:\VHDs\" + req.Name + ".vhdx", null,
                    req.HighlyAvailable ? true : null), req.HighlyAvailable ? ["created", "highly-available"] : ["created"]);
            case HvOperation.NewTemplate:
                var nt = HvOperations.FromElement<NewTemplateRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(nt) is { } invalidTemplate)
                {
                    return new HvOperationOutcome { Ok = false, Error = invalidTemplate };
                }
                if (!_vms.TryGetValue(nt.VmId, out var source) || source.HostId != hostId)
                {
                    return new HvOperationOutcome { Ok = false, Error = "VM not found on this host." };
                }
                if (source.State != VmPowerState.Off)
                {
                    return new HvOperationOutcome { Ok = false, Error = $"The VM must be off to create a template from it (it is {source.State})." };
                }
                if (TemplateFoldersOf(hostId).ContainsKey(nt.Folder))
                {
                    return new HvOperationOutcome { Ok = false, Error = "The template folder already exists: " + nt.Folder };
                }
                var sourceGb = _specs.TryGetValue(nt.VmId, out var sourceSpec) ? sourceSpec.DiskGb : 64L;
                var templateDisks = new List<TemplateDisk> { new("disk0.vhdx", sourceGb << 30, 9L << 30) };
                if (TemplateDataDiskGb > 0)
                {
                    templateDisks.Add(new("disk1.vhdx", (long)TemplateDataDiskGb << 30, 1L << 30));
                }
                TemplateFoldersOf(hostId)[nt.Folder] = templateDisks.Select(d => d.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return Ok(new NewTemplateResult
                {
                    Folder = nt.Folder, Disks = templateDisks, Generation = 2, ProcessorCount = 2, MemoryStartupMb = 4096, DynamicMemory = false,
                    MemoryMinimumMb = 512, MemoryMaximumMb = 1048576, SecureBootTemplate = "MicrosoftWindows", TpmEnabled = false,
                }, "folder", "copied");
            case HvOperation.RemoveTemplate:
                var rt = HvOperations.FromElement<TemplateFolderRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(rt) is { } invalidFolder)
                {
                    return new HvOperationOutcome { Ok = false, Error = invalidFolder };
                }
                return TemplateFoldersOf(hostId).TryRemove(rt.Folder, out _)
                    ? Ok(new { removed = true, folder = rt.Folder }, "removed")
                    : Ok(new { removed = false, folder = rt.Folder });
            case HvOperation.ListCheckpoints:
                return Ok(new[] { new CheckpointInfo(Guid.NewGuid().ToString("D"), "Before update", null, DateTimeOffset.UtcNow, "Production", true) });
            case HvOperation.NewCheckpoint or HvOperation.RestoreCheckpoint or HvOperation.RemoveCheckpoint:
                return Ok(new { id = Guid.NewGuid() }, "done");
            case HvOperation.GetHostDefaults:
                return Ok(new HostDefaults(@"D:\VHDs", @"D:\VMs", 12, 128L << 30, ["12.0"], "12.0"));
            case HvOperation.ListSwitches:
                return Ok(new[] { new SwitchInfo(Guid.NewGuid().ToString("D"), "vSwitch", "External", "NIC", true, false,
                    _vms.Values.Where(x => x.HostId == hostId).Select(x => x.Name).ToList()) }.Concat(Switches.GetValueOrDefault(hostId) ?? []).ToList());
            case HvOperation.ListNetAdapters:
                return Ok(new[]
                {
                    new NetAdapterEntry("Ethernet", "Intel(R) I350 #1", "Up", "1 Gbps", "00-15-5D-00-00-01", "vSwitch"),
                    new NetAdapterEntry("Ethernet 2", "Intel(R) I350 #2", "Up", "1 Gbps", "00-15-5D-00-00-02", null),
                });
            case HvOperation.NewSwitch:
                var ns = HvOperations.FromElement<NewSwitchRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(ns) is { } badSwitch)
                {
                    return new HvOperationOutcome { Ok = false, Error = badSwitch };
                }
                var hostSwitches = Switches.GetOrAdd(hostId, _ => []);
                if (ns.Name == "vSwitch" || hostSwitches.Any(x => x.Name == ns.Name))
                {
                    return new HvOperationOutcome { Ok = false, Error = $"A virtual switch named '{ns.Name}' already exists on this host." };
                }
                var createdSwitch = new SwitchInfo(Guid.NewGuid().ToString("D"), ns.Name, ns.Type, ns.NetAdapterName, ns.AllowManagementOs, false, []);
                hostSwitches.Add(createdSwitch);
                return Ok(new { id = createdSwitch.Id, name = createdSwitch.Name, type = createdSwitch.Type }, "created");
            case HvOperation.RemoveSwitch:
                var rs = HvOperations.FromElement<SwitchRef>(command.OperationArgs)!;
                var removed = Switches.GetValueOrDefault(hostId)?.RemoveAll(x => x.Id == rs.SwitchId) ?? 0;
                return removed == 0 ? new HvOperationOutcome { Ok = false, Error = "Virtual switch not found." } : Ok(new { id = rs.SwitchId }, "removed");
            case HvOperation.ListHostDisks:
                return Ok(_vms.Where(kv => kv.Value.HostId == hostId)
                    .Select(kv => new HostDiskInfo(@"D:\VHDs\" + kv.Value.Name + ".vhdx", kv.Key, kv.Value.Name, "SCSI 0:0", "VHDX", "Dynamic",
                        (_specs.TryGetValue(kv.Key, out var sized) ? sized.DiskGb : 64L) << 30, 10L << 30, null)).ToList());
            case HvOperation.ListIsos:
                return Ok(new[] { new IsoFile(@"D:\ISO\win.iso", 5L << 30, DateTimeOffset.UtcNow) });
            case HvOperation.GetHostHealth:
                return Ok(Health ?? new HostHealthReport
                {
                    CollectedUtc = DateTimeOffset.UtcNow,
                    PhysicalDisks = [new PhysicalDiskHealth { Name = "Disk 0", HealthStatus = "Healthy", Status = HealthLevel.Ok }],
                    Volumes = [new VolumeHealth { Name = "C:", SizeBytes = 100L << 30, FreeBytes = 50L << 30, HealthStatus = "Healthy", Status = HealthLevel.Ok }],
                    Services = [new ServiceHealth("vmms", "Hyper-V Virtual Machine Management", "Running", "Automatic")],
                });
            case HvOperation.MoveVm:
                var mv = HvOperations.FromElement<MoveVmRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(mv) is { } badMove)
                {
                    return new HvOperationOutcome { Ok = false, Error = badMove };
                }
                if (!_vms.TryGetValue(mv.VmId, out var moving) || moving.HostId != hostId)
                {
                    return new HvOperationOutcome { Ok = false, Error = "VM not found on this host." };
                }
                if (MoveDestinationHost is { } destHost)
                {
                    _vms[mv.VmId] = moving with { HostId = destHost };
                }
                return Ok(new MoveVmResult(mv.VmId, moving.Name, mv.DestinationHost, mv.DestinationPath, moving.State.ToString()), "moved");
            case HvOperation.MoveVmStorage:
                var ms = HvOperations.FromElement<MoveVmStorageRequest>(command.OperationArgs)!;
                return OperationValidation.Validate(ms) is { } badStorage
                    ? new HvOperationOutcome { Ok = false, Error = badStorage }
                    : Ok(new MoveVmResult(ms.VmId, null, null, ms.DestinationPath + @"\vm", "Running"), "moved");
            case HvOperation.NewFolder:
                var nf = HvOperations.FromElement<NewFolderRequest>(command.OperationArgs)!;
                var created = System.IO.Path.Combine(nf.Parent, nf.Name);
                if (!Folders.TryGetValue(nf.Parent, out var parentSubs)) return new HvOperationOutcome { Ok = false, Error = "not_found: folder not found." };
                if (parentSubs.Contains(nf.Name, StringComparer.OrdinalIgnoreCase)) return new HvOperationOutcome { Ok = false, Error = "file_exists: a file or folder with this name already exists." };
                Folders[nf.Parent] = [.. parentSubs, nf.Name];
                Folders[created] = [];
                return Ok(new FolderEntry(nf.Name, created));
            case HvOperation.GetBackupReadiness or HvOperation.BackupVm or HvOperation.VerifyBackup or HvOperation.RemoveBackup or HvOperation.ListBackups
                or HvOperation.RestoreBackup or HvOperation.RestoreBackupDisk or HvOperation.MountBackupDisk or HvOperation.DismountBackupDisk
                or HvOperation.ListBackupFiles or HvOperation.ReadBackupFile:
                return BackupOperation(hostId, command);
            case HvOperation.ListFolder or HvOperation.ReadFile or HvOperation.WriteFileBegin or HvOperation.WriteFileChunk
                or HvOperation.WriteFileCommit or HvOperation.WriteFileAbort:
                return FileOperation(command);
            case HvOperation.ListVmGroups:
                return ListVmGroups(hostId);
            case HvOperation.SyncVmGroups:
                var syncReq = HvOperations.FromElement<VmGroupSyncRequest>(command.OperationArgs)!;
                return OperationValidation.Validate(syncReq) is { } bad ? new HvOperationOutcome { Ok = false, Error = bad } : SyncVmGroups(hostId, syncReq);
            case HvOperation.BrowseFolders:
                var b = HvOperations.FromElement<FolderBrowseRequest>(command.OperationArgs)!;
                if (b.Path is null)
                {
                    return Ok(new FolderListing { Drives = Folders.Keys.Where(k => k.Length == 3).Select(k => new DriveEntry(k, null, 1L << 40, 1L << 39)).ToList() });
                }
                if (!Folders.TryGetValue(b.Path, out var subs))
                {
                    return new HvOperationOutcome { Ok = false, Error = "Folder not found." };
                }
                return Ok(new FolderListing
                {
                    Path = b.Path,
                    Folders = subs.Select(n => new FolderEntry(n, System.IO.Path.Combine(b.Path, n))).ToList(),
                    Isos = b.IncludeIsos && b.Path.Equals(@"D:\ISO", StringComparison.OrdinalIgnoreCase) ? [new IsoFile(@"D:\ISO\win.iso", 5L << 30, DateTimeOffset.UtcNow)] : [],
                    FreeBytes = 1L << 39,
                });
            case HvOperation.GetVmMetrics:
                var mr = HvOperations.FromElement<VmMetricsRequest>(command.OperationArgs)!;
                var measured = _vms.Where(kv => kv.Value.HostId == hostId && (mr.VmId is { } one ? kv.Key == one : Meters.ContainsKey(kv.Key)))
                    .Select(kv => Meters.TryGetValue(kv.Key, out var totals)
                        ? totals with { VmId = kv.Key, Name = kv.Value.Name, State = kv.Value.State.ToString(), MeteringEnabled = true }
                        : new VmMetricsSample { VmId = kv.Key, Name = kv.Value.Name, State = kv.Value.State.ToString(), MeteringEnabled = false, ProcessorCount = 2 })
                    .ToList();
                if (mr.VmId is { } wanted && measured.Count == 0)
                {
                    return new HvOperationOutcome { Ok = false, Error = $"VM {wanted} not found on this host." };
                }
                return Ok(new VmMetricsReport { HostProcessorMhz = 2000, HostLogicalProcessors = 16, Vms = measured });
            case HvOperation.SetVmResourceMetering:
                var mc = HvOperations.FromElement<VmMeteringChange>(command.OperationArgs)!;
                if (!_vms.TryGetValue(mc.VmId, out var meteredVm) || meteredVm.HostId != hostId)
                {
                    return new HvOperationOutcome { Ok = false, Error = "VM not found on this host." };
                }
                if (mc.Enabled)
                {
                    Meters.TryAdd(mc.VmId, NewMeter(mc.VmId));
                }
                else
                {
                    Meters.TryRemove(mc.VmId, out _);
                }
                return Ok(new { enabled = mc.Enabled, changed = true }, mc.Enabled ? "enabled" : "disabled");
            case HvOperation.ResetVmResourceMetering:
                var rr = HvOperations.FromElement<VmRef>(command.OperationArgs)!;
                if (!Meters.ContainsKey(rr.VmId))
                {
                    return new HvOperationOutcome { Ok = false, Error = "invalid_state: resource metering is off for this VM." };
                }
                Meters[rr.VmId] = NewMeter(rr.VmId);
                MeteringResets.Enqueue(rr.VmId);
                return Ok(new { reset = true }, "reset");
            // ---- protocol 11: failover clusters (simulated; the real cmdlets are in the real-host test plan)
            case HvOperation.GetClusterStatus:
                if (ClusterStatusFails.TryGetValue(hostId, out var clusterError))
                {
                    return new HvOperationOutcome { Ok = false, Error = clusterError };
                }
                return Cluster is { } report
                    ? Ok(report with { ReadByNode = ClusterNodeHosts.FirstOrDefault(kv => kv.Value == hostId).Key ?? "NODE" })
                    : new HvOperationOutcome { Ok = false, Error = "access_denied: the cluster cannot be read with the agent account." };
            case HvOperation.SetClusterNodeMaintenance:
                var maintenance = HvOperations.FromElement<ClusterNodeMaintenanceRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(maintenance) is { } badMaintenance)
                {
                    return new HvOperationOutcome { Ok = false, Error = badMaintenance };
                }
                MaintenanceRequests.Enqueue((hostId, maintenance));
                var newState = maintenance.Action == MaintenanceActions.Enter ? "Paused" : "Up";
                if (Cluster is { } current)
                {
                    Cluster = current with
                    {
                        Nodes = current.Nodes.Select(n => string.Equals(n.Name, maintenance.NodeName, StringComparison.OrdinalIgnoreCase)
                            ? n with { State = newState, DrainStatus = maintenance.Action == MaintenanceActions.Enter ? "Completed" : n.DrainStatus } : n).ToList(),
                    };
                }
                return Ok(new ClusterNodeMaintenanceResult
                {
                    NodeName = maintenance.NodeName, State = newState, DrainStatus = maintenance.Action == MaintenanceActions.Enter ? "Completed" : null,
                    StorageMaintenance = maintenance.StorageMaintenance && Cluster?.Storage?.S2dEnabled == true && maintenance.Action == MaintenanceActions.Enter,
                }, maintenance.Action == MaintenanceActions.Enter ? "drained" : "resumed");
            case HvOperation.MoveClusterVm:
                var clusterMove = HvOperations.FromElement<MoveClusterVmRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(clusterMove) is { } badClusterMove)
                {
                    return new HvOperationOutcome { Ok = false, Error = badClusterMove };
                }
                if (!_vms.TryGetValue(clusterMove.VmId, out var clusterVm) || clusterVm.HostId != hostId)
                {
                    return new HvOperationOutcome { Ok = false, Error = "VM not found on this host." };
                }
                if (ClusterNodeHosts.TryGetValue(clusterMove.NodeName, out var nodeHost))
                {
                    _vms[clusterMove.VmId] = clusterVm with { HostId = nodeHost };
                }
                return Ok(new MoveVmResult(clusterMove.VmId, clusterVm.Name, clusterMove.NodeName, null, clusterVm.State.ToString()), "moved");
            default:
                return new HvOperationOutcome { Ok = false, Error = "Unsupported operation." };
        }
    }

    /// <summary>Cluster report returned by GetClusterStatus (null = the agent cannot read the cluster).</summary>
    public ClusterStatusReport? Cluster { get; set; }

    /// <summary>Hosts whose GetClusterStatus fails with this error (another node must answer).</summary>
    public ConcurrentDictionary<Guid, string> ClusterStatusFails { get; } = new();

    /// <summary>Cluster node name -> host (MoveClusterVm moves the VM there; ReadByNode).</summary>
    public ConcurrentDictionary<string, Guid> ClusterNodeHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<(Guid SentTo, ClusterNodeMaintenanceRequest Request)> MaintenanceRequests { get; } = new();

    /// <summary>Simulated resource metering (Hyper-V VM id -> Measure-VM totals of this moment; present = metering on).</summary>
    public ConcurrentDictionary<Guid, VmMetricsSample> Meters { get; } = new();

    public ConcurrentQueue<Guid> MeteringResets { get; } = new();

    public static VmMetricsSample NewMeter(Guid vmId) => new()
    {
        VmId = vmId, MeteringEnabled = true, ProcessorCount = 2, CpuUsagePercent = 5, MemoryAssignedMb = 4096, MemoryDemandMb = 2048,
        MemoryStartupMb = 4096, MemoryMaximumMb = 8192, DynamicMemoryEnabled = true, MemoryStatus = "OK", MeteringSeconds = 0,
        AverageCpuMhz = 0, TotalDiskMb = 65536, NormalizedIoCount = 0, AverageLatencyMs = 0, DiskReadMb = 0, DiskWrittenMb = 0,
        NetworkInboundMb = 0, NetworkOutboundMb = 0,
    };

    /// <summary>Health report returned by GetHostHealth (default: a healthy host).</summary>
    public HostHealthReport? Health { get; set; }

    /// <summary>When set, a MoveVm moves the simulated VM to this host.</summary>
    public Guid? MoveDestinationHost { get; set; }

    /// <summary>Simulated repositories: "&lt;repository path&gt;|&lt;point folder&gt;" -> manifest written by BackupVm.</summary>
    public ConcurrentDictionary<string, BackupManifest> Repository { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, BackupVm fails with this message.</summary>
    public string? BackupFailure { get; set; }

    /// <summary>Result of the next restore tests (heartbeat seen).</summary>
    public bool RestoreTestPasses { get; set; } = true;

    /// <summary>Mounted backup disks: mount id -> volume root.</summary>
    public ConcurrentDictionary<Guid, string> BackupMountsSimulated { get; } = new();

    private HvOperationOutcome BackupOperation(Guid hostId, AgentCommand command)
    {
        HvOperationOutcome Ok(object result, params string[] applied) => new() { Ok = true, Result = HvOperations.ToElement(result), Applied = applied };
        HvOperationOutcome Fail(string error) => new() { Ok = false, Error = error };
        switch (command.Operation)
        {
            case HvOperation.GetBackupReadiness:
                var ready = HvOperations.FromElement<BackupReadinessRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(ready) is { } badReady) return Fail(badReady);
                var checks = new List<BackupCheck> { new("hyperv-module", "Ok", "Hyper-V PowerShell module available.", null) };
                if (ready.Target is { } t)
                {
                    checks.Add(new("repository", "Ok", $"Repository {t.Path} reachable and writable.", null));
                }
                return Ok(new BackupReadiness(checks));
            case HvOperation.BackupVm:
                var b = HvOperations.FromElement<BackupVmRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(b) is { } badBackup) return Fail(badBackup);
                if (!_vms.TryGetValue(b.VmId, out var vm) || vm.HostId != hostId) return Fail("VM not found on this host.");
                if (BackupFailure is { } failure) return Fail(failure);
                var files = new List<BackupFile>
                {
                    new($@"{vm.Name}\Virtual Machines\{b.VmId:D}.vmcx", 40_000, new string('a', 64)),
                    new($@"{vm.Name}\Virtual Hard Disks\{vm.Name}.vhdx", 10L << 30, new string('b', 64)),
                };
                var manifest = new BackupManifest
                {
                    PointId = b.PointId, VmId = b.VmId, VmName = vm.Name, HostName = "FAKEHOST", CreatedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow,
                    CheckpointTypeRequested = b.CheckpointType, CheckpointTypeUsed = "Production", Generation = 2, ProcessorCount = 2, MemoryStartupMb = 4096,
                    ConfigFile = files[0].Path, Disks = [files[1].Path], Files = files, TotalBytes = files.Sum(f => f.SizeBytes),
                };
                Repository[b.Target.Path + "|" + b.Folder] = manifest;
                return Ok(manifest, "checkpoint", "exported", "checkpointRemoved", "manifest");
            case HvOperation.VerifyBackup:
                var v = HvOperations.FromElement<BackupPointRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(v) is { } badVerify) return Fail(badVerify);
                return Repository.TryGetValue(v.Target.Path + "|" + v.Folder, out var m)
                    ? Ok(new BackupVerifyResult(true, m.Files.Count, m.TotalBytes, []), "verified")
                    : Ok(new BackupVerifyResult(false, 0, 0, ["missing: hvportal-backup.json"]), "verified");
            case HvOperation.RemoveBackup:
                var r = HvOperations.FromElement<BackupPointRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(r) is { } badRemove) return Fail(badRemove);
                return Ok(new { removed = Repository.TryRemove(r.Target.Path + "|" + r.Folder, out _) }, "removed");
            case HvOperation.ListBackups:
                var l = HvOperations.FromElement<BackupPointRequest>(command.OperationArgs)!;
                return Ok(Repository.Where(kv => kv.Key.StartsWith(l.Target.Path + "|", StringComparison.OrdinalIgnoreCase))
                    .Select(kv => new { folder = kv.Key.Split('|')[1], manifest = kv.Value }).ToList());
            case HvOperation.RestoreBackup:
                var rb = HvOperations.FromElement<RestoreBackupRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(rb) is { } badRestore) return Fail(badRestore);
                if (!Repository.TryGetValue(rb.Source.Path + "|" + rb.Folder, out var point)) return Fail("The restore point has no manifest.");
                if (rb.Mode == RestoreModes.Test)
                {
                    return Ok(new RestoreBackupResult { Mode = rb.Mode, Name = rb.NewName, TestPassed = RestoreTestPasses, TestSeconds = 42,
                        TestDetail = RestoreTestPasses ? "The VM started and the guest reported heartbeat OkApplicationsUnknown after 42 s." : "No healthy heartbeat within 600 s." }, "imported", "testVmRemoved");
                }
                if (rb.Mode == RestoreModes.Replace)
                {
                    return Ok(new RestoreBackupResult { Mode = rb.Mode, VmId = rb.ReplaceVmId, Name = point.VmName }, "originalRemoved", "imported");
                }
                var restoredId = Guid.NewGuid();
                _vms[restoredId] = (hostId, rb.NewName!, rb.StartAfter ? VmPowerState.Running : VmPowerState.Off, 0);
                return Ok(new RestoreBackupResult { Mode = rb.Mode, VmId = restoredId, Name = rb.NewName, Path = (rb.Location ?? @"D:\VMs") + @"\" + rb.NewName }, "imported");
            case HvOperation.RestoreBackupDisk:
                var rd = HvOperations.FromElement<RestoreDiskRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(rd) is { } badDisk) return Fail(badDisk);
                return Ok(new RestoreDiskResult(rd.DestinationFolder + @"\disk-restored.vhdx", rd.AttachVmId is not null, rd.AttachVmId), "copied");
            case HvOperation.MountBackupDisk:
                var mb = HvOperations.FromElement<MountBackupDiskRequest>(command.OperationArgs)!;
                if (OperationValidation.Validate(mb) is { } badMount) return Fail(badMount);
                var mountId = Guid.NewGuid();
                BackupMountsSimulated[mountId] = @"F:\";
                Folders[@"F:\"] = ["Users"];
                Files[@"F:\report.docx"] = [1, 2, 3, 4];
                return Ok(new BackupMount(mountId, [new MountedVolume(@"F:\", "Windows", "NTFS", 100L << 30)], DateTimeOffset.UtcNow.AddHours(2), false), "mounted");
            case HvOperation.DismountBackupDisk:
                var d = HvOperations.FromElement<BackupMountRef>(command.OperationArgs)!;
                return Ok(new { detached = BackupMountsSimulated.TryRemove(d.MountId, out _) }, "dismounted");
            case HvOperation.ListBackupFiles:
                var lf = HvOperations.FromElement<BackupFileListRequest>(command.OperationArgs)!;
                if (!BackupMountsSimulated.ContainsKey(lf.MountId)) return Fail("not_found: the backup disk is no longer attached.");
                return FileOperation(command with { Operation = HvOperation.ListFolder, OperationArgs = HvOperations.ToElement(new FileListRequest(lf.Path)) });
            default:
                var rf = HvOperations.FromElement<BackupFileReadRequest>(command.OperationArgs)!;
                if (!BackupMountsSimulated.ContainsKey(rf.MountId)) return Fail("not_found: the backup disk is no longer attached.");
                return FileOperation(command with { Operation = HvOperation.ReadFile, OperationArgs = HvOperations.ToElement(new FileReadRequest(rf.Path, rf.Offset, rf.Length)) });
        }
    }

    /// <summary>Simulated template folders per host: folder path -> disk files in it (NewTemplate / RemoveTemplate / NewVm from a template).</summary>
    public ConcurrentDictionary<Guid, ConcurrentDictionary<string, HashSet<string>>> TemplateFolders { get; } = new();

    public ConcurrentDictionary<string, HashSet<string>> TemplateFoldersOf(Guid hostId) =>
        TemplateFolders.GetOrAdd(hostId, _ => new(StringComparer.OrdinalIgnoreCase));

    /// <summary>VMs created from a template, with the request the agent received (disks to copy, sizes).</summary>
    public ConcurrentDictionary<Guid, NewVmRequest> CreatedFromTemplate { get; } = new();

    /// <summary>When greater than 0 the next templates get a second (data) disk of this size.</summary>
    public int TemplateDataDiskGb { get; set; }

    /// <summary>Simulated native Hyper-V VM groups per host (Get-VMGroup), same rules as the operations script.</summary>
    public sealed class NativeGroup
    {
        public required string Name { get; set; }
        public required string GroupType { get; init; }
        public HashSet<Guid> VmIds { get; } = [];
        public HashSet<Guid> GroupIds { get; } = [];
    }

    public ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, NativeGroup>> NativeGroups { get; } = new();

    public ConcurrentDictionary<Guid, NativeGroup> NativeGroupsOf(Guid hostId) => NativeGroups.GetOrAdd(hostId, _ => new());

    private HvOperationOutcome ListVmGroups(Guid hostId) =>
        new()
        {
            Ok = true,
            Result = HvOperations.ToElement(NativeGroupsOf(hostId).Select(kv => new NativeVmGroup(kv.Key, kv.Value.Name, kv.Value.GroupType, kv.Value.VmIds.ToList(), kv.Value.GroupIds.ToList())).ToList()),
        };

    private HvOperationOutcome SyncVmGroups(Guid hostId, VmGroupSyncRequest req)
    {
        var all = NativeGroupsOf(hostId);
        var managed = req.Managed.ToHashSet();
        var ids = new Dictionary<string, Guid>();
        var applied = new List<string>();
        var removed = new List<Guid>();
        foreach (var it in req.Items)
        {
            if (it.Id is { } id && managed.Contains(id) && all.TryGetValue(id, out var g) && g.GroupType == it.GroupType)
            {
                if (g.Name != it.Name) { g.Name = it.Name; applied.Add("renamed:" + it.Name); }
                ids[it.Key] = id;
            }
            else
            {
                var nid = Guid.NewGuid();
                all[nid] = new NativeGroup { Name = it.Name, GroupType = it.GroupType };
                managed.Add(nid);
                ids[it.Key] = nid;
                applied.Add("created:" + it.Name);
            }
        }
        var hostVms = _vms.Where(kv => kv.Value.HostId == hostId).Select(kv => kv.Key).ToHashSet();
        foreach (var it in req.Items)
        {
            var g = all[ids[it.Key]];
            if (it.GroupType == VmGroupTypes.Vm)
            {
                var want = it.VmIds.Where(hostVms.Contains).ToHashSet();
                if (!g.VmIds.SetEquals(want)) { g.VmIds.Clear(); g.VmIds.UnionWith(want); applied.Add("vms:" + it.Name); }
            }
            else
            {
                var want = it.MemberKeys.Where(ids.ContainsKey).Select(k => ids[k]).ToHashSet();
                if (!g.GroupIds.SetEquals(want)) { g.GroupIds.Clear(); g.GroupIds.UnionWith(want); applied.Add("groups:" + it.Name); }
            }
        }
        foreach (var rid in managed.Where(m => !ids.ContainsValue(m)).ToList())
        {
            foreach (var p in all.Values) p.GroupIds.Remove(rid);
            if (all.TryRemove(rid, out var gone)) applied.Add("removed:" + gone.Name);
            removed.Add(rid);
        }
        return new HvOperationOutcome { Ok = true, Applied = applied, Result = HvOperations.ToElement(new VmGroupSyncResult { Ids = ids, Removed = removed }) };
    }

    /// <summary>Simulated host files (full path -> content) for the file explorer; folders are those of <see cref="Folders"/>.</summary>
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths that behave like the disk of a running VM (open for writing elsewhere).</summary>
    public HashSet<string> FilesInUse { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<Guid, (string Path, long Size, bool Overwrite, MemoryStream Data)> _uploads = new();

    private HvOperationOutcome FileOperation(AgentCommand command)
    {
        HvOperationOutcome Ok(object result) => new() { Ok = true, Result = HvOperations.ToElement(result) };
        HvOperationOutcome Fail(string error) => new() { Ok = false, Error = error };
        switch (command.Operation)
        {
            case HvOperation.ListFolder:
                var l = HvOperations.FromElement<FileListRequest>(command.OperationArgs)!;
                if (!Folders.TryGetValue(l.Path, out var subs)) return Fail("not_found: folder not found.");
                return Ok(new FolderListing
                {
                    Path = l.Path,
                    Folders = subs.Select(n => new FolderEntry(n, System.IO.Path.Combine(l.Path, n))).ToList(),
                    Files = Files.Where(kv => string.Equals(System.IO.Path.GetDirectoryName(kv.Key), l.Path, StringComparison.OrdinalIgnoreCase))
                        .Select(kv => new FileEntry(System.IO.Path.GetFileName(kv.Key), kv.Key, kv.Value.Length, DateTimeOffset.UtcNow)).ToList(),
                });
            case HvOperation.ReadFile:
                var r = HvOperations.FromElement<FileReadRequest>(command.OperationArgs)!;
                if (FilesInUse.Contains(r.Path)) return Fail("file_in_use: the file is open for writing by another process.");
                if (!Files.TryGetValue(r.Path, out var content)) return Fail("not_found: file not found.");
                var len = (int)Math.Min(r.Length, content.Length - r.Offset);
                return Ok(new FileChunk(r.Offset, content.Length, DateTimeOffset.UtcNow, Convert.ToBase64String(content, (int)r.Offset, len), r.Offset + len >= content.Length));
            case HvOperation.WriteFileBegin:
                var b = HvOperations.FromElement<FileWriteBeginRequest>(command.OperationArgs)!;
                var final = System.IO.Path.Combine(b.Folder, b.Name);
                if (!Folders.ContainsKey(b.Folder)) return Fail("not_found: folder not found.");
                if (Files.ContainsKey(final) && !b.Overwrite) return Fail("file_exists: the file already exists.");
                var uid = Guid.NewGuid();
                _uploads[uid] = (final, b.SizeBytes, b.Overwrite, new MemoryStream());
                return Ok(new FileWriteBeginResult(uid, final));
            case HvOperation.WriteFileChunk:
                var c = HvOperations.FromElement<FileWriteChunkRequest>(command.OperationArgs)!;
                if (!_uploads.TryGetValue(c.UploadId, out var up)) return Fail("upload_not_found: unknown upload.");
                if (c.Offset != up.Data.Length) return Fail("invalid_range: unexpected offset.");
                up.Data.Write(Convert.FromBase64String(c.Data));
                return Ok(new { written = up.Data.Length });
            case HvOperation.WriteFileCommit:
                var cm = HvOperations.FromElement<FileWriteCommitRequest>(command.OperationArgs)!;
                if (!_uploads.TryRemove(cm.UploadId, out var done)) return Fail("upload_not_found: unknown upload.");
                var bytes = done.Data.ToArray();
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                if (bytes.Length != done.Size) return Fail("invalid_size: size mismatch.");
                if (hash != cm.Sha256) return Fail("hash_mismatch: hash mismatch.");
                Files[done.Path] = bytes;
                return Ok(new FileWriteResult(done.Path, bytes.Length, hash));
            default:
                var a = HvOperations.FromElement<FileWriteAbortRequest>(command.OperationArgs)!;
                _uploads.TryRemove(a.UploadId, out _);
                AbortedUploads++;
                return Ok(new { aborted = true });
        }
    }

    public int AbortedUploads { get; private set; }

    private VmSummary Summary(Guid id, (Guid HostId, string Name, VmPowerState State, long UptimeMs) v) =>
        _specs.TryGetValue(id, out var s)
            ? new()
            {
                VmId = id, Name = v.Name, PowerState = v.State, UptimeMs = v.UptimeMs, ProcessorCount = s.Processors, MemoryStartupMb = s.MemoryMb,
                DynamicMemoryEnabled = s.Dynamic, MemoryMaximumMb = s.Dynamic ? s.MaximumMb : s.MemoryMb,
                ConfigurationPath = _paths.GetValueOrDefault(id),
            }
            : new() { VmId = id, Name = v.Name, PowerState = v.State, UptimeMs = v.UptimeMs };
}
