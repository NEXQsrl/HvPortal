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
| GET/POST `/users`, POST `/users/{id}/{disable,enable,reset-password,reset-mfa}` | users | `users.manage` (+ re-auth for writes) |
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
| GET `/vms/{id}/config` | live configuration + `can {compute,storage,network}` + switches + ISO library + options | `inventory.read` |
| PATCH `/vms/{id}/config` (only changed fields) | configuration job `vm.config/Update` | `vm.config.compute` / `.storage` / `.network` per field; 409 `requires_off` (vCPU, dynamic memory toggle, Secure Boot); ISO must be in the host library |
| GET `/vms/{id}/checkpoints` | checkpoint tree (live) | `inventory.read` |
| POST `/vms/{id}/checkpoints` `{name?}` | create (job) | `vm.checkpoint.create` |
| POST `/vms/{id}/checkpoints/{cid}/apply` `{confirmName}` | apply (job) | `vm.checkpoint.apply`; VM Off/Saved; typed VM name |
| POST `/vms/{id}/checkpoints/{cid}/delete` `{includeChildren}` | delete/merge (job) | `vm.checkpoint.apply` |
| GET `/hosts/{id}/defaults` | wizard data: host capacity, default paths, switches, ISO library, existing names | `vm.create` on host |
| POST `/hosts/{id}/vms` `{vm, groupId?}` | create VM (job `vm.create/New`, serialized per host); optional `vm.location` (local folder; `invalid_location`, `location_not_allowed`, 501 `agent_outdated` before agent 0.3.1) | `vm.create` on host; group placement needs `groups.manage` (+ access-change rule) |
| GET `/hosts/{id}/switches` · `/hosts/{id}/disks` | virtual switches · VM virtual disks (live) | host visibility; VM names/disks filtered to readable VMs |
| GET `/hosts/{id}/folders?path=&isos=` | folder browser (agent 0.3.1+): drives when `path` is empty, else sub-folders (system/hidden/linked excluded) and, with `isos=true`, `.iso` files; 501 `agent_outdated` on older agents | folders: `vm.create` on host or `hosts.manage` · ISO files: `hosts.manage` |
| GET `/hosts/{id}/isos` · PUT `/hosts/{id}/iso-library` `{paths}` | ISO files · library folders (local absolute paths, max 16, no UNC/`..`) | read: host read / `vm.create` / `vm.config.storage` on a VM of the host · write: `hosts.manage` |
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
