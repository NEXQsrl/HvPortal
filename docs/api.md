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
| POST `/auth/reauth/code` · POST `/auth/reauth {confirmationCode}` | Microsoft accounts: random 6-character code to type again (2 min, single use, 5 attempts) · step-up with it (ADR-038) | any session (local accounts: `password_required`) |
| POST `/auth/logout?reason=idle` | sign-out after the inactivity warning (15 + 3 minutes); audited with `reason: idle` | any session |
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
| GET `/audit?skip=&take=&action=&outcome=&q=&from=&to=`, GET `/audit/export`, POST `/audit/verify` | audit. List filters: `action` = one prefix or several separated by commas, `outcome`, period (`from` / `to`, ISO dates), `q` = free text (action, actor, address, details, correlation id, and the current names of hosts, VMs, users and groups). Each entry carries `resourceName` and `resourceExists` (false = the name comes from the entry, the resource is gone) | `audit.read` |
| GET `/hosts`, GET `/hosts/{id}` | hosts | visible if `hosts.manage` or any readable VM / host scope |
| POST `/hosts`, `/hosts/{id}/enrollment-token`, `/hosts/{id}/revoke` | enrollment lifecycle | `hosts.manage` (+ re-auth) |
| POST `/hosts/{id}/refresh` | collect inventory now | `inventory.read` on host |
| GET `/vms?hostId&search&state`, GET `/vms/{id}`, GET `/vms/{id}/events` | inventory | `inventory.read` |
| POST `/vms/{id}/power` `{operation, confirmName?, idempotencyKey?}` | power job (202 + jobId) | `vm.power`; `vm.power.force` for TurnOff/Reset (typed name required) |
| GET `/jobs`, GET `/jobs/{id}`, POST `/jobs/{id}/cancel` | jobs (`resultJson` carries the host result, e.g. created VM id) | own jobs, or `jobs.read.all` / `jobs.cancel.all` |
| GET `/system/info` | version, database, telemetry state | full session |
| `/hubs/events` (SignalR) | `inventoryChanged {hostId}`, `jobChanged {jobId,status,progress,targetId}`, `groupsChanged {}` | full session; server → client only |

