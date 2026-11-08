# Licensing

> **Status: draft.** The text of the NEXQ Community License 1.0 in [`LICENSE`](../LICENSE) is a draft prepared for
> review by a lawyer before the definitive public release. It is a custom license: it is not approved by the Open
> Source Initiative and has not been validated legally.

## License of HvPortal

HvPortal is developed by **NEXQ S.r.l.** ([www.nexq.it](https://www.nexq.it)) and is licensed under the
**NEXQ Community License 1.0** ([`LICENSE`](../LICENSE)).

HvPortal is **source available**, not open source in the OSI sense. The source code is public and the software is
free of charge for everyone, companies included, but the license does not allow selling the software without the
written authorization of NEXQ. That restriction on commercial distribution is the reason it is not an OSI open-source
license.

The source code is in the repository [github.com/NEXQsrl/HvPortal](https://github.com/NEXQsrl/HvPortal); the setups
of the latest version are on its [Releases page](https://github.com/NEXQsrl/HvPortal/releases/latest).

### What the license allows and forbids

| Activity | Allowed? |
|---|---|
| Personal use | Yes, free of charge |
| Use in a company, a public body or another organization, also in production | Yes, free of charge |
| Installation on the organization's own servers and Hyper-V hosts | Yes |
| Use in professional and commercial environments for internal purposes | Yes |
| Any number of users, Hyper-V hosts, VMs, installations or copies | Yes, no limits |
| Reading and studying the source code | Yes |
| Modifying the code for personal or business needs | Yes (modified files must say so when distributed) |
| Redistributing the original or a modified version **free of charge** | Yes, with `LICENSE`, `NOTICE` and all attributions, under the same license |
| Paid installation, configuration, training, maintenance and technical support | Yes, if the software itself is not sold or licensed for a fee |
| Reselling HvPortal or selling copies of it | No, without written authorization of NEXQ |
| Selling modified or renamed versions | No, without written authorization of NEXQ |
| Selling licenses or sublicenses, or licensing it on other terms | No, without written authorization of NEXQ |
| Including HvPortal as a component of a commercial product, appliance or package sold or licensed for a fee | No, without written authorization of NEXQ |
| Removing copyright notices, the license, `NOTICE` or required attributions | No |
| Using the NEXQ name, logos or the HvPortal name and logo to suggest approval, certification or partnership | No, without written authorization of NEXQ |

Commercial rights beyond the license, and the authorizations listed above, are requested from NEXQ through the
contacts on [www.nexq.it](https://www.nexq.it).

### Not decided yet

The license does not address these cases yet. Until NEXQ decides (and the text is updated), ask NEXQ before relying
on an interpretation:

- **Hosted service / SaaS**: offering HvPortal to third parties as a service they access over the network, against a
  fee (the license forbids selling copies or licenses, but does not say whether a hosted service is a sale).
- **Managed service providers**: using HvPortal to manage customers' Hyper-V hosts as part of a paid managed service.
- **Integration into paid services or platforms**: for example a hosting control panel or a paid platform that embeds
  or calls HvPortal, when HvPortal is not sold as a separate product.
- **Hardware with HvPortal pre-installed**: a reseller or integrator that sells servers and installs HvPortal free of
  charge as part of the job (Section 7(e) of the license forbids components of appliances sold for a fee).
- **Paid custom development**: modifications developed for a customer against a fee (Section 8 lists installation,
  configuration, training, maintenance and support only).
- **Renamed or rebranded free redistributions**: whether a modified version distributed free of charge must change its
  name (today it must only not be presented as the original or as approved by NEXQ).
- **Contributions**: under which terms external contributions are accepted (contributor license agreement or
  assignment), so that NEXQ can keep granting commercial authorizations on code written by others.
- **Patents, governing law and jurisdiction, cure period after a violation**: not included in the draft.

### Scope: what the license covers

| Material | License |
|---|---|
| Code, scripts, documentation, images and setups written by NEXQ for HvPortal | NEXQ Community License 1.0 |
| Third-party libraries, fonts and icons distributed with HvPortal (below) | Their own licenses, not restricted by the NEXQ license |
| Windows components used by HvPortal (Hyper-V module, WMI, .NET Framework 4.7.2) | Microsoft terms; not redistributed |
| Projects only cited in the documentation (for example Apache Guacamole and FreeRDP in [console-study.md](console-study.md)) | Their own licenses; no code of theirs is in the repository |

## Third-party components

Runtime components distributed with the release (checked for v0.8, November 2026). Copyright holders and notices are
in [`NOTICE`](../NOTICE).

| Component | Used in | License |
|---|---|---|
| .NET and ASP.NET Core runtimes (self-contained), EF Core, Microsoft.Data.Sqlite, Microsoft.Extensions.*, System.Management, System.Diagnostics.EventLog, System.Security.Cryptography.ProtectedData, Microsoft.AspNetCore.OpenApi, Microsoft.OpenApi | gateway / agent | MIT (.NET Foundation and Microsoft); the runtime packs carry their own `THIRD-PARTY-NOTICES.TXT` |
| Microsoft.IdentityModel.* 8.16, System.IdentityModel.Tokens.Jwt | gateway (Entra ID sign-in) | MIT |
| SQLitePCLRaw 2.1 (bundle, core, provider, lib.e_sqlite3) | gateway | Apache-2.0 (no NOTICE file in the packages) |
| SQLite (e_sqlite3) | gateway | Public domain |
| QRCoder 1.8 | gateway (local QR codes) | MIT |
| React, React DOM, React Router | web | MIT |
| @microsoft/signalr | web | MIT |
| shadcn/ui components (source vendored in `web/src/components/ui`, partly modified), radix-ui, Floating UI, cmdk, react-resizable-panels, get-nonce, cn and the small helpers they use | web | MIT |
| recharts 3.10 with @reduxjs/toolkit, redux, redux-thunk, react-redux, reselect, immer, es-toolkit (with a NOTICE for code derived from Lodash), eventemitter3, decimal.js-light, tiny-invariant, clsx, use-sync-external-store | web | MIT |
| victory-vendor; d3-array, d3-color, d3-format, d3-interpolate, d3-path, d3-scale, d3-shape, d3-time, d3-time-format, d3-timer, internmap; d3-ease | web | MIT AND ISC AND BSD-3-Clause; ISC; BSD-3-Clause |
| lucide-react (icons) | web | ISC (parts derived from Feather, MIT) |
| class-variance-authority | web | Apache-2.0 (no NOTICE file in the package) |
| Tailwind CSS 4, tw-animate-css (compiled into the CSS bundle) | web | MIT |
| Geist / Geist Mono variable fonts (@fontsource-variable) | web (bundled `.woff2` files) | SIL OFL 1.1 |
| Hyper-V PowerShell module, WMI | host (Windows components, not redistributed) | Microsoft (part of Windows Server) |
| .NET Framework 4.7.2 (Windows Forms) | graphical setups (`installer/HvPortal.Setup`); part of Windows, not redistributed; built against `Microsoft.NETFramework.ReferenceAssemblies` (build-time only) | Windows component / MIT (reference assemblies) |

The web dependency tree (147 packages) contains only MIT, ISC, BSD-2-Clause, BSD-3-Clause, 0BSD, Unlicense,
Apache-2.0 and OFL-1.1 licenses; some packages in it (for example `ws`, `node-fetch`, `tough-cookie`) are used only
when the SignalR client runs in Node.js and are not part of the browser bundle.

Build/test only (not distributed): xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), coverlet (MIT), Vite (MIT),
Vitest (MIT), TypeScript (Apache-2.0), playwright-core (Apache-2.0), shadcn CLI (MIT, used only to add components),
@tailwindcss/vite (MIT), Entity Framework Core design-time tools and Roslyn (MIT).

The authoritative, complete lists (including transitive packages and versions) are generated at release time in
`artifacts/release/<version>/deps/` by `installer/Build-Release.ps1`.

### Compatibility with the NEXQ Community License

All the licenses above are permissive: they allow the components to be redistributed inside a product distributed
under different terms, as long as their own conditions are kept (copyright and license notices; for Apache-2.0 a copy
of the license and any NOTICE; for OFL-1.1 the font license, and the fonts are not sold by themselves). None of them is
copyleft, so none of them requires HvPortal's own code to be released under their terms, and the NEXQ restrictions do
not apply to them.

### Obligations not met by the release yet

- The release does **not yet ship the full license texts** of the third-party components: the minified web bundle
  drops the license comments, the `.woff2` fonts travel without the OFL text, and the setups and the agent package do
  not contain `LICENSE` and `NOTICE`. Before the public release, `Build-Release.ps1` should add `LICENSE`, `NOTICE` and
  a generated `THIRD-PARTY-NOTICES.txt` (license texts of the npm and NuGet packages, the OFL text, the .NET runtime
  notices) to the setups, the agent package and the gateway's web root, and the portal could link it from its about
  information.
- An SPDX or CycloneDX SBOM (for example with Microsoft `sbom-tool`) is not produced yet.

### Rules for new dependencies

A new dependency needs a license that allows redistribution in HvPortal under the NEXQ Community License: permissive
licenses (MIT, BSD, ISC, Apache-2.0, OFL for fonts) are fine. Copyleft licenses (GPL, AGPL, LGPL, MPL, EPL), licenses
that forbid commercial use (for example Creative Commons NonCommercial) and licenses without clear terms need a review
by NEXQ before they are added. Record the dependency in this page and in `NOTICE`.
