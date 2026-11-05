# Installation guide

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

## 5. Assign permissions

Users & roles → *Assign role*. New users have no permissions. Prefer scoped roles (host or VM) over global ones.

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

Internet access is not required. Entra ID (M2) will need outbound HTTPS to Microsoft identity endpoints.
