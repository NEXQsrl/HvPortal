import { Archive, RefreshCw } from 'lucide-react';
import { useCallback, useState } from 'react';
import { get } from '@/api/client';
import type { Vm } from '@/api/types';
import { ErrorAlert, Loading, Notice, StatCard } from '@/components/common';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLive, useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';
import type { MountInfo, Repository, VmBackupsResponse } from './backup-api';
import { BackupNowDialog } from './BackupNowDialog';
import { MountBrowseDialog, MountsList } from './FileRestore';
import { PointsTable, usePointActions } from './PointActions';

/** "Backups" tab of a VM: its restore points, what can be done with them, and "Back up now" (GET /api/v1/vms/{id}/backups). */
export function VmBackups({ vm }: { vm: Vm }) {
  const { t, fmtDate } = useI18n();
  const ops = useOps();
  const { can } = useSession();
  const data = useLoad(() => get<VmBackupsResponse>(`/api/v1/vms/${vm.id}/backups`), [vm.id]);
  // Repository details (host of a local folder, credentials) refine the hosts offered for restores; readable with a global backup permission.
  const globalBackup = can('backup.manage') || can('backup.run') || can('backup.restore');
  const repos = useLoad(() => (globalBackup ? get<Repository[]>('/api/v1/backup/repositories').catch(() => [] as Repository[]) : Promise.resolve([] as Repository[])), [globalBackup]);
  const mounts = useLoad(() => get<MountInfo[]>('/api/v1/backup/mounts').catch(() => [] as MountInfo[]), []);
  const reload = useCallback(() => { data.reload(); mounts.reload(); }, [data.reload, mounts.reload]);
  // A restore point changes when its task ends (backup copied, verified, deleted): follow the tasks.
  useLive(reload, ['jobChanged'], 800);
  const [backingUp, setBackingUp] = useState(false);
  const [browsing, setBrowsing] = useState<MountInfo | null>(null);
  const r = data.data;
  const pa = usePointActions({ can: { manage: !!r?.canManage, run: !!r?.canRun, restore: !!r?.canRestore }, repositories: repos.data ?? undefined, onChanged: reload });
  const points = r?.points ?? [];
  const pointIds = new Set(points.map((p) => p.id));
  const myMounts = (mounts.data ?? []).filter((m) => pointIds.has(m.pointId));
  const lastGood = points.find((p) => p.status === 'Ok');
  const total = points.filter((p) => p.status === 'Ok' || p.status === 'Unverified').reduce((s, p) => s + p.sizeBytes, 0);
  const latest = points[0];

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b px-4 py-2">
        {r?.canRun && vm.canBackup && <Button size="sm" onClick={() => setBackingUp(true)} disabled={vm.isMissing}><Archive />{t('bk.backupNow')}</Button>}
        <Button size="sm" variant="outline" onClick={reload} disabled={data.loading}><RefreshCw className={cn(data.loading && 'animate-spin')} />{t('common.refresh')}</Button>
      </div>
      <div className="min-h-0 flex-1 space-y-3 overflow-auto p-4">
        <ErrorAlert error={data.error} />
        {data.loading && !r ? <Loading /> : r && !r.allowed ? <Notice kind="info">{t('bk.vm.notAllowed')}</Notice> : r && (
          <>
            <div className="grid gap-3 sm:grid-cols-3">
              <StatCard label={t('bk.vm.lastGood')} value={lastGood ? fmtDate(lastGood.createdAt) : t('common.never')}
                sub={lastGood ? lastGood.repositoryName : t('bk.vm.noGood')} className={cn(!lastGood && 'border-warning/40')} />
              <StatCard label={t('bk.vm.points')} value={points.filter((p) => p.status === 'Ok' || p.status === 'Unverified').length} sub={formatBytes(total)} />
              <StatCard label={t('bk.vm.latest')} value={latest ? fmtDate(latest.createdAt) : '—'} sub={latest ? t(('bk.pointStatus.' + latest.status) as TKey) : undefined} />
            </div>
            {latest && (latest.status === 'Failed' || latest.status === 'Unverified') && (
              <Notice kind={latest.status === 'Failed' ? 'danger' : 'warn'} title={t(latest.status === 'Failed' ? 'bk.vm.latestFailed' : 'bk.vm.latestUnverified')}>{latest.error ?? t('bk.pointStatusHelp.Unverified')}</Notice>
            )}
            <MountsList mounts={myMounts} onChanged={mounts.reload} onBrowse={setBrowsing} />
            <div className="rounded-lg border bg-card">
              <PointsTable points={points} actions={pa.actions} canRestore={r.canRestore} onRestore={(p) => pa.open('restore', p)} empty={t('bk.vm.none')} />
            </div>
            <p className="text-xs text-muted-foreground">{t('bk.vm.note')}</p>
          </>
        )}
      </div>
      {pa.dialogs}
      {backingUp && <BackupNowDialog vm={vm} onClose={() => setBackingUp(false)} onQueued={(a, label) => { ops.queued(a, label); reload(); }} />}
      {browsing && <MountBrowseDialog mount={browsing} onClose={() => setBrowsing(null)} onChanged={mounts.reload} />}
    </div>
  );
}
