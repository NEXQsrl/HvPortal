# HvPortal

Browser-based management for Microsoft Hyper-V hosts — standalone or (later) clustered, in a domain or in a
workgroup — with local accounts, MFA, fine-grained RBAC, persistent jobs and a tamper-evident audit log.

> **Status: Milestone 1 (pre-release, private).** Not published; do not distribute.
> "Hyper-V", "Windows Server" and "Microsoft Entra" are trademarks of Microsoft. HvPortal is an independent
> project and is not affiliated with or endorsed by Microsoft. The product name is provisional.

## What works today (Milestone 1)

| Area | State |
|---|---|
| Gateway (ASP.NET Core 10, Windows service, virtual account) | ✅ implemented, tested |
| Agent (Windows service, `Hyper-V Administrators` only, outbound mTLS) | ✅ implemented; Hyper-V calls **not yet validated on a real host with VMs** |
| Enrollment: one-time token (15 min), CA pinning, certificate renewal/revocation | ✅ implemented, tested end-to-end |
| First run: one-time setup token readable only by local administrators, no default credentials | ✅ |
| Local accounts: PBKDF2, NIST-style policy (15–256 chars), progressive delays, recovery via local admin CLI | ✅ |
| TOTP MFA (local QR), replay protection, single-use recovery codes, MFA mandatory for admins by default | ✅ |
| RBAC: scopes Global/Host/Group/VM, explicit deny, default deny, anti-escalation | ✅ |
| Inventory (hosts, VMs) + out-of-portal change detection | ✅ code; real-host validation pending |
| VM power: start, guest shutdown/restart, turn off, reset, pause, resume, save — as persistent jobs | ✅ code; real-host validation pending |
| Jobs: leases, per-VM locks, permission re-validation, restart reconciliation, safe cancellation | ✅ tested |
| Audit log with HMAC hash chain, export, verification | ✅ tested |
| Web UI (React/TypeScript): IT/EN, light/dark, responsive, live updates (SignalR) | ✅ tested with Edge |
| SQLite | ✅ — PostgreSQL / SQL Server: Milestone 2 |
| Entra ID, VM configuration, checkpoints, networks, clusters, backup, console | ⏳ see [roadmap](docs/roadmap.md) |

See [what was tested and how](docs/testing.md) and the [compatibility matrix](docs/compatibility-matrix.md).

## Architecture in one picture

```
 Browser ──HTTPS(cookie, CSRF, CSP)──►  Gateway (NT SERVICE\HvPortalGateway)   ◄──mTLS WebSocket (agent-initiated)──  Agent (NT SERVICE\HvPortalAgent)
                                         ├─ REST API + OpenAPI, SignalR                                                 ├─ typed commands only
                                         ├─ auth, MFA, RBAC, jobs, audit                                                ├─ WMI root\virtualization\v2
                                         └─ SQLite + key ring (DPAPI, exportable)                                       └─ member of Hyper-V Administrators
```

Details: [architecture](docs/architecture.md) · [decisions](docs/decisions.md) · [security model](docs/security-model.md) ·
[threat model](docs/threat-model.md) · [capability matrix](docs/capability-matrix.md) · [API](docs/api.md)

## Quick start (development, no administrator rights needed)

Prerequisites: .NET SDK 10.0.2xx, Node.js 22+ (tested 24), Windows 10/11 or Windows Server 2022/2025.

```powershell
cd web; npm ci; npm run build; cd ..               # builds the SPA into the gateway wwwroot
dotnet build HvPortal.slnx
.\scripts\dev\Start-DevGateway.ps1 -Reset           # https://localhost:18443 (agent port 18444)
# The setup token is in %LOCALAPPDATA%\HvPortal\dev-gateway\bootstrap\setup-token.txt
```

Automated checks:

```powershell
dotnet test HvPortal.slnx                           # 126 unit + integration tests
.\scripts\dev\Invoke-SmokeTest.ps1                  # 35 HTTP end-to-end checks against a fresh dev gateway
cd web; npx vitest run; node e2e/ui-smoke.mjs ...   # UI e2e with the local Edge (see docs/testing.md)
```

## Production installation

See [docs/install.md](docs/install.md). In short: `installer\Build-Release.ps1` → `Install-Gateway.ps1` on the
management server → create a host in the portal → `Install-Agent.ps1` on each Hyper-V host with the one-time token.

## Repository layout

```
src/HvPortal.Contracts   shared DTOs: agent protocol, Hyper-V state model and rules
src/HvPortal.Gateway     control plane: API, auth, RBAC, jobs, audit, PKI, agent hub, admin CLI
src/HvPortal.Agent       host agent: WMI v2 adapter, enrollment, channel, command executor
src/Shared               small shared Windows helpers (ACLs)
web/                     React + TypeScript SPA (Vite), e2e UI test
tests/                   xUnit unit + integration tests (gateway, agent)
installer/               PowerShell install/upgrade/uninstall, release build
scripts/dev/             development scripts and HTTP smoke test
docs/                    requirements, architecture, security, operations
```

## Documentation index

- [Requirements and scope (MVP / next / research)](docs/requirements.md) · [Roadmap](docs/roadmap.md)
- [Installation](docs/install.md) · [Troubleshooting](docs/troubleshooting.md) · [Product backup & restore](docs/backup-restore.md)
- [Security model](docs/security-model.md) · [Threat model](docs/threat-model.md) · [SECURITY.md](SECURITY.md)
- [Capability matrix](docs/capability-matrix.md) · [Compatibility matrix](docs/compatibility-matrix.md)
- [Real-host verification plan](docs/verification/real-host-test-plan.md) · [Testing](docs/testing.md)
- [Console study (M4)](docs/console-study.md) · [License proposal](docs/licensing.md) · [CONTRIBUTING.md](CONTRIBUTING.md)

## Privacy

No telemetry. Nothing in the product contacts external services. Entra ID sign-in (Milestone 2) will be the only
feature requiring Internet access.
