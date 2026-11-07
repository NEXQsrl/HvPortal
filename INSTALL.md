<p align="center"><b>English</b> | <a href="INSTALL_IT.md">Italiano</a></p>

# Installing HvPortal

This guide installs HvPortal 0.7.0 on your servers: the **gateway** (the web portal) on a management server, and an
**agent** on each Hyper-V host. It also covers certificates, updates, uninstallation and what Hyper-V needs before
backups and live migration. [docs/install.md](docs/install.md) is the reference for every option.

> HvPortal is a preview. The checks still to be run on real hosts are listed in the
> [real-host test plan](docs/verification/real-host-test-plan.md). Install it in a lab or on non-critical hosts first.

## Contents

- [Overview](#overview)
- [Requirements](#requirements)
- [1. Get the release files](#1-get-the-release-files)
- [2. Install the gateway](#2-install-the-gateway)
- [3. First access](#3-first-access)
- [4. Portal certificate and names](#4-portal-certificate-and-names)
- [5. Install the agent on each Hyper-V host](#5-install-the-agent-on-each-hyper-v-host)
- [6. Microsoft Entra ID sign-in (optional)](#6-microsoft-entra-id-sign-in-optional)
- [7. Before the first backup](#7-before-the-first-backup)
- [8. Before moving VMs between hosts](#8-before-moving-vms-between-hosts)
- [9. Updating](#9-updating)
- [10. Uninstalling](#10-uninstalling)
- [11. Troubleshooting](#11-troubleshooting)

## Overview

| Component | Where | What it is |
|---|---|---|
| Gateway | A management server or VM (Windows Server), or a Docker host | Windows service `HvPortalGateway` running as `NT SERVICE\HvPortalGateway`, which is not an administrator, or a non-root Linux container. It serves the portal, the REST API and the agent channel. |
| Agent | Every Hyper-V host | Windows service `HvPortalAgent` running as `NT SERVICE\HvPortalAgent`, a member of the local *Hyper-V Administrators* group only. It opens the connection to the gateway. |
| Management agent (optional) | A Windows server without Hyper-V | The same agent, installed in management mode. It reaches hosts on which nothing is installed, using credentials stored encrypted in the portal. |

The recommended layout is a dedicated management server or VM for the gateway. The gateway can also run on a Hyper-V
host: the gateway and the agent then remain two services with two separate identities. No component needs domain
membership, and hosts in different domains or workgroups can be managed by the same gateway.

Network ports (both numbers can be changed at installation):

| Port | From → to | Purpose |
|---|---|---|
| TCP 8443 | Browsers → gateway | Portal, REST API, real-time updates |
| TCP 8444 | Agents → gateway | Enrollment, agent channel (mutual TLS), agent package downloads |
| None | → Hyper-V hosts | The agent opens the connection: no inbound rule is created on the hosts |

Traffic to the agent port must reach the gateway directly. It cannot go through a proxy that terminates TLS, because
the agent authenticates with a client certificate. Management agents are the one exception to "no inbound ports": they need
WMI/DCOM (TCP 135 and dynamic RPC), WinRM (5985 or 5986) and SMB (445) from the management server to each remote host.

## Requirements

**Gateway server**

- Windows Server, 64-bit. Windows Server 2025 is the target platform and Windows Server 2022 is under evaluation (see
  the [compatibility matrix](docs/compatibility-matrix.md)). A VM is fine.
- Administrator rights to install. The service itself runs without them.
- The release is self-contained, so no .NET runtime needs to be installed. The graphical setup needs .NET Framework
  4.7.2, which is part of Windows Server 2019 and later.
- The data directory (`C:\ProgramData\HvPortal\Gateway` by default) must be on a local disk, not on an SMB share.
- A list of the DNS names and IP addresses that browsers and agents will use to reach the gateway. They go into the
  internal certificate. Hosts outside the domain often cannot resolve domain names, so include the IP address too.
- Free TCP ports for the portal and the agents. Port 443 is often already taken by IIS or another HTTP.sys listener.

**Or a Docker host** (`linux/amd64`) for the gateway: see [2c](#2c-docker-linux-container).

**Each Hyper-V host**

- Windows Server with the Hyper-V role, Windows PowerShell 5.1 (part of Windows) and administrator rights to install.
- The Hyper-V PowerShell module: `Install-WindowsFeature Hyper-V-PowerShell`. Without it, inventory, power operations
  and the console still work; VM settings, VM creation, checkpoints, templates, networks, backups, moves and host health
  do not.
- Outbound TCP to the gateway's agent port, using one of the names or addresses given to the gateway.
- If a Group Policy enforces the `AllSigned` PowerShell execution policy, install a signed release. PowerShell
  Constrained Language Mode (WDAC/AppLocker script enforcement) is not supported yet.

**Browsers**: current Edge, Chrome or Firefox.

**Internet access** is not required. Only the features you enable use it: Microsoft Entra ID sign-in (HTTPS to
`login.microsoftonline.com` from the gateway), and your SMTP server and webhooks for notifications.

## 1. Get the release files

Build the release on a Windows machine with the prerequisites listed in the [README](README.md#building-from-source),
or use release files published by the maintainers.

```powershell
.\installer\Build-Release.ps1                                       # unsigned build
.\installer\Build-Release.ps1 -SigningCertificateThumbprint <thumb>  # signed build
```

The output is in `artifacts\release\<version>\`:

| Folder or file | Contents |
|---|---|
| `setup\` | `HvPortal.Gateway.Setup.exe` and `HvPortal.Agent.Setup.exe`, the graphical setups |
| `gateway\` | The gateway files. `gateway\packages\` holds the agent package and setup that the portal offers for download. |
| `agent\` | The agent files |
| `installer\` | The PowerShell scripts, plus `HvPortal-CodeSigning.cer` in a signed build |
| `deps\` | Inventory of the third-party dependencies |
| `SHA256SUMS.txt` | SHA-256 of every file |

Copy the files to the servers through your usual software distribution channel, then compare them with
`SHA256SUMS.txt` (`Get-FileHash <file> -Algorithm SHA256`).

### Code signing

| Build | What it means |
|---|---|
| Unsigned | Windows treats the files as unsigned. Agents can only be updated by hand. |
| Signed with a self-signed certificate (`installer\New-CodeSigningCertificate.ps1`) | The signatures are present and intact, but Windows reports them as untrusted until the certificate is imported. This gives no SmartScreen or antivirus reputation: a self-signed signature does not make a build trusted. Agents can be updated from the portal. |
| Signed with a certificate from a CA your machines trust | The signatures validate normally. Agents can be updated from the portal. |

With an unsigned or self-signed build, Windows may show "Windows protected your PC" when a setup starts: check the
SHA-256, then choose *More info > Run anyway*. To make a self-signed signature validate, import the public certificate
on each server (elevated PowerShell, or a GPO):

```powershell
Import-Certificate -FilePath .\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath .\HvPortal-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

Once the certificate is imported, Authenticode reports the files as valid, `AllSigned` PowerShell policies accept the
scripts, and application-control or antivirus rules can be written by publisher. It still gives no SmartScreen or
antivirus reputation. Keep the private key of the signing certificate on the build machine only: whoever holds it can
sign code that these machines will trust. Without `-TimestampServer` the signatures stop validating when the
certificate expires.

## 2. Install the gateway

### 2a. Graphical setup

1. Copy `HvPortal.Gateway.Setup.exe` to the gateway server, start it and accept the administrator prompt.
2. **Addresses and ports**: list every name and IP address that browsers and agents will use. The portal port is 8443
   by default; with 443 users do not have to type the port. The agent port is 8444.
3. **Portal certificate**: keep the *internal certificate*, choose a certificate already in the computer store, or
   *import a .pfx file* from your CA (see [section 4](#4-portal-certificate-and-names)).
4. **Windows Firewall**: let the setup create the inbound rules, and say who may open the portal and which hosts may
   connect as agents (`LocalSubnet`, addresses or subnets such as `10.0.20.0/24`). Without rules, the portal answers
   only from the server itself.
5. **Install**. At the end the setup shows the portal address and, on a new installation, the one-time setup token.

The setup is a single file. It unpacks the signed binaries and scripts of the release into a folder only administrators
can write, runs `Install-Gateway.ps1` with your choices and shows its output. The log is saved in
`C:\ProgramData\HvPortal\SetupLogs`. The wizard is in English or Italian, following the Windows display language.

### 2b. PowerShell script

Use the script for unattended installations or Server Core. The scripts are in the release `installer\` folder, and each
setup can also extract them: `HvPortal.Gateway.Setup.exe --extract C:\HvPortal\files`.

```powershell
cd <release>\installer
.\Install-Gateway.ps1 -Source ..\gateway -PublicNames gw01.contoso.local,10.0.10.5 -OpenFirewall `
    -UiRemoteAddress 10.0.10.0/24 -AgentRemoteAddress 10.0.20.0/24
```

| Parameter | Default | Meaning |
|---|---|---|
| `-Source` | (required) | The `gateway` folder of the release |
| `-PublicNames` | none | DNS names and IP addresses used by browsers and agents. They are placed in the internal certificate. |
| `-UiPort` / `-AgentPort` | 8443 / 8444 | Portal port and agent port |
| `-BindAddress` | `0.0.0.0` | Address to listen on (`0.0.0.0` means every adapter) |
| `-UiCertificateThumbprint` | none | Portal certificate from `LocalMachine\My` instead of the internal one |
| `-OpenFirewall` | off | Create the inbound rules for both ports (Domain and Private firewall profiles) |
| `-UiRemoteAddress` / `-AgentRemoteAddress` | `LocalSubnet` | Who may reach the portal port and the agent port |
| `-InstallPath` / `-DataPath` | `C:\Program Files\HvPortal\Gateway` / `C:\ProgramData\HvPortal\Gateway` | Binaries and data |

The script checks the ports and the certificate before it touches a running service. It installs the service with a
delayed automatic start and restart on failure. The binaries are read-only for the service, and the data directory is
private (SYSTEM, Administrators and the service). The settings are stored as environment variables of the service in
the registry: `(Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\HvPortalGateway).Environment`.

### 2c. Docker (Linux container)

The gateway can run as a Linux container (for example with Portainer). The agents stay Windows services on the hosts.
The full procedure is in [docker/README.md](docker/README.md). In short:

1. Build the image from the repository; no prebuilt image is published:
   `docker build --provenance=false --sbom=false --platform linux/amd64 -f docker/Dockerfile -t hvportal-gateway:0.7.0 .`
2. Deploy `docker/docker-compose.yml` with the variables from `docker/.env.example`. Set at least:
   - `HVP_PUBLIC_NAMES`: the names and IP addresses of the Docker host, as the Hyper-V hosts see it;
   - `HVP_KEY_ENCRYPTION_KEY`: 32 random bytes in base64 (`openssl rand -base64 32`). **Store it in your password
     vault.** Without it the data volume cannot be opened.
3. Read the setup token in the container log (line `HvPortal setup token ...`). After the first sign-in, set
   `HVP_PRINT_SETUP_TOKEN=false`.

The container runs as a non-root user, with a read-only root file system, no Linux capabilities and
`no-new-privileges`. Publish the agent port straight to the container, never through a TLS-terminating proxy.

The agent package built inside the image is not signed and has no signed catalog. Agents connected to a container
gateway are therefore updated by hand ([section 9](#9-updating)).

## 3. First access

1. Open the address shown by the setup, for example `https://gw01.contoso.local:8443`. With the internal certificate
   the browser shows a warning; this is expected (see [section 4](#4-portal-certificate-and-names)).
2. Enter the **setup token**. The graphical setup shows it. It is also stored in
   `C:\ProgramData\HvPortal\Gateway\bootstrap\setup-token.txt`, which only administrators can read; in a container it
   is in the log. The token works once, for 24 hours, and is replaced at every restart until the setup is completed.
   There are no default credentials.
3. Choose the administrator's user name and a passphrase of at least 15 characters.
4. Enroll an authenticator app (TOTP) and keep the recovery codes offline. Two-step verification is required for
   administrators by default.
5. Protect the gateway's own data: **Settings > Backup and restore > Create backup**, with a passphrase of at least 20
   characters. Store the file and the passphrase separately, offline. The database alone cannot be restored: the keys
   that protect the MFA secrets and the internal CA are stored outside it. On Windows you can also run
   `HvPortal.Gateway.exe admin export-keys` (see [docs/backup-restore.md](docs/backup-restore.md)).
6. Create users and assign roles in **Users & roles**. New users have no permissions. Prefer roles assigned on a host,
   a group or a VM over global ones.

If nobody can sign in any more, a local administrator of the gateway server can run
`HvPortal.Gateway.exe admin recover-account --user <name> [--reset-mfa]` to get a one-time recovery token.

## 4. Portal certificate and names

The gateway uses two certificates:

- The **portal certificate**, seen by browsers on the portal port. By default it is issued by the gateway's internal
  CA, and browsers show a warning until you trust that CA. You can download the CA from
  `https://<gateway>:8444/agent/v1/ca` and import it into the trusted roots of your clients.
- The **agent channel certificate**, always issued by the internal CA. Agents pin that CA by its fingerprint when they
  enroll, and they check that the gateway's name is in the certificate. Every name or address an agent uses must be
  among the gateway's public names. To add a name later, run the gateway setup again: the certificate is issued again
  when the service starts.

**Using a certificate from your CA (and port 443)**

- With the graphical setup: run `HvPortal.Gateway.Setup.exe` again. On the *Portal certificate* page choose *Import a
  certificate file from your CA (.pfx)*, and change the port on the first page if you want to.
- With the script: import the PFX into the computer store, then run `Install-Gateway.ps1` again with **all** the
  parameters you used at installation, plus the thumbprint:

  ```powershell
  $cert = Import-PfxCertificate -FilePath C:\temp\hvportal.pfx -CertStoreLocation Cert:\LocalMachine\My `
      -Password (Read-Host -AsSecureString 'PFX password')
  .\Install-Gateway.ps1 -Source ..\gateway -PublicNames hvportal.contoso.com,10.0.10.5 -OpenFirewall `
      -UiPort 443 -UiCertificateThumbprint $cert.Thumbprint
  ```

The certificate must contain the names users type in the browser (Subject Alternative Name), and its CA must be
trusted by the clients. The installer gives the gateway service read access to the private key. The agents keep using
port 8444 and the internal CA, so nothing changes on the hosts. To renew, import the new certificate and repeat the
step with the new thumbprint. If you use Entra ID sign-in, register every new portal address as a redirect URI.

## 5. Install the agent on each Hyper-V host

### 5a. Add the host in the portal

In the portal choose **Add host**, then **Agent on the host (recommended)**. Keep the dialog open. It shows:

- a **one-time token**, valid for 15 minutes (*New enrollment token* creates another one), and the **fingerprint of
  the CA**;
- the downloads: `HvPortal.Agent.Setup.exe` and the package `HvPortal.Agent-win-x64.zip`, with their SHA-256 and their
  signature state (downloads need the `hosts.manage` permission and are audited);
- the gateway name to use: choose one the host can reach (`localhost` never works from another machine);
- the live connection status of the host.

From the host, check the network first: `Test-NetConnection <gateway> -Port 8444`.

### 5b. Graphical setup

1. In the dialog choose *Guided setup*, download `HvPortal.Agent.Setup.exe` and copy it to the host.
2. Copy the line **Data for the setup** (gateway address, CA fingerprint and token). If you copy it on another PC and
   the clipboard does not reach the host, paste it into a text file on the host and copy it from there, or type the
   three values by hand.
3. On the host, start the setup and accept the administrator prompt:
   - **Where is the agent installed**: *On a Hyper-V host*.
   - **Connection to the gateway**: press **Paste from the portal**.
   - **Service account**: keep *Automatic account* unless your rules require a named account
     ([5f](#5f-dedicated-service-account)).
   - **Storage folders**: *Every local drive* or *Only these folders* ([5d](#5d-storage-folders)). On the same page,
     *Allow updates of the agent from the portal* is ticked by default ([5e](#5e-updates-from-the-portal)).
4. **Install**. The host turns online in the portal within a few seconds. Check that *Hyper-V access* shows `Ok`.

### 5c. PowerShell script

```powershell
Get-FileHash .\HvPortal.Agent-win-x64.zip -Algorithm SHA256     # compare with the value in the portal
Unblock-File .\HvPortal.Agent-win-x64.zip
Expand-Archive .\HvPortal.Agent-win-x64.zip C:\HvPortal\agent-package
cd C:\HvPortal\agent-package\installer
$env:HVP_ENROLLMENT_TOKEN = '<token>'       # the token travels in a variable, not on the installer's command line
.\Install-Agent.ps1 -Source ..\agent -Gateway https://gw01.contoso.local:8444 -CaSha256 <fingerprint>
```

The portal's *PowerShell* method shows these commands already filled in.

| Parameter | Meaning |
|---|---|
| `-Source` | The `agent` folder of the package (required) |
| `-Gateway`, `-CaSha256` | Gateway address on the agent port and CA fingerprint. Only for enrollment: leave them out on an update. |
| `-EnrollmentToken` | The one-time token. Prefer the variable `HVP_ENROLLMENT_TOKEN`. |
| `-ForceReenroll` | Enroll again with a new token, replacing the current identity |
| `-StorageFolders` | Folders the portal may use on this host ([5d](#5d-storage-folders)) |
| `-DisableRemoteUpdate` | No updates from the portal ([5e](#5e-updates-from-the-portal)) |
| `-ServiceAccount` | Dedicated local user instead of the virtual account ([5f](#5f-dedicated-service-account)) |
| `-Management` | Management server instead of a Hyper-V host ([5h](#5h-hosts-without-an-agent-management-agents)) |
| `-InstallPath` / `-DataPath` | Default `C:\Program Files\HvPortal\Agent` / `C:\ProgramData\HvPortal\Agent` |

The script installs the `HvPortalAgent` service as `NT SERVICE\HvPortalAgent` and adds it to *Hyper-V Administrators*
(found by its SID, `S-1-5-32-578`, so the localized group name does not matter). The service is not added to
*Administrators*. The script creates no inbound firewall rule, then enrolls and starts the agent. If the new version
does not start on an update, the previous binaries are restored.

### 5d. Storage folders

By default the portal may use every local fixed drive of the host, except the system folders (`Windows`,
`Program Files`, `ProgramData`, `Users` and others), which are hidden and refused.

With `-StorageFolders` (or *Only these folders* in the setup), the portal may use **only** the listed folders and their
subfolders: in the file explorer and the folder pickers, and for VM locations, new disks, the ISO and template
libraries, backup repositories, the backup staging folder, restore locations and the destinations of VM moves. Nothing
else on the host is visible.

```powershell
.\Install-Agent.ps1 -Source ..\agent -StorageFolders 'D:\VMs','D:\ISO','E:\Backups'   # an installed agent: no token
.\Install-Agent.ps1 -Source ..\agent -StorageFolders ''                                # remove the restriction
```

- Up to 16 local folders on fixed drives, with no system folders and no `..`. Missing folders are created.
- The list is enforced by the agent, on the host. It is saved in `C:\ProgramData\HvPortal\Agent\storage.json` (private
  to SYSTEM, Administrators and the service), and **the portal cannot change it**. If the file is not valid, the agent
  refuses every folder operation until it is fixed with `-StorageFolders`.
- An update without the parameter keeps the current list.
- For hosts reached through a management agent there is no agent on the host: the list is set in the portal, in the
  host's settings, by a user with `hosts.manage`.
- The list does not grant file system rights. See [5g](#5g-folder-permissions).

### 5e. Updates from the portal

By default `Install-Agent.ps1` registers the scheduled task `\HvPortal\HvPortal Agent Update`. It runs as SYSTEM, and
the agent's service account may only start it, not change it. Its script is copied to
`C:\Program Files\HvPortal\Updater`. Section [9](#9-updating) explains what it checks before it installs anything.

`-DisableRemoteUpdate` removes the task, so the agent can only be updated by hand. In the graphical setup, untick
*Allow updates of the agent from the portal*. **Repeat the choice at every manual update**: an update without
`-DisableRemoteUpdate` registers the task again, and the box in the setup is ticked by default.

### 5f. Dedicated service account

The default virtual account `NT SERVICE\HvPortalAgent` is already least privilege: it has no password, cannot sign in,
and belongs to *Hyper-V Administrators* only. If your policies require a named account (for example for auditing or
application-control rules), add `-ServiceAccount svc-hvportal`, or choose *Dedicated local user* in the setup. The
installer then creates a **local** user and:

- gives it a random 40-character password that nobody sees and that never expires, set without appearing on a command
  line (running the installer again with `-ServiceAccount` rotates it);
- makes it a member of *Hyper-V Administrators* only, and removes the virtual account from that group;
- gives it only the *Log on as a service* right, and denies local, Remote Desktop, network and batch sign-in;
- refuses to take over an existing user that it did not create.

Updates without `-ServiceAccount` keep the account. On domain-joined hosts, a Group Policy that sets the same user
rights overrides the local policy; the installer reports it.

### 5g. Folder permissions

Being a Hyper-V administrator does not give the agent's account write access to your folders. Give it NTFS *Modify*
on the folders where the portal creates VMs, uploads files, keeps templates or writes backups:

```powershell
icacls D:\VMs /grant "NT SERVICE\HvPortalAgent:(OI)(CI)M"
icacls D:\Templates /grant "NT SERVICE\HvPortalAgent:(OI)(CI)M"
icacls E:\Backups /grant "NT SERVICE\HvPortalAgent:(OI)(CI)M"
# with a dedicated account: icacls D:\VMs /grant "svc-hvportal:(OI)(CI)M"
```

### 5h. Hosts without an agent: management agents

For hosts on which nothing may be installed (typically workgroup hosts), install the agent with `-Management` (or *On
a management server* in the setup) on a Windows server, using the token of **Inventory > Management agents > New
management agent**. Then add each host with **Add host > Through a management agent**, giving its administrator
credentials. The credentials are stored encrypted in the portal and held only in memory by the management agent. The
hosts need WinRM, WMI and SMB from the management server. The preparation of the hosts and the TrustedHosts
configuration are in [docs/install.md](docs/install.md) (management agents section).

The agent on the host remains the recommended mode, because it stores no credentials. Some features need it: for
example, browsing the files of a restore point needs a host with its own agent.

## 6. Microsoft Entra ID sign-in (optional)

The portal has a step-by-step page: **Guide > Microsoft Entra ID sign-in**. In short:

1. In Entra ID, register a single-tenant application of type *Web*, with the redirect URI
   `https://<portal address>/api/v1/auth/entra/callback` (one per address users open), and create a client secret.
2. In **Settings > Microsoft Entra ID**, enter the tenant ID, the client ID and the secret, use *Test connection*, then
   enable it.
3. Create each Microsoft user in **Users & roles** (type *Microsoft*, exact UPN, role). Users who are not created in
   the portal cannot sign in.
4. Require MFA for these users with Conditional Access in Entra ID. Keep at least one local administrator for
   emergencies before you limit local sign-in.

The gateway needs outbound HTTPS to `login.microsoftonline.com`.

## 7. Before the first backup

Backups, restores and restore tests run on the hosts, with the Hyper-V PowerShell module. A backup takes a checkpoint
of the VM, exports it into the repository, removes the checkpoint (its differencing disks are merged back), computes a
SHA-256 for every exported file and writes a manifest last: a folder without a manifest is an incomplete restore
point. Each restore point is a full copy, so size the repository accordingly.

**Checklist**

| What | Why |
|---|---|
| Agent 0.7.0 or later on the hosts that back up or restore | Older agents do not know these operations; the portal says "update the agent". |
| Hyper-V PowerShell module on the host (`Export-VMSnapshot`, `Import-VM`, `Mount-VHD`) | Used for export, import and file restore. |
| Checkpoints enabled on each VM (checkpoint type not *Disabled*), for example `Set-VM -Name <vm> -CheckpointType Production` | A backup needs a checkpoint. When the backup job asks for a different type, the VM's setting is changed only for the moment of the checkpoint and then put back. |
| The *Backup (volume shadow copy)* integration service enabled for the VM, and the integration services running in the guest | With a production checkpoint the guest takes part (VSS in Windows guests). Otherwise the copy falls back to a standard checkpoint, which is crash-consistent; with *production only* the backup fails instead. |
| A repository the host can write to | See below. |
| A **backup staging folder** on the host (host settings) | Needed for shares with credentials, for restore tests (the test VM is created in `<staging>\restore-tests`) and for browsing files of restore points on such shares. It must be a local folder, not a drive root, with room for a full copy of the largest VM. |
| Space | The readiness check warns below 50 GB free on a local repository drive and below 100 GB on the staging folder. |
| Storage folders | If the host has `-StorageFolders`, local repositories, the staging folder and restore locations must be inside them. |

**Repositories**

- **Local folder of a host** (for example `E:\Backups`): it belongs to that host. The agent's account must be able to
  create and write it ([5g](#5g-folder-permissions)).
- **SMB share without credentials** (for example `\\nas01\backups\hyperv`): any host can use it. The host reaches it
  with its **computer account** (`HOST$`), so give the hosts' computer accounts write access to the share and to its
  folders. This needs hosts and a file server in the same domain (or trusting domains).
- **SMB share with credentials** (for example a NAS in a workgroup): the user name and password are stored encrypted in
  the portal and sent to the agent only for the operation in progress. Hyper-V cannot use these credentials, so the
  export is first prepared in the host's staging folder and then copied to the share. A staging folder is therefore
  required on each host that uses this repository.

**Readiness check**: before the first backup, let the portal check the host (with the repository you plan to use). It
lists the Hyper-V cmdlets, the checkpoint setting and integration services of each VM, whether the repository is
reachable and writable, free space and the staging folder, each with a suggested fix. The same check is available from
the API: `GET /api/v1/hosts/{id}/backup-readiness?repositoryId=<id>`.

**Permissions**: `backup.manage` (global) manages repositories and backup jobs and deletes restore points;
`backup.run` backs up and verifies; `backup.restore` restores VMs, disks and files. A restore as a new VM also needs
`vm.create` on the destination host. The built-in role *Backup operator* combines them.

**Good to know**

- Restoring **over the original VM** turns it off and removes it from Hyper-V, renames its disk files
  (`*.pre-restore-<time>`) and imports the restore point with the original id, name and folders. You must type the VM's
  name to confirm. The renamed files stay on disk until you remove them.
- **File-level restore** mounts a disk of the restore point read-only on a host that has the agent installed. Only the
  user who mounted it can browse it, every download is audited, and the disk is detached when you are done or after 2
  hours without use. The guest's file permissions apply: files the agent's account cannot read are reported as such.
- A backup of a large VM can take hours. The limit is 12 hours per VM.

## 8. Before moving VMs between hosts

HvPortal moves VMs with Hyper-V's own commands: `Move-VM` to another host (live migration if the VM is running) and
`Move-VMStorage` to another folder of the same host. **The portal does not change the migration settings of the
hosts**: if something is missing, the task fails with the error reported by Hyper-V.

**In the portal**

- Agent 0.7.0 or later on the source host. The destination must be a host of the portal that has already sent its
  inventory, because its DNS name is taken from it.
- The user needs `vm.migrate` on the VM and `vm.create` on the destination host. The limits of the VM's group apply.
- The destination folder must be inside the storage folders of the destination host, if it has any. The VM goes to
  `<folder>\<VM name>`.

**On the Hyper-V hosts (live migration)**

- Live migration enabled on **both** hosts: *Hyper-V Settings > Live Migrations*, or `Enable-VMMigration`, with the
  networks to use (`Set-VMMigrationNetwork`, or `Set-VMHost -UseAnyNetworkForMigration $true`).
- An authentication method that works between the two hosts:
  - **Kerberos with constrained delegation** (domain): on each host's computer object in Active Directory, allow
    delegation to the other host's services *Microsoft Virtual System Migration Service* and *cifs* (*cifs* is needed
    to move storage), then `Set-VMHost -VirtualMachineMigrationAuthenticationType Kerberos`;
  - or **CredSSP**.
- Microsoft's requirements for live migration without a failover cluster include hosts in the same Active Directory
  domain, or in domains that trust each other.
- Virtual switches with the same names on the destination, compatible processors (same manufacturer; enable processor
  compatibility on the VM if the generations differ), and enough free space at the destination.
- Without *Move the files of the VM too*, the files must already be on storage that both hosts can reach (an SMB share
  or a cluster volume).

**Move of storage on the same host**: the destination folder must not exist or must be empty. A running VM keeps
running during the move.

> **Not yet validated on real hosts.** A move is started by the agent service, not by a signed-in administrator. Two
> points still have to be validated on real hosts. First, with CredSSP Microsoft requires the migration to be started
> from a session signed in on the source host, which a service does not have; Kerberos with constrained delegation is
> the method that fits. Second, check which identity the destination host sees for the agent (on the network, the
> agent's virtual account acts as the source host's computer account) and which rights that identity needs there.

## 9. Updating

Update the **gateway first, then the agents**. The gateway never sends an agent a command it does not know. Features
that need a newer agent say so ("update the agent").

**Before updating**, create a portal backup (**Settings > Backup and restore**).

### Gateway

- **Graphical setup**: run the `HvPortal.Gateway.Setup.exe` of the new version on the same server. It recognizes the
  installation, fills in the current settings and copies the database before updating. If the new version does not
  start, the previous one is restored automatically.
- **Script**: run `Install-Gateway.ps1` from the new release **with the same parameters used at installation**. The
  service settings are rewritten, so a parameter you leave out goes back to its default. The script copies the database
  to `db\pre-upgrade-<timestamp>\`, backs up the binaries to `<InstallPath>.bak-<timestamp>` and puts them back if the
  service does not stay running.
- **Docker**: build the new image, change `HVP_IMAGE` and update the stack.

Database migrations run when the gateway starts. To go back to an older release: stop the service, delete
`hvportal.db-wal` and `hvportal.db-shm`, restore the `pre-upgrade` copy and reinstall the old release.

### Agents from the portal

From the portal you can update one agent, or every outdated agent at once. Each update is a task, and needs the
`hosts.manage` permission. It works when **all** these conditions are true:

1. The agent already runs **0.7.0 or later**. Older agents have no updater: update them once by hand.
2. The updater task is registered: the agent was installed by `Install-Agent.ps1` 0.7.0 or later without
   `-DisableRemoteUpdate`.
3. The release is **signed**: the gateway's agent package carries a signed catalog, `HvPortal.Agent.cat`. This
   requires `Build-Release.ps1 -SigningCertificateThumbprint`. Unsigned builds, including the package built inside the
   Docker image, are never installed this way.
4. The package is signed by **the same certificate** as the installed agent. If you sign a release with a different
   certificate, update the agents by hand once.
5. The package is not older than the installed agent, and the agent is connected.

What happens on the host:

1. The agent downloads the package from the gateway's agent port (mutual TLS, the same identity as its channel). It
   checks the size and the SHA-256 given by the gateway and saves it in `C:\ProgramData\HvPortal\Agent\update\`.
2. The agent starts the scheduled task. Starting it is the only right the agent has on the task.
3. The task, running as SYSTEM, copies the package into a folder that only SYSTEM and Administrators can write, and
   checks again that the SHA-256 matches. It then requires:
   - `HvPortal.Agent.cat` signed by the **same certificate (thumbprint) as the installed `HvPortal.Agent.exe`**;
   - every file of the package matching the catalog, with no file added (`Test-FileCatalog`);
   - the new `HvPortal.Agent.exe` signed by that same certificate;
   - a version that is not older than the installed one.
4. It runs the package's own `Install-Agent.ps1`, which keeps the identity, the service account, the storage folders
   and the data. If the new version does not start, the previous binaries are restored.
5. The outcome is written to `C:\ProgramData\HvPortal\Agent\update\last-result.json` and reported to the gateway when
   the agent reconnects.

The trust anchor is the certificate that signed the agent you installed by hand, not the Windows trust store. That is
why an intact self-signed signature is accepted even when its root is not trusted on the host. Neither the gateway nor
the agent service can make the task install code that this certificate did not sign. When notifications are
configured, the gateway also reports outdated agents once a day.

### Agents by hand

- **Graphical setup**: run the new `HvPortal.Agent.Setup.exe` on the host. It recognizes the installation and keeps the
  connection to the gateway (no token), the account and, if you choose *Keep the folders set now*, the storage folders.
- **Script**: extract the new package into a **new** folder, then run it without a token:

  ```powershell
  Expand-Archive .\HvPortal.Agent-win-x64.zip C:\HvPortal\agent-package-0.7.0
  cd C:\HvPortal\agent-package-0.7.0\installer
  .\Install-Agent.ps1 -Source ..\agent        # add -DisableRemoteUpdate if you do not want updates from the portal
  ```

  The enrollment in `C:\ProgramData\HvPortal\Agent` is kept, and the previous binaries are restored if the service does
  not start. To roll back, run the same command from the previous package folder.

The portal page **Guide > Update the Windows services** shows the commands for your installation and the agent
version of every host.

## 10. Uninstalling

Run the uninstaller from an elevated PowerShell, from the `installer` folder of the release or of the agent package
(each setup can extract it: `HvPortal.Agent.Setup.exe --extract C:\HvPortal\files`).

```powershell
.\Uninstall-HvPortal.ps1 -Component Agent      # on a host; then remove the host in the portal
.\Uninstall-HvPortal.ps1 -Component Gateway    # on the gateway server
```

- The agent's uninstallation removes the service, its membership of *Hyper-V Administrators*, the updater task, the
  dedicated local account (only if the installer created it), the binaries and the HvPortal firewall rules.
- The gateway's uninstallation removes the service, the binaries and the firewall rules.
- The data directories (`C:\ProgramData\HvPortal\Agent`, `C:\ProgramData\HvPortal\Gateway`) are **kept** unless you
  add `-RemoveData`. Before removing the gateway data, create a portal backup or export the keys.
- VMs, disks, templates, restore points and the storage folders are never removed.
- Docker: remove the stack. The data stays in the `hvportal-data` volume until you delete it.

## 11. Troubleshooting

Logs: *Event Viewer > Windows Logs > Application*, sources **HvPortal Gateway** and **HvPortal Agent**. The setup logs
are in `C:\ProgramData\HvPortal\SetupLogs`. On a host, `& 'C:\Program Files\HvPortal\Agent\HvPortal.Agent.exe' status`
shows the state of the agent.

| Symptom | What to check |
|---|---|
| The setup stops with an error | The last lines in the setup window, and the log in `C:\ProgramData\HvPortal\SetupLogs` |
| "Port 443 is already in use" | IIS or another HTTP.sys listener uses 443 (shown as process *System*). Choose another port or stop it. |
| The agent does not connect | From the host: `Test-NetConnection <gateway> -Port 8444`. The name used must be among the gateway's public names, and the host's address must be allowed by the gateway's firewall rule. |
| "CA fingerprint does not match" | Stop. Check that you are reaching the right gateway, and compare the fingerprint in the portal through a channel you trust. |
| Enrollment refused | The token expired (15 minutes) or was already used: create a new one in the portal. |
| *Hyper-V access* is `AccessDenied` | `Get-LocalGroupMember -SID S-1-5-32-578` must list the agent's account. Restart the agent service. |
| "Access denied" when creating VMs, uploading or backing up | NTFS *Modify* for the agent's account on the folder ([5g](#5g-folder-permissions)) |
| A folder is "outside the storage folders" | The host was installed with `-StorageFolders`: choose a folder inside the list, or change the list on the host ([5d](#5d-storage-folders)). |
| `agent_outdated` (501) | The host's agent is older than the feature needs: 0.7.0 for backups, VM moves, host health and the VLAN of new network adapters. Update the agent ([section 9](#9-updating)). |
| An update from the portal is refused | The reason is shown: agent older than 0.7.0, updater task missing, unsigned package, or a different signer. See [section 9](#9-updating) and `C:\ProgramData\HvPortal\Agent\update\last-result.json`. |
| A task shows *Not verified* or *Unknown outcome* | Hyper-V did not confirm the final state in time, or the connection was lost after the command was sent. Check the VM before retrying: a task never claims success without evidence. |
| A move fails | Read the Hyper-V error in the task, then check [section 8](#8-before-moving-vms-between-hosts). |
| No administrator can sign in | On the gateway server (elevated): `HvPortal.Gateway.exe admin recover-account --user <name> --reset-mfa` |

More cases are listed in [docs/troubleshooting.md](docs/troubleshooting.md).
