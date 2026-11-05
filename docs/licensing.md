# License proposal

**Proposal: Apache License 2.0.** Not applied yet — the repository stays private and no `LICENSE` file is added
until publication is authorized.

## Why Apache-2.0
- Permissive, widely accepted by enterprises that would deploy an infrastructure tool.
- Explicit **patent grant** and patent-retaliation clause (MIT lacks one) — relevant for a project touching
  virtualization protocols.
- Compatible with every current dependency (all MIT or Apache-2.0) and with likely future ones (FreeRDP, Guacamole
  are Apache-2.0).
- Alternatives considered: MIT (simpler, no patent grant); GPL-3.0/AGPL-3.0 (copyleft would discourage corporate
  adoption and complicate bundling with Windows services); MPL-2.0 (file-level copyleft, a reasonable middle ground if
  the maintainers want modifications to stay open).

## Dependency inventory (runtime, as of M1)

| Component | Used in | License |
|---|---|---|
| .NET runtime, ASP.NET Core, EF Core, SQLite provider, System.Management, Hosting.WindowsServices, ProtectedData, OpenApi | gateway/agent | MIT (.NET Foundation) |
| SQLitePCLRaw + SQLite (e_sqlite3) | gateway | Apache-2.0 / public domain |
| QRCoder | gateway (local QR) | MIT |
| React, React DOM, React Router | web | MIT |
| @microsoft/signalr | web | MIT |

Build/test only: xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), coverlet (MIT), Vite (MIT), Vitest (MIT),
TypeScript (Apache-2.0), playwright-core (Apache-2.0).

The authoritative, complete lists (including transitive packages) are generated at release time in
`artifacts/release/<version>/deps/` by `installer/Build-Release.ps1`. Before publication: run a license scanner over
those inventories, produce an SPDX/CycloneDX SBOM (e.g. Microsoft `sbom-tool`), add `NOTICE` with third-party
attributions, and choose a trademark-safe project name.
