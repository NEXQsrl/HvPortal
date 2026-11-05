# Threat model (Milestone 1)

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
| T3 | Path traversal / reparse point on data dirs | data dir refused if any component is a reparse point; private ACLs; no user-supplied paths in M1 | future disk/ISO features must reuse the same checks |
| R1 | User denies an action | audit with actor, auth methods, IP, correlation id, outcome | — |
| I1 | Inventory disclosure across tenants/teams | per-VM filtering, 404 for unauthorized, hub messages only to authorized users, ids-only payloads | timing side channels |
| I2 | Secrets in logs | no request body logging, no secrets in audit details, tokens shown once | operator copying tokens into tickets |
| I3 | Setup token theft | file ACL limited to admins/SYSTEM/service, 24 h, single use, rotated on restart | local admin compromise (B5) |
| D1 | Account lockout DoS | no hard lockout; account-wide counter never blocks | per-IP throttling can affect users behind the same NAT |
| D2 | Job flooding | per-VM locks, bounded concurrency, idempotency keys | dedicated per-user quotas (M2) |
| E1 | Horizontal escalation (other VMs) | scope evaluation at request and job time; disks/networks/groups features will validate every referenced resource | — |
| E2 | Vertical escalation via role management | anti-escalation rules (see security model), tests | — |
| E3 | Gateway compromise → host takeover | gateway has no host credentials; agent only accepts typed operations; no arbitrary execution endpoint | attacker can still power off/on VMs the agent can manage |
| E4 | Command injection in WMI/PowerShell | WQL built only from GUIDs formatted by the agent; no PowerShell in M1; sanitizer for free-text reason | — |
| E5 | CSRF / clickjacking / XSS | anti-forgery token, Origin check, SameSite, CSP without inline scripts, frame-ancestors none, React escaping | — |

## Out of scope / accepted
- Compromise of an OS administrator account on gateway or hosts.
- Physical attacks; Hyper-V vulnerabilities; side channels between VMs.
- Availability of the gateway beyond a single instance (SQLite; HA requires M2 server databases).
