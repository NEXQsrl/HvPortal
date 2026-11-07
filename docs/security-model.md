# Security model

## Identities

| Identity | Used by | Privileges |
|---|---|---|
| Portal users (local accounts in the DB, or Microsoft Entra ID accounts pre-provisioned in the portal) | people | only what RBAC grants; independent of Windows accounts |
| `NT SERVICE\HvPortalGateway` | gateway service | non-admin virtual account; read/execute on binaries; full control on its data directory only |
| `NT SERVICE\HvPortalAgent` | agent service | member of *Hyper-V Administrators* (S-1-5-32-578); not a local administrator; no domain account |
| optional local user (`Install-Agent.ps1 -ServiceAccount`) | agent service instead of the virtual account | same group and ACLs as the virtual account (which leaves *Hyper-V Administrators*); random 40-char password unknown to anyone, set via `Win32_Service.Change`; only *Log on as a service*, local/RDP/network/batch logon denied; removed by the uninstaller |
| Agent certificate (CN=hvportal-host-&lt;id&gt;, EKU clientAuth) | agent → gateway | identifies one host; revocable; also authenticates the package download of agent updates (`/agent/v1/package`) |
| Scheduled task `\HvPortal\HvPortal Agent Update` (v0.7.0, ADR-029) | updater on each agent machine | runs as **SYSTEM** the fixed `Program Files\HvPortal\Updater\Update-HvPortalAgent.ps1`; task descriptor: SYSTEM and Administrators full control, the agent service SID(s) read + run only; installs only packages signed by the publisher of the installed agent; absent with `-DisableRemoteUpdate` |
| API token (`hvp_...`, v0.7.0, ADR-032) | monitoring systems | read-only scope `backup.read` on `/api/v1/public/*` only; not bound to a user |
| Computer account of a Hyper-V host (`HOST$`) | Hyper-V and the agent (its virtual account appears on the network as the computer account) on SMB backup repositories without credentials, and toward the destination of a VM move (Kerberos) | expected — **unverified** (real-host plan V66, V67): needs share and NTFS rights on that repository, and the right to manage Hyper-V on a move destination; with stored credentials a share is opened with the repository's user instead |
| Local OS administrator of the gateway server | operator | can read keys/DB and run the admin CLI — trusted by design |

Domain Admin is never required. Workgroup hosts are supported (no Kerberos/WinRM between gateway and hosts).

## First run

- No default credentials. At first start (no users) the gateway writes a 256-bit one-time setup token to
  `<data>\bootstrap\setup-token.txt` (ACL: SYSTEM, Administrators, service). It is valid 24 h, single use, replaced
  at every restart until setup completes, never logged and never shown by the UI.
- Setup creates the first *platform administrator* with a definitive password. The resulting session is restricted
  to MFA enrollment (policy default: MFA required for privileged users) — no management is possible before.

## Passwords

- PBKDF2-HMAC-SHA256, 600 000 iterations, 16-byte salt, versioned format, rehash on login when parameters increase.
- NFKC normalization; length counted in Unicode text elements; **minimum 15 (configurable upwards), maximum 256**,
  never truncated (longer input is rejected with an explicit message).
- Rejected: embedded common-password list (exact, after stripping trailing digits/symbols, and repeated words),
  optional local blocklist (plain or SHA-1 hex per line, e.g. an offline breach corpus — set
  `Gateway:Security:BlocklistPath`; loaded once and cached), passwords containing the user name, repetitions,
  sequences.
- No composition rules, no periodic expiry. Change is required only after an administrator reset or recovery.

## Sign-in protections

- Generic error `invalid_credentials` for unknown user, wrong password and disabled account; equalized hashing time.
- **Progressive delay, no hard lockout**: per (account, IP) after 3 failures (2^(n-3) s, max 15 min), per IP after 20
  failures (any account), plus a fixed-window rate limit (30/min/IP) on auth endpoints. The account-wide counter only
  raises `auth.suspicious` audit events (10, 50, 200, 1000 failures) — it never blocks, so a remote attacker cannot
  lock a known user out.
