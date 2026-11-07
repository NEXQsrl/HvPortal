# Architecture

## Components

```
┌────────────── Management server (or a Hyper-V host, or a management VM) ─────────────┐
│  HvPortal.Gateway  — Windows service, NT SERVICE\HvPortalGateway (virtual account)      │
│   ├─ Kestrel :8443  browser UI + REST API (/api/v1) + SignalR (/hubs/events)            │
│   ├─ Kestrel :8444  agent endpoints only (/agent/v1: ca, enroll, channel, package) — mTLS │
│   ├─ Modules: Auth/MFA · Sessions · RBAC · Groups · Inventory · Jobs · Console · Audit · PKI │
│   ├─ v0.7: Networks · VmBackup (repositories, jobs, scheduler, file restore) ·          │
│   │        notifications (SMTP, webhooks) · API tokens (/api/v1/public) · agent updates  │
│   ├─ SQLite (EF Core migrations)  ·  key ring (DPAPI-wrapped, exportable)  ·  ASP.NET DP │
│   └─ Admin CLI (same exe): recovery tokens, key export/import/rotation, audit verify     │
└────────────────────────────────────────────────────────────────────────────────────────┘
            ▲ outbound WebSocket over TLS with client certificate (agent → gateway)
┌──────── Hyper-V host ───────────────────────────────────────────────────────────────────┐
│  HvPortal.Agent — Windows service, NT SERVICE\HvPortalAgent ∈ Hyper-V Administrators     │
│   ├─ Channel: reconnect with backoff, inventory every 30 s, certificate renewal          │
│   ├─ CommandExecutor: closed set of typed commands, per-VM serialization, dedup by id   │
│   ├─ WmiHyperVHost: root\virtualization\v2 + root\cimv2 (System.Management)            │
│   ├─ ConsoleService: screen capture + keyboard/mouse injection (WMI, per-VM session)    │
│   ├─ PowerShellOperationRunner → Ops\HvPortal.Ops.ps1 (Hyper-V module, fixed script)   │
│   ├─ LocalStorageFolders (storage.json) · BackupMounts + BackupMountJanitor (v0.7)      │
│   └─ AgentUpdater (v0.7) → schtasks /Run "\HvPortal\HvPortal Agent Update"              │
│  Scheduled task (SYSTEM) → Program Files\HvPortal\Updater\Update-HvPortalAgent.ps1:     │
│   checks the signed catalog of the package against the installed agent's signer        │
└────────────────────────────────────────────────────────────────────────────────────────┘
            │ SMB (v0.7): restore points in a host folder, a domain share or a NAS share
```

The control plane is a **modular monolith**: one process, modules separated by namespace and by DI boundaries
(`Agents`, `Api`, `Audit`, `Authorization`, `Backup` (portal backup), `Bootstrap`, `Configuration`, `Crypto`, `Data`,
`Hosting`, `Jobs`, `Networks`, `Pki`, `Quotas`, `Realtime`, `Security`, `Templates`, `VmBackup` (VM backups,
notifications, API tokens), `VmConsole`, `VmGroups`).
Privileged work happens only in the agent. Same-server installations still run two services and two identities.

**Management agents (ADR-022, optional).** The same agent installed with `-Management` on a server without Hyper-V
manages *remote hosts* with credentials stored in the portal: the gateway routes each remote host id to that channel,
sends the host list with credentials (`TargetsConfiguration`) and stamps commands with `TargetHostId`; the agent runs
one `WmiHyperVHost`/console/operations runner per target (WMI/DCOM with credentials, `Ops\HvPortal.Remote.ps1` →
`Invoke-Command -FilePath Ops\HvPortal.Ops.ps1` over WinRM, SMB administrative shares for files) and reports
`TargetInventory` per host.

## Request flow: "start VM" (browser → host)

1. Browser `POST /api/v1/vms/{id}/power {operation:"Start", idempotencyKey}` with session cookie + `X-CSRF-TOKEN`.
2. Middleware: port separation → security headers → rate limiter → cookie auth (session re-validated in DB on every
   request) → Origin + anti-forgery check → authorization policy (no pending restriction).
3. Endpoint: VM must be readable (else 404), user must hold `vm.power` on the VM scope (else 403 + audit *Denied*),
   destructive operations require the VM name typed back, state checked against fresh inventory.
4. `JobService` persists a `Queued` job (idempotent per user+key) and wakes the runner; audit *requested*.
5. `JobRunner`: claims via conditional UPDATE + lease, acquires per-VM lock row, **re-checks user status and
   permission**, resolves the owning host now, persists `DispatchedAt`, sends `AgentCommand{CommandId=JobId}`.
6. Agent: validates the command, serializes per VM, de-duplicates by command id, calls
   `Msvm_ComputerSystem.RequestStateChange(2)`, follows `Msvm_ConcreteJob`, then polls until the VM is `Running`.
7. Result → job `Succeeded` / `Unverified` / `Failed` with Hyper-V return code; audit; SignalR `jobChanged` to the
   requester; a fresh inventory is pushed and `inventoryChanged` notifies authorized viewers.
8. Failure paths: host offline before dispatch → retried (not sent); channel lost / timeout after dispatch →
   `Reconciling` → outcome derived from observed VM state (never re-sent) → `Succeeded` or `Unknown`.

## Request flow: "change VM settings" (v0.3)

