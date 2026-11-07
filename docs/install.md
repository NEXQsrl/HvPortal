# Installation guide

## Docker (Linux container, e.g. Portainer)

The gateway can also run as a container: see [docker/README.md](../docker/README.md) (`docker/docker-compose.yml`,
`docker/.env.example`). The Hyper-V hosts keep the Windows agent. The agent package served by a container gateway is
unsigned and has no signed catalog, so its agents cannot be updated from the portal (section 4d): update them by hand.

## Topologies

| Topology | Gateway | Agent | Notes |
|---|---|---|---|
| Dedicated management server or VM (recommended) | yes | on each Hyper-V host | hosts only need outbound TCP 8444 to the gateway |
| Gateway on a Hyper-V host | yes | same host + others | two services, two identities; agent connects over loopback mTLS |
| Single host lab | yes | same host | as above |

Domain membership is not required for any component. Hosts in different domains/workgroups are supported.

## 1. Build the release

```powershell
.\installer\Build-Release.ps1                       # tests, SPA, self-contained gateway/agent, checksums
.\installer\Build-Release.ps1 -SigningCertificateThumbprint <thumbprint> -TimestampServer http://timestamp.example
```
Output: `artifacts\release\<version>\{gateway,agent,installer,deps}\` and `SHA256SUMS.txt`. Copy the folders to the
target servers through your normal software distribution channel and verify the checksums
(`Get-FileHash -Algorithm SHA256`). Without a code-signing certificate the binaries are unsigned.

**Self-signed signature (no CA certificate available).** `New-CodeSigningCertificate.ps1` creates a code-signing
certificate (default publisher *NEXQ S.r.l.*, RSA 3072, 5 years, `CA=false`) in `CurrentUser\My` and exports only the
public `.cer`; pass its thumbprint to `Build-Release.ps1`. The release signs `HvPortal.*.exe/.dll` and every `.ps1`
and ships the public certificate as `installer\HvPortal-CodeSigning.cer` (also inside the agent package); the portal
shows the package as *Signed (self-signed)*.
```powershell
.\installer\New-CodeSigningCertificate.ps1                   # prints the thumbprint
.\installer\Build-Release.ps1 -SigningCertificateThumbprint <thumbprint>
# on each gateway/host/management server (elevated), or with a GPO:
Import-Certificate -FilePath .\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath .\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```
With a signing certificate the agent package also gets a signed catalog `HvPortal.Agent.cat` covering every file
(`New-AgentPackage.ps1 -SigningCertificateThumbprint`, called by `Build-Release.ps1`): agents 0.7.0+ accept updates
from the portal only for packages with that catalog, signed by the same certificate as the agent already installed
(section 4d). An unsigned release can only be installed and updated by hand.

A self-signed signature is **not** a reputation: SmartScreen and antivirus products do not trust it by themselves.
Once the certificate is imported, Authenticode reports the files as valid, PowerShell `AllSigned` policies accept the
scripts, and AppLocker/WDAC or antivirus allow rules can be written *by publisher* instead of by path or hash. Without
`-TimestampServer` the signatures stop validating when the certificate expires (re-sign with a new certificate).
Keep the private key on the build machine only; whoever holds it can sign code that those machines will trust.

## 1b. Graphical setup (one file per component)

`Build-Release.ps1` also produces two setup programs in `artifacts\release\<version>\setup\` (ADR-026):

| File | Use |
|---|---|
| `HvPortal.Gateway.Setup.exe` | installs or updates the gateway. Pages: names and ports, portal certificate (internal, one from the computer store, or **import a .pfx**), Windows Firewall. On an update the current settings are read from the service and pre-filled. At the end it shows the portal address and, on a new installation, the setup token. |
| `HvPortal.Agent.Setup.exe` | installs or updates the agent. Pages: Hyper-V host or management server, connection data (**Paste from the portal**: Add host > Guided setup), service account (automatic or dedicated local user), **storage and updates** (v0.7.0: every local drive / only the folders typed one per line / on an update keep the current list; "allow updates from the portal", ticked by default — same as `-StorageFolders` and `-DisableRemoteUpdate`, section 4c–4d). On an update the connection to the gateway is kept. Also downloadable from the portal (Add host / New management agent). |

Double-click the file and accept the administrator prompt. Each setup is a single file that needs nothing else
(.NET Framework 4.7.2, part of Windows Server 2019 and later) and carries the same signed binaries and scripts of the
release; it unpacks them into a folder only administrators can write (`Program Files\HvPortal\Setup\...`), runs the
installer script of sections 2 and 4 with your choices and shows its output. The log is saved in
`C:\ProgramData\HvPortal\SetupLogs`. The wizard is in Italian or English (Windows display language).

A setup signed with a self-signed certificate (or unsigned) makes Windows show "Windows protected your PC" until the
publisher certificate is trusted: import `HvPortal-CodeSigning.cer` as described above, or choose *More info > Run
anyway* after checking the SHA-256 (`SHA256SUMS.txt`, or the value shown by the portal for the agent setup).

For unattended installations keep using the scripts below; they are also inside each setup:
`HvPortal.Gateway.Setup.exe --extract C:\HvPortal\files` unpacks `gateway\` (or `agent\`) and `installer\`.

## 2. Install the gateway (elevated PowerShell on the management server)

```powershell
cd <release>\installer
.\Install-Gateway.ps1 -Source ..\gateway -PublicNames gw01.contoso.local,10.0.10.5 -OpenFirewall `
    -UiRemoteAddress 10.0.10.0/24 -AgentRemoteAddress 10.0.20.0/24
```
- Service `HvPortalGateway`, account `NT SERVICE\HvPortalGateway`, delayed automatic start, restart on failure.
- Binaries `C:\Program Files\HvPortal\Gateway` (read-only for the service), data `C:\ProgramData\HvPortal\Gateway`
  (private ACL). The data directory must be on local disk.