- MFA challenge: random, 5 min, max 5 attempts, single use; failures also feed the throttle.

## MFA (TOTP)

- RFC 6238 (HMAC-SHA1, 6 digits, 30 s), the profile supported by all common authenticator apps; verified against the
  RFC test vectors.
- Enrollment requires recent re-authentication; QR code generated **locally** (QRCoder) and delivered as a data URI;
  the secret is shown only during enrollment and never retrievable afterwards.
- Secret stored **encrypted** (AES-256-GCM, key ring, AAD "totp") — not hashed, because verification needs it.
- Activation only after the first valid code. Drift tolerance ±1 step. **Replay protection**: the highest accepted
  time step is stored; codes at or below it are rejected; concurrent use of the same code is resolved by optimistic
  concurrency (one wins).
- 10 recovery codes (60 bits each), shown once, stored as keyed HMAC-SHA256, single use (atomic update).
- Disabling MFA or regenerating codes requires re-authentication (password + current TOTP). Disabling is refused when
  policy requires MFA for the account.
- Policy: `None`, `Privileged` (default: anyone holding hosts/users/roles/settings management), `All`.
- WebAuthn/passkeys: planned; sessions already record authentication methods (`amr`) to support it.

## Sessions

- Server-side session rows; cookie carries only ids. Idle timeout (default 30 min) and absolute lifetime (12 h).
- Revocation: logout, password change (all sessions), admin password/MFA reset, user disable, MFA enable/disable,
  security-stamp change. Users can list and revoke their own sessions.
- **Re-authentication window** (5 min) for critical actions: user/role/assignment changes, security settings, host
  creation/revocation/token issuance, MFA changes, and in v0.7.0: creation of API tokens, creating or changing a backup
  repository (credentials), notification settings, agent updates from the portal and a *Replace* restore. Other
  restores and the deletion of restore points rely on the permission, typed confirmations where noted, and audit.

## RBAC

- Permissions (see `Permissions.cs`): `inventory.read`, `vm.power`, `vm.power.force`, `vm.config.*`,
  `vm.checkpoint.*`, `vm.delete`, `vm.console`, `vm.migrate`, `vm.create`, `templates.manage`, `network.manage`,
  `host.files.*`, `backup.run`, `backup.restore`, `backup.manage`, `groups.manage`, `jobs.read.all`, `jobs.cancel.all`,
  `hosts.manage`, `users.manage`, `roles.manage`, `settings.manage`, `audit.read`.
- Built-in roles: platform administrator, infrastructure administrator, folder administrator, VM operator, backup
  operator, auditor, read only. Custom roles supported. Since v0.7.0 the *Backup operator* holds `inventory.read`,
  `backup.run`, `backup.restore`, `backup.manage`, `jobs.read.all` **and `vm.create`** (a restore as a new VM creates a
  VM): assigned at Global scope it may create VMs on every host. Where that matters use a custom role without
  `vm.create` (restores as a new VM are then refused) — on a narrower scope `backup.manage` and the backup area, which
  need Global, are not available.
- Scopes: Global, Host, Logical group, VM (Cluster, Network, Backup repository, Template are modeled for later).
- **Evaluation**: default deny; effective scopes of a VM = VM + its host + each logical group containing it **and
  that group's ancestors** + Global;
  any applicable **Deny wins** over any Allow regardless of level; platform permissions only from Global assignments;
  disabled users have no permissions. Evaluated fresh per request and **again at job execution**.
- Unauthorized VMs return **404** (indistinguishable from nonexistent); denied actions return 403 and are audited.
- **Anti-escalation**: no self-assignment; granting a role (or removing a deny) requires holding all of its
  permissions on that scope; custom-role permissions limited to those the editor holds globally; managing a
  privileged user requires all privileged permissions; the last platform administrator cannot be removed/disabled.