Agent port only (not in OpenAPI): `GET /agent/v1/ca`, `POST /agent/v1/enroll` (rate-limited),
`GET /agent/v1/channel` (WebSocket, client certificate required), `GET /agent/v1/package` (v0.7.0: the agent package
for agents updating themselves; client certificate of an active, recorded, non-revoked agent, otherwise 403; audited
`agent.package.download`).

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
| POST `/vms/{id}/delete` `{confirmName, deleteDisks?, idempotencyKey?}` | delete the VM (job `vm.delete`, Off or Saved). `deleteDisks: true` (agent 0.7.0+, operation `DeleteWithDisks`, ADR-037) also deletes the disk files used only by that VM inside the allowed storage folders; 501 `agent_outdated` with an older agent; the job message lists deleted and kept files | `vm.delete` + re-auth, typed name; audited with `deleteDisks` |
| GET `/vms/{id}/metrics?hours=1..240` · POST `/vms/{id}/metering` `{enabled, idempotencyKey?}` | resource use history (stored samples + live reading, summary, at most 600 points) · resource metering on/off (job `vm.metering`, agent 0.8.0+, ADR-039) | `inventory.read` on the VM · `vm.config.compute` on the VM |
| GET `/hosts/{id}/defaults?groupId=` | wizard data: host capacity, default paths, switches, ISO library, existing names; with `groupId` only the group's allowed switches and `storageRoots` | `vm.create` on host, or on the group when its limits enable the host |
| POST `/hosts/{id}/vms` `{vm, groupId?}` | create VM (job `vm.create/New`, serialized per host); optional `vm.location` (local folder; `invalid_location`, `location_not_allowed`, 501 `agent_outdated` before agent 0.3.1) | `vm.create` on host; group placement needs `groups.manage` (+ access-change rule) |
| GET `/hosts/{id}/switches` · `/hosts/{id}/disks` | virtual switches · VM virtual disks (live) | host visibility; VM names/disks filtered to readable VMs |
| POST `/hosts/{id}/switches` `{name, type, netAdapterName?, allowManagementOs?, notes?, groupId?}` · POST `/hosts/{id}/switches/delete` `{switchId, name, confirmName}` · GET `/hosts/{id}/net-adapters` | create / remove a virtual switch (job `host.network`, agent 0.5.0+) · physical adapters. With `groupId`: private network of the group, within its quota (`folder_networks_are_private`, 409 `quota_exceeded`). 409 `switch_exists`, 501 `agent_outdated` | `network.manage` on the host, or on the group for its private networks; adapters: host only; typed name to delete; audited |
| GET `/hosts/{id}/folders?path=&isos=` | folder browser (agent 0.3.1+): drives when `path` is empty, else sub-folders (system/hidden/linked excluded) and, with `isos=true`, `.iso` files; 501 `agent_outdated` on older agents | folders: `vm.create` on host or `hosts.manage` · ISO files: `hosts.manage` |
| GET `/hosts/{id}/isos` · PUT `/hosts/{id}/iso-library` `{paths}` | ISO files · library folders (local absolute paths, max 16, no UNC/`..`) | read: host read / `vm.create` / `vm.config.storage` on a VM of the host · write: `hosts.manage` |
| GET `/hosts/{id}/vm-groups` | native Hyper-V VM groups (Get-VMGroup) with members, owner portal group, mirroring status (`status {syncedAt, error, adoptedAt, autoImport}`: `adoptedAt` null with `autoImport` true = the groups already on the host are still to be imported automatically, ADR-027); `supported:false` before agent 0.3.2 | host read |
| POST `/hosts/{id}/vm-groups/sync` | force the mirroring now | `groups.manage` |
| POST `/hosts/{id}/vm-groups/import` | import native groups not owned by the portal as portal groups, then mirror (all of them, also the ones the automatic import of ADR-027 leaves alone) | `hosts.manage` + `groups.manage` |
| GET `/templates?hostId=&groupId=` | VM templates (ADR-028) of the hosts where the caller may create VMs (directly, or inside `groupId`) or manages templates: status (`Creating`, `Ready`, `Failed`, `Unverified`, `Deleting`), hardware of the model, disks, size, `can {deploy, manage, delete}`; `folderPath` only for who manages them | `vm.create` or `templates.manage` on the host (others get an empty list) |
| POST `/vms/{id}/template` `{name, description?}` | create a template from a VM that is off (job `template.capture`: the disks are copied, the VM is not changed); 409 `invalid_state`, `template_library_not_set`, `template_name_exists`; 501 `agent_outdated` before agent 0.6.0 | `templates.manage` on the host of the VM; audited |
| PATCH `/templates/{id}` `{name?, description?}` · POST `/templates/{id}/delete` `{confirmName}` | rename · delete the template and its files (job `template.delete`); 409 `template_busy` | `templates.manage` on the host; audited |
| PUT `/hosts/{id}/template-library` `{path}` | folder of the host where templates are kept (null clears it); 400 `invalid_template_library` | `hosts.manage`; audited |
| GET `/hosts/{id}/files?path=&groupId=` | drives (no path) or folders + files of a folder (agent 0.3.3+). With `groupId` (also on download/upload): only the storage paths of the group, 403 `storage_not_allowed` outside | `host.files.read` on host, or on the group (ADR-024) |
| GET `/hosts/{id}/files/download?path=` | file download (streamed, 4 MB chunks); 409 `file_in_use` for files open for writing | `host.files.read` on host; audited |
| POST `/hosts/{id}/files/upload?folder=&name=&overwrite=` | raw body (`application/octet-stream`, Content-Length required); 409 `file_exists` unless `overwrite=true`; returns path, size, SHA-256 | `host.files.write` on host; audited |
| GET `/downloads` · GET `/downloads/agent` · GET `/downloads/agent-setup` | agent package (zip: version, size, SHA-256, signature state) and, when the release provides it, the graphical setup of the agent (`agentSetup {fileName, sizeBytes, sha256}`, null otherwise) · the two files | `hosts.manage`; downloads audited |
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

