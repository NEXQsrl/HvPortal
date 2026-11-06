# Threat model (v0.4)

## Assets
A1 control of VMs and hosts · A2 portal credentials, sessions, MFA secrets · A3 agent identities and the internal CA ·
A4 key ring · A5 audit trail integrity · A6 inventory confidentiality · A7 availability of management.

## Trust boundaries
B1 browser ↔ gateway (untrusted network) · B2 agent ↔ gateway (untrusted network, possibly different domains) ·
B3 gateway process ↔ its data directory · B4 agent ↔ Hyper-V WMI (local) · B5 OS administrators of gateway/hosts
(**trusted**: they can read keys and control Hyper-V directly; HvPortal cannot defend against them).

## STRIDE

| # | Threat | Mitigation | Residual risk |
|---|---|---|---|
| S1 | Credential stuffing / brute force on login | PBKDF2, blocklist, progressive delay per account+IP and per IP, rate limit, MFA for admins by default, suspicious-activity audit | distributed slow attacks on accounts without MFA → enforce MFA `All` where possible |
| S2 | Stolen session cookie | HttpOnly, Secure, SameSite=Strict, idle/absolute timeout, server-side revocation, re-auth for critical actions | malware in the browser |
| S3 | Rogue agent / impersonation of a host | one-time token + CSR, private CA, DB-bound certificates, revocation | a host administrator can steal that host's identity (trusted boundary B5) |
| S4 | Rogue gateway (MITM) toward agents | CA pinned at enrollment by fingerprint, custom root trust, hostname check | operator skipping fingerprint verification |
| T1 | Tampering with audit rows | HMAC chain verified on demand; external export | DB + key ring holder can rebuild the chain → export to SIEM/WORM storage |
| T2 | Tampering with jobs in transit | TLS; typed commands; agent validation | — |
| T3 | Path traversal / reparse point on data dirs, VHD/ISO paths | data dir refused if any component is a reparse point; private ACLs; **new VHDX only in the host default VHD folder with generated names; ISO only inside the host ISO library (gateway containment check + agent `GetFullPath` + no reparse point on any path component); expand only for disks attached to that VM** | a host administrator can place hostile files in the ISO library (trusted boundary B5) |
| R1 | User denies an action | audit with actor, auth methods, IP, correlation id, outcome | — |
| I1 | Inventory disclosure across tenants/teams | per-VM filtering, 404 for unauthorized, hub messages only to authorized users, ids-only payloads | timing side channels |
| I2 | Secrets in logs | no request body logging, no secrets in audit details, tokens shown once | operator copying tokens into tickets |
| I3 | Setup token theft | file ACL limited to admins/SYSTEM/service, 24 h, single use, rotated on restart | local admin compromise (B5) |
| D1 | Account lockout DoS | no hard lockout; account-wide counter never blocks | per-IP throttling can affect users behind the same NAT |
| D2 | Job flooding | per-VM locks, bounded concurrency, idempotency keys | dedicated per-user quotas (M2) |
| E1 | Horizontal escalation (other VMs) | scope evaluation at request and job time; disks/networks/groups features will validate every referenced resource | — |
| E2 | Vertical escalation via role management | anti-escalation rules (see security model), tests | — |
| E3 | Gateway compromise → host takeover | gateway has no host credentials (except remote hosts, see T13); agent only accepts typed operations; no arbitrary execution endpoint | attacker can still power off/on VMs the agent can manage |
| E4 | Group user escapes the limits of the group (ADR-024) | hosts, networks and storage are checked server-side at request and at execution; the VM location and the folder of a new disk are set by the gateway, never by the client; `existingNames` and switch lists are filtered; group file access is confined to the group's storage paths; only private switches can be created with a group permission | limits are enforced by the portal only (changes made in Hyper-V are not prevented); a group user with VM console access controls the guest, not the host |
| T13 | Theft of remote-host credentials (management agents, ADR-022) | opt-in per host; password AES-256-GCM with the key ring, bound to the host id; write-only API, not audited; only `hosts.manage` (global) + recent re-auth can set it; sent only to the assigned management agent over mutual TLS; agent keeps it in memory (SecureString), never on disk, dropped on disconnect; inventory for unassigned hosts discarded; WinRM without disabled certificate checks | a holder of the gateway database **and** key ring (or of the management agent's memory) obtains host administrator passwords: use a dedicated host account, prefer the agent on the host, rotate the password when a gateway or management server is rebuilt |
| S5 | Rogue management agent reporting other hosts | each inventory accepted only for hosts assigned to the reporting agent; commands stamped with the target by the gateway | a compromised management server controls the hosts assigned to it |
| E4 | Command injection in WMI/PowerShell | WQL built only from GUIDs formatted by the agent; PowerShell only through one fixed script with a fixed command line, JSON on stdin, closed operation switch, typed named parameters, no `Invoke-Expression`/script blocks (unit test enforced), validation at API, job execution and agent; sanitizer for free-text reason | notes are free text passed as a typed `-Notes` value (never evaluated) |
| E6 | Console hijacking (ticket theft, cross-site WebSocket, lingering access) | one-time 30 s ticket bound to user+session+VM, sent in the first message (not in URLs), Origin required, SameSite cookie, permission/session re-check every 60 s, idle/absolute timeouts, input validation, audit open/close | a user who may open the console can type into the VM like at a physical keyboard: grant `vm.console` accordingly |
| E7 | Escalation through group membership/moves | access-change guard (ADR-015): gaining Allow or losing Deny via groups needs `roles.manage` + held permissions; tests | — |
| E5 | CSRF / clickjacking / XSS | anti-forgery token, Origin check, SameSite, CSP without inline scripts (style nonce per response only for component `<style>` elements), frame-ancestors none, React escaping | — |

## Out of scope / accepted
- Compromise of an OS administrator account on gateway or hosts.
- Physical attacks; Hyper-V vulnerabilities; side channels between VMs.
- Availability of the gateway beyond a single instance (SQLite; HA requires M2 server databases).
