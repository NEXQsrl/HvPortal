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
    private readonly ConcurrentDictionary<Guid, bool> _connected = new();
    private readonly ConcurrentDictionary<Guid, (Guid HostId, string Name, VmPowerState State, long UptimeMs)> _vms = new();

    public ConcurrentQueue<AgentCommand> Received { get; } = new();

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
                var newId = Guid.NewGuid();
                _vms[newId] = (hostId, req.Name, req.StartAfterCreate ? VmPowerState.Running : VmPowerState.Off, 0);
                return Ok(new NewVmResult(newId, req.Name, @"D:\VMs", @"D:\VHDs\" + req.Name + ".vhdx", null), "created");
            case HvOperation.ListCheckpoints:
                return Ok(new[] { new CheckpointInfo(Guid.NewGuid().ToString("D"), "Before update", null, DateTimeOffset.UtcNow, "Production", true) });
            case HvOperation.NewCheckpoint or HvOperation.RestoreCheckpoint or HvOperation.RemoveCheckpoint:
                return Ok(new { id = Guid.NewGuid() }, "done");
            case HvOperation.GetHostDefaults:
                return Ok(new HostDefaults(@"D:\VHDs", @"D:\VMs", 12, 128L << 30, ["12.0"], "12.0"));
            case HvOperation.ListSwitches:
                return Ok(new[] { new SwitchInfo(Guid.NewGuid().ToString("D"), "vSwitch", "External", "NIC", true, false,
                    _vms.Values.Where(x => x.HostId == hostId).Select(x => x.Name).ToList()) });
            case HvOperation.ListHostDisks:
                return Ok(_vms.Where(kv => kv.Value.HostId == hostId)
                    .Select(kv => new HostDiskInfo(@"D:\VHDs\" + kv.Value.Name + ".vhdx", kv.Key, kv.Value.Name, "SCSI 0:0", "VHDX", "Dynamic", 64L << 30, 10L << 30, null)).ToList());
            case HvOperation.ListIsos:
                return Ok(new[] { new IsoFile(@"D:\ISO\win.iso", 5L << 30, DateTimeOffset.UtcNow) });
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
            default:
                return new HvOperationOutcome { Ok = false, Error = "Unsupported operation." };
        }
    }

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

    private static VmSummary Summary(Guid id, (Guid HostId, string Name, VmPowerState State, long UptimeMs) v) =>
        new() { VmId = id, Name = v.Name, PowerState = v.State, UptimeMs = v.UptimeMs };
}