- `-PublicNames`: DNS names/IPs agents will use; they are placed in the internal server certificate.
- Browser certificate: by default the internal certificate is used (browsers will warn; you can import the internal
  CA from `/agent/v1/ca` on port 8444 into your trust store). For production use a certificate from your PKI:
  import it into `LocalMachine\My`, grant the service read access to its private key, and pass
  `-UiCertificateThumbprint`. Renewal: replace the certificate, update the thumbprint, restart the service.
- Configuration is passed through service-scoped environment variables (registry `Services\HvPortalGateway\Environment`).

## 3. First setup

1. On the gateway server, as administrator, open `C:\ProgramData\HvPortal\Gateway\bootstrap\setup-token.txt`.
2. Browse to `https://<gateway>:8443`, enter the token, an administrator user name and a passphrase (≥ 15 chars).
3. Enroll an authenticator app (mandatory for administrators by default), store the recovery codes offline.
4. **Export the keys** for disaster recovery (see [backup-restore.md](backup-restore.md)).

## 4. Add a Hyper-V host

The portal contains a step-by-step guide in the language selected by the user (menu **Guide**, and the
*Add host* / *New enrollment token* dialogs, where every command is pre-filled with the token, a valid gateway name and
the CA fingerprint, with a token countdown and the live connection status of the host).

1. Portal → Hosts → *Add host*. The dialog shows the one-time token (15 min) and the CA fingerprint.
2. **Download the agent package** from the dialog (`HvPortal.Agent-win-x64.zip`, served by the gateway from
   `<gateway>\packages`, built by `Build-Release.ps1`; requires `hosts.manage`; downloads are audited). The dialog
   shows the package SHA-256 and whether it is Authenticode-signed.
3. On the host (elevated): verify `Get-FileHash`, `Unblock-File`, `Expand-Archive`, then check the network with
   `Test-NetConnection <gateway> -Port 8444`.
