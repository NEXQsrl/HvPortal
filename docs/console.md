# Browser console

**Status (v0.3.1): basic console implemented** (v0.3.1 fixes the WebSocket handshake for browsers using HTTP/2: the endpoint accepts the extended CONNECT of RFC 8441, which carries no anti-forgery header and is protected by the Origin check and the one-time ticket). It is a *Hyper-V console* (the hypervisor's view of the VM display),
not RDP into the guest: it works when the guest has no network and before the operating system starts (BIOS/UEFI,
boot manager, OS installation, recovery).

## How it works

```
browser (canvas) ◄── WSS, binary frames ──── gateway ◄── mTLS channel ──── agent ── WMI root\virtualization\v2 ── vmms
   keyboard/mouse ─── JSON messages ───────►  ticket, Origin, RBAC  ─────►  typed ConsoleFrame / ConsoleInput commands
```

| Part | Documented API | Notes |
|---|---|---|
| Screen | `Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage(TargetSystem, WidthPixels, HeightPixels)` → RGB565 | sized from `Msvm_VideoHead.CurrentHorizontalResolution/CurrentVerticalResolution`; return code 32775 = VM not running |
| Keyboard | `Msvm_Keyboard.TypeScancodes(uint8[])`, `TypeCtrlAltDel()`, `TypeText(string)` | scan code set 1: physical key positions, the guest keyboard layout decides the character |
| Mouse | `Msvm_SyntheticMouse.SetAbsolutePosition(x, y)`, `SetButtonState(button, isDown)` | only when the synthetic mouse is enabled (`EnabledState = 2`, i.e. guest integration services running) |

- The agent compares each frame with the previous one and sends only changed frames, zlib-compressed. Since 0.9.2,
  when the gateway asks for it (`ConsoleFrameRequest.Partial`), only the band of rows between the first and the last
  changed row is sent (`ConsoleFrame.Top`/`Rows`): typing in a text console or a moving cursor sends a few rows
  instead of the whole screen. An older agent ignores the flag and keeps sending full frames.
- The gateway pumps frames adaptively (one capture every 80 ms at most while the screen changes or the user types,
  every 400 ms on a static screen); since 0.9.2 a key, a mouse action, a refresh or a resize ends the wait at once, so
  what was typed shows up with the next capture. It requests a size that fits the browser viewport and pauses when
  the browser tab is hidden. The real rate is bounded by how long Hyper-V takes to produce a thumbnail (to measure on a
  real host, V85).
- Keyboard and mouse no longer wait for a screen capture in progress on the agent (separate lock, 0.9.2).
- The browser inflates frames with `DecompressionStream('deflate')` and converts RGB565 to RGBA on a canvas, keeping
  the last image and repainting only the rows received.
- Binary frame: `[0]=1, [1]=flags (bit0 mouse), u16 width, u16 height, u16 guest width, u16 guest height, zlib data`.
  Partial frame (only after the browser sent `{"t":"caps","partial":true}`): `[0]=2`, same header, `u16 first row,
  u16 rows`, zlib data of those rows.

## Security controls

| Control | Implementation |
|---|---|
| Authorization | `vm.console` on the VM (inherits from host, groups, global) checked when the ticket is issued, when the socket is redeemed and **every 60 s** while open (`ConsoleRelay.WatchdogAsync`) |
| Ticket | 256-bit random, one-time, 30 s, bound to user **and** portal session **and** VM; only its SHA-256 is kept, in memory |
| Ticket transport | first WebSocket message — never in the URL (URLs reach access logs) |
| Socket | same origin only: `Origin` must be present and equal to the portal origin; session cookie (SameSite=Strict) required |
| Session validity | portal session revoked/expired/idle, user disabled or security stamp changed → console closed at the next check |
| Timeouts | 15 minutes without input, 8 hours maximum |
| Limits | 4 consoles per user, 4 per VM |
| Input validation | scan codes 1–32 bytes (0–255), text ASCII printable ≤ 512 chars, mouse coordinates clamped, buttons 1–3; anything else is dropped, never forwarded |
| Audit | `vm.console.open` (success or denied with reason) and `vm.console.close` with duration and reason |
| Host exposure | nothing new: no port 2179/3389, no host credentials; the agent keeps its outbound-only channel |

## Limits (stated in the UI)

- Low frame rate, no audio, no clipboard sync, no USB/drive redirection, no dynamic resolution.
- "Type text" sends ASCII keystrokes (useful for passwords and commands); the guest layout must match.
- Win, Alt+Tab and Esc are captured only in full screen (Keyboard Lock API, Chromium-based browsers).
- The mouse works only once the guest integration services are running; before that (BIOS/setup) use the keyboard.
- The guest's own cursor is part of the captured image only if the guest draws it in software.
- For a full desktop experience use RDP to the guest (a different thing: it needs guest networking).

## Messages seen in the console of Linux guests

`I/O error, dev fd0, sector 0 op 0x0:(READ)` printed on the console of a Linux VM is written by the guest kernel,
not by HvPortal: a **generation 1** VM always has a virtual floppy controller (it cannot be removed), the Linux
`floppy` driver finds the drive, and every time something in the guest probes the block devices (udev, `blkid`,
a disk being attached, some backup/snapshot agents) the read of the empty drive fails and the kernel prints it on the
console (tty1), which is the screen the console shows. It is harmless. To stop it, in the guest:

```bash
echo "blacklist floppy" | sudo tee /etc/modprobe.d/blacklist-floppy.conf
sudo rmmod floppy            # now; the blacklist keeps it out after a reboot
sudo update-initramfs -u     # Debian/Ubuntu (dracut -f on RHEL-like systems)
```

New Linux VMs are better created as **generation 2** (UEFI, no floppy; use the *Microsoft UEFI Certificate
Authority* Secure Boot template, or turn Secure Boot off, for distributions that need it). Kernel messages on the
console can also be limited with `sudo dmesg -n 3` (only errors more serious than this kind).

## Not implemented / research

- VMConnect protocol (RDP to vmms on TCP 2179 with a pre-connection blob) for higher frame rates and enhanced
  session — see the earlier [feasibility study](console-study.md).
- Recording of console sessions.

## Real-host verification

See [real-host test plan](verification/real-host-test-plan.md), section "Console": Gen1 and Gen2 VM, BIOS/UEFI
screen, Windows logon with Ctrl+Alt+Del, Linux text console, mouse before/after integration services, idle timeout.
