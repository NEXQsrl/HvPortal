using System.Diagnostics;
using System.Management;
using HvPortal.Contracts.Agent;
using HvPortal.Contracts.HyperV;

namespace HvPortal.Agent.HyperV;

/// <summary>
/// Hyper-V access through the documented WMI v2 provider (root\virtualization\v2).
/// Classes/methods used (see docs/capability-matrix.md):
///   Msvm_VirtualSystemManagementService (+ GetSummaryInformation), Msvm_ComputerSystem (+ RequestStateChange),
///   Msvm_VirtualSystemSettingData, Msvm_ProcessorSettingData, Msvm_MemorySettingData,
///   Msvm_ShutdownComponent (InitiateShutdown, InitiateReboot), Msvm_ConcreteJob.
/// Host facts come from root\cimv2 (Win32_OperatingSystem, Win32_ComputerSystem, Win32_Service).
/// All WQL strings are constants or contain only a GUID formatted by Guid.ToString("D"): no user text is interpolated.
/// </summary>
public sealed class WmiHyperVHost : IHyperVHost
{
    private const string VirtualizationNamespace = @"root\virtualization\v2";
    private const string CimV2Namespace = @"root\cimv2";
    private const uint ReturnCompleted = 0;
    private const uint ReturnJobStarted = 4096;

    // Name, ElementName, CreationTime, Notes, NumberOfProcessors, ProcessorLoad, MemoryUsage, Heartbeat, UpTime, GuestOperatingSystem,
    // HealthState, MemoryAvailable, AvailableMemoryBuffer, ReplicationMode, ReplicationState, ReplicationHealth (GetSummaryInformation docs).
    private static readonly uint[] SummaryRequest = [0, 1, 2, 3, 4, 101, 103, 104, 105, 106, 109, 112, 113, 114, 115, 116];

    private readonly ILogger _logger;
    private readonly string _agentVersion;
    private readonly HostTarget _target;
    private readonly CpuSampler _cpu;

    public WmiHyperVHost(ILogger<WmiHyperVHost> logger) : this(logger, HostTarget.Local)
    {
    }

