import { Activity, ShieldCheck } from 'lucide-react';
import type { TrackedStorageJob } from '@/api/cluster-types';
import { Meter, Notice, Section, StatusBadge } from '@/components/common';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, formatDuration } from '@/lib/format';

/** Seconds between two reads of the cluster while the Storage tab is open: faster while jobs are going on. */
export const storageRefreshSeconds = (jobs: TrackedStorageJob[]) => (jobs.some((j) => !j.outcome) ? 20 : 60);

const stalled = (state: string | null) => state === 'Suspended' || state === 'Service';

/**
 * Storage jobs of Windows on a cluster (Get-StorageJob through a node agent): repair, regeneration, rebalance,
 * resynchronization. Not the jobs of the portal. The percentage is shown only when Windows reports one; a job that is
 * no longer listed is shown as such, never as completed (the gateway keeps what it saw: StorageJobTracker).
 */
export function ClusterStorageJobs({ jobs, readable, hasReport, legacyAgent }: { jobs: TrackedStorageJob[]; readable: boolean; hasReport: boolean; legacyAgent: boolean }) {
  const { t, fmtDate } = useI18n();
  const active = jobs.filter((j) => !j.outcome);
  const ended = jobs.filter((j) => j.outcome);
  return (
    <Section title={<span className="flex items-center gap-2"><Activity className="size-4 text-muted-foreground" />{t('cl.jobs.title')}</span>}
      actions={active.length > 0 ? <StatusBadge tone="warn">{active.length}</StatusBadge> : null} bodyClassName="space-y-3 p-3">
      <p className="text-xs text-muted-foreground">{t('cl.jobs.help', { seconds: storageRefreshSeconds(jobs) })}</p>
      {hasReport && !readable && <Notice kind="warn">{t('cl.jobs.unreadable')}</Notice>}
      {legacyAgent && <p className="text-xs text-muted-foreground">{t('cl.jobs.oldAgent')}</p>}
      {active.length === 0 ? (
        readable ? <div className="flex items-center gap-2 text-sm text-success"><ShieldCheck className="size-4" />{t('cl.jobs.none')}</div> : null
      ) : (
        <ul className="space-y-3">
          {active.map((j) => {
            const pct = j.percentComplete;
            const eta = j.elapsedSeconds && pct != null && pct > 0 && pct < 100 ? Math.round(j.elapsedSeconds * (100 - pct) / pct) : null;
            return (
              <li key={j.key} className="rounded-md border p-2.5 text-sm">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="min-w-0 font-medium [overflow-wrap:anywhere]">{j.name}
                    {j.description && j.description !== j.name && <span className="ml-1 text-xs font-normal text-muted-foreground">{j.description}</span>}</span>
                  <span className="flex items-center gap-2">
                    <StatusBadge tone={stalled(j.state) ? 'warn' : 'info'}>{j.state ?? '—'}</StatusBadge>
                    {pct != null && <span className="tabular font-medium">{pct}%</span>}
                  </span>
                </div>
                {pct != null ? <Meter value={pct} className="mt-2 [&>div]:bg-primary" label={t('cl.s2d.progress')} />
                  : <div className="mt-2 text-xs text-muted-foreground">{t('cl.jobs.noPercent')}</div>}
                <div className="mt-1.5 flex flex-wrap gap-x-3 gap-y-0.5 text-xs text-muted-foreground tabular">
                  {j.resources.length > 0 && <span>{t('cl.jobs.resource')}: <span className="text-foreground">{j.resources.join(', ')}</span></span>}
                  {j.bytesTotal ? <span>{t('cl.s2d.copied', { done: formatBytes(j.bytesProcessed), total: formatBytes(j.bytesTotal) })}</span> : null}
                  {j.elapsedSeconds != null && <span>{t('cl.s2d.elapsed', { time: formatDuration(j.elapsedSeconds * 1000) })}</span>}
                  {j.startedUtc && <span>{t('cl.jobs.started', { at: fmtDate(j.startedUtc) })}</span>}
                  {eta != null && <span>{t('cl.s2d.eta', { time: formatDuration(eta * 1000) })}</span>}
                </div>
              </li>
            );
          })}
        </ul>
      )}
      {ended.length > 0 && (
        <div>
          <div className="mb-1 text-sm font-semibold">{t('cl.jobs.ended')}</div>
          <p className="mb-2 text-xs text-muted-foreground">{t('cl.jobs.endedHelp')}</p>
          <Table containerClassName="max-h-72 rounded-md border">
            <TableHeader><TableRow>
              <TableHead>{t('common.name')}</TableHead><TableHead>{t('cl.jobs.outcome')}</TableHead>
              <TableHead>{t('cl.jobs.lastProgress')}</TableHead><TableHead>{t('cl.jobs.lastSeen')}</TableHead>
            </TableRow></TableHeader>
            <TableBody>
              {ended.map((j) => (
                <TableRow key={j.key}>
                  <TableCell><div className="font-medium [overflow-wrap:anywhere]">{j.name}</div>
                    {j.resources.length > 0 && <div className="text-xs text-muted-foreground">{j.resources.join(', ')}</div>}
                    {j.error && <div className="text-xs text-destructive [overflow-wrap:anywhere]">{j.error}</div>}</TableCell>
                  <TableCell><StatusBadge tone={j.outcome === 'Completed' ? 'ok' : j.outcome === 'Failed' ? 'danger' : 'muted'}>{t(('cl.jobs.outcome.' + j.outcome) as TKey)}</StatusBadge></TableCell>
                  <TableCell className="text-xs tabular">{j.percentComplete != null ? `${j.percentComplete}%` : '—'}{j.state && j.outcome === 'NotReported' ? ` · ${j.state}` : ''}</TableCell>
                  <TableCell className="text-xs whitespace-nowrap tabular">{fmtDate(j.lastSeenUtc)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </Section>
  );
}
