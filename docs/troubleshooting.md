# Troubleshooting

Diagnostic logs: *Event Viewer → Windows Logs → Application*, sources **HvPortal Gateway** and **HvPortal Agent**.
Every API response carries `X-Correlation-Id`; the same id appears in audit entries and job details.

| Symptom | Likely cause | What to do |
|---|---|---|
| Host *Hyper-V access* = `AccessDenied` | agent identity not in *Hyper-V Administrators*, or the change is not effective yet | `Get-LocalGroupMember -SID S-1-5-32-578` must list `NT SERVICE\HvPortalAgent`; restart the agent service. Note: a non-privileged caller gets an **empty** WMI result, not an error — the agent reports this explicitly instead of "0 VMs". |
| `NotInstalled` / `ServiceStopped` | Hyper-V role missing or vmms stopped | `Get-Service vmms`; install the role. |
| Host page: *The host does not answer* (`wmi_unreachable`) | agent 0.9.4+: WMI of the host cannot be reached (RPC server unavailable, name not resolved) | host on and resolvable; through a management agent: firewall rule group *Windows Management Instrumentation* (TCP 135 + dynamic RPC); host with its own agent: service `Winmgmt` running. The original error is under *Original error* and, complete, in the agent log. |
| *WMI or its Hyper-V provider is not available* (`wmi_provider_unavailable`) | WMI service or provider stopped, disabled or failed to load; Hyper-V namespace missing | `Get-Service Winmgmt, vmms`; restart *Windows Management Instrumentation*; `winmgmt /verifyrepository`. |
| *The host did not answer in time* (`wmi_timeout`) | host overloaded or packets lost; a remote host that gave no inventory in 2 minutes | wait for the next inventory; check the load of the host and the network. Nothing is known about a request that timed out. |
| *The host refused the credentials* (`wmi_authentication_failed`) | wrong user name or password, account locked, disabled or expired (hosts through a management agent) | Host → Actions → *Connection and credentials*. |
| *Access denied* (`wmi_access_denied`) | the account is accepted but has no right | own agent: `NT SERVICE\HvPortalAgent` in *Hyper-V Administrators*, restart the service; management agent: administrator of the host, `LocalAccountTokenFilterPolicy` for local accounts. |
| Operation fails with `host_busy` | agent 0.9.4+: more operations than `Agent:Operations:MaxConcurrent` (8) were running on the host for the whole time the operation could wait; it was **not started** | retry; if it happens often, look at the totals in the agent log (*Operations script totals*) and raise the limit, or find what queues so many operations (`docs/performance-powershell.md`). |
| Backup > *Browse files*: `file_restore_right_missing`, or (agent before 0.9.5) `A required privilege is not held by the client (0x80070522)` after a long copy | attaching a backup disk to the host needs the Windows right *Perform volume maintenance tasks*, which *Hyper-V Administrators* does not give | on the host, as administrator: `.\Install-Agent.ps1 -Source ..\agent -FileRestore Enabled` (setup: *File restore from backups* on the service account page). By hand: `secpol.msc` > Local Policies > User Rights Assignment > *Perform volume maintenance tasks* > add `NT SERVICE\HvPortalAgent`, then restart the service. |
| Storage: `protected_file` / `protected_content` | virtual disks, checkpoints and VM configuration files (and folders that contain them) are never renamed or deleted in the file explorer | delete the VM, checkpoint, template or restore point they belong to; move a VM with *Move storage*. |
| Storage: `path_in_use` | the folder is, or contains, the template library, an ISO library, the backup staging folder or the folder of a registered VM | change that setting first, or work on what is inside. |
| Storage: `folder_not_empty` | only empty folders are deleted (hidden files count) | delete or move the content first. |
| Cluster shows *Unknown* with "no node agent is connected" or "not read for too long" | 0.9.4: a stored status is not shown as healthy when it cannot be confirmed now | bring a node agent online; the next monitor pass reads the cluster. |
| Storage job shown as *no longer reported, outcome not verified* | Windows removed the job from `Get-StorageJob` while the portal last saw it running | check the health of the virtual disks on the Storage tab; the portal does not know how the job ended. |
| Update page: *The update was not installed* | the updater refused the setup (signature, checksum, version) or the new version did not start and the previous one was restored | the reason is on the page and in *Settings > System > Updates*; on the server: `<data>\update\last-result.json`. |
| Agent shows *offline* | firewall/DNS/TLS | from the host: `Test-NetConnection <gateway> -Port 8444`; Application log of the agent; `HvPortal.Agent.exe status`. |
| Enrollment: "CA fingerprint does not match" | wrong gateway, MITM, or copied fingerprint mismatch | do not continue; verify the fingerprint in the portal through a trusted channel. |
| Enrollment: `enrollment_rejected` | token expired (15 min), already used, or wrong host | create a new token (Host → *New enrollment token*). |
| Agent logs "name mismatch" / TLS errors | the URL host name is not in the gateway certificate | reinstall the gateway with `-PublicNames` including that name (the certificate is re-issued at start). |
| Agent logs "certificate expired" | agent offline for > 90 days | re-enroll with a new token. |
| Browser: 403 `origin_mismatch` | reverse proxy or different host name rewriting `Origin` | access the gateway directly or keep host names consistent. |
| Browser: 400 `csrf_invalid` | stale page after restart / cookie cleared | reload the page. |
| 403 `mfa_enrollment_required` | policy requires MFA for this account | complete MFA enrollment (the UI redirects automatically). |
| 403 `reauthentication_required` | critical action older than 5 min after last password entry | confirm the password in the dialog. |
| 429 `throttled` | repeated failures from this address/account | wait the indicated seconds; check *Audit* for `auth.suspicious`. |
| Locked-out last administrator | lost password/MFA | on the gateway server (elevated): `HvPortal.Gateway.exe admin recover-account --user <name> --reset-mfa`. |
| Setup token missing | setup already completed, or service not started | `Get-Service HvPortalGateway`; the token is regenerated at each start until setup completes. |
| Job `Unverified` | Hyper-V accepted the request but the end state was not observed in time (guest slow to shut down, restart heuristic) | check the VM; the job never claims success without evidence. |
| Job `Unknown` | channel lost or gateway restarted after the command was sent and the state is inconclusive | check the VM on the host before retrying. |
| Job `Failed` with `host_unreachable` | agent offline before dispatch: **the command was not sent** | bring the agent online and retry. |
| Gateway does not start: "reparse point" | data directory is a junction/symlink | use a real local directory. |
| Gateway does not start after restore: `CryptographicException` key not in ring | keys not imported | `admin import-keys` before starting. |
| `admin` commands: "Run this command from an elevated prompt" | CLI requires administrator | open PowerShell as administrator. |
| VM settings / creation / checkpoints fail with "The term 'Get-VM' is not recognized" or "module 'Hyper-V' was not loaded" | Hyper-V PowerShell module missing on the host | `Install-WindowsFeature Hyper-V-PowerShell` (inventory, power and console keep working without it). |
| Operations fail with "...is not digitally signed" / "running scripts is disabled" | Group Policy sets PowerShell execution policy (MachinePolicy) to AllSigned/Restricted, which overrides `-ExecutionPolicy Bypass` | sign the release (Authenticode) or allow the agent's `Ops\HvPortal.Ops.ps1` by policy. |
| Operations fail with "Cannot create type. Only core types are supported in this language mode" | PowerShell Constrained Language Mode (WDAC/AppLocker script enforcement) | not supported yet: allow-list the signed script in the policy. |
| 501 `agent_outdated` | agent older than 0.3 | update the agent package on the host (re-run `Install-Agent.ps1` without `-Gateway`; enrollment is kept). |
| 409 `requires_off` | vCPU count, dynamic memory on/off, Secure Boot, automatic stop action (or, on Generation 1, a new network adapter or disk) changed on a running VM | shut down the VM and apply again. |
| 400 `iso_outside_library` | ISO path not inside the host ISO library | Host → Settings → ISO library: add the folder (local path such as `D:\ISO`). |
| 409 `quota_exceeded` | the operation would exceed a quota of the VM's group or of a group above it (VMs, vCPU, memory, disk, private networks); the message names the group and the numbers | free resources in the group, or ask an administrator (`groups.manage` + `hosts.manage`) to raise the limits (group page > Limits). Queued creations count too. |
| 409 `network_not_allowed` / `storage_not_allowed` | the switch or the location is not among those allowed for the group | choose an allowed network / one of the group's storage folders, or have the limits changed. A new disk needs the VM to be stored inside the group's storage. |
| 409 `quota_memory_maximum_required` | the group has a memory limit and the VM uses dynamic memory without a maximum | set the maximum memory of the VM: it is the value counted against the limit. |
| 409 `quota_usage_unknown` | the disk space used by the group cannot be read (host of some of its VMs unreachable and sizes never read) | bring the host online, or open Storage for that host once, then retry. |
| Network job `Reconciling` then verified | creating/removing an external switch dropped the agent's connection for a few seconds | expected: the outcome is read from the host when the agent reconnects. |
| 403 `group_access_change_requires_roles_manage` / `permission_amplification` | the group move/membership change would grant or lift role assignments | ask an administrator with `roles.manage`, or use a group without role assignments. |
| Console shows "The VM is not running" | expected while the VM is off/saved | start it from the console toolbar; BIOS/UEFI appears as soon as it powers on. |
| Console mouse does nothing | synthetic mouse requires guest integration services | use the keyboard (Tab/arrows) until the guest has started its services. |
| Console closes after a while | 15 minutes without input or 8 hours maximum; permission or session revoked | reopen the console. |