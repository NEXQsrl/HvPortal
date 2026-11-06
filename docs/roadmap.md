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
  live and storage migration.
- Templates (versioned, generalized images, unique identities, secure first-boot injection, no embedded secrets).
- Notifications: email + webhooks with retry, de-duplication, SSRF protection (allow-list, no private ranges unless
  allowed, DNS re-resolution checks).
- Backup provider interface + first external provider; consistency reporting; restore to new VM; isolated restore
  tests; product configuration/key backup automation.

## Milestone 4
- Console v2 (VMConnect/RDP on 2179, enhanced session) after the [study](console-study.md); basic console done in v0.3.
- S2D monitoring → provisioning (separate, destructive operations gated).
- Native backup engine (VSS, RCT, reference points) if the provider approach proves insufficient.
- Hyper-V Replica.

## Research items
Console protocol feasibility · RCT-based incremental chains · immutability on supported storage only ·
Server Core installation UX · TPM-bound agent keys.
