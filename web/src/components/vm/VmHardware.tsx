import { RefreshCw } from 'lucide-react';
import { get } from '@/api/client';
import type { Vm, VmDetails } from '@/api/types';
import { Loading, Meter, Notice, Section, StatusBadge } from '@/components/common';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { formatBytes, percent } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';

export type DetailsResponse = { details: VmDetails | null; stale: boolean; collectedAt: string | null; error?: string };

/** Live disks, controllers, network adapters and guest IP addresses (cached copy when the host is offline). */
export function VmHardware({ vm }: { vm: Vm }) {
  const { t, fmtDate } = useI18n();
  const details = useLoad(() => get<DetailsResponse>(`/api/v1/vms/${vm.id}/details`), [vm.id]);
  const d = details.data?.details;
  return (
    <div className="h-full space-y-3 overflow-auto p-4">
      <div className="flex items-center gap-2">
        {details.data?.stale && <StatusBadge tone="warn" title={details.data.error ?? ''}>{t('vm.detailsStale', { at: fmtDate(details.data.collectedAt) })}</StatusBadge>}
        <span className="flex-1" />
        <Button size="sm" variant="outline" onClick={details.reload} disabled={details.loading}><RefreshCw className={cn(details.loading && 'animate-spin')} />{t('common.refresh')}</Button>
      </div>
      {details.loading && !details.data ? <Loading /> : (
        <>
          <Section title={t('vm.disks')}>
            <Table>
              <TableHeader><TableRow>
                <TableHead>{t('vm.diskType')}</TableHead><TableHead>{t('vm.path')}</TableHead><TableHead>{t('vm.controller')}</TableHead>
                <TableHead>{t('vm.format')}</TableHead><TableHead className="text-right">{t('vm.capacity')}</TableHead><TableHead className="w-40">{t('vm.fileSize')}</TableHead>
              </TableRow></TableHeader>
              <TableBody>
                {!d || d.disks.length === 0 ? <TableRow><TableCell colSpan={6} className="text-muted-foreground">{details.data?.error ?? '—'}</TableCell></TableRow>
                  : d.disks.map((k, i) => (
                    <TableRow key={i}>
                      <TableCell className="whitespace-nowrap">{k.kind === 'VirtualHardDisk' ? t('vm.vhd') : k.kind === 'DvdIso' ? 'DVD / ISO' : k.kind}</TableCell>
                      <TableCell className="max-w-96"><div className="font-mono text-xs break-all">{k.path ?? '—'}</div>{k.parentPath && <div className="font-mono text-xs break-all text-muted-foreground">↳ {k.parentPath}</div>}</TableCell>
                      <TableCell className="text-xs whitespace-nowrap">{k.controller ?? '—'}{k.location != null && ` : ${k.location}`}</TableCell>
                      <TableCell className="text-xs">{[k.format, k.diskType].filter(Boolean).join(' · ') || '—'}</TableCell>
                      <TableCell className="text-right tabular">{formatBytes(k.maxSizeBytes)}</TableCell>
                      <TableCell><div className="flex items-center gap-2"><Meter value={percent(k.fileSizeBytes, k.maxSizeBytes)} className="w-16" /><span className="text-xs tabular">{formatBytes(k.fileSizeBytes)}</span></div></TableCell>
                    </TableRow>
                  ))}
              </TableBody>
            </Table>
          </Section>
          <Section title={t('vm.networkAdapters')}>
            <Table>
              <TableHeader><TableRow>
                <TableHead>{t('host.adapter')}</TableHead><TableHead>{t('vm.switch')}</TableHead><TableHead>{t('vm.ip')}</TableHead>
                <TableHead>{t('vm.gateway')}</TableHead><TableHead>DNS</TableHead><TableHead>MAC</TableHead>
              </TableRow></TableHeader>
              <TableBody>
                {!d || d.networkAdapters.length === 0 ? <TableRow><TableCell colSpan={6} className="text-muted-foreground">—</TableCell></TableRow>
                  : d.networkAdapters.map((n, i) => (
                    <TableRow key={i}>
                      <TableCell>{n.name}<div className="text-xs text-muted-foreground">{n.dhcpEnabled == null ? '' : n.dhcpEnabled ? 'DHCP' : t('vm.static')}</div></TableCell>
                      <TableCell>{n.switchName ?? <span className="text-muted-foreground">{t('vm.notConnected')}</span>}</TableCell>
                      <TableCell className="font-mono text-xs">{n.ipAddresses.length === 0 ? <span className="font-sans text-muted-foreground">{t('vm.noIp')}</span> : n.ipAddresses.map((ip) => <div key={ip}>{ip}</div>)}</TableCell>
                      <TableCell className="font-mono text-xs">{n.gateways.join(', ')}</TableCell>
                      <TableCell className="font-mono text-xs">{n.dnsServers.join(', ')}</TableCell>
                      <TableCell className="font-mono text-xs">{n.macAddress}</TableCell>
                    </TableRow>
                  ))}
              </TableBody>
            </Table>
          </Section>
          {d && d.warnings.length > 0 && <Notice kind="warn">{d.warnings.join(' · ')}</Notice>}
        </>
      )}
    </div>
  );
}
