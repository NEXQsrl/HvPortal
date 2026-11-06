# Architecture decision records

Format: context → decision → consequences. Status of all records: **accepted** (Milestone 1).

## ADR-001 Modular monolith + separate privileged agent
- **Context**: the control plane needs consistency (auth, RBAC, jobs, audit) more than independent scaling; host
  operations need privileges the web tier must not have.
- **Decision**: one gateway process with internal modules; one agent per host for all Hyper-V access.
- **Consequences**: simple deployment and transactions; a compromised gateway can only send typed commands that the
  agent validates; HA of the gateway requires a server database (M2) and is not offered with SQLite.

## ADR-002 Agent-initiated WebSocket over mutual TLS with a private CA
- **Context**: hosts may be in workgroups or other domains; inbound management ports on hosts are undesirable.
- **Decision**: agents connect to `:8444/agent/v1/channel` with an X.509 client certificate issued by the gateway's
  internal CA; the gateway checks chain *and* an active, non-revoked database record. Commands flow over the socket.
- **Alternatives**: WinRM/PowerShell remoting (needs Kerberos/TrustedHosts or HTTPS+NTLM, credentials on gateway);
  gateway→agent HTTPS (inbound port on every host); gRPC (more dependencies, same security).
- **Consequences**: no credentials for hosts stored on the gateway; only outbound 8444 from hosts; revocation is
  immediate (socket closed + DB check on connect + watchdog). Exception, opt-in per host: remote hosts reached by a
  management agent with credentials stored in the portal (ADR-022).

## ADR-003 Local channel for same-server installation
- **Decision (M1)**: the same mTLS channel over loopback is used when gateway and agent share a machine: processes and
  identities remain separate, traffic is mutually authenticated and encrypted. A named-pipe transport with an ACL for
  the gateway service SID is a possible optimization, not required for security.

## ADR-004 Enrollment protocol
- One-time token (256-bit, SHA-256 stored, 15 min, bound to a pending host, atomic consumption) + CA fingerprint pin
  delivered out of band. The agent downloads the CA, compares the pin, and only then sends the token over a TLS
  connection validated against that CA. Private keys are generated on the host (P-256) and never leave it.
- Certificates: 90 days, renewed over the authenticated channel when ⅓ lifetime remains; older certificates are
  revoked once the agent proves possession of the new one.

## ADR-005 Password hashing with PBKDF2-HMAC-SHA256 (600 000 iterations)
- **Context**: requirement for trustworthy libraries and upgradeable parameters; preference for platform tooling.
- **Decision**: BCL `Rfc2898DeriveBytes.Pbkdf2` (FIPS-approved, OWASP 2023 parameters), versioned format, transparent
  rehash on login. Argon2id would require a third-party package; it can be added behind the same format prefix.

## ADR-006 Secrets and keys
- Key ring with 256-bit keys wrapped by DPAPI (LocalMachine) in `keys\master.json`, ACL-restricted; AES-256-GCM with
  purpose-bound AAD; HKDF sub-keys for HMAC uses. Passphrase-protected export for disaster recovery (DPAPI alone
  would make DR impossible). ASP.NET Data Protection keys are machine-bound on purpose (losing them only invalidates
  sessions).

## ADR-007 Server-side sessions with cookie handle
- Cookie `__Host-hvp` (Secure, HttpOnly, SameSite=Strict) holds only user id + session id (encrypted by Data
  Protection). Each request re-validates the session row and the user's security stamp → immediate revocation.
- CSRF: anti-forgery token header on every state-changing request + Origin check + SameSite.

## ADR-008 Jobs: at-least-once dispatch avoidance, outcome verification
- Persist `DispatchedAt` before sending; never re-send after an uncertain dispatch; reconcile from observed state;
  agent de-duplicates by command id within its lifetime. Statuses `Unverified` and `Unknown` are first-class.

## ADR-009 SQLite first, provider-specific migrations
- SQLite for single control plane on local disk (WAL). PostgreSQL / SQL Server in M2 with separate migration
  assemblies and integration tests on each. Provider change = export/import with verification, not live switch.

## ADR-010 SPA served by the gateway
- Same origin, strict CSP (`script-src 'self'`), no inline scripts; static assets immutable-cached, `index.html`
  no-cache. React + Vite chosen for maturity.
- **Revised in v0.3 (see ADR-014):** the UI now uses shadcn/ui (source components copied into the repo, built on
  Radix UI primitives, Tailwind CSS, lucide icons; all MIT). They are vendored source files, so the supply chain is
  the Radix/Tailwind packages pinned in `package-lock.json`.

