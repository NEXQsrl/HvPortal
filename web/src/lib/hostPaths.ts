// Client-side mirror of HvPortal.Contracts.HyperV.HostPaths (the gateway and the agent re-check everything).

/** Top-level folders never used as VM location (operating system, profiles, IIS, Veeam catalog, OVH agent, logs...). */
export const BLOCKED_ROOT_FOLDERS = [
  'Windows', 'Users', 'User', 'inetpub', 'VBRCatalog', 'ovhupd', 'Packages', 'PerfLogs',
  'Program Files', 'Program Files (x86)', 'ProgramData', 'Recovery', 'Boot', 'System Volume Information',
  'Documents and Settings', 'Config.Msi', 'MSOCache', 'OneDriveTemp', 'Intel', 'AMD', 'NVIDIA',
];

// Same pattern as HostPaths.LocalFolderRegex on the server (written so that it cannot backtrack exponentially).
const LOCAL_FOLDER = /^[A-Za-z]:\\(?:[^\x00-\x1f"*?<>|:\\/]+(?:\\[^\x00-\x1f"*?<>|:\\/]+)*\\?)?$/;

export function normalizeFolder(p: string): string {
  const s = p.trim();
  if (s.length < 3) return s;
  const up = s[0]!.toUpperCase() + s.slice(1);
  return up.length > 3 ? up.replace(/\\+$/, '') : up;
}

/** null when acceptable, otherwise an error code (same codes as the API). */
export function validateLocation(p: string): 'invalid_location' | 'location_not_allowed' | null {
  const s = normalizeFolder(p);
  if (s.length < 3 || s.length > 200 || !LOCAL_FOLDER.test(s) || s.split('\\').some((x) => x === '.' || x === '..' || /[ .]$/.test(x))) return 'invalid_location';
  const first = s.split('\\').filter(Boolean)[1];
  if (first && (first.startsWith('$') || BLOCKED_ROOT_FOLDERS.some((b) => b.toLowerCase() === first.toLowerCase()))) return 'location_not_allowed';
  return null;
}

/** Same rule as Get-SafeName in the agent's operations script (disk file name). */
export function safeName(name: string): string {
  let s = name.replace(/[^A-Za-z0-9_.-]/g, '_').replace(/^[. ]+|[. ]+$/g, '');
  if (!s) s = 'vm';
  return s.length > 60 ? s.slice(0, 60) : s;
}

export function joinPath(a: string, b: string): string {
  return a.endsWith('\\') ? a + b : `${a}\\${b}`;
}

export function parentFolder(p: string): string {
  const i = p.lastIndexOf('\\');
  return i <= 2 ? p.slice(0, 3) : p.slice(0, i);
}
