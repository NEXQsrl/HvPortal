# Testing

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
```

The test double `FakeAgentDispatcher` exists only in the test project; production code never simulates host results.
