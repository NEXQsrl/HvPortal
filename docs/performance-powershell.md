# PowerShell operations: cost, concurrency, what to move to in-process code (0.9.4)

HvPortal runs every typed management operation as **one Windows PowerShell 5.1 process** with the fixed script
`Ops\HvPortal.Ops.ps1` and a JSON request on stdin (ADR-012). That is a security decision (closed set of operations, no
caller data on a command line, a crash or a leak of one operation cannot touch the agent), not an accident. This page
says what it costs, how the agent now limits and measures it (ADR-046), and which operations are candidates for
in-process code. Numbers are measurements; where nothing was measured, it says so.

## Which operations use what

| How | Operations | Notes |
|---|---|---|
| **WMI in-process** (`System.Management`, `root\virtualization\v2`, `root\cimv2`, `root\MSCluster`) | inventory (hosts, VMs, state, uptime, heartbeat, memory), `GetVm`, `GetVmDetails` (disks, adapters, guest IPs), power operations, `DeleteVm` without disks, console frames and input, CPU load, cluster facts of the node in every inventory | no process is started; the most frequent calls (inventory every 30 s, console frames) are already here |
| **File system in-process** | `BrowseFolders`, `ListFolder`, `ReadFile`, `WriteFile*`, `NewFolder`, `RenamePath`, `DeletePath`, `ListBackupFiles`, `ReadBackupFile` | no process is started |
| **PowerShell script** (one `powershell.exe` each) | reads: `GetVmConfig`, `GetHostDefaults`, `ListIsos`, `ListCheckpoints`, `ListSwitches`, `ListHostDisks`, `ListVmGroups`, `ListNetAdapters`, `GetHostHealth`, `GetBackupReadiness`, `ListBackups`, `GetVmMetrics`, `GetClusterStatus`; changes: `SetVmConfig`, `NewVm`, checkpoints (`New/Restore/RemoveCheckpoint`), `SyncVmGroups`, `NewSwitch`, `RemoveSwitch`, `NewTemplate`, `RemoveTemplate`, `MoveVm`, `MoveVmStorage`, backups and restores, `Mount/DismountBackupDisk`, `RemoveVmWithDisks`, resource metering, `SetClusterNodeMaintenance`, `MoveClusterVm`, `MoveClusterSharedVolume` | Hyper-V, Storage and FailoverClusters modules |
| **PowerShell over WinRM** (management agent) | the same script, run on the remote host by `Ops\HvPortal.Remote.ps1` | adds the WinRM session to every operation |

## Measured cost of one operation

Development machine (Windows 11, Intel Core Ultra 7 268V, 8 logical processors, 2026-11-11), `GetHostDefaults` through
the real script, started exactly as the agent starts it. The account has no Hyper-V rights there, so the Hyper-V call
itself fails at once: what is measured is the **fixed cost** of an operation (process, engine, module), not the Hyper-V
work.

| | Value |
|---|---|
| `powershell.exe` wall clock, first run (cold) | 2.9 s |
| `powershell.exe` wall clock, runs 2–6 | 1.3 – 1.9 s |
| of which: process start until the script's first statement | 0.57 – 1.08 s |
| of which: `Import-Module Hyper-V` (from script start) | 0.12 – 0.24 s (0.97 s cold) |
| peak working set of one process | 134 – 140 MB |
| 8 operations started at once | all done in 3.4 s; working sets together up to ≈ 1.1 GB; engine start 1.1 – 1.3 s each |

So an operation that takes Hyper-V 50 ms still costs more than a second and about 140 MB while it runs. **Not measured**:
a Hyper-V host under load (Windows Server, the Hyper-V module actually talking to vmms), the FailoverClusters and Storage
modules (`GetClusterStatus` loads both), WinRM for remote hosts. Real-host plan V91.

## What the agent does about it (0.9.4)

