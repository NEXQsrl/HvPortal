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
  no-cache. React + Vite chosen for maturity; no UI component library (smaller supply chain).

## ADR-011 WMI via System.Management in the agent
- Same documented classes used by Microsoft samples; synchronous calls run on dedicated threads. PowerShell is not
  used in M1; when needed (e.g. cmdlets without WMI equivalents) it will be invoked in-process with parameter binding
  and a fixed allow-list of cmdlets — never string-built scripts.
