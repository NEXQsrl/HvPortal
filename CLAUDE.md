# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

HvPortal: browser-based Hyper-V management (vCenter-like). Private repository: do not publish, push to public
remotes or send code/configuration to external services without explicit authorization. The user writes in Italian;
reports to the user are in Italian.

## Commands

```powershell
# .NET 10 (global.json pins SDK 10.0.204)
dotnet build HvPortal.slnx
dotnet test HvPortal.slnx
dotnet test tests\HvPortal.Gateway.Tests --filter "FullyQualifiedName~OperationsTests.Create_vm"   # single test
dotnet test tests\HvPortal.Agent.Tests --filter "FullyQualifiedName~HostFolderTests"

# Web (React 19 + Vite 6 + Tailwind v4 + shadcn/ui); build output goes to src/HvPortal.Gateway/wwwroot
cd web; npm ci
npx tsc -b --noEmit        # typecheck (no lint script exists)
npx vitest run             # checks IT/EN dictionaries have identical keys
npx vite build

# Local dev gateway (UI https://localhost:18443, agent channel 18444; data in %LOCALAPPDATA%\HvPortal\dev-gateway)
.\scripts\dev\Start-DevGateway.ps1 [-Reset] [-BindAddress 0.0.0.0 -PublicNames host,ip] [-BuildAgentPackage]
.\scripts\dev\Invoke-SmokeTest.ps1 [-UiPort -AgentPort -GatewayDataDirectory -AgentExe]   # real Kestrel + real agent, against a FRESH gateway

# Browser e2e (Edge headless via playwright-core) against a fresh gateway with a real agent
node web\e2e\ui-smoke.mjs --url https://localhost:28443 --data <gw-data> --agent-data <agent-data> --agent-exe <HvPortal.Agent.exe> --agent-port 28444 --out <screenshots>

# Docker (gateway only; the agent stays a Windows service)
docker build --provenance=false --sbom=false --platform linux/amd64 -f docker/Dockerfile -t hvportal-gateway:0.3.4 .      # compose + .env example in docker/

# Packaging / release
.\installer\New-AgentPackage.ps1 -OutputDirectory artifacts\packages   # zip served by the gateway (/api/v1/downloads)
.\installer\Build-Release.ps1 [-SkipTests]

# EF Core migration (SQLite)
dotnet ef migrations add <Name> --project src/HvPortal.Gateway --output-dir Data/Migrations/Sqlite --configuration Release
```

Practical notes:
- A running dev gateway locks `src\HvPortal.Gateway\bin\Debug`; build/test into another folder with
  `--artifacts-path <dir>` (and `--configuration Release` for `dotnet ef`).
- The dev machine has no Hyper-V rights: the agent truthfully reports `AccessDenied`. Anything that needs real
  Hyper-V (VM creation, config changes, console frames) is tested via the test double `FakeAgentDispatcher`
  (test project only) and listed in `docs/verification/real-host-test-plan.md`. Never present it as validated.
- After changes, update `docs/testing.md` (suite counts) and the relevant docs/ADR.

## Architecture

Three .NET projects plus the SPA:

- **`src/HvPortal.Contracts`** — shared by gateway and agent: the agent protocol (`AgentProtocol`, envelopes,
  `AgentCommand`/`AgentCommandResult`), the closed `HvOperation` enum with typed argument records
  (`HyperV/Operations.cs`), and validation used on **both** sides (`OperationValidation`, `HostPaths`).
- **`src/HvPortal.Gateway`** — ASP.NET Core minimal APIs, runs as `NT SERVICE\HvPortalGateway` (least privilege).
  Kestrel on two ports: UI/API/SignalR (8443) and agent channel (8444, mTLS with an internal CA). EF Core SQLite.
  Endpoints live in `Api/*Endpoints.cs`; authorization via `AccessEvaluator` + `http.GuardAsync(permission, resource, auditAction)`.
