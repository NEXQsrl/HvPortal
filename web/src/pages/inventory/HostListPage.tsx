import { Server } from 'lucide-react';
import { useNavigate } from 'react-router-dom';
import { KebabMenu, rowLink, WithContextMenu } from '@/components/actions';
import { Empty, ErrorAlert, HostIcon, Loading, Meter, StatusBadge } from '@/components/common';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useOps } from '@/components/ops/OperationsProvider';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { percent } from '@/lib/format';

/** Hosts table of the inventory page (Inventory > Host). */
export function HostTable() {
  const { t, fmtDate } = useI18n();
  const inv = useInventory();
  const ops = useOps();
  const navigate = useNavigate();
  return (
    <div className="flex h-full min-h-0 flex-col">
      <ErrorAlert error={inv.error} className="mx-4" />
      {inv.loading ? <Loading /> : inv.hosts.length === 0 ? <Empty icon={<Server className="size-6" />}>{t('dash.noHosts')}</Empty> : (
        <Table containerClassName="min-h-0 flex-1">
          <TableHeader>
            <TableRow>
              <TableHead>{t('host.name')}</TableHead>
              <TableHead>{t('host.status')}</TableHead>
              <TableHead>{t('host.access')}</TableHead>
              <TableHead className="hidden w-32 lg:table-cell">CPU</TableHead>
              <TableHead className="hidden w-32 lg:table-cell">{t('vm.memory')}</TableHead>
              <TableHead className="hidden md:table-cell">{t('host.platform')}</TableHead>
              <TableHead className="text-right">VM</TableHead>
              <TableHead className="hidden xl:table-cell">{t('host.lastInventory')}</TableHead>
              <TableHead className="w-10"><span className="sr-only">{t('vm.actions')}</span></TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {inv.hosts.map((h) => {
              const items = ops.hostActions(h);
              const hw = h.hardware;
              const mem = hw?.totalPhysicalMemoryBytes != null && hw.freePhysicalMemoryBytes != null ? percent(hw.totalPhysicalMemoryBytes - hw.freePhysicalMemoryBytes, hw.totalPhysicalMemoryBytes) : null;
              return (
                <WithContextMenu key={h.id} items={items} label={h.displayName}>
                  <TableRow {...rowLink(() => navigate(`/hosts/${h.id}`))} className="clickable cursor-pointer" data-host={h.displayName}>
                    <TableCell className="max-w-72">
                      <div className="flex items-center gap-2"><HostIcon connected={h.connected} access={h.access} />
                        <div className="min-w-0"><div className="cell-name truncate font-medium">{h.displayName}</div>
                          <div className="truncate text-xs text-muted-foreground">{h.description ?? h.machineName}</div></div></div>
                    </TableCell>
                    <TableCell className="whitespace-nowrap">
                      <div className="flex items-center gap-1">
                        <StatusBadge tone={h.connected ? 'ok' : 'danger'}>{h.connected ? t('host.online') : t('host.offline')}</StatusBadge>
                        {h.status !== 'Active' && <StatusBadge tone={h.status === 'Revoked' ? 'danger' : 'warn'}>{h.status}</StatusBadge>}
                        {h.stale && h.status === 'Active' && <StatusBadge tone="warn" title={t('stale.banner')}>stale</StatusBadge>}
                      </div>
                    </TableCell>
                    <TableCell>{h.access ? <StatusBadge tone={h.access === 'Ok' ? 'ok' : 'danger'} title={h.accessMessage ?? ''}>{h.access}</StatusBadge> : '—'}</TableCell>
                    <TableCell className="hidden lg:table-cell">{hw?.cpuLoadPercent != null
                      ? <div className="flex items-center gap-2"><Meter value={hw.cpuLoadPercent} className="w-16" /><span className="text-xs tabular">{hw.cpuLoadPercent}%</span></div> : '—'}</TableCell>
                    <TableCell className="hidden lg:table-cell">{mem != null
                      ? <div className="flex items-center gap-2"><Meter value={mem} className="w-16" /><span className="text-xs tabular">{mem}%</span></div> : '—'}</TableCell>
                    <TableCell className="hidden text-xs md:table-cell">{h.platform}<div className="text-muted-foreground">{h.edition}</div></TableCell>
                    <TableCell className="text-right tabular">{h.vmCount}</TableCell>
                    <TableCell className="hidden text-xs whitespace-nowrap tabular xl:table-cell">{fmtDate(h.lastInventoryAt)}</TableCell>
                    <TableCell className="text-right"><KebabMenu items={items} label={t('menu.more')} /></TableCell>
                  </TableRow>
                </WithContextMenu>
              );
            })}
          </TableBody>
        </Table>
      )}
    </div>
  );
}
