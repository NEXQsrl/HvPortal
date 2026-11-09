# Compatibility matrix

"Supported" is claimed only after the [real-host test plan](verification/real-host-test-plan.md) passes on that
platform. Status as of Milestone 1:

| Platform | Role | Status | Evidence |
|---|---|---|---|
| Windows Server 2025 Datacenter (build 26100) | gateway, agent | **Target — validation pending** | no test on real host yet |
| Windows Server 2025 Standard (build 26100) | gateway, agent | **Target — validation pending** | edition differences surfaced as capabilities |
| Windows Server 2025 Datacenter: Azure Edition | agent | untested | SKU detected (407) |
| Windows Server 2022 (build 20348) | gateway, agent | **Under evaluation — not supported** | same WMI v2 surface per docs; not tested |
| Windows Server 2019 and older | — | not planned | — |
| Windows 11 (client Hyper-V) | development only | **not supported for production** | gateway + agent + enrollment + mTLS + inventory access detection tested on build 26200 without Hyper-V rights |
| Server Core | gateway, agent | expected to work (no GUI dependency); untested | — |
| Workgroup hosts | agent | design target; enrollment/mTLS tested in workgroup (dev machine) | — |
| Active Directory domain hosts | agent | design target; no AD dependency in code | untested |
| Failover Cluster nodes (domain and workgroup, with or without S2D) | agent on every node | status, capacity, node maintenance, highly available VMs, moves between nodes (v0.9.0, ADR-041); **not validated on a real cluster** | Install-Agent `-ClusterAccess Full` |

| Component | Version |
|---|---|
| .NET runtime | 10.0 (LTS, support until Nov 2028) — releases are self-contained, no runtime install needed |
| Browsers | current Edge, Chrome, Firefox (UI e2e tested with Edge) |
| Database | SQLite (bundled) — PostgreSQL 16+/SQL Server 2022+: Milestone 2 |

Edition-specific capabilities (from `OperatingSystemSKU`): Datacenter enables S2D, SDN, shielded VMs; S2D status is
read through the cluster page (v0.9.0), SDN and shielded VMs are not managed; Standard shows them as unavailable. Licensing limits (number of VMs) are not enforced by the product.
