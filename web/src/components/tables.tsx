import { useNavigate } from 'react-router-dom';
import type { InventoryEvent, Job } from '@/api/types';
import { rowLink } from '@/components/actions';
import { Empty, JobBadge, StatusBadge } from '@/components/common';
import { Progress } from '@/components/ui/progress';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { jobLabel } from '@/lib/jobs';
import { cn } from '@/lib/utils';

export function EventsTable({ events, className }: { events: InventoryEvent[]; className?: string }) {
  const { fmtDate } = useI18n();
  if (events.length === 0) return <Empty>—</Empty>;
  return (
    <Table containerClassName={className}>
      <TableBody>
        {events.map((e) => (
          <TableRow key={e.id}>
            <TableCell className="w-40 text-xs whitespace-nowrap tabular">{fmtDate(e.at)}</TableCell>
            <TableCell className="w-56"><StatusBadge tone={e.kind.includes('externally') ? 'warn' : e.kind.includes('missing') || e.kind.includes('deleted') ? 'danger' : 'info'}>{e.kind}</StatusBadge></TableCell>
            <TableCell className="text-sm">{e.details}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

export function JobTable({ rows, compact, className }: { rows: { job: Job; requestedBy: string | null }[]; compact?: boolean; className?: string }) {
  const { t, fmtDate } = useI18n();
  const navigate = useNavigate();
  if (rows.length === 0) return <Empty>{t('job.empty')}</Empty>;
  return (
    <Table containerClassName={cn('min-h-0', className)}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('job.created')}</TableHead>
          <TableHead>{t('job.operation')}</TableHead>
          <TableHead>{t('job.target')}</TableHead>
          <TableHead>{t('job.status')}</TableHead>
          {!compact && <TableHead className="hidden md:table-cell">{t('job.by')}</TableHead>}
          <TableHead>{t('job.result')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map(({ job, requestedBy }) => (
          <TableRow key={job.id} {...rowLink(() => navigate(`/jobs/${job.id}`))} className="cursor-pointer">
            <TableCell className="text-xs whitespace-nowrap tabular">{fmtDate(job.createdAt)}</TableCell>
            <TableCell className="whitespace-nowrap">{jobLabel(t, job)}</TableCell>
            <TableCell className="max-w-56 truncate">{job.targetName}</TableCell>
            <TableCell className="whitespace-nowrap">
              <div className="flex items-center gap-2"><JobBadge status={job.status} />{job.status === 'Running' && <Progress value={job.progress} className="h-1.5 w-16" />}</div>
            </TableCell>
            {!compact && <TableCell className="hidden md:table-cell">{requestedBy}</TableCell>}
            <TableCell className="max-w-md truncate text-xs text-muted-foreground" title={job.resultMessage ?? job.progressMessage ?? ''}>{job.resultMessage ?? job.progressMessage}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
