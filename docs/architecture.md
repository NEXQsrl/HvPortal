# Architecture

## Components

```
┌────────────── Management server (or a Hyper-V host, or a management VM) ─────────────┐
│  HvPortal.Gateway  — Windows service, NT SERVICE\HvPortalGateway (virtual account)      │
│   ├─ Kestrel :8443  browser UI + REST API (/api/v1) + SignalR (/hubs/events)            │
│   ├─ Kestrel :8444  agent endpoints only (/agent/v1: ca, enroll, channel) — mTLS        │
│   ├─ Modules: Auth/MFA · Sessions · RBAC · Inventory · Jobs · Audit · PKI · Agent hub    │
│   ├─ SQLite (EF Core migrations)  ·  key ring (DPAPI-wrapped, exportable)  ·  ASP.NET DP │
│   └─ Admin CLI (same exe): recovery tokens, key export/import/rotation, audit verify     │
└────────────────────────────────────────────────────────────────────────────────────────┘
            ▲ outbound WebSocket over TLS with client certificate (agent → gateway)
┌──────── Hyper-V host ───────────────────────────────────────────────────────────────────┐
│  HvPortal.Agent — Windows service, NT SERVICE\HvPortalAgent ∈ Hyper-V Administrators     │
│   ├─ Channel: reconnect with backoff, inventory every 30 s, certificate renewal          │
│   ├─ CommandExecutor: closed set of typed commands, per-VM serialization, dedup by id   │
│   └─ WmiHyperVHost: root\virtualization\v2 + root\cimv2 (System.Management)            │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

The control plane is a **modular monolith**: one process, modules separated by namespace and by DI boundaries
(`Agents`, `Api`, `Audit`, `Authorization`, `Bootstrap`, `Crypto`, `Data`, `Jobs`, `Pki`, `Realtime`, `Security`).
Privileged work happens only in the agent. Same-server installations still run two services and two identities.

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

## Data model (main tables)

`Users`, `Sessions`, `MfaChallenges`, `RecoveryCodes`, `LoginThrottles`, `OneTimeTokens` · `Roles`,
`RolePermissions`, `RoleAssignments`, `LogicalGroups`, `LogicalGroupMembers` · `Hosts`, `AgentCertificates`,
`EnrollmentTokens`, `VirtualMachines`, `InventoryEvents` · `Jobs`, `JobEvents`, `ResourceLocks` · `AuditEntries`,
`Settings`.

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
installation. Third-party runtime dependencies: QRCoder (MIT) in the gateway, React/React Router/SignalR client (MIT)
in the browser.
