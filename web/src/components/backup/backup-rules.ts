// Client-side mirrors of the backup rules of the server (BackupRules, OperationValidation.Protocol9, RemoteHosts,
// NotificationService.Validate). The gateway validates everything again; these only explain problems early.
// Patterns are written so that each character is matched in one way only (no backtracking on long refused input).
import { GROUP_NAME } from '@/lib/format';
import { normalizeFolder, validateLocation } from '@/lib/hostPaths';

/** BackupRules.NameRegex: names of repositories, backup jobs and API tokens (same rule as group names). */
export const BACKUP_NAME = GROUP_NAME;

/** BackupRules.TimeRegex: "HH:mm". */
export const TIME_OF_DAY = /^(?:[01][0-9]|2[0-3]):[0-5][0-9]$/;

export const MAX_RETENTION_POINTS = 366;
export const MAX_TARGETS = 500;
export const MAX_TIMES = 24;

// RemoteHosts.HostNameRegex (the length is checked separately, instead of the look-ahead).
const HOST_NAME = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$/;
// RemoteHosts.UserNameRegex: "user", "HOST\user", "DOMAIN\user" or "user@domain".
const USER_NAME = /^(?:[A-Za-z0-9][A-Za-z0-9.-]{0,62}\\)?[^\x00-\x1f\\/"[\]:;|=+*?<>@]{1,104}(?:@[A-Za-z0-9.-]{1,253})?$/;
// OperationValidation.UncPathRegex: \\server\share[\folder...]; hidden and administrative shares ($) refused.
const UNC_PATH = /^\\\\([A-Za-z0-9.-]{1,253})\\([^\x00-\x1f"*?<>|:\\/$]{1,80})((?:\\[^\x00-\x1f"*?<>|:\\/]{1,120})*)$/;

/** RemoteHosts.IsValidComputerName: DNS name or complete dotted IPv4 address, never loopback. */
export function isValidComputerName(name: string): boolean {
  if (!name || name.length > 253 || !HOST_NAME.test(name) || name.toLowerCase() === 'localhost') return false;
  if (/^[0-9.]+$/.test(name)) {
    const parts = name.split('.');
    if (parts.length !== 4 || parts.some((p) => p === '' || Number(p) > 255 || (p.length > 1 && p.startsWith('0')))) return false;
    return parts[0] !== '127' && name !== '0.0.0.0' && name !== '255.255.255.255';
  }
  return true;
}

export const isValidAccountName = (user: string) => user.length >= 1 && user.length <= 256 && user.trim() === user && USER_NAME.test(user);

/** A share path as the server stores it: trimmed, without trailing backslashes. */
export const normalizeShare = (p: string) => p.trim().replace(/\\+$/, '');

export function isValidSharePath(path: string): boolean {
  if (path.length < 5 || path.length > 200) return false;
  const m = UNC_PATH.exec(path);
  if (!m || !isValidComputerName(m[1] ?? '')) return false;
  return path.slice(2).split('\\').slice(1).every((s) => s.length > 0 && s !== '.' && s !== '..' && !s.endsWith(' ') && !s.endsWith('.') && s.trim() === s);
}

/** OperationValidation.IsValidRepositoryPath for a local folder: not a system folder, not a drive root. */
export const isValidLocalRepositoryPath = (path: string) => validateLocation(path) === null && normalizeFolder(path).length > 3;

/** A local folder of a host for restores and the staging of copies (HostPaths.ValidateLocation). */
export const isValidHostFolder = (path: string) => validateLocation(path) === null;

/** StorageFolderRules.IsInside: true when the host has no restriction or the path is inside one of its folders. */
export function isInsideStorageFolders(path: string, folders: string[] | null | undefined): boolean {
  if (!folders || folders.length === 0) return true;
  if (path.includes('..')) return false;
  const p = normalizeFolder(path).replace(/\\+$/, '').toLowerCase();
  return folders.some((f) => {
    const root = normalizeFolder(f).replace(/\\+$/, '').toLowerCase();
    return p === root || p.startsWith(root + '\\');
  });
}

/** NotificationService.IsMail: a plain address (no display name), 3-254 characters. */
export function isValidMail(s: string): boolean {
  if (s.length < 3 || s.length > 254 || /\s/.test(s)) return false;
  const at = s.lastIndexOf('@');
  if (at < 1 || at === s.length - 1) return false;
  const domain = s.slice(at + 1);
  return !s.slice(0, at).includes('@') && /^[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*$/.test(domain);
}

/** Webhooks: https only, no user information, at most 1000 characters. */
export function isValidWebhookUrl(s: string): boolean {
  if (s.length > 1000) return false;
  try {
    const u = new URL(s);
    return u.protocol === 'https:' && !u.username && !u.password && !!u.hostname;
  } catch {
    return false;
  }
}

/** Time zone of the browser (IANA id), used as default of a new schedule. */
export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

/** IANA time zones known to the browser (the server checks the id again). */
export function knownTimeZones(): string[] {
  try {
    return ['UTC', ...Intl.supportedValuesOf('timeZone').filter((z) => z !== 'UTC')];
  } catch {
    return ['UTC'];
  }
}
