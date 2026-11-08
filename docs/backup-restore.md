# Backup and restore

> Italian version: [backup-restore_IT.md](backup-restore_IT.md). Keep both in step when this page changes.

This page has two parts:

- **Part A — VM backups** (v0.7.0, menu *Backup*): copies of the virtual machines, restores, restore tests. Design and
  rationale: [ADR-031](decisions.md).
- **Part B — HvPortal's own state** (database, keys, CA): how to back up and recover the portal itself. A VM backup
  does not contain the portal, and a portal backup does not contain any VM.

> **Status.** VM backups are implemented and covered by automated tests that use a test double of the host. No backup,
> restore, mount or restore test has run on a real Hyper-V host yet (the development machine has no Hyper-V rights).
> Run the procedures V56–V62 and V67 of the [real-host plan](verification/real-host-test-plan.md) on test VMs before relying on
> them.

---

## Part A — VM backups

### How it works: technology and components

HvPortal does **not** use Windows Server Backup (`wbadmin`), the Hyper-V VSS writer of a host-level backup, or any
third-party backup engine. A backup is made with the standard cmdlets of the Hyper-V PowerShell module, run by the
agent on the host that owns the VM, through the single fixed operations script `Ops\HvPortal.Ops.ps1` (Windows
PowerShell 5.1, typed operations only, ADR-012). No backup software has to be installed on the hosts or on the NAS.

| Component | Where | What it does |
|---|---|---|
| Backup scheduler (`VmBackup/BackupScheduler`) | gateway | every 20 s (`Gateway__Backup__IntervalSeconds`): starts the jobs that are due (also a start missed while the gateway was stopped), applies retention after each run, queues restore tests every *N* days, sends notifications |
| Backup service and job runner (`VmBackup/BackupService`, `Jobs/JobRunner`) | gateway | one persistent task per VM (`backup.vm`, `backup.verify`, `backup.restore`, `backup.delete`, ...), authorized again when it starts, at most 4 long tasks at the same time; records restore points and their state in the portal database |
| Agent | each host | receives the typed command over its outbound channel and runs the matching branch of the operations script (`BackupVm`, `VerifyBackup`, `RestoreBackup`, `RestoreBackupDisk`, `MountBackupDisk`, `RemoveBackup`, `ListBackups`, `GetBackupReadiness`) |
| Hyper-V (`vmms`) | each host | takes the checkpoint, writes the export, imports a restore point |
| Repository | local folder or SMB share / NAS | holds the restore points: plain folders and files, readable without HvPortal |

**A backup, step by step** (`BackupVm`):

1. The repository is opened. Local folder or domain share: used directly. Share with stored credentials (NAS outside the
   domain): mapped for the duration of the task with `New-PSDrive -Credential`, and the export goes to a temporary
   folder `hvportal-<id>` in the host's **staging folder**, because Hyper-V cannot use those credentials.
2. A checkpoint left by an interrupted backup (`HvPortal backup *`) is removed first, and the checkpoint type it
   recorded is put back.
3. **Checkpoint** (`Checkpoint-VM`), also while the VM runs. If the job's checkpoint type (Production,
   ProductionOnly, Standard) differs from the VM's, the VM's type is changed only for that moment and put back.
4. **Export of the checkpoint** (`Export-VMSnapshot`): configuration (`.vmcx`, `.vmgs`, ...) and disks (`.vhdx`) as
   they were at the checkpoint, written by Hyper-V into the restore point folder (or the staging folder).
5. **Removal of the checkpoint** (`Remove-VMSnapshot`, merged by Hyper-V), always, also when the export fails.
6. **SHA-256 of every exported file** (`Get-FileHash`), computed on the host before any copy.
7. Staging only: **copy** to the share (`Copy-Item`, file by file) and, with *verify after copy*, the copy is read
   back and compared with the hashes; then the staging folder is deleted.
8. **Manifest** `hvportal-backup.json` written last (VM, host, times, checkpoint type requested and used, files with
   size and SHA-256, configuration file, disks). A folder without the manifest is never used; a failed backup deletes
   the folder it created.

