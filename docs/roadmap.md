# Roadmap

Each milestone is complete only when frontend, backend, authorization, audit, error handling, unit, integration,
negative-authorization tests and the real-host procedures for its features pass.

## Milestone 1 — foundation (this delivery)
Done in code and automated tests: repository, installer scripts, gateway service, agent service, secure enrollment,
SQLite, administrator bootstrap, TOTP, RBAC (scopes Global/Host/Group/VM), audit, inventory, VM power jobs, web UI.

**Remaining to close M1**
1. Run the [real-host test plan](verification/real-host-test-plan.md) on WS2025 Datacenter and Standard
   (Hyper-V calls, service accounts, firewall, upgrade/rollback, uninstall).
2. Confirm the restart/reset uptime heuristic and the `InstanceID` parsing of RASD on real VMs.
3. Install scripts executed elevated end-to-end (not possible in the development environment used so far).
5. External audit forwarding (syslog/webhook) — currently export only.

## v0.3.0 — delivered (code + automated tests; real-host validation pending)
- VM configuration (general, CPU, memory, Secure Boot, DVD/ISO from a per-host ISO library, vNIC switch/VLAN/add,
  new and expanded VHDX) as jobs with per-section permissions and off-only rules.
- VM creation wizard (Gen1/Gen2, CPU, memory, disk, switch, ISO + first boot, Secure Boot template, vTPM, start,
  placement in a group).
- Checkpoints: tree, create, apply (off/saved), delete with children.
- Host resources: switches, VM virtual disks, ISO library.
- Logical groups as nested folders with RBAC inheritance and access-change guard; inventory tree (hosts view and
  groups view) with drag and drop.
- Basic browser console (hypervisor screen capture, keyboard, synthetic mouse) — [console.md](console.md).
- UI rebuilt with shadcn/ui: vCenter-like layout (navigator tree, object tabs, recent tasks, command palette).

## v0.3.1 — delivered (code + automated tests; real-host validation pending)
- Console fixed in browsers using HTTP/2 (WebSocket over extended CONNECT, RFC 8441).
- Inventory as the single entry point: one tree (Hosts → VMs, Groups → folders/VMs, All VMs); add host / new group /
  new VM from the tree and the inventory page; "New VM in this group" in the group context menu.
- New-VM wizard: VM location chosen with a host folder browser (system folders hidden and refused), memory and disk
  presets, ISO browser for administrators (adds the folder to the host ISO library), footer no longer clipped.
- Agent protocol 3 (agent 0.3.1): `BrowseFolders`, `NewVm.Location`; the gateway never sends them to older agents;
  agents reply `Rejected` to commands they cannot parse instead of dropping the channel.

## v0.7.0 — delivered in code (automated tests against the test double; real-host validation pending)
Agent protocol 9 (agent 0.7.0). Nothing that touches Hyper-V has run on a real host yet: real-host plan V49–V67.
- **VM backups** (ADR-031): repositories on a host folder or an SMB share, also a NAS outside the domain with stored
  credentials and a local staging folder; backup jobs for VMs and groups with daily (time zone) or hourly schedule,
  retention by count and age, *Keep*; restore points = export of a checkpoint + SHA-256 manifest, verify, rescan;
  restore as a new VM, over the original VM, as an isolated restore test (heartbeat), a single disk, single files
  (read-only attach). Permissions `backup.run`, `backup.restore`, new `backup.manage`; role *Backup operator* extended.
  Long jobs have their own concurrency budget. Not provided: application-consistency guarantee, incremental copies,
  deduplication, immutability.
- **Notifications** (ADR-033): e-mail (SMTP STARTTLS) and webhooks (generic, Teams, Slack) for backup runs, failed
  restore tests and outdated agents. **API tokens** (ADR-032): read-only `backup.read` tokens for monitoring on
  `/api/v1/public/backup/*`.
- **Agent update from the portal** (ADR-029): signed packages only (catalog signed by the publisher of the installed
  agent), SYSTEM updater task the agent may only start, no downgrade, job verified by the reconnection;
  `-DisableRemoteUpdate`. The first 0.7.0 installation is by hand.
