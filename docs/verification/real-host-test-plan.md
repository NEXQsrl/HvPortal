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
| V21 | (agent 0.3.3) Storage > Files: open D:\, upload a 6 GB ISO, then mount it from the ISO library | progress to 100 %, file appears only at the end, audit `host.files.upload` with SHA-256 equal to `Get-FileHash` on the host |
| V22 | Download the VHDX of a running VM and of a stopped VM | running: refused "file in use"; stopped: downloads, SHA-256 matches |
| V23 | Upload into a folder the agent account cannot write (e.g. an ACL-protected folder) | `access_denied`, nothing left behind (no `.hvpart` file) |
| V24 | Entra: register the app (Web, redirect URI from Settings), enter tenant/client/secret, "Test connection", enable | metadata read; login page shows "Sign in with Microsoft" |
| V25 | Create a Microsoft user (UPN) with a role, sign in with that account; sign in with a non-provisioned account of the same tenant | first: signed in, user row no longer marked "*"; second: "not enabled in this portal", audit denied |
| V26 | As the Entra user perform a sensitive action (e.g. create a user) | popup asks to sign in again; action completes; audit `auth.reauthenticate` method entra |
| V27 | (agent 0.3.4) VM summary of a dynamic-memory VM and of a replicated VM; edit the notes in Hyper-V Manager | creation time / path equal to `Get-VM \| fl CreationTime,Path`; memory demand close to `MemoryDemand`; replication values equal to `Get-VMReplication`; notes updated within one inventory cycle and an event "notes changed" |
| V28 | Settings > Backup: download, change something, restore the backup, "Restart the service now" (service mode) | service restarts by itself, change is gone, everybody signed out, audit verify OK, `db\pre-restore-*` present |
| V29 | (agent 0.4.0, management) Install the agent with `-Management` on a server without Hyper-V, add a workgroup host with `HOST\Administrator` | management agent online; host inventory with VMs, CPU source `hypervisor` (never the management server's CPU), *Hyper-V access* `Ok`; `HvPortalAgent` not in *Hyper-V Administrators* on the management server |
| V30 | Same host with a wrong password, then with a non-built-in local admin without `LocalAccountTokenFilterPolicy`, then with the host powered off | `AccessDenied` with the account in the message; then `AccessDenied`; then `Error` "cannot reach … WMI/DCOM"; existing VMs kept (not marked missing) |
| V31 | Remote host: start/shutdown a VM, open the console, change the VM settings, create a checkpoint, create a VM with location `D:\VMs` | all succeed; the configuration change and the VM creation run through WinRM (job log), the console shows the guest |
| V32 | WinRM HTTP without TrustedHosts on the management server; then with it; then HTTPS with an untrusted certificate | first: error from WinRM naming TrustedHosts, job failed (no success); second: works; third: certificate error, no fallback to HTTP |
| V33 | Remote host Storage > Files and the folder pickers | drives listed from the host (not from the management server); upload/download through `\\host\D$`; hidden/system folders and blocked roots refused as on a local agent |
| V34 | Change the password in the portal, move the host to another management agent, revoke the host | new password used at the next cycle; old agent stops managing it, new one starts; after revoke both forget it (no inventory, commands refused) |
| V35 | (0.4.0) VM settings on a **running** VM: Gen2 dynamic memory, Gen2 static memory, Gen1 with an IDE disk | locked with "VM must be off": vCPU, dynamic memory, Secure Boot, automatic stop action, startup memory (dynamic only), DVD when the VM has none, NIC/disk add on Gen1, expansion of IDE or `.vhd` disks. Allowed and applied: rename, notes, start action/delay, checkpoint type, CPU reserve/limit/weight, memory min lowered / max raised / buffer / priority, static memory resize (Windows Server 2016+ guest), ISO change, switch/VLAN, NIC and SCSI disk hot-add on Gen2, SCSI VHDX expansion. Confirm `Set-VM -AutomaticStopAction` really fails while running (otherwise unlock it) |
| V36 | (0.4.0) `Install-Agent.ps1 ... -ServiceAccount svc-hvportal` on a workgroup host; then sign in as that user locally, over RDP and `net use \\host\c$ /user:host\svc-hvportal`; re-run the installer without `-ServiceAccount`; `Uninstall-HvPortal.ps1 -Component Agent` | service runs as `HOST\svc-hvportal`, member of *Hyper-V Administrators* only (`NT SERVICE\HvPortalAgent` removed); `secpol.msc` shows *Log on as a service* and the four deny rights; every sign-in attempt refused; inventory/power/console/VM settings/VM creation (with `icacls` Modify on the folder) work as with the virtual account; upgrade keeps the account; uninstall removes user, rights, profile |
| V37 | (0.4.0) Import a PFX from the enterprise CA, re-run `Install-Gateway.ps1` with `-UiPort 443 -UiCertificateThumbprint`; then with IIS listening on 443 | portal at `https://<name>` with the CA certificate (no warning on domain clients), agents still connected on 8444; key read granted (no manual step); with IIS: installer stops with "port 443 already in use by System" before touching the service |
| V38 | (agent 0.5.0) Networks > New network: Private, Internal, then External on a spare adapter with and without "let the host use this adapter"; delete each | switches appear in Hyper-V Manager with the chosen type; external with sharing creates the host vEthernet and keeps the host reachable; the job ends Succeeded (directly or after "verified by reconciliation" if the agent's connection dropped); delete is refused while a VM is connected; duplicate name refused |
| V39 | (agent 0.5.0) Apply a checkpoint to a **running** VM (production and standard checkpoint) | VM turned off, checkpoint applied, job result lists `turnedOff`, `restored` and the state after (Off for production, Saved for standard); the VM starts again from the portal |
| V40 | (0.5.0) Group with limits (2 VMs, 4 vCPU, 8 GB, 100 GB, 1 network; one host; one switch; storage `D:\Tenants\A`) and a user with role *Folder administrator* on it | the user creates VMs only in that group and host, only on the allowed switch, files land in `D:\Tenants\A\<vm>`; quota errors name the group and the numbers; a new disk goes to `D:\Tenants\A\<vm>\Virtual Hard Disks`; usage on the group page equals `Get-VM`/`Get-VHD` totals; the user creates one private network and not a second; the file explorer of the group shows only `D:\Tenants\A` |
| V41 | (agent 0.5.0) Dynamic-memory VM in a group with a memory limit | the group's memory usage counts the VM's maximum memory (`Get-VMMemory .Maximum`), not the startup |
| V42 | (0.5.0) Settings > Backup: wait for (or run) the automatic copy; corrupt `hvportal.db` on a test gateway and restore the copy as documented | a verified `hvportal-auto-*.db` every 24 h, copies older than 7 days removed; after the restore the gateway starts with the data of the copy, audit verify OK |
| V43 | (0.5.0) `HvPortal.Gateway.Setup.exe` on a clean server: default choices; then run it again changing the portal port to 443 and importing a CA .pfx; then with IIS on 443 | first run: service installed, portal reachable at the address shown, setup token shown and accepted by the portal; second run: settings pre-filled, certificate imported into `LocalMachine\My`, portal on 443 with the CA certificate, firewall rule on the new port when ticked; third: the wizard refuses port 443 before installing. Log in `C:\ProgramData\HvPortal\SetupLogs`, staging folder under `Program Files\HvPortal\Setup` removed |
| V44 | (0.5.0) `HvPortal.Agent.Setup.exe` downloaded from the portal on a Hyper-V host: *Paste from the portal*, automatic account; on a second host with a dedicated local user; on a management server; then run it again (update) | host online in the portal after the wizard; the dedicated user has only the rights of V36; management mode skips the Hyper-V group; the update keeps the connection (no token asked); a wrong or expired token is reported in the wizard log and nothing is left half-installed; SmartScreen behaviour with and without the publisher certificate recorded |
| V45 | (0.5.1) A host that already has VM groups (left by another gateway, or made with `New-VMGroup`: a management collection with two VM collections with VMs, a standalone VM collection with VMs, an empty VM collection) is added to a gateway without groups; later one more native group is created in Hyper-V. Also: a 0.5.0 gateway with such a host, never organised, is updated to 0.5.1 | shortly after the first inventory the portal shows the folders with their VMs; `Get-VMGroup` on the host is unchanged (same ids, names, members) and the empty group is still there, listed as "not imported"; audit entry `hosts.vmgroups.import` by `system` with `automatic: true`; the group created later is imported only with *Import*; after the update the groups appear at the first pass (within about a minute of the service start) |
| V16 | Console from Edge/Chrome on the production URL (HTTP/2) | the WebSocket opens (no "WebSocket connection failed"); frames and input work |

## Result log template

| ID | Date | Host build / edition | Result | Evidence (job id, audit id, screenshot) | Notes |
|---|---|---|---|---|---|
| R1 | | | | | |