Who accesses the repository: with a local folder or a domain share, Hyper-V writes the export (on a share: with the
**computer account** of the host, `HOST$`) and the agent account reads it to compute the hashes; with stored
credentials only the agent reads and writes the share, Hyper-V writes only to the local staging folder.

**Restores** use the same tools:

| Operation | Cmdlets |
|---|---|
| As a new VM / restore test | `Compare-VM -Copy -GenerateNewId` (compatibility check, nothing copied), `Import-VM -CompatibilityReport` into `<folder>\<name>`, `Rename-VM`; restore test: `Start-VM`, wait for the guest heartbeat, then `Stop-VM -TurnOff`, `Remove-VM` and the folder deleted |
| Replace the VM | the same check **before** the original is touched; then `Stop-VM -TurnOff`, `Remove-VM`, original disks renamed `.pre-restore-<time>`, `Import-VM` with the **original id** (`Compare-VM -Copy` without a new id) |
| One disk | `Copy-Item` of the `.vhdx` out of the point, optionally `Add-VMHardDiskDrive` (SCSI) |
| Single files | `Mount-VHD -ReadOnly` on the host, volumes found through WMI; files read by the agent in chunks and streamed by the gateway to the browser; `Dismount-VHD` at the end |
| Verify | `Get-FileHash` of every file, compared with the manifest |
| Delete a point | removal of the point folder `<VM folder>\<point id>` only (its name is checked against a fixed pattern) |

From a share with stored credentials the files are first copied to the staging folder, because Hyper-V reads the export
itself during an import.

**Why not Windows Server Backup.** It backs up at host level (volumes or the whole Hyper-V host, through the Hyper-V VSS
writer) and is driven per host with `wbadmin`: it does not give per-VM restore points, jobs, retention, notifications,
permissions per VM or group, restore tests or file restore from the portal, and on a network share it keeps only the
latest backup (each run overwrites the previous one). The export of a checkpoint gives a point in time of one VM while it runs, in a standard Hyper-V format
that can be imported with Hyper-V Manager even without HvPortal. Considered as future work: recovery checkpoints with
resilient change tracking (RCT) for incremental copies ([ADR-031](decisions.md)).

### What a restore point is, and what it is not

A **restore point** is a full copy of one VM taken at one moment: the agent takes a checkpoint, exports that checkpoint
(`Export-VMSnapshot`) into the repository, removes the checkpoint again and writes a manifest `hvportal-backup.json`
with the SHA-256 of every exported file. The manifest is written last: a folder without it is an incomplete copy and is
never used. Layout in the repository:

```
<repository>\<VM name>_<first 8 hex of the Hyper-V id>\<restore point id>\
    Virtual Machines\<id>.vmcx ...      (exported configuration)
    Virtual Hard Disks\<disk>.vhdx ...  (exported disks)
    hvportal-backup.json                (manifest, written last)
```

**Consistency.** The job's *checkpoint type* decides how the copy is taken:

| Type | What Hyper-V does | What you get |
|---|---|---|
| Production (default) | asks the guest, through the *Backup* integration service, to flush its data (VSS in Windows guests, file-system freeze in Linux guests); if the guest cannot take part Hyper-V falls back to a standard checkpoint | the restore point records the type Hyper-V reports (`checkpointTypeUsed`): check it |
| ProductionOnly | as above, but the backup **fails** instead of falling back | fails on guests without working integration services |
| Standard | no guest involvement | crash-consistent disks (as after a power loss) |

HvPortal does **not** verify or guarantee application consistency. A restore point is a copy of a VM, not an
application-consistent backup of the databases or mail stores inside it: protect those with their own tools (native
dumps, application-aware backup) and test restores.

**Not provided:** incremental or differential copies (every point is a full copy), deduplication, compression,
encryption of the repository, immutability (WORM), a second copy of the points, backups of VMs whose checkpoints are
disabled, file search, application item restore, failover-cluster awareness.

### Before you start

