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
  immediate (socket closed + DB check on connect + watchdog).

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
- Portal groups stay logical (ADR-015). They are deliberately **not** created as native Hyper-V VM groups
  (`New-VMGroup`), which are per host and carry no permissions; native groups will be shown separately (roadmap).

## ADR-015 Nested logical groups and access-change guard
- Groups are folders: `LogicalGroup.ParentId`, max depth 16, sibling names unique, cycles refused. Role assignments
  on a group apply to its VMs and to every subgroup (`AccessEvaluator` adds the ancestor chain).
- Because moving a VM or a group changes who can access the VM, membership and tree changes go through
  `GroupTree.CheckAccessChangeAsync`: if the change makes **Allow** assignments newly apply or **Deny** assignments
  stop applying, the actor needs `roles.manage` and must already hold every permission of those roles on each
  affected VM (for a VM being created: on its host). Otherwise `groups.manage` is enough.
- Deleting a group requires it to have no subgroups and no role assignments.