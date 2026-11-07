import { FolderSearch, HardDrive, Info, ListChecks, Pin, PinOff, RotateCcw, ShieldCheck, Trash2 } from 'lucide-react';
import { useCallback, useState, type ReactNode } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { get, post } from '@/api/client';
import type { Accepted } from '@/api/ops-types';
import { KebabMenu, type Action } from '@/components/actions';
import { Empty, ErrorAlert, KeyValue, Loading, Mono, Notice, StatusBadge, errorText } from '@/components/common';
import { ConfirmTypedDialog } from '@/components/dialogs';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { toast } from '@/lib/toast';
import type { BackupManifest, BackupPoint, Repository } from './backup-api';
import { CheckpointUsed, PointStatusBadge, VerifyState, checkpointRequested } from './BackupCommon';
import { FileRestoreDialog } from './FileRestore';
import { RestoreDiskDialog, RestoreVmDialog } from './RestoreDialogs';

type Dlg = { kind: 'restore' | 'disk' | 'files' | 'delete' | 'details'; point: BackupPoint };

export const isUsable = (p: BackupPoint) => p.status === 'Ok' || p.status === 'Unverified';

/**
 * Everything that can be done with a restore point, and its dialogs: the same menu in the backup area and in the
 * Backups tab of a VM. Flags only hide what would be refused; the server authorizes each request.
 */
export function usePointActions({ can, repositories, onChanged }: {
  can: { manage: boolean; run: boolean; restore: boolean }; repositories?: Repository[]; onChanged: () => void;
}) {
  const { t, fmtDate } = useI18n();
  const ops = useOps();
  const navigate = useNavigate();
  const [dialog, setDialog] = useState<Dlg | null>(null);
  const close = useCallback(() => setDialog(null), []);
  const repoOf = (p: BackupPoint) => repositories?.find((r) => r.id === p.repositoryId);
  const queued = useCallback((r: Accepted, label: string) => { ops.queued(r, label); onChanged(); }, [ops, onChanged]);

  const actions = (p: BackupPoint): Action[] => {
    const list: Action[] = [{ key: 'details', label: t('bk.point.details'), icon: Info, onSelect: () => setDialog({ kind: 'details', point: p }) }];
    if (can.restore && isUsable(p)) {
      list.push({ key: 'restore', label: t('bk.point.restore'), icon: RotateCcw, separatorBefore: true, onSelect: () => setDialog({ kind: 'restore', point: p }) });
      if (p.disks.length > 0) {
        list.push({ key: 'disk', label: t('bk.point.restoreDisk'), icon: HardDrive, onSelect: () => setDialog({ kind: 'disk', point: p }) });
        list.push({ key: 'files', label: t('bk.point.files'), icon: FolderSearch, onSelect: () => setDialog({ kind: 'files', point: p }) });
      }
    }
    if (can.run && isUsable(p)) {
      list.push({
        key: 'verify', label: t('bk.point.verify'), icon: ShieldCheck, separatorBefore: true,
        onSelect: async () => {
          try {
            const r = await post<Accepted>(`/api/v1/backup/points/${p.id}/verify`);
            queued(r, `${t('jobop.backup.verify.Verify')} — ${p.vmName} (${fmtDate(p.createdAt)})`);
          } catch (e) { toast.error(t('common.error'), errorText(e)); }
        },
      });
    }
    if (can.manage && p.status !== 'Deleting' && p.status !== 'Deleted') {
      list.push({
        key: 'keep', label: t(p.keep ? 'bk.point.unkeep' : 'bk.point.keep'), icon: p.keep ? PinOff : Pin, separatorBefore: !(can.run && isUsable(p)), hint: t('bk.point.keepHelp'),
        onSelect: async () => {
          try {
            await post(`/api/v1/backup/points/${p.id}/keep`, { keep: !p.keep });
            toast.success(t(p.keep ? 'bk.point.unkept' : 'bk.point.kept'));
            onChanged();
          } catch (e) { toast.error(t('common.error'), errorText(e)); }
        },
      });
    }
    if (p.jobId) list.push({ key: 'task', label: t('bk.point.task'), icon: ListChecks, onSelect: () => navigate(`/jobs/${p.jobId}`) });
    if (can.manage && (p.status === 'Ok' || p.status === 'Unverified' || p.status === 'Failed')) {
      list.push({ key: 'delete', label: t('bk.point.delete'), icon: Trash2, danger: true, separatorBefore: true, onSelect: () => setDialog({ kind: 'delete', point: p }) });
    }
    return list;
  };

  const d = dialog;
  const dialogs: ReactNode = (
    <>
      {d?.kind === 'details' && <PointDetailsDialog point={d.point} onClose={close} />}
      {d?.kind === 'restore' && <RestoreVmDialog point={d.point} repo={repoOf(d.point)} onQueued={queued} onClose={close} />}
      {d?.kind === 'disk' && <RestoreDiskDialog point={d.point} repo={repoOf(d.point)} onQueued={queued} onClose={close} />}
      {d?.kind === 'files' && <FileRestoreDialog point={d.point} repo={repoOf(d.point)} onClose={close} onChanged={onChanged} />}
      {d?.kind === 'delete' && (
        <ConfirmTypedDialog title={t('bk.point.deleteTitle', { name: d.point.vmName })} name={d.point.vmName} confirmLabel={t('bk.point.delete')} onClose={close}
          impact={t('bk.point.deleteImpact', { date: fmtDate(d.point.createdAt), repo: d.point.repositoryName ?? '—' })}
          onConfirm={async (typed) => {
            const r = await post<{ jobId: string }>(`/api/v1/backup/points/${d.point.id}/delete`, { confirmName: typed });
            queued({ jobId: r.jobId, created: true }, `${t('jobop.backup.remove.Delete')} — ${d.point.vmName} (${fmtDate(d.point.createdAt)})`);
          }}>
          {d.point.keep && <Notice kind="warn">{t('bk.point.deleteKept')}</Notice>}
        </ConfirmTypedDialog>
      )}
    </>
  );
  return { actions, dialogs, open: (kind: Dlg['kind'], point: BackupPoint) => setDialog({ kind, point }) };
}

