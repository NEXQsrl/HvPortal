# Testing

## v0.7.0 — backups, VM move, host health, networks with VLAN, storage folders, agent update from the portal (2026-11-07, development machine)

| Suite | Count | Result |
|---|---|---|
| `tests/HvPortal.Gateway.Tests` | 213 | pass — adds `Protocol9Tests` (9): a portal network is a switch + VLAN and a VM created on it or whose adapter is put on it gets that VLAN (switch and VLAN taken from the network by the gateway, also when the job runs; an unknown network refused); changing the VLAN of a network re-tags only the adapters on the old switch + VLAN and only for VMs the user may reconfigure (others listed as skipped); host health read live only by who reads the host, overall state never better than what was read (unreadable sections → Unknown, stopped `vmms` → Critical), `agent_outdated` with protocol 8; VM move to another host keeps the portal record (same id, new host, events), needs `vm.migrate` on the VM **and** `vm.create` on the destination (refusals audited), respects folder limits and the storage folders of the destination; storage move follows the storage rules of the host; storage folders are read-only for hosts with their own agent (`decided_on_the_host`) and editable for remote hosts (`hosts.manage`, validated, sent to the management agent); new folders need `host.files.write` and agent 0.7; outdated agents listed with the reason they cannot be updated (not connected, agent older than 0.7.0, no updater task, unsigned package), update jobs only for agents that can check the package, job succeeds only when the agent reconnects with the new version, "update all" skips the others; the package on the agent port needs an agent client certificate. Adds `BackupTests` (7): repositories need `backup.manage`, their password is sealed, never returned nor audited and only reaches the agent inside a command; a job backs up its VMs and the VMs of its groups, closes the run and applies the retention (newest points kept, kept points never removed); a failed backup gives a failed point and a failed run, a lost result is reconciled as *Unverified*, never *Ok*; restores create a new VM, or replace the original only with the typed confirmation, and restore tests report what was checked (a failed test is reported as failed); files of a restore point are browsed only by who attached the disk, downloads are audited; API tokens are shown once, stored hashed, read backup status only (`/api/v1/public/backup/*`), revocable, other API calls refused; notification settings validated (HTTPS webhooks, SMTP) and their secrets write-only. After the code review of this
release the tests also check: the webhook address is sealed in the database (not only masked in the API), a masked
address sent back under another name is refused, `localhost` / loopback / link-local / `[::1]` webhooks are refused,
SMTP credentials without TLS are refused, attaching a backup disk on another host needs `host.files.write` there (a
backup operator is refused and audited), `GET /backup/mounts` really returns the list (it was mapped as a
`RequestDelegate` and answered an empty 200), storage folders cannot be set for a remote host whose management agent
is older than 0.7.0. `SetupLogicTests`: `Stop-HvPortalService` used by the installers, new `Install-Agent.ps1` parameters `-StorageFolders` and `-DisableRemoteUpdate` checked against the script, storage-folder rules of the agent wizard |
| `tests/HvPortal.Agent.Tests` | 118 | pass — adds `Protocol9Tests` (12): protocol 9 required for every new operation, for VLANs on new VMs/added adapters and for agent updates; VLAN 1-4094 with a switch; UNC share paths checked without backtracking (long refused inputs answered at once), hidden `$` shares refused; repository credentials only for shares, staging folder required, password never in `ToString`; backup/restore requests name exactly one restore point folder, restore tests are isolated (disconnected network, not started); moves and new folders follow the folder rules; allowed storage folders restrict every path the portal can use (browsing, files, VM locations, ISO/template libraries, backups) and an invalid `storage.json` blocks every folder (fail closed); files of an attached backup disk are reachable only inside its volumes; an update request is a version, a SHA-256 and a bounded size; `Update-HvPortalAgent.ps1` installs only packages whose signed catalog and agent executable are signed by the publisher of the installed agent, refuses older versions and restores on failure; the ops script removes the backup checkpoint in `finally`, never deletes the original disks on restore (renamed `*.pre-restore-<stamp>`). Existing tests updated to protocol 9 and the 12-hour limit for disk copies |
| PowerShell parser on `Ops\HvPortal.Ops.ps1`, `installer\*.ps1` (5.1) | — | pass |
| `web`: `tsc -b`, `vitest` (IT/EN keys), `vite build` | — | pass |
| `web/e2e/ui-smoke.mjs` (Edge headless, fresh gateway + real agent on this machine) | 84 | pass — updated for the new navigation (Users & roles and Audit are reached from the tree of the Settings area, no longer top icons) and adds: the backup area (overview and every tab) renders, the network tab of a host shows its portal networks (VLAN), the host page has the health tab, host settings show the allowed storage folders, the backup staging folder and the agent section, Settings has the Notifications and API tokens tabs; the update guide shows the agent table with the package state (the e2e package is unsigned: "update from the portal" is correctly not offered); no CSP violation or page error on any of them. The agent of this machine has no Hyper-V rights, so the health tab and the backup pages show no host data |

