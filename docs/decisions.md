# Architecture decision records

Format: context → decision → consequences. Status of all records: **accepted** (Milestone 1).

## ADR-001 Modular monolith + separate privileged agent
- **Context**: the control plane needs consistency (auth, RBAC, jobs, audit) more than independent scaling; host
  operations need privileges the web tier must not have.
- **Decision**: one gateway process with internal modules; one agent per host for all Hyper-V access.
- **Consequences**: simple deployment and transactions; a compromised gateway can only send typed commands that the
  agent validates; HA of the gateway requires a server database (M2) and is not offered with SQLite.

## ADR-002 Agent-initiated WebSocket over mutual TLS with a private CA
- **Context**: hosts may be in workgroups or other domains; inbound management ports on hosts are undesirable.
- **Decision**: agents connect to `:8444/agent/v1/channel` with an X.509 client certificate issued by the gateway's
  internal CA; the gateway checks chain *and* an active, non-revoked database record. Commands flow over the socket.
- **Alternatives**: WinRM/PowerShell remoting (needs Kerberos/TrustedHosts or HTTPS+NTLM, credentials on gateway);
  gateway→agent HTTPS (inbound port on every host); gRPC (more dependencies, same security).
- **Consequences**: no credentials for hosts stored on the gateway; only outbound 8444 from hosts; revocation is
  immediate (socket closed + DB check on connect + watchdog). Exception, opt-in per host: remote hosts reached by a
  management agent with credentials stored in the portal (ADR-022).

## ADR-003 Local channel for same-server installation
- **Decision (M1)**: the same mTLS channel over loopback is used when gateway and agent share a machine: processes and
  identities remain separate, traffic is mutually authenticated and encrypted. A named-pipe transport with an ACL for
  the gateway service SID is a possible optimization, not required for security.

## ADR-004 Enrollment protocol
- One-time token (256-bit, SHA-256 stored, 15 min, bound to a pending host, atomic consumption) + CA fingerprint pin
  delivered out of band. The agent downloads the CA, compares the pin, and only then sends the token over a TLS
  connection validated against that CA. Private keys are generated on the host (P-256) and never leave it.
- Certificates: 90 days, renewed over the authenticated channel when ⅓ lifetime remains; older certificates are
  revoked once the agent proves possession of the new one.

## ADR-005 Password hashing with PBKDF2-HMAC-SHA256 (600 000 iterations)
- **Context**: requirement for trustworthy libraries and upgradeable parameters; preference for platform tooling.
- **Decision**: BCL `Rfc2898DeriveBytes.Pbkdf2` (FIPS-approved, OWASP 2023 parameters), versioned format, transparent
  rehash on login. Argon2id would require a third-party package; it can be added behind the same format prefix.

## ADR-006 Secrets and keys
- Key ring with 256-bit keys wrapped by DPAPI (LocalMachine) in `keys\master.json`, ACL-restricted; AES-256-GCM with
  purpose-bound AAD; HKDF sub-keys for HMAC uses. Passphrase-protected export for disaster recovery (DPAPI alone
  would make DR impossible). ASP.NET Data Protection keys are machine-bound on purpose (losing them only invalidates
  sessions).

## ADR-007 Server-side sessions with cookie handle
- Cookie `__Host-hvp` (Secure, HttpOnly, SameSite=Strict) holds only user id + session id (encrypted by Data
  Protection). Each request re-validates the session row and the user's security stamp → immediate revocation.
- CSRF: anti-forgery token header on every state-changing request + Origin check + SameSite.

## ADR-008 Jobs: at-least-once dispatch avoidance, outcome verification
- Persist `DispatchedAt` before sending; never re-send after an uncertain dispatch; reconcile from observed state;
  agent de-duplicates by command id within its lifetime. Statuses `Unverified` and `Unknown` are first-class.

## ADR-009 SQLite first, provider-specific migrations
- SQLite for single control plane on local disk (WAL). PostgreSQL / SQL Server in M2 with separate migration
  assemblies and integration tests on each. Provider change = export/import with verification, not live switch.

## ADR-010 SPA served by the gateway
- Same origin, strict CSP (`script-src 'self'`), no inline scripts; static assets immutable-cached, `index.html`
  no-cache. React + Vite chosen for maturity.
- **Revised in v0.3 (see ADR-014):** the UI now uses shadcn/ui (source components copied into the repo, built on
  Radix UI primitives, Tailwind CSS, lucide icons; all MIT). They are vendored source files, so the supply chain is
  the Radix/Tailwind packages pinned in `package-lock.json`.

## ADR-011 WMI via System.Management in the agent
- Same documented classes used by Microsoft samples; synchronous calls run on dedicated threads. Inventory, power,
  delete and console stay on WMI. Configuration operations use PowerShell as described in ADR-012.

## ADR-012 Typed VM operations through one fixed PowerShell script
- **Context.** VM configuration, creation and checkpoints through raw WMI (`ModifyResourceSettings`, embedded CIM
  instances, `Msvm_ImageManagementService`) are large, version-sensitive and easy to get subtly wrong; the Hyper-V
  PowerShell module is Microsoft's supported, documented surface for the same operations ("Usa strumenti nativi").
- **Decision.** The agent ships `Ops\HvPortal.Ops.ps1` (read-only in Program Files). For each typed operation it runs
  `powershell.exe -NoProfile -NonInteractive -File <script>` with a fixed argument list and passes one JSON request on
  stdin. The script contains a closed `switch` of operations; every value is cast to an explicit type and passed as a
  named cmdlet parameter (splatting of fixed keys). There is no `Invoke-Expression`, no script block built from
  input, no `Start-Process`; VMs are resolved by GUID; new disks are created only in the host default VHD folder;
  ISO paths must be inside the host's ISO library roots and must not traverse reparse points. A unit test fails the
  build if forbidden constructs appear in the script code.
- **Validation in three places**: gateway API (`OperationValidation`, permissions per section, state rules), job
  execution (re-authorization, re-stamping of server-controlled fields such as VM id and ISO roots), agent
  (`PowerShellOperationRunner.Validate`) before PowerShell starts.
- **Consequences.** Requires the `Hyper-V-PowerShell` feature on the host; GPO `AllSigned` blocks the unsigned script
  (sign the release); PowerShell Constrained Language Mode (WDAC script enforcement) is not supported yet. A process
  per operation costs ~0.5–2 s, acceptable for configuration changes (not used for the console or inventory).

## ADR-013 Console through hypervisor screen capture and input injection
- **Decision.** Basic console with documented WMI methods (`GetVirtualSystemThumbnailImage`, `Msvm_Keyboard`,
  `Msvm_SyntheticMouse`) relayed over the existing agent channel; one-time tickets; see [console.md](console.md).
- **Rejected for now.** Proxying VMConnect (RDP on 2179 with a pre-connection blob): undocumented exchange, needs a
  Windows principal authenticated by vmms and a full RDP client in the agent; kept as research.
- **Consequences.** Works before boot and without guest networking; low frame rate; no clipboard/audio.

## ADR-014 CSP: per-response style nonce, scripts unchanged
- Radix UI inserts a few `<style>` elements at runtime (scroll locking for modal dialogs, Select/ScrollArea
  viewports). Instead of `'unsafe-inline'`, every response carries `style-src 'self' 'nonce-<random>'`; the SPA shell
  (`/` and client routes) is rendered by `SpaShell` which writes the same nonce into
  `<meta property="csp-nonce">`, and the UI passes it to those components (`get-nonce`, Radix `nonce` props).
  `script-src` stays `'self'` with no nonce. `index.html` is never served as a static file.

## ADR-042 Portal updates from the GitHub releases, installed by a SYSTEM updater task
- **Context**: updating the gateway meant copying the new setup to the server and running it there. Administrators want
  to see from the portal that a new release exists and install it from there, like the agent updates (ADR-029). The
  portal must not contact the Internet by itself unless asked (no telemetry, works offline), and the gateway account
  (`NT SERVICE\HvPortalGateway`, not an administrator) cannot replace its own binaries or service.
- **Decision**:
  - *Check*: `PortalUpdateService.CheckAsync` reads `https://api.github.com/repos/<Gateway:Updates:Repository>/releases/latest`
    (default `NEXQsrl/HvPortal`, validated `owner/name`) only when a `settings.manage` holder presses *Check for
    updates*, or every 12 hours when that holder turned the automatic check on (setting `portal-updates`, **off by
    default**; `PortalUpdateChecker` wakes every 5 minutes and does nothing while it is off). No identifying data is
    sent (fixed User-Agent `HvPortal-gateway/<version>`, no token). Drafts and tags that are not versions are ignored;
    a release is an update only when its version is greater than the running one. The result (version, date, notes
    link, setup name, size, whether a SHA-256 is known) and the last error are stored and audited (`system.updates.*`).
  - *Checksum*: the SHA-256 of `HvPortal.Gateway.Setup.exe` comes from GitHub's own asset `digest` (`sha256:...`), else
    from a line `<64 hex>  HvPortal.Gateway.Setup.exe` of the release notes (written by `Publish-GitHubRelease.ps1`;
    CRLF tolerated, first line wins). Without one the release cannot be installed from the portal (`no_checksum`).
  - *Install* (`POST /system/updates/install {version}`, `settings.manage` + recent re-authentication): only the latest
    release found by the last check, only newer, only when the updater is usable (`InstallBlocker`: `container` in
    Docker/Linux, `not_a_service` when started by hand, `updater_missing` when the task does not exist). The asset URL
    must be `https://github.com/...` or `https://api.github.com/...` (no user info); redirects to GitHub's download host
    are followed by the HTTP client; at most 600 MB. The file is written to `<data>\update\HvPortal.Gateway.Setup.exe.partial`, its
    size and SHA-256 compared, then renamed to `HvPortal.Gateway.Setup.exe` next to `request.json {version, sha256}`;
    any mismatch deletes it (`checksum_mismatch`). Then the gateway starts the scheduled task.
  - *Updater*: `Install-Gateway.ps1` registers `\HvPortal\HvPortal Gateway Update` (runs as SYSTEM, script
    `Update-HvPortalGateway.ps1` copied with `Common.ps1` to `%ProgramFiles%\HvPortal\Updater`; the gateway's service
    SID may only *run* it, the same descriptor as the agent updater). `-DisableRemoteUpdate` (setup: *Allow updates of the portal*,
    on by default) does not register it and removes it. The script copies the staged setup into a folder only SYSTEM
    and Administrators can write, checks the SHA-256 against `request.json` again, requires the installed
    `HvPortal.Gateway.exe` to be Authenticode-signed and the setup to be signed with the **same thumbprint**, runs
    `setup --extract`, requires `gateway\HvPortal.Gateway.exe`, `installer\Install-Gateway.ps1` and
    `installer\Common.ps1` of the extracted files signed with the same thumbprint and a version not older than the
    installed one, then runs `Install-Gateway.ps1` with the settings of the running service read from its registry
    environment (data folder, ports, bind address, public names, portal certificate thumbprint). The installer keeps
    its database copy and rollback (ADR-026). The outcome goes to `<data>\update\last-result.json`, shown in Settings.
  - *UI*: *Settings > System > Updates* (status, check, automatic check, install with the steps explained); the
    dashboard shows a notice to `settings.manage` holders when the **last stored** check found a newer release (the
    dashboard never contacts GitHub). The agents are updated afterwards with ADR-029 (the new agent package comes with
    the gateway).