- **Upgrade fix** of the installers: wait for the service process to exit, retry locked files, put the previous binaries
  back when the replacement fails (the "clrjit.dll access denied" upgrade failure).
- **Portal networks** (ADR-030): network = switch + access VLAN, chosen by name for new VMs, added and changed adapters;
  VLAN change can re-tag the adapters on it.
- **VM move** (ADR-035): to another host (`Move-VM`, live migration, with or without storage) and to another folder
  (`Move-VMStorage`); group limits and storage folders apply; the portal record follows the VM.
- **Allowed storage folders** (ADR-034): `Install-Agent.ps1 -StorageFolders` (and the setup page) limits the folders
  the portal may use on a host, enforced by the agent; set in the portal for remote hosts; new folders in the file
  explorer.
- **Host health** (ADR-036): hardware, disks, volumes, pools, NICs, recent hardware events, Hyper-V services.
- UI (being finished): Settings area tree with Users and Audit; VM summary with disks and uptime cards; scrollable
  dialogs; new sign-in page; topic guides for every user (Guide > topics).

## v0.6.0 — delivered (code + automated tests + browser e2e; real-host validation pending)
- VM templates (ADR-028, agent protocol 8): create a template from a VM that is off (copy of its disks), create VMs
  from it choosing name and resources (also from the New VM wizard), rename and delete; permission
  `templates.manage`; template library folder per host; quotas of the folders count the template disks. Real-host
  plan V46-V48. Later: copy between hosts, versions, guest customization at first boot.
- Audit for people: actions in words (IT/EN), names of the resources instead of identifiers, who and how, readable
  details with before/after, filters by area, outcome, period and free text.
- Navigation icons larger and spaced, white in the dark theme; only the current section is highlighted (it was a
  defect: every icon looked selected).
- Security fix: the folder path patterns no longer backtrack exponentially (threat model D3).

## v0.5.1 — delivered (code + automated tests; real-host validation pending)
- The VM groups a host already has are imported automatically the first time the host is mirrored (ADR-027): only
  groups with VMs, nothing removed from Hyper-V, no access gained; the rest stays on request. Real-host plan V45.

## v0.5.0 — delivered (code + automated tests + browser e2e; real-host validation pending)
- Networks: create and remove virtual switches from the portal (External / Internal / Private) with contextual help
  (ADR-025, agent protocol 7).
- Groups as projects (ADR-024): limits per group (VMs, vCPU, memory, disk, private networks, enabled hosts, allowed
  networks, storage paths); role *Folder administrator*; group users create VMs and private networks and reach files
  only within the limits; usage shown on the group page.
- Users can be deleted. Automatic verified copies of the database, 7 days retention (ADR-023).
- Checkpoints can be applied to a running VM (turned off first). VM description replaced by the Hyper-V notes.
- Navigator: the sidebar is gone; icon-only navigation above a tree that follows the page (inventory, networks,
  storage, tasks, audit, settings, guide). Real-host plan V38–V42.
- Graphical setup programs for the gateway and the agent (ADR-026), agent setup downloadable from the portal with
  "Paste from the portal"; page tab bars no longer show a scrollbar. Real-host plan V43–V44.

## v0.4.0 — delivered (code + automated tests; real-host validation pending)
- Management agents (ADR-022): remote Hyper-V hosts (e.g. workgroup) managed from a management server with
  credentials stored encrypted in the portal; one management agent for many hosts; agent protocol 6. The agent on the
  host remains the recommended mode. Real-host plan V29–V34.
- UI: copy buttons show only the icon (label as tooltip); VM settings show the current size of each virtual disk,
  validate the new size against it (must be larger, whole GB) and offer *Cancel resize*.
- VM settings on a running VM lock every field Hyper-V cannot change online (automatic stop action, startup memory
  with dynamic memory, dynamic range only widened, DVD add, Gen1 NIC/disk add, IDE/VHD expansion) and *Apply* refuses
  drafts that still contain such changes; the gateway also refuses automatic stop action and Gen1 hot-add (409
  `requires_off`). Search palette centred on screen. Real-host plan V35.

