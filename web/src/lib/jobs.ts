import type { Job } from '@/api/types';
import type { TKey } from '@/i18n';

type T = (key: TKey, vars?: Record<string, string | number>) => string;

/** Human label of a job: power operations by operation, the others by type + operation. */
export function jobLabel(t: T, job: Pick<Job, 'type' | 'operation'>): string {
  if (job.type === 'vm.power') return t(('op.' + job.operation) as TKey);
  const key = ('jobop.' + job.type + '.' + job.operation) as TKey;
  const s = t(key);
  return s === key ? `${job.type} ${job.operation}` : s;
}

export const isActive = (status: Job['status']) => status === 'Queued' || status === 'Running' || status === 'Reconciling';

/** Jobs started from this browser tab: the tasks panel notifies when they finish. */
export const watchedJobs = new Map<string, string>();

export function watchJob(jobId: string, label: string) {
  watchedJobs.set(jobId, label);
}