- **Consequences**: the trust anchor is the code-signing certificate of the installed gateway, as for agents: whoever
  holds that private key can push any gateway to every portal whose administrator presses *Install*. A compromised
  gateway can stage a file and start the task, but the task installs only a setup signed by the installed publisher and
  not older (no downgrade). Unsigned and self-built installations, Docker and installations made before 0.9.1 are
  updated by hand. The gateway needs outbound HTTPS to `api.github.com`, `github.com` and GitHub's download host only
  when the check or the install is asked for. Not run end to end on a real server yet: real-host plan V82.
- **Alternatives**: the gateway updating itself (would need administrator rights for the service account); a
  self-hosted update feed (one more service to run); downloading only and letting the administrator run the setup (kept
  as the fallback shown when the updater is not available).

## ADR-041 Failover clusters: one agent per node, cluster read through any node (agent protocol 11)
- **Context**: Hyper-V hosts are often nodes of a failover cluster, also in a workgroup (DNS administrative access
  point) and with Storage Spaces Direct. Administrators want the portal to keep working when a node or its agent is
  down, to see whether the cluster is healthy (nodes, quorum and witness, storage, free space, capacity left if a node
  is lost), to put a node in maintenance safely and to choose the node of a new VM. Until 0.8.1 the agent only
  *detected* the Cluster service; a VM moved by the cluster appeared as a new VM on the other node.
