# Troubleshooting

Diagnostic logs: *Event Viewer → Windows Logs → Application*, sources **HvPortal Gateway** and **HvPortal Agent**.
Every API response carries `X-Correlation-Id`; the same id appears in audit entries and job details.

| Symptom | Likely cause | What to do |
|---|---|---|
| Host *Hyper-V access* = `AccessDenied` | agent identity not in *Hyper-V Administrators*, or the change is not effective yet | `Get-LocalGroupMember -SID S-1-5-32-578` must list `NT SERVICE\HvPortalAgent`; restart the agent service. Note: a non-privileged caller gets an **empty** WMI result, not an error — the agent reports this explicitly instead of "0 VMs". |
| `NotInstalled` / `ServiceStopped` | Hyper-V role missing or vmms stopped | `Get-Service vmms`; install the role. |
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