- **Limit and queue per host** (`OperationGate`): at most `Agent:Operations:MaxConcurrent` scripts run at once for one
  host (default **8**, 1–64); the others wait their turn. Operations that copy whole disks or drain a node (backup,
  restore, template, move, maintenance: hours) use at most `MaxConcurrent - ReservedForShort` slots
  (`Agent:Operations:ReservedForShort`, default **2**), so reads and short changes never wait behind them. Before 0.9.4
  nothing limited the number of processes: 30 requests at once meant 30 processes (30 × 140 MB ≈ 4 GB by the measurement
  above; not observed on a host).
- **The wait counts against the operation's time.** An operation that could not start within the time it was given is
  refused with `host_busy` — it did nothing, and says so — instead of starting late and being cut off half-way. Timeout,
  cancellation (agent stopping) and the one-operation-per-VM lock work as before; every operation still runs in its own
  process.
- **Measurements in the agent log**, one line per operation: total time, time in the queue, time `powershell.exe` took to
  reach the script (`startupMs`), when the Hyper-V module was loaded (`moduleMs`), script time, peak working set, how many
  were running and waiting. Every 100 operations a summary per operation (count, average and maximum time, average
  engine start, maximum queue, maximum memory, failed / timed out / refused as busy) and the highest number of processes
  seen at once. The script reports its own timing in its answer (`timing`); nothing is estimated.
- Settings (agent `appsettings.json`, section `Agent`, or environment `Agent__Operations__MaxConcurrent`):

  ```json
  { "Agent": { "Operations": { "MaxConcurrent": 8, "ReservedForShort": 2 } } }
  ```

## Candidates for in-process code

Moving an operation to C# (WMI `root\virtualization\v2`) removes its fixed cost — about 1.3 s and 140 MB per call on the
machine above — but each one is new code against an API with many versions and must be validated on real hosts. Order
of preference, by how often the portal calls it and how simple the WMI equivalent is:

| Operation | Called | In-process equivalent | Judgement |
|---|---|---|---|
| `ListIsos` | every time a VM wizard or the DVD dialog opens | plain file enumeration under the ISO roots (the folder rules already exist in `HostFolderBrowser`) | **first**: no Hyper-V API at all, same checks as the file explorer |
| `GetHostDefaults` | VM wizard | `Msvm_VirtualSystemManagementServiceSettingData` (default paths), supported versions from `Msvm_VirtualSystemCapabilities` | good candidate, read-only |
| `ListSwitches`, `ListNetAdapters` | VM wizard, settings, Networks page | `Msvm_VirtualEthernetSwitch`, `Msvm_ExternalEthernetPort`, `Msvm_LANEndpoint` | good candidate, read-only; the adapter-to-switch binding needs care (SET teams) |
| `ListCheckpoints` | Checkpoints tab | `Msvm_VirtualSystemSettingData` with `VirtualSystemType` snapshot, `Msvm_MostCurrentSnapshotInBranch` | good candidate, read-only |
| `GetVmConfig` | every opening of a VM's Settings / Disks and network tab | `Msvm_VirtualSystemSettingData` + `Msvm_ProcessorSettingData`, `Msvm_MemorySettingData`, resource allocation settings | worth it (most frequent script read) but the largest mapping; do after the others |
| `GetVmMetrics` | resource history sampler | `Msvm_MetricForME` / `Msvm_BaseMetricValue` | measure first: one call per sampling interval per host |
| `GetClusterStatus` | cluster monitor (every 120 s per cluster), cluster page | partly `root\MSCluster`; Storage Spaces and Health Service have no simple WMI equivalent | keep in PowerShell; see `docs/proposals/clusapi-notifications.md` |
| every change (`SetVmConfig`, `NewVm`, checkpoints, moves, backups, cluster maintenance) | on request | `Msvm_VirtualSystemManagementService` methods with embedded instances | **keep in PowerShell**: rare, long compared with the fixed cost, and the cmdlets hide a lot of version-specific behaviour |

Rule for each migration: measure the operation in the agent log on a real host before and after (same host, same VM
count), keep the PowerShell branch until the in-process one passed the real-host plan, and never remove the argument
validation or the folder rules that the script and the agent share.
