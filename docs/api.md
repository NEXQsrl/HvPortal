# API

- Machine-readable description: `GET /openapi/v1.json` (authenticated). A generated copy is committed at
  [openapi/v1.json](openapi/v1.json) (regenerate: see `OpenApiTests`).
- Base path `/api/v1`, JSON, enums as strings. Errors are RFC 9457 problem documents with a stable `code`.
- Authentication: session cookie set by the login endpoints. **Every non-GET request needs the header
  `X-CSRF-TOKEN`** obtained from `GET /api/v1/auth/csrf` (refresh it after sign-in).
- Authorization: enforced per endpoint and per resource; unauthorized resources return 404.

## Endpoints (Milestone 1)

| Method & path | Purpose | Permission / notes |
|---|---|---|
| GET `/auth/state` | setup required? authenticated? | anonymous |
| GET `/auth/csrf` | anti-forgery token | anonymous |
| POST `/setup` | first administrator with one-time token | anonymous, only while no user exists |
| POST `/auth/login` | password step → `ok` or `mfa_required` + challenge | anonymous, rate-limited, progressive delay |
| POST `/auth/login/mfa` | TOTP code or recovery code | anonymous + challenge |
| POST `/auth/recover` | password reset with admin-CLI recovery token | anonymous |
| POST `/auth/logout` · GET `/auth/me` | session | any session |
| POST `/auth/password` · POST `/auth/reauth` | change password · step-up | any session |
| GET `/auth/sessions` · DELETE `/auth/sessions/{id}` | own sessions | full session |
| POST `/mfa/totp/enroll` · `/mfa/totp/confirm` | TOTP enrollment | any session + recent re-auth |
| POST `/mfa/totp/disable` · `/mfa/recovery-codes` | MFA management | full session + recent re-auth |
| GET/POST `/users`, POST `/users/{id}/{disable,enable,reset-password,reset-mfa}` | users; POST accepts `authSource` (`local`/`entra`), `requireMfa` (local, default true), `roleId` + `scopeType`/`scopeId` | `users.manage` (+ re-auth for writes); role at creation also `roles.manage` and no permission amplification |
| POST `/users/{id}/delete` `{confirmName}` | delete a user for good (sessions, MFA data, role assignments); `confirmation_required`, `self_management_forbidden`, `target_more_privileged`, 409 `last_platform_admin` | `users.manage` + re-auth; audited with the removed assignments |
| GET/PUT `/settings/entra`, POST `/settings/entra/test` | Entra ID app registration (tenant, client id, write-only client secret, cloud); metadata test | `settings.manage` (+ re-auth for PUT) |
| GET `/auth/entra/start?returnUrl=&mode=reauth` · GET `/auth/entra/callback` | Entra ID sign-in / re-authentication (authorization code + PKCE) | anonymous; state cookie |
| GET `/permissions`, GET/POST/PUT/DELETE `/roles` | roles | `roles.manage` (+ re-auth) |
| GET/POST/DELETE `/role-assignments` | assignments | `roles.manage` (+ re-auth), anti-escalation |
| GET/PUT `/settings/security` | security policy | `settings.manage` (+ re-auth) |
| GET `/audit`, GET `/audit/export`, POST `/audit/verify` | audit | `audit.read` |
| GET `/hosts`, GET `/hosts/{id}` | hosts | visible if `hosts.manage` or any readable VM / host scope |
| POST `/hosts`, `/hosts/{id}/enrollment-token`, `/hosts/{id}/revoke` | enrollment lifecycle | `hosts.manage` (+ re-auth) |
| POST `/hosts/{id}/refresh` | collect inventory now | `inventory.read` on host |
| GET `/vms?hostId&search&state`, GET `/vms/{id}`, GET `/vms/{id}/events` | inventory | `inventory.read` |
| POST `/vms/{id}/power` `{operation, confirmName?, idempotencyKey?}` | power job (202 + jobId) | `vm.power`; `vm.power.force` for TurnOff/Reset (typed name required) |
| GET `/jobs`, GET `/jobs/{id}`, POST `/jobs/{id}/cancel` | jobs (`resultJson` carries the host result, e.g. created VM id) | own jobs, or `jobs.read.all` / `jobs.cancel.all` |
| GET `/system/info` | version, database, telemetry state | full session |
| `/hubs/events` (SignalR) | `inventoryChanged {hostId}`, `jobChanged {jobId,status,progress,targetId}`, `groupsChanged {}` | full session; server → client only |

Agent port only (not in OpenAPI): `GET /agent/v1/ca`, `POST /agent/v1/enroll` (rate-limited),
`GET /agent/v1/channel` (WebSocket, client certificate required).

