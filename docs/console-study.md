# Browser console — feasibility study (Milestone 4, research)

**Status: study only. No console is implemented in Milestone 1.** The `vm.console` permission exists so roles stay
stable; no endpoint uses it.

## Three different things that must not be confused

| # | Kind | Works before the guest OS boots? | Needs guest network? | Transport |
|---|---|---|---|---|
| 1 | **Hyper-V console (VMConnect basic session)** | yes — BIOS/UEFI, OS installation, boot failures | no | RDP to the **host**, TCP 2179, served by `vmms.exe`, VM selected by a pre-connection blob (VM GUID) |
| 2 | **RDP into the guest** | no | yes | ordinary RDP to the guest's 3389 (or a guest RD Gateway) |
| 3 | **Enhanced session (VMConnect)** | no (needs the guest RDP stack over VMBus) | no (VMBus) | RDP over VMBus; Windows guests, Linux only with xrdp + hv_sock configuration |

An RDP gateway in front of guests (#2) is **not** a Hyper-V console and does not reach VMs without network or
before boot.

## What is known (sources to be re-verified before implementation)

- VMConnect talks to the host's VMMS on TCP 2179 using RDP; the VM is identified by a pre-connection blob carrying the
  VM GUID; VMMS authenticates the caller (Windows authentication) and checks Hyper-V authorization.
  ([gpotter2 – Hyper-V video](https://gpotter2.github.io/blog/hyperv-video))
- FreeRDP implements this as `/vmconnect[:<vmid>]` — "Hyper-V console (use port 2179, disable negotiation)".
  ([xfreerdp man page](https://manpages.debian.org/testing/freerdp-x11/xfreerdp.1),
  [Microsoft Virtual PC Guy blog: local VM management with FreeRDP](https://learn.microsoft.com/en-us/archive/blogs/virtual_pc_guy/local-vm-management-on-hyper-v-server-with-freerdp))
- Apache Guacamole (guacd uses FreeRDP) exposes an RDP security mode "Hyper-V / VMConnect" and a preconnection BLOB
  parameter; users report it working, with caveats (e.g. audio not available over 2179, older FreeRDP builds
  ignoring the blob). ([Guacamole user list 1](https://www.mail-archive.com/user@guacamole.apache.org/msg11335.html),
  [2](https://go.mail-archive.com/user@guacamole.apache.org/msg01018.html),
  [3](https://www.mail-archive.com/user@guacamole.apache.org/msg00928.html))
- Port 2179 gives console access to every VM the authenticated principal may access and must be restricted to
  management networks. ([connected.app – port 2179](https://www.connected.app/ports/2179), secondary source)

Gaps: no official Microsoft protocol documentation for the VMConnect pre-connection exchange was found; behavior must
be validated on WS2025 and WS2022.

## Candidate architecture (to prototype)

```
browser ──WSS (ticket, Origin check)──► gateway ──mTLS channel──► agent ──TCP 2179 (loopback)──► vmms
            renders frames/inputs                    relays a console stream          FreeRDP-based client
```

Options:
1. **guacd on each host** (Apache-2.0, Linux-native; Windows builds are not official) — agent spawns/talks to guacd,
   gateway proxies the Guacamole protocol over the existing channel; browser uses guacamole-common-js (Apache-2.0).
2. **FreeRDP (Apache-2.0) embedded in the agent** (Windows build) producing a framebuffer stream; gateway relays;
   browser renders on canvas. More work, fewer moving parts.
3. Bridge to an existing RDP web client — rejected unless it supports pre-connection blobs to 2179.

Authentication to 2179: the console client must authenticate to the host as a Windows principal authorized for the
VM. The agent's virtual account cannot be used for NLA network logon; options are a dedicated low-privilege local
account per host (stored only on that host, DPAPI) that is granted Hyper-V access, or the agent acting as the client
locally if VMMS accepts its token on loopback. **Credentials never reach the browser.** This must be proven in the
prototype.

## Security requirements (from the project brief)

- Per-VM authorization (`vm.console`) checked when issuing a ticket **and** periodically during the session.
- One-time ticket, ≤ 30 s validity, bound to user, session and VM; WSS only; Origin check; idle and absolute
  timeouts; revocation closes the stream; audit start/stop with duration.
- No host credentials or management ports exposed to the browser; 2179 reachable only on the host loopback.

## Limits to document per guest type

BIOS/UEFI and OS setup (basic session only), keyboard layouts and special keys (Ctrl+Alt+Del injection), clipboard
(enhanced session only, policy-controlled), file transfer (enhanced session only, disabled by default), Linux guests
(basic session works; enhanced requires xrdp + hv_sock), Gen1 vs Gen2 resolution limits.

## Fallback until then

The VM detail page can show connection information for administrators to use the native VMConnect from a management
workstation; the portal does not proxy it. Prototype plan: M4 spike on WS2025 with option 2, then decide.
