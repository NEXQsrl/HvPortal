# License proposal

**Proposal: Apache License 2.0.** Not applied yet — the repository stays private and no `LICENSE` file is added
until publication is authorized.

## Why Apache-2.0
- Permissive, widely accepted by enterprises that would deploy an infrastructure tool.
- Explicit **patent grant** and patent-retaliation clause (MIT lacks one) — relevant for a project touching
  virtualization protocols.
- Compatible with every current dependency (MIT, ISC, Apache-2.0; fonts under SIL OFL 1.1) and with likely future
  ones (FreeRDP, Guacamole are Apache-2.0).
- Alternatives considered: MIT (simpler, no patent grant); GPL-3.0/AGPL-3.0 (copyleft would discourage corporate
  adoption and complicate bundling with Windows services); MPL-2.0 (file-level copyleft, a reasonable middle ground if
  the maintainers want modifications to stay open).

## Dependency inventory (runtime, as of v0.3.0)

| Component | Used in | License |
|---|---|---|
| .NET runtime, ASP.NET Core, EF Core, SQLite provider, System.Management, Hosting.WindowsServices, ProtectedData, OpenApi | gateway/agent | MIT (.NET Foundation) |
| SQLitePCLRaw + SQLite (e_sqlite3) | gateway | Apache-2.0 / public domain |
| QRCoder | gateway (local QR) | MIT |
| .NET Framework 4.7.2 (Windows Forms) | graphical setups (`installer/HvPortal.Setup`) — part of Windows, not redistributed; built against `Microsoft.NETFramework.ReferenceAssemblies` (build-time only) | Windows component / MIT (reference assemblies) |
| Microsoft.IdentityModel.Protocols.OpenIdConnect 8.16.0 (+ JsonWebTokens, Tokens, Logging, Abstractions) | gateway (Entra ID sign-in: metadata, keys, id_token validation) | MIT |
| React, React DOM, React Router | web | MIT |
| @microsoft/signalr | web | MIT |
| shadcn/ui components (source vendored in `web/src/components/ui`), radix-ui 1.8, cmdk, react-resizable-panels, get-nonce, cn | web | MIT |
| lucide-react (icons) | web | ISC |
| recharts 3.10 (charts of the VM resource use tab, ADR-039) and its runtime dependencies: @reduxjs/toolkit, redux, redux-thunk, react-redux, reselect, immer, es-toolkit, eventemitter3, decimal.js-light, tiny-invariant, clsx, use-sync-external-store (MIT); victory-vendor (MIT AND ISC AND BSD-3-Clause), d3-array/color/format/interpolate/path/scale/shape/time/time-format/timer and internmap (ISC), d3-ease (BSD-3-Clause) | web | MIT / ISC / BSD-3-Clause |
| class-variance-authority | web | Apache-2.0 |
| Tailwind CSS 4, tw-animate-css (compiled into the CSS bundle) | web | MIT |
| Geist / Geist Mono variable fonts (@fontsource-variable) | web (bundled font files) | SIL OFL 1.1 — keep the font license notice in `NOTICE` |
| Hyper-V PowerShell module | host (Windows component, not redistributed) | Microsoft (part of Windows Server) |

Build/test only: xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), coverlet (MIT), Vite (MIT), Vitest (MIT),
TypeScript (Apache-2.0), playwright-core (Apache-2.0), shadcn CLI (MIT, used only to add components), @tailwindcss/vite (MIT).

The authoritative, complete lists (including transitive packages) are generated at release time in
`artifacts/release/<version>/deps/` by `installer/Build-Release.ps1`. Before publication: run a license scanner over
those inventories, produce an SPDX/CycloneDX SBOM (e.g. Microsoft `sbom-tool`), add `NOTICE` with third-party
attributions, and choose a trademark-safe project name.