## ADR-011 WMI via System.Management in the agent
- Same documented classes used by Microsoft samples; synchronous calls run on dedicated threads. Inventory, power,
  delete and console stay on WMI. Configuration operations use PowerShell as described in ADR-012.

## ADR-012 Typed VM operations through one fixed PowerShell script
- **Context.** VM configuration, creation and checkpoints through raw WMI (`ModifyResourceSettings`, embedded CIM
  instances, `Msvm_ImageManagementService`) are large, version-sensitive and easy to get subtly wrong; the Hyper-V
  PowerShell module is Microsoft's supported, documented surface for the same operations ("Usa strumenti nativi").
- **Decision.** The agent ships `Ops\HvPortal.Ops.ps1` (read-only in Program Files). For each typed operation it runs
  `powershell.exe -NoProfile -NonInteractive -File <script>` with a fixed argument list and passes one JSON request on
  stdin. The script contains a closed `switch` of operations; every value is cast to an explicit type and passed as a
  named cmdlet parameter (splatting of fixed keys). There is no `Invoke-Expression`, no script block built from
  input, no `Start-Process`; VMs are resolved by GUID; new disks are created only in the host default VHD folder;
  ISO paths must be inside the host's ISO library roots and must not traverse reparse points. A unit test fails the
  build if forbidden constructs appear in the script code.
- **Validation in three places**: gateway API (`OperationValidation`, permissions per section, state rules), job
  execution (re-authorization, re-stamping of server-controlled fields such as VM id and ISO roots), agent
  (`PowerShellOperationRunner.Validate`) before PowerShell starts.
- **Consequences.** Requires the `Hyper-V-PowerShell` feature on the host; GPO `AllSigned` blocks the unsigned script
  (sign the release); PowerShell Constrained Language Mode (WDAC script enforcement) is not supported yet. A process
  per operation costs ~0.5–2 s, acceptable for configuration changes (not used for the console or inventory).

## ADR-013 Console through hypervisor screen capture and input injection
- **Decision.** Basic console with documented WMI methods (`GetVirtualSystemThumbnailImage`, `Msvm_Keyboard`,
  `Msvm_SyntheticMouse`) relayed over the existing agent channel; one-time tickets; see [console.md](console.md).
- **Rejected for now.** Proxying VMConnect (RDP on 2179 with a pre-connection blob): undocumented exchange, needs a
  Windows principal authenticated by vmms and a full RDP client in the agent; kept as research.
- **Consequences.** Works before boot and without guest networking; low frame rate; no clipboard/audio.

## ADR-014 CSP: per-response style nonce, scripts unchanged
- Radix UI inserts a few `<style>` elements at runtime (scroll locking for modal dialogs, Select/ScrollArea
  viewports). Instead of `'unsafe-inline'`, every response carries `style-src 'self' 'nonce-<random>'`; the SPA shell
  (`/` and client routes) is rendered by `SpaShell` which writes the same nonce into
  `<meta property="csp-nonce">`, and the UI passes it to those components (`get-nonce`, Radix `nonce` props).
  `script-src` stays `'self'` with no nonce. `index.html` is never served as a static file.

## ADR-028 VM templates: a copy of the disks of a prepared VM (agent protocol 8)
- **Context**: administrators want the VMware workflow: prepare a VM once, keep it as a model, create new VMs from it
  choosing name and resources. Hyper-V has no template object.
