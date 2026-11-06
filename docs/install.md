# Installation guide

## Docker (Linux container, e.g. Portainer)

The gateway can also run as a container: see [docker/README.md](../docker/README.md) (`docker/docker-compose.yml`,
`docker/.env.example`). The Hyper-V hosts keep the Windows agent.

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
A self-signed signature is **not** a reputation: SmartScreen and antivirus products do not trust it by themselves.
Once the certificate is imported, Authenticode reports the files as valid, PowerShell `AllSigned` policies accept the
scripts, and AppLocker/WDAC or antivirus allow rules can be written *by publisher* instead of by path or hash. Without
`-TimestampServer` the signatures stop validating when the certificate expires (re-sign with a new certificate).
Keep the private key on the build machine only; whoever holds it can sign code that those machines will trust.

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
- Order: gateway first, then agents. The gateway never sends command kinds an agent's protocol does not know; those
  functions show "update the agent".

## Uninstall

```powershell
.\Uninstall-HvPortal.ps1 -Component Agent            # on a host (then revoke the host in the portal)
.\Uninstall-HvPortal.ps1 -Component Gateway          # keeps data unless -RemoveData
```

## Ports

| Port | Direction | Purpose |
|---|---|---|
| 8443/tcp | browsers → gateway | UI, REST API, SignalR |
| 8444/tcp | agents → gateway | enrollment and agent channel (mTLS) |

Internet access is not required. Entra ID sign-in (section 7) needs outbound HTTPS from the gateway to
`login.microsoftonline.com` (or the national cloud endpoint).