4. Install and enroll:
   ```powershell
   cd C:\HvPortal\agent-package\installer
   $env:HVP_ENROLLMENT_TOKEN = '<token>'
   .\Install-Agent.ps1 -Source ..\agent -Gateway https://gw01.contoso.local:8444 -CaSha256 <fingerprint>
   ```
   `-Gateway` must use one of the names in the gateway certificate (listed in the guide); `localhost` never works
   from another machine. If the gateway listens on loopback only, the guide shows a blocking warning.
   The script installs `HvPortalAgent` as `NT SERVICE\HvPortalAgent`, adds it to *Hyper-V Administrators*, enrolls
   and starts it. No inbound firewall rule is created on the host.
3. The host turns *online* in the portal within seconds; check *Hyper-V access* = `Ok`.

### 4a. Optional: a named local service account for the agent

By default the agent runs as the virtual account `NT SERVICE\HvPortalAgent`, which is already least privilege (no
password, cannot sign in, member of *Hyper-V Administrators* only). If your policies require a named account (audit,
antivirus or application-control rules by user), add `-ServiceAccount`:
```powershell
.\Install-Agent.ps1 -Source ..\agent -Gateway https://gw01.contoso.local:8444 -CaSha256 <fingerprint> -ServiceAccount svc-hvportal
```
The installer creates the **local** user (or resets one it created earlier; it refuses to take over any other user) with:
- a random 40-character password that nobody sees, set on the service through `Win32_Service.Change` (never on a
  command line) and never expiring; re-running the installer with `-ServiceAccount` rotates it;
- membership of *Hyper-V Administrators* only (not *Administrators*); the virtual account is removed from that group;
- user rights: *Log on as a service* only; *Deny log on locally*, *through Remote Desktop Services*, *from the network*
  and *as a batch job* (local security policy, applied with `secedit` and verified);
- the same private ACLs as the virtual account: binaries read-only, data directory (agent identity) full control.

The rights are the same as the default account's, so the same limits apply: to create VMs or upload files in a folder,
the account needs NTFS *Modify* on it (e.g. `icacls D:\Hyper-V /grant "svc-hvportal:(OI)(CI)M"`). Upgrades without
`-ServiceAccount` keep the account. `Uninstall-HvPortal.ps1 -Component Agent` removes the user, its rights and profile.
On domain-joined hosts a Group Policy that sets the same user rights overrides the local policy (the installer reports
it); use the virtual account there or add the account to the GPO.

## 4b. Alternative: remote hosts through a management agent (ADR-022)

Optional, for hosts on which nothing should be installed (typically workgroup hosts). A **management agent** runs on a
management server (Windows Server, no Hyper-V needed) and reaches the hosts with an administrator account stored,
encrypted, in the portal. The agent on the host (section 4) remains the recommended mode: it stores no credentials.

1. Portal → Inventory → **Management agents** → *New management agent*. Install it on the management server with the
   same package and guide, adding `-Management`:
   ```powershell
   $env:HVP_ENROLLMENT_TOKEN = '<token>'
   .\Install-Agent.ps1 -Source ..\agent -Gateway https://gw01.contoso.local:8444 -CaSha256 <fingerprint> -Management
   ```
   The service account is **not** added to *Hyper-V Administrators* (no local Hyper-V rights are needed).
2. Prepare each Hyper-V host (elevated):
   ```powershell
   Enable-PSRemoting -Force                       # WinRM listener (HTTP 5985) and firewall rule
   # Local account other than the built-in Administrator: allow remote administrative tokens (UAC remote restrictions)
   New-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System -Name LocalAccountTokenFilterPolicy -Value 1 -PropertyType DWord -Force
   # Firewall: WMI (DCOM) and file sharing (SMB 445) from the management server
   Enable-NetFirewallRule -DisplayGroup 'Windows Management Instrumentation (WMI)'
   Enable-NetFirewallRule -DisplayGroup 'File and Printer Sharing'
   ```
   Restrict these rules to the management server's address (`Set-NetFirewallRule -RemoteAddress`). For WinRM over
   HTTPS create a 5986 listener with a certificate the management server trusts and tick *WinRM over HTTPS* in the portal.
