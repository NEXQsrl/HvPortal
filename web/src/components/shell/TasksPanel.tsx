import { ChevronUp, ListChecks } from 'lucide-react';
import { useCallback, useEffect, useRef } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { get } from '@/api/client';
import { realtime } from '@/api/realtime';
import type { Job } from '@/api/types';
import { rowLink } from '@/components/actions';
import { JobBadge } from '@/components/common';
import { Button } from '@/components/ui/button';
import { Progress } from '@/components/ui/progress';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { isActive, jobLabel, watchedJobs } from '@/lib/jobs';
import { useLive, useLoad, useLocalStorage } from '@/lib/hooks';
import { toast } from '@/lib/toast';
import { cn } from '@/lib/utils';

type Row = { job: Job; requestedBy: string | null };

/** "Recent tasks" panel docked at the bottom (as in hypervisor consoles); notifies when tasks started here finish. */
export function TasksPanel() {
  const { t, fmtDate } = useI18n();
  const navigate = useNavigate();
  const [open, setOpen] = useLocalStorage('hvp.tasks.open', false);
  const jobs = useLoad(() => get<Row[]>('/api/v1/jobs?take=25'));
  const reload = useCallback(() => jobs.reload(), [jobs.reload]);
  useLive(reload, ['jobChanged'], 400);

  // Toast when a watched job reaches a final state.
  const seen = useRef(new Map<string, string>());
  useEffect(() => {
    for (const { job } of jobs.data ?? []) {
      const label = watchedJobs.get(job.id);
      if (!label || isActive(job.status) || seen.current.get(job.id) === job.status) continue;
      seen.current.set(job.id, job.status);
      watchedJobs.delete(job.id);
      const link = <Link className="text-primary hover:underline" to={`/jobs/${job.id}`}>{t('job.openTask')}</Link>;
      if (job.status === 'Succeeded') toast.success(t('job.finished'), <span>{label} · {link}</span>);
      else toast.error(t('job.failedToast'), <span>{label}: {job.resultMessage ?? job.status} · {link}</span>);
      // Results change inventory: make sure every view refreshes even if the agent push is delayed.
      realtime.emit('inventoryChanged', {});
    }
  }, [jobs.data, t]);

  const rows = jobs.data ?? [];
  const running = rows.filter((r) => isActive(r.job.status)).length;
  return (
    <div className={cn('tasks-panel flex shrink-0 flex-col border-t bg-panel', open ? 'h-56' : 'h-9')}>
      <div className="flex h-9 shrink-0 items-center gap-2 px-3">
        <button type="button" className="flex items-center gap-2 text-xs font-semibold" onClick={() => setOpen(!open)} aria-expanded={open} aria-label={t('tasks.toggle')}>
          <ChevronUp className={cn('size-4 transition-transform', !open && 'rotate-180')} />
          <ListChecks className="size-4 text-muted-foreground" />
          {t('tasks.recent')}
        </button>
        {running > 0 && <span className="rounded-md bg-info/10 px-1.5 py-0.5 text-[11px] font-medium text-info">{t('tasks.running', { n: running })}</span>}
        {!open && rows[0] && (
          <span className="hidden min-w-0 truncate text-xs text-muted-foreground md:inline">
            {jobLabel(t, rows[0].job)} — {rows[0].job.targetName} · {t(('jobstatus.' + rows[0].job.status) as never)}
          </span>
        )}
        <span className="flex-1" />
        <Button variant="link" size="xs" asChild><Link to="/jobs">{t('tasks.viewAll')}</Link></Button>
      </div>
      {open && (
        rows.length === 0 ? <div className="px-4 py-3 text-xs text-muted-foreground">{t('tasks.none')}</div> : (
          <Table containerClassName="min-h-0 flex-1 border-t">
            <TableHeader>
              <TableRow>
                <TableHead>{t('job.operation')}</TableHead>
                <TableHead>{t('job.target')}</TableHead>
                <TableHead>{t('job.status')}</TableHead>
                <TableHead className="hidden md:table-cell">{t('job.by')}</TableHead>
                <TableHead className="hidden md:table-cell">{t('job.created')}</TableHead>
                <TableHead className="hidden lg:table-cell">{t('job.completed')}</TableHead>
                <TableHead>{t('job.result')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map(({ job, requestedBy }) => (
                <TableRow key={job.id} {...rowLink(() => navigate(`/jobs/${job.id}`))} className="cursor-pointer text-xs">
                  <TableCell className="font-medium whitespace-nowrap">{jobLabel(t, job)}</TableCell>
                  <TableCell className="max-w-48 truncate">{job.targetName}</TableCell>
                  <TableCell className="whitespace-nowrap">
                    <div className="flex items-center gap-2"><JobBadge status={job.status} />
                      {job.status === 'Running' && <Progress value={job.progress} className="h-1.5 w-16" />}</div>
                  </TableCell>
                  <TableCell className="hidden md:table-cell">{requestedBy}</TableCell>
                  <TableCell className="hidden whitespace-nowrap md:table-cell tabular">{fmtDate(job.createdAt)}</TableCell>
                  <TableCell className="hidden whitespace-nowrap lg:table-cell tabular">{fmtDate(job.completedAt)}</TableCell>
                  <TableCell className="max-w-96 truncate text-muted-foreground" title={job.resultMessage ?? job.progressMessage ?? ''}>{job.resultMessage ?? job.progressMessage}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )
      )}
    </div>
  );
}