1. **Agents 0.7.0** on the hosts (backups need agent protocol 9; older agents are refused with "update the agent").
2. **Permissions** (Users & roles): `backup.manage` (Global) to create repositories and jobs and to delete restore
   points; `backup.run` to back up VMs and run jobs; `backup.restore` to restore. The built-in role *Backup operator*
   has all three plus `inventory.read`, `jobs.read.all` and `vm.create` (a restore as a new VM creates a VM). The Backup
   page needs one of the three permissions at **Global** scope; holders on single VMs use the VM's *Backups* tab.
3. **Checkpoints enabled** on every VM to protect (VM settings > checkpoint type not *Disabled*), integration services
   installed and the *Backup (volume shadow copy)* integration service enabled for production checkpoints.
4. **A repository** (next section) and, where needed, a **backup staging folder** per host (Host > Settings > Backup
   staging folder, `hosts.manage` or `backup.manage`): a local folder, not a drive root, large enough for a full copy of
   the largest VM of that host. It is required for shares with stored credentials, used for restore tests and for file
   restore from such shares.
5. **Allowed storage folders** ([ADR-034](decisions.md)): if a host restricts its folders, the repository folder, the
   staging folder and the restore folders must be inside them.
6. **Check**: Backup > Repositories > *Check* (or the host's backup readiness) lists what is missing on a host, with
   the fix: Hyper-V module and cmdlets, checkpoint setting and backup integration service of each VM, write test and
   free space of the repository and of the staging folder.

### Repositories

| Kind | Path | Who writes | Notes |
|---|---|---|---|
| Local folder of one host | `D:\Backups` | Hyper-V (export) and the agent account (folders, hashes, deletions) | only that host's VMs can use it; give the agent account *Modify* on the folder if it is not created by the agent; same disk as the VMs = no protection against the loss of the host |
| SMB share in the domain, no credentials | `\\fs01\hvbackup` | the **computer account** of each host (`H1$`, `H2$`) — expected, not verified yet (real-host plan V67) | grant share and NTFS rights to the computer accounts of every host that backs up or restores |
| SMB share outside the domain (NAS in a workgroup) | `\\nas01\backups` + user (`NAS01\backup`) and password | the agent, with the stored credentials, from the host's **staging folder** | Hyper-V cannot use those credentials: the export is prepared in the staging folder of the host and then copied (and, with *verify after copy*, read back and compared); needs space for one full VM in staging |

- The password is encrypted with the gateway key ring, never shown again, never written to the audit log and never
  stored in job parameters; it is sent to the host only inside the command of the operation that needs it. Use a
  dedicated NAS account with rights on the backup share only.
- The path of a repository cannot be changed (its restore points live there). A repository can be deleted only when no
  job uses it and it holds no restore points.
- Hidden or administrative shares (`\\server\D$`) are refused.

### Backup jobs

Backup > Jobs > *New job*:

- **What**: VMs and/or groups. A group includes its sub-groups and is resolved at every run, so VMs added to it later
  are protected. You must hold `backup.run` on everything you select.
- **Where**: one repository.
- **When**: *daily* (days of the week and one or more `HH:mm` times in a time zone; a time skipped by a daylight-saving
  change runs one hour later), *hourly* (every 1–24 hours) or *manual*.
- **Retention**: the newest *N* good restore points per VM (1–366) and optionally none older than *D* days. Points
  marked *Keep* and the newest good point of each VM are never removed; failed and unverified points older than the
  newest good one are removed. Deleting the files is itself a job on a host that can reach the repository.
- **Checkpoint type**, **verify after copy** (staging only: read the copy back from the share and compare the SHA-256),
  **restore test every N days** (see Restore tests), **notify** (never / failure / warning / always).

How runs behave:

- A run creates one restore point and one task (job `backup.vm`) per VM. Tasks run **as the user who last saved the
  backup job** and are authorized again per VM when they start: a VM that this user may no longer back up fails with
  *authorization revoked* (save the job with an account that holds `backup.run` on everything).
- A start missed while the gateway was stopped runs once when it is back. A start is skipped while the previous run of
  the same job is still running.
- At most 4 long tasks (backups, verifications, restores, moves, agent updates) run at the same time
  (`Gateway__Jobs__MaxConcurrentLongJobs`), separately from power and configuration tasks. A backup can take hours (up
  to 12 h); while a VM is being backed up, other tasks on that VM wait.
- During a backup the VM gets a checkpoint named `HvPortal backup <id>`, which is removed (merged) after the export. If
  the VM's checkpoint type differs from the job's, it is changed for the moment of the checkpoint and put back.

**Back up now** (VM menu or the VM's *Backups* tab, `backup.run` on the VM): one restore point into a repository usable
from the VM's host. These points have no retention: delete them by hand. Points of a deleted job are kept, also without
retention.

### Watching the results

- **Overview**: runs of the last 24 h, protected and unprotected VMs (not in an enabled job and without a good point in
  the last 7 days), latest problems.
- **Runs**: *Success*, *Warning* (some VM failed or is unverified) or *Failed* (no good point), with the message of each
  failed VM.
- **Restore points**: one row per point.

| State | Meaning | What to do |
|---|---|---|
| Creating | the backup task is running | wait |
| Ok | the agent returned the manifest of this point; SHA-256 recorded at backup time | usable |
| Unverified | the result of the task was lost, or the point was found by a rescan | run *Verify* before relying on it |
| Failed | the backup did not complete; no usable copy | read the error |
| Deleting / Deleted | removal in progress / done | — |

- **Notifications** (Settings > Notifications, [ADR-033](decisions.md)): e-mail (SMTP with STARTTLS) and webhooks
  (generic JSON, Microsoft Teams, Slack) at the end of runs according to the job, for failed restore tests, and daily
  for outdated agents. Use *Send test*.
- **Monitoring systems** (Settings > API tokens, [ADR-032](decisions.md)): a read-only token for
  `GET /api/v1/public/backup/status` (overall `ok` / `warning` / `critical`, jobs, VMs and their newest point) and
  `/public/backup/runs`. The token is shown once; it is not tied to the user who created it — revoke it when it is no
  longer needed.

### Verifying

- **Verify** (restore point menu, `backup.run`): reads every file of the point again and compares size and SHA-256 with
  the manifest. It finds damaged or incomplete copies. It cannot detect a deliberate change made together with the
  manifest by someone who can write to the share: protect the share.
- **Restore test** (restore point menu, or *restore test every N days* in the job): the point is restored as a new VM
  `hvp-test-<id>` with its network adapters **disconnected**, started, and passes when the guest's heartbeat reports OK
  within 10 minutes; then the VM is turned off, removed and its folder deleted. On hosts with a
  staging folder the test VM is created in `<staging folder>\restore-tests`; scheduled tests skip hosts without one. A
  passed test proves the VM boots and its integration services run, not that its applications work.

### Restoring

Restore point menu > *Restore* (`backup.restore`). The restore runs on a host that can use the repository.

| Mode | What happens | Needs | Watch out |
|---|---|---|---|
| **As a new VM** | the point is imported as a copy with a **new id** and the name you choose, in `<folder>\<name>` (or the host default folders); network adapters as in the point, disconnected, or all on one switch; optionally started | `vm.create` on the target host; a free VM name; enough space | the original VM is not touched; the new VM is in no group (place it and assign roles); two copies with the same guest identity on the same network conflict — start disconnected when the original still runs |
| **Replace the VM** | only on the VM the point was taken from, without checkpoints; type its name. The VM is turned off and **removed from Hyper-V**; its disk files are renamed `<file>.pre-restore-<yyyyMMddHHmmss>` (kept, not deleted); the point is imported with the original id, name and folders | typed name | the current state is discarded; the renamed disks keep using space (delete them when the restore is confirmed); **if the import fails, the disks get their names back but the VM stays removed from Hyper-V**: recreate it from its disks or restore it as a new VM |
| **Restore test** | see Verifying | `vm.create` on the host | — |
| **One disk** | one disk of the point is copied to a host folder as `<disk>-restored-<time>.vhdx` and optionally attached to a VM (SCSI; a running generation 2 VM accepts it) | `vm.config.storage` on that VM, or `host.files.write` on the host | to copy files back with the guest's own accounts: attach, copy inside the guest, detach and delete the disk |
| **Single files** | Backup > Files, or *Browse files* on a point: one disk is attached **read-only** to a host that runs its own agent; its volumes are listed; files are downloaded through the browser | host with its own agent (not remote hosts) | only volumes that get a drive letter (BitLocker volumes and unknown file systems cannot be read); the guest's file permissions apply to the agent account, so protected folders (user profiles) are refused — use *One disk* instead; only you can browse a disk you attached; it is detached when you close it, after 2 h without use, or when the agent restarts; every download is audited with its SHA-256 |

Network incompatibilities found by Hyper-V during an import (a switch that does not exist on the target host) disconnect
the adapter with a warning; any other incompatibility stops the restore. A VM with a virtual TPM may not start on
another host without its key protector (to be verified, V57).

### After a portal reinstall or a database restore: Rescan

Backup > Repositories > *Rescan* reads the manifests in the repository and adds the restore points the portal does not
know as **Unverified**. Verify them, then restore as usual. Restore points whose VM no longer exists are listed only to
holders of backup permissions at Global scope.

### Space and time

Every restore point is a full copy of the VM's disks and configuration: repository space ≈ size of the VM's files ×
points kept per VM, plus one full copy in the staging folder while a share with credentials is used. Backups read the
whole VM; copies to a NAS go over the network at the speed of the share. Schedule large VMs outside working hours and
record the duration of the first runs.

### Common messages

| Message / code | Cause | Fix |
|---|---|---|
| `staging_required` | the repository uses credentials and the host has no staging folder | Host > Settings > Backup staging folder |
| `repository_other_host` | a local-folder repository of another host | use a share, or a repository of this host |
| "Checkpoints are disabled for this VM" | checkpoint type *Disabled* | set Production (VM settings) |
| "cannot be reached with the computer account of this host" | share without credentials, rights missing for `HOST$` | grant the computer account, or use credentials + staging |
| `repository_credentials_unreadable` | the key ring that encrypted the password is not available (portal moved without its keys) | enter the password again |
| `backup_in_progress` | a backup of that VM is already running | wait |
| `agent_outdated` | agent older than 0.7.0 | update the agent |

---

## Part B — HvPortal's own state

This part covers backing up **HvPortal's own state**. Restore points of VMs are not part of it; the portal database
does contain the records of repositories (with their encrypted passwords), jobs and restore points, so after a
recovery the restore points are known again (or can be found again with *Rescan*).

### From the portal (recommended): Settings > Backup and restore

- **Create backup** (platform administrator, re-authentication): choose a passphrase (≥ 20 characters) and download
  one `.hvpbak` file. It contains a consistent snapshot of the database (SQLite online backup, the service keeps
  running), the key ring and the internal CA, sealed with the passphrase (same format as `admin export-keys`).
  Audited with the SHA-256 of the file. Store file and passphrase separately, offline.
- **Restore**: choose the file, enter the passphrase, **Verify** (shows date, version, gateway and contents; nothing
  is written), type `RIPRISTINA`, **Prepare restore**. The restore is staged (`restore-pending\`, key ring re-protected
  with DPAPI on this machine) and applied at the next start of the service, before the database is opened; the
  previous database, key ring and CA files are kept in `db\pre-restore-<timestamp>\`. After the start every session is
  revoked and `settings.backup.restore.applied` is written to the (restored) audit log. **Restart the service now**
  exits the process so the service recovery restarts it (installed by `Install-Gateway.ps1`).
- A backup made by a newer HvPortal (unknown database migration) is refused: install that version first.
- Hosts enrolled after the backup must be enrolled again; VM data is not part of this backup.

### What to back up (manual procedure)

| Item | Path (default `C:\ProgramData\HvPortal\Gateway`) | Notes |
|---|---|---|
| Database | `db\hvportal.db` (+ `-wal`, `-shm`) | users, roles, hosts, inventory, jobs, audit, backup repositories/jobs/restore points records |
| Key ring + internal CA | via `admin export-keys` | **required** to read TOTP secrets, remote-host, repository and SMTP passwords, keep agents trusted, verify audit |
| Configuration | service environment (registry) or `appsettings.Production.json` if used | ports, names, certificate thumbprint |
| UI certificate | your PKI | if a custom certificate is used |
| Agent package | `<gateway>\packages` | rebuilt by the release; needed to update agents from the portal |

The database alone is not enough: TOTP secrets, stored passwords and the CA private key are encrypted with keys
**outside** the database. The DPAPI-protected `keys\master.json` cannot be used on another machine — that is why the
passphrase export exists.

### Key export (after setup, and after every `admin rotate-keys`)

```powershell
& 'C:\Program Files\HvPortal\Gateway\HvPortal.Gateway.exe' admin export-keys --out D:\secure\hvportal-keys.hvpx
```
Passphrase ≥ 20 characters (or `HVP_EXPORT_PASSPHRASE`). Store the file and the passphrase separately, offline,
following a two-person procedure. The export contains every key ever created plus `pki\ca.bin` and `pki\server.bin`.

### Automatic database copies (ADR-023)

The gateway copies its database by itself: once a day, verified with `PRAGMA integrity_check`, kept for 7 days in
`<data>\db\backups\hvportal-auto-<UTC date>-<time>.db` (Settings > Backup and restore shows them and offers
*Copy now*). Configuration: `Gateway:Database:AutoBackupEnabled` (default true), `AutoBackupIntervalHours` (24),
`AutoBackupRetentionDays` (7). In the Windows service use the environment variables
`Gateway__Database__AutoBackupRetentionDays`, etc.

These copies protect against a **damaged database file** only: they are on the same disk and do not contain the key
ring. For the loss of the server keep downloading the sealed backup (above) and store it elsewhere.

Going back to a copy (database damaged, gateway not starting or reporting database errors):
```powershell
Stop-Service HvPortalGateway
cd C:\ProgramData\HvPortal\Gateway\db
Rename-Item hvportal.db hvportal.db.damaged
Remove-Item hvportal.db-wal, hvportal.db-shm -ErrorAction SilentlyContinue
Copy-Item .\backups\hvportal-auto-<date>-<time>.db .\hvportal.db
Start-Service HvPortalGateway
```
Everything changed in the portal after that copy is lost (users, roles, groups, jobs, audit entries); the inventory is
rebuilt by the agents at their next report. The copy must come from the same gateway (same key ring). Restore points
created after the copy are still in their repositories: use *Rescan* (Part A).

### Database backup

Consistent copy (recommended): stop the service, copy `db\*`, start the service.
```powershell
Stop-Service HvPortalGateway; Copy-Item C:\ProgramData\HvPortal\Gateway\db\* D:\backup\hvportal\ ; Start-Service HvPortalGateway
```
Online alternative: SQLite online backup (`sqlite3 hvportal.db ".backup D:\backup\hvportal.db"`) if the sqlite3
tool is approved in your environment. Do not copy the database file while the service runs without the online
backup API (WAL mode).

### Restore / disaster recovery on a new server

1. Install the gateway with the same version (`Install-Gateway.ps1`), then `Stop-Service HvPortalGateway`.
2. `HvPortal.Gateway.exe admin import-keys --in hvportal-keys.hvpx --force` (elevated).
3. Copy the database files into `db\`.
4. Use the same `-PublicNames` (agents validate the gateway name against the restored CA) and start the service.
5. Verify: sign in with TOTP, *Audit → Verify integrity*, hosts reconnect, Backup > Repositories > *Check*.
If the key export is lost: agents must be re-enrolled, users must re-enroll MFA (admins can reset it), audit chain
verification is no longer possible for old entries, and the passwords of remote hosts, backup repositories and SMTP
must be entered again.

### Key rotation

`admin rotate-keys` adds a new active key; existing ciphertexts remain readable with older keys. Export again after
rotation. Re-encryption of existing secrets with the new key is a Milestone 2 task.

### Audit retention / external copy

`GET /api/v1/audit/export` (JSON Lines, includes hashes) — schedule an export to an external, append-only system.
The in-product chain detects tampering but is not immutable.