/** Everything recorded about a restore point (GET /backup/points/{id}): state, checks and the manifest written by the agent. */
export function PointDetailsDialog({ point, onClose }: { point: BackupPoint; onClose: () => void }) {
  const { t, fmtDate } = useI18n();
  const data = useLoad(() => get<{ point: BackupPoint; manifest: BackupManifest | null }>(`/api/v1/backup/points/${point.id}`), [point.id]);
  const p = data.data?.point ?? point;
  const m = data.data?.manifest ?? null;
  const [showFiles, setShowFiles] = useState(false);
  return (
    <Dialog open onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="flex max-h-[calc(100dvh-2rem)] flex-col overflow-hidden sm:max-w-3xl">
        <DialogHeader className="shrink-0 pr-6">
          <DialogTitle>{t('bk.point.detailsTitle', { name: p.vmName })}</DialogTitle>
          <DialogDescription>{fmtDate(p.createdAt)}</DialogDescription>
        </DialogHeader>
        <div className="-mx-4 min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-0.5">
          <ErrorAlert error={data.error} />
          {p.status === 'Unverified' && <Notice kind="warn">{t('bk.pointStatusHelp.Unverified')}</Notice>}
          {p.error && <Notice kind={p.status === 'Failed' ? 'danger' : 'warn'} title={t('bk.point.error')}>{p.error}</Notice>}
          <KeyValue items={[
            [t('common.status'), <span key="s" className="flex flex-wrap items-center gap-1.5"><PointStatusBadge status={p.status} />{p.keep && <StatusBadge tone="info"><Pin />{t('bk.point.kept')}</StatusBadge>}</span>],
            [t('bk.point.vm'), p.vmExists ? <Link key="v" className="text-primary hover:underline" to={`/vms/${p.vmId}`}>{p.vmName}</Link> : <span key="v">{p.vmName} <span className="text-xs text-muted-foreground">({t('bk.point.vmGone')})</span></span>],
            [t('bk.point.source'), p.jobName ?? (p.backupJobId ? t('bk.point.jobGone') : t('bk.point.adhoc'))],
            [t('bk.point.repository'), p.repositoryName ?? '—'],
            [t('bk.point.folder'), <Mono key="f" className="break-all">{p.folder}</Mono>],
            [t('bk.point.created'), fmtDate(p.createdAt)],
            [t('bk.point.completed'), fmtDate(p.completedAt)],
            [t('common.size'), p.sizeBytes > 0 ? formatBytes(p.sizeBytes) : '—'],
            [t('bk.point.cpRequested'), checkpointRequested(t, p.checkpointType)],
            [t('bk.point.cpUsed'), <CheckpointUsed key="c" point={p} />],
            [t('bk.point.verification'), <span key="vf"><VerifyState point={p} />{p.verifyDetail && <span className="mt-0.5 block text-xs text-muted-foreground">{p.verifyDetail}</span>}</span>],
            ...(p.testDetail ? [[t('bk.point.test'), p.testDetail] as [ReactNode, ReactNode]] : []),
            ...(p.jobId ? [[t('bk.point.task'), <Link key="j" className="text-primary hover:underline" to={`/jobs/${p.jobId}`}>{t('job.openTask')}</Link>] as [ReactNode, ReactNode]] : []),
          ]} />
          {data.loading && !data.data ? <Loading /> : m ? (
            <section className="space-y-2 rounded-lg border p-3">
              <h3 className="text-sm font-semibold">{t('bk.manifest.title')}</h3>
              <KeyValue items={[
                [t('bk.manifest.vm'), `${m.vmName ?? '—'}${m.hostName ? ` · ${m.hostName}` : ''}`],
                [t('bk.manifest.hardware'), `Gen ${m.generation} · ${m.processorCount} vCPU · ${formatBytes(m.memoryStartupMb * 1048576)}${m.configurationVersion ? ` · v${m.configurationVersion}` : ''}`],
                [t('bk.manifest.network'), m.networkAdapters.length === 0 ? '—' : m.networkAdapters.map((n) => `${n.name ?? '?'} → ${n.switchName ?? t('bk.manifest.notConnected')}${n.vlanId ? ` (VLAN ${n.vlanId})` : ''}`).join(' · ')],
                [t('bk.manifest.disks'), <span key="d" className="space-y-0.5">{m.disks.map((x) => <Mono key={x} className="block break-all">{x}</Mono>)}</span>],
                [t('bk.manifest.files'), t('bk.manifest.filesCount', { n: m.files.length, size: formatBytes(m.totalBytes) })],
                [t('bk.manifest.agent'), m.agentVersion ?? '—'],
              ]} />
              {m.warnings.length > 0 && <Notice kind="warn" title={t('bk.manifest.warnings')}>{m.warnings.join(' ')}</Notice>}
              {m.files.length > 0 && (
                <div>
                  <Button type="button" size="xs" variant="ghost" onClick={() => setShowFiles(!showFiles)}>{t(showFiles ? 'bk.manifest.hideFiles' : 'bk.manifest.showFiles')}</Button>
                  {showFiles && (
                    <Table containerClassName="max-h-64 rounded-md border">
                      <TableHeader><TableRow><TableHead>{t('files.name')}</TableHead><TableHead className="text-right">{t('common.size')}</TableHead><TableHead>SHA-256</TableHead></TableRow></TableHeader>
                      <TableBody>
                        {m.files.map((f) => (
                          <TableRow key={f.path}>
                            <TableCell className="font-mono text-[11px] break-all">{f.path}</TableCell>
                            <TableCell className="text-right text-xs whitespace-nowrap tabular">{formatBytes(f.sizeBytes)}</TableCell>
                            <TableCell className="max-w-40 truncate font-mono text-[11px]" title={f.sha256 ?? ''}>{f.sha256 ?? '—'}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  )}
                </div>
              )}
            </section>
          ) : data.data && <p className="text-xs text-muted-foreground">{t('bk.manifest.none')}</p>}
        </div>
        <DialogFooter className="shrink-0"><Button type="button" variant="outline" onClick={onClose}>{t('common.close')}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Restore points as a table: the VM column only in the backup area (the VM tab is about one VM). */
export function PointsTable({ points, showVm, actions, onRestore, canRestore, empty, className }: {
  points: BackupPoint[]; showVm?: boolean; actions: (p: BackupPoint) => Action[]; onRestore?: (p: BackupPoint) => void; canRestore?: boolean; empty: ReactNode; className?: string;
}) {
  const { t, fmtDate } = useI18n();
  if (points.length === 0) return <Empty>{empty}</Empty>;
  return (
    <Table containerClassName={className}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('bk.point.created')}</TableHead>
          {showVm && <TableHead>{t('bk.point.vm')}</TableHead>}
          <TableHead>{t('common.status')}</TableHead>
          <TableHead className="hidden lg:table-cell">{t('bk.point.source')}</TableHead>
          <TableHead className="hidden md:table-cell">{t('bk.point.repository')}</TableHead>
          <TableHead className="text-right">{t('common.size')}</TableHead>
          <TableHead className="hidden xl:table-cell">{t('bk.point.checkpoint')}</TableHead>
          <TableHead className="hidden md:table-cell">{t('bk.point.verification')}</TableHead>
          <TableHead><span className="sr-only">{t('common.actions')}</span></TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {points.map((p) => (
          <TableRow key={p.id} className="backup-point-row align-top">
            <TableCell className="text-xs whitespace-nowrap tabular">
              <div className="flex items-center gap-1">{fmtDate(p.createdAt)}{p.keep && <Pin className="size-3 text-info" aria-label={t('bk.point.kept')} />}</div>
            </TableCell>
            {showVm && (
              <TableCell className="max-w-48">
                {p.vmExists ? <Link className="font-medium text-primary hover:underline" to={`/vms/${p.vmId}?tab=backups`}>{p.vmName}</Link>
                  : <span className="font-medium">{p.vmName}</span>}
                {!p.vmExists && <div className="text-xs text-muted-foreground">{t('bk.point.vmGone')}</div>}
              </TableCell>
            )}
            <TableCell className="max-w-64 whitespace-normal">
              <PointStatusBadge status={p.status} />
              {p.error && <div className="mt-1 line-clamp-2 text-xs break-words text-muted-foreground" title={p.error}>{p.error}</div>}
            </TableCell>
            <TableCell className="hidden max-w-40 truncate text-xs lg:table-cell">{p.jobName ?? (p.backupJobId ? t('bk.point.jobGone') : t('bk.point.adhoc'))}</TableCell>
            <TableCell className="hidden max-w-40 truncate text-xs md:table-cell">{p.repositoryName ?? '—'}</TableCell>
            <TableCell className="text-right text-xs whitespace-nowrap tabular">{p.sizeBytes > 0 ? formatBytes(p.sizeBytes) : '—'}</TableCell>
            <TableCell className="hidden text-xs xl:table-cell"><CheckpointUsed point={p} /></TableCell>
            <TableCell className="hidden md:table-cell"><VerifyState point={p} /></TableCell>
            <TableCell className="text-right whitespace-nowrap">
              <div className="flex items-center justify-end gap-1">
                {canRestore && onRestore && isUsable(p) && <Button size="xs" variant="outline" onClick={() => onRestore(p)}><RotateCcw />{t('bk.point.restoreShort')}</Button>}
                <KebabMenu items={actions(p)} label={t('common.actions')} />
              </div>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