- **`src/HvPortal.Agent`** — Windows service in *Hyper-V Administrators* only. It **opens** an outbound WebSocket to
  the gateway (`Channel/GatewayChannelService`); no inbound ports. Inventory/power/console via WMI
  (`System.Management`, `HyperV/WmiHyperVHost`, `ConsoleService`); typed management operations via the single fixed
  script `Ops/HvPortal.Ops.ps1` run in Windows PowerShell 5.1 with a JSON request on stdin
  (`PowerShellOperationRunner`, ADR-012). `BrowseFolders` runs in-process (`HostFolderBrowser`).
- **`web/`** — SPA served by the gateway (`Hosting/SpaShell` injects a per-response CSP style nonce into index.html).
  `main.tsx`: Gate → InventoryProvider → OperationsProvider → routes. `OperationsProvider` owns all action menus
  (`vmActions`/`hostActions`/`groupActions`) and dialogs, reused by tree, tables and pages. Inventory is the single
  entry point (tree: Hosts / Groups / All VMs). i18n: `i18n.tsx` + `i18n-ops.ts` + `i18n-guide.ts` (IT and EN, same keys).

Key cross-cutting flows:
- **Commands are typed and closed.** There is no "run script/WQL/path" command anywhere; adding a capability means a
  new `HvOperation` value + argument record + validation in Contracts + a branch in the script/agent + gateway endpoint.
- **Protocol versioning.** `AgentProtocol.Version` (currently 5) is announced in `AgentHello`.
  `AgentProtocol.RequiredVersion(command)` is checked in `AgentConnectionManager.SendAsync` (and mirrored in
  `FakeAgentDispatcher`): never send a command an older agent cannot parse or would silently misinterpret. Bump it
  when adding enum values or semantics-changing fields. Product version is in `Directory.Build.props` (+ `web/package.json`).
- **Changes are persistent jobs** (`Jobs/JobService`, `JobRunner`): created at request time, then re-authorized and
  re-validated at execution; server-controlled arguments are re-stamped (`JobRunner.PrepareArguments`: VM Hyper-V id,
  ISO library roots). Unknown outcomes are reconciled, never reported as success. Reads are live agent calls.
- **RBAC**: scopes Global/Host/Group/VM, default deny, explicit deny wins; logical groups are nested folders whose
  assignments apply to sub-groups (ADR-015). Membership/tree moves go through `GroupTree.CheckAccessChangeAsync`
  (anti-escalation). Portal groups are mirrored to native Hyper-V VM groups by `VmGroups/VmGroupSyncService` (ADR-017); only native groups linked in `HostVmGroupLinks` are ever touched.
- **Security middleware order** (`Program.cs`): routing → WebSockets → cross-site protection (Origin + antiforgery;
  WebSocket handshakes incl. HTTP/2 CONNECT skip antiforgery) → authorization. Console: one-time ticket sent as the
  first WebSocket message (`VmConsole/`).
- **Audit**: HMAC hash-chained log; write audit entries for every change and every denial.

Decisions and rationale: `docs/decisions.md` (ADRs). Security: `docs/security-model.md`, `docs/threat-model.md`.
Verified Hyper-V APIs and required privileges: `docs/capability-matrix.md`.

## Rules (from CONTRIBUTING.md and the project brief)

- No fake success, no mock/fictitious data in the production UI; unknown outcomes are shown as `Unverified`/`Unknown`/`stale`.
- Every endpoint authorized server-side, with a negative authorization test.
- Agent: only typed, authorized operations; parameterized PowerShell with fixed keys (the agent test suite fails on
  `Invoke-Expression`, `Start-Process`, `Add-Type`, `[ScriptBlock]::Create`, ... in the ops script).
- Never log passwords, tokens or credentials; external telemetry off by default; no Domain Admin requirement.
- Do not call an RDP session a Hyper-V console; do not call checkpoints/exports/VHDX copies application-consistent backups.
- Prefer the .NET BCL and native Windows tools; new dependencies need a license check (`docs/licensing.md`).
- C#: nullable + analyzers, file-scoped namespaces, `TimeProvider` for time, async throughout.
  TypeScript: strict, no `any`, all user-visible text in the i18n dictionaries.
- Commits only when asked, locally on `main`, message explaining *why*.
