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

## v0.3 operations (agent 0.3.0 on the host; use a test VM, never production workloads)

| ID | Procedure | Expected |
|---|---|---|
| V1 | VM page → Settings on a running Gen2 VM | live values match Hyper-V Manager; vCPU/dynamic memory/Secure Boot disabled with "VM must be off" |
| V2 | Off VM: change vCPU, memory startup/min/max, notes, auto start/stop, checkpoint type; Apply | job Succeeded, "Applied: …" lists every step; values visible in Hyper-V Manager |
| V3 | Rename VM | name updated in Hyper-V and portal (inventory), job history keeps the old name |
| V4 | Host → Settings → ISO library `D:\ISO`; VM → DVD → choose ISO; then eject | ISO mounted/unmounted; ISO outside the folder refused (400); junction inside the library refused by the agent |
| V5 | Network: move a vNIC to another switch, set VLAN 10, back to untagged, add a vNIC | Get-VMNetworkAdapterVlan shows the values; management connectivity of the host unaffected |
| V6 | Add a 10 GB dynamic disk; expand it to 20 GB (VM off and on, SCSI) | new `<vm>_<timestamp>.vhdx` in the host default folder attached; expand grows only; expand refused with checkpoints |
| V7 | New VM wizard Gen2 + ISO + Secure Boot template + vTPM + start; Gen1 without disk | VM created, boots from ISO in the console; TPM enabled; job result shows VM id; placed in the chosen group |
| V8 | Checkpoints: create (running), apply (off; typed name), delete with children | tree matches Hyper-V Manager; merge completes; apply on a running VM refused |
| V9 | Console: Gen2 UEFI boot screen, Windows setup keyboard (IT layout), Ctrl+Alt+Del at logon, Type text password, mouse after integration services, full screen with Win key | screen updates within ~1 s; input correct; idle close after 15 min; audit open/close entries |
| V10 | Console with a user who loses `vm.console` while connected | session closed within 60 s with "no longer authorized" |
| V11 | Groups: VM in nested folder; VM operator assigned on the parent folder | operator sees and powers the VM; moving the subfolder out removes access; organizer without `roles.manage` cannot move VMs into the operator's folder |
| V12 | Host with `Hyper-V-PowerShell` removed / GPO AllSigned | operations fail with the explicit error message; inventory/power/console unaffected |
| V13 | (agent 0.3.1) Wizard → Storage → Browse: drives of the host incl. LUN/mounted volumes; open D:\ | Windows, Users, Program Files, ProgramData, inetpub, PerfLogs, VBRCatalog, ovhupd, Packages, `$Recycle.Bin`, System Volume Information not listed; free space correct |
| V14 | (agent 0.3.1) Create a VM in `D:\HV-Test` (folder not existing) with a 20 GB dynamic disk | `D:\HV-Test\<name>\Virtual Machines\<id>.vmcx` and `...\Virtual Hard Disks\<name>.vhdx` (verify the exact layout created by `New-VM -Path`); typing `C:\Windows\x` is refused; re-creating with the same name refused (non-empty folder) |
| V15 | (agent 0.3.1, admin) Wizard → Installation → Browse an ISO outside the library | folder added to the host ISO library (audit `hosts.iso_library.update`), VM boots from the ISO in the console; a VM operator without `hosts.manage` sees no Browse button |
| V17 | (agent 0.3.2) Portal group "Linux" with 1 VM, sub-group "Ubuntu" with 1 VM; wait ~5 s | `Get-VMGroup` shows VM collections "Linux" and "Ubuntu" and management collection "Linux" containing both; `(Get-VMGroup Ubuntu).VMMembers` = the VM; confirm the id property name (`Get-VMGroup \| Get-Member`) |
| V18 | Rename "Ubuntu", remove its VM, delete it | renamed in place (same id), then removed; a group created by hand with `New-VMGroup` is untouched |
| V19 | Create a native group by hand, then Host > Hyper-V VM groups > Import | portal group created with the VMs, row shows "managed"; renaming it in the portal renames it in Hyper-V |
| V20 | Delete a mirrored group in Hyper-V Manager / PowerShell | recreated at the next forced pass (≤ 30 min) or with "Sync now" |
| V16 | Console from Edge/Chrome on the production URL (HTTP/2) | the WebSocket opens (no "WebSocket connection failed"); frames and input work |

## Result log template

| ID | Date | Host build / edition | Result | Evidence (job id, audit id, screenshot) | Notes |
|---|---|---|---|---|---|
| R1 | | | | | |