Not executed: `Build-Release.ps1` for this release. Nothing that needs Hyper-V was run on
a real host — backups (checkpoint + export, NAS share with credentials and staging), every restore mode, restore tests,
file restore (`Mount-VHD`), Move-VM / Move-VMStorage, host health cmdlets, VLAN on adapters, the updater task and its
security descriptor, the end-to-end self-update, the storage folders on a real agent and the upgrade with a locked
`clrjit.dll`. Here the host side is the test double `FakeAgentDispatcher` and the scripts are checked by parsing and by
their text. See the real-host test plan for the items to validate.

## v0.6.0 — VM templates, readable audit, navigation icons, path pattern fix (2026-11-06, development machine)

| Suite | Count | Result |
|---|---|---|
| `tests/HvPortal.Gateway.Tests` | 196 | pass — adds `TemplateTests` (5, ADR-028): a template is created only from a VM that is off, on a host with a template library (system and network folders refused), the agent is told to copy into `<library>\<template id>`, the record becomes *Ready* with the disks reported; a VM from the template gets the caller's name and resources and the template's disks, location and generation whatever the request says (injected `templateDisks` / `templateRoots` / generation ignored), a larger boot disk is kept and a smaller one raised to the template's; a creation without a template never reaches the agent with template fields; rename; delete with the typed name removes the files; a template cannot be used on another host. Permissions: an operator sees no template and cannot create, rename, delete or set the library (refusals audited); who creates VMs on the host uses the template but cannot change it and is not told where the files are; an administrator of another host does not see it; agents with protocol 7 never receive template commands (`agent_outdated`). Folder limits: every disk of the template counts against the disk quota at request time, the folder's users see the templates of the hosts opened to them. States: a failed copy leaves a *Failed* template that cannot be used and is cleaned by deleting it; a lost result (channel dropped, then reconciliation) gives *Unverified*, never *Ready*; a requester who loses the permission before execution leaves no template and nothing is sent; a template that stops being ready while a creation is queued fails that job (`template_not_available`), no VM is created; the library path rules answer at once on long refused paths. Adds `AuditListTests` (2): entries carry the name of their resource (current, or the recorded one when the resource was deleted), search by the name of a host or group, several action prefixes, period, correlation id; the list needs `audit.read` (refusal recorded) |
| `tests/HvPortal.Agent.Tests` | 106 | pass — adds `TemplateOperationTests` (5): a template folder is exactly one level below the library and named by an id (other folders, traversal, system and network paths refused, also by the agent before PowerShell runs); a VM from a template only takes .vhdx/.vhd files inside the library (template fields without disks are refused, not ignored); protocol 8 required for `NewTemplate`, `RemoveTemplate` and `NewVm` with template disks; copies may run up to 8 hours while everything else keeps 30 minutes; script rules (source VM only read: off, no checkpoints, no differencing or pass-through disks, nothing left behind on failure; template deletion file by file, never recursive, nothing deleted when the folder holds anything else; `NewVm` copies and never moves template disks); **path rules answer at once** on long refused names (see below) and accept/refuse the same paths as before |
| PowerShell parser on `Ops\HvPortal.Ops.ps1` (5.1) | — | pass |
| `web`: `tsc -b`, `vitest` (IT/EN keys), `vite build` | — | pass |
| `web/e2e/ui-smoke.mjs` (Edge headless, fresh gateway + real agent on this machine) | 80 | pass — adds: audit rows describe actions in words with the name of the resource, who acted and how, translated outcome, never raw JSON; a row expands to its details (action code, fields); filter by area; search by the name of a host; template library of the host saved from its settings (a path that is not a local folder is refused before saving); Templates page with its explanation and the tree of hosts; in the dark theme the navigation icons are white, at least 36 px high and **only the current section is highlighted** |
| Update of an existing database: gateway 0.6.0 started on a copy of the data directory left by the 0.5.1 e2e | — | pass — migration `VmTemplates` applied (new table, `Hosts.TemplateLibraryPath`), `PRAGMA integrity_check` ok, portal answers |
| `installer/Build-Release.ps1 -SigningCertificateThumbprint <self-signed NEXQ S.r.l.>` (full, with tests) | 106 + 196 | pass — release 0.6.0 |

