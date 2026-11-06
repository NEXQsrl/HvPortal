# Product backup and restore (HvPortal itself)

This page covers backing up **HvPortal's own state**. It is unrelated to VM backup (Milestone 3).

## From the portal (recommended): Settings > Backup and restore

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

## What to back up (manual procedure)

| Item | Path (default `C:\ProgramData\HvPortal\Gateway`) | Notes |
|---|---|---|
| Database | `db\hvportal.db` (+ `-wal`, `-shm`) | users, roles, hosts, inventory, jobs, audit |
| Key ring + internal CA | via `admin export-keys` | **required** to read TOTP secrets, keep agents trusted, verify audit |
| Configuration | service environment (registry) or `appsettings.Production.json` if used | ports, names, certificate thumbprint |
| UI certificate | your PKI | if a custom certificate is used |

The database alone is not enough: TOTP secrets and the CA private key are encrypted with keys **outside** the
database. The DPAPI-protected `keys\master.json` cannot be used on another machine — that is why the passphrase
export exists.

## Key export (after setup, and after every `admin rotate-keys`)

```powershell
& 'C:\Program Files\HvPortal\Gateway\HvPortal.Gateway.exe' admin export-keys --out D:\secure\hvportal-keys.hvpx
```
Passphrase ≥ 20 characters (or `HVP_EXPORT_PASSPHRASE`). Store the file and the passphrase separately, offline,
following a two-person procedure. The export contains every key ever created plus `pki\ca.bin` and `pki\server.bin`.

## Automatic database copies (ADR-023)

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
rebuilt by the agents at their next report. The copy must come from the same gateway (same key ring).

## Database backup

Consistent copy (recommended): stop the service, copy `db\*`, start the service.
```powershell
Stop-Service HvPortalGateway; Copy-Item C:\ProgramData\HvPortal\Gateway\db\* D:\backup\hvportal\ ; Start-Service HvPortalGateway
```
Online alternative: SQLite online backup (`sqlite3 hvportal.db ".backup D:\backup\hvportal.db"`) if the sqlite3
tool is approved in your environment. Do not copy the database file while the service runs without the online
backup API (WAL mode).

## Restore / disaster recovery on a new server

1. Install the gateway with the same version (`Install-Gateway.ps1`), then `Stop-Service HvPortalGateway`.
2. `HvPortal.Gateway.exe admin import-keys --in hvportal-keys.hvpx --force` (elevated).
3. Copy the database files into `db\`.
4. Use the same `-PublicNames` (agents validate the gateway name against the restored CA) and start the service.
5. Verify: sign in with TOTP, *Audit → Verify integrity*, hosts reconnect.
If the key export is lost: agents must be re-enrolled, users must re-enroll MFA (admins can reset it), audit chain
verification is no longer possible for old entries.

## Key rotation

`admin rotate-keys` adds a new active key; existing ciphertexts remain readable with older keys. Export again after
rotation. Re-encryption of existing secrets with the new key is a Milestone 2 task.

## Audit retention / external copy

`GET /api/v1/audit/export` (JSON Lines, includes hashes) — schedule an export to an external, append-only system.
The in-product chain detects tampering but is not immutable.
