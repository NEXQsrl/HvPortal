import { ListChecks, X } from 'lucide-react';
import { useCallback, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { get, post } from '@/api/client';
import type { Job } from '@/api/types';
import { ErrorAlert, JobBadge, KeyValue, Loading, PageHeader, Section, StatusBadge } from '@/components/common';
import { JobTable } from '@/components/tables';
import { Button } from '@/components/ui/button';
import { NativeSelect } from '@/components/ui/native-select';
import { Progress } from '@/components/ui/progress';
import { Table, TableBody, TableCell, TableRow } from '@/components/ui/table';
import { useI18n, type TKey } from '@/i18n';
import { isActive, jobLabel } from '@/lib/jobs';
import { useLive, useLoad } from '@/lib/hooks';

const STATUSES = ['Queued', 'Running', 'Reconciling', 'Succeeded', 'Unverified', 'Failed', 'Cancelled', 'Unknown'];

export function JobsPage() {
  const { t } = useI18n();
  const [status, setStatus] = useState('');
  const jobs = useLoad(() => get<{ job: Job; requestedBy: string | null }[]>(`/api/v1/jobs?take=500${status ? '&status=' + status : ''}`), [status]);
  const reload = useCallback(() => jobs.reload(), [jobs.reload]);
  useLive(reload, ['jobChanged'], 500);
  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<ListChecks className="size-5" />} title={t('nav.jobs')} subtitle={`${(jobs.data ?? []).filter((j) => isActive(j.job.status)).length} ${t('tasks.running', { n: '' }).trim()}`}
        actions={
          <NativeSelect aria-label={t('job.status')} value={status} onChange={(e) => setStatus(e.target.value)} className="w-44">
            <option value="">{t('common.all')}</option>
            {STATUSES.map((s) => <option key={s} value={s}>{t(('jobstatus.' + s) as TKey)}</option>)}
          </NativeSelect>
        } />
      <ErrorAlert error={jobs.error} className="mx-4" />
      {jobs.loading && !jobs.data ? <Loading /> : <JobTable rows={jobs.data ?? []} className="flex-1" />}
    </div>
  );
}

export function JobDetailPage() {
  const { id } = useParams();
  const { t, fmtDate } = useI18n();
  const data = useLoad(() => get<{ job: Job; events: { id: number; at: string; kind: string; message: string | null }[] }>(`/api/v1/jobs/${id}`), [id]);
  const reload = useCallback(() => data.reload(), [data.reload]);
  useLive(reload, ['jobChanged'], 300);
  const [error, setError] = useState<unknown>(null);
  if (data.loading && !data.data) return <Loading />;
  if (!data.data) return <div className="p-4"><ErrorAlert error={data.error} /></div>;
  const j = data.data.job;
  const targetLink = j.targetType === 'Vm' ? `/vms/${j.targetId}` : j.targetType === 'Host' ? `/hosts/${j.targetId}` : null;
  const cancel = async () => {
    setError(null);
    try { await post(`/api/v1/jobs/${j.id}/cancel`); data.reload(); } catch (e) { setError(e); }
  };
  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<ListChecks className="size-5" />} title={`${jobLabel(t, j)} — ${j.targetName ?? ''}`} badges={<JobBadge status={j.status} />}
        subtitle={<span className="font-mono">{j.id}</span>}
        actions={j.cancellable && <Button size="sm" variant="outline" onClick={() => void cancel()}><X />{t('job.cancel')}</Button>} />
      <div className="min-h-0 flex-1 space-y-3 overflow-auto p-4">
        <ErrorAlert error={error} />
        <Section bodyClassName="space-y-3 p-3">
          <KeyValue items={[
            [t('job.status'), <span key="s" className="flex items-center gap-2"><JobBadge status={j.status} />{j.errorCode && <span className="font-mono text-xs">({j.errorCode}{j.hyperVCode != null ? ` / Hyper-V ${j.hyperVCode}` : ''})</span>}</span>],
            [t('job.result'), j.resultMessage ?? j.progressMessage ?? '—'],
            [t('job.target'), targetLink ? <Link className="text-primary hover:underline" to={targetLink}>{j.targetName}</Link> : j.targetName],
            [t('job.created'), fmtDate(j.createdAt)],
            [t('job.completed'), fmtDate(j.completedAt)],
            ['Correlation', <span key="c" className="font-mono text-xs">{j.correlationId}</span>],
          ]} />
          {(j.status === 'Running' || j.status === 'Queued') && <Progress value={j.progress} className="h-2" />}
          {j.status === 'Unknown' && <StatusBadge tone="danger">{t('jobstatus.Unknown')}</StatusBadge>}
        </Section>
        {j.resultJson && (
          <Section title="Hyper-V">
            <pre className="command m-3 max-h-64 overflow-auto">{(() => { try { return JSON.stringify(JSON.parse(j.resultJson), null, 2); } catch { return j.resultJson; } })()}</pre>
          </Section>
        )}
        <Section title={t('job.events')}>
          <Table>
            <TableBody>
              {data.data.events.map((e) => (
                <TableRow key={e.id}>
                  <TableCell className="w-44 text-xs whitespace-nowrap tabular">{fmtDate(e.at)}</TableCell>
                  <TableCell className="w-32"><StatusBadge tone="info">{e.kind}</StatusBadge></TableCell>
                  <TableCell className="text-sm">{e.message}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Section>
      </div>
    </div>
  );
}