**Defect found while writing these tests** (threat model D3): the pattern that validates folder paths
(`HostPaths`, `OperationPolicy`, and its copies in the web client) took exponential time on a long folder name followed
by a refused character: one request with such a path kept a thread busy indefinitely (the test host hung for 10
minutes before it was stopped). The pattern is rewritten so that it cannot backtrack; the new tests feed paths of 150-200
characters ending with `:`, `|`, `*` and require the answer within 2 seconds, on the agent contract and on the gateway.

Not executed (needs a host with Hyper-V access): creating a template from a real VM, creating VMs from it, deleting it,
copy times, the behaviour with checkpoints / differencing / pass-through disks. Here the host side is the test double
`FakeAgentDispatcher` and the script is checked by parsing and by its text; the e2e agent has no Hyper-V rights, so no
VM exists there and "Create template" is never offered. Real-host test plan V46–V48. The audit descriptions cover the
actions the gateway writes today; an action without a description is shown with its code.

## v0.5.1 — VM groups already on a host imported automatically (2026-11-06, development machine)

| Suite | Count | Result |
|---|---|---|
| `tests/HvPortal.Gateway.Tests` | 189 | pass — adds to `VmGroupTests` (ADR-027): the first mirroring pass of a host imports the groups it already has with nobody signed in (management collection with its VM collections, standalone VM collection), the native groups are byte-for-byte as before (nothing created, renamed, emptied or removed), audit entry by `system`; empty groups and a management collection with an empty member are left alone and listed as not imported; it runs once per host (a group made later is taken only by the import on request, which still takes everything); a portal group with role assignments is never filled (suffixed group, the scoped operator still gets 404 on the VM, the VM keeps its native group); a group naming a VM the inventory has not reported waits, attempts are spaced, and it is imported once the VM is known; `Gateway:VmGroups:AutoImport=false` switches it off (no listing sent to the host); the selection rules alone (VM collection in two management collections, empty management collection, member added to an owned collection, unknown VM) |
| `tests/HvPortal.Agent.Tests` | 101 | pass (no agent change) |
| `web`: `tsc -b`, `vitest` (IT/EN keys), `vite build` | — | pass |
| `web/e2e/ui-smoke.mjs` (Edge headless, fresh gateway + real agent on this machine, packages directory of the release) | 69 | pass — unchanged checks on the 0.5.1 build |
| Update of an existing database: gateway 0.5.1 started on a copy of the data directory left by the 0.5.0 e2e | — | pass — migration `VmGroupAdoption` applied, `PRAGMA integrity_check` ok, portal answers; the statement that marks the hosts already organised by the portal was run verbatim on that copy with a link row added (host marked with its last mirroring time, integer like the other dates) |
| `installer/Build-Release.ps1 -SigningCertificateThumbprint <self-signed NEXQ S.r.l.>` (full, with tests) | 101 + 189 | pass — release 0.5.1, 13 files signed plus the two setups |

Not executed (needs a host with Hyper-V access and existing VM groups): the automatic import against real `Get-VMGroup`
output, and that Hyper-V is left unchanged by the mirroring pass that follows it. Here the host side is the test double
`FakeAgentDispatcher`; the e2e agent has no Hyper-V rights, so the import never starts there. Real-host test plan V45.

## v0.5.0 — networks, group limits, user deletion, automatic database copies, navigator, graphical setups (2026-11-06, development machine)