- **Group changes are access changes** (ADR-015): adding/removing VMs or moving a group requires `roles.manage`
  plus all involved permissions whenever Allow assignments would newly apply or Deny assignments stop applying.
- **Operations are re-validated at execution**: configuration/creation/checkpoint jobs re-check every permission
  the change needs (`vm.config.compute`/`storage`/`network`), re-stamp server-controlled arguments (Hyper-V VM
  id, ISO library roots) and validate again; the agent validates once more before running the fixed script (ADR-012).
- **Console** (`vm.console`): one-time 30 s tickets bound to user, session and VM, sent in the first WebSocket
  message; Origin required; permission and session re-checked every 60 s; idle 15 min, max 8 h; audited
  ([console.md](console.md)).
- Native Hyper-V VM groups are inventory objects and never grant portal permissions.
- **Templates** (ADR-028): `templates.manage` (host scope) creates, renames and deletes templates; `vm.create` uses
  them. Where a template lives (library folder, its own folder, its disk files) is decided by the gateway from its
  records and re-stamped when the job runs; a client never supplies a path. A template is a full copy of the disks
  of its source VM, so whoever may create VMs from it obtains what those disks contain.
- **Limits of groups** (ADR-024): `vm.create`, `network.manage` and `host.files.*` held on a group work only on the
  hosts, networks and storage paths that the group's limits open, and within its quotas (VMs, vCPU, memory, disk,
  private networks; totals include subgroups and queued creations). Checked when the operation is requested and
  again when the job runs; refusals are audited. Limits are set with `groups.manage` + `hosts.manage`. Quotas bind
  administrators too. File paths of a group user are checked by prefix against the group's storage paths after the
  usual normalization (no `..`, no links: the agent refuses reparse points).
- **Deleting a user** (`users.manage` + re-auth, typed name): sessions, MFA secrets, recovery codes, pending tokens and
  role assignments are removed; the audit log keeps the past actions; queued jobs of the user fail with
  `authorization_revoked`. Not allowed on yourself, on a more privileged user, on the last platform administrator.
- **Backups** (ADR-031): the backup area (repositories, jobs, runs, overview) needs a backup permission at Global
  scope; `backup.manage` is Global only. A restore point is evaluated on its VM while the VM exists (unreadable VM =
  404) and on Global once the VM is gone, so restore points of deleted VMs are reachable only by Global holders.
  `backup.restore` is enough for every restore mode: *Replace* (typed VM name + re-authentication) turns the VM off and removes it from
  Hyper-V before importing the point, without `vm.delete` or `vm.config.*`; a restore as a new VM also needs
  `vm.create` on the target host; a single disk needs `vm.config.storage` on the VM it is attached to, or
  `host.files.write` on the host. Scheduled runs and retention act as the user who last saved the backup job and are
  re-authorized per VM at execution. File restore: only the user who attached a disk can browse it, `backup.restore`
  is re-checked on every listing and download, every download is audited with its SHA-256; attaching the disk on a host
  other than the VM's also needs `host.files.write` on that host. Points found by a repository rescan are attached to no
  VM (their manifest is not signed): only Global holders see and restore them, and only as a new VM.
- **VM move** (ADR-035): `vm.migrate` on the VM plus `vm.create` on the destination host, both re-checked at execution,
  as are the limits of the VM's groups; the destination host name comes from the portal's records. A move between hosts changes which host-scoped role
  assignments (Allow and Deny) apply to the VM; it is not treated as an access change of ADR-015.
- **Portal networks** (ADR-030): `network.manage` on the host; switch and VLAN of a chosen network are stamped by the
  gateway. Limits of groups restrict switches, not VLANs.