- **Decision**: a template is a folder `<template library of the host>\<template id>` holding **copies** of the
  virtual disks of a VM, plus a record in the portal (`VmTemplates`) with the hardware of that VM.
  - *Create* (`POST /vms/{id}/template`, job `template.capture`): the VM must be off, without checkpoints, with plain
    .vhdx/.vhd disks (no pass-through, no differencing disks). `NewTemplate` copies the disk files; the VM is never
    changed, removed or exported, and can be used again when the job ends. A failed copy removes the folder it
    created.
  - *Use* (`POST /hosts/{id}/vms` with `vm.templateId`, job `vm.create` / `FromTemplate`): the ordinary creation with
    the template's disks copied into the folder of the new VM (`NewVm.TemplateDisks`): the first as boot disk, the
    others attached to the SCSI controller. Name, vCPU, memory, network, location, options are the caller's; the
    generation is the template's; the boot disk can be made larger (never smaller: the guest extends its partition).
    The template files are only read.
  - *Delete* (`POST /templates/{id}/delete`, typed name, job `template.delete`): `RemoveTemplate` deletes the disk
    files directly inside the template folder and then the folder; anything else in it stops the operation with
    nothing deleted. Never a recursive delete of a path received in a request.
  - Why copies and not "convert the VM": a copy cannot lose anything (the VM stays), does not depend on
    `Export-VM`/`Import-VM` compatibility between configurations, and a template can never be started by mistake.
    The price is disk space and copy time.
  - **Paths are the gateway's**: the library folder is chosen per host by `hosts.manage` (`Hosts.TemplateLibraryPath`,
    same syntax rules as the ISO library, system folders excluded); the folder of a template is derived from its id;
    the disk list is what the agent reported when it copied. The API never accepts those from a client and the job
    runner stamps them again from the record at execution (`StampTemplateAsync`); a creation without `templateId`
    never carries template fields. The agent validates again (text + real file system: fixed local drive, no
    hidden/system folders, no junctions) and the script checks that every path is `<library>\<guid>[\file.vhdx]`.
  - **Permissions**: new `templates.manage` (host scope) to create, rename, delete; `vm.create` to use a template
    (on the host, or inside a folder whose limits open the host). Templates are listed only to those two groups of
    people; the location of the files only to who manages them. Inside a folder (ADR-024) every disk of the template
    counts against the disk quota and the usual network/storage rules apply.
  - **Truthful states**: `Creating`, `Ready`, `Failed`, `Unverified`, `Deleting`, written in the same transaction
    as the final state of the job. A template is `Ready` only when the agent returned the list of disks it copied;
    a lost result is `Unverified` (not usable; delete and create again); a request that never reached the host
    leaves no record. A template that stops being ready while a creation from it is queued makes that job fail
    (`template_not_available`): never an empty VM passed off as a deployment.
  - Protocol 8 (agent 0.6.0): `NewTemplate`, `RemoveTemplate`, `NewVm.TemplateDisks`; older agents never receive
    them (an older agent would create an empty VM). Copies may run up to 8 hours (30 minutes for everything else);
    the time allowed is 15 minutes plus 20 seconds per GB.
- **Not done by the portal**: generalizing the guest. A template is an exact copy: without Sysprep (Windows) or
  cloud-init / a cleaned machine-id (Linux) every VM created from it has the same computer name and identifiers. The
  UI says so where templates are created and listed. A vTPM is not copied (a new one is created if asked).
- **Limits of this version**: a template is used on the host where it lives (no copy between hosts); no versions; no
  progress percentage during the copy; creations and template deletions on the same host run one at a time (they
  share the host creation lock). A template contains everything that was on the disks, secrets included: who can
  create VMs from it gets a copy.
- **Alternatives**: `Export-VM` + `Import-VM -Copy -GenerateNewId` (keeps devices but ties the template to switch
  names and processor features of the source, and import errors are opaque); differencing disks on a shared parent
  (fast and small, but every VM then depends on the template file forever); converting the VM in place (no copy
  time, but the VM is gone).
- Not validated on a real host yet: real-host plan V46-V48.

## ADR-027 VM groups already on a host are imported automatically, once
- **Context**: a host can already be organised in Hyper-V VM groups when it is added: by hand, or because another
  gateway mirrored its groups there (a reinstalled or moved gateway). ADR-017 imported them only on request (host page >
  Hyper-V VM groups > Import), so a new gateway showed every VM as ungrouped and looked as if the folders were lost.
- **Decision**: the first mirroring pass of a host (`VmGroupSyncService.SyncHostAsync`, i.e. after the first inventory
  with Hyper-V access) imports the native groups that are already there, then marks the host
  (`Hosts.VmGroupsAdoptedAt`); it never runs again for that host.
  - Nobody asked for it, so it takes only what can be taken over **without changing Hyper-V**
    (`VmGroupImporter.Adoptable`). Once imported, a group is owned by the mirroring, which removes native groups
    without VMs and re-writes memberships; therefore: a VM collection is taken only if it has VMs and the portal knows
    all of them; a management collection only if every group inside it is such a VM collection, belonging to no other
    management collection; a VM collection inside a management collection is taken with it or not at all. Everything
    else (empty groups such as placeholders of other tools, ambiguous hierarchies) is left untouched, listed as "not
    imported", and can still be imported on request.
  - Access: the importer is the one of the request (ADR-017). Imported groups carry no role assignments and a portal
    group that has assignments is never filled (a suffixed group is created instead), so nobody gains access to a VM.
    The native group is then renamed like its portal group; that rename, and the one for characters that are not valid
    in portal names, are the only changes the automatic import can cause on a host. It never deletes a native group
    and never removes a VM from one.
  - A group naming a VM the inventory has not reported yet waits: attempts every 5 minutes (each starts PowerShell on
    the host) for at most 30 minutes, then it is left to the import on request.
  - Audited as `hosts.vmgroups.import`, actor `system`, `automatic: true`. `Gateway:VmGroups:AutoImport=false`
    switches it off. An import on request also marks the host.
  - Upgrade: the migration marks the hosts where the portal already owns native groups; the others (nothing mirrored
    or imported yet) get the automatic import at the first pass after the upgrade.