3. On the management server, for workgroup hosts over WinRM HTTP (NTLM):
   ```powershell
   Set-Item WSMan:\localhost\Client\TrustedHosts -Value 'hv01.lab.local,10.0.20.15' -Concatenate -Force
   Test-WSMan hv01.lab.local -Credential (Get-Credential) -Authentication Negotiate
   ```
4. Portal → *Add host* → **Through a management agent**: management agent, host name or IPv4 address, user name
   (`HOST\Administrator`; a bare name is qualified with the host name, with an IP address `HOST\name` is required),
   password, WinRM transport. The first inventory arrives within one cycle; *Hyper-V access* shows
   `AccessDenied`/`Error` with the reason if the account or the network is wrong.
5. Credentials can be changed later (host menu → *Connection and credentials*); the password is never shown again.
   Revoking the host makes the management agent forget them at once (saving the connection again re-enables it);
   deleting the host deletes them.

Ports from the management server to each host: TCP 135 + dynamic RPC (WMI/DCOM), 5985 or 5986 (WinRM), 445 (SMB).

## 4c. Optional: allowed storage folders (agent 0.7.0, ADR-034)

By default the portal may use every local fixed drive of the host except the system folders. To limit it to some
folders, add `-StorageFolders`:
```powershell
.\Install-Agent.ps1 -Source ..\agent -Gateway https://gw01.contoso.local:8444 -CaSha256 <fingerprint> -StorageFolders 'D:\VMs','E:\ISO'
```
- Local folders of fixed drives only (no UNC, no `..`, no system folders such as `C:\Windows` or `C:\Users`), at most
  16; missing folders are created. The list is checked **before** the service is stopped: a wrong folder leaves the
  running agent untouched.
- The list is written to `C:\ProgramData\HvPortal\Agent\storage.json` and enforced by the agent: file explorer, folder
  pickers, VM locations, new disks, ISO and template libraries, backup repositories, staging and restore folders, VM
  file moves. The portal shows it on the host page and cannot change it. If the file is damaged the agent refuses every
  folder operation until it is fixed (re-run the installer).
- Upgrades without the parameter keep the list; `-StorageFolders ''` removes the restriction. The agent reads the list
  at start (the installer restarts it). `HvPortal.Agent.exe status` prints it.
- Known gap of 0.7.0: an operation that uses the host's **default** folders because no folder was chosen (a VM created
  without location, a new disk without folder, a restore without location) is not checked against the list. Set the
  Hyper-V default folders (`Set-VMHost -VirtualMachinePath ... -VirtualHardDiskPath ...`) inside the allowed folders.
- Remote hosts reached by a management agent: the list is set in the portal (host page > Storage folders, `hosts.manage`)
  and enforced by the management agent 0.7.0+.
- The agent account still needs NTFS rights in those folders (section 4a).

## 4d. Updates of the agent from the portal (agent 0.7.0, ADR-029)

`Install-Agent.ps1` 0.7.0 registers a scheduled task `\HvPortal\HvPortal Agent Update` (runs as SYSTEM; the agent
service account may only start it) and copies the updater to `C:\Program Files\HvPortal\Updater`. From then on the
portal can update the agent (from the host page or the list of outdated agents — `POST /api/v1/hosts/{id}/agent/update`,
`/api/v1/agents/update-outdated`; `hosts.manage` at Global scope):

1. the agent downloads the package of the gateway over port 8444 and checks its size and SHA-256;
2. the task installs it only if the package's signed catalog and its `HvPortal.Agent.exe` are signed by the **same
   certificate** as the installed agent, every file matches the catalog and the version is not older;
3. the job succeeds when the agent reconnects with the new version (outcome in
   `C:\ProgramData\HvPortal\Agent\update\last-result.json`, also shown in the portal).

