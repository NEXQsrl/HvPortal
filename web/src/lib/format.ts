export function formatBytes(n: number | null | undefined): string {
  if (n == null) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let i = 0;
  let v = n;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v >= 10 || i === 0 ? 0 : 1)} ${units[i]}`;
}

export const mb = (v: number | null | undefined) => (v == null ? null : v * 1024 * 1024);

export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return '—';
  const s = Math.floor(ms / 1000);
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  return d > 0 ? `${d}d ${h}h` : h > 0 ? `${h}h ${m}m` : `${m}m ${s % 60}s`;
}

export function percent(used: number | null | undefined, total: number | null | undefined): number | null {
  if (used == null || !total) return null;
  return Math.min(100, Math.max(0, Math.round((used / total) * 100)));
}

/** Client-side mirror of the server rules (the server validates again). */
export const VM_NAME = /^[\p{L}\p{N}][\p{L}\p{N} ._()-]{0,99}$/u;
export const GROUP_NAME = /^[\p{L}\p{N}][\p{L}\p{N} ._()-]{0,63}$/u;
export const CHECKPOINT_NAME = /^[\p{L}\p{N}][\p{L}\p{N} ._():-]{0,99}$/u;
export const LIBRARY_PATH = /^[A-Za-z]:\\(?:[^\x00-\x1f"*?<>|:\\/]+\\?)*$/;
