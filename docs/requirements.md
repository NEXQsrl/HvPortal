# Requirements and scope

## Feasibility summary

| Topic | Assessment |
|---|---|
| Inventory and VM lifecycle via WMI v2 (`root\virtualization\v2`) | **Feasible**, documented and stable since WS2012; this is what Hyper-V Manager and the Hyper-V PowerShell module use. |
| Least-privilege agent | **Feasible**: built-in group *Hyper-V Administrators* (S-1-5-32-578) grants full Hyper-V management without local admin. Cluster operations (M3) will require additional Failover Cluster permissions. |
| Workgroup hosts | **Feasible**: agent-initiated mTLS with a private CA removes the need for Kerberos/WinRM trust between gateway and hosts. |
| Browser console equivalent to VMConnect | **Research**: VMConnect uses RDP to the host on port 2179 with a VM-specific pre-connection blob; no supported browser implementation exists. See [console-study.md](console-study.md). |
| Native application-consistent backup | **Research / later**: requires VSS + Hyper-V WMI backup APIs (RCT, reference points). v0.7.0 provides full-copy VM backups through the export of a (production or standard) checkpoint, which record the checkpoint type Hyper-V reports and make no application-consistency claim (ADR-031); incremental chains and an external provider interface remain later work. |
| Windows Server 2025 Standard vs Datacenter | **Feasible**: differences (S2D, SDN, shielded VMs, unlimited VMs licensing) are detected from `OperatingSystemSKU` and surfaced as capabilities. |
| Windows Server 2022 | **Under evaluation**: same WMI v2 surface; not declared supported until tested (see compatibility matrix). |

## Functional requirements (by milestone)

### MVP — Milestone 1 (this delivery)
- F1 Gateway web service, Windows service, least-privilege identity.
- F2 Agent service on Hyper-V hosts, typed operations only.
- F3 Secure enrollment: one-time, short-lived token; CA pinning; mTLS; renewable and revocable certificates.
- F4 SQLite database with migrations.
- F5 First-run bootstrap without default credentials; forced definitive password; admins forced to MFA.
- F6 Local accounts: password policy (≥15, ≤256, blocklist, no truncation, no periodic expiry), progressive delays,
  generic errors, recovery procedure, session revocation on critical changes.
- F7 TOTP MFA (local QR, encrypted secret, confirmation, replay protection, drift ±1 step, recovery codes,
  re-authentication for disable/regeneration, policy None/Privileged/All). WebAuthn: data model ready.
- F8 RBAC (minimum viable): built-in roles, custom roles, scopes Global/Host/Group/VM, explicit deny, default deny,
  anti-escalation, re-validation at job execution.
- F9 Audit log separated from diagnostics and operation history; HMAC chain; export; verification.
- F10 Real inventory (hosts, VMs) with staleness, partial data and access errors shown explicitly.
- F11 VM power operations as persistent jobs (start, guest shutdown/restart, turn off, reset, pause, resume, save).
- F12 Web UI: dashboard, inventory, VM detail, hosts, jobs, users/roles, audit, settings; IT/EN; light/dark; responsive.

### Next — Milestones 2–3
- VM configuration (CPU, memory static/dynamic, firmware/boot order/Secure Boot/vTPM, disks, DVD/ISO, NICs).
- Checkpoints (standard/production, fallback policy, apply/delete, merge monitoring) — explicitly *not* backups.
- Portal logical groups UI, tags, bulk operations, start order/dependencies/timeouts with cycle detection; native
  Hyper-V VM groups (`Msvm_VirtualSystemCollection`, `VMCollectionType` / `ManagementCollectionType`) shown separately.
- Networks: external/internal/private switches, vNIC, VLAN; SET/QoS/NAT/SR-IOV as separate capabilities.
- Entra ID (OIDC code + PKCE, BFF), PostgreSQL and SQL Server providers, provider migration procedure.
- Clusters (Failover Clustering WMI `root\MSCluster`), migrations, templates, notifications (email/webhook with SSRF
  protection), backup provider integration.

### Research — Milestone 4
- Browser console (pre-OS console vs guest RDP vs enhanced session), S2D provisioning, native backup engine.

## Non-functional requirements

| ID | Requirement |
|---|---|
| N1 | No component of the web tier runs as LocalSystem or administrator. |
| N2 | Every operation authorized server-side; default deny. |
| N3 | No endpoint able to execute arbitrary commands on hosts. |
| N4 | Secrets encrypted at rest with keys outside the database; keys exportable for disaster recovery. |
| N5 | No secrets in logs; no telemetry; offline operation for local management. |
| N6 | Jobs survive gateway restarts; no exactly-once claim; outcomes verified from host state. |
| N7 | Accessibility: semantic HTML, keyboard navigation, labelled controls, visible focus, contrast in both themes. |
| N8 | UI never shows placeholder data as real data. |
| N9 | Reproducible, verifiable builds; dependency inventory; optional Authenticode signing; checksums. |