Requirements: a **signed** release (section 1) and the first 0.7.0 installation done by hand. Unsigned builds, agents
older than 0.7.0 and agents installed with `-DisableRemoteUpdate` are updated by hand only. A gateway built as a
**Docker image** publishes an agent package built inside the image, without signature and without signed catalog
(Authenticode and `New-FileCatalog` are not available there, ADR-021): agents connected to it are always updated by
hand.
```powershell
.\Install-Agent.ps1 -Source ..\agent -DisableRemoteUpdate     # removes the updater task
```
Changing the code-signing certificate (new thumbprint) means one more update by hand on every agent. The portal also
sends a daily notification listing outdated agents when notifications are configured (Settings > Notifications).

## 5. Assign permissions

Users & roles → *Assign role*. New users have no permissions. Prefer scoped roles (host or VM) over global ones.

## 6. UI port 443 and a certificate from your CA (PFX)

1. Import the PFX into the computer store (elevated; the private key stays non-exportable):
   ```powershell
   $cert = Import-PfxCertificate -FilePath C:\temp\hvportal.pfx -CertStoreLocation Cert:\LocalMachine\My -Password (Read-Host -AsSecureString 'PFX password')
   $cert | Format-List Subject, DnsNameList, NotAfter, Thumbprint
   ```
   The certificate must contain the name users type in the browser (Subject Alternative Name), and the issuing CA
   must be trusted by the clients (domain-joined clients usually get it from AD).
2. Re-run `Install-Gateway.ps1` from the installed release with **all** the parameters used at installation (the
   service environment is rewritten; current values:
   `(Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\HvPortalGateway).Environment`) plus:
   ```powershell
   .\Install-Gateway.ps1 -Source ..\gateway -PublicNames hvportal.contoso.com,10.0.10.5 -OpenFirewall `
       -UiPort 443 -UiCertificateThumbprint $cert.Thumbprint
   ```
   The installer refuses ports already in use (443 is often taken by IIS or another HTTP.sys listener, shown as
   process *System*), checks that the certificate is in `LocalMachine\My` with its private key and grants
   `NT SERVICE\HvPortalGateway` read access to that key. With `-OpenFirewall` the UI rule is recreated on the new port.
3. Open `https://hvportal.contoso.com` (no port). Agents keep using port 8444 and the internal CA: nothing changes on the
   hosts. With Entra ID sign-in, add the new address as a redirect URI (section 7).
4. Renewal: import the new PFX and re-run step 2 with the new thumbprint.

## 7. Microsoft Entra ID sign-in (optional)

The portal has a step-by-step page: **Guide → Microsoft Entra ID sign-in** (with the redirect URI of the address in
use). In short: register a single-tenant *Web* application with redirect URI
`https://<portal address>/api/v1/auth/entra/callback` (one per address/port users open), create a client secret (copy
its *Value*), then Settings → Microsoft Entra ID: tenant ID, client ID, secret, *Test connection*, enable. Create each
Microsoft user in Users & roles (type *Microsoft*, exact UPN, role); require MFA with Conditional Access; keep a local
break-glass administrator before setting *Local sign-in* to emergency accounts only. The gateway needs outbound HTTPS
to `login.microsoftonline.com`.

## 8. VM backups, notifications, VM moves (v0.7.0)