## Endpoints added in v0.7.0 (agent protocol 9)

Operations on hosts need agent 0.7.0 (protocol 9): older agents get 501 `agent_outdated` and nothing is sent. Long
operations are jobs (202 + `jobId`) re-authorized and re-validated at execution; server-controlled fields (VM id,
destination host name, switch and VLAN of a network, repository path, credentials, point folder) are stamped by the
gateway from its records, never taken from the request. Codes below are the `code` of the problem document.

**Changed responses.** `GET /hosts`, `/hosts/{id}`: `agent7` (agent 0.7.0+ connected with Hyper-V access),
`storageFolders` (allowed folders; `[]` = not restricted, `null` = unknown / older agent), `storageFoldersEditable`
(remote hosts only), `backupStagingPath`, `updaterInstalled`, `lastUpdateResult`. `GET /vms/{id}`: `canMigrate`,
`canBackup`, `canRestore`. `GET /vms/{id}/config`: `networks` (portal networks of the host), `vlanOnAdd`.
`GET /hosts/{id}/defaults`: `networks` (only those on switches the caller may use), `vlanSupported`.
`POST /hosts/{id}/vms` accepts `vm.networkId`; `PATCH /vms/{id}/config` accepts `networkAdapterChanges[].networkId` and
`addNetworkAdapterNetworkId` (switch and VLAN replaced by the network's; 400 `unknown_network`).

### Networks, host health, host settings, VM move, folders

| Method & path | Purpose | Permission / notes |
|---|---|---|
| GET `/hosts/{id}/networks` · GET `/networks` | portal networks (ADR-030): `{id, hostId, hostName, name, switchName, vlanId, description, createdAt, canManage}` | host visibility (404 otherwise) |
| POST `/hosts/{id}/networks` `{name, switchName, vlanId?, description?}` | new network on an existing switch (`vlanId` 1–4094, null = untagged). 400 `invalid_network_name`, `invalid_switch_name`, `invalid_vlan`, `unknown_switch`, `description_too_long`; 409 `too_many_networks` (200 per host), `network_name_exists`, `network_exists` (same switch + VLAN) | `network.manage` on the host; audited `network.portal.create` |
| PUT `/networks/{id}` `{name, vlanId?, description?, updateAdapters}` | rename / change VLAN; with `updateAdapters` the adapters on the old switch + VLAN (read live, at most 50 VMs) are re-tagged by one `vm.config/Update` job per VM. Returns `{network, jobs, skipped}` (VMs without `vm.config.network`). 503 `host_unreachable` when the adapters cannot be read | `network.manage` on the host; audited `network.portal.update` |
| DELETE `/networks/{id}` | removes the portal record only (adapters keep switch and VLAN) | `network.manage` on the host; audited |
| GET `/hosts/{id}/health` | host health report (ADR-036): hardware, physical disks, volumes, storage pools, physical NICs, recent critical/error events, Hyper-V services, `errors`, overall `status` (`Ok`/`Warning`/`Critical`/`Unknown`). Live, up to 120 s | `inventory.read` on the **host** (404 otherwise) |
| PUT `/hosts/{id}/storage-folders` `{folders}` | allowed storage folders of a **remote** host (ADR-034), sent to its management agent; 409 `decided_on_the_host` for hosts with their own agent; 400 `invalid_storage_folder`, `too_many_storage_folders` (16), `duplicate_storage_folder` | `hosts.manage` global; audited `hosts.storage_folders.update` |
| PUT `/hosts/{id}/backup-staging` `{path}` | local folder for temporary backup copies (null clears it); 400 `invalid_staging_path` (drive roots and system folders refused), `location_not_allowed` | `hosts.manage` or `backup.manage` global; audited |
| POST `/vms/{id}/move` `{mode: "host" \| "storage", destinationHostId?, destinationPath?, includeStorage = true, idempotencyKey?}` | job `vm.migrate` (ADR-035): `host` = `Move-VM` to another portal host (with storage: `destinationPath` required, the VM goes to `<path>\<name>`), `storage` = `Move-VMStorage` on the same host. 400 `invalid_move_mode`, `invalid_destination_host`, `destination_path_required`, `destination_path_only_with_storage`, `invalid_location`, `location_not_allowed`; 409 `vm_missing`, `destination_unknown_name`, `host_not_allowed`, `storage_not_allowed` (group limits); 503 `host_unreachable` | `vm.migrate` on the VM + `vm.create` on the destination host; audited `vm.migrate.request` |
| POST `/hosts/{id}/files/folders` `{parent, name, groupId?}` | new folder `<parent>\<name>` in the file explorer (inside the allowed storage folders, never over an existing name). 400 `invalid_path`, `invalid_folder_name`, `location_not_allowed`, `path_too_long` | `host.files.write` on the host, or on the group within its storage paths; audited `host.files.folder.create` |

### Agent updates (ADR-029)

| Method & path | Purpose | Permission / notes |
|---|---|---|
| GET `/agents/updates` | `{gatewayVersion, package {version, sha256, sizeBytes, signed, selfSigned, catalogSigned, signer}, outdated, agents[]}`; each agent `{id, kind: host \| management, name, version, connected, protocol, outdated, updaterInstalled, lastUpdateResult, canUpdate, reason, runningJobId}`; `reason` = `not_connected`, `agent_too_old` (before 0.7.0), `updater_missing`, `package_unsigned` | `hosts.manage` global |
| POST `/hosts/{id}/agent/update` · POST `/management-agents/{id}/update` | job `agent.update` (one running per agent; `created:false` returns the running one). 409 `package_missing`, `up_to_date`, or the `reason` above | `hosts.manage` global (also at execution); audited `agent.update.request` |
| POST `/agents/update-outdated` | one job per agent that can be updated: `{queued[], skipped[{id, name, reason}]}` | `hosts.manage` global; audited |

### Backups (ADR-031)

"Backup area" = any of `backup.manage`, `backup.run`, `backup.restore` held at **Global** scope (403 + audit
otherwise). A restore point is evaluated on its VM while the VM exists (404 when the VM is not readable), on Global
afterwards. Repository passwords are write-only.

| Method & path | Purpose | Permission / notes |
|---|---|---|
| GET `/backup/overview` | last 24 h of runs, counts, protected / unprotected VMs, latest problems, recent runs | backup area |
| GET `/backup/repositories` | repositories with usage (`points`, `bytes`), `hasCredentials`, last check; `userName` only to `backup.manage` | backup area |
| POST `/backup/repositories` `{name, description?, path, hostId?, userName?, password?}` · PUT `/backup/repositories/{id}` (same body; the path never changes; `userName` empty = remove the credentials, `password` omitted = keep) | local folder of one host (`hostId` required, within its allowed storage folders) or SMB share (`\\server\share[\folder]`, no `$` shares, no host). 400 `invalid_repository_name`, `invalid_repository_path`, `host_required`, `share_has_no_host`, `credentials_only_for_shares`, `invalid_repository_credentials`, `location_not_allowed`; 409 `repository_name_exists` | `backup.manage` global; audited without the password |
| DELETE `/backup/repositories/{id}` | 409 `repository_in_use` (jobs), `repository_has_points` | `backup.manage` global; audited |
| POST `/backup/repositories/{id}/check` `{hostId?}` | readiness checks from one host (write test, free space, staging folder, VM checks); stored as last check | `backup.manage` global; audited |
| POST `/backup/repositories/{id}/rescan` `{hostId?}` | restore points found in the repository and unknown to the portal are added as `Unverified`: `{found, added}` | `backup.manage` global; audited |
| GET `/hosts/{id}/backup-readiness?repositoryId=` | readiness checks of a host (optionally with a repository) | `hosts.manage` or `backup.manage` global |
| GET `/backup/jobs` · POST `/backup/jobs` · PUT `/backup/jobs/{id}` · DELETE `/backup/jobs/{id}` | backup jobs. Body `{name, description?, repositoryId, enabled, scheduleKind: daily \| hourly \| manual, daysMask (bit 0 = Sunday), times ["HH:mm"], intervalHours (1–24), timeZoneId, retentionPoints (1–366), retentionDays?, checkpointType: Production \| ProductionOnly \| Standard, verifyAfterCopy, restoreTestDays (0–365), notifyOn: never \| failure \| warning \| always, vmIds[], groupIds[]}` (1–500 targets). The saver must hold `backup.run` on every target (400 `target_not_allowed`); runs execute as the user who last saved the job (`runsAs`). Delete keeps the restore points (no retention afterwards) | list: backup area; writes: `backup.manage` global; audited |
| POST `/backup/jobs/{id}/run` | run now (`{runId, total}`); 409 `run_in_progress` | `backup.manage` or `backup.run` global; audited `backup.job.run` |
| GET `/backup/runs?jobId=&limit=` · GET `/backup/runs/{id}` | runs (status Running / Success / Warning / Failed, counts, message) · one run with its points | backup area |
| GET `/backup/points?vmId=&repositoryId=&jobId=` · GET `/backup/points/{id}` | restore points (`status` Creating / Ok / Failed / Unverified / Deleting, size, checkpoint type requested and used, verify and test results, disks, `keep`) · one point with its manifest. Points of VMs the caller cannot read are left out | backup area (list) / readable point |
| POST `/backup/points/{id}/verify` | job `backup.verify` (size + SHA-256 of every file) on a host that can read the repository; 409 `backup_point_not_usable`; 503 `host_unreachable` | `backup.run` on the point; audited |
| POST `/backup/points/{id}/delete` `{confirmName}` | job `backup.remove` (typed VM name); 409 `backup_point_busy` | `backup.manage` global; audited `backup.point.delete.request` |
| POST `/backup/points/{id}/keep` `{keep}` | exclude from retention | `backup.manage` global; audited |
| POST `/backup/points/{id}/restore` `{mode: NewVm \| Replace \| Test, hostId?, name?, location?, network: Same \| Disconnected \| Switch, switchName?, startAfter, confirmName?, idempotencyKey?}` | job `backup.restore`. NewVm / Test: on `hostId` (default the point's host), `vm.create` there; Test is always disconnected and never started after. Replace: the original VM (typed name; 409 `vm_not_found` when it is gone). 400 `invalid_restore_mode`, `invalid_vm_name`, `invalid_location`, `location_not_allowed`, `invalid_restore_network`, `confirmation_required`; 409 `backup_point_not_usable`, `vm_name_exists`, `staging_required`, `repository_other_host`, `idempotency_conflict` | `backup.restore` on the point (+ `vm.create` on the host for NewVm / Test); audited `backup.restore.request` |
| POST `/backup/points/{id}/restore-disk` `{disk, hostId?, destinationFolder, attachVmId?}` | job `backup.restoredisk`: copy one disk of the point to a host folder, optionally attached to a VM (SCSI). 400 `invalid_disk`, `invalid_vm`, `invalid_location`, `location_not_allowed` | `backup.restore` on the point + `vm.config.storage` on the VM to attach to, or `host.files.write` on the host; audited |
| POST `/backup/points/{id}/mount` `{disk, hostId?}` | attach a disk of the point read-only to a host with its own agent (file restore): `{mountId, hostId, volumes[], expiresUtc, staged}`. 409 `not_supported` (remote host), `backup_point_not_usable`, `staging_required`; 503 `host_unreachable`. May take long when a staged copy is made (up to 1 h) | `backup.restore` on the point; audited `backup.files.mount` |
| GET `/backup/mounts` · GET `/backup/mounts/{mountId}/files?path=` · GET `/backup/mounts/{mountId}/download?path=` · POST `/backup/mounts/{mountId}/dismount` | the caller's own mounts · folder listing inside the mounted volumes · streamed download (4 MB chunks, aborted on failure, audited with SHA-256) · detach | only the user who mounted (404 for others); `backup.restore` re-checked on every list/download |
| GET `/vms/{id}/backups` | restore points of a VM + `canRun`, `canRestore`, `canManage` + repositories usable from its host | `inventory.read` on the VM |
| POST `/vms/{id}/backup` `{repositoryId, checkpointType?, verifyAfterCopy?}` | "Back up now": a run with one point and one `backup.vm` job; 409 `vm_missing`, `backup_in_progress`, `staging_required`, `repository_other_host`; 400 `unknown_repository`, `invalid_checkpoint_type` | `backup.run` on the VM; audited `backup.vm.request` |

### Notifications and API tokens (ADR-032, ADR-033)

| Method & path | Purpose | Permission / notes |
|---|---|---|
| GET `/settings/notifications` · PUT `/settings/notifications` `{email {enabled, host, port, security: starttls \| none, userName?, password?, from, to[]}, webhooks [{name, url, format: generic \| teams \| slack}], restoreTestFailed, agentsOutdated}` · POST `/settings/notifications/test` | SMTP and webhooks. `password`: omitted = keep, `""` = remove; webhook URLs are returned masked and a masked URL sent back keeps the stored one. 400 `invalid_smtp_host`, `invalid_smtp_port`, `invalid_smtp_security`, `invalid_mail_address`, `too_many_webhooks`, `invalid_webhook_url` (https only), `invalid_webhook_format`, `no_channel` | `settings.manage`; audited without secrets |
| GET `/api-tokens` · POST `/api-tokens` `{name, expiresInDays?, scopes?: ["backup.read"]}` · POST `/api-tokens/{id}/revoke` | read-only tokens for monitoring; the token (`hvp_` + 40 characters) is returned **once** by POST. 400 `invalid_token_name`, `invalid_scope`, `invalid_expiry`; 409 `too_many_tokens` (100) | `settings.manage` (+ recent re-authentication to create); audited |
| GET `/public/backup/status` · GET `/public/backup/runs?since=` | public read-only API: overall status, jobs with last run, VMs with newest point (names only) · runs since a time (default 24 h, max 500) | `Authorization: Bearer hvp_...` with scope `backup.read` only (no cookie, no CSRF); 401 `invalid_token` (audited `api_tokens.use`); rate limit 120/min per address (429) |

## Endpoints added in v0.9.0 (agent protocol 11, failover clusters, ADR-041)

| Method / path | What | Authorization |
|---|---|---|
| GET `/clusters` | clusters with at least one node the user can see; `status` only for who reads the cluster | `inventory.read` (cluster, or a node) |
| POST `/clusters` | `{name, description, newNodes[], hostIds[]}`: cluster + pending hosts with their enrollment (`-ClusterAccess Full`) | `hosts.manage` + recent re-authentication |
| GET `/clusters/{id}` | nodes (state, agent, memory, VMs, mismatches), last stored status: report, issues, capacity | `inventory.read` on the **cluster** (404 otherwise) |
| GET `/clusters/{id}/status` | same, read live through the first node agent that answers (120 s per node), stored | `inventory.read` on the cluster |
| PUT `/clusters/{id}` | rename, description | `hosts.manage` |
| POST `/clusters/{id}/nodes` | `{hostId}` existing host or `{displayName}` new pending host (enrollment returned) | `hosts.manage` + recent re-authentication |
| POST `/clusters/{id}/nodes/{hostId}/remove` | the host becomes standalone (nothing changes in Windows) | `hosts.manage` + recent re-authentication |
| POST `/clusters/{id}/delete` | hosts stay as standalone hosts, role assignments on the cluster deleted | `hosts.manage` + recent re-authentication |
| GET `/hosts/{id}/maintenance?action=Enter` (or `Exit`) | live check: `allowed`, `blocking[]`, `warnings[]`, `s2d` | `hosts.maintenance` on the node |
| POST `/hosts/{id}/maintenance` | `{action, storageMaintenance, failback, acknowledgeWarnings, idempotencyKey}` → job `host.maintenance`; 409 `maintenance_blocked` / `maintenance_warnings` | `hosts.maintenance` on the node |
| PUT `/clusters/{id}/backup-staging` | v0.9.1: `{path, replaceExisting}` sets the backup staging folder of every node (all or nothing; nodes that have one keep it unless `replaceExisting`) → `{path, updated[], kept[{name, path}]}`; 400 `invalid_staging_path` / `drive_not_found` / `location_not_allowed` naming each node | `hosts.manage` or `backup.manage` (Global) |
| POST `/clusters/{id}/shared-volumes/move` | v0.9.1: `{volumeName, destinationHostId, idempotencyKey}` → job `cluster.storage` (`MoveCsv`): the node becomes the owner of the CSV (data does not move); 409 `csv_not_found` / `csv_not_online` / `csv_owner_not_up` / `csv_already_owner`, 501 `agent_outdated` without a node agent 0.9.1 | `hosts.maintenance` on the destination node |

`GET /clusters` also returns `problems` and `nodeProblems` (last stored read, only for who reads the cluster).
Also: `POST /hosts/{id}/vms` accepts `vm.highlyAvailable` (location on a CSV of the node); `POST /vms/{id}/move` between
two nodes of the same cluster queues `vm.migrate` operation `ClusterNode`; hosts carry `clusterId`, `clusterName`,
`clusterFacts`, `clusterMismatch`, `canMaintain`; VMs carry `isClustered`; `/hosts/{id}/defaults` carries `cluster`;
role assignments accept `scopeType: Cluster`.
`GET /clusters/{id}` (v0.9.1) also returns `backupStagingPath` per node, `can.setBackupStaging` and
`backup.credentialRepositories`; `GET /backup/repositories` returns `hostsWithoutStaging[{id, name, clusterId}]` to
backup administrators (repositories with credentials).
The cluster status report (v0.9.1) also carries `coreGroupState`, `coreResources[]`, `healthFaults[]`, `replication[]`
and `storageSpacesDirect.jobs[].elapsedSeconds` (null when not read or with an older agent).

## Endpoints added in v0.9.1 (portal updates from GitHub, ADR-042)

| Method / path | What | Authorization |
|---|---|---|
| GET `/system/updates` | `{current, autoCheck, lastCheckAt, lastError, latest {version, tag, publishedAt, htmlUrl, gatewaySetup {name, size, hasChecksum}, agentSetup}, updateAvailable, installBlocker, installRequestedAt, installRequestedVersion, lastResult {timeUtc, status, version, message}}`; never contacts GitHub | `settings.manage` |
| POST `/system/updates/check` | reads the latest release on GitHub now; returns the status (a failed check returns 200 with `lastError`, audited Failure) | `settings.manage` |
| PUT `/system/updates/settings` | `{autoCheck}`: check every 12 hours (off by default) → 204 | `settings.manage` |
| POST `/system/updates/install` | `{version}` (the latest found): download, SHA-256 check, stage, start the updater task → 202; 409 `not_latest`, `not_newer`, `no_setup`, `no_checksum`, `invalid_size`, `checksum_mismatch`, `download_failed`, `updater_failed`, `container`, `not_a_service`, `updater_missing` | `settings.manage` + recent re-authentication |

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

Monitoring with an API token (v0.7.0, read-only, `/api/v1/public/*` only):
```powershell
Invoke-RestMethod https://gw:8443/api/v1/public/backup/status -Headers @{ Authorization = "Bearer $env:HVP_API_TOKEN" }
```
Tokens with write scopes (automation accounts) do not exist: changes still need a signed-in user.
