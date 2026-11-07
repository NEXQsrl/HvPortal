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
docker build --provenance=false --sbom=false --platform linux/amd64 -f docker/Dockerfile -t hvportal-gateway:0.7.0 .      # compose + .env example in docker/

# Packaging / release
.\installer\New-AgentPackage.ps1 -OutputDirectory artifacts\packages   # zip served by the gateway (/api/v1/downloads)
.\installer\Build-Release.ps1 [-SkipTests]                             # also builds setup\HvPortal.{Gateway,Agent}.Setup.exe
# Graphical setup alone (net472 WinForms, not in the solution); pages can be checked without installing:
dotnet build installer\HvPortal.Setup -c Release -p:SetupComponent=Agent -o <dir>      # or Gateway; -p:PayloadZip=<zip> embeds the files
<dir>\HvPortal.Agent.Setup.exe --render <folder> --lang it                            # PNG of every wizard page
# Signed release (self-signed "NEXQ S.r.l." code-signing cert in CurrentUser\My; only the public .cer is exported)
.\installer\New-CodeSigningCertificate.ps1                                   # prints the thumbprint (reuses a valid one)
.\installer\Build-Release.ps1 -SigningCertificateThumbprint <thumbprint>     # signs HvPortal.*.exe/.dll + all .ps1, ships installer\HvPortal-CodeSigning.cer

# EF Core migration (SQLite)
dotnet ef migrations add <Name> --project src/HvPortal.Gateway --output-dir Data/Migrations/Sqlite --configuration Release
```

Practical notes:
- A running dev gateway locks `src\HvPortal.Gateway\bin\Debug`; build/test into another folder with
  `--artifacts-path <dir>` (and `--configuration Release` for `dotnet ef`).
- The antivirus of the dev machine refuses executables started from `%TEMP%` ("Access is denied"): for the browser e2e
  start the gateway with `dotnet <artifacts>\bin\HvPortal.Gateway\debug\HvPortal.Gateway.dll` (working directory
  `src\HvPortal.Gateway`, same `Gateway__*` variables as `Start-DevGateway.ps1`) and build the agent into the repository
  (`dotnet build src\HvPortal.Agent -o artifacts\e2e\agent`); the e2e `--files-dir` must not be under `C:\Users`.
- The dev machine has no Hyper-V rights: the agent truthfully reports `AccessDenied`. Anything that needs real
  Hyper-V (VM creation, config changes, console frames) is tested via the test double `FakeAgentDispatcher`
  (test project only) and listed in `docs/verification/real-host-test-plan.md`. Never present it as validated.
- After changes, update `docs/testing.md` (suite counts) and the relevant docs/ADR.
- The graphical setups (ADR-026) only drive `Install-Gateway.ps1` / `Install-Agent.ps1` through `Invoke-Setup.ps1`:
  a new installer parameter needs the script, `installer/HvPortal.Setup/SetupLogic.cs` (+ wizard page and both
  languages in `Strings.cs`) and `SetupLogicTests` (which checks the names and types against the real scripts).
- Self-signed signatures show `UnknownError` (untrusted root) until the `.cer` is imported into `LocalMachine\Root` +
  `TrustedPublisher`; they do not give SmartScreen/antivirus reputation. Never claim a build is "trusted" because it is
  signed, and never export or commit the private key (`artifacts/` is git-ignored). No timestamp server by default:
  `-TimestampServer` contacts an external service, so use it only when the user asks.

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
  `main.tsx`: Gate → InventoryProvider → OperationsProvider → routes. `shell/AppShell` has no sidebar: `shell/Navigator`
  (left panel of every page) shows the main navigation as icons and a tree that follows the route (inventory tree, or a
  page tree whose nodes are links with URL parameters such as `/networks?host=`, `/jobs?status=`). `OperationsProvider` owns all action menus
  (`vmActions`/`hostActions`/`groupActions`) and dialogs, reused by tree, tables and pages. Inventory is the single
  entry point (tree: Hosts / Groups / All VMs). i18n: `i18n.tsx` + `i18n-ops.ts` + `i18n-guide.ts` + `i18n-limits.ts` + `i18n-audit.ts` + `i18n-templates.ts` (IT and EN, same keys). An audit action or job type added on the server needs its text in `i18n-audit.ts` (`audit.a.<action>`) and `jobop.<type>.<Operation>`.

Key cross-cutting flows:
- **Commands are typed and closed.** There is no "run script/WQL/path" command anywhere; adding a capability means a
  new `HvOperation` value + argument record + validation in Contracts + a branch in the script/agent + gateway endpoint.
- **Protocol versioning.** `AgentProtocol.Version` (currently 9) is announced in `AgentHello`.
  `AgentProtocol.RequiredVersion(command)` is checked in `AgentConnectionManager.SendAsync` (and mirrored in
  `FakeAgentDispatcher`): never send a command an older agent cannot parse or would silently misinterpret. Bump it
  when adding enum values or semantics-changing fields. Product version is in `Directory.Build.props` (+ `web/package.json`).
- **Changes are persistent jobs** (`Jobs/JobService`, `JobRunner`): created at request time, then re-authorized and
  re-validated at execution; server-controlled arguments are re-stamped (`JobRunner.PrepareArguments`: VM Hyper-V id,
  ISO library roots). Unknown outcomes are reconciled, never reported as success. Reads are live agent calls.
- **RBAC**: scopes Global/Host/Group/VM, default deny, explicit deny wins; logical groups are nested folders whose
  assignments apply to sub-groups (ADR-015). Membership/tree moves go through `GroupTree.CheckAccessChangeAsync`
  (anti-escalation). Portal groups are mirrored to native Hyper-V VM groups by `VmGroups/VmGroupSyncService` (ADR-017); only native groups linked in `HostVmGroupLinks` are ever touched. The groups a host already has are imported once, by the first mirroring pass (ADR-027): only those `VmGroupImporter.Adoptable` accepts, i.e. whose import changes nothing in Hyper-V; the rest on request.
- **Limits of VM groups** (ADR-024, `Quotas/GroupPolicyService`): a group may carry quotas and lists of hosts, networks
  and storage paths (`LogicalGroups.PolicyJson`). `vm.create`, `network.manage` and `host.files.*` held on a group work
  only where those limits allow (`HoldsViaGroupAsync`); every creation/resize is checked at request time **and** in
  `JobRunner.ApplyFolderLimitsAsync`, which also re-stamps the VM location and the new-disk folder. Any new operation
  that creates or grows something inside a group must go through this service.
- **VM templates** (ADR-028, `Templates/TemplateRules`, `Api/TemplateEndpoints`): a template is a copy of the disks of
  a VM that is off, in `<template library of the host>\<template id>`; VMs are created from it through the normal
  creation job (`NewVm.TemplateDisks`). Library, folder and disk paths always come from the gateway's records and are
  re-stamped by `JobRunner.StampTemplateAsync`; the template state is written with the final state of its job
  (`TemplateRules.ApplyJobOutcome`): never `Ready` without the list of copied disks.
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
- Patterns applied to text from clients (paths, names) must not backtrack: no optional separator inside a repeated
  group (`(segment\\?)*`); write `segment(\\segment)*` and add a test with a long refused input.
- C#: nullable + analyzers, file-scoped namespaces, `TimeProvider` for time, async throughout.
  TypeScript: strict, no `any`, all user-visible text in the i18n dictionaries.
- Commits only when asked, locally on `main`, message explaining *why*.
