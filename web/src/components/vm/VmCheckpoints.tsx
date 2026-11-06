import { Camera, CornerDownRight, MapPin, RefreshCw, RotateCcw, Trash2 } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { get, newIdempotencyKey, post } from '@/api/client';
import type { Accepted, CheckpointInfo, CheckpointsResponse } from '@/api/ops-types';
import { realtime } from '@/api/realtime';
import type { Vm } from '@/api/types';
import { Empty, ErrorAlert, Loading, Notice, StatusBadge } from '@/components/common';
import { ActionDialog, ConfirmTypedDialog } from '@/components/dialogs';
import { useOps } from '@/components/ops/OperationsProvider';
import { CreateCheckpointDialog } from '@/components/ops/VmDialogs';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';

/** Checkpoint tree read live from the host (Get-VMSnapshot), with create / apply / delete as jobs. */
export function VmCheckpoints({ vm }: { vm: Vm }) {
  const { t, fmtDate } = useI18n();
  const ops = useOps();
  const data = useLoad(() => get<CheckpointsResponse>(`/api/v1/vms/${vm.id}/checkpoints`), [vm.id]);
  const [creating, setCreating] = useState(false);
  const [applying, setApplying] = useState<CheckpointInfo | null>(null);
  const [deleting, setDeleting] = useState<CheckpointInfo | null>(null);
  const [jobs, setJobs] = useState<Set<string>>(new Set());

  useEffect(() => realtime.on('jobChanged', (p) => {
    if (p.jobId && jobs.has(p.jobId) && p.status && !['Queued', 'Running', 'Reconciling'].includes(p.status)) {
      setJobs((s) => { const n = new Set(s); n.delete(p.jobId!); return n; });
      data.reload();
    }
  }), [jobs, data]);

  const queued = (r: Accepted, label: string) => { setJobs((s) => new Set(s).add(r.jobId)); ops.queued(r, label); };

  // Depth-first order with indentation (parent before children).
  const rows = useMemo(() => {
    const list = data.data?.checkpoints ?? [];
    const ids = new Set(list.map((c) => c.id));
    const out: { cp: CheckpointInfo; depth: number }[] = [];
    const walk = (parent: string | null, depth: number) => {
      for (const c of list.filter((x) => (x.parentId && ids.has(x.parentId) ? x.parentId : null) === parent).sort((a, b) => a.createdUtc.localeCompare(b.createdUtc))) {
        out.push({ cp: c, depth });
        walk(c.id, depth + 1);
      }
    };
    walk(null, 0);
    return out;
  }, [data.data]);
  const hasChildren = (id: string) => (data.data?.checkpoints ?? []).some((c) => c.parentId === id);
  const vmOff = vm.powerState === 'Off' || vm.powerState === 'Saved';
  // Agent 0.5.0+: a running VM is turned off by the agent, then the checkpoint is applied.
  const canApplyNow = vmOff || !!data.data?.applyWhileRunning;

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b px-4 py-2">
        {data.data?.canCreate && <Button size="sm" onClick={() => setCreating(true)} disabled={vm.isMissing}><Camera />{t('cp.create')}</Button>}
        <Button size="sm" variant="outline" onClick={data.reload} disabled={data.loading}><RefreshCw className={cn(data.loading && 'animate-spin')} />{t('common.refresh')}</Button>
        <span className="flex-1" />
        {jobs.size > 0 && <StatusBadge tone="info">{t('jobstatus.Running')}</StatusBadge>}
      </div>
      <div className="px-4 pt-3"><Notice kind="warn">{t('cp.notBackup')}</Notice></div>
      {!canApplyNow && data.data?.canApply && <div className="px-4 pt-2 text-xs text-muted-foreground">{t('cp.requiresOff')}</div>}
      {canApplyNow && !vmOff && data.data?.canApply && rows.length > 0 && <div className="px-4 pt-2 text-xs text-muted-foreground">{t('cp.applyRunningHint')}</div>}
      <ErrorAlert error={data.error} className="mx-4" />
      {data.loading && !data.data ? <Loading /> : rows.length === 0 ? <Empty icon={<Camera className="size-6" />}>{t('cp.none')}</Empty> : (
        <Table containerClassName="mt-2 min-h-0 flex-1">
          <TableHeader>
            <TableRow>
              <TableHead>{t('cp.name')}</TableHead>
              <TableHead>{t('common.type')}</TableHead>
              <TableHead>{t('cp.created')}</TableHead>
              <TableHead className="w-48"><span className="sr-only">{t('vm.actions')}</span></TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map(({ cp, depth }) => (
              <TableRow key={cp.id}>
                <TableCell>
                  <div className="flex items-center gap-1.5" style={{ paddingLeft: depth * 18 }}>
                    {depth > 0 && <CornerDownRight className="size-3.5 text-muted-foreground" />}
                    <Camera className="size-4 text-info" />
                    <span className="font-medium">{cp.name}</span>
                    {cp.isCurrent && <StatusBadge tone="info"><MapPin />{t('cp.current')}</StatusBadge>}
                  </div>
                </TableCell>
                <TableCell className="text-xs text-muted-foreground">{cp.type ?? '—'}</TableCell>
                <TableCell className="whitespace-nowrap tabular">{fmtDate(cp.createdUtc)}</TableCell>
                <TableCell className="text-right whitespace-nowrap">
                  {data.data?.canApply && (
                    <>
                      <Button size="xs" variant="outline" disabled={!canApplyNow} title={canApplyNow ? undefined : t('cp.requiresOff')} onClick={() => setApplying(cp)}><RotateCcw />{t('cp.apply')}</Button>{' '}
                      <Button size="xs" variant="ghost" className="text-destructive" onClick={() => setDeleting(cp)}><Trash2 />{t('cp.delete')}</Button>
                    </>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {creating && <CreateCheckpointDialog vm={vm} onQueued={queued} onClose={() => setCreating(false)} />}
      {applying && (
        <ConfirmTypedDialog title={t('cp.applyTitle', { name: applying.name })} name={vm.name} impact={vmOff ? t('cp.applyImpact') : `${t('cp.applyImpact')} ${t('cp.applyRunningImpact')}`} confirmLabel={t('cp.apply').replace('…', '')}
          onClose={() => setApplying(null)}
          onConfirm={async (typed) => {
            const r = await post<Accepted>(`/api/v1/vms/${vm.id}/checkpoints/${applying.id}/apply`, { confirmName: typed, idempotencyKey: newIdempotencyKey() });
            queued(r, `${t('jobop.vm.checkpoint.Apply')} — ${vm.name}`);
          }} />
      )}
      {deleting && <DeleteCheckpointDialog vmId={vm.id} vmName={vm.name} cp={deleting} withChildren={hasChildren(deleting.id)} onQueued={queued} onClose={() => setDeleting(null)} />}
    </div>
  );
}

function DeleteCheckpointDialog({ vmId, vmName, cp, withChildren, onQueued, onClose }: {
  vmId: string; vmName: string; cp: CheckpointInfo; withChildren: boolean; onQueued: (r: Accepted, label: string) => void; onClose: () => void;
}) {
  const { t } = useI18n();
  const [children, setChildren] = useState(false);
  return (
    <ActionDialog title={t('cp.deleteTitle', { name: cp.name })} confirmLabel={t('common.delete')} danger onClose={onClose}
      onConfirm={async () => {
        const r = await post<Accepted>(`/api/v1/vms/${vmId}/checkpoints/${cp.id}/delete`, { includeChildren: children, idempotencyKey: newIdempotencyKey() });
        onQueued(r, `${t('jobop.vm.checkpoint.Delete')} — ${vmName}`);
      }}>
      <p className="text-sm">{t('cp.deleteImpact')}</p>
      {withChildren && <label className="flex items-center gap-2 text-sm"><Checkbox checked={children} onCheckedChange={(v) => setChildren(!!v)} />{t('cp.includeChildren')}</label>}
    </ActionDialog>
  );
}