- **Allowed storage folders** (ADR-034): decided on the host (`Install-Agent.ps1 -StorageFolders`) and enforced by the
  agent against the real file system, so a compromised or misconfigured gateway cannot widen them; fail closed when the
  file is invalid. For remote hosts the list is set in the portal (`hosts.manage` global) and enforced by the management
  agent (a management agent older than 0.7.0 would ignore it, so the portal refuses to set it). Where the script falls
  back to a default folder of the host, the agent passes the list with the request and the script refuses a default
  folder outside it.
- **Agent updates** (ADR-029): `hosts.manage` at Global scope and a recent re-authentication, the permission re-checked
  when the job runs.

## Web protections

HTTPS only (TLS 1.2/1.3), HSTS in production, CSP `default-src 'self'`, `script-src 'self'` (no inline scripts, no eval),
`style-src 'self' 'nonce-…'` with a fresh nonce per response (ADR-014), `frame-ancestors 'none'`, `X-Content-Type-Options`, `Referrer-Policy: no-referrer`, COOP/CORP, `Cache-Control: no-store` on the API,
anti-forgery header token + Origin check + SameSite=Strict cookies, request body limit 1 MB, strict input validation
(regex allow-lists for names, GUID route constraints, enum parsing), parameterized EF Core queries, no server header.

## Agent channel

mTLS with private CA (chain + EKU) **and** database check of the certificate; the agent validates the gateway
certificate against the pinned CA only (Windows trust store ignored) and rejects name mismatches. Messages limited to
4 MB; commands are a closed enum; VM ids are GUIDs formatted by the agent before being placed in WQL; shutdown reasons
are sanitized. The agent never receives scripts, paths or WQL from the gateway.

**Management agents (ADR-022, opt-in per host).** A management agent is enrolled like a host agent but its certificate
subject is a management agent record; it receives the credentials of the remote hosts assigned to it
(`TargetsConfiguration`) over the same mutual-TLS channel, keeps them in memory only and forgets them when the channel
closes or the host is revoked/deleted/moved. The gateway stamps every command with the target host and accepts
inventory only for hosts assigned to the reporting agent. Toward the host the agent uses WMI/DCOM with packet privacy,
WinRM (`Invoke-Command -FilePath` of the fixed operations script; HTTP 5985 is encrypted by Negotiate/NTLM, HTTPS 5986
uses normal certificate validation — no skip options) and SMB administrative shares with `LOGON32_LOGON_NEW_CREDENTIALS`.
The management server needs no local Hyper-V rights (the service account is not added to *Hyper-V Administrators*).

**Agent self-update (ADR-029, v0.7.0).** The gateway can only *offer* its agent package (version, size, SHA-256). The
agent downloads it over the agent port with its client certificate, checks size and SHA-256 and starts a SYSTEM
scheduled task it may run but not change. The task copies the package to a private folder and installs it only when
the installed `HvPortal.Agent.exe` is signed, the package's catalog `HvPortal.Agent.cat` and its new
`HvPortal.Agent.exe` are signed by the same certificate thumbprint, `Test-FileCatalog` reports every file unchanged,
and the version is not older. A compromised gateway or agent service can therefore trigger a reinstall of a version
signed by the publisher, not install its own code. A self-signed publisher is pinned by thumbprint (its untrusted-root
status is accepted); that does not make the build trusted by Windows, and the private signing key becomes a key to
every agent (keep it off the gateway and the hosts). Unsigned builds are never installed this way.

**Backup repository credentials (ADR-031).** Stored encrypted; decrypted by the gateway only when a job runs or a disk
is mounted, sent inside that one command over the agent channel (never in job parameters, never in the audit), used by
the fixed script as a process-scoped PowerShell drive and dropped at the end of the operation. For a remote host the
command, password included, travels to the host through the existing WinRM session (ADR-022). The scripts build the
`SecureString` character by character instead of `ConvertTo-SecureString -AsPlainText`, so the password is not a cmdlet
parameter that PowerShell **module logging** would record; for remote hosts the whole request (with the password) is
the `-ArgumentList` of `Invoke-Command` on the management server, which module logging of `Microsoft.PowerShell.Core`
could record: check it (real-host plan V58) and keep module logging off for that module there, or use local repositories
or credential-less shares for remote hosts.

