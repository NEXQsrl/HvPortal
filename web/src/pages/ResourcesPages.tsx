import { HardDrive, Network, Plus } from 'lucide-react';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Empty, HostIcon, Meter, Notice, PageHeader, Section } from '@/components/common';
import { HelpTip } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { NewSwitchDialog } from '@/components/ops/NetworkDialogs';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { NativeSelect } from '@/components/ui/native-select';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { HostFileExplorer } from '@/components/vm/HostFileExplorer';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, percent } from '@/lib/format';
import { HostStorage, HostSwitches } from './inventory/HostPage';

/** Virtual switches per host. The navigator selects a host (?host=) or a switch (?switch=); new networks are created here. */
export function NetworkPage() {
  const { t } = useI18n();
  const inv = useInventory();
  const ops = useOps();
  const [params] = useSearchParams();
  const selectedHost = params.get('host');
  const [creating, setCreating] = useState(false);
  const hosts = inv.hosts.filter((h) => h.connected && h.access === 'Ok' && (!selectedHost || h.id === selectedHost));
  const manageable = inv.hosts.filter((h) => h.canManageNetworks);
  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<Network className="size-5" />} title={t('net.title')} subtitle={t('net.help')}
        actions={manageable.length > 0 && <Button size="sm" onClick={() => setCreating(true)}><Plus />{t('net.new')}</Button>} />
      <div className="min-h-0 flex-1 space-y-3 overflow-auto p-4">
        {hosts.length === 0 && <Empty>{t('net.noHosts')}</Empty>}
        {hosts.map((h) => (
          <div key={h.id} className="space-y-1.5">
            <Link to={`/hosts/${h.id}?tab=network`} className="flex items-center gap-2 text-sm font-semibold hover:underline"><HostIcon connected={h.connected} access={h.access} />{h.displayName}</Link>
            <HostSwitches hostId={h.id} highlight={h.id === selectedHost ? params.get('switch') ?? undefined : undefined} />
          </div>
        ))}
      </div>
      {creating && <NewSwitchDialog hosts={manageable} initialHostId={selectedHost ?? undefined} onQueued={ops.queued} onClose={() => setCreating(false)} />}
    </div>
  );
}

const STORAGE_TABS = ['overview', 'files'] as const;

export function StoragePage() {
  const { t } = useI18n();
  const inv = useInventory();
  const [params, setParams] = useSearchParams();
  const tab = (STORAGE_TABS as readonly string[]).includes(params.get('tab') ?? '') ? params.get('tab')! : 'overview';
  const connected = inv.hosts.filter((h) => h.connected && h.access === 'Ok');
  // The host comes from the navigator (?host=) or from the selectors of the page.
  const urlHost = params.get('host');
  const setHost = (id: string) => setParams((p) => { const n = new URLSearchParams(p); n.set('host', id); n.delete('view'); return n; }, { replace: true });
  const host = (urlHost ? connected.find((h) => h.id === urlHost) : undefined) ?? connected[0];
  const hostId = host?.id ?? '';
  const fileHosts = inv.hosts.filter((h) => h.canBrowseFiles);
  const fileHost = (urlHost ? fileHosts.find((h) => h.id === urlHost) : undefined) ?? fileHosts[0];
  const disksOnly = params.get('view') === 'disks';
  return (
    <Tabs value={tab} onValueChange={(v) => setParams((p) => { const n = new URLSearchParams(p); if (v === 'overview') n.delete('tab'); else n.set('tab', v); n.delete('view'); return n; }, { replace: true })}
      className="flex h-full min-h-0 flex-col gap-0">
      <PageHeader icon={<HardDrive className="size-5" />} title={t('sto.title')} subtitle={t('sto.help')}>
        <TabsList variant="line" className="page-tabs -mb-px h-9 w-full justify-start rounded-none bg-transparent p-0">
          {STORAGE_TABS.map((x) => <TabsTrigger key={x} value={x} className="flex-none px-3">{t(('sto.tab.' + x) as TKey)}</TabsTrigger>)}
        </TabsList>
      </PageHeader>
      <TabsContent value="overview" className="min-h-0 space-y-3 overflow-auto p-4">
        {!disksOnly && <Section title={t('sto.volumes')}>
          <Table>
            <TableHeader><TableRow><TableHead>{t('vm.host')}</TableHead><TableHead>{t('host.volume')}</TableHead><TableHead>{t('host.fileSystem')}</TableHead><TableHead className="text-right">{t('host.size')}</TableHead><TableHead className="text-right">{t('host.freeSpace')}</TableHead><TableHead className="w-36">{t('host.usage')}</TableHead></TableRow></TableHeader>
            <TableBody>
              {inv.hosts.filter((h) => !urlHost || h.id === urlHost).flatMap((h) => h.volumes.map((v) => (
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
        </Section>}
        <div className="flex items-center gap-2">
          <h2 className="text-sm font-semibold">{t('sto.disks')}</h2>
          {connected.length > 0 && (
            <NativeSelect aria-label={t('vm.host')} value={hostId} onChange={(e) => setHost(e.target.value)} className="w-56">
              {connected.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
            </NativeSelect>
          )}
        </div>
        {host ? <HostStorage host={host} /> : <Empty>{t('net.noHosts')}</Empty>}
      </TabsContent>
      <TabsContent value="files" className="flex min-h-0 flex-col gap-3 overflow-auto p-4">
        {tab === 'files' && (
          <>
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="text-sm font-semibold">{t('files.host')}</h2>
              {fileHosts.length > 0 && (
                <NativeSelect aria-label={t('vm.host')} value={fileHost?.id ?? ''} onChange={(e) => setHost(e.target.value)} className="w-56">
                  {fileHosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
                </NativeSelect>
              )}
              <HelpTip>{t('files.help')}</HelpTip>
            </div>
            {fileHost ? <HostFileExplorer key={fileHost.id} host={fileHost} /> : <Notice kind="warn">{t('files.noHosts')}</Notice>}
          </>
        )}
      </TabsContent>
    </Tabs>
  );
}
