import { History } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import type { Vm } from '@/api/types';
import { KebabMenu, rowLink, WithContextMenu } from '@/components/actions';
import { CheckpointIcon, Empty, Meter, PowerBadge, StatusBadge, VmIcon } from '@/components/common';
import { useOps } from '@/components/ops/OperationsProvider';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { formatBytes, formatDuration } from '@/lib/format';
import { cn } from '@/lib/utils';

export function vmMemory(v: Vm) {
  return v.memoryAssignedMb != null && v.powerState === 'Running' ? formatBytes(v.memoryAssignedMb * 1048576)
    : v.memoryStartupMb != null ? formatBytes(v.memoryStartupMb * 1048576) : '—';
}

export function VmTable({ vms, showHost = true, groupId, className, emptyText }: {
  vms: Vm[]; showHost?: boolean; groupId?: string; className?: string; emptyText?: string;
}) {
  const { t } = useI18n();
  const ops = useOps();
  const navigate = useNavigate();
  if (vms.length === 0) return <Empty>{emptyText ?? t('vm.empty')}</Empty>;
  return (
    <Table containerClassName={cn('min-h-0', className)}>
      <TableHeader>
        <TableRow>
          <TableHead className="w-8"><span className="sr-only">{t('vm.state')}</span></TableHead>
          <TableHead className="w-10" title={t('vm.checkpoints')}><History className="size-3.5" aria-hidden="true" /><span className="sr-only">{t('vm.checkpoints')}</span></TableHead>
          <TableHead>{t('vm.name')}</TableHead>
          <TableHead>{t('vm.state')}</TableHead>
          {showHost && <TableHead className="hidden md:table-cell">{t('vm.host')}</TableHead>}
          <TableHead className="hidden text-right sm:table-cell">{t('vm.cpu')}</TableHead>
          <TableHead className="hidden w-28 lg:table-cell">CPU %</TableHead>
          <TableHead className="hidden text-right sm:table-cell">{t('vm.memory')}</TableHead>
          <TableHead className="hidden xl:table-cell">{t('vm.uptime')}</TableHead>
          <TableHead className="hidden xl:table-cell">Gen</TableHead>
          <TableHead className="w-10"><span className="sr-only">{t('vm.actions')}</span></TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {vms.map((v) => {
          const items = ops.vmActions(v, { groupId });
          return (
            <WithContextMenu key={v.id} items={items} label={v.name}>
              <TableRow {...rowLink(() => navigate(`/vms/${v.id}`))} className="clickable cursor-pointer" data-vm={v.name}>
                <TableCell><VmIcon state={v.powerState} critical={v.isCritical} /></TableCell>
                <TableCell><CheckpointIcon count={v.checkpointCount} /></TableCell>
                <TableCell className="max-w-72">
                  <div className={cn('cell-name truncate font-medium', v.isMissing && 'text-muted-foreground line-through')}>{v.name}</div>
                  {v.notes && <div className="truncate text-xs text-muted-foreground" title={v.notes}>{v.notes.split(/\r?\n/)[0]}</div>}
                </TableCell>
                <TableCell className="whitespace-nowrap">
                  <div className="flex items-center gap-1">
                    <PowerBadge state={v.powerState} critical={v.isCritical} />
                    {v.isMissing && <StatusBadge tone="danger">{t('vm.missing')}</StatusBadge>}
                    {v.stale && !v.isMissing && <StatusBadge tone="warn" title={t('stale.banner')}>stale</StatusBadge>}
                  </div>
                </TableCell>
                {showHost && <TableCell className="hidden truncate md:table-cell">{v.hostName}</TableCell>}
                <TableCell className="hidden text-right tabular sm:table-cell">{v.processorCount ?? '—'}</TableCell>
                <TableCell className="hidden lg:table-cell">
                  {v.powerState === 'Running' && v.processorLoadPercent != null
                    ? <div className="flex items-center gap-2"><Meter value={v.processorLoadPercent} className="w-14" /><span className="text-xs tabular">{v.processorLoadPercent}%</span></div>
                    : <span className="text-muted-foreground">—</span>}
                </TableCell>
                <TableCell className="hidden text-right whitespace-nowrap tabular sm:table-cell">{vmMemory(v)}</TableCell>
                <TableCell className="hidden whitespace-nowrap tabular xl:table-cell">{v.powerState === 'Running' ? formatDuration(v.uptimeMs) : '—'}</TableCell>
                <TableCell className="hidden xl:table-cell">{v.generation ?? '—'}</TableCell>
                <TableCell className="text-right"><KebabMenu items={items} label={t('menu.more')} /></TableCell>
              </TableRow>
            </WithContextMenu>
          );
        })}
      </TableBody>
    </Table>
  );
}