- **Backups**: prerequisites and operator guide in [backup-restore.md](backup-restore.md) (Part A). In short: agents
  0.7.0, a repository (host folder, domain share writable by the hosts' computer accounts, or NAS with credentials), a
  backup staging folder on the hosts that use a NAS with credentials (Host > Settings), checkpoints enabled on the VMs.
- **Notifications**: Settings > Notifications (SMTP with STARTTLS — implicit TLS on port 465 is not supported — and/or
  HTTPS webhooks for Teams, Slack or a generic receiver), then *Send test*. **API tokens** for monitoring: Settings >
  API tokens (shown once).
- **VM moves between hosts** need live migration enabled and authenticated on both hosts by their administrators
  (`Enable-VMMigration`, `Set-VMHost -VirtualMachineMigrationAuthenticationType`, migration networks, firewall); the
  agent does not change those settings, and how the agent's identity is authorized by the destination is not validated
  yet (real-host plan V54, V66): CredSSP cannot work because the move is started by a service; with Kerberos the
  source host's computer account (`HOST$`) is expected to need the right to manage Hyper-V on the destination.

## Upgrade

The portal has a step-by-step page with the commands for your installation: **Guide → Update the Windows services**
(it also lists the agent version of every host).

- **Gateway**: run `Install-Gateway.ps1` from the new release **with the same parameters used at installation**
  (`-PublicNames`, `-BindAddress`, ports, `-UiCertificateThumbprint`; current values:
  `(Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\HvPortalGateway).Environment`). The script stops the
  service, copies the database files to `db\pre-upgrade-<timestamp>\` (`hvportal.db`, `-wal`, `-shm`), backs up the
  binaries (`<InstallPath>.bak-<timestamp>`), installs, starts, and **rolls back the binaries automatically** if the
  service does not stay running. Migrations run at start: to go back to an older release, stop the service, delete
  `hvportal.db-wal`/`-shm`, restore the pre-upgrade copy and reinstall the old release.
- **Agent**: extract the new package into a new folder on the host and run `.\Install-Agent.ps1 -Source ..\agent`
  without `-Gateway` or token: the enrollment (`C:\ProgramData\HvPortal\Agent`) is kept; binaries are backed up and
  restored automatically if the service does not start. Rollback: same command from the previous package folder.
  From 0.7.0 on, agents of a signed release can also be updated from the portal (section 4d).
- **Locked files (fixed in 0.7.0)**: earlier installers could fail with "Cannot remove clrjit.dll: access denied"
  because `Stop-Service` returns before the service process has released its files. The installers now wait for the
  service **process** to exit (and end it after 120 s), retry files that are still in use (10 attempts, 3 s apart) and,
  if the files still cannot be replaced, put the previous binaries back from `<InstallPath>.bak-<timestamp>` and start
  them ("Upgrade not applied"). If even that copy back fails, the message says to restore by hand from that folder;
  check that the service runs before leaving the server. The upgrade from 0.6.x already uses the new scripts (they
  come with the 0.7.0 package).
- Order: gateway first, then agents. The gateway never sends command kinds an agent's protocol does not know; those
  functions show "update the agent".
- **0.7.0** (agent protocol 9): the database migration adds the tables of backups, portal networks and API tokens and
  the host columns for storage folders, backup staging and updater state; nothing to do by hand. Host health, VM move,
  networks with VLAN, backups and new folders need agent 0.7.0 on the host.

## Uninstall

```powershell
.\Uninstall-HvPortal.ps1 -Component Agent            # on a host (then revoke the host in the portal)
.\Uninstall-HvPortal.ps1 -Component Gateway          # keeps data unless -RemoveData
```
The agent uninstall also removes the updater task and `C:\Program Files\HvPortal\Updater` (0.7.0); it waits for the
service process to exit before deleting the binaries. Restore points in repositories are never deleted by an
uninstall.

## Ports

| Port | Direction | Purpose |
|---|---|---|
| 8443/tcp | browsers → gateway | UI, REST API, SignalR; also the public read-only API for monitoring with API tokens (v0.7.0) |
| 8444/tcp | agents → gateway | enrollment and agent channel (mTLS); package download for agent updates (v0.7.0) |
| 445/tcp | hosts → backup repositories (SMB shares) | only when a repository is a share (v0.7.0) |
| SMTP (587 or the configured port) / 443 | gateway → mail server / webhook endpoints | only when notifications are configured (v0.7.0) |
| live migration (Windows: rule *Hyper-V (MIG-TCP-In)*) and SMB between hosts | host ↔ host | only for VM moves between hosts (v0.7.0); configured by the host administrators, not by HvPortal |

Internet access is not required. Entra ID sign-in (section 7) needs outbound HTTPS from the gateway to
`login.microsoftonline.com` (or the national cloud endpoint); webhooks to Teams or Slack need outbound HTTPS to them.