- **Decision**:
  - *Model*: a cluster of the portal (`HostCluster`, `ManagedHost.ClusterId`) groups hosts that are nodes of the same
    Windows cluster. **Every node keeps its own agent** (the existing enrollment, mTLS, least privilege). Nothing about
    a standalone host changes: no cluster, no new call, same pages. A host can be in one cluster only; hosts reached
    through a management agent cannot be nodes (they do not read clusters). Creating clusters and choosing their nodes
    needs `hosts.manage` + recent re-authentication; removing a node or deleting the cluster never changes Windows
    (hosts stay as standalone hosts; role assignments on the cluster are deleted with it).
  - *Agent HA*: cluster-wide reads and operations go to **any connected node agent** with protocol 11
    (`ClusterService`: the node itself first when it is the target, otherwise nodes that are Up first). A node that does
    not answer (channel lost, timeout, error) is skipped and the next one is asked; the nodes tried are returned to the
    UI. Node maintenance is sent to the node's own agent, or through another node when that agent is not connected.
  - *Inventory* (every 30 s, WMI, read-only, constant WQL): `HostFacts.Cluster` = cluster name, this node's name, state
    and drain status (`root\MSCluster`: `MSCluster_Cluster`, `MSCluster_Node`), the VMs that are cluster roles
    (`MSCluster_Resource` of type *Virtual Machine*, `PrivateProperties.VmID`) and the cluster shared volumes
    (`Win32_Volume` with file system CSVFS). An error (typically access denied) is reported as such, never guessed.
  - *Status* (`GetClusterStatus`, fixed script, FailoverClusters/Storage modules, read-only, each section read on its
    own): nodes (state, drain, votes, storage maintenance), quorum and witness (type, state, share or storage account
    name, **never the cloud witness key**), cluster networks, shared volumes (size, free, owner, Direct/redirected
    access on the reading node, maintenance), Storage Spaces Direct (state, cache, pools, virtual disks, physical disks,
    running storage jobs), roles (with the VM id of VM roles) and failed resources. `GET /clusters/{id}/status` reads
    it live (120 s per node); `ClusterMonitor` reads every cluster every `Gateway:Clusters:MonitorIntervalSeconds` (120)
    and stores the last report and level on the cluster record (tree badge, page opened instantly).
  - *Rules* (`ClusterRules`, pure, unit tested; the status is never better than what was read): node down, majority of
    nodes down, paused/joining nodes, failed drain, disks left in storage maintenance; two-node cluster without witness,
    witness not online (critical with two nodes); network down/partitioned; CSV not online, redirected access, in
    maintenance, under 10 % / 5 % free; S2D pool read-only or not healthy, virtual disks not healthy, physical disks not
    healthy or retired, repair jobs running, unallocated pool space under the recommended reserve (one capacity disk per
    node, up to four); failed roles and resources; unreadable sections (Unknown); nodes without an agent in the portal,
    agents disconnected or older than 0.9.0, hosts of the portal cluster that Windows does not list. Each problem has a
    code with parameters (translated in the UI, with what to do) and an English message (API, notifications).
  - *Capacity*: memory and vCPU totals of the nodes; **one node lost**: for each node, the memory of its running
    highly available VMs must fit in the free memory of the other nodes that are up, minus 2 GB per host; vCPU per
    logical processor with the largest node stopped flagged above 4:1. VMs that are not highly available are not
    counted (they do not move); a VM whose cluster flag is unknown is counted (safe side); unknown free memory = not
    computed.
  - *Maintenance* (`POST /hosts/{id}/maintenance {action: Enter|Exit, storageMaintenance, failback,
    acknowledgeWarnings}`, permission `hosts.maintenance` on the node, scopes Global/Cluster/Host, job
    `host.maintenance`, one at a time per cluster): before queuing, the cluster is read live and **blocked** when the
    node is not Up, another node is not Up, the cluster has one node, S2D repair jobs run, a virtual disk or pool is not
    healthy, a CSV is not online, the witness is not online on a two-node cluster, or the highly available VMs of the
    node do not fit in the free memory of the others; **warnings** (VMs not highly available that will not move, failed
    roles, unknown memory) must be acknowledged. The job reads the cluster again and re-checks everything before
    sending. The script runs `Suspend-ClusterNode -Drain -Wait` and then, with S2D, `Enable-StorageMaintenanceMode` on
    the node's storage scale unit; Exit runs `Disable-StorageMaintenanceMode` **before** `Resume-ClusterNode -Failback
    Immediate|NoFailback` (Microsoft's procedure). A failed drain fails the job with the state left (node paused) said
    in the message; storage maintenance that cannot be set leaves the job Succeeded with a warning and the command to
    run. The portal never forces a drain, evicts, stops or restarts a node.
  - *VMs*: the wizard lists hosts by cluster with node state and free memory (paused nodes not offered) and suggests
    the node with most free memory. `NewVm.HighlyAvailable` (protocol 11) adds `Add-ClusterVirtualMachineRole` after
    the VM is created and configured; the location must be on a CSV that the node reports (checked by the gateway, by
    the shared validation and by the agent on the file system); names are unique across the nodes (the role is named
    after the VM). If the role cannot be added the VM exists, is **not** highly available, and the job message says so
    (`highlyAvailable: false` in the result). A move between two nodes of the same cluster uses
    `Move-ClusterVirtualMachineRole` (live when running, quick otherwise, files stay on the cluster storage), only for
    highly available VMs.
  - *Re-homing*: when a node reports a VM whose record belongs to another node of the same cluster and that node no
    longer reports it (missing there, not in its latest inventory, or its inventory is stale), the record moves to the
    reporting node (same portal id, groups, assignments, history; events `vm.moved_out`/`vm.moved_in`). While both nodes
    report it (live migration in progress) no second record is created.
  - *CSV paths*: shared volumes are mount points (reparse points), which the folder rules refuse everywhere. The only
    exception: a folder exactly `<drive>:\ClusterStorage\<name>` whose volume Windows reports as CSVFS
    (`GetVolumeInformation` in the agent, `Win32_Volume` in the script). They are listed in the folder pickers and the
    file explorer; free space is the volume's.
  - *RBAC*: Cluster scope implemented: a host inherits the assignments of its cluster, a VM those of its host's
    cluster. The cluster status (nodes, storage, quorum, capacity) needs `inventory.read` on the cluster; who sees only
    some nodes or VMs sees the cluster in the tree without its status.
  - *Access of the agent*: the virtual account `NT SERVICE\HvPortalAgent` is not an administrator; reading the cluster
    needs cluster access. `Install-Agent.ps1 -ClusterAccess Full|ReadOnly|None` runs `Grant-ClusterAccess` /
    `Remove-ClusterAccess` for the service account (the setup offers it, pre-checked when the Cluster service runs or the
    data from the portal ask for it; enrollment commands of cluster nodes include `-ClusterAccess Full`). Service SIDs
    are derived from the service name, so the same account is valid on every node; with `-ServiceAccount` (local users,
    different SIDs per node) it must be granted on each node.
  - *Notifications*: `cluster.status` when the level gets worse or is back to Ok (setting *Cluster problems*, on by
    default).
- **Consequences**: no new inbound port, no credentials stored; with every agent down the cluster is shown as not
  readable (never as healthy). The S2D storage cmdlets may need administrator rights that the agent account does not
  have: those sections then appear as not read (Unknown). The FailoverClusters PowerShell module must be installed on
  the nodes (`RSAT-Clustering-PowerShell`). Draining a node with many VMs can take hours (job timeout 4 h). Cluster
  creation, quorum changes, CSV/pool provisioning, node eviction and cluster-aware updating are not done by the portal.
- **Alternatives**: one "cluster agent" talking to the cluster name (single point of failure, credentials or Kerberos
  across nodes, impossible without AD); a management agent per cluster with stored credentials (ADR-022: no
  credentials needed here since every node has its agent).
- Not validated on a real cluster: real-host plan V72–V80.
- **0.9.1 (agent protocol 12)**: *Owner of a cluster shared volume*: `POST /clusters/{id}/shared-volumes/move
  {volumeName, destinationHostId}` (permission `hosts.maintenance` on the new owner node, audited) queues the job
  `cluster.storage` / `MoveCsv` (same per-cluster lock as maintenance), which runs `Move-ClusterSharedVolume -Name
  -Node` through any connected node agent 0.9.1+. The volume name is validated on both sides (letters, digits, space,
  `._()-`, up to 128 characters) and passed as a parameter; the node name comes from the inventory of the destination
  host. Checked on the live cluster at request and again at execution: the volume exists and is online, the node is up
  and does not own it already. Only the coordinator role moves: the data stays where it is and the VMs keep running.
  *Problems per node*: `GET /clusters` returns the problems of the last stored read and those about each node (subject =
  node or host name), only to who reads the cluster status; the tree shows an icon on the cluster and on the node with
  the problems as tooltip, the host page lists the problems of its node. In the tree the VMs of a cluster are listed under
  the cluster, after its nodes (they move between nodes); standalone hosts keep their VMs below them. Real-host plan V81.
  *Cluster state and copies of the data* (same protocol, new optional fields of `GetClusterStatus`, read-only, each
  guarded by `Get-Command`): `coreGroupState` and `coreResources` (the group of type *Cluster*: name, IP address,
  witness); `healthFaults` from `Get-HealthFault` (S2D Health Service: severity, type, reason, recommended action,
  faulting object); `replication` from `Get-SRGroup`/`Get-SRPartnership` (Storage Replica: mode, status, bytes left,
  primary, partner) when the module exists; `elapsedSeconds` of storage jobs (the UI shows data copied, elapsed time and
  an estimate of the time left). New rules: `core_group_not_online` (critical), `core_resource_not_online`,
  `health_fault` (critical for Critical/Fatal faults), `replication_not_in_sync`. The Storage tab lists for every
  volume whether all its copies are in sync or being rebuilt. Older agents simply omit the fields (shown as not read).
  Real-host plan V83.
  *Backup staging folder of the nodes* (ADR-031 applied to clusters): a share with credentials needs the staging folder
  on the host that runs the VM, and in a cluster the VMs move between nodes, so a folder set on one node only made
  backups work there and fail elsewhere. `PUT /clusters/{id}/backup-staging {path, replaceExisting}` sets one folder on
  every node (same permission and checks as for one host: `hosts.manage` or `backup.manage`; drive known on the node,
  inside its storage folders), **all or nothing** (one node refusing the path changes nothing and every refusal is
  named), nodes that already have a folder keep it unless `replaceExisting`; audited per host and for the cluster.
  Nothing is sent to the agents. The cluster page (Nodes tab) lists the folder of each node and warns when backup
  repositories with credentials exist and some node has none; the repository list gives backup administrators the
  hosts that back up there without a folder (hosts of the VMs of its jobs, plus every node of their clusters); the
  job dialog warns about the nodes of the clusters of the chosen VMs. When the portal chooses the host of a
  repository check or rescan and no connected host can use it, the error lists every host with its reason (agent not
  connected, too old, no staging folder) instead of the reason of the first host only. Real-host plan V84.

## ADR-040 License: PolyForm Shield License 1.0.0 (source available)
- **Context**: NEXQ wants HvPortal to be free for everyone, companies included, with public source code, but does not
  want third parties to resell it, and wants a recognized license rather than a custom one. The earlier proposal
  (Apache-2.0) would have allowed resale; no license in GitHub's list can forbid it, since they are all OSI open-source
  licenses. A custom "NEXQ Community License" was drafted and dropped for that reason.
- **Decision**: the standard [PolyForm Shield License 1.0.0](https://polyformproject.org/licenses/shield/1.0.0),
  official text unchanged in [`LICENSE`](../LICENSE), with `Required Notice: Copyright NEXQ S.r.l.
  (https://www.nexq.it)`. Any use is permitted except providing a product that competes with the software or with a
  product NEXQ provides using it (so no resale, no competing forks or services); distribution with the terms and the
  required notice; patent license with patent defense; 32-day cure of a first violation. Third-party components keep
  their own licenses ([`NOTICE`](../NOTICE), [licensing.md](licensing.md)).
- **Consequences**: HvPortal is source available, not OSI open source; GitHub shows "Other". If NEXQ sells services
  based on HvPortal, the same services offered by others may count as competing: NEXQ should state how it applies the
  noncompete to third-party installation and support (listed in [licensing.md](licensing.md)). Contributions and a
  `Licensor Line of Business` line are not decided. From 0.8.1 the release ships `LICENSE`, `NOTICE` and a generated
  `THIRD-PARTY-NOTICES.txt` (`installer/New-ThirdPartyNotices.ps1`) in the setups, the agent package and the
  installation folders.

## ADR-039 Resource use history of VMs (agent protocol 10)
- **Context**: administrators want to see how much CPU, memory, disk and network a VM used over the last days (sizing,
  troubleshooting), as charts with peaks. Hyper-V resource metering (`Enable-VMResourceMetering`, `Measure-VM`) keeps
  only totals and averages since it was enabled or reset, not a time series; the inventory already reports CPU load
  and assigned memory every 30 s.
- **Decision**:
  - *On/off*: `POST /vms/{id}/metering {enabled}` queues a job `vm.metering` (`Enable` / `Disable`, permission
    `vm.config.compute` on the VM, audited) that runs `SetVmResourceMetering` (Enable-/Disable-VMResourceMetering by VM
    id; the result is the state Hyper-V reports afterwards). The VM tab *Resource use* turns metering on by itself only
    the first time it is opened for a VM never metered and only for a user who may do it; after a "Turn off" it stays
    off until someone turns it on again. Hyper-V is the truth: a VM metered or no longer metered outside the portal is
    adopted / marked off at the next reading.
  - *Collection* (`VmMetrics/MetricsCollector`, every `Gateway:Metrics:SampleIntervalSeconds` = 300 s): for each
    connected host with agent protocol 10 and at least one metered VM, one `GetVmMetrics` (Get-VM + Measure-VM for all
    metered VMs, read-only). One row per VM (`VmMetricSamples`) with: CPU average and peak and assigned memory average
    and peak of the period, computed from the inventory readings of the period (`VmMetricsBuffer`, in memory); memory
    demand, status and limits of that moment; the Hyper-V totals (normalized I/O count, MB read/written, MB
    received/sent, metering duration, latency average, allocated disk space).
  - *Series* (`MetricsSeries`): disk and network rates are differences of two consecutive totals divided by the time
    Hyper-V measured between them; a total that goes back means a reset (the period starts from zero); more than three
    intervals without a sample is a gap (no value is invented). At most 600 points per request (averages per bucket,
    peaks kept). Summary cards: CPU peak/average/95th percentile/hours above 80 %, highest assigned memory and demand,
    memory pressure (demand ÷ assigned), IOPS, data read/written, network peaks and totals, allocated space.
  - *Retention*: samples older than `RetentionDays` (10) are deleted; every `ResetDays` (10) the collector runs
    `ResetVmResourceMetering` (audited as `vm.metering.reset`, system action) so that the Hyper-V totals cover at most
    that period. Deleting a VM or a host deletes its samples.
  - CPU percent comes from the inventory (Msvm_SummaryInformation ProcessorLoad), not from `Measure-VM` AvgCPU: that is an
    integer MHz average since the reset, whose rounding makes the difference over 5 minutes meaningless after a few days.
- **Consequences**: one PowerShell process per host every 5 minutes while at least one VM is metered. After a gateway
  restart the first sample covers a shorter period (`ObservedSeconds`). CPU and memory peaks shorter than the 30-second
  inventory interval are not seen. Hosts with agents older than 0.8.0: the tab says so, nothing is sent. Remote hosts
  (management agents) use the same script through the launcher.
- Not validated on a real host: Measure-VM property names differ between Windows Server versions (the script reads
  them by name and leaves a value empty when missing); real-host plan V69-V71.

## ADR-038 Confirmation code for Microsoft accounts and sign-out after inactivity
- **Context**: sensitive actions need a recent step-up (`RequireRecentReauthAsync`, 5 minutes). For Microsoft (Entra ID)
  accounts it opened a Microsoft sign-in popup (`prompt=login`), which did not work in practice (popup blocked,
  redirect issues, conditional access). Separately, the portal refreshes in the background, so an unattended browser
  kept its session alive: the server idle timeout (default 30 minutes) never expired while a tab was open.
- **Decision**:
  - *Confirmation code*: `POST /auth/reauth/code` (Microsoft accounts only; local accounts get `password_required`)
    returns a random 6-character code (no 0/O/1/I/L), kept in memory for 2 minutes, bound to the session, single use,
    discarded after 5 wrong attempts. The user types it again (paste disabled in the dialog) and `POST /auth/reauth
    {confirmationCode}` marks the session as re-authenticated (audit `auth.reauthenticate`, method
    `confirmation-code`; failures audited with the reason). Local accounts keep password (+ authenticator code).
  - *Inactivity*: `IdleGuard` in the browser counts real user activity (pointer, keyboard, wheel, touch, scroll) in any
    tab of the portal (shared through localStorage). After 15 minutes without activity (or 3 minutes before the server
    idle timeout when that is shorter) a dialog counts down 3 minutes; only "Stay signed in" keeps the session (it also
    calls `/auth/me`). At zero the browser signs out with `POST /auth/logout?reason=idle` (session revoked with reason
    "idle timeout (browser)", audit `auth.logout` with `reason: idle`) and the sign-in page says why.
- **Consequences**: the code confirms the **intent** of the person at the session, not the identity: whoever controls
  an open session (or its cookie) can also type the code. The protection left for Microsoft accounts is the Microsoft
  sign-in of the session itself, the 5-minute window, the inactivity sign-out and the audit. The server idle timeout
  still applies independently. The Entra `mode=reauth` endpoint stays for compatibility but the UI no longer uses it.

## ADR-037 Delete a VM with its disk files, only on explicit request (agent protocol 9)
- **Context**: deleting a VM removed only its configuration (`DestroySystem`, ADR-009 era) and left the disk files on
  the host: safe, but administrators then had to find and delete the files by hand, often outside the portal.
- **Decision**: `POST /vms/{id}/delete` takes `deleteDisks` (default `false`). With it the job is `vm.delete`,
  operation `DeleteWithDisks` (900 s); the gateway sends `DeleteVm` with `DeleteDisks = true`, which needs protocol 9
  (`AgentProtocol.RequiredVersion`; an older agent is refused with `agent_outdated` at request time instead of
  deleting only the configuration). The agent runs the fixed-script operation `RemoveVmWithDisks` (never accepted as a
  plain `HyperVOperation` command). The script, **before** changing anything: the VM must be Off or Saved and have no
  checkpoints; it lists the disks attached to the VM; a file is deleted only when it is a `.vhd`/`.vhdx`/`.avhdx` that
  exists, is not used by any other VM of the host (attached, or anywhere in the parent chain of their disks) and lies
  inside the allowed storage folders (ADR-034). Then `Remove-VM -Force` and the files one by one (`Remove-Item`, never
  recursive). Parents of the VM's own differencing disks, ISO images and folders are never deleted. The job message
  lists deleted, kept (with the reason) and not deletable (with the error) files; the VM record is removed as for the
  configuration-only delete. Same permission (`vm.delete`), typed VM name and recent re-authentication; the request is
  audited with `deleteDisks`.
- **Consequences**: irreversible: only a backup brings the disks back. A file shared with a VM on another host (SMB
  share) is not detected as in use. A file that cannot be deleted leaves the job Succeeded with the list in its message
  (the VM itself is gone).
- Not validated on a real host (real-host plan V68).

## ADR-036 Host health: a read-only report never better than what was read (agent protocol 9)
- **Context**: administrators want failing disks, degraded pools, full volumes and hardware errors of a host visible
  in the portal, without RDP and without a separate monitoring product.
- **Decision**: `GetHostHealth`, a read-only branch of the fixed script, `GET /hosts/{id}/health` (live, 120 s,
  nothing stored), `inventory.read` on the **host** itself (serial numbers and hardware are host information, not
  shown to someone who only reads some VMs of it).
  - Sections, each read on its own: CIM `Win32_ComputerSystem`, `Win32_BIOS`, `Win32_PhysicalMemory`,
    `Win32_Processor`; `Get-PhysicalDisk` + `Get-StorageReliabilityCounter`; `Get-Volume` (fixed volumes);
    `Get-StoragePool -IsPrimordial $false` + `Get-VirtualDisk`; `Get-NetAdapter -Physical`; `Get-WinEvent` level
    critical/error of the last 7 days from the System log (only disk, file system, storage controller, WHEA, power and
    Hyper-V providers) and from `Microsoft-Windows-Hyper-V-VMMS-Admin` (at most 40 each); services `vmms`,
    `vmcompute`, `HvHost`. A section that cannot be read is returned in `errors` with the reason, never guessed.
  - Rules (agent): disk *Warning* with wear ≥ 90 %, uncorrected read/write errors or an operational status
    predictive/degraded/error/failed/lost; volume *Warning* under 10 % free, *Critical* under 5 %; pool *Warning* when
    one of its virtual disks is not healthy; adapter *Warning* when not Up or Disabled. Gateway (`Overall`): the worst
    section; `vmms` not running = *Critical*; a critical event in the last 24 h = at least *Warning*; any unreadable
    section = at least *Unknown*; nothing read = *Unknown*.
- **Consequences**: which sections the agent account (*Hyper-V Administrators*, not an administrator) may read is not
  known yet: the storage cmdlets and the reliability counters may need administrator rights; such sections appear as
  errors and keep the status at *Unknown* (real-host plan V52). No history and no alerting on health. Windows sees only
  what the drivers report: this does not replace the management controller of the server (iDRAC, iLO, ...).

## ADR-035 Move a VM to another host or to another folder (agent protocol 9)
- **Context**: hosts need maintenance and volumes fill up; on standalone hosts (no failover cluster) Hyper-V offers
  `Move-VM` (live migration, also shared-nothing) and `Move-VMStorage`.
- **Decision**: two typed operations of the fixed script, job type `vm.migrate` (target = the VM, per-VM lock, long
  job of up to 12 h), `POST /vms/{id}/move`:
  - `MoveVm` (`Operation=Host`): `Move-VM -DestinationHost <name>`; with `includeStorage` (default) also
    `-IncludeStorage -DestinationStoragePath <folder>\<VM name>`. A running VM is live-migrated, a VM that is off is moved
    with its configuration. Without storage the files stay where they are, which only works when they are on storage
    both hosts reach (SMB share). The destination is a host of the portal (`destinationHostId`); its name is the DNS name
    from its inventory (DNS host name + domain; for remote hosts the computer name), resolved by the gateway at request
    time and again when the job runs (`MigrationRules.DestinationName`), never taken from the client.
  - `MoveVmStorage` (`Operation=Storage`): `Move-VMStorage -DestinationStoragePath <folder>\<VM name>` on the same host
    (configuration, disks, checkpoints, smart paging); the VM keeps running; refused when that folder exists and is not
    empty. The new configuration path is recorded.
  - Permissions: `vm.migrate` on the VM and, for a host move, `vm.create` on the destination host; both checked again
    at execution (`HoldsExtraPermissionsAsync`). Limits of groups (ADR-024): `GroupPolicyService.CheckMoveAsync` refuses
    a destination host that the VM's folders do not list and a destination folder outside their storage paths on that
    host (checked when the move is requested and again when the job runs). Allowed storage folders (ADR-034): the
    destination folder of a host move is checked by the gateway against the destination host's list at request and at
    execution (the source agent cannot see the other host's file system); for a storage move the gateway checks it at
    request and execution and the agent checks the folder itself.
  - After success the portal record follows the VM (`MigrationRules.ApplyMoveAsync`, Hyper-V keeps the id): host
    changed, groups, VM-level role assignments, history and restore points kept; a record that the destination's
    inventory may have created meanwhile for the same Hyper-V id is removed; events `vm.moved_out` / `vm.moved_in`.
- **Not done by the agent**: host migration settings. Live migration must be enabled and authenticated on both hosts
  by their administrators (`Enable-VMMigration`, `Set-VMHost -VirtualMachineMigrationAuthenticationType`, migration
  networks, firewall). `Move-VM` is started by the agent service on the source host, so CredSSP cannot work (it needs
  an interactive sign-in on the source): only Kerberos authentication is practical. On the network a virtual service
  account appears as the source's computer account (`HOST$`), so that account is expected to need the right to manage
  Hyper-V on the destination (e.g. membership of its *Hyper-V Administrators*), possibly with constrained delegation.
  All of this is **unverified** (real-host plan V54, V66). Hosts in a workgroup outside a cluster are not expected to
  support live migration.
- **Consequences**: a move whose result is lost ends `Unknown` and the portal record is **not** moved; the inventory has
  no automatic re-homing, so the destination then reports the VM as a newly discovered one (without the groups and
  assignments of the original) and the original record turns missing: to be fixed by hand. A move between hosts changes
  which host-scoped role assignments apply to the VM (those of the source stop, those of the destination start, Deny
  included); it is not routed through the access-change guard of ADR-015. Failover clusters
  (`Move-ClusterVirtualMachineRole`) are not covered.
- **Alternatives**: `Msvm_VirtualSystemMigrationService` through WMI (same reasons as ADR-012); a move started by the
  gateway with stored host credentials (against ADR-002).
- Not validated on a real host (real-host plan V54–V55, V66).

## ADR-034 Allowed storage folders of a host (agent protocol 9)
- **Context**: on some hosts the portal must reach only some folders (data volumes, a tenant's folder), and that limit
  must hold even if the gateway is wrong or compromised.
- **Decision**: the list is decided **on the host**. `Install-Agent.ps1 -StorageFolders 'D:\VMs','E:\ISO'` validates the
  folders before the service is stopped (local folders of fixed drives, not system folders, no `..`, at most 16),
  creates the missing ones and writes `storage.json` in the agent data folder (private ACL). Without the parameter an
  upgrade keeps the list; `-StorageFolders ''` removes it. The graphical agent setup has a page for it (keep / every
  drive / only these folders).
  - The agent reads the file at start (`LocalStorageFolders`). A file that cannot be read or does not validate makes
    every folder operation fail (fail closed), never "everything allowed".
  - With a non-empty list `HostFileAccess.CheckAllowed` runs in `HostFolderBrowser.CheckFolder`, which every folder of a
    request goes through: pickers, file explorer (list, download, upload, new folder), VM location, folder of a new
    disk, template library, restore location and disk destination, repository and staging folders, storage-move
    destination; also the folder of an ISO and the ISO library roots. The browser shows the allowed folders as the top
    level, with no way up from them.
  - The agent reports the list in `HostFacts.StorageFolders` (null for older agents); the gateway shows it (read-only
    for hosts with their own agent) and uses it to refuse early (repositories, staging folder, restore folders, move
    destinations).
  - Remote hosts (management agents, ADR-022): there is no installer on the host, so the list is set in the portal
    (`PUT /hosts/{id}/storage-folders`, `hosts.manage` global, audited), stored in `Hosts.StorageFoldersJson`, sent in
    `RemoteTarget.StorageFolders` and enforced by the management agent.
  - New folders: `NewFolder` (in-process `Directory.CreateDirectory`, `POST /hosts/{id}/files/folders`,
    `host.files.write` on the host or through a group) only inside an allowed folder, never over an existing name.
- **Consequences**: changing the list needs the installer (the agent reads it at start). A host administrator can edit
  the file (trusted boundary B5); the agent service account has full control of its data folder. Drive roots are
  accepted as allowed folders. Where the script falls back to a default folder of the host (VM creation without
  location, a new disk without folder, a restore as new VM without location) the agent passes the list (and a load
  error) with every request and the script refuses a default folder outside it (`location_not_allowed`); a new disk
  goes next to the first disk of the VM on a restricted host; the creation wizard proposes the allowed folders. A
  management agent older than 0.7.0 would ignore the list, so the portal refuses to set it for its remote hosts
  (`agent_outdated`).
- **Alternatives**: a list kept in the portal for every host (a compromised gateway could widen it); NTFS ACLs only
  (still the real boundary for the agent account, but they do not hide folders in the portal).
- Not validated on a real host (real-host plan V50–V51).

## ADR-033 Notifications: e-mail and webhooks
- **Decision**: `NotificationService`, configured in Settings > Notifications (`GET/PUT /settings/notifications`,
  `POST /settings/notifications/test`, `settings.manage`, audited without secrets). Nothing is configured and nothing
  is sent by default.
  - E-mail: BCL `SmtpClient`, security `starttls` or `none` (implicit TLS on 465 is not supported by that client),
    optional user and password; the password is sealed with the key ring (purpose `notification-smtp-password`), never
    returned (`hasPassword`), kept when the field is omitted, removed with an empty value. At most 20 recipients.
  - Webhooks: at most 10, `https://` only, no user info in the address, 20 s timeout; formats `generic`
    (`{source, event, severity, title, text, data, timeUtc}`), `teams` and `slack` (`{text}`). Addresses usually
    embed the secret of the webhook: they are **sealed** with the key ring (purpose `notification-webhook-url`) and
    returned masked (scheme, host, first 12 characters of the path); a masked address sent back under the same name
    keeps the stored one, under another name it is refused (`webhook_url_masked`). No redirects are followed; literal
    `localhost`, loopback, link-local (cloud metadata), unspecified and multicast addresses are refused when saving, and
    the connection is refused when the name resolves only to such addresses (checked after DNS resolution, in the
    socket connect callback of the `notifications` HTTP client).
  - SMTP credentials are never sent without TLS (`smtp_credentials_need_tls` with security `none` and a user).
  - Changing the settings needs a recent re-authentication, like the other settings with secrets.
  - Events: end of a backup run according to the job (`notifyOn`: never / failure / warning / always; "Back up now"
    runs only on failure), failed restore tests, and once a day the agents older than the package of the gateway.
  - Sending never blocks a backup: failures are logged and audited (`notifications.send`, outcome Failure).
- **Not done (the Milestone 3 items stay open)**: no queue, retry or de-duplication; private (RFC 1918) destinations
  are allowed on purpose (on-premises receivers), so whoever holds `settings.manage` can make the gateway POST a fixed
  JSON body to an internal HTTPS address. Messages carry VM names and error texts out of the network.
- **Alternatives**: MailKit (implicit TLS, OAuth; third-party dependency to license-check); syslog (planned for audit
  forwarding).
- Not validated against real mail servers and chat services yet (real-host plan V63).

## ADR-032 Read-only API tokens for monitoring systems
- **Context**: monitoring systems (PRTG, Zabbix, Nagios, ...) must read the backup status without a browser session,
  MFA, cookies or anti-forgery tokens.
- **Decision**: table `ApiTokens`. A token is `hvp_` + 40 characters drawn with `RandomNumberGenerator` from 57
  unambiguous letters and digits, shown once at creation; the database keeps only its SHA-256 (hex) and its first 8
  characters (to recognise it). Scopes are a closed list: only `backup.read`. Optional expiry (1–3650 days); at most 100
  active tokens. Create (`settings.manage` **and** a recent re-authentication), list and revoke (`settings.manage`),
  all audited.
  - Tokens open only `/api/v1/public/*` (`Authorization: Bearer hvp_...`): anonymous for the cookie scheme, GET only,
    rate limiter `api-token` (120 requests per minute per source address). Unknown, revoked, expired or out-of-scope
    tokens are refused with 401 and audited (`api_tokens.use`, Denied). Last use (time, address) is recorded at most
    once a minute.
  - `GET /public/backup/status`: overall `ok` / `warning` / `critical` (critical = an enabled job whose last run failed;
    warning = a last run with warnings, or a VM without a good restore point in the last 48 h), each job with its last
    run, each VM with its newest point. Names only: no paths, hosts or credentials. `GET /public/backup/runs?since=`
    (default the last 24 h, at most 500 runs).
- **Consequences**: a token works only while the user who created it exists and is enabled (a disabled or deleted
  creator stops its tokens; refused with reason `creator_disabled`); it keeps working when the creator loses
  `settings.manage`, until it is revoked or expires; it sees every job and VM name, not filtered by RBAC. Refused uses
  are audited at most once per token prefix and minute. No IP allow-list. No write scope exists.
- **Alternatives**: service users with a password and no MFA (an interactive account with a weaker login); Entra client
  credentials (ties monitoring to Entra); client certificates (heavy for monitoring tools).
- Real monitoring products not tried yet (real-host plan V63).

## ADR-031 VM backups: export of a checkpoint into a repository, verified restore points (agent protocol 9)
- **Context**: customers need copies of their VMs restorable from the portal, also to a NAS outside the domain, without
  a third-party backup product. Hyper-V's documented PowerShell surface offers checkpoints, export and import.
- **Decision**: a **restore point** is a full export of a checkpoint of one VM in
  `<repository>\<vm key>\<point id>` (`<vm key>` = safe VM name + first 8 hex digits of the Hyper-V id), plus a manifest
  `hvportal-backup.json` (format 1) with the SHA-256 of every exported file, written **last**: a folder without
  manifest is an incomplete point and is never used.
  - **Backup** (`BackupVm`, fixed script, job `backup.vm`, target the VM): open the repository; refuse an existing
    point folder; take a checkpoint of the requested type (the VM's checkpoint type is changed for that moment only and
    put back; checkpoints disabled on the VM = failure); `Export-VMSnapshot` of that checkpoint; `Remove-VMSnapshot`
    (merge); SHA-256 of every exported file; through a staging folder: copy to the repository and, with
    `verifyAfterCopy`, read back and compare; manifest (VM name and id, host, times, checkpoint type requested and the
    one Hyper-V reports, generation, configuration version, vCPU, memory, adapters with switch and VLAN, configuration
    file, disks, files, size, agent version, warnings). On failure the folder created by that run is removed when it
    has no manifest; the checkpoint is removed in any case the script reaches its end. The checkpoint is named
    `HvPortal backup <point id> [<checkpoint type of the VM>]`: if the agent or the host stopped in the middle, the next
    backup of that VM removes the leftover checkpoint and puts back the checkpoint type recorded in its name, with a
    warning in the point.
  - **Consistency wording**: `Production` (default) asks Hyper-V for a production checkpoint, which uses the backup
    integration service of the guest (VSS in Windows guests, a file-system freeze in Linux guests) and falls back to a
    standard checkpoint when the guest cannot take part; `ProductionOnly` fails instead of falling back; `Standard` is a
    crash-consistent copy of the disks. The point records the checkpoint type Hyper-V reports
    (`checkpointTypeUsed`). HvPortal does **not** verify or guarantee application consistency: applications inside the
    guest (databases, mail) must be protected by their own means; a restore point is a VM copy, not an
    application-consistent backup.
  - **Repositories** (`backup.manage`): a local folder of one host (only that host's VMs; it follows that host's
    allowed storage folders) or an SMB share usable by every host. A share without credentials is expected to be reached
    with the **computer account** `HOST$` of each host (Hyper-V writes the export itself, and the agent's virtual account
    appears on the network as the computer account), so share and NTFS rights are needed for every host — unverified
    (real-host plan V67). A share outside the domain (NAS in a workgroup) takes a user name and password: AES-256-GCM with the key ring,
    purpose bound to the repository id, write-only in the API, never stored in job parameters (the job runner stamps
    target, folder and point from the records at execution), sent only inside the command over the agent channel and
    used by the script as a PowerShell drive of that process (`New-PSDrive -Credential`). Hyper-V cannot use those
    credentials, so exports and imports go through the **backup staging folder** of the host
    (`Hosts.BackupStagingPath`, `hosts.manage` or `backup.manage`), which must hold a full copy of the largest VM. The
    path of a repository cannot be changed (its points live there). `GetBackupReadiness` checks the Hyper-V module and
    cmdlets, the checkpoint setting, backup integration service and heartbeat of the VMs, a write test and the free
    space of the repository and the staging folder.
  - **Jobs and schedule**: a backup job protects VMs and groups (with their sub-groups, resolved at every run) into one
    repository, with a schedule (`daily`: days of the week and `HH:mm` times in a time zone, a time skipped by daylight
    saving runs one hour later; `hourly`: every 1–24 h; `manual`), retention, checkpoint type, verify after copy,
    restore test every N days and notification setting. The scheduler (`BackupScheduler`, every 20 s,
    `Gateway:Backup`) starts due runs; a start missed while the gateway was down runs once when it is back; a start is
    skipped while the previous run of the job is still running. A run creates one point and one persistent job per VM,
    executed **as the user who last saved the backup job** and re-authorized per VM at execution (a VM added later to a
    group that this user may not back up fails with `authorization_revoked`). "Back up now" on a VM is a run of its own.
  - **Truthful states**: a point is `Ok` only when the agent returned a valid manifest of that point; a lost result is
    `Unverified` (verify before relying on it); otherwise `Failed`. Deletion: `Deleting` → `Deleted`; a failed delete
    after dispatch → `Unverified` (files may be partly gone). A run is Success, Warning (some failed or unverified) or
    Failed (none good) from its points.
  - **Verify** (`VerifyBackup`, `backup.run`): every file of the manifest present with the same size and SHA-256. It
    detects damaged or incomplete copies; it does not prove authenticity (the manifest is not signed, see threat T15).
  - **Retention** (after each run of a job): per VM the newest N good points are kept and, with a day limit, none older;
    points marked *Keep* and the newest good point of a VM are never removed; failed and unverified points older than
    the newest good one are removed. Removal (`RemoveBackup`, job `backup.remove`) deletes only a folder whose manifest
    carries the same point id. Points of "Back up now" and of deleted jobs have no retention (delete them by hand).
  - **Restore** (`RestoreBackup`, `Compare-VM` + `Import-VM -Copy`; network adapters that do not fit are disconnected
    with a warning, any other incompatibility stops the restore):
    - *New VM*: new id (`-GenerateNewId`), new name, `<location>\<name>` (or the host default folders), network as in
      the point / disconnected / one switch, optionally started; the original VM is not touched; any host that can use
      the repository; needs `vm.create` on that host. The new VM is not placed in a group.
    - *Replace* (typed VM name): only the VM the point was taken from, without checkpoints. It is turned off and removed
      from Hyper-V; its disk files are **renamed** `<file>.pre-restore-<yyyyMMddHHmmss>` (never deleted, they keep using
      space); the point is imported with the original id, name and folders, disks copied into the folder of its first
      disk. Before the original is touched, `Compare-VM -GenerateNewId` checks that the point can be imported on that
      host (version, processor, memory...): an incompatibility stops the restore with the VM unchanged. If the import
      still fails afterwards, the disks get their names back but the VM **stays removed from Hyper-V** (its
      configuration is gone): recreate it from the disks or restore as a new VM. Replacing needs the typed VM name and a
      recent re-authentication.
    - *Test*: a new VM `hvp-test-<id>` (in `<staging folder>\restore-tests` when the host has one), adapters
      disconnected, started; it passes when the guest heartbeat reports OK within 600 s (the contract accepts 60–3600 s,
      the gateway always sends 600); then it is turned off, removed and its folder deleted. Scheduled every N days for
      the newest good point of each VM of a job
      (skipped on hosts without a staging folder); failures are notified. A heartbeat proves the guest booted and runs
      the integration services, not that its applications work.
  - **One disk** (`RestoreBackupDisk`): copied to a host folder as `<name>-restored-<stamp>.vhdx`, optionally attached to
    a VM on its SCSI controller (`vm.config.storage` on that VM, otherwise `host.files.write` on the host): the way to
    copy files back inside the guest with the guest's own accounts.
  - **Files** (`MountBackupDisk`, only on hosts with their own agent): the disk is attached read-only to the host
    (`Mount-VHD -ReadOnly`; from a share with credentials a staged copy first); only volumes that get a drive letter are
    offered; the agent keeps its own list (at most 4 per host, detached after 2 h without use and at agent start) and
    browses only those volumes, in-process, without following junctions; the guest's NTFS permissions apply to the agent
    account (unreadable files are refused: restore the disk to a VM instead). Only the user who attached the disk
    browses it; `backup.restore` is checked at every call; every download is audited with its SHA-256.
  - **Rescan**: points found in a repository but unknown to the portal (portal reinstalled, older database restored)
    are added as `Unverified` and attached to **no** portal VM (new VM id, so they are evaluated on Global): their
    manifest is not signed and anyone who can write to the share could have written it, so only Global holders of the
    backup permissions see them, they restore only as a new VM, never over an existing VM.
  - **Files on another host**: attaching a disk on a host other than the VM's needs `host.files.write` on that host.
  - Creating or changing a repository (credentials) needs a recent re-authentication.
  - **Permissions** (`Permissions.cs`): `backup.run` (back up a VM, run jobs with Global scope, verify), `backup.restore`
    (restore VMs, disks, files, restore tests), `backup.manage` (Global: repositories, jobs, staging folder, delete and
    keep points). A point is evaluated on its VM while the VM exists, on Global afterwards. The built-in role *Backup
    operator* holds `inventory.read`, the three backup permissions, `jobs.read.all` and `vm.create` (restores create VMs).
  - **Long jobs**: backups, verifications, restores, moves, agent updates and template captures have their own
    concurrency budget (`Gateway:Jobs:MaxConcurrentLongJobs`, default 4) besides `MaxConcurrentJobs` (8), so power and
    configuration jobs do not wait behind them; the agent allows them up to 12 h. A backup holds the VM's lock: other
    operations on that VM wait until it ends.
- **Not provided**: no guarantee of application consistency; no incremental or differential copies (no RCT): every
  point is a full copy; no deduplication or compression; no encryption of the repository by HvPortal (use the NAS or
  BitLocker); no immutability/WORM and no second copy of points; no backup of VMs with checkpoints disabled; no
  file-level search or item-level application restore; no cluster/CSV awareness; file browsing not on remote hosts;
  no I/O throttling.
- **Alternatives**: recovery snapshots of `Msvm_VirtualSystemSnapshotService` with resilient change tracking
  (`GetVirtualDiskChanges`) for incremental chains — far more efficient, much more code, kept as research; Windows
  Server Backup — host-level, not per VM from the portal; `Export-VM` of the VM itself — the export of a checkpoint
  gives one point in time while the VM runs.
- Not validated on a real host: everything that touches Hyper-V was exercised only against the test double of the
  gateway tests; real-host plan V56–V62, V65 and V67.

## ADR-030 Portal networks: a name for "virtual switch + access VLAN" (agent protocol 9)
- **Context**: users think in networks ("DMZ", "Customer A"), as in vCenter port groups, not in a switch name plus a VLAN
  number to type correctly on every adapter.
- **Decision**: table `PortalNetworks` (host, name, switch, VLAN 1–4094 or null = untagged, description); names unique
  per host, one network per (switch, VLAN). Create, change, delete: `network.manage` on the host, audited
  (`network.portal.*`); list: whoever sees the host.
  - Requests name the network (`vm.networkId` in the VM wizard, `networkAdapterChanges[].networkId`,
    `addNetworkAdapterNetworkId`); the gateway replaces switch and VLAN with the network's values at request time and
    again when the job runs (`NetworkRules.ResolveAsync`), so a VLAN changed in between is the one applied. For an added
    adapter without a network the VLAN field is cleared.
  - Protocol 9: `NewVmRequest.VlanId` and `VmConfigChange.AddNetworkAdapterVlanId`. `RequiredVersion` returns 9 when
    they are set: an older agent would leave the adapter untagged, i.e. on another network. If the VLAN cannot be set the
    agent disconnects the adapter (a new VM is then not started) instead of leaving it untagged.
  - Changing the VLAN of a network can also re-tag the adapters that are on it now (same switch and old access VLAN,
    read live, at most 50 VMs): one configuration job per VM as the requesting user, only for VMs where that user holds
    `vm.config.network` (the others are returned as skipped).
  - Deleting a network removes only the portal record; adapters keep their switch and VLAN in Hyper-V.
- **Consequences**: a network is a label of the portal: nothing is created in Hyper-V and the physical switch ports must
  carry the VLAN (network team). An adapter is "on" a network when its switch and access VLAN match it. Limits of groups
  (ADR-024) restrict switches, not VLANs; a user with `vm.config.network` can still set any VLAN on an adapter
  (unchanged since v0.3) and a VM creation request can still carry a VLAN without a network. An adapter added to a
  running VM is connected untagged for the moment between `Add-VMNetworkAdapter` and `Set-VMNetworkAdapterVlan`. Trunk
  and private VLAN modes are not managed. Not validated on a real host (real-host plan V53).

## ADR-029 Agent update from the portal, signed packages only (agent protocol 9)
- **Context**: every release needed an administrator on each host to run `Install-Agent.ps1`; that is the main cost of
  keeping agents current. The agent service is deliberately not an administrator and cannot replace its own binaries.
- **Decision**: a chain in which no single component can install code the publisher did not sign:
  1. *Gateway*: job `agent.update` (target a host agent or a management agent; `hosts.manage` at Global scope and a
     recent re-authentication at request, the permission again at execution). It offers the gateway's own package (`packages\HvPortal.Agent-win-x64.zip`) with version, size and
     SHA-256 in an `UpdateAgent` command. Offered only when the agent is connected, speaks protocol 9, reported its
     updater task in `AgentHello.UpdaterInstalled`, and the package manifest says `catalogSigned`.
  2. *Agent* (`Channel/AgentUpdater`, service account): downloads `GET /agent/v1/package` from the **agent port** with its
     own client certificate (same identity check as the channel: internal CA, recorded, not revoked or expired, active
     host or management agent; audited `agent.package.download`) and validates the gateway against the pinned CA;
     refuses more bytes than announced (and more than 1 GB); compares size and SHA-256 (fixed-time); stores
     `<agent data>\update\package.zip` + `request.json`; starts the task with `schtasks /Run`.
  3. *Task* `\HvPortal\HvPortal Agent Update`, registered by `Install-Agent.ps1`: runs as SYSTEM (highest, 1 h limit, no
     parallel instance) `Update-HvPortalAgent.ps1` from `Program Files\HvPortal\Updater` (private ACL, read-only for
     others). Its security descriptor is `D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;<service SIDs>)`: the agent account may
     read and run it, not change it.
  4. *Updater*: copies the package into a new folder only SYSTEM and Administrators can write and compares its SHA-256
     with the request; before expanding it as SYSTEM, checks that every zip entry is a plain relative path inside the
     extraction folder (no `..`, rooted or drive paths, no `:`), so a crafted zip cannot write elsewhere; expands it; requires the **installed** `HvPortal.Agent.exe` to be signed; requires
     `HvPortal.Agent.cat`, a catalog of every file of the package, signed by the **same certificate thumbprint**;
     `Test-FileCatalog` must report every file unchanged; the new `HvPortal.Agent.exe` must carry the same signer and a
     file version not older than the installed one; then runs the package's own `Install-Agent.ps1` (identity, account,
     storage folders and data kept; binaries restored if the new service does not start). Outcome in
     `update\last-result.json`, reported in the next `AgentHello.LastUpdateResult`.
  5. *Job outcome*: `Succeeded` only when the agent reconnects with at least the offered version within 10 minutes;
     `Failed` when the agent refuses or the updater reported a failure; otherwise `Unknown`. Never success on the word of
     the command alone.
  - Packaging: `New-AgentPackage.ps1 -SigningCertificateThumbprint` creates `HvPortal.Agent.cat` (`New-FileCatalog`,
    version 2.0, every file except itself), signs it with SHA-256 and checks it with `Test-FileCatalog`;
    `Build-Release.ps1` passes its signing certificate. An unsigned build has no catalog and is never installed this way.
    A gateway built as a Docker image (ADR-021) builds its agent package inside the image without a certificate: that
    package is unsigned and has no catalog, so agents connected to a container gateway are always updated by hand.
  - API: `GET /agents/updates`, `POST /agents/update-outdated`, `POST /hosts/{id}/agent/update`,
    `POST /management-agents/{id}/update`; a daily notification lists outdated agents (ADR-033).
  - Opt-out: `Install-Agent.ps1 -DisableRemoteUpdate` (or the checkbox of the graphical setup) removes the task. The
    first 0.7.0 installation on a host is always done by hand.
  - Prerequisite: the upgrade fix of `installer/Common.ps1` (0.7.0). `Stop-HvPortalService` waits for the service
    **process** to exit (ends it after 120 s), `Clear-DirectoryContent` retries locked files, and a failed replacement
    puts the previous binaries back and starts them: the first upgrades failed with "Cannot remove clrjit.dll: access
    denied" because `Stop-Service` returns before the process has released its files.
- **Trust**: the signer is pinned by thumbprint to the installed agent; a self-signed signer is accepted with the
  untrusted-root status (`UnknownError`), which only means the signature is present and intact. This makes no build
  "trusted" by Windows, SmartScreen or antivirus products. Whoever holds the signing private key can produce an update
  that every agent installs; a new signing certificate (new thumbprint) stops portal updates until each agent has been
  updated by hand once.
- **Risks to verify** (real-host plan V49, V64): the package is expanded as SYSTEM before its catalog is checked
  (`Expand-Archive` must not write outside the target folder); `Install-Agent.ps1` re-registers the task while the task
  is running; that a non-administrator service SID can query and start a SYSTEM task with this descriptor.
- **Alternatives**: the agent replacing its own files (needs write access to Program Files, against least privilege);
  software distribution tools (WSUS/ConfigMgr/Intune — fine where available, the scripts still work with them); an MSI
  with signed patches (tool chain, see ADR-026).

## ADR-028 VM templates: a copy of the disks of a prepared VM (agent protocol 8)
- **Context**: administrators want the VMware workflow: prepare a VM once, keep it as a model, create new VMs from it
  choosing name and resources. Hyper-V has no template object.
- **Decision**: a template is a folder `<template library of the host>\<template id>` holding **copies** of the
  virtual disks of a VM, plus a record in the portal (`VmTemplates`) with the hardware of that VM.
  - *Create* (`POST /vms/{id}/template`, job `template.capture`): the VM must be off, without checkpoints, with plain
    .vhdx/.vhd disks (no pass-through, no differencing disks). `NewTemplate` copies the disk files; the VM is never
    changed, removed or exported, and can be used again when the job ends. A failed copy removes the folder it
    created.
  - *Use* (`POST /hosts/{id}/vms` with `vm.templateId`, job `vm.create` / `FromTemplate`): the ordinary creation with
    the template's disks copied into the folder of the new VM (`NewVm.TemplateDisks`): the first as boot disk, the
    others attached to the SCSI controller. Name, vCPU, memory, network, location, options are the caller's; the
    generation is the template's; the boot disk can be made larger (never smaller: the guest extends its partition).
    The template files are only read.
  - *Delete* (`POST /templates/{id}/delete`, typed name, job `template.delete`): `RemoveTemplate` deletes the disk
    files directly inside the template folder and then the folder; anything else in it stops the operation with
    nothing deleted. Never a recursive delete of a path received in a request.
  - Why copies and not "convert the VM": a copy cannot lose anything (the VM stays), does not depend on
    `Export-VM`/`Import-VM` compatibility between configurations, and a template can never be started by mistake.
    The price is disk space and copy time.
  - **Paths are the gateway's**: the library folder is chosen per host by `hosts.manage` (`Hosts.TemplateLibraryPath`,
    same syntax rules as the ISO library, system folders excluded); the folder of a template is derived from its id;
    the disk list is what the agent reported when it copied. The API never accepts those from a client and the job
    runner stamps them again from the record at execution (`StampTemplateAsync`); a creation without `templateId`
    never carries template fields. The agent validates again (text + real file system: fixed local drive, no
    hidden/system folders, no junctions) and the script checks that every path is `<library>\<guid>[\file.vhdx]`.
  - **Permissions**: new `templates.manage` (host scope) to create, rename, delete; `vm.create` to use a template
    (on the host, or inside a folder whose limits open the host). Templates are listed only to those two groups of
    people; the location of the files only to who manages them. Inside a folder (ADR-024) every disk of the template
    counts against the disk quota and the usual network/storage rules apply.
  - **Truthful states**: `Creating`, `Ready`, `Failed`, `Unverified`, `Deleting`, written in the same transaction
    as the final state of the job. A template is `Ready` only when the agent returned the list of disks it copied;
    a lost result is `Unverified` (not usable; delete and create again); a request that never reached the host
    leaves no record. A template that stops being ready while a creation from it is queued makes that job fail
    (`template_not_available`): never an empty VM passed off as a deployment.
  - Protocol 8 (agent 0.6.0): `NewTemplate`, `RemoveTemplate`, `NewVm.TemplateDisks`; older agents never receive
    them (an older agent would create an empty VM). Copies may run up to 8 hours (30 minutes for everything else);
    the time allowed is 15 minutes plus 20 seconds per GB.
- **Not done by the portal**: generalizing the guest. A template is an exact copy: without Sysprep (Windows) or
  cloud-init / a cleaned machine-id (Linux) every VM created from it has the same computer name and identifiers. The
  UI says so where templates are created and listed. A vTPM is not copied (a new one is created if asked).
- **Limits of this version**: a template is used on the host where it lives (no copy between hosts); no versions; no
  progress percentage during the copy; creations and template deletions on the same host run one at a time (they
  share the host creation lock). A template contains everything that was on the disks, secrets included: who can
  create VMs from it gets a copy.
- **Alternatives**: `Export-VM` + `Import-VM -Copy -GenerateNewId` (keeps devices but ties the template to switch
  names and processor features of the source, and import errors are opaque); differencing disks on a shared parent
  (fast and small, but every VM then depends on the template file forever); converting the VM in place (no copy
  time, but the VM is gone).
- Not validated on a real host yet: real-host plan V46-V48.

## ADR-027 VM groups already on a host are imported automatically, once
- **Context**: a host can already be organised in Hyper-V VM groups when it is added: by hand, or because another
  gateway mirrored its groups there (a reinstalled or moved gateway). ADR-017 imported them only on request (host page >
  Hyper-V VM groups > Import), so a new gateway showed every VM as ungrouped and looked as if the folders were lost.
- **Decision**: the first mirroring pass of a host (`VmGroupSyncService.SyncHostAsync`, i.e. after the first inventory
  with Hyper-V access) imports the native groups that are already there, then marks the host
  (`Hosts.VmGroupsAdoptedAt`); it never runs again for that host.
  - Nobody asked for it, so it takes only what can be taken over **without changing Hyper-V**
    (`VmGroupImporter.Adoptable`). Once imported, a group is owned by the mirroring, which removes native groups
    without VMs and re-writes memberships; therefore: a VM collection is taken only if it has VMs and the portal knows
    all of them; a management collection only if every group inside it is such a VM collection, belonging to no other
    management collection; a VM collection inside a management collection is taken with it or not at all. Everything
    else (empty groups such as placeholders of other tools, ambiguous hierarchies) is left untouched, listed as "not
    imported", and can still be imported on request.
  - Access: the importer is the one of the request (ADR-017). Imported groups carry no role assignments and a portal
    group that has assignments is never filled (a suffixed group is created instead), so nobody gains access to a VM.
    The native group is then renamed like its portal group; that rename, and the one for characters that are not valid
    in portal names, are the only changes the automatic import can cause on a host. It never deletes a native group
    and never removes a VM from one.
  - A group naming a VM the inventory has not reported yet waits: attempts every 5 minutes (each starts PowerShell on
    the host) for at most 30 minutes, then it is left to the import on request.
  - Audited as `hosts.vmgroups.import`, actor `system`, `automatic: true`. `Gateway:VmGroups:AutoImport=false`
    switches it off. An import on request also marks the host.
  - Upgrade: the migration marks the hosts where the portal already owns native groups; the others (nothing mirrored
    or imported yet) get the automatic import at the first pass after the upgrade.
- **Alternatives**: leave it on request and make the button more visible: the folders still look lost after adding a
  host. Import everything, like the request does: an unattended step would then delete empty native groups created by
  other tools. Continuous two-way synchronisation: two sources of truth, and conflicts between hosts.
- **Consequences**: groups created directly in Hyper-V after the host was added are imported on request only; the
  portal stays the source of truth afterwards (ADR-017). Hierarchies deeper than two levels were flattened by the
  mirroring that wrote them and come back flattened. Not validated on a real host yet (real-host plan V45).

## ADR-026 Graphical setup for the gateway and the agent
- **Context**: installing with PowerShell commands (parameters to remember, names to repeat on every update, PFX
  import by hand) is not friendly for occasional administrators.
- **Decision**: `installer/HvPortal.Setup` builds two wizard programs, `HvPortal.Gateway.Setup.exe` and
  `HvPortal.Agent.Setup.exe`, each a single file with the release payload embedded (binaries + installer scripts).
  - The wizard is a front end, not a second installer: it runs the same `Install-Gateway.ps1` / `Install-Agent.ps1`
    through `installer\Invoke-Setup.ps1`. Choices are written to a JSON file and splatted to the script, so nothing
    typed in the wizard reaches a command line or is parsed as PowerShell; unknown parameter names are refused; the
    enrollment token travels in the `HVP_ENROLLMENT_TOKEN` environment variable of the child process. Every field is
    also validated by `SetupLogic.cs` (names, ports, thumbprint, firewall addresses, gateway URL, fingerprint, account).
  - The payload is unpacked into `Program Files\HvPortal\Setup\<component>-<guid>` with a protected ACL
    (Administrators and SYSTEM only) and removed at the end: the scripts run elevated, so no other user may change
    them in between; it also avoids running anything from `%TEMP%`.
  - Gateway: on an update the settings are read from the service environment and pre-filled; a .pfx can be imported
    into `LocalMachine\My` (machine key, not exportable) from the wizard; ports in use are reported before installing
    (the ports the running gateway already holds are its own). `Install-Gateway.ps1` repeats the port and certificate
    checks **before** it stops the running service, so a refused update leaves the working gateway up.
  - Agent: the portal shows one line of connection data (`HVPORTAL-SETUP gateway=... ca=... token=... role=...`) that
    the wizard reads from the clipboard; the portal serves the agent setup from `packages\` next to the zip
    (`GET /downloads/agent-setup`, `hosts.manage`, audited) and the guide offers "Guided setup" first. When the pasted
    data were created for the other kind of agent (host / management), the wizard follows them and says so.
  - .NET Framework 4.7.2 / Windows Forms: present on Windows Server 2019 and later, so the setup is one small file
    that starts without installing or unpacking a runtime. Elevation is requested by the program itself (UAC).
- **Alternatives**: MSI with WiX or Inno Setup — external tool chains to license and download, and a second
  installation logic to keep aligned with the scripts; a self-contained .NET 10 single-file wizard — 70 MB larger
  per setup and it unpacks native libraries to `%TEMP%` at start (blocked by some antivirus policies).
- **Consequences**: the scripts stay the single source of truth (and the way for unattended installs:
  `--extract`). A setup that is unsigned or signed with a self-signed certificate triggers SmartScreen until the
  publisher certificate is trusted. The wizard needs a desktop session; on Server Core use the scripts. The wizard
  pages are checked by rendering them (`--render`); the installation itself needs elevation and is in the real-host
  plan (V43–V44).

## ADR-025 Virtual switches, checkpoint apply on a running VM, VM notes (agent protocol 7)
- **Virtual switches**: three typed operations — `ListNetAdapters` (physical adapters and the switch bound to each),
  `NewSwitch` (`NewSwitchRequest`: name, External/Internal/Private, physical adapter and "share with the host" for
  external) and `RemoveSwitch` (by id, refused by the agent while VM adapters are connected). They are jobs on the host
  (`host.network`, serialized with VM creations) and need the new permission `network.manage` (host scope: any type;
  group scope: private networks only, ADR-024). Names of new switches are restricted (letters, digits, space . _ ( ) -)
  and must be unique on the host, because VMs are connected by switch name. Creating or removing an external switch on
  the adapter the agent uses drops its channel for a few seconds: such jobs are reconciled from the host (the switch
  exists / is gone), never reported as success blindly.
- **Apply checkpoint on a running VM**: the agent turns the VM off (`Stop-VM -TurnOff`) and then restores the
  checkpoint; the job result reports the state after the restore. The current state is discarded by an apply anyway,
  and the typed VM name is still required. Agents before protocol 7 keep the old rule (VM off or saved).
- **VM notes instead of a portal description**: the portal-only description of a VM is removed; the Hyper-V notes
  (already synchronized from the host) are the VM's description everywhere (tables, search, tree filter). Editing them
  is a configuration job that needs `inventory.annotate` (not `vm.config.compute`), so VM operators keep annotating.
  The migration keeps every existing description as an entry of the VM history (`vm.description.removed`).
- **Other protocol 7 changes**: `NewDiskSpec.Folder` (set by the gateway only, ADR-024) and
  `VmSummary.MemoryMaximumMb`. `RequiredVersion` returns 7 for the switch operations and for a new disk with a folder
  (an older agent would create the disk in the host default folder).
- **Not validated on real hosts**: real-host test plan V38–V42.

## ADR-024 Limits of VM groups: quotas, hosts, networks and storage ("folders as projects")
- **Context**: a group (folder) is given to a team or customer with role assignments; they must be able to create
  VMs and networks by themselves, but only within limits, like a project of a cloud provider.
- **Decision**: a group can carry a `GroupPolicy` (`LogicalGroups.PolicyJson`):
  - quotas on the totals of the group **and its subgroups**: VMs, vCPU, memory, virtual disk space, private networks;
  - `hosts`: where users whose permissions come from the group may create VMs and networks;
  - `networks`: the switches the group's VMs may use (null = any; empty = only the group's own private networks);
  - `storage`: folders of a host that may contain the group's VMs and that group users may browse.
  Every group above a VM applies: quotas of each ancestor must have room, lists are intersected.
- **Group-scoped creation**: `vm.create`, `network.manage`, `host.files.read` and `host.files.write` can now be held
  on a group. There they work only on the hosts listed by the group's limits (intersection along the chain) and, for
  files, only inside the group's storage paths. The built-in role *Folder administrator* (`folder-admin`) bundles the
  VM permissions with these four. A user who may create VMs in a group places them there without `groups.manage`: the
  group's role assignments then apply to the new VM, which is the purpose of the group (the anti-escalation check of
  ADR-015 still applies to everyone else).
- **Enforcement** (`Quotas/GroupPolicyService`, used by the API and again by the job runner at execution):
  - new VM: allowed network, location inside the storage paths (set by the gateway when none is chosen), quotas;
    with a memory quota a dynamic-memory VM must state its maximum (it is what counts);
  - configuration change: new network connections must be allowed; growth of vCPU, memory and disks must fit; a new
    disk goes to `<VM folder>\Virtual Hard Disks` when storage is restricted (`NewDiskSpec.Folder`, never taken from
    the client);
  - private network: quota; it then belongs to the group (`GroupNetworks`) and only that group's users (or host
    network administrators) may delete it;
  - usage = VMs of the subtree from inventory (memory = startup, or the dynamic maximum when the agent reports it;
    disk = sum of the virtual disk capacities read from the hosts with `ListHostDisks`, remembered per VM) **plus the
    creations already queued** for the subtree. At execution only earlier requests count, so of two competing requests
    the first keeps its place. Unknown disk usage (host unreachable and never read) refuses the operation.
  - Quotas bind everybody, administrators included (they can raise them). Adding existing VMs to a group is not
    blocked by quotas (it is an administrator action); the group then shows as over its limits and cannot grow.
- **Who sets limits**: `groups.manage` **and** `hosts.manage` (the limits decide which host resources group users can
  reach). Audited (`groups.policy.update`, before/after).
- **Alternatives**: quotas per user or per role assignment — harder to reason about with nested groups; a separate
  "tenant" object — duplicates the group tree; soft quotas (warning only) — not what a provider-style limit means.
- **Consequences**: existing behaviour is unchanged for groups without limits. Group users see only the names of
  the VMs they can read on a host (the creation wizard no longer lists every VM name to them). Limits are enforced by
  the portal only: changes made directly in Hyper-V are not prevented, they show up as usage.
- (0.7.0) A VM move must keep the VM within the hosts and storage paths of its folders (`CheckMoveAsync`, ADR-035).
  Restores as a new VM are not placed in a group and do not count against its quotas (ADR-031).

## ADR-023 Automatic local copies of the database
- **Context**: SQLite is a single file; a damaged file (disk fault, interrupted write on a non-journaled volume)
  would lose users, roles, groups, hosts and audit unless a sealed backup (ADR-020) had been downloaded recently.
- **Decision**: `Backup/DatabaseBackupService` copies the database with the SQLite online backup API once a day
  (`Gateway:Database:AutoBackupIntervalHours`, default 24) into `<data>\db\backups\hvportal-auto-<UTC>.db`, runs
  `PRAGMA integrity_check` on the copy and keeps it only when the result is `ok`; copies older than 7 days
  (`AutoBackupRetentionDays`) are removed, but only after a newer verified copy exists and never the newest one.
  A failed copy is reported in Settings > Backup and in the log. Settings shows the copies and offers "Copy now"
  (`settings.manage` + re-authentication, audited).
- **Limits (stated in the UI)**: the copies are on the same disk and contain no keys (the key ring and CA are
  elsewhere in the data directory): they protect against database corruption, not against the loss of the server.
  Restoring one is a manual, offline step (stop the service, replace `hvportal.db`, remove `-wal`/`-shm`).

## ADR-022 Management agents: remote hosts reached with stored credentials (agent protocol 6)
- **Context**: some Hyper-V hosts are in a workgroup and the administrators prefer not to install anything on them;
  a management server with the Windows administration tools is available. ADR-002 keeps host credentials off the
  gateway; this is an **optional** second mode, the agent on the host stays the recommended one.
- **Decision**: a *management agent* is the same agent binary installed with `-Management` on a server that needs no
  Hyper-V. It is enrolled like a host agent (one-time token, CA pin, own certificate) but its subject is a
  `ManagementAgent` row, not a host; the gateway returns `Role = Management` at enrollment. Remote hosts
  (`ManagedHost.ConnectionMode = Remote`) are created in the portal with management agent, computer name (DNS or IPv4,
  never loopback or IPv6 literals), user name, password and WinRM transport. One management agent serves many hosts.
  - Credentials: the password is encrypted with the gateway key ring (AES-256-GCM, purpose bound to the host id),
    write-only in the API (never returned, never audited: audit entries record computer, user, transport and
    `passwordChanged`), deleted with the host. Requires `hosts.manage` at global scope and a recent re-authentication.
  - Channel: after `Hello` the gateway sends `TargetsConfiguration` (the agent's remote hosts with decrypted
    credentials) over the mutual-TLS channel, again after every change (create, edit, move, revoke, delete). The
    agent keeps it in memory only (SecureString) and drops it when the channel closes. Every command to a remote host
    is stamped by the gateway with `TargetHostId`; inventories come back as `TargetInventory`, and the gateway discards
    any inventory for a host not assigned to the reporting agent. A host agent rejects commands with a target, a
    management agent rejects commands without one.
  - Transports (from the management server to the host, with the stored account): WMI/DCOM with packet privacy for
    inventory, power, delete, details and console (the same `root\virtualization\v2` code as a local agent); WinRM for
    the typed operations — the fixed launcher `Ops\HvPortal.Remote.ps1` runs `Invoke-Command -FilePath` with the same
    `HvPortal.Ops.ps1` and passes the JSON request as its only argument (no script block is built, the password comes
    on stdin); SMB administrative shares (`\\host\D$`) for the folder browser and the file explorer, accessed under
    `LogonUser(LOGON32_LOGON_NEW_CREDENTIALS)` so the account is used only for outbound access. All folder rules are
    applied to the host-local path and to what the host reports (fixed drives from `Win32_LogicalDisk`). The CPU
    sampler has no local fallback for remote hosts (it would report the management server).
  - Protocol 6 (agent 0.4.0); `RequiredVersion` returns 6 for any command with a target.
- **Alternatives**: credentials entered only on the management server (DPAPI, never on the gateway) — safer, but the
  user wanted portal-managed credentials; one service instance per remote host — simpler routing, but heavier to
  operate; PowerShell Direct / CIM sessions everywhere — would rewrite every operation.
- **Consequences**: the gateway becomes a custodian of host administrator passwords for the hosts that use this mode:
  a compromise of the gateway *and* its key ring exposes them (threat model T13). Hosts must accept WMI/DCOM, WinRM and
  SMB from the management server; in a workgroup a non-built-in local administrator needs
  `LocalAccountTokenFilterPolicy = 1` and WinRM over HTTP needs the host in the management server's `TrustedHosts`.
  Outcome reporting is unchanged: a broken WinRM session is reported as a failure with "outcome unknown" text, never as
  success. Not validated on real hosts yet (real-host test plan V29–V34).
- (0.7.0) The allowed storage folders of a remote host are set in the portal and sent in `RemoteTarget.StorageFolders`
  (ADR-034); the management agent itself can be updated from the portal (ADR-029). Browsing the files of a restore
  point needs the agent on the host (ADR-031).

## ADR-021 Gateway also as a Linux container
- The gateway targets `net10.0` (was `net10.0-windows`) and runs either as a Windows service or in a Linux container
  (`docker/`); the agent stays Windows-only. Platform-specific parts are selected at run time:
  key ring wrapping DPAPI vs AES-256-GCM with a key-encryption key (`HVP_KEY_ENCRYPTION_KEY[_FILE]`, generated in the
  volume with a warning if missing), Data Protection keys DPAPI vs sealed with the key ring, private files NTFS ACLs
  vs POSIX 0700/0600, UI certificate from the Windows store (thumbprint) vs PFX/PEM file.
- The key file records its protection (`dpapi` / `kek-aesgcm`); a DPAPI key ring cannot be opened in a container:
  migration uses the portal backup/restore (keys are re-wrapped for the target).
- Image: multi-stage (Node for the SPA, .NET SDK for the gateway and the cross-published win-x64 agent package),
  runtime `aspnet:10.0`, non-root, read-only root file system, no capabilities, `admin health` as HEALTHCHECK.
  Authenticode cannot be checked on Linux: a package built in the image is reported as unsigned.

## ADR-020 Portal backup and restore from Settings
- One file for disaster recovery instead of three procedures (DB copy with the service stopped, key export, CA):
  `KeyRing.Export` seals the key ring plus extra files with the passphrase; the extra files are a manifest, the SQLite
  online-backup snapshot of the database and the CA/server certificate blobs.
- Restore never swaps the database under a running process: the decrypted content is staged (keys re-protected with
  DPAPI, private ACLs) and applied by the next start before the key ring and EF Core open their files; the previous
  state is moved aside, not deleted. Sessions of the restored database are revoked.
- Only holders of every privileged permission, with re-authentication, typed confirmation for the restore; every
  step audited. The passphrase travels in a request header over HTTPS and is never stored or logged.

## ADR-019 Microsoft Entra ID sign-in and user onboarding
- Entra ID (OpenID Connect authorization code flow + PKCE, confidential client, single tenant) implemented directly
  on `Microsoft.IdentityModel.Protocols.OpenIdConnect` (metadata + signing keys) and `JsonWebTokenHandler`
  (signature RS256, issuer of the configured tenant, audience, lifetime, nonce, `tid`). The configuration lives in
  Settings (no restart): tenant, client id, cloud instance (global / US Gov / China only), client secret encrypted
  with the key ring and never returned or logged. Outbound HTTPS to Microsoft only when Entra is enabled.
- Users are **pre-provisioned** (Users > New user > Microsoft, by UPN) and bound to the Entra object id at their first
  sign-in; later sign-ins match the object id only (a recreated account with the same UPN is refused). No password
  is stored for them; local password sign-in is refused. Roles are assigned in the portal as for local users.
- MFA for Entra users is delegated to Entra ID (Conditional Access): the portal does not enroll a local authenticator.
  Sensitive actions re-authenticate them with Entra (`prompt=login`, `max_age=0`) in a popup that marks the session.
- State, nonce and PKCE verifier are kept in a data-protected, 10-minute, `SameSite=Lax` cookie scoped to
  `/api/v1/auth/entra` (Lax is required for the top-level redirect back from Microsoft); the session cookie stays
  `Strict`. "Local sign-in: only emergency accounts" turns the portal into Entra-only with a break-glass exit.
- New local users get a temporary password and, by default, `MfaEnrollmentRequired`: after the forced password
  change they must enroll an authenticator before using the portal, regardless of the global MFA policy. The creation
  dialog also takes the role and its scope (same anti-escalation rules as role assignments). The re-authentication
  dialog states explicitly that it asks for the **administrator's own** password and code.

## ADR-018 Host file explorer: browse, download, upload (agent protocol 5)
- Need: copy ISOs and other files to/from hosts from the browser (Storage > Files), without RDP or SMB shares.
- Typed, in-process agent operations (no PowerShell): `ListFolder`, `ReadFile`, `WriteFileBegin/Chunk/Commit/Abort`.
  Same folder rules as ADR-016 (local fixed drives; hidden, system and reparse-point entries skipped; top-level
  system folders and `$...` refused), enforced by gateway and agent. File names: no separators, streams, wildcards,
  device names, trailing dot/space.
- Transfers are relayed in 4 MB chunks over the agent channel; nothing is stored on the gateway. Downloads stream to
  the browser and are aborted (not truncated) on failure. Uploads require Content-Length, go to a hidden
  `.<name>.<id>.hvpart` file next to the target and are renamed only after size and SHA-256 (computed by the gateway
  while relaying, re-computed by the agent) match. Existing files are replaced only with explicit overwrite (the UI
  asks). Idle uploads are discarded after 10 minutes; at most 4 concurrent uploads per host.
- Reads open files with `FileShare.Read`: a file open for writing elsewhere (the VHDX of a running VM) cannot be
  downloaded, so the portal never hands out an inconsistent copy of an active disk.
- Permissions `host.files.read` (browse + download) and `host.files.write` (upload), scoped Global/Cluster/Host;
  included in the platform and infrastructure administrator roles. Every download and upload is audited with path,
  size and SHA-256. The agent writes with its own identity (virtual service account): folders it cannot write in
  return `access_denied`.
- (0.7.0) A host can restrict these folders to an allowed list decided on the host, and new folders can be created
  inside them: ADR-034.

## ADR-017 Portal groups mirrored as native Hyper-V VM groups (agent protocol 4)
- Requirement: groups created in the portal must be visible with `Get-VMGroup` on the hosts. The portal stays the
  source of truth; permissions still come only from portal groups (native groups never grant anything).
- Mapping per host (`VmGroupPlanner`), within the documented Hyper-V model (a ManagementCollectionType contains only
  VMCollectionType groups): a portal group with VMs on the host → VM collection with its name; a portal group with VM
  collections below it → management collection with its name containing its own VM collection and those of **all**
  descendants (levels deeper than two are flattened). Groups with no VM on a host are not created there.
- Ownership: `HostVmGroupLinks` stores the native id of every group the portal created or imported. `SyncVmGroups`
  receives the desired items plus the list of owned ids and only renames, re-members or removes those; anything else
  in Hyper-V (backup tools, manual groups) is left untouched. New groups are created with `New-VMGroup -Id <guid>`.
  Removal empties the group first (`Remove-VMGroup` requires it).
- Triggers: group changes and host changes (inventory, connection) raise a debounced signal; a pass sends only if the
  plan hash changed; every 30 minutes a forced pass corrects drift made directly in Hyper-V. Hosts without Hyper-V
  access are skipped; after a failure automatic passes wait 5 minutes. Changes are audited (`hosts.vmgroups.sync`,
  actor `system`).
- Import (`hosts.manage` + `groups.manage`, audited): native groups not owned yet become portal groups (management
  collection → root group, its VM collections → sub-groups, a VM collection with the same name as its parent gives the
  parent its direct VMs). Existing portal groups are reused only when no role assignment applies to them, otherwise a
  suffixed group is created, so importing never changes who can access a VM.
- Protocol 4 (agent 0.3.2): older agents never receive these commands.
- (0.5.1) The groups a host already has are imported automatically at its first mirroring pass: ADR-027.

## ADR-016 Host folder browser and VM location (agent protocol 3)
- The location and ISO pickers need to see the host's folders. `BrowseFolders` is a read-only operation executed
  in-process by the agent (.NET file-system APIs, no PowerShell): only local fixed drives; hidden, system and
  reparse-point entries (junctions, symbolic links, mount points) are skipped; the top-level system folders of a drive
  (`HostPaths.BlockedRootFolders`: Windows, Users, Program Files, ProgramData, inetpub, PerfLogs, VBRCatalog, ovhupd,
  Packages, ...) and every `$...` folder are neither listed nor accepted. Max 500 entries per folder.
- Folders are listed to users with `vm.create` on the host; `.iso` files only to `hosts.manage`, because choosing an ISO
  outside the library adds its folder to the host's ISO library (audited). Non-administrators still pick only ISOs
  inside the library.
- `NewVmRequest.Location`: validated by the gateway (syntax, blocked folders) and by the agent against the real file
  system (fixed drive, no hidden/system/reparse ancestors). The script creates `<location>\<name>\Virtual Hard Disks`
  and calls `New-VM -Path <location>` (Hyper-V adds `<name>\Virtual Machines`); it refuses a non-empty existing
  `<location>\<name>` and removes only the empty folders and the fixed disk it created if `New-VM` fails.
- Protocol 3: older agents would ignore `location` (creating the VM elsewhere) or fail to parse `BrowseFolders`.
  `AgentProtocol.RequiredVersion(command)` is checked before sending; agents 0.3.1+ answer `Rejected` to unknown
  commands so a future gateway cannot break their channel.
- (Superseded by ADR-017: portal groups are now mirrored as native Hyper-V VM groups.)
- (0.7.0) Every folder accepted by `CheckFolder` must also be inside the allowed storage folders of the host: ADR-034.

## ADR-015 Nested logical groups and access-change guard
- Groups are folders: `LogicalGroup.ParentId`, max depth 16, sibling names unique, cycles refused. Role assignments
  on a group apply to its VMs and to every subgroup (`AccessEvaluator` adds the ancestor chain).
- Because moving a VM or a group changes who can access the VM, membership and tree changes go through
  `GroupTree.CheckAccessChangeAsync`: if the change makes **Allow** assignments newly apply or **Deny** assignments
  stop applying, the actor needs `roles.manage` and must already hold every permission of those roles on each
  affected VM (for a VM being created: on its host). Otherwise `groups.manage` is enough.
- Deleting a group requires it to have no subgroups and no role assignments.