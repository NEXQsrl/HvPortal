<p align="center"><b>English</b> | <a href="SECURITY_IT.md">Italiano</a></p>

# Security policy

## Reporting a vulnerability

Please report vulnerabilities **privately**. Do not open a public issue, do not publish details before a fix is
available, and do not test against systems that you do not own or are not authorized to test.

> **To be completed by the maintainers before publication:** the private reporting channel. For example, GitHub
> private vulnerability reporting for this repository (*Security > Report a vulnerability*, which must be enabled in
> the repository settings) and/or a dedicated security contact address with an optional PGP key. No contact address
> has been published yet.

Please include:

- the affected version or commit, and how HvPortal is deployed (Windows service or container; gateway and agent
  versions; hosts in a domain or a workgroup);
- the steps to reproduce it, and the impact you observed or expect;
- whether credentials, tokens, keys or audit data may be exposed or altered.

Please give the maintainers reasonable time to investigate and publish a fix before any public disclosure.

### Scope

**In scope**: the gateway, the agent, the installers and graphical setups, the web interface, the REST API, the
enrollment protocol and the agent channel, the update mechanism of the agent, cryptography and key handling, and the
authorization logic.

**Out of scope**:

- vulnerabilities in Windows, Hyper-V or third-party dependencies (report them upstream, and tell us if HvPortal is
  affected);
- denial of service that needs network-level flooding;
- findings that need local administrator rights on the gateway server or on a host. An administrator of those machines
  is trusted by design: they can read the keys and control Hyper-V directly (see the
  [threat model](docs/threat-model.md)).

## Supported versions

HvPortal is pre-1.0 preview software. Security fixes are made on `main` and shipped in the next release; only the
latest release receives them.

| Version | Security fixes |
|---|---|
| 0.7.x (latest preview) | Yes |
| Older than 0.7.0 | No: update to the latest release |

HvPortal **has not had an independent security review yet**. Do not expose the portal or the agent port to the
Internet. Restrict both ports to the networks that need them (`-UiRemoteAddress`, `-AgentRemoteAddress`, or your own
firewall).

## Security model in brief

The complete description is in [docs/security-model.md](docs/security-model.md) and
[docs/threat-model.md](docs/threat-model.md).

### Least privilege

- The **gateway** runs as the virtual account `NT SERVICE\HvPortalGateway`, which is not an administrator. Its
  binaries are read-only for it, and its data directory is private (SYSTEM, Administrators and the service). In a
  container it runs as a non-root user, with a read-only root file system, no Linux capabilities and
  `no-new-privileges`.
- The **agent** runs as `NT SERVICE\HvPortalAgent`, a member of the local *Hyper-V Administrators* group only; it is not
  a local administrator. If a named account is required, the installer creates a dedicated local user that has only
  *Log on as a service*: local, Remote Desktop, network and batch sign-in are denied, and its random password is known
  to nobody.
- No component needs a domain account or Domain Admin rights.
- The gateway holds **no credentials for the hosts** that have an agent. Credentials exist only for hosts that you
  choose to reach through a management agent, and they are stored encrypted.

### Agent channel

- The agent **opens** the connection: an outbound WebSocket over mutual TLS (TLS 1.2 or 1.3). The hosts need no inbound
  port.
- The gateway runs an **internal CA**. A host enrolls with a one-time token (valid for 15 minutes) and the SHA-256
  fingerprint of that CA, which the agent pins. From then on the agent trusts only that CA for the gateway (the Windows
  trust store is ignored) and checks the gateway's name.
- Agent certificates are valid for 90 days and renewed automatically. Each one is checked against the database at
  every connection and can be revoked from the portal.

### A closed, typed command set

- The gateway can send an agent only commands from a **closed list of typed operations**, with typed arguments. There
  is no command that runs a script, a WMI query or a path supplied by a caller.
- Management operations go through **one fixed PowerShell script**, with the request as JSON on standard input and
  fixed parameter names. The test suite fails if that script contains `Invoke-Expression`, `Start-Process`, `Add-Type`,
  `[ScriptBlock]::Create` or similar constructs.
- Arguments are validated when a change is requested, again when the job runs, and again by the agent. Values that the
  server controls (the Hyper-V id of a VM, library folders, template paths, the VLAN of a network, repository
  credentials) are filled in by the gateway from its own records, never taken from the client.
- File paths are normalized and checked: system folders, `..` and reparse points are refused. With `-StorageFolders`
  the agent itself refuses every folder outside the list chosen on the host; the gateway cannot change that list.

### Authentication and sessions

