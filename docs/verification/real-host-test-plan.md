# Real-host verification plan (Milestone 1)

These procedures **require real Windows Server hosts with Hyper-V and VMs**. They have not been executed yet; the
development machine had Hyper-V running but no administrative rights, so only the "no access" path was observed.
Record results (date, build, edition, pass/fail, evidence) in a copy of the table at the end.

## Lab

- G: gateway on a WS2025 member or workgroup server (non-Hyper-V).
- H1: WS2025 **Datacenter** Hyper-V host, workgroup. H2: WS2025 **Standard** Hyper-V host, domain member.
- Optional H3: WS2022 Datacenter (evaluation only).
- VMs on H1: `gen2-win` (Windows Server guest, integration services), `gen1-linux` (Linux with hv_utils),
  `no-ic` (guest with shutdown integration service disabled), `locked` (Windows guest with a locked console and
  unsaved Notepad document).
- Users: `admin` (platform admin), `op-h1` (VM operator scoped to H1), `op-vm` (VM operator on `gen2-win` only),
  `ro` (read only).

## Procedures

| ID | Procedure | Expected |
|---|---|---|
| R1 | `Install-Gateway.ps1` on G (elevated) | service runs as `NT SERVICE\HvPortalGateway`; `whoami /priv` of the process shows no admin privileges; ACLs on data dir = SYSTEM, Administrators, service only |
| R2 | First setup from a browser on another machine | token file readable only by admins; setup + forced MFA; token file deleted |
| R3 | `Install-Agent.ps1` on H1 with token + fingerprint | service runs as `NT SERVICE\HvPortalAgent`, member of Hyper-V Administrators, **not** of Administrators; host online; access `Ok` |
| R4 | Wrong fingerprint / reused token / expired token (wait 16 min) | enrollment refused, audit `agent.enroll` Denied |
| R5 | Inventory accuracy | VM list, generation, version, vCPU, memory (static and dynamic), uptime, heartbeat, guest OS match `Get-VM` / Hyper-V Manager |
| R6 | Each power operation on `gen2-win` and `gen1-linux` from every allowed state | job `Succeeded`; state matches Hyper-V Manager; Hyper-V return code recorded |
| R7 | Guest restart on `gen2-win` | job `Succeeded` with "uptime reset observed" — **validates the restart heuristic**; if `Unverified`, adjust the heuristic |
| R8 | Shutdown on `no-ic` | action not offered; API returns invalid/NotSupported; nothing executed |
| R9 | Shutdown on `locked` | job `Failed` with Hyper-V 32781 explained; guest data preserved |
| R10 | Turn off / Reset without typed name | rejected (`confirmation_required`) |
| R11 | Change a VM state in Hyper-V Manager | history shows `vm.state_changed_externally` within 30 s |
| R12 | Create/delete/rename a VM outside the portal | `vm.discovered` / `vm.missing` / `vm.renamed` |
| R13 | `op-vm` lists VMs; tries GET/POST on another VM id | sees only `gen2-win`; other ids → 404; audit Denied for power attempts |
| R14 | Remove `op-h1` assignment while a job is queued (stop the agent first so it queues) | job fails `authorization_revoked`, nothing sent |
| R15 | Stop the gateway service during a long Save on a large-memory VM; restart | job goes `Reconciling` → `Succeeded` if the VM is Saved, else `Unknown`; command not re-sent (agent log) |
| R16 | Unplug H1 network during a Shutdown | job → `Reconciling`; after reconnect → verified result |
| R17 | Revoke H1 in the portal | agent disconnected immediately; reconnect attempts denied and audited |
| R18 | Certificate renewal: set `Gateway:Security:AgentCertificateDays` to 1, re-enroll, wait | renewal within 8 h; old certificate revoked as superseded |
| R19 | Upgrade with a broken build (e.g. remove a DLL) | installer rolls back binaries; service running old version |
| R20 | Uninstall agent and gateway | services, firewall rules, group membership removed; data kept unless `-RemoveData` |
| R21 | DR: export keys, restore DB + keys on a new server with same names | users sign in with TOTP, audit verifies, agents reconnect |
| R22 | H2 (Standard, domain) repeat R3, R5, R6 | same results; S2D/shielded shown unavailable |
| R23 | WS2022 (H3) repeat R3, R5, R6 | results recorded in compatibility matrix; no support claim before pass |
| R24 | Account recovery via `admin recover-account --reset-mfa` | one-time token works once, sessions revoked, audit records OS identity |
| R25 | Firewall: from a non-allowed subnet connect to 8443/8444 | blocked when `-UiRemoteAddress/-AgentRemoteAddress` set |

## Result log template

| ID | Date | Host build / edition | Result | Evidence (job id, audit id, screenshot) | Notes |
|---|---|---|---|---|---|
| R1 | | | | | |