1. The UI reads `GET /vms/{id}/config` (live, through the agent's fixed script) and sends only changed fields with
   `PATCH /vms/{id}/config`.
2. The API derives the required permissions from the fields (`vm.config.compute` / `.storage` / `.network`), checks
   each one, validates values (`OperationValidation`), refuses off-only fields on a running VM (409), checks ISO
   paths against the host ISO library and switch names against inventory, stamps the Hyper-V VM id and the ISO roots
   itself, and queues a `vm.config/Update` job with the arguments in `Job.ParametersJson`.
3. `JobRunner` re-checks every permission and the user, rebuilds the arguments from current data (VM id, ISO roots),
   validates them again and sends `AgentCommand{Kind=HyperVOperation, Operation=SetVmConfig}`.
4. The agent validates again, serializes per VM and runs `powershell.exe -File Ops\HvPortal.Ops.ps1` with the JSON
   request on stdin; the script applies the steps in a fixed order and returns `{ok, result, error, applied}`.
5. The job ends `Succeeded` ("Applied: …") or `Failed` (with the steps applied before the error); an uncertain
   outcome is `Unknown` and never re-sent. The agent pushes a fresh inventory.

Creation (`POST /hosts/{id}/vms`) and checkpoints follow the same path; creation is serialized per host and, when a
group was requested, the created VM is placed after it appears in inventory.

## Request flow: scheduled VM backup (v0.7.0, ADR-031)

1. `BackupScheduler` (every 20 s) finds a due backup job, resolves its VMs (VMs + groups with sub-groups) and creates a
   run: one `BackupPoint` (`Creating`, folder `<vm key>\<point id>`) and one persistent job `backup.vm` per VM, as the
   user who last saved the backup job. The job parameters carry no credentials.
2. `JobRunner` claims the job within the long-job budget (`MaxConcurrentLongJobs`), takes the VM lock, re-checks
   `backup.run` on the VM, stamps the VM id and — from the repository record — path, credentials (decrypted now with
   the key ring) and the host's staging folder, then sends `HyperVOperation/BackupVm`.
3. The agent validates the request (folders against the allowed storage folders) and runs the fixed script:
   checkpoint, `Export-VMSnapshot` (directly into the repository, or into staging and then copied to a share opened
   with the credentials), checkpoint removed, SHA-256 of every file, manifest written last.
4. The result marks the point `Ok` only with a valid manifest of that point (`BackupJobOutcome`); a lost result makes
   it `Unverified`; the scheduler closes the run, applies retention (deletion jobs `backup.remove`) and sends
   notifications.

## Request flow: agent update (v0.7.0, ADR-029)

1. `POST /hosts/{id}/agent/update` (`hosts.manage`) creates job `agent.update`; the runner sends `UpdateAgent` with the
   version, size and SHA-256 of the gateway's package.
2. The agent downloads `GET /agent/v1/package` on port 8444 with its client certificate, checks size and SHA-256 and
   starts the SYSTEM task; the task checks the signed catalog against the installed agent's signer and runs the
   package's `Install-Agent.ps1`.
3. The job succeeds when the agent reconnects with the new version (`AgentHello`), fails when the updater reports a
   failure, and is `Unknown` after 10 minutes without either.

## Data model (main tables)

`Users`, `Sessions`, `MfaChallenges`, `RecoveryCodes`, `LoginThrottles`, `OneTimeTokens` · `Roles`,
`RolePermissions`, `RoleAssignments`, `LogicalGroups` (`ParentId`: folders), `LogicalGroupMembers` · `Hosts`, `AgentCertificates`,
`EnrollmentTokens`, `VirtualMachines`, `InventoryEvents` · `Jobs` (`ParametersJson`, `ResultJson`), `JobEvents`, `ResourceLocks` · `AuditEntries`,
`Settings` · (v0.7.0) `PortalNetworks` · `BackupRepositories`, `BackupJobs`, `BackupJobTargets`, `BackupRuns`,
`BackupPoints` · `ApiTokens`; host columns `StorageFoldersJson`, `BackupStagingPath`, `AgentUpdaterInstalled`,
`AgentLastUpdateResult` (migration `BackupsNetworksAgentUpdates`). Notification settings are a `Settings` row.

VM identity: portal id (GUID) + unique `(HostId, HyperVId)`. Hyper-V ids are not globally unique (`Import-VM -Copy`
can keep the id). In clusters (M3) a VM moving between nodes will be re-homed by Hyper-V id within the cluster.

## Three separate records

| Record | Where | Purpose |
|---|---|---|
| Security audit | `AuditEntries` (HMAC chain) | who did what, with which authentication, outcome, correlation id |
| Diagnostics | Windows Event Log (Application, sources *HvPortal Gateway/Agent*) + console | troubleshooting; no secrets |
| Operation history | `Jobs`/`JobEvents`, `InventoryEvents` | progress, results, out-of-portal changes |

## Technology choices

See [decisions.md](decisions.md). Summary: .NET 10 LTS (support to Nov 2028), ASP.NET Core minimal APIs,
EF Core 10, SignalR, built-in OpenAPI, React 19 + TypeScript + Vite, `System.Management` for WMI, BCL cryptography
(PBKDF2, AES-GCM, HKDF, ECDSA, `CertificateRequest`, DPAPI), Windows ACLs, `sc.exe` and NetSecurity cmdlets for
installation, the Hyper-V PowerShell module on hosts for configuration operations. Third-party runtime dependencies:
QRCoder (MIT) in the gateway; React, React Router, SignalR client, Radix UI/shadcn, Tailwind, lucide, cmdk in the
browser (see [licensing](licensing.md)). v0.7.0 adds none: e-mail uses the BCL `SmtpClient`, webhooks `HttpClient`,
package signing and checks the built-in `New-FileCatalog` / `Test-FileCatalog` / Authenticode cmdlets.