- Local accounts: PBKDF2-HMAC-SHA256 with 600,000 iterations; passphrases of 15 to 256 characters checked against
  common passwords; progressive delays and rate limits instead of lockouts.
- Two-step verification with TOTP, required by default for anyone holding administrative permissions. It includes
  replay protection and single-use recovery codes.
- Microsoft Entra ID sign-in (authorization code with PKCE) for users created in the portal beforehand.
- Sessions are kept on the server, with an idle timeout and a maximum lifetime. Critical actions need a recent
  re-authentication.
- The web interface uses HTTPS only, a Content Security Policy without inline scripts, anti-forgery tokens, an Origin
  check and `SameSite=Strict` cookies. The VM console opens with a one-time ticket that is bound to the user, the
  session and the VM.

### Authorization

- **Default deny.** Roles are assigned at global, host, group or VM level, and an explicit deny wins over any allow.
- VMs that a user may not see return 404, exactly like VMs that do not exist. Refusals are audited.
- **Anti-escalation**: nobody can assign roles to themselves or grant permissions they do not hold, and moving VMs
  between groups counts as an access change.
- Permissions are checked when a change is requested and again when its job runs. The limits of a group (quotas,
  hosts, networks, storage paths) are enforced at both moments.

### Audit

- Every change and every refused attempt is recorded: who, how they signed in, what, from where, and the outcome.
- Each entry is chained to the previous one with HMAC-SHA256, so changes to stored entries can be detected. The chain
  can be verified (*Audit > Verify integrity*, or `HvPortal.Gateway.exe admin verify-audit`) and exported as JSON Lines.
- The chain is tamper-evident, not immutable: whoever holds both the database and the key ring could rebuild it.
  Export the log regularly to an external, append-only system.

### Secrets

- A key ring of AES-256 keys protects the secrets. On Windows it is protected by DPAPI (machine scope); in a container,
  by a key-encryption key that you supply.
- **Encrypted with the key ring**: TOTP secrets, the private key of the internal CA, the Entra ID client secret, the
  SMTP password, the passwords of backup repositories and of hosts reached through a management agent. These values
  are write-only in the API: they are never returned.
- **Stored in the database only as hashes**: API tokens (SHA-256), MFA recovery codes, and the setup, enrollment and
  account-recovery tokens. Tokens are shown once, when they are created. Until the first setup, the setup token is
  also written to a file that only administrators can read.
- The credentials of a backup repository are decrypted only to be sent to the agent, over the mutual TLS channel, for
  the operation in progress. They are never stored in job parameters and never logged.
- Webhook addresses are stored as entered, because they often contain a secret. The portal shows them back only
  masked.
- No passwords, tokens or credentials are written to logs or audit details. HvPortal has no telemetry.

### API tokens

- API tokens are **read-only** and limited to one scope, `backup.read`. They open only the endpoints under
  `/api/v1/public/` (backup status and recent runs), never the rest of the API.
- Creating one requires `settings.manage` and a recent re-authentication. Tokens can expire and can be revoked. Their
  use is rate-limited, and both creations and refused uses are audited.

### Code signing and agent updates

- Releases can be Authenticode-signed. The project's own builds use a **self-signed** certificate. A self-signed
  signature proves that files have not changed since they were signed by the holder of that certificate. It does not
  make a build trusted, and it gives no SmartScreen or antivirus reputation. Windows reports it as untrusted until you
  import the public certificate (`HvPortal-CodeSigning.cer`). Never treat a build as trustworthy just because it is
  signed: verify the SHA-256 and where you obtained it.
- Agent updates from the portal are installed only when the package's signed catalog (`HvPortal.Agent.cat`) and the
  new executable are signed by **the same certificate as the agent already installed**, every file matches the
  catalog, and the version is not older. Unsigned builds are never installed this way. The check is done by a SYSTEM
  task that the agent may only start, on a copy that the agent cannot modify.

### Known limits

- A compromised gateway can do on the hosts everything the portal can do (for example turn off or delete VMs, or
  upload files into allowed folders). The agent gives it no way to run arbitrary commands on a host.
- Hosts reached through a management agent depend on stored administrator credentials. Whoever holds the gateway's
  database and key ring, or the memory of the management agent, can obtain them. Prefer the agent on the host.
- The limits of a group are enforced by the portal. Changes made directly in Hyper-V are not prevented.
- A template contains whatever was on the disks of its source VM. A user with console access to a VM controls that
  guest as if at its keyboard.

For the complete list of threats, mitigations and residual risks, see [docs/threat-model.md](docs/threat-model.md).