## Endpoints added in v0.3

Live reads call the agent (503 `host_unreachable`, 504 `host_timeout`, 502 `host_operation_failed` with the Hyper-V
message, 501 `agent_outdated`). Changes are jobs (202 + `jobId`), re-authorized and re-validated when they run.

| Method & path | Purpose | Permission / notes |
|---|---|---|
| GET `/groups` | visible groups (`parentId`, `vmIds`) + `canManage` | groups containing visible VMs and their ancestors |
| POST `/groups` `{name, description?, parentId?}` · PATCH `/groups/{id}` `{name?, description?, parentId? \| moveToRoot}` · DELETE `/groups/{id}` | folders | `groups.manage`; cycles/depth refused; delete only empty of subgroups and assignments; moves are access changes (ADR-015) |
| POST `/groups/{id}/members` · `/groups/{id}/members/remove` `{vmIds}` | membership | `groups.manage` (+ `roles.manage` and held permissions when the change alters access) |
| GET `/groups/{id}/policy?live=` · PUT `/groups/{id}/policy` | limits of the group (ADR-024): own policy, limits and usage of the group and of every group above it, enabled hosts with their allowed switches and storage paths, the group's private networks. PUT body = `{maxVms?, maxProcessors?, maxMemoryMb?, maxDiskGb?, maxNetworks?, hosts?, networks?: [{hostId,name}], storage?: [{hostId,path}]}` (empty = no limits) | GET: `inventory.read` on the group (404 otherwise); PUT: `groups.manage` + `hosts.manage`; audited |
| GET `/vms/{id}/config` | live configuration + `can {compute,storage,network,annotate}` + switches + ISO library + options | `inventory.read` |
| PATCH `/vms/{id}/config` (only changed fields) | configuration job `vm.config/Update` | `vm.config.compute` / `.storage` / `.network` per field; 409 `requires_off` (vCPU, dynamic memory toggle, Secure Boot, automatic stop action; on Generation 1 also new network adapter and new disk); ISO must be in the host library |
| GET `/vms/{id}/checkpoints` | checkpoint tree (live) | `inventory.read` |
| POST `/vms/{id}/checkpoints` `{name?}` | create (job) | `vm.checkpoint.create` |
| POST `/vms/{id}/checkpoints/{cid}/apply` `{confirmName}` | apply (job); a running VM is turned off first by agents 0.5.0+ (409 `invalid_state` with older agents) | `vm.checkpoint.apply`; typed VM name |
| POST `/vms/{id}/checkpoints/{cid}/delete` `{includeChildren}` | delete/merge (job) | `vm.checkpoint.apply` |
| GET `/hosts/{id}/defaults?groupId=` | wizard data: host capacity, default paths, switches, ISO library, existing names; with `groupId` only the group's allowed switches and `storageRoots` | `vm.create` on host, or on the group when its limits enable the host |
| POST `/hosts/{id}/vms` `{vm, groupId?}` | create VM (job `vm.create/New`, serialized per host); optional `vm.location` (local folder; `invalid_location`, `location_not_allowed`, 501 `agent_outdated` before agent 0.3.1) | `vm.create` on host; group placement needs `groups.manage` (+ access-change rule) |
| GET `/hosts/{id}/switches` · `/hosts/{id}/disks` | virtual switches · VM virtual disks (live) | host visibility; VM names/disks filtered to readable VMs |
| POST `/hosts/{id}/switches` `{name, type, netAdapterName?, allowManagementOs?, notes?, groupId?}` · POST `/hosts/{id}/switches/delete` `{switchId, name, confirmName}` · GET `/hosts/{id}/net-adapters` | create / remove a virtual switch (job `host.network`, agent 0.5.0+) · physical adapters. With `groupId`: private network of the group, within its quota (`folder_networks_are_private`, 409 `quota_exceeded`). 409 `switch_exists`, 501 `agent_outdated` | `network.manage` on the host, or on the group for its private networks; adapters: host only; typed name to delete; audited |
| GET `/hosts/{id}/folders?path=&isos=` | folder browser (agent 0.3.1+): drives when `path` is empty, else sub-folders (system/hidden/linked excluded) and, with `isos=true`, `.iso` files; 501 `agent_outdated` on older agents | folders: `vm.create` on host or `hosts.manage` · ISO files: `hosts.manage` |
| GET `/hosts/{id}/isos` · PUT `/hosts/{id}/iso-library` `{paths}` | ISO files · library folders (local absolute paths, max 16, no UNC/`..`) | read: host read / `vm.create` / `vm.config.storage` on a VM of the host · write: `hosts.manage` |
| GET `/hosts/{id}/vm-groups` | native Hyper-V VM groups (Get-VMGroup) with members, owner portal group, mirroring status; `supported:false` before agent 0.3.2 | host read |
| POST `/hosts/{id}/vm-groups/sync` | force the mirroring now | `groups.manage` |
| POST `/hosts/{id}/vm-groups/import` | import native groups not owned by the portal as portal groups, then mirror | `hosts.manage` + `groups.manage` |
| GET `/hosts/{id}/files?path=&groupId=` | drives (no path) or folders + files of a folder (agent 0.3.3+). With `groupId` (also on download/upload): only the storage paths of the group, 403 `storage_not_allowed` outside | `host.files.read` on host, or on the group (ADR-024) |
| GET `/hosts/{id}/files/download?path=` | file download (streamed, 4 MB chunks); 409 `file_in_use` for files open for writing | `host.files.read` on host; audited |
| POST `/hosts/{id}/files/upload?folder=&name=&overwrite=` | raw body (`application/octet-stream`, Content-Length required); 409 `file_exists` unless `overwrite=true`; returns path, size, SHA-256 | `host.files.write` on host; audited |
| GET `/management-agents` | management agents (status, connected, server, version, number of hosts) | `hosts.manage` global |
| POST `/management-agents` `{displayName, description?}` · POST `/management-agents/{id}/enrollment-token` | create + one-time token / new token (install with `-Management`) | `hosts.manage` global + re-auth |
| POST `/management-agents/{id}/revoke` · POST `/management-agents/{id}/delete` `{confirmName}` | revoke certificates and disconnect · delete (409 `management_agent_in_use` while hosts use it) | `hosts.manage` global + re-auth |
| POST `/hosts/remote` `{displayName, managementAgentId, computerName, userName, password, useHttps}` | remote host reached by a management agent (ADR-022); `invalid_computer_name`, `invalid_user_name`, `user_name_needs_host` (IP without `HOST\user`), `invalid_password`, `invalid_management_agent`, 409 `host_exists` | `hosts.manage` global + re-auth; audited without the password |
| PUT `/hosts/{id}/remote` `{managementAgentId, computerName, userName, password?, useHttps}` | change connection/account/agent; `password` omitted or null keeps the stored one; re-enables a revoked remote host | `hosts.manage` global + re-auth; audited (`passwordChanged`) |
| POST `/settings/backup/export` `{passphrase}` | sealed `.hvpbak` (DB snapshot + key ring + CA) | every privileged permission + re-auth; audited |
| GET `/settings/backup/auto` · POST `/settings/backup/auto/run` | automatic local copies of the database (ADR-023): schedule, folder, last error, files · copy now (verified with `PRAGMA integrity_check`) | `settings.manage` (+ re-auth to run); audited |
| POST `/settings/backup/restore[?apply=true]` | raw body = backup file; headers `X-HvPortal-Passphrase`, `X-HvPortal-Confirm: RIPRISTINA` (apply); preview without `apply` | every privileged permission + re-auth |
| GET / DELETE `/settings/backup/restore` · POST `/settings/backup/restart` | staged restore state / cancel / restart the service to apply | `settings.manage` (GET) · every privileged permission + re-auth |
| POST `/vms/{id}/console/tickets` | one-time console ticket (30 s) | `vm.console` |
| GET `/vms/{id}/console/ws` (WebSocket) | console stream; first message `{"t":"auth","ticket":…}` | session cookie + same Origin + valid ticket ([console.md](console.md)) |
| GET `/console/sessions` | open console sessions | `audit.read` |

## Example (PowerShell)

```powershell
$s = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$csrf = (Invoke-RestMethod https://gw:8443/api/v1/auth/csrf -WebSession $s).token
Invoke-RestMethod -Method Post https://gw:8443/api/v1/auth/login -WebSession $s -Headers @{ 'X-CSRF-TOKEN' = $csrf } `
  -ContentType application/json -Body (@{ userName = 'op'; password = '<passphrase>' } | ConvertTo-Json)
$csrf = (Invoke-RestMethod https://gw:8443/api/v1/auth/csrf -WebSession $s).token   # token is bound to the signed-in user
Invoke-RestMethod -Method Post "https://gw:8443/api/v1/vms/$vmId/power" -WebSession $s -Headers @{ 'X-CSRF-TOKEN' = $csrf } `
  -ContentType application/json -Body (@{ operation = 'Start'; idempotencyKey = [guid]::NewGuid().ToString() } | ConvertTo-Json)
```
Note: `WebRequestSession` keeps headers passed with `-Headers`; clear `$s.Headers` between unrelated calls.
Automation accounts with API tokens (non-interactive) are planned for Milestone 2.