## v0.3.4 — delivered (code + automated tests; real-tenant validation pending)
- Microsoft Entra ID sign-in configured from Settings, pre-provisioned Microsoft users, Entra re-authentication
  (ADR-019). New user dialog: type (local / Microsoft), role and scope, MFA at first sign-in for local users.
- Settings > Backup and restore of the portal (sealed file, staged restore at the next start; ADR-020).
- VM summary (agent 0.3.4): creation time, path, Hyper-V notes (synchronized, change events), replication
  mode/state/health, memory assigned and demand. Agent update notice lists only the missing functions.

## v0.3.3 — delivered (code + automated tests; real-host validation pending)
- Storage > Files: host file explorer (open folders with one click, download, upload with progress and drag and
  drop, overwrite confirmation), permissions `host.files.read` / `host.files.write`, audited (ADR-018). Agent
  protocol 5 (agent 0.3.3). Folder picker: one click opens a folder.

## v0.3.2 — delivered (code + automated tests; real-host validation pending)
- Portal groups mirrored automatically as native Hyper-V VM groups (`Get-VMGroup`), nested groups as management
  collections; host tab "Hyper-V VM groups" with status, "Sync now" and one-time import of existing native groups
  (ADR-017). Agent protocol 4 (agent 0.3.2).

## Milestone 2
- Remaining VM configuration: boot order, TPM on existing VMs, controllers, disk detach/removal with ownership
  checks, online/offline matrix validated on hosts.
- Checkpoint fallback policy reporting and merge monitoring.
- Tags, filters, bulk operations, start/stop order with dependencies; native Hyper-V VM groups shown separately.
- Networks: switch creation/changes; management-cutting changes with pre-checks and automatic rollback timer.
- Entra ID: OIDC code + PKCE via Microsoft.Identity.Web, BFF, single-tenant default, tenant allow-list, issuer/
  audience/nonce/state validation, encrypted rotating client credentials (prefer certificate), tid+oid identity,
  explicit role mapping from app roles/groups, group overage via Graph, no email-based linking, Conditional Access
  honored, break-glass policy.
- PostgreSQL and SQL Server providers, provider-specific migrations, integration tests on all three, export/import
  procedure with verification.
- Named-pipe local transport (optional), WebAuthn/passkeys.

## Milestone 3
- Failover clusters (`root\MSCluster`): nodes, CSV, quorum, owner resolution per operation, drain/maintenance,
  live and storage migration (standalone hosts: done in 0.7.0).
- Templates, next steps (the base is in 0.6.0): versions, generalized images, unique identities, secure first-boot injection, no embedded secrets, copy between hosts.
- Notifications, next steps (e-mail + webhooks are in 0.7.0): retry and queue, de-duplication, SSRF protection
  (allow-list, no private ranges unless allowed, DNS re-resolution checks), encrypted webhook addresses.
- Backup, next steps (native full-copy backups, restore to a new VM, isolated restore tests and checkpoint-type
  reporting are in 0.7.0): incremental chains (RCT, reference points), signed manifests, repository immutability where
  the storage supports it, copy of restore points to a second repository, backup provider interface + first external
  provider; product configuration/key backup automation.
- Fixes found while documenting 0.7.0: storage-folder check for operations that use the host default folders; access
  change guard for VM moves; re-authentication for repository credentials, notification settings and destructive
  restores; authorization of the host chosen for a file-restore mount.

## Milestone 4
- Console v2 (VMConnect/RDP on 2179, enhanced session) after the [study](console-study.md); basic console done in v0.3.
- S2D monitoring → provisioning (separate, destructive operations gated).
- Native backup engine (VSS, RCT, reference points) if the provider approach proves insufficient.
- Hyper-V Replica.

## Research items
Console protocol feasibility · RCT-based incremental chains · immutability on supported storage only ·
Server Core installation UX · TPM-bound agent keys · authentication of `Move-VM` started by the agent service on the
source host (Kerberos constrained delegation vs. computer account) · which host-health sections need administrator
rights.