- **Alternatives**: leave it on request and make the button more visible: the folders still look lost after adding a
  host. Import everything, like the request does: an unattended step would then delete empty native groups created by
  other tools. Continuous two-way synchronisation: two sources of truth, and conflicts between hosts.
- **Consequences**: groups created directly in Hyper-V after the host was added are imported on request only; the
  portal stays the source of truth afterwards (ADR-017). Hierarchies deeper than two levels were flattened by the
  mirroring that wrote them and come back flattened. Not validated on a real host yet (real-host plan V45).

## ADR-026 Graphical setup for the gateway and the agent
- **Context**: installing with PowerShell commands (parameters to remember, names to repeat on every update, PFX
  import by hand) is not friendly for occasional administrators.
- **Decision**: `installer/HvPortal.Setup` builds two wizard programs, `HvPortal.Gateway.Setup.exe` and
  `HvPortal.Agent.Setup.exe`, each a single file with the release payload embedded (binaries + installer scripts).
  - The wizard is a front end, not a second installer: it runs the same `Install-Gateway.ps1` / `Install-Agent.ps1`
    through `installer\Invoke-Setup.ps1`. Choices are written to a JSON file and splatted to the script, so nothing
    typed in the wizard reaches a command line or is parsed as PowerShell; unknown parameter names are refused; the
    enrollment token travels in the `HVP_ENROLLMENT_TOKEN` environment variable of the child process. Every field is
    also validated by `SetupLogic.cs` (names, ports, thumbprint, firewall addresses, gateway URL, fingerprint, account).
  - The payload is unpacked into `Program Files\HvPortal\Setup\<component>-<guid>` with a protected ACL
    (Administrators and SYSTEM only) and removed at the end: the scripts run elevated, so no other user may change
    them in between; it also avoids running anything from `%TEMP%`.
  - Gateway: on an update the settings are read from the service environment and pre-filled; a .pfx can be imported
    into `LocalMachine\My` (machine key, not exportable) from the wizard; ports in use are reported before installing
    (the ports the running gateway already holds are its own). `Install-Gateway.ps1` repeats the port and certificate
    checks **before** it stops the running service, so a refused update leaves the working gateway up.
  - Agent: the portal shows one line of connection data (`HVPORTAL-SETUP gateway=... ca=... token=... role=...`) that
    the wizard reads from the clipboard; the portal serves the agent setup from `packages\` next to the zip
    (`GET /downloads/agent-setup`, `hosts.manage`, audited) and the guide offers "Guided setup" first. When the pasted
    data were created for the other kind of agent (host / management), the wizard follows them and says so.
  - .NET Framework 4.7.2 / Windows Forms: present on Windows Server 2019 and later, so the setup is one small file
    that starts without installing or unpacking a runtime. Elevation is requested by the program itself (UAC).
- **Alternatives**: MSI with WiX or Inno Setup — external tool chains to license and download, and a second
  installation logic to keep aligned with the scripts; a self-contained .NET 10 single-file wizard — 70 MB larger
  per setup and it unpacks native libraries to `%TEMP%` at start (blocked by some antivirus policies).
- **Consequences**: the scripts stay the single source of truth (and the way for unattended installs:
  `--extract`). A setup that is unsigned or signed with a self-signed certificate triggers SmartScreen until the
  publisher certificate is trusted. The wizard needs a desktop session; on Server Core use the scripts. The wizard
  pages are checked by rendering them (`--render`); the installation itself needs elevation and is in the real-host
  plan (V43–V44).

## ADR-025 Virtual switches, checkpoint apply on a running VM, VM notes (agent protocol 7)
- **Virtual switches**: three typed operations — `ListNetAdapters` (physical adapters and the switch bound to each),
  `NewSwitch` (`NewSwitchRequest`: name, External/Internal/Private, physical adapter and "share with the host" for
  external) and `RemoveSwitch` (by id, refused by the agent while VM adapters are connected). They are jobs on the host
  (`host.network`, serialized with VM creations) and need the new permission `network.manage` (host scope: any type;
  group scope: private networks only, ADR-024). Names of new switches are restricted (letters, digits, space . _ ( ) -)
  and must be unique on the host, because VMs are connected by switch name. Creating or removing an external switch on
  the adapter the agent uses drops its channel for a few seconds: such jobs are reconciled from the host (the switch
  exists / is gone), never reported as success blindly.
- **Apply checkpoint on a running VM**: the agent turns the VM off (`Stop-VM -TurnOff`) and then restores the
  checkpoint; the job result reports the state after the restore. The current state is discarded by an apply anyway,
  and the typed VM name is still required. Agents before protocol 7 keep the old rule (VM off or saved).
- **VM notes instead of a portal description**: the portal-only description of a VM is removed; the Hyper-V notes
  (already synchronized from the host) are the VM's description everywhere (tables, search, tree filter). Editing them
  is a configuration job that needs `inventory.annotate` (not `vm.config.compute`), so VM operators keep annotating.
  The migration keeps every existing description as an entry of the VM history (`vm.description.removed`).
- **Other protocol 7 changes**: `NewDiskSpec.Folder` (set by the gateway only, ADR-024) and
  `VmSummary.MemoryMaximumMb`. `RequiredVersion` returns 7 for the switch operations and for a new disk with a folder
  (an older agent would create the disk in the host default folder).
- **Not validated on real hosts**: real-host test plan V38–V42.

## ADR-024 Limits of VM groups: quotas, hosts, networks and storage ("folders as projects")
- **Context**: a group (folder) is given to a team or customer with role assignments; they must be able to create
  VMs and networks by themselves, but only within limits, like a project of a cloud provider.
- **Decision**: a group can carry a `GroupPolicy` (`LogicalGroups.PolicyJson`):
  - quotas on the totals of the group **and its subgroups**: VMs, vCPU, memory, virtual disk space, private networks;
  - `hosts`: where users whose permissions come from the group may create VMs and networks;
  - `networks`: the switches the group's VMs may use (null = any; empty = only the group's own private networks);
  - `storage`: folders of a host that may contain the group's VMs and that group users may browse.
  Every group above a VM applies: quotas of each ancestor must have room, lists are intersected.
- **Group-scoped creation**: `vm.create`, `network.manage`, `host.files.read` and `host.files.write` can now be held
  on a group. There they work only on the hosts listed by the group's limits (intersection along the chain) and, for
  files, only inside the group's storage paths. The built-in role *Folder administrator* (`folder-admin`) bundles the
  VM permissions with these four. A user who may create VMs in a group places them there without `groups.manage`: the
  group's role assignments then apply to the new VM, which is the purpose of the group (the anti-escalation check of
  ADR-015 still applies to everyone else).
- **Enforcement** (`Quotas/GroupPolicyService`, used by the API and again by the job runner at execution):
  - new VM: allowed network, location inside the storage paths (set by the gateway when none is chosen), quotas;
    with a memory quota a dynamic-memory VM must state its maximum (it is what counts);
  - configuration change: new network connections must be allowed; growth of vCPU, memory and disks must fit; a new
    disk goes to `<VM folder>\Virtual Hard Disks` when storage is restricted (`NewDiskSpec.Folder`, never taken from
    the client);
  - private network: quota; it then belongs to the group (`GroupNetworks`) and only that group's users (or host
    network administrators) may delete it;
  - usage = VMs of the subtree from inventory (memory = startup, or the dynamic maximum when the agent reports it;
    disk = sum of the virtual disk capacities read from the hosts with `ListHostDisks`, remembered per VM) **plus the
    creations already queued** for the subtree. At execution only earlier requests count, so of two competing requests
    the first keeps its place. Unknown disk usage (host unreachable and never read) refuses the operation.
  - Quotas bind everybody, administrators included (they can raise them). Adding existing VMs to a group is not
    blocked by quotas (it is an administrator action); the group then shows as over its limits and cannot grow.
- **Who sets limits**: `groups.manage` **and** `hosts.manage` (the limits decide which host resources group users can
  reach). Audited (`groups.policy.update`, before/after).
- **Alternatives**: quotas per user or per role assignment — harder to reason about with nested groups; a separate
  "tenant" object — duplicates the group tree; soft quotas (warning only) — not what a provider-style limit means.
- **Consequences**: existing behaviour is unchanged for groups without limits. Group users see only the names of
  the VMs they can read on a host (the creation wizard no longer lists every VM name to them). Limits are enforced by
  the portal only: changes made directly in Hyper-V are not prevented, they show up as usage.

## ADR-023 Automatic local copies of the database
- **Context**: SQLite is a single file; a damaged file (disk fault, interrupted write on a non-journaled volume)
  would lose users, roles, groups, hosts and audit unless a sealed backup (ADR-020) had been downloaded recently.
- **Decision**: `Backup/DatabaseBackupService` copies the database with the SQLite online backup API once a day
  (`Gateway:Database:AutoBackupIntervalHours`, default 24) into `<data>\db\backups\hvportal-auto-<UTC>.db`, runs
  `PRAGMA integrity_check` on the copy and keeps it only when the result is `ok`; copies older than 7 days
  (`AutoBackupRetentionDays`) are removed, but only after a newer verified copy exists and never the newest one.
  A failed copy is reported in Settings > Backup and in the log. Settings shows the copies and offers "Copy now"
  (`settings.manage` + re-authentication, audited).
- **Limits (stated in the UI)**: the copies are on the same disk and contain no keys (the key ring and CA are
  elsewhere in the data directory): they protect against database corruption, not against the loss of the server.
  Restoring one is a manual, offline step (stop the service, replace `hvportal.db`, remove `-wal`/`-shm`).

## ADR-022 Management agents: remote hosts reached with stored credentials (agent protocol 6)
- **Context**: some Hyper-V hosts are in a workgroup and the administrators prefer not to install anything on them;
  a management server with the Windows administration tools is available. ADR-002 keeps host credentials off the
  gateway; this is an **optional** second mode, the agent on the host stays the recommended one.
- **Decision**: a *management agent* is the same agent binary installed with `-Management` on a server that needs no
  Hyper-V. It is enrolled like a host agent (one-time token, CA pin, own certificate) but its subject is a
  `ManagementAgent` row, not a host; the gateway returns `Role = Management` at enrollment. Remote hosts
  (`ManagedHost.ConnectionMode = Remote`) are created in the portal with management agent, computer name (DNS or IPv4,
  never loopback or IPv6 literals), user name, password and WinRM transport. One management agent serves many hosts.
  - Credentials: the password is encrypted with the gateway key ring (AES-256-GCM, purpose bound to the host id),
    write-only in the API (never returned, never audited: audit entries record computer, user, transport and
    `passwordChanged`), deleted with the host. Requires `hosts.manage` at global scope and a recent re-authentication.
  - Channel: after `Hello` the gateway sends `TargetsConfiguration` (the agent's remote hosts with decrypted
    credentials) over the mutual-TLS channel, again after every change (create, edit, move, revoke, delete). The
    agent keeps it in memory only (SecureString) and drops it when the channel closes. Every command to a remote host
    is stamped by the gateway with `TargetHostId`; inventories come back as `TargetInventory`, and the gateway discards
    any inventory for a host not assigned to the reporting agent. A host agent rejects commands with a target, a
    management agent rejects commands without one.
  - Transports (from the management server to the host, with the stored account): WMI/DCOM with packet privacy for
    inventory, power, delete, details and console (the same `root\virtualization\v2` code as a local agent); WinRM for
    the typed operations — the fixed launcher `Ops\HvPortal.Remote.ps1` runs `Invoke-Command -FilePath` with the same
    `HvPortal.Ops.ps1` and passes the JSON request as its only argument (no script block is built, the password comes
    on stdin); SMB administrative shares (`\\host\D$`) for the folder browser and the file explorer, accessed under
    `LogonUser(LOGON32_LOGON_NEW_CREDENTIALS)` so the account is used only for outbound access. All folder rules are
    applied to the host-local path and to what the host reports (fixed drives from `Win32_LogicalDisk`). The CPU
    sampler has no local fallback for remote hosts (it would report the management server).
  - Protocol 6 (agent 0.4.0); `RequiredVersion` returns 6 for any command with a target.
- **Alternatives**: credentials entered only on the management server (DPAPI, never on the gateway) — safer, but the
  user wanted portal-managed credentials; one service instance per remote host — simpler routing, but heavier to
  operate; PowerShell Direct / CIM sessions everywhere — would rewrite every operation.
- **Consequences**: the gateway becomes a custodian of host administrator passwords for the hosts that use this mode:
  a compromise of the gateway *and* its key ring exposes them (threat model T13). Hosts must accept WMI/DCOM, WinRM and
  SMB from the management server; in a workgroup a non-built-in local administrator needs
  `LocalAccountTokenFilterPolicy = 1` and WinRM over HTTP needs the host in the management server's `TrustedHosts`.
  Outcome reporting is unchanged: a broken WinRM session is reported as a failure with "outcome unknown" text, never as
  success. Not validated on real hosts yet (real-host test plan V29–V34).

## ADR-021 Gateway also as a Linux container
- The gateway targets `net10.0` (was `net10.0-windows`) and runs either as a Windows service or in a Linux container
  (`docker/`); the agent stays Windows-only. Platform-specific parts are selected at run time:
  key ring wrapping DPAPI vs AES-256-GCM with a key-encryption key (`HVP_KEY_ENCRYPTION_KEY[_FILE]`, generated in the
  volume with a warning if missing), Data Protection keys DPAPI vs sealed with the key ring, private files NTFS ACLs
  vs POSIX 0700/0600, UI certificate from the Windows store (thumbprint) vs PFX/PEM file.
- The key file records its protection (`dpapi` / `kek-aesgcm`); a DPAPI key ring cannot be opened in a container:
  migration uses the portal backup/restore (keys are re-wrapped for the target).
- Image: multi-stage (Node for the SPA, .NET SDK for the gateway and the cross-published win-x64 agent package),
  runtime `aspnet:10.0`, non-root, read-only root file system, no capabilities, `admin health` as HEALTHCHECK.
  Authenticode cannot be checked on Linux: a package built in the image is reported as unsigned.

## ADR-020 Portal backup and restore from Settings
- One file for disaster recovery instead of three procedures (DB copy with the service stopped, key export, CA):
  `KeyRing.Export` seals the key ring plus extra files with the passphrase; the extra files are a manifest, the SQLite
  online-backup snapshot of the database and the CA/server certificate blobs.
- Restore never swaps the database under a running process: the decrypted content is staged (keys re-protected with
  DPAPI, private ACLs) and applied by the next start before the key ring and EF Core open their files; the previous
  state is moved aside, not deleted. Sessions of the restored database are revoked.
- Only holders of every privileged permission, with re-authentication, typed confirmation for the restore; every
  step audited. The passphrase travels in a request header over HTTPS and is never stored or logged.

## ADR-019 Microsoft Entra ID sign-in and user onboarding
- Entra ID (OpenID Connect authorization code flow + PKCE, confidential client, single tenant) implemented directly
  on `Microsoft.IdentityModel.Protocols.OpenIdConnect` (metadata + signing keys) and `JsonWebTokenHandler`
  (signature RS256, issuer of the configured tenant, audience, lifetime, nonce, `tid`). The configuration lives in
  Settings (no restart): tenant, client id, cloud instance (global / US Gov / China only), client secret encrypted
  with the key ring and never returned or logged. Outbound HTTPS to Microsoft only when Entra is enabled.
- Users are **pre-provisioned** (Users > New user > Microsoft, by UPN) and bound to the Entra object id at their first
  sign-in; later sign-ins match the object id only (a recreated account with the same UPN is refused). No password
  is stored for them; local password sign-in is refused. Roles are assigned in the portal as for local users.
- MFA for Entra users is delegated to Entra ID (Conditional Access): the portal does not enroll a local authenticator.
  Sensitive actions re-authenticate them with Entra (`prompt=login`, `max_age=0`) in a popup that marks the session.
- State, nonce and PKCE verifier are kept in a data-protected, 10-minute, `SameSite=Lax` cookie scoped to
  `/api/v1/auth/entra` (Lax is required for the top-level redirect back from Microsoft); the session cookie stays
  `Strict`. "Local sign-in: only emergency accounts" turns the portal into Entra-only with a break-glass exit.
- New local users get a temporary password and, by default, `MfaEnrollmentRequired`: after the forced password
  change they must enroll an authenticator before using the portal, regardless of the global MFA policy. The creation
  dialog also takes the role and its scope (same anti-escalation rules as role assignments). The re-authentication
  dialog states explicitly that it asks for the **administrator's own** password and code.

## ADR-018 Host file explorer: browse, download, upload (agent protocol 5)
- Need: copy ISOs and other files to/from hosts from the browser (Storage > Files), without RDP or SMB shares.
- Typed, in-process agent operations (no PowerShell): `ListFolder`, `ReadFile`, `WriteFileBegin/Chunk/Commit/Abort`.
  Same folder rules as ADR-016 (local fixed drives; hidden, system and reparse-point entries skipped; top-level
  system folders and `$...` refused), enforced by gateway and agent. File names: no separators, streams, wildcards,
  device names, trailing dot/space.
- Transfers are relayed in 4 MB chunks over the agent channel; nothing is stored on the gateway. Downloads stream to
  the browser and are aborted (not truncated) on failure. Uploads require Content-Length, go to a hidden
  `.<name>.<id>.hvpart` file next to the target and are renamed only after size and SHA-256 (computed by the gateway
  while relaying, re-computed by the agent) match. Existing files are replaced only with explicit overwrite (the UI
  asks). Idle uploads are discarded after 10 minutes; at most 4 concurrent uploads per host.
- Reads open files with `FileShare.Read`: a file open for writing elsewhere (the VHDX of a running VM) cannot be
  downloaded, so the portal never hands out an inconsistent copy of an active disk.
- Permissions `host.files.read` (browse + download) and `host.files.write` (upload), scoped Global/Cluster/Host;
  included in the platform and infrastructure administrator roles. Every download and upload is audited with path,
  size and SHA-256. The agent writes with its own identity (virtual service account): folders it cannot write in
  return `access_denied`.

## ADR-017 Portal groups mirrored as native Hyper-V VM groups (agent protocol 4)
- Requirement: groups created in the portal must be visible with `Get-VMGroup` on the hosts. The portal stays the
  source of truth; permissions still come only from portal groups (native groups never grant anything).
- Mapping per host (`VmGroupPlanner`), within the documented Hyper-V model (a ManagementCollectionType contains only
  VMCollectionType groups): a portal group with VMs on the host → VM collection with its name; a portal group with VM
  collections below it → management collection with its name containing its own VM collection and those of **all**
  descendants (levels deeper than two are flattened). Groups with no VM on a host are not created there.
- Ownership: `HostVmGroupLinks` stores the native id of every group the portal created or imported. `SyncVmGroups`
  receives the desired items plus the list of owned ids and only renames, re-members or removes those; anything else
  in Hyper-V (backup tools, manual groups) is left untouched. New groups are created with `New-VMGroup -Id <guid>`.
  Removal empties the group first (`Remove-VMGroup` requires it).
- Triggers: group changes and host changes (inventory, connection) raise a debounced signal; a pass sends only if the
  plan hash changed; every 30 minutes a forced pass corrects drift made directly in Hyper-V. Hosts without Hyper-V
  access are skipped; after a failure automatic passes wait 5 minutes. Changes are audited (`hosts.vmgroups.sync`,
  actor `system`).
- Import (`hosts.manage` + `groups.manage`, audited): native groups not owned yet become portal groups (management
  collection → root group, its VM collections → sub-groups, a VM collection with the same name as its parent gives the
  parent its direct VMs). Existing portal groups are reused only when no role assignment applies to them, otherwise a
  suffixed group is created, so importing never changes who can access a VM.
- Protocol 4 (agent 0.3.2): older agents never receive these commands.
- (0.5.1) The groups a host already has are imported automatically at its first mirroring pass: ADR-027.

## ADR-016 Host folder browser and VM location (agent protocol 3)
- The location and ISO pickers need to see the host's folders. `BrowseFolders` is a read-only operation executed
  in-process by the agent (.NET file-system APIs, no PowerShell): only local fixed drives; hidden, system and
  reparse-point entries (junctions, symbolic links, mount points) are skipped; the top-level system folders of a drive
  (`HostPaths.BlockedRootFolders`: Windows, Users, Program Files, ProgramData, inetpub, PerfLogs, VBRCatalog, ovhupd,
  Packages, ...) and every `$...` folder are neither listed nor accepted. Max 500 entries per folder.
- Folders are listed to users with `vm.create` on the host; `.iso` files only to `hosts.manage`, because choosing an ISO
  outside the library adds its folder to the host's ISO library (audited). Non-administrators still pick only ISOs
  inside the library.
- `NewVmRequest.Location`: validated by the gateway (syntax, blocked folders) and by the agent against the real file
  system (fixed drive, no hidden/system/reparse ancestors). The script creates `<location>\<name>\Virtual Hard Disks`
  and calls `New-VM -Path <location>` (Hyper-V adds `<name>\Virtual Machines`); it refuses a non-empty existing
  `<location>\<name>` and removes only the empty folders and the fixed disk it created if `New-VM` fails.
- Protocol 3: older agents would ignore `location` (creating the VM elsewhere) or fail to parse `BrowseFolders`.
  `AgentProtocol.RequiredVersion(command)` is checked before sending; agents 0.3.1+ answer `Rejected` to unknown
  commands so a future gateway cannot break their channel.
- (Superseded by ADR-017: portal groups are now mirrored as native Hyper-V VM groups.)

## ADR-015 Nested logical groups and access-change guard
- Groups are folders: `LogicalGroup.ParentId`, max depth 16, sibling names unique, cycles refused. Role assignments
  on a group apply to its VMs and to every subgroup (`AccessEvaluator` adds the ancestor chain).
- Because moving a VM or a group changes who can access the VM, membership and tree changes go through
  `GroupTree.CheckAccessChangeAsync`: if the change makes **Allow** assignments newly apply or **Deny** assignments
  stop applying, the actor needs `roles.manage` and must already hold every permission of those roles on each
  affected VM (for a VM being created: on its host). Otherwise `groups.manage` is enough.
- Deleting a group requires it to have no subgroups and no role assignments.