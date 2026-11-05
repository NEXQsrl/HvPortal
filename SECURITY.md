# Security policy

## Status

HvPortal is pre-release software (Milestone 1) in a private repository. It has not had an independent security
review and must not be exposed to untrusted networks yet.

## Reporting a vulnerability

While the repository is private, report issues directly to the maintainers through the private channel used by the
project team. Do not open public issues, do not publish details, and do not test against systems you do not own.
When the project becomes public this section will list a dedicated address and a PGP key, and GitHub private
vulnerability reporting will be enabled.

Please include: affected version/commit, configuration, reproduction steps, impact, and whether credentials,
tokens or audit data may be exposed.

## Scope

In scope: gateway, agent, installers, web UI, enrollment protocol, cryptography and key handling, authorization logic.
Out of scope: vulnerabilities in Windows, Hyper-V or third-party dependencies (report upstream; tell us if HvPortal
is affected), denial of service requiring network-level flooding, findings requiring local administrator rights on
the gateway or host (an administrator of those machines is trusted by design — see the threat model).

## Supported versions

Only the latest commit of `main` receives fixes until the first release.

## Security design references

- [docs/security-model.md](docs/security-model.md) — authentication, MFA, sessions, RBAC, keys, recovery
- [docs/threat-model.md](docs/threat-model.md) — assets, trust boundaries, STRIDE analysis, residual risks