| Suite | Count | Result |
|---|---|---|
| `tests/HvPortal.Gateway.Tests` | 185 | pass — adds `SetupLogicTests` (18, graphical setups, ADR-026): field rules of the wizard (names, ports, thumbprint, fingerprint, token, account, firewall addresses, gateway URL), options to script parameters (the enrollment token is never among them), settings read back from the service environment on an update, portal data line and PowerShell command both parsed, parameters file valid JSON whatever the values, the real `Invoke-Setup.ps1` run with a stub installer (typed literal values, hostile strings not executed, unknown parameter refused), every parameter name and type checked against the real `Install-Gateway.ps1` / `Install-Agent.ps1`, and the gateway installer refuses (ports, certificate) before it stops the running service; `DownloadTests` extended: agent setup absent (null in the listing, 404), served with size and SHA-256, download audited, refused without `hosts.manage`. Adds `FolderLimitsTests`: limits of a group need `groups.manage` + `hosts.manage` (group administrator, host-scoped admin and reader refused and audited, nothing changed) and are validated (ranges, unknown host, system folders, UNC); a *Folder administrator* creates VMs only in their group, only on the enabled host, network and storage (location set by the gateway), within VM / vCPU / memory / disk quotas, and is placed in the group without `groups.manage`; dynamic memory needs a maximum under a memory quota; configuration changes are limited too (vCPU, memory, new disk, network) and the new disk folder is decided by the gateway (a client-sent folder is ignored); queued creations reserve the quota, the limits are checked again when the job runs (`quota_exceeded`) and a host removed from the limits revokes a queued job; parent limits apply to subgroups and bind administrators; virtual switches need `network.manage` (reader refused, audited), group users get private networks only, within the quota, owned by the group and deletable only by them or by host administrators (typed name), external switch with adapter for host administrators, protocol 6 agents never receive the new operations; group users reach only the group's storage paths in the file explorer (list, download, upload refused and audited outside); user deletion (no `users.manage` refused, confirmation, not yourself, sessions and assignments removed, open session stops working); automatic database copy verified, pruned after 7 days, restricted to `settings.manage`; checkpoint apply on a running VM with a protocol 7 agent, refused with protocol 6. Existing tests updated: VM notes need `inventory.annotate` and are a configuration job; no portal description any more |
| `tests/HvPortal.Agent.Tests` | 101 | pass — adds switch argument validation (names, types, adapter only for external), new-disk folder rules, protocol 7 requirements (switch operations, disk folder), script branches for the new operations, VM turned off before `Restore-VMSnapshot` |
| `web`: `tsc -b`, `vitest` (IT/EN keys), `vite build` | — | pass |
| `web/e2e/ui-smoke.mjs` (Edge headless, fresh gateway + real agent on this machine, packages directory of the release) | 69 | pass — adds: "Add host" opens on the guided setup with the connection data line (gateway name, CA fingerprint, token, role; never localhost) and the setup file downloads with the SHA-256 shown; the guide page documents both installation methods (the PowerShell one keeps the commands); the tab bar of the host page has no scrollbar (nothing overflows it, bar hidden when it scrolls sideways); icon-only navigation above the tree on every page, new group dialog with advanced limits, limits and usage on the group page, user deleted from Users & roles, automatic database copy on demand listed in Settings, navigator trees of Networks and Tasks (status filter from the tree), guide pages reached from the tree; the earlier checks pass with the new layout (no CSP violations or page errors) |
| PowerShell parser on `Ops\HvPortal.Ops.ps1` (5.1) | — | pass |
| `installer/Build-Release.ps1 -SigningCertificateThumbprint <self-signed NEXQ S.r.l.>` (full, with tests) | 101 + 184 | pass — release 0.5.0, 13 files signed plus the two setups (`setup\HvPortal.Gateway.Setup.exe` 138 MB, `setup\HvPortal.Agent.Setup.exe` 42 MB, the latter also in `gateway\packages`), agent package manifest `signed: true, selfSigned: true`; rebuilt with `-SkipTests` after later changes (render of the failure page, a regression test, guide texts and loading state), with the suites above re-run on the final sources |
| Setup programs: `--render` (both components, IT and EN) | 28 pictures | looked at — every page of the two wizards and both outcomes of the last page; texts fit, no clipping |
| Setup programs of the release: `--extract`, `Get-AuthenticodeSignature` | — | pass — payload unpacked without elevation; `gateway\` (392 files) and `agent\` (232 files) identical by SHA-256 to the release folders, `installer\` contains `Invoke-Setup.ps1` and the install scripts; both setups carry the NEXQ signature (reported `UnknownError` here: self-signed root not trusted on this machine) |

Not executed (need a host with Hyper-V access): creating and removing virtual switches, checkpoint apply on a running
VM, the creation wizard inside a group, disk usage read from a real host, the new-disk folder: real-host test plan
V38–V42. The e2e host has no Hyper-V rights, so "New network" is not offered there and the dialog is covered by type
checks only. On this machine the antivirus blocks executables started from `%TEMP%` ("Access is denied"): the e2e
gateway was started with `dotnet HvPortal.Gateway.dll` and the agent from `artifacts\e2e\agent`.

Not executed (needs elevation, which this session does not have): an installation or an update through the two setup
programs — here the wizards were only rendered, never driven to an installation. What is covered is the logic it relies on (tests
above), the wrapper script against a stub installer, the pages (rendered) and the payload. The elevation prompt,
SmartScreen with the self-signed signature, the staging folder and its ACL, PFX import, the pre-filled update and the
agent enrollment through "Paste from the portal" are in the real-host test plan (V43–V44).

## v0.4.0 — management agents, icon-only copy buttons, disk expansion check (2026-11-06, development machine)

| Suite | Count | Result |
|---|---|---|
| `tests/HvPortal.Gateway.Tests` | 158 | pass — adds management agents / remote hosts (ADR-022): every endpoint refused without global `hosts.manage` (host-scoped admin and read-only user, denial audited, nothing changed); password encrypted with the key ring, bound to the host id, absent from API responses, list and audit, connection details hidden from host viewers, kept on update without password, replaced with a new one; input validation (IP needs `HOST\user`, localhost, UNC, IPv6, bad user, empty password, unknown agent, duplicate computer); no enrollment token for remote hosts; moving a host reconfigures both agents; agent in use cannot be deleted, revoked agent disconnected and refused for new hosts; host delete reconfigures its agent; enrollment of a management agent returns role Management and creates no host |
| `tests/HvPortal.Agent.Tests` | 97 | pass — adds computer-name and user-name rules, password never in `ToString`, command routing (host agent refuses targeted commands, management agent refuses untargeted or unknown targets, protocol 6 requirement), target registry (unchanged targets kept, changed credentials replaced, invalid skipped), administrative-share path mapping both ways, remote launcher contains a single `Invoke-Command -FilePath` of the fixed script and no dynamic code or certificate-check bypass |
| `web`: `tsc -b`, `vitest` (IT/EN keys), `vite build` | — | pass |
| Follow-up (same day): `tests/HvPortal.Gateway.Tests` | 158 | pass — off-only fields extended (automatic stop action; Generation 1 NIC/disk hot-add) in `FieldsRequiringOff`; agent package manifest `selfSigned` reported as such (and never when `signed` is false) |
| `installer/Build-Release.ps1 -SigningCertificateThumbprint <self-signed NEXQ S.r.l.>` (full, with tests) | 97 + 158 | pass — 12 files (gateway/agent `HvPortal.*.exe/.dll`, every `.ps1`) carry the NEXQ S.r.l. signature (`Get-AuthenticodeSignature`: `UnknownError` = untrusted root on the build machine, expected until the `.cer` is imported); `installer\HvPortal-CodeSigning.cer` in the release and in the agent zip, whose installer scripts are now the signed ones; manifest `signed: true, selfSigned: true` |

| Installer scripts (PowerShell 5.1 and 7 parser, all `installer\*.ps1`) | — | pass; `Edit-UserRightsInf` checked on a sample `secedit` export: adds the SID to the 5 rights, idempotent, removal clears only that SID (rights left empty are written empty), other rights untouched; `New-RandomPassword` 40 chars with all four classes |
| `web`: guide page *Microsoft Entra ID sign-in* — `tsc -b`, `vitest` (IT/EN keys), `vite build` | — | pass |

Not executed: anything that needs a real remote Hyper-V host (WMI/DCOM, WinRM, SMB with credentials): real-host test
plan V29–V34; VM settings rules on a running VM: V35. Not executable on the development machine (no elevation):
`Install-Agent.ps1 -ServiceAccount` (user creation, `secedit`, `Win32_Service.Change`, uninstall cleanup) — V36; the
gateway port check and private-key grant with a CA PFX on port 443 — V37. Effect of the self-signed signature on Defender/SmartScreen or
other antivirus products after importing the certificate was not observed. `web/e2e/ui-smoke.mjs` was not re-run.

## v0.3.4 — Docker image and settings UI (2026-11-05)

| Check | Result |
|---|---|
| `docker build -f docker/Dockerfile` (SPA + gateway linux + cross-published win-x64 agent package) | ok |
| Container from `docker/docker-compose.yml` (read-only FS, non-root, cap_drop ALL) | healthy; migrations applied; setup token in the log |
| `Invoke-SmokeTest.ps1` against the container, **real Windows agent enrolled over mTLS** | 45/46 — the failing check reads the token file of its own copy of the data folder; inside the container the file is removed after setup (verified with `docker exec`) |
| Inside the container | `/data` 0700, files 0600 (uid 1654), key ring `kek-aesgcm` from the variable (no `kek.bin`), Data Protection keys encrypted with the key ring |
| Container restart on the same volume | healthy, data readable |
| `tests/HvPortal.Gateway.Tests` | 153 (adds KEK wrapping: env KEK, id binding, wrong KEK refused, generated KEK flagged, invalid values) |
| `web/e2e/ui-smoke.mjs` | 58 — adds Settings tabs, inputs aligned in the same row (security, Entra, backup), "?" help with tooltip |

Not executed in the container: restore applied by "Restart now" (restart policy) and Entra sign-in.

## v0.3.4 run (2026-11-05, development machine)

| Suite | Count | Result |
|---|---|---|
| `tests/HvPortal.Gateway.Tests` | 148 | pass — adds user creation with role (anti-escalation: amplification refused, user not created), MFA enrollment forced after the first password change, Entra settings validation and encrypted secret (not in DB plain text nor audit), **Entra flow with a test identity provider**: PKCE/state/nonce in the authorize request, not-provisioned / foreign-signature / bad-nonce / other-tenant / forged-state refused, binding by object id, same UPN with another object id refused, no password sign-in for Entra users, Entra re-authentication of the session |
| `tests/HvPortal.Gateway.Tests` (later in v0.3.4) | 150 | pass — adds portal backup round trip (privileged-only, weak passphrase refused, sealed file, wrong passphrase refused, preview writes nothing, typed confirmation, staged restore applied at "startup", previous state kept, post-change user gone, sessions revoked, audit entry, audit chain valid) and VM summary details (notes/path/creation time stored, memory demand computed, notes change event, older agent does not erase values) |
| `web/e2e/ui-smoke.mjs` | 54 | pass — adds new-user dialog (type, role, MFA at first sign-in), user created with role, Entra section with redirect URI in Settings, sealed backup download and restore preview |

Not executed: sign-in against a real Entra tenant (needs an app registration): real-host test plan V24–V26.

## v0.3.3 run (2026-11-05, development machine, non-administrator)

| Suite | Count | Result |
|---|---|---|
| `tests/HvPortal.Agent.Tests` | 79 | pass — adds upload state machine on the real file system (hidden temporary file, retried chunk ignored, gap refused, verified commit, no overwrite by default, hash mismatch leaves the existing file and removes the temporary one, abort), chunked reads, **files open for writing refused**, hidden files refused, file-name rules, protocol 5 |
| `tests/HvPortal.Gateway.Tests` | 144 | pass — adds file permissions (read vs write, host scope), blocked/invalid paths, 3-chunk download with audit SHA-256, in-use file 409, 2-chunk upload with SHA-256, conflict without overwrite, overwrite, invalid names, protocol gate |
| `web/e2e/ui-smoke.mjs` | 49 | pass — adds the file explorer **against the real agent on this machine**: system folders hidden at C:\, download equals the file on disk, 5 MB upload lands on disk byte-identical, overwrite confirmation, declined replacement leaves the file unchanged |
| `web` Vitest | 1 | pass |

## v0.3.2 run (2026-11-05, development machine, non-administrator)

| Suite | Count | Result |
|---|---|---|
| `tests/HvPortal.Agent.Tests` | 68 | pass — adds `SyncVmGroups` argument validation (names, keys, types, members), protocol 4 requirement, script contains the VM group operations and limits removal to managed groups |
| `tests/HvPortal.Gateway.Tests` | 141 | pass — adds mirroring with native hierarchy (VM + management collections), rename in place, member changes, deletion, **foreign native groups untouched**, unchanged plan not re-sent, protocol-3 agents skipped (`agent_outdated`), import (management → parent + sub-groups, never merging into groups with role assignments, links, idempotent, audited, permissions), planner flattening of deep trees |
| `web/e2e/ui-smoke.mjs` | 44 | pass — adds the host tab "Gruppi VM Hyper-V" |
| `scripts/dev/Invoke-SmokeTest.ps1` | 46 | pass (agent protocol 4) |
| `web` Vitest | 1 | pass |

The ops script was parse-checked with Windows PowerShell 5.1 and PowerShell 7. The VM group cmdlets were **not**
executed (no Hyper-V rights here): real-host test plan V17–V20.

## v0.3.1 run (2026-11-05, development machine: Windows 11 Business 26200, non-administrator)

| Suite | Count | Result |
|---|---|---|
| `tests/HvPortal.Agent.Tests` | 66 | pass — adds VM-location rules (system folders, UNC, traversal, streams, device paths), folder browser against the real file system (drives, hidden/system folders excluded, blocked roots refused, missing tail accepted only for the location), protocol 3 requirements, unknown command values answered without dropping the channel |
| `tests/HvPortal.Gateway.Tests` | 137 | pass — adds folder browser permissions (`vm.create` for folders, `hosts.manage` for ISO files), blocked/UNC/traversal paths, **nothing sent to protocol-2 agents** (`agent_outdated`), VM location validation + normalization + protocol check, console WebSocket route accepting HTTP/2 extended CONNECT |
| `web/e2e/ui-smoke.mjs` (Edge headless) | 43 | pass — adds single Inventory entry in the sidebar, tree sections Host / Groups / All VMs, "Add host" from the Host section context menu, All VMs view, groups note about Hyper-V VM groups, redirect of the old `/vms` link |
| `web` Vitest | 1 | pass |
| Manual probe (real Kestrel, `ClientWebSocket` HTTP/2 and HTTP/1.1, no session) | 2 | before the fix HTTP/2 got **400** (CSRF on CONNECT) and HTTP/1.1 401; after the fix both reach authentication (401) |

Not covered without a Hyper-V host where the agent has rights: creating a VM in a chosen folder, the folder browser
on a real server (LUNs, mount points), adding an ISO folder from the browser and booting from it (real-host test plan
V13–V15). The new-VM wizard is not exercised by the e2e (it needs a host with Hyper-V access): it is covered by type
checking and by the API tests.

## v0.3.0 run (2026-11-05, development machine: Windows 11 Business 26200, non-administrator)

| Suite | Count | Result |
|---|---|---|
| `tests/HvPortal.Agent.Tests` | 44 | pass — adds operation argument validation before PowerShell, forbidden constructs absent from the operations script, mutating vs read-only command classification |
| `tests/HvPortal.Gateway.Tests` | 134 | pass — adds nested group inheritance, cycle/duplicate/delete rules, **group membership and move escalation (Allow gained / Deny lifted)**, per-section configuration permissions, `requires_off`, **server re-stamping of VM id and ISO roots**, re-authorization at execution, ISO library containment, VM creation permission per host + group placement, checkpoint apply rules, **console ticket single use / session binding / foreign Origin rejection / frame streaming / input validation**, CSP nonce consistency |
| `scripts/dev/Invoke-SmokeTest.ps1` (real Kestrel + real agent) | 46 | pass — adds **gateway → agent → PowerShell script path with the real Hyper-V error** (no rights on this machine: 502 `host_operation_failed`), console ticket 404, ISO library validation, group tree rules, CSP style nonce matching the SPA shell |
| `web/e2e/ui-smoke.mjs` (Edge headless) | 31 | pass — new shell (header, sidebar, inventory tree, recent tasks), host tabs and ISO library editor, group creation from the tree + context menu, command palette, account menu radios, dark theme, **no CSP violations or page errors** |
| `web` Vitest | 1 | pass |

Not covered without a Hyper-V host where the agent has rights: real configuration changes, VM creation,
checkpoints, console frames and input (see the real-host test plan, v0.3 section).

## What has been executed (Milestone 1, development machine: Windows 11 Business 26200, non-administrator)

| Suite | Count | Result | What it proves |
|---|---|---|---|
| `tests/HvPortal.Agent.Tests` | 39 | pass | WMI state mapping, operation availability, WMI return codes, RASD InstanceID parsing, command de-duplication (same id executes once), malformed/undefined command rejection, shutdown-reason sanitization, pinned-CA validation, fingerprint comparison |
| `tests/HvPortal.Gateway.Tests` (unit) | ~50 | pass | PBKDF2 (no truncation, NFKC, rehash), password policy (length 15–256, blocklist incl. SHA-1 file, repetition, sequences), RFC 6238 vectors, drift ±1, replay rejection, key ring AEAD + purpose binding + export passphrase, recovery code HMAC, internal CA (subject chosen by gateway, foreign/self-signed/server certs rejected, weak CSR rejected), RBAC (default deny, inheritance, host/VM/group scopes, deny precedence, platform permissions global-only, disabled users), audit chain (modification and deletion detected) |
| `tests/HvPortal.Gateway.Tests` (integration, real pipeline on TestServer + SQLite) | ~33 | pass | unauthenticated access, CSRF, VM isolation (404), force permission + denied audit, typed confirmation, **revocation enforced at job execution**, sessions revoked after admin reset, re-authentication window, **escalation prevention**, last admin protection, user-name injection, hostile query strings, idempotency keys, **TOTP replay**, MFA challenge brute-force bound, progressive delay without account lockout, **restart before/after dispatch**, reconciliation without re-send, **channel loss**, unreachable host retry/fail, cancellation rules, per-VM lock, OpenAPI generation and protection |
| `scripts/dev/Invoke-SmokeTest.ps1` (real Kestrel, real TLS, real agent process) | 35 | pass | setup token + ACL, CSRF/Origin, common password, setup once, MFA restriction, TOTP enrollment (local QR), replay, recovery codes single use, generic login errors, port separation, enrollment with wrong pin refused, token single use, **mTLS agent channel**, real inventory reporting `AccessDenied` truthfully, revocation disconnects and blocks reconnection, audit chain verification, denied actions audited |
| `web` Vitest | 1 | pass | IT/EN dictionaries have identical keys |
| `tests/HvPortal.Gateway.Tests` (downloads) | 4 | pass | package download requires `hosts.manage`, missing package reported (no fake), real SHA-256 and manifest signature state, download audited, connection info never offers loopback names |
| `web/e2e/ui-smoke.mjs` (Edge headless via playwright-core) | 16 | pass | as below, plus: guide with ready-to-paste command never pointing to localhost, token countdown, package download matching the displayed SHA-256, live "agent connected" status in the guide, guide page in Italian and English |
| (previous run) `web/e2e/ui-smoke.mjs` | 10 | pass | setup → forced MFA → recovery codes → dashboard; host creation and enrollment dialog; real agent enrollment; **live update via SignalR**; host detail shows real access status; audit verification from UI; language/theme switch; no horizontal scroll at 390 px; **no CSP violations** |
| `installer/Build-Release.ps1 -SkipTests` | — | pass | self-contained publish of gateway and agent, SPA included, dependency inventory, SHA-256 checksums; unsigned (no certificate) |

## Executed on a real Hyper-V host (production, 2026-11-05)

- `Install-Agent.ps1` elevated on a real Hyper-V host: service account, *Hyper-V Administrators* membership,
  enrollment over the network to a gateway on another machine, mTLS channel, automatic reconnection after gateway
  restarts. Issues found and fixed: loopback URL in enrollment instructions, gateway bound to loopback only,
  transient "Access is denied" on first launch of the unsigned executable (antivirus scan), expired token reuse.
  These pitfalls are now covered by the in-app guide.

## Not executed yet (requires real hosts or elevation)

- Power operations on real VMs and the restart heuristic (inventory accuracy to be compared with Hyper-V Manager).
- `Install-Gateway.ps1` as a service, firewall rules, upgrade/rollback, uninstall.
- WS2025 Standard/Datacenter and WS2022 compatibility.
- Load/performance with many hosts/VMs; SignalR scale.

→ See [verification/real-host-test-plan.md](verification/real-host-test-plan.md).

## How to run

```powershell
dotnet test HvPortal.slnx
.\scripts\dev\Start-DevGateway.ps1 -Reset          # in a second terminal
.\scripts\dev\Invoke-SmokeTest.ps1
# UI e2e (fresh gateway; use separate ports if a dev gateway is already running): 
cd web; node e2e/ui-smoke.mjs --url https://localhost:18443 --agent-port 18444 --data $env:LOCALAPPDATA\HvPortal\dev-gateway `
  --agent-data $env:LOCALAPPDATA\HvPortal\dev-agent --agent-exe ..\src\HvPortal.Agent\bin\Debug\net10.0-windows10.0.17763.0\HvPortal.Agent.exe --out e2e-out
# Setup wizards: pictures of every page (no elevation, nothing installed); the guided-setup e2e checks need
# Gateway__PackagesDirectory pointing at a release (gateway\packages with HvPortal.Agent.Setup.exe)
dotnet build installer\HvPortal.Setup -c Release -p:SetupComponent=Gateway -o artifacts\setup-probe\Gateway
artifacts\setup-probe\Gateway\HvPortal.Gateway.Setup.exe --render artifacts\setup-probe\shots --lang it
```

The test double `FakeAgentDispatcher` exists only in the test project; production code never simulates host results.
