# Product backup and restore (HvPortal itself)

This page covers backing up **HvPortal's own state**. It is unrelated to VM backup (Milestone 3).

## What to back up

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