    /// <summary>Remote target (management agent): every WMI connection goes to that host with its credentials.</summary>
    public WmiHyperVHost(ILogger logger, HostTarget target)
    {
        _logger = logger;
        _target = target;
        _cpu = new CpuSampler(target);
        _agentVersion = typeof(WmiHyperVHost).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    public Task<HostInventory> CollectInventoryAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => CollectInventory(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public Task<VmSummary?> GetVmAsync(Guid vmId, CancellationToken ct) =>
        Task.Run(() =>
        {
            var scope = Connect(VirtualizationNamespace);
            return ReadVm(scope, vmId);
        }, ct);

    public Task<PowerOutcome> ExecutePowerAsync(Guid vmId, VmPowerOperation operation, string reason, TimeSpan timeout,
        IProgress<(int Percent, string Message)> progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => ExecutePower(vmId, operation, reason, timeout, progress, ct), ct,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // ---------------------------------------------------------------- inventory

    private HostInventory CollectInventory(CancellationToken ct)
    {
        var warnings = new List<string>();
        var facts = ReadHostFacts(warnings);
        var (access, message) = CheckAccess(facts);
        facts = AddHostResources(facts, warnings, access == HyperVAccessStatus.Ok);
        if (access != HyperVAccessStatus.Ok)
        {
            return new HostInventory
            {
                CollectedAtUtc = DateTimeOffset.UtcNow,
                Access = access,
                AccessMessage = message,
                Host = facts,
                Warnings = warnings,
                Partial = warnings.Count > 0,
            };
        }

        ct.ThrowIfCancellationRequested();
        var scope = Connect(VirtualizationNamespace);
        var vms = ReadComputerSystems(scope).ToDictionary(v => v.VmId);
        var partial = warnings.Count > 0;

        TryEnrich("settings", () => EnrichWithSettings(scope, vms), warnings, ref partial);
        TryEnrich("summary", () => EnrichWithSummary(scope, vms), warnings, ref partial);
        TryEnrich("shutdown component", () => EnrichWithShutdownComponent(scope, vms), warnings, ref partial);

        return new HostInventory
        {
            CollectedAtUtc = DateTimeOffset.UtcNow,
            Access = HyperVAccessStatus.Ok,
            Host = facts,
            Vms = vms.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Partial = partial,
            Warnings = warnings,
        };
    }

    private void TryEnrich(string what, Action action, List<string> warnings, ref bool partial)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            partial = true;
            warnings.Add($"Could not read {what}: {ex.Message}");
            _logger.LogWarning(ex, "Partial inventory: could not read {What}", what);
        }
    }

    private (HyperVAccessStatus, string?) CheckAccess(HostFacts facts)
    {
        if (!_target.IsLocal && RemoteReachability() is { } unreachable)
        {
            // A remote host whose facts cannot be read is unreachable or refuses the credentials, not "Hyper-V not installed".
            return unreachable;
        }
        if (facts.HyperVServiceState is null)
        {
            return (HyperVAccessStatus.NotInstalled, "The Hyper-V Virtual Machine Management service (vmms) is not installed.");
        }
        if (!string.Equals(facts.HyperVServiceState, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return (HyperVAccessStatus.ServiceStopped, $"The vmms service is in state '{facts.HyperVServiceState}'.");
        }
        try
        {
            var scope = Connect(VirtualizationNamespace);
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Name FROM Msvm_VirtualSystemManagementService"), Enumeration());
            using var results = searcher.Get();
            if (results.Count == 0)
            {
                return (HyperVAccessStatus.AccessDenied,
                    "root\\virtualization\\v2 returned no Msvm_VirtualSystemManagementService instance. " +
                    (_target.IsLocal
                        ? "The agent identity is probably not a member of 'Hyper-V Administrators' (S-1-5-32-578)."
                        : $"The account {_target.UserName} is probably not an administrator of {_target.Display}."));
            }
            return (HyperVAccessStatus.Ok, null);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidNamespace)
        {
            return (HyperVAccessStatus.NotInstalled, "The root\\virtualization\\v2 WMI namespace does not exist.");
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return (HyperVAccessStatus.AccessDenied, AccessDeniedMessage());
        }
        catch (UnauthorizedAccessException)
        {
            return (HyperVAccessStatus.AccessDenied, AccessDeniedMessage());
        }
        catch (ManagementException ex)
        {
            return (HyperVAccessStatus.Error, $"WMI error: {ex.ErrorCode}");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            return (HyperVAccessStatus.Error, $"Cannot reach {_target.Display} over WMI/DCOM: {ex.Message}");
        }
    }

    /// <summary>Remote targets only: null when root\cimv2 accepts the connection, otherwise the reason.</summary>
    private (HyperVAccessStatus, string?)? RemoteReachability()
    {
        try
        {
            Connect(CimV2Namespace);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is ManagementException me && me.ErrorCode == ManagementStatus.AccessDenied))
        {
            return (HyperVAccessStatus.AccessDenied, AccessDeniedMessage());
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            return (HyperVAccessStatus.Error, $"Cannot reach {_target.Display} over WMI/DCOM (TCP 135 and dynamic RPC ports): {ex.Message}");
        }
    }

    private string AccessDeniedMessage() => _target.IsLocal
        ? "Access denied to root\\virtualization\\v2."
        : $"Access denied by {_target.Display} for {_target.UserName}: check user name and password and that the account is a local "
          + "administrator of the host; for local accounts other than the built-in Administrator see LocalAccountTokenFilterPolicy in docs/install.md.";

    private HostFacts ReadHostFacts(List<string> warnings)
    {
        string computerName = _target.ComputerName ?? Environment.MachineName;
        string? dns = null, domain = null, caption = null, version = null, build = null, vmmsState = null;
        bool partOfDomain = false, clusterPresent = false, clusterRunning = false;
        uint? sku = null, productType = null;
        int? lp = null;
        long? totalMem = null, freeMem = null;
        try
        {
            var scope = Connect(CimV2Namespace);
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                       "SELECT Caption, Version, BuildNumber, OperatingSystemSKU, ProductType, FreePhysicalMemory FROM Win32_OperatingSystem"), Enumeration()))
            using (var r = s.Get())
            {
                foreach (ManagementBaseObject o in r)
                {
                    using (o)
                    {
                        caption = o["Caption"] as string;
                        version = o["Version"] as string;
                        build = o["BuildNumber"] as string;
                        sku = ToUInt(o["OperatingSystemSKU"]);
                        productType = ToUInt(o["ProductType"]);
                        freeMem = o["FreePhysicalMemory"] is ulong kb ? (long)kb * 1024 : null;
                    }
                }
            }
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                       "SELECT Name, DNSHostName, Domain, PartOfDomain, TotalPhysicalMemory, NumberOfLogicalProcessors FROM Win32_ComputerSystem"), Enumeration()))
            using (var r = s.Get())
            {
                foreach (ManagementBaseObject o in r)
                {
                    using (o)
                    {
                        computerName = o["Name"] as string ?? computerName;
                        dns = o["DNSHostName"] as string;
                        domain = o["Domain"] as string;
                        partOfDomain = o["PartOfDomain"] is true;
                        totalMem = o["TotalPhysicalMemory"] is ulong tm ? (long)tm : null;
                        lp = o["NumberOfLogicalProcessors"] is uint n ? (int)n : null;
                    }
                }
            }
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                       "SELECT Name, State FROM Win32_Service WHERE Name = 'vmms' OR Name = 'ClusSvc'"), Enumeration()))
            using (var r = s.Get())
            {
                foreach (ManagementBaseObject o in r)
                {
                    using (o)
                    {
                        var name = o["Name"] as string;
                        if (string.Equals(name, "vmms", StringComparison.OrdinalIgnoreCase))
                        {
                            vmmsState = o["State"] as string ?? "Unknown";
                        }
                        else if (string.Equals(name, "ClusSvc", StringComparison.OrdinalIgnoreCase))
                        {
                            clusterPresent = true;
                            clusterRunning = string.Equals(o["State"] as string, "Running", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            warnings.Add($"Could not read host facts: {ex.Message}");
        }

        return new HostFacts
        {
            ComputerName = computerName,
            DnsHostName = dns,
            Domain = domain,
            PartOfDomain = partOfDomain,
            OsCaption = caption,
            OsVersion = version,
            OsBuildNumber = build,
            OsSku = sku,
            ProductType = productType,
            LogicalProcessorCount = lp,
            TotalPhysicalMemoryBytes = totalMem,
            FreePhysicalMemoryBytes = freeMem,
            HyperVServiceState = vmmsState,
            ClusterServicePresent = clusterPresent,
            // Protocol 11: only for the host the agent runs on (remote hosts of a management agent are not cluster-aware).
            Cluster = clusterRunning && _target.IsLocal ? ClusterReader.Read(computerName, Connect, _logger) : null,
            AgentVersion = _agentVersion,
        };
    }

    private HostFacts AddHostResources(HostFacts facts, List<string> warnings, bool hyperVAccessible)
    {
        string? cpuName = null;
        int sockets = 0, cores = 0, loadSum = 0, loadCount = 0;
        DateTimeOffset? lastBoot = null;
        var volumes = new List<HostVolume>();
        var adapters = new List<NetworkAdapterInfo>();
        var switches = new List<string>();
        try
        {
            var scope = Connect(CimV2Namespace);
            foreach (var o in Query(scope, "SELECT Name, NumberOfCores, LoadPercentage FROM Win32_Processor"))
            {
                cpuName ??= (o["Name"] as string)?.Trim();
                sockets++;
                cores += o["NumberOfCores"] is uint c ? (int)c : 0;
                if (o["LoadPercentage"] is ushort l)
                {
                    loadSum += l;
                    loadCount++;
                }
            }
            foreach (var o in Query(scope, "SELECT LastBootUpTime FROM Win32_OperatingSystem"))
            {
                lastBoot = ParseDmtf(o["LastBootUpTime"] as string);
            }
            foreach (var o in Query(scope, "SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3"))
            {
                volumes.Add(new HostVolume(o["DeviceID"] as string ?? "?", o["VolumeName"] as string, o["FileSystem"] as string,
                    o["Size"] is ulong sz ? (long)sz : null, o["FreeSpace"] is ulong fr ? (long)fr : null));
            }
            foreach (var o in Query(scope, "SELECT Description, MACAddress, IPAddress, IPSubnet, DefaultIPGateway, DNSServerSearchOrder, DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
            {
                adapters.Add(new NetworkAdapterInfo
                {
                    Name = o["Description"] as string ?? "?",
                    MacAddress = o["MACAddress"] as string,
                    IpAddresses = o["IPAddress"] as string[] ?? [],
                    Subnets = o["IPSubnet"] as string[] ?? [],
                    Gateways = o["DefaultIPGateway"] as string[] ?? [],
                    DnsServers = o["DNSServerSearchOrder"] as string[] ?? [],
                    DhcpEnabled = o["DHCPEnabled"] as bool?,
                });
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            warnings.Add($"Could not read host resources: {ex.Message}");
        }
        if (hyperVAccessible)
        {
            try
            {
                var v2 = Connect(VirtualizationNamespace);
                foreach (var o in Query(v2, "SELECT ElementName FROM Msvm_VirtualEthernetSwitch"))
                {
                    switches.Add(o["ElementName"] as string ?? "?");
                }
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not read virtual switches: {ex.Message}");
            }
        }
        var (cpuLoad, cpuSource) = _cpu.Sample();
        if (cpuLoad is null && loadCount > 0)
        {
            (cpuLoad, cpuSource) = (loadSum / loadCount, "root-partition");
        }
        return facts with
        {
            CpuName = cpuName,
            CpuSockets = sockets > 0 ? sockets : null,
            CpuCores = cores > 0 ? cores : null,
            CpuLoadPercent = cpuLoad,
            CpuLoadSource = cpuSource,
            LastBootUtc = lastBoot,
            Volumes = volumes.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            NetworkAdapters = adapters,
            VirtualSwitches = switches.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            // Protocol 9: what the portal may use on this host (decided on the host, or in the portal for remote hosts).
            StorageFolders = _target.IsLocal ? LocalStorageFolders.Folders : _target.StorageFolders,
        };
    }

    /// <summary>Runs a WQL query and materializes the results (small result sets).</summary>
    private static List<ManagementBaseObject> Query(ManagementScope scope, string wql)
    {
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery(wql), Enumeration());
        using var r = s.Get();
        return Materialize(r);
    }

    /// <summary>
    /// Enumerates a WMI collection exactly once with foreach. Do NOT use LINQ (Cast/ToList/FirstOrDefault) on
    /// ManagementObjectCollection: recent .NET LINQ reads ICollection.Count first, which consumes a forward-only
    /// (Rewindable = false) enumeration and leaves the collection empty.
    /// </summary>
    internal static List<ManagementBaseObject> Materialize(ManagementObjectCollection collection)
    {
        var list = new List<ManagementBaseObject>();
        foreach (ManagementBaseObject o in collection)
        {
            list.Add(o);
        }
        return list;
    }

    private static ManagementObject? FirstObject(ManagementObjectCollection collection)
    {
        foreach (ManagementBaseObject o in collection)
        {
            return o as ManagementObject;
        }
        return null;
    }

    // ---------------------------------------------------------------- VM details (on demand)

    private static readonly System.Text.RegularExpressions.Regex SafePath = new(@"^[^\x00-\x1f""<>|]{1,1024}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public Task<VmDetails?> GetVmDetailsAsync(Guid vmId, CancellationToken ct) =>
        Task.Factory.StartNew(() => ReadVmDetails(vmId), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private VmDetails? ReadVmDetails(Guid vmId)
    {
        var scope = Connect(VirtualizationNamespace);
        var vssd = Query(scope, $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmId:D}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'")
            .OfType<ManagementObject>().FirstOrDefault();
        if (vssd is null)
        {
            return null;
        }
        var warnings = new List<string>();
        var disks = new List<VmDisk>();
        var nics = new List<NetworkAdapterInfo>();
        ManagementObject? imageService = null;
        try
        {
            imageService = Query(scope, "SELECT * FROM Msvm_ImageManagementService").OfType<ManagementObject>().FirstOrDefault();
            foreach (ManagementObject sasd in vssd.GetRelated("Msvm_StorageAllocationSettingData"))
            {
                var resourceType = sasd["ResourceType"] is ushort rt ? rt : (ushort)0;
                var path = (sasd["HostResource"] as string[])?.FirstOrDefault();
                var (controller, location) = DescribeLocation(scope, sasd["Parent"] as string);
                var disk = new VmDisk
                {
                    Kind = resourceType switch { 31 => "VirtualHardDisk", 16 => "DvdIso", _ => "Other" },
                    Path = path,
                    Controller = controller,
                    Location = location,
                };
                if (resourceType == 31 && path is not null && SafePath.IsMatch(path))
                {
                    disk = EnrichDisk(imageService, disk, path, warnings);
                }
                disks.Add(disk);
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            warnings.Add("Could not read disks: " + ex.Message);
        }
        finally
        {
            imageService?.Dispose();
        }

        try
        {
            foreach (var cls in new[] { "Msvm_SyntheticEthernetPortSettingData", "Msvm_EmulatedEthernetPortSettingData" })
            {
                foreach (ManagementObject port in vssd.GetRelated(cls))
                {
                    string? switchName = null;
                    foreach (ManagementObject alloc in port.GetRelated("Msvm_EthernetPortAllocationSettingData"))
                    {
                        var sw = (alloc["HostResource"] as string[])?.FirstOrDefault();
                        var disabled = alloc["EnabledState"] is ushort es && es == 3; // 3 = connection disabled
                        if (sw is not null && !disabled)
                        {
                            try
                            {
                                using var swObj = new ManagementObject(scope, new ManagementPath(sw), null);
                                swObj.Get();
                                switchName = swObj["ElementName"] as string;
                            }
                            catch (ManagementException)
                            {
                                switchName = "?";
                            }
                        }
                    }
                    using var guestCollection = port.GetRelated("Msvm_GuestNetworkAdapterConfiguration");
                    var guest = FirstObject(guestCollection);
                    nics.Add(new NetworkAdapterInfo
                    {
                        Name = port["ElementName"] as string ?? (cls.Contains("Emulated", StringComparison.Ordinal) ? "Legacy adapter" : "Network adapter"),
                        MacAddress = FormatMac(port["Address"] as string),
                        SwitchName = switchName,
                        IpAddresses = guest?["IPAddresses"] as string[] ?? [],
                        Subnets = guest?["Subnets"] as string[] ?? [],
                        Gateways = guest?["DefaultGateways"] as string[] ?? [],
                        DnsServers = guest?["DNSServers"] as string[] ?? [],
                        DhcpEnabled = guest?["DHCPEnabled"] as bool?,
                    });
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            warnings.Add("Could not read network adapters: " + ex.Message);
        }
        return new VmDetails { VmId = vmId, CollectedAtUtc = DateTimeOffset.UtcNow, Disks = disks, NetworkAdapters = nics, Warnings = warnings };
    }

    private static (string? Controller, string? Location) DescribeLocation(ManagementScope scope, string? drivePath)
    {
        if (drivePath is null)
        {
            return (null, null);
        }
        try
        {
            using var drive = new ManagementObject(scope, new ManagementPath(drivePath), null);
            drive.Get();
            var location = drive["AddressOnParent"] as string;
            string? controller = null;
            if (drive["Parent"] is string ctrlPath)
            {
                using var ctrl = new ManagementObject(scope, new ManagementPath(ctrlPath), null);
                ctrl.Get();
                controller = (ctrl["ResourceSubType"] as string) switch
                {
                    "Microsoft:Hyper-V:Synthetic SCSI Controller" => "SCSI",
                    "Microsoft:Hyper-V:Emulated IDE Controller" => "IDE",
                    var other => other,
                } + (ctrl["Address"] is string a && a.Length > 0 ? " " + a : "");
            }
            return (controller, location);
        }
        catch (ManagementException)
        {
            return (null, null);
        }
    }

    private static VmDisk EnrichDisk(ManagementObject? imageService, VmDisk disk, string path, List<string> warnings)
    {
        long? fileSize = null;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                fileSize = fi.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        disk = disk with { FileSizeBytes = fileSize };
        if (imageService is null)
        {
            return disk;
        }
        try
        {
            using var inParams = imageService.GetMethodParameters("GetVirtualHardDiskSettingData");
            inParams["Path"] = path;
            using var outParams = imageService.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null);
            if (ToUInt(outParams["ReturnValue"]) == ReturnCompleted && outParams["SettingData"] is string xml)
            {
                var props = ParseEmbeddedInstance(xml);
                return disk with
                {
                    MaxSizeBytes = props.TryGetValue("MaxInternalSize", out var max) && long.TryParse(max, out var m) ? m : null,
                    Format = props.GetValueOrDefault("Format") switch { "2" => "VHD", "3" => "VHDX", "4" => "VHDSet", var f => f },
                    DiskType = props.GetValueOrDefault("Type") switch { "2" => "Fixed", "3" => "Dynamic", "4" => "Differencing", var t => t },
                    ParentPath = props.GetValueOrDefault("ParentPath") is { Length: > 0 } p ? p : null,
                };
            }
        }
        catch (ManagementException ex)
        {
            warnings.Add($"Disk {Path.GetFileName(path)}: {ex.Message}");
        }
        return disk;
    }

    /// <summary>Parses a CIM-XML embedded instance (DTD 2.0) into property name -> value. DTD processing disabled.</summary>
    internal static Dictionary<string, string> ParseEmbeddedInstance(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 1_000_000 };
        using var reader = System.Xml.XmlReader.Create(new StringReader(xml), settings);
        var doc = System.Xml.Linq.XDocument.Load(reader);
        foreach (var p in doc.Descendants("PROPERTY"))
        {
            if (p.Attribute("NAME")?.Value is { } name && p.Element("VALUE")?.Value is { } value)
            {
                result[name] = value;
            }
        }
        return result;
    }

    private static string? FormatMac(string? raw) =>
        raw is { Length: 12 } ? string.Join('-', Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2))) : raw;

    // ---------------------------------------------------------------- VM delete

    public Task<PowerOutcome> DeleteVmAsync(Guid vmId, TimeSpan timeout, IProgress<(int Percent, string Message)> progress, CancellationToken ct) =>
        Task.Factory.StartNew(() =>
        {
            var elapsed = Stopwatch.StartNew();
            var scope = Connect(VirtualizationNamespace);
            using var vm = FindVmObject(scope, vmId);
            if (vm is null)
            {
                return new PowerOutcome(AgentCommandStatus.AlreadyInState, "VM not present on the host (already deleted).", null, null);
            }
            var state = FromComputerSystem(vm, vmId).PowerState;
            if (state is not (VmPowerState.Off or VmPowerState.Saved))
            {
                return new PowerOutcome(AgentCommandStatus.InvalidState, $"The VM must be Off or Saved to be deleted (it is {state}).", null, null);
            }
            using var vsms = GetSingleton(scope, "Msvm_VirtualSystemManagementService");
            using var inParams = vsms.GetMethodParameters("DestroySystem");
            inParams["AffectedSystem"] = vm.Path.Path;
            using var outParams = vsms.InvokeMethod("DestroySystem", inParams, null);
            var rv = ToUInt(outParams["ReturnValue"]) ?? uint.MaxValue;
            if (rv == ReturnJobStarted && outParams["Job"] is string job)
            {
                if (WaitForJob(scope, job, elapsed, timeout, progress, ct) is { } failed)
                {
                    return failed;
                }
            }
            else if (rv != ReturnCompleted)
            {
                // DestroySystem uses the CIM generic codes: 1 not supported, 2 failed, 3 timeout, 4 invalid parameter, 5 invalid state.
                var status = rv switch { 1 => AgentCommandStatus.NotSupported, 5 => AgentCommandStatus.InvalidState, 3 => AgentCommandStatus.TimedOut, _ => AgentCommandStatus.Failed };
                return new PowerOutcome(status, $"DestroySystem returned {rv}.", rv, null);
            }
            return ReadComputerSystems(scope, vmId).Count == 0
                ? new PowerOutcome(AgentCommandStatus.Succeeded, "VM configuration deleted. Disk files were not deleted.", 0, null)
                : new PowerOutcome(AgentCommandStatus.Unverified, "DestroySystem completed but the VM is still listed.", rv, null);
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static List<VmSummary> ReadComputerSystems(ManagementScope scope, Guid? only = null)
    {
        var query = only is { } id
            ? $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{id:D}'"
            : "SELECT * FROM Msvm_ComputerSystem";
        var list = new List<VmSummary>();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query), Enumeration());
        using var results = searcher.Get();
        foreach (ManagementBaseObject o in results)
        {
            using (o)
            {
                // The host itself is also an Msvm_ComputerSystem; its Name is the NetBIOS name, not a GUID.
                if (o["Name"] is not string name || !Guid.TryParse(name, out var vmId))
                {
                    continue;
                }
                list.Add(FromComputerSystem(o, vmId));
            }
        }
        return list;
    }

    internal static VmSummary FromComputerSystem(ManagementBaseObject o, Guid vmId)
    {
        var enabled = o["EnabledState"] is ushort es ? es : (ushort)0;
        var state = VmPowerStateMapper.Map(enabled, out var critical);
        return new VmSummary
        {
            VmId = vmId,
            Name = o["ElementName"] as string ?? vmId.ToString("D"),
            PowerState = state,
            EnabledStateRaw = enabled,
            IsCritical = critical,
            HealthState = o["HealthState"] as ushort?,
            OperationalStatus = o["OperationalStatus"] as ushort[] ?? [],
            UptimeMs = o["OnTimeInMilliseconds"] is ulong ms ? (long)ms : null,
            TimeOfLastStateChangeUtc = ParseDmtf(o["TimeOfLastStateChange"] as string),
            TimeOfLastConfigurationChangeUtc = ParseDmtf(o["TimeOfLastConfigurationChange"] as string),
        };
    }

    private static void EnrichWithSettings(ManagementScope scope, Dictionary<Guid, VmSummary> vms)
    {
        // One query for all setting data: the realized one gives generation/version, snapshot ones are counted as checkpoints.
        var checkpoints = vms.Keys.ToDictionary(k => k, _ => 0);
        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                   "SELECT VirtualSystemIdentifier, VirtualSystemType, VirtualSystemSubType, Version, ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData"), Enumeration()))
        using (var r = s.Get())
        {
            foreach (ManagementBaseObject o in r)
            {
                using (o)
                {
                    if (o["VirtualSystemIdentifier"] is not string vsi || !Guid.TryParse(vsi, out var id) || !vms.TryGetValue(id, out var vm))
                    {
                        continue;
                    }
                    var type = o["VirtualSystemType"] as string;
                    if (type == "Microsoft:Hyper-V:System:Realized")
                    {
                        vms[id] = vm with
                        {
                            Generation = ParseGeneration(o["VirtualSystemSubType"] as string),
                            ConfigurationVersion = o["Version"] as string,
                            ConfigurationPath = o["ConfigurationDataRoot"] as string,
                        };
                    }
                    else if (type == "Microsoft:Hyper-V:Snapshot:Realized")
                    {
                        checkpoints[id]++;
                    }
                }
            }
        }
        foreach (var (id, count) in checkpoints)
        {
            vms[id] = vms[id] with { CheckpointCount = count };
        }

        // Realized RASD InstanceIDs have the form "Microsoft:<VM GUID>\...". Snapshot settings use the snapshot GUID,
        // pool/default settings use non-GUID tokens, so they are filtered out by the dictionary lookup.
        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                   "SELECT InstanceID, VirtualQuantity FROM Msvm_ProcessorSettingData"), Enumeration()))
        using (var r = s.Get())
        {
            foreach (ManagementBaseObject o in r)
            {
                using (o)
                {
                    if (TryParseOwner(o["InstanceID"] as string, out var id) && vms.TryGetValue(id, out var vm) && o["VirtualQuantity"] is ulong q)
                    {
                        vms[id] = vm with { ProcessorCount = (int)q };
                    }
                }
            }
        }

        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery(
                   "SELECT InstanceID, VirtualQuantity, DynamicMemoryEnabled, Limit FROM Msvm_MemorySettingData"), Enumeration()))
        using (var r = s.Get())
        {
            foreach (ManagementBaseObject o in r)
            {
                using (o)
                {
                    if (TryParseOwner(o["InstanceID"] as string, out var id) && vms.TryGetValue(id, out var vm))
                    {
                        vms[id] = vm with
                        {
                            MemoryStartupMb = o["VirtualQuantity"] is ulong mb ? (long)mb : vm.MemoryStartupMb,
                            DynamicMemoryEnabled = o["DynamicMemoryEnabled"] as bool?,
                            MemoryMaximumMb = o["Limit"] is ulong limit && limit <= long.MaxValue ? (long)limit : vm.MemoryMaximumMb,
                        };
                    }
                }
            }
        }
    }

    private static void EnrichWithSummary(ManagementScope scope, Dictionary<Guid, VmSummary> vms)
    {
        using var vsms = GetSingleton(scope, "Msvm_VirtualSystemManagementService");
        using var inParams = vsms.GetMethodParameters("GetSummaryInformation");
        inParams["RequestedInformation"] = SummaryRequest;
        // SettingData == null: documented as "information for all virtual machines".
        using var outParams = vsms.InvokeMethod("GetSummaryInformation", inParams, null);
        var rv = ToUInt(outParams["ReturnValue"]) ?? uint.MaxValue;
        if (rv != ReturnCompleted)
        {
            throw new ManagementException($"GetSummaryInformation returned {rv}.");
        }
        if (outParams["SummaryInformation"] is not ManagementBaseObject[] items)
        {
            return;
        }
        foreach (var si in items)
        {
            using (si)
            {
                if (si["Name"] is not string n || !Guid.TryParse(n, out var id) || !vms.TryGetValue(id, out var vm))
                {
                    continue;
                }
                vms[id] = vm with
                {
                    ProcessorCount = si["NumberOfProcessors"] is ushort np ? np : vm.ProcessorCount,
                    ProcessorLoadPercent = si["ProcessorLoad"] as ushort?,
                    MemoryAssignedMb = si["MemoryUsage"] is ulong mu ? (long)mu : null,
                    Heartbeat = si["Heartbeat"] as ushort?,
                    UptimeMs = si["UpTime"] is ulong up ? (long)up : vm.UptimeMs,
                    GuestOperatingSystem = si["GuestOperatingSystem"] as string,
                    HealthState = si["HealthState"] as ushort? ?? vm.HealthState,
                    CreatedUtc = ParseDmtf(si["CreationTime"] as string) ?? vm.CreatedUtc,
                    Notes = si["Notes"] is string notes ? (notes.Length > 4000 ? notes[..4000] : notes) : null,
                    // Valid only with dynamic memory (documented); null otherwise.
                    MemoryAvailablePercent = vm.DynamicMemoryEnabled == true && si["MemoryAvailable"] is int ma ? ma : null,
                    MemoryBufferPercent = vm.DynamicMemoryEnabled == true && si["AvailableMemoryBuffer"] is int mb ? mb : null,
                    ReplicationMode = si["ReplicationMode"] as ushort?,
                    ReplicationState = si["ReplicationState"] as ushort?,
                    ReplicationHealth = si["ReplicationHealth"] as ushort?,
                };
            }
        }
    }

    private static void EnrichWithShutdownComponent(ManagementScope scope, Dictionary<Guid, VmSummary> vms)
    {
        using var s = new ManagementObjectSearcher(scope, new ObjectQuery(
            "SELECT SystemName, OperationalStatus FROM Msvm_ShutdownComponent"), Enumeration());
        using var r = s.Get();
        foreach (ManagementBaseObject o in r)
        {
            using (o)
            {
                if (o["SystemName"] is string sn && Guid.TryParse(sn, out var id) && vms.TryGetValue(id, out var vm))
                {
                    vms[id] = vm with { ShutdownServiceAvailable = o["OperationalStatus"] is ushort[] { Length: > 0 } st && st[0] == 2 };
                }
            }
        }
    }

    private static VmSummary? ReadVm(ManagementScope scope, Guid vmId)
    {
        var list = ReadComputerSystems(scope, vmId);
        if (list.Count == 0)
        {
            return null;
        }
        var dict = new Dictionary<Guid, VmSummary> { [vmId] = list[0] };
        try { EnrichWithSettings(scope, dict); } catch (ManagementException) { }
        try { EnrichWithShutdownComponent(scope, dict); } catch (ManagementException) { }
        return dict[vmId];
    }

    // ---------------------------------------------------------------- power operations

    private PowerOutcome ExecutePower(Guid vmId, VmPowerOperation op, string reason, TimeSpan timeout,
        IProgress<(int, string)> progress, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        ManagementScope scope;
        try
        {
            scope = Connect(VirtualizationNamespace);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            return new PowerOutcome(AgentCommandStatus.AccessDenied, "Cannot connect to root\\virtualization\\v2: " + ex.Message, null, null);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Nothing was sent to the host: a definite failure, not an unknown outcome.
            return new PowerOutcome(AgentCommandStatus.Failed, $"Cannot reach {_target.Display}: {ex.Message}", null, null);
        }

        using var vm = FindVmObject(scope, vmId);
        if (vm is null)
        {
            return new PowerOutcome(AgentCommandStatus.NotFound, "VM not found on this host (or not visible to the agent identity).", null, null);
        }

        var before = FromComputerSystem(vm, vmId);
        if (VmPowerRules.IsAlreadyInTargetState(op, before.PowerState))
        {
            return new PowerOutcome(AgentCommandStatus.AlreadyInState, $"VM already {before.PowerState}.", null, before);
        }
        if (!VmPowerRules.IsAllowed(op, before.PowerState))
        {
            return new PowerOutcome(AgentCommandStatus.InvalidState, $"{op} is not allowed while the VM is {before.PowerState}.", null, before);
        }

        var startedAt = Stopwatch.GetTimestamp();
        uint rv;
        string? jobPath = null;
        try
        {
            if (VmPowerRules.RequestedStateFor(op) is { } requested)
            {
                using var inParams = vm.GetMethodParameters("RequestStateChange");
                inParams["RequestedState"] = requested;
                using var outParams = vm.InvokeMethod("RequestStateChange", inParams, null);
                rv = ToUInt(outParams["ReturnValue"]) ?? uint.MaxValue;
                jobPath = outParams["Job"] as string;
            }
            else
            {
                using var shutdown = FindShutdownComponent(scope, vmId);
                if (shutdown is null)
                {
                    return new PowerOutcome(AgentCommandStatus.NotSupported,
                        "The guest shutdown integration service is not available for this VM.", null, before);
                }
                var method = op == VmPowerOperation.Restart ? "InitiateReboot" : "InitiateShutdown";
                using var inParams = shutdown.GetMethodParameters(method);
                // Force=false: do not close guest applications with unsaved data.
                inParams["Force"] = false;
                inParams["Reason"] = reason;
                using var outParams = shutdown.InvokeMethod(method, inParams, null);
                rv = ToUInt(outParams["ReturnValue"]) ?? uint.MaxValue;
                jobPath = outParams.Properties.Cast<PropertyData>().Any(p => p.Name == "Job") ? outParams["Job"] as string : null;
            }
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new PowerOutcome(AgentCommandStatus.AccessDenied, "Access denied by Hyper-V.", null, before);
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.MethodNotImplemented or ManagementStatus.InvalidMethod)
        {
            return new PowerOutcome(AgentCommandStatus.NotSupported, $"{op} is not supported by this Hyper-V version.", null, before);
        }

        if (rv == ReturnJobStarted && jobPath is not null)
        {
            var jobOutcome = WaitForJob(scope, jobPath, deadline, timeout, progress, ct);
            if (jobOutcome is not null)
            {
                return jobOutcome with { Vm = ReadVm(scope, vmId) };
            }
        }
        else if (rv != ReturnCompleted)
        {
            return new PowerOutcome(MapReturnCode(rv), DescribeReturnCode(rv), rv, ReadVm(scope, vmId));
        }

        progress.Report((90, "Waiting for the VM to reach the expected state"));
        return WaitForEndState(scope, vmId, op, startedAt, deadline, timeout, ct);
    }

    private PowerOutcome? WaitForJob(ManagementScope scope, string jobPath, Stopwatch elapsed, TimeSpan timeout,
        IProgress<(int, string)> progress, CancellationToken ct)
    {
        using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);
        while (elapsed.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            job.Get();
            var state = job["JobState"] is ushort js ? js : (ushort)0;
            var pct = job["PercentComplete"] is ushort p ? p : (ushort)0;
            progress.Report((Math.Min((int)pct, 89), $"Hyper-V job state {state}"));
            switch (state)
            {
                case 7: // Completed
                    return null;
                case >= 8 and <= 10: // Terminated, Killed, Exception
                    var code = ToUInt(job["ErrorCode"]);
                    var desc = job["ErrorDescription"] as string ?? "Hyper-V job failed.";
                    return new PowerOutcome(AgentCommandStatus.Failed, desc, code, null);
            }
            Thread.Sleep(500);
        }
        return new PowerOutcome(AgentCommandStatus.TimedOut, "The Hyper-V job did not complete within the timeout.", null, null);
    }

    private PowerOutcome WaitForEndState(ManagementScope scope, Guid vmId, VmPowerOperation op, long startedAt,
        Stopwatch elapsed, TimeSpan timeout, CancellationToken ct)
    {
        var expected = VmPowerRules.ExpectedState(op);
        VmSummary? last = null;
        while (elapsed.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            last = ReadVm(scope, vmId);
            if (last is null)
            {
                return new PowerOutcome(AgentCommandStatus.NotFound, "VM disappeared during the operation.", null, null);
            }
            if (last.PowerState == expected)
            {
                if (!VmPowerRules.RequiresUptimeProof(op))
                {
                    return new PowerOutcome(AgentCommandStatus.Succeeded, $"VM is {expected}.", 0, last);
                }
                var sinceStartMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                if (last.UptimeMs is { } up && up < sinceStartMs)
                {
                    return new PowerOutcome(AgentCommandStatus.Succeeded, "VM restarted (uptime reset observed).", 0, last);
                }
            }
            Thread.Sleep(1000);
        }
        return new PowerOutcome(AgentCommandStatus.Unverified,
            $"Hyper-V accepted the request, but the VM did not reach '{expected}' within {timeout.TotalSeconds:0}s.", null, last);
    }

    private static ManagementObject? FindVmObject(ManagementScope scope, Guid vmId)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'"), Enumeration());
        using var r = searcher.Get();
        return FirstObject(r);
    }

    private static ManagementObject? FindShutdownComponent(ManagementScope scope, Guid vmId)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery($"SELECT * FROM Msvm_ShutdownComponent WHERE SystemName = '{vmId:D}'"), Enumeration());
        using var r = searcher.Get();
        return FirstObject(r);
    }

    // ---------------------------------------------------------------- helpers

    internal static AgentCommandStatus MapReturnCode(uint rv) => rv switch
    {
        0 => AgentCommandStatus.Succeeded,
        32769 => AgentCommandStatus.AccessDenied,
        32770 => AgentCommandStatus.NotSupported,
        32775 => AgentCommandStatus.InvalidState,
        32772 => AgentCommandStatus.TimedOut,
        _ => AgentCommandStatus.Failed,
    };

    internal static string DescribeReturnCode(uint rv) => rv switch
    {
        32768 => "Failed (32768).",
        32769 => "Access denied (32769).",
        32770 => "Not supported (32770).",
        32771 => "Status is unknown (32771).",
        32772 => "Timeout (32772).",
        32773 => "Invalid parameter (32773).",
        32774 => "System is in use (32774).",
        32775 => "Invalid state for this operation (32775).",
        32776 => "Incorrect data type (32776).",
        32777 => "System is not available (32777).",
        32778 => "Out of memory (32778).",
        32779 => "File not found (32779).",
        32780 => "The system is not ready (32780).",
        32781 => "The guest is locked and cannot be shut down without the force option (32781).",
        32782 => "A system shutdown is already in progress (32782).",
        _ => $"Hyper-V returned {rv}.",
    };

    internal static int? ParseGeneration(string? subType) => subType switch
    {
        "Microsoft:Hyper-V:SubType:1" => 1,
        "Microsoft:Hyper-V:SubType:2" => 2,
        _ => null,
    };

    internal static bool TryParseOwner(string? instanceId, out Guid vmId)
    {
        vmId = Guid.Empty;
        const string prefix = "Microsoft:";
        if (instanceId is null || !instanceId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var rest = instanceId.AsSpan(prefix.Length);
        var slash = rest.IndexOf('\\');
        var token = slash >= 0 ? rest[..slash] : rest;
        return Guid.TryParse(token, out vmId);
    }

    private static DateTimeOffset? ParseDmtf(string? dmtf)
    {
        if (string.IsNullOrEmpty(dmtf))
        {
            return null;
        }
        try
        {
            var local = ManagementDateTimeConverter.ToDateTime(dmtf);
            return local.Year < 1700 ? null : new DateTimeOffset(local).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static uint? ToUInt(object? value) => value switch
    {
        uint u => u,
        ushort us => us,
        int i when i >= 0 => (uint)i,
        _ => null,
    };

    private static ManagementObject GetSingleton(ManagementScope scope, string className)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {className}"), Enumeration());
        using var r = searcher.Get();
        return FirstObject(r)
               ?? throw new ManagementException($"{className} not found (insufficient rights?).");
    }

    private ManagementScope Connect(string ns) => _target.ConnectWmi(ns);

    private static System.Management.EnumerationOptions Enumeration() => new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        ReturnImmediately = false,
        Rewindable = false,
    };
}