**Notifications (ADR-033).** Outbound only when configured: SMTP to the configured server (STARTTLS, or none —
credentials are refused without TLS) and HTTPS POSTs to the configured webhooks. Webhook addresses are sealed with the
key ring; no redirects; loopback, link-local (cloud metadata), unspecified and multicast destinations are refused, also
after DNS resolution; private addresses are allowed (on-premises receivers), so a `settings.manage` holder decides which
internal HTTPS address receives the fixed JSON body. Messages contain job names, VM names and error texts.

**Public API with tokens (ADR-032).** `/api/v1/public/*` accepts only `Authorization: Bearer hvp_...` tokens with the
`backup.read` scope (GET, rate-limited 120/min per address); it returns names and statuses, no paths, hosts or
credentials, not filtered by RBAC. A token works only while its creator exists and is enabled; refused uses are audited
at most once per token prefix and minute.

## Keys and secrets

| Material | Storage | Protection | Recovery |
|---|---|---|---|
| Key ring (AES-256 keys) | `keys\master.json` | DPAPI LocalMachine + ACL | `admin export-keys` (PBKDF2 600k + AES-GCM, passphrase ≥ 20) |
| Internal CA + server cert | `pki\ca.bin`, `pki\server.bin` | encrypted with key ring + ACL | included in the key export |
| TOTP secrets | DB | AES-GCM via key ring | needs key ring |
| Remote host passwords (management agents only) | DB (`Hosts.RemotePasswordEncrypted`) | AES-GCM via key ring, AAD bound to the host id; write-only API; never audited | needs key ring; otherwise re-enter in the portal |
| Backup repository passwords (shares outside the domain, v0.7.0) | DB (`BackupRepositories.PasswordEncrypted`) | AES-GCM via key ring, purpose bound to the repository id; write-only API; never audited | needs key ring; otherwise re-enter in Backup > Repositories |
| SMTP password (v0.7.0) | DB (`Settings` row `notifications`, `passwordSealed`) | AES-GCM via key ring (purpose `notification-smtp-password`); write-only API | needs key ring; otherwise re-enter |
| Webhook addresses (often carrying a secret token) | DB (`Settings` row `notifications`) | **not encrypted**; returned masked by the API | re-enter |
| API tokens (v0.7.0) | DB (`ApiTokens.TokenHash`) | SHA-256 only, plus the first 8 characters | not recoverable: create a new one |
| Recovery codes, tokens | DB | HMAC / SHA-256 only | not recoverable (by design) |
| ASP.NET Data Protection keys | `keys\dataprotection` | DPAPI LocalMachine | not needed (sessions re-established) |
| Agent private key | `<agent data>\identity.bin` | DPAPI LocalMachine + ACL | re-enroll |
| Code-signing private key (release builds) | build machine, `CurrentUser\My` | never exported or committed | lose it = sign with a new certificate and update every agent by hand once (ADR-029) |

## Account recovery

1. Another administrator resets the password and/or MFA (re-authentication required, sessions revoked).
2. If no administrator can sign in: a local administrator of the gateway server runs
   `HvPortal.Gateway.exe admin recover-account --user <name> [--reset-mfa]` → one-time token (30 min, audited with the
   OS identity) → *Recover account* on the sign-in page.

## Break-glass account (prepared for M2)

`Users.IsBreakGlass` marks an emergency account. When Entra is configured and local sign-in is set to
`break-glass-only`, only such accounts can use local credentials; every use is audited with `breakGlass=true` and
MFA is always required for them. Procedure: sealed credentials, two-person retrieval, alert on use, rotation after use.
