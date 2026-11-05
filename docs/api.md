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
| GET `/jobs`, GET `/jobs/{id}`, POST `/jobs/{id}/cancel` | jobs | own jobs, or `jobs.read.all` / `jobs.cancel.all` |
| GET `/system/info` | version, database, telemetry state | full session |
| `/hubs/events` (SignalR) | `inventoryChanged {hostId}`, `jobChanged {jobId,status,progress,targetId}` | full session; server → client only |

Agent port only (not in OpenAPI): `GET /agent/v1/ca`, `POST /agent/v1/enroll` (rate-limited),
`GET /agent/v1/channel` (WebSocket, client certificate required).

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
