import { HardDrive, Network } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { Empty, HostIcon, Meter, PageHeader, Section } from '@/components/common';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { NativeSelect } from '@/components/ui/native-select';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { formatBytes, percent } from '@/lib/format';
import { HostStorage, HostSwitches } from './inventory/HostPage';

export function NetworkPage() {
  const { t } = useI18n();
  const inv = useInventory();
  const hosts = inv.hosts.filter((h) => h.connected && h.access === 'Ok');
  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<Network className="size-5" />} title={t('net.title')} subtitle={t('net.help')} />
      <div className="min-h-0 flex-1 space-y-3 overflow-auto p-4">
        {hosts.length === 0 && <Empty>{t('net.noHosts')}</Empty>}
        {hosts.map((h) => (
          <div key={h.id} className="space-y-1.5">
            <Link to={`/hosts/${h.id}?tab=network`} className="flex items-center gap-2 text-sm font-semibold hover:underline"><HostIcon connected={h.connected} access={h.access} />{h.displayName}</Link>
            <HostSwitches hostId={h.id} />
          </div>
        ))}
      </div>
    </div>
  );
}

export function StoragePage() {
  const { t } = useI18n();
  const inv = useInventory();
  const connected = inv.hosts.filter((h) => h.connected && h.access === 'Ok');
  const [hostId, setHostId] = useState(connected[0]?.id ?? '');
  const host = inv.hostById.get(hostId);
  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<HardDrive className="size-5" />} title={t('sto.title')} subtitle={t('sto.help')} />
      <div className="min-h-0 flex-1 space-y-3 overflow-auto p-4">
        <Section title={t('sto.volumes')}>
          <Table>
            <TableHeader><TableRow><TableHead>{t('vm.host')}</TableHead><TableHead>{t('host.volume')}</TableHead><TableHead>{t('host.fileSystem')}</TableHead><TableHead className="text-right">{t('host.size')}</TableHead><TableHead className="text-right">{t('host.freeSpace')}</TableHead><TableHead className="w-36">{t('host.usage')}</TableHead></TableRow></TableHeader>
            <TableBody>
              {inv.hosts.flatMap((h) => h.volumes.map((v) => (
                <TableRow key={h.id + v.name}>
                  <TableCell><Link className="hover:underline" to={`/hosts/${h.id}`}>{h.displayName}</Link></TableCell>
                  <TableCell><span className="font-medium">{v.name}</span> <span className="text-xs text-muted-foreground">{v.label}</span></TableCell>
                  <TableCell className="text-xs">{v.fileSystem}</TableCell>
                  <TableCell className="text-right whitespace-nowrap tabular">{formatBytes(v.sizeBytes)}</TableCell>
                  <TableCell className="text-right whitespace-nowrap tabular">{formatBytes(v.freeBytes)}</TableCell>
                  <TableCell><div className="flex items-center gap-2"><Meter value={percent(v.sizeBytes != null && v.freeBytes != null ? v.sizeBytes - v.freeBytes : null, v.sizeBytes)} className="w-20" />
                    <span className="text-xs tabular">{percent(v.sizeBytes != null && v.freeBytes != null ? v.sizeBytes - v.freeBytes : null, v.sizeBytes) ?? '—'}%</span></div></TableCell>
                </TableRow>
              )))}
            </TableBody>
          </Table>
        </Section>
        <div className="flex items-center gap-2">
          <h2 className="text-sm font-semibold">{t('sto.disks')}</h2>
          {connected.length > 0 && (
            <NativeSelect aria-label={t('vm.host')} value={hostId} onChange={(e) => setHostId(e.target.value)} className="w-56">
              {connected.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
            </NativeSelect>
          )}
        </div>
        {host ? <HostStorage host={host} /> : <Empty>{t('net.noHosts')}</Empty>}
      </div>
    </div>
  );
}
