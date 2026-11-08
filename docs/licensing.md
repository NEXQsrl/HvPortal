# Licensing

## License of HvPortal

HvPortal is developed by **NEXQ S.r.l.** ([www.nexq.it](https://www.nexq.it)) and is licensed under the
**[PolyForm Shield License 1.0.0](https://polyformproject.org/licenses/shield/1.0.0)** ([`LICENSE`](../LICENSE)), with
this required notice:

> Required Notice: Copyright NEXQ S.r.l. (https://www.nexq.it)

PolyForm Shield is a standard license of the [PolyForm Project](https://polyformproject.org), written by lawyers and
used unchanged: the text in `LICENSE` is the official 1.0.0 text, not a custom license. HvPortal is **source
available**, not open source in the OSI sense: the source code is public and the software is free for any purpose,
companies included, except providing products that compete with HvPortal. Because of that noncompete limit no license
in GitHub's list (all OSI open-source licenses) could be used: those licenses must allow anyone to sell the software.
GitHub shows the license of the repository as "Other".

The source code is in the repository [github.com/NEXQsrl/HvPortal](https://github.com/NEXQsrl/HvPortal); the setups
of the latest version are on its [Releases page](https://github.com/NEXQsrl/HvPortal/releases/latest).

### What the license allows and forbids

The license allows every use ("any purpose is a permitted purpose") except providing a product that competes with
the software, or with any product NEXQ provides using the software. Products compete also when they are free, and
whatever is marketed as a practical substitute for HvPortal competes.

| Activity | Under PolyForm Shield 1.0.0 |
|---|---|
| Personal use | Allowed, free of charge |
| Use in a company, a public body or another organization, also in production, on its own servers and Hyper-V hosts | Allowed, free of charge |
| Any number of users, Hyper-V hosts, VMs, installations or copies | Allowed: the license sets no limits |
| Reading, studying and modifying the source code, and creating new works based on it | Allowed, for any permitted purpose |
| Distributing copies, original or modified | Allowed, with the license terms (or their URL) and the `Required Notice:` line, and only if the copies are not a competing product |
| Reselling HvPortal, selling copies, or selling modified or renamed versions | Not allowed: they compete with HvPortal |
| Distributing a free fork presented as an alternative to HvPortal | Not allowed: free products compete too |
| Offering HvPortal, or a modified version, as a hosted service to others | Not allowed when it is a substitute for HvPortal or for a NEXQ product based on it |
| Installation, configuration, training, maintenance and support services for a fee | Allowed, unless they compete with a product NEXQ itself provides using HvPortal (see below) |
| Sublicensing or transferring the license | Not allowed: everyone gets the license directly from NEXQ |
| Patents | Patent license included; it ends for whoever claims that the software infringes a patent |
| Violations | A first violation can be cured within 32 days of a written notice; otherwise the licenses end |
| Warranty and liability | None, as far as the law allows |

Rights beyond the license (for example selling HvPortal, or a competing product) are requested from NEXQ through the
contacts on [www.nexq.it](https://www.nexq.it); the license states that NEXQ may grant licenses to anyone else.

### Points to keep in mind and to decide

- **Paid services of third parties and NEXQ's own services.** The noncompete covers also "any product the licensor
  ... provides using the software". If NEXQ sells installation, support or managed services based on HvPortal, the
  same paid services offered by others could be considered competing. NEXQ should decide and publish how it reads this
  (for example a statement that third-party installation and support services are welcome), or give written
  permissions.
- **Line of business.** The license lets NEXQ add a `Licensor Line of Business:` line to keep the noncompete for
  products it stops providing. Not added: to decide.
- **Managed service providers and integration into paid platforms**: allowed when they do not compete with HvPortal or
  with a NEXQ product based on it; where the line is depends on the case. Ask NEXQ when in doubt.
- **Trademarks**: the license grants no rights in the NEXQ name and logos or in the HvPortal name and logo ("these
  terms do not imply any other licenses").
- **Contributions**: the terms under which external contributions are accepted (for example a contributor license
  agreement, so that NEXQ can also license contributed code to others) are not defined yet.
- **Governing law**: the license does not name one.
- It is still advisable that NEXQ's lawyer confirms that PolyForm Shield fits these goals.

### Scope: what the license covers

| Material | License |
|---|---|
| Code, scripts, documentation, images and setups written by NEXQ for HvPortal | PolyForm Shield License 1.0.0 |
| Third-party libraries, fonts and icons distributed with HvPortal (below) | Their own licenses, not restricted by PolyForm Shield |
| Windows components used by HvPortal (Hyper-V module, WMI, .NET Framework 4.7.2) | Microsoft terms; not redistributed |
| Projects only cited in the documentation (for example Apache Guacamole and FreeRDP in [console-study.md](console-study.md)) | Their own licenses; no code of theirs is in the repository |

## Third-party components

Runtime components distributed with the release (checked for v0.8, November 2026). Copyright holders and notices are
in [`NOTICE`](../NOTICE).

| Component | Used in | License |
|---|---|---|
| .NET and ASP.NET Core runtimes (self-contained), EF Core, Microsoft.Data.Sqlite, Microsoft.Extensions.*, System.Management, System.Diagnostics.EventLog, System.Security.Cryptography.ProtectedData, Microsoft.AspNetCore.OpenApi, Microsoft.OpenApi | gateway / agent | MIT (.NET Foundation and Microsoft); the runtime packs carry their own `THIRD-PARTY-NOTICES.TXT` |
| Microsoft.IdentityModel.* 8.16, System.IdentityModel.Tokens.Jwt | gateway (Entra ID sign-in) | MIT |
| Microsoft.Windows.SDK.NET (C#/WinRT projection, included by the Windows-specific .NET target) | gateway / agent | Microsoft Windows SDK license terms ([aka.ms/WinSDKLicenseURL](https://aka.ms/WinSDKLicenseURL)), redistributed as part of .NET applications for Windows |
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

### Compatibility with the PolyForm Shield License

All the licenses above are permissive (the Windows SDK projection follows Microsoft's terms for redistributable
components of .NET applications): they allow the components to be redistributed inside a product distributed
under different terms, as long as their own conditions are kept (copyright and license notices; for Apache-2.0 a copy
of the license and any NOTICE; for OFL-1.1 the font license, and the fonts are not sold by themselves). None of them is
copyleft, so none of them requires HvPortal's own code to be released under their terms, and the PolyForm Shield noncompete does
not apply to them.

### How the release meets the third-party obligations

The minified web bundle drops the license comments of the packages, and the `.woff2` fonts carry no license text, so
the release ships the texts separately. From 0.8.1, `installer/Build-Release.ps1` runs
`installer/New-ThirdPartyNotices.ps1`, which writes `THIRD-PARTY-NOTICES.txt` from what is actually shipped:

- the .NET packages listed in the published `*.deps.json` of the gateway and the agent (runtime packs included, with
  the .NET runtime's own third-party notices), with the license file of each package or its license expression;
- the runtime npm dependencies of the web interface, transitively, with their `LICENSE` and `NOTICE` files (OFL text
  of the Geist fonts included), plus the build-time packages whose code is in the bundle (vendored shadcn/ui sources,
  Tailwind CSS, tw-animate-css);
- an appendix with the standard text of the licenses of packages that ship no license file (MIT, Unlicense, Apache-2.0).

`LICENSE`, `NOTICE` and `THIRD-PARTY-NOTICES.txt` are copied into the release root and into the `gateway` and `agent`
folders, so they are inside both setups, inside the agent package (covered by its signed catalog) and in every
installation folder. Identical texts (for example the notices file repeated in 30 Microsoft.Extensions packages) are
written once.

Still open:

- The portal does not show or link the notices (an "About / licenses" page would make them visible to users).
- An SPDX or CycloneDX SBOM (for example with Microsoft `sbom-tool`) is not produced yet.
- The 0.8.0 setups published before this change do not contain these files.

### Rules for new dependencies

A new dependency needs a license that allows redistribution in HvPortal under the PolyForm Shield License: permissive
licenses (MIT, BSD, ISC, Apache-2.0, OFL for fonts) are fine. Copyleft licenses (GPL, AGPL, LGPL, MPL, EPL), licenses
that forbid commercial use (for example Creative Commons NonCommercial) and licenses without clear terms need a review
by NEXQ before they are added. Record the dependency in this page and in `NOTICE`.
