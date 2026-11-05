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

/** major.minor.patch of "0.3.0", "0.3.0.0" or "0.3.0+abc"; null when unknown. */
export function semver(v: string | null | undefined): [number, number, number] | null {
  const m = /^(\d+)\.(\d+)\.(\d+)/.exec(v ?? '');
  return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
}

export const compareVersions = (a: [number, number, number], b: [number, number, number]) => a[0] - b[0] || a[1] - b[1] || a[2] - b[2];

export const shortVersion = (v: string | null | undefined) => semver(v)?.join('.') ?? v ?? '—';

/** True when the agent is known to be older than the gateway (both are released together). */
export function isAgentOlder(agentVersion: string | null | undefined, gatewayVersion: string | null | undefined): boolean {
  const a = semver(agentVersion);
  const g = semver(gatewayVersion);
  return !!a && !!g && compareVersions(a, g) < 0;
}
