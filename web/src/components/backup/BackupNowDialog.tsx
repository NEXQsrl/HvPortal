import { LoaderCircle, RefreshCw } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { ApiError, get, newIdempotencyKey, post } from '@/api/client';
import type { Accepted } from '@/api/ops-types';
import type { Vm } from '@/api/types';
import { ErrorAlert, Loading, Notice } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { NativeSelect } from '@/components/ui/native-select';
import { useI18n, type TKey } from '@/i18n';
import { useLoad } from '@/lib/hooks';
import { useSession } from '@/session';
import { CHECKPOINT_TYPES, type CheckpointType, type ReadinessResponse, type VmBackupsResponse } from './backup-api';
import { ReadinessList, checksForVm } from './BackupCommon';

/**
 * "Back up now" of one VM (POST /api/v1/vms/{id}/backup): choose the repository and how the copy is taken; the
 * readiness of the VM's host for that repository is checked first (GET /hosts/{id}/backup-readiness), so that what is
 * missing is shown with how to enable it before a job is queued. The job checks everything again on the host.
 */
export function BackupNowDialog({ vm, onQueued, onClose }: { vm: Vm; onQueued: (r: Accepted, label: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const { can } = useSession();
  const info = useLoad(() => get<VmBackupsResponse>(`/api/v1/vms/${vm.id}/backups`), [vm.id]);
  const repos = info.data?.repositories ?? [];
  const [repoId, setRepoId] = useState('');
  const [checkpoint, setCheckpoint] = useState<CheckpointType>('Production');
  const [verify, setVerify] = useState(true);
  const [anyway, setAnyway] = useState(false);
  const [key] = useState(newIdempotencyKey);
  const selected = repoId || repos[0]?.id || '';
  const repo = repos.find((r) => r.id === selected);
  // The readiness check needs hosts.manage or backup.manage at Global scope; without them the job checks on the host.
  const canCheck = can('hosts.manage') || can('backup.manage');
  const [readiness, setReadiness] = useState<{ data: ReadinessResponse | null; error: unknown; loading: boolean }>({ data: null, error: null, loading: false });
  const [tick, setTick] = useState(0);

  useEffect(() => {
    if (!canCheck || !selected) return;
    let cancelled = false;
    setReadiness({ data: null, error: null, loading: true });
    setAnyway(false);
    get<ReadinessResponse>(`/api/v1/hosts/${vm.hostId}/backup-readiness?repositoryId=${selected}`)
      .then((r) => { if (!cancelled) setReadiness({ data: r, error: null, loading: false }); })
      .catch((e) => { if (!cancelled) setReadiness({ data: null, error: e, loading: false }); });
    return () => { cancelled = true; };
  }, [canCheck, selected, vm.hostId, tick]);

  const checks = readiness.data ? checksForVm(readiness.data.checks, vm.hyperVId) : [];
  const critical = checks.some((c) => c.status === 'Critical');
  const readinessDenied = readiness.error instanceof ApiError && readiness.error.status === 403;
  const ready = !canCheck || readinessDenied || (!readiness.loading && (readiness.data !== null || readiness.error !== null));
  const valid = !!selected && ready && (!critical || anyway);

  return (
    <ActionDialog title={t('bk.now.title', { name: vm.name })} description={t('bk.now.subtitle', { host: vm.hostName ?? '—' })}
      confirmLabel={t('bk.now.confirm')} disabled={!valid} wide onClose={onClose}
      onConfirm={async () => {
        const r = await post<{ jobId: string; pointId: string; runId: string }>(`/api/v1/vms/${vm.id}/backup`, {
          repositoryId: selected, checkpointType: checkpoint, verifyAfterCopy: verify, idempotencyKey: key,
        });
        onQueued({ jobId: r.jobId, created: true }, `${t('jobop.backup.vm.Backup')} — ${vm.name} → ${repo?.name ?? ''}`);
      }}>
      {info.loading && !info.data ? <Loading /> : info.error ? <ErrorAlert error={info.error} /> : repos.length === 0 ? (
        <Notice kind="warn" title={t('bk.now.noRepoTitle')}>
          {t('bk.now.noRepo', { host: vm.hostName ?? '—' })}{' '}
          {can('backup.manage') && <Link className="text-primary hover:underline" to="/backup?tab=repositories" onClick={onClose}>{t('bk.now.openRepos')}</Link>}
        </Notice>
      ) : (
        <>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label={t('bk.now.repository')} htmlFor="bn-repo" hint={t('bk.now.repositoryHint')}>
              <NativeSelect id="bn-repo" value={selected} onChange={(e) => setRepoId(e.target.value)}>
                {repos.map((r) => <option key={r.id} value={r.id}>{r.name} — {r.path}</option>)}
              </NativeSelect>
            </Field>
            <Field label={t('bk.job.checkpoint')} htmlFor="bn-cp" hint={t('bk.job.checkpointHint')}>
              <NativeSelect id="bn-cp" value={checkpoint} onChange={(e) => setCheckpoint(e.target.value as CheckpointType)}>
                {CHECKPOINT_TYPES.map((c) => <option key={c} value={c}>{t(('bk.cp.' + c) as TKey)}</option>)}
              </NativeSelect>
            </Field>
          </div>
          <p className="text-xs text-muted-foreground">{t(('bk.cp.' + checkpoint + 'Help') as TKey)}</p>
          <label className="flex items-start gap-2 text-sm">
            <Checkbox checked={verify} onCheckedChange={(v) => setVerify(!!v)} className="mt-0.5" />
            <span>{t('bk.job.verifyAfterCopy')}<span className="block text-xs text-muted-foreground">{t('bk.job.verifyAfterCopyHelp')}</span></span>
          </label>

          <section className="space-y-2 rounded-lg border p-3" aria-live="polite">
            <div className="flex items-center gap-2">
              <h3 className="text-sm font-semibold">{t('bk.now.readiness')}</h3>
              <span className="flex-1" />
              {canCheck && <Button type="button" size="xs" variant="ghost" disabled={readiness.loading} onClick={() => setTick((x) => x + 1)}><RefreshCw />{t('bk.now.recheck')}</Button>}
            </div>
            {!canCheck || readinessDenied ? <p className="text-xs text-muted-foreground">{t('bk.now.readinessNoPermission')}</p>
              : readiness.loading ? <p className="flex items-center gap-2 text-sm text-muted-foreground"><LoaderCircle className="size-4 animate-spin" />{t('bk.now.checking')}</p>
              : readiness.error ? <><ErrorAlert error={readiness.error} /><p className="text-xs text-muted-foreground">{t('bk.now.readinessFailed')}</p></>
              : <ReadinessList checks={checks} hostId={vm.hostId} onNavigate={onClose} />}
          </section>
          {critical && (
            <label className="flex items-start gap-2 text-sm">
              <Checkbox checked={anyway} onCheckedChange={(v) => setAnyway(!!v)} className="mt-0.5" />
              <span>{t('bk.now.anyway')}</span>
            </label>
          )}
          <Notice kind="info">{t('bk.now.howItWorks')}</Notice>
        </>
      )}
    </ActionDialog>
  );
}
