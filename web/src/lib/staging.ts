import type { Host, HostVolume } from '@/api/types';

/** Drive letter of a local path ("C:\x" -> "C"), or null. */
export function driveOf(path: string): string | null {
  const m = /^([A-Za-z]):\\/.exec(path.trim());
  return m ? m[1]!.toUpperCase() : null;
}

/** The volume of the host that holds a drive letter (inventory names are "C:"). */
export function volumeOf(host: Pick<Host, 'volumes'>, drive: string | null): HostVolume | undefined {
  return drive ? host.volumes.find((v) => driveOf(v.name.endsWith('\\') ? v.name : v.name + '\\') === drive) : undefined;
}

/** A path is inside one of the allowed storage folders of the host (no list = every local drive). */
export function insideStorageFolders(path: string, folders: string[] | null | undefined): boolean {
  if (!folders || folders.length === 0) return true;
  const p = path.trim().replace(/\\+$/, '').toLowerCase();
  return folders.some((f) => {
    const root = f.replace(/\\+$/, '').toLowerCase();
    return p === root || p.startsWith(root + '\\');
  });
}

/**
 * A staging folder that can work on this host: inside its allowed storage folders when it has a list, on the local
 * drive with the most free space otherwise; null when the portal does not know the drives of the host.
 */
export function suggestStagingPath(host: Pick<Host, 'volumes' | 'storageFolders'>): string | null {
  const free = (drive: string | null) => volumeOf(host, drive)?.freeBytes ?? -1;
  const folders = host.storageFolders ?? [];
  if (folders.length > 0) {
    const best = [...folders].sort((a, b) => free(driveOf(b)) - free(driveOf(a)))[0]!;
    return best.replace(/\\+$/, '') + '\\HvPortal-staging';
  }
  const drives = host.volumes.map((v) => driveOf(v.name.endsWith('\\') ? v.name : v.name + '\\')).filter((d): d is string => !!d);
  if (drives.length === 0) return null;
  const best = [...drives].sort((a, b) => free(b) - free(a))[0]!;
  return `${best}:\\HvPortal-staging`;
}
