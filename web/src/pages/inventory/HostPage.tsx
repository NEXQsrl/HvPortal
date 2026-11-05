import { ChevronDown, Clock, Cpu, FolderOpen, MemoryStick, Monitor, Plus, RefreshCw, Server, Trash2 } from 'lucide-react';
import { useCallback, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { get, put } from '@/api/client';
import type { HostDiskRow, IsosResponse, SwitchInfo } from '@/api/ops-types';
import type { Host, InventoryEvent } from '@/api/types';
import { ActionsButton } from '@/components/actions';
import { ErrorAlert, HostIcon, KeyValue, Loading, Meter, Notice, PageHeader, Section, StatCard, StatusBadge } from '@/components/common';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useOps } from '@/components/ops/OperationsProvider';
import { EventsTable } from '@/components/tables';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { VmTable } from '@/components/vm/VmTable';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, formatDuration, LIBRARY_PATH, percent } from '@/lib/format';
import { useLive, useLoad } from '@/lib/hooks';
import { toast } from '@/lib/toast';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';

const TABS = ['summary', 'vms', 'network', 'storage', 'settings', 'events'] as const;
type Tab = (typeof TABS)[number];

export function HostPage() {
  const { id } = useParams();
  const { t } = useI18n();
  const inv = useInventory();
  const ops = useOps();
  const [params, setParams] = useSearchParams();
  const data = useLoad(() => get<{ host: Host; events: InventoryEvent[] }>(`/api/v1/hosts/${id}`), [id]);
  const reload = useCallback(() => data.reload(), [data.reload]);
  useLive(reload, ['inventoryChanged'], 500);

  const h = data.data?.host ?? (id ? inv.hostById.get(id) : undefined);
  if (!h) return data.loading ? <Loading /> : <div className="p-4"><ErrorAlert error={data.error} /></div>;
  const tab: Tab = (TABS as readonly string[]).includes(params.get('tab') ?? '') ? (params.get('tab') as Tab) : 'summary';
  const vms = inv.vms.filter((v) => v.hostId === h.id);

  return (
    <Tabs value={tab} onValueChange={(v) => setParams(v === 'summary' ? {} : { tab: v }, { replace: true })} className="flex h-full min-h-0 flex-col gap-0">
      <PageHeader icon={<HostIcon connected={h.connected} access={h.access} className="scale-125" />} title={h.displayName}
        badges={<>
          <StatusBadge tone={h.connected ? 'ok' : 'danger'}>{h.connected ? t('host.online') : t('host.offline')}</StatusBadge>
          {h.access && <StatusBadge tone={h.access === 'Ok' ? 'ok' : 'danger'} title={h.accessMessage ?? ''}>Hyper-V: {h.access}</StatusBadge>}
        </>}
        subtitle={[h.dnsHostName ?? h.machineName, h.platform, h.edition].filter(Boolean).join(' · ')}
        actions={<>
          {h.canCreateVm && <Button size="sm" onClick={() => ops.openCreateVm({ hostId: h.id })}><Plus />{t('host.newVm')}</Button>}
          <ActionsButton items={ops.hostActions(h, { page: true })} label={h.displayName}>{t('common.actions')}<ChevronDown /></ActionsButton>
        </>}>
        <TabsList variant="line" className="-mb-px h-9 w-full justify-start overflow-x-auto rounded-none bg-transparent p-0">
          {TABS.map((x) => <TabsTrigger key={x} value={x} className="flex-none px-3">{t(('host.tab.' + x) as TKey)}{x === 'vms' && <span className="ml-1 text-xs text-muted-foreground">{vms.length}</span>}</TabsTrigger>)}
        </TabsList>
      </PageHeader>
      {(h.stale && h.status === 'Active') || (h.access && h.access !== 'Ok') || h.partial ? (
        <div className="space-y-2 px-4 pt-3">
          {h.stale && h.status === 'Active' && <Notice kind="warn">{t('stale.banner')}</Notice>}
          {h.access && h.access !== 'Ok' && <Notice kind="danger">{h.access}: {h.accessMessage}</Notice>}
          {h.partial && <Notice kind="warn">{t('partial.banner')} {h.warnings.join(' · ')}</Notice>}
        </div>
      ) : null}

      <TabsContent value="summary" className="min-h-0 overflow-auto p-4"><HostSummary host={h} running={vms.filter((v) => v.powerState === 'Running').length} /></TabsContent>
      <TabsContent value="vms" className="flex min-h-0 flex-col">{tab === 'vms' && <VmTable vms={vms} showHost={false} className="flex-1" />}</TabsContent>
      <TabsContent value="network" className="min-h-0 overflow-auto p-4">{tab === 'network' && <HostSwitches hostId={h.id} />}</TabsContent>
      <TabsContent value="storage" className="min-h-0 overflow-auto p-4">{tab === 'storage' && <HostStorage host={h} />}</TabsContent>
      <TabsContent value="settings" className="min-h-0 overflow-auto p-4">{tab === 'settings' && <HostSettings host={h} onChanged={data.reload} />}</TabsContent>
      <TabsContent value="events" className="min-h-0 overflow-auto p-4"><Section title={t('host.events')}><EventsTable events={data.data?.events ?? []} /></Section></TabsContent>
    </Tabs>
  );
}

function HostSummary({ host: h, running }: { host: Host; running: number }) {
  const { t, fmtDate } = useI18n();
  const hw = h.hardware;
  const usedMem = hw?.totalPhysicalMemoryBytes != null && hw.freePhysicalMemoryBytes != null ? hw.totalPhysicalMemoryBytes - hw.freePhysicalMemoryBytes : null;
  return (
    <div className="space-y-3">
      <div className="resource-grid grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard icon={<Cpu className="size-3.5" />} label="CPU" value={hw?.cpuLoadPercent != null ? `${hw.cpuLoadPercent}%` : '—'} meter={hw?.cpuLoadPercent ?? null}
          sub={<>
            {hw?.cpuLoadSource && <div title={t(('host.cpuSource.' + hw.cpuLoadSource + '.help') as TKey)}>{t(('host.cpuSource.' + hw.cpuLoadSource) as TKey)}</div>}
            <div className="truncate">{hw?.cpuName ?? '—'}</div>
            <div>{t('host.cpuTopology', { s: hw?.cpuSockets ?? '?', c: hw?.cpuCores ?? '?', lp: hw?.logicalProcessorCount ?? '?' })}</div>
          </>} />
        <StatCard icon={<MemoryStick className="size-3.5" />} label={t('vm.memory')} value={`${formatBytes(usedMem)} / ${formatBytes(hw?.totalPhysicalMemoryBytes)}`}
          meter={percent(usedMem, hw?.totalPhysicalMemoryBytes)} sub={t('host.free', { v: formatBytes(hw?.freePhysicalMemoryBytes) })} />
        <StatCard icon={<Clock className="size-3.5" />} label={t('host.uptime')} value={h.lastBootUtc ? formatDuration(Date.now() - new Date(h.lastBootUtc).getTime()) : '—'}
          sub={<>{t('host.lastBoot')}: {fmtDate(h.lastBootUtc)}</>} />
        <StatCard icon={<Monitor className="size-3.5" />} label={t('host.vms')} value={`${running} / ${h.vmCount}`}
          sub={<>{t('dash.running')} · {t('host.switches')}: {h.virtualSwitches.join(', ') || '—'}</>} />
      </div>
      <div className="grid gap-3 xl:grid-cols-2">
        <Section title={t('host.info')} bodyClassName="p-3">
          <KeyValue items={[
            [t('host.os'), `${h.os?.osCaption ?? '—'} ${h.os?.osVersion ?? ''} (${h.os?.osBuildNumber ?? '?'})`],
            [t('host.domain'), h.os ? `${h.os.domain ?? '—'}${h.os.partOfDomain ? '' : ' (workgroup)'}` : '—'],
            [t('host.edition'), h.edition], [t('host.platform'), h.platform], [t('host.support'), h.support],
            [t('host.connection'), `${h.agentVersion ?? '—'} · ${t('host.lastInventory')}: ${fmtDate(h.lastInventoryAt)}`],
          ]} />
        </Section>
        <Section title={t('host.capabilities')}>
          <Table containerClassName="max-h-64">
            <TableBody>
              {h.capabilities.map((c) => (
                <TableRow key={c.name}>
                  <TableCell className="text-sm whitespace-nowrap">{c.name}</TableCell>
                  <TableCell><StatusBadge tone={c.status === 'available' || c.status === 'supported' ? 'ok' : c.status === 'planned' ? 'muted' : 'warn'}>{c.status}</StatusBadge></TableCell>
                  <TableCell className="min-w-40 text-xs [overflow-wrap:anywhere] text-muted-foreground">{c.note}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Section>
      </div>
      <div className="grid gap-3 xl:grid-cols-2">
        <Section title={t('host.volumes')}>
          <Table>
            <TableHeader><TableRow><TableHead>{t('host.volume')}</TableHead><TableHead>{t('host.fileSystem')}</TableHead><TableHead className="text-right">{t('host.size')}</TableHead><TableHead className="text-right">{t('host.freeSpace')}</TableHead><TableHead className="w-28">{t('host.usage')}</TableHead></TableRow></TableHeader>
            <TableBody>
              {h.volumes.length === 0 ? <TableRow><TableCell colSpan={5} className="text-muted-foreground">—</TableCell></TableRow> : h.volumes.map((v) => (
                <TableRow key={v.name}>
                  <TableCell><span className="font-medium">{v.name}</span> <span className="text-xs text-muted-foreground">{v.label}</span></TableCell>
                  <TableCell className="text-xs">{v.fileSystem}</TableCell>
                  <TableCell className="text-right whitespace-nowrap tabular">{formatBytes(v.sizeBytes)}</TableCell>
                  <TableCell className="text-right whitespace-nowrap tabular">{formatBytes(v.freeBytes)}</TableCell>
                  <TableCell><Meter value={percent(v.sizeBytes != null && v.freeBytes != null ? v.sizeBytes - v.freeBytes : null, v.sizeBytes)} /></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Section>
        <Section title={t('host.network')}>
          <Table>
            <TableHeader><TableRow><TableHead>{t('host.adapter')}</TableHead><TableHead>{t('vm.ip')}</TableHead><TableHead>{t('vm.gateway')}</TableHead><TableHead>MAC</TableHead></TableRow></TableHeader>
            <TableBody>
              {h.networkAdapters.length === 0 ? <TableRow><TableCell colSpan={4} className="text-muted-foreground">—</TableCell></TableRow> : h.networkAdapters.map((n, i) => (
                <TableRow key={i}>
                  <TableCell>{n.name}<div className="text-xs text-muted-foreground">{n.dhcpEnabled ? 'DHCP' : t('vm.static')}{n.switchName ? ` · ${n.switchName}` : ''}</div></TableCell>
                  <TableCell className="font-mono text-xs">{n.ipAddresses.map((ip) => <div key={ip}>{ip}</div>)}</TableCell>
                  <TableCell className="font-mono text-xs">{n.gateways.join(', ')}</TableCell>
                  <TableCell className="font-mono text-xs">{n.macAddress}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Section>
      </div>
    </div>
  );
}

export function HostSwitches({ hostId }: { hostId: string }) {
  const { t } = useI18n();
  const data = useLoad(() => get<SwitchInfo[]>(`/api/v1/hosts/${hostId}/switches`), [hostId]);
  return (
    <Section title={t('host.switches')} actions={<Button size="xs" variant="ghost" onClick={data.reload} disabled={data.loading}><RefreshCw className={cn(data.loading && 'animate-spin')} /></Button>}>
      <ErrorAlert error={data.error} className="mx-3" />
      {data.loading && !data.data ? <Loading /> : (
        <Table>
          <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('host.switchType')}</TableHead><TableHead>{t('host.adapter')}</TableHead><TableHead>{t('host.mgmtOs')}</TableHead><TableHead>{t('host.set')}</TableHead><TableHead>{t('host.connectedVms')}</TableHead></TableRow></TableHeader>
          <TableBody>
            {(data.data ?? []).map((s) => (
              <TableRow key={s.id}>
                <TableCell className="font-medium">{s.name}</TableCell>
                <TableCell><StatusBadge tone={s.type === 'External' ? 'info' : 'muted'}>{s.type}</StatusBadge></TableCell>
                <TableCell className="text-xs">{s.netAdapter ?? '—'}</TableCell>
                <TableCell>{s.allowManagementOs ? t('common.yes') : t('common.no')}</TableCell>
                <TableCell>{s.embeddedTeaming ? t('common.yes') : t('common.no')}</TableCell>
                <TableCell className="text-xs">{s.connectedVms.length === 0 ? '—' : s.connectedVms.join(', ')}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </Section>
  );
}

export function HostStorage({ host }: { host: Host }) {
  const { t } = useI18n();
  const data = useLoad(() => get<HostDiskRow[]>(`/api/v1/hosts/${host.id}/disks`), [host.id]);
  return (
    <Section title={t('host.virtualDisks')} actions={<Button size="xs" variant="ghost" onClick={data.reload} disabled={data.loading}><RefreshCw className={cn(data.loading && 'animate-spin')} /></Button>}>
      <ErrorAlert error={data.error} className="mx-3" />
      {data.loading && !data.data ? <Loading /> : (
        <Table>
          <TableHeader><TableRow><TableHead>{t('common.path')}</TableHead><TableHead>VM</TableHead><TableHead>{t('vm.controller')}</TableHead><TableHead>{t('vm.format')}</TableHead><TableHead className="text-right">{t('vm.capacity')}</TableHead><TableHead className="w-40">{t('vm.fileSize')}</TableHead></TableRow></TableHeader>
          <TableBody>
            {(data.data ?? []).map(({ disk: d, portalVmId }, i) => (
              <TableRow key={i}>
                <TableCell className="max-w-96"><div className="font-mono text-xs break-all">{d.path}</div>{d.parentPath && <div className="font-mono text-xs break-all text-muted-foreground">↳ {d.parentPath}</div>}</TableCell>
                <TableCell><Link className="text-primary hover:underline" to={`/vms/${portalVmId}`}>{d.vmName}</Link></TableCell>
                <TableCell className="text-xs whitespace-nowrap">{d.controller}</TableCell>
                <TableCell className="text-xs">{[d.format, d.diskType].filter(Boolean).join(' · ')}</TableCell>
                <TableCell className="text-right whitespace-nowrap tabular">{formatBytes(d.sizeBytes)}</TableCell>
                <TableCell><div className="flex items-center gap-2"><Meter value={percent(d.fileSizeBytes, d.sizeBytes)} className="w-16" /><span className="text-xs tabular">{formatBytes(d.fileSizeBytes)}</span></div></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </Section>
  );
}

function HostSettings({ host, onChanged }: { host: Host; onChanged: () => void }) {
  const { t } = useI18n();
  const { can } = useSession();
  const ops = useOps();
  const manage = can('hosts.manage');
  const [paths, setPaths] = useState<string[]>(host.isoLibrary);
  const [adding, setAdding] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const isos = useLoad(() => (host.isoLibrary.length > 0 ? get<IsosResponse>(`/api/v1/hosts/${host.id}/isos`) : Promise.resolve(null)), [host.id, host.isoLibrary.join('|')]);
  const dirty = paths.join('|') !== host.isoLibrary.join('|');
  const validNew = LIBRARY_PATH.test(adding.trim()) && !adding.includes('..');

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await put(`/api/v1/hosts/${host.id}/iso-library`, { paths });
      toast.success(t('settings.saved'));
      onChanged();
    } catch (e) { setError(e); } finally { setBusy(false); }
  };

  return (
    <div className="max-w-3xl space-y-3">
      <Section title={<span className="flex items-center gap-2"><FolderOpen className="size-4 text-muted-foreground" />{t('host.isoLibrary')}</span>} bodyClassName="space-y-3 p-3">
        <p className="text-xs text-muted-foreground">{t('host.isoLibraryHelp')}</p>
        <div className="space-y-1">
          {paths.length === 0 && <div className="text-sm text-muted-foreground">{t('common.none')}</div>}
          {paths.map((p) => (
            <div key={p} className="flex items-center gap-2 rounded-md border px-2 py-1">
              <FolderOpen className="size-4 text-muted-foreground" /><span className="flex-1 font-mono text-xs">{p}</span>
              {manage && <Button size="icon-xs" variant="ghost" aria-label={t('common.remove')} onClick={() => setPaths(paths.filter((x) => x !== p))}><Trash2 /></Button>}
            </div>
          ))}
        </div>
        {manage && (
          <form className="flex flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); if (validNew && !paths.includes(adding.trim())) { setPaths([...paths, adding.trim()]); setAdding(''); } }}>
            <Input value={adding} onChange={(e) => setAdding(e.target.value)} placeholder="D:\ISO" className="w-72 font-mono" aria-label={t('host.addFolder')} aria-invalid={!!adding && !validNew} />
            <Button type="submit" size="sm" variant="outline" disabled={!validNew || paths.length >= 16}><Plus />{t('host.addFolder')}</Button>
            <span className="flex-1" />
            <Button type="button" size="sm" disabled={!dirty || busy} onClick={() => void save()}>{t('common.save')}</Button>
          </form>
        )}
        {adding && !validNew && <div className="text-xs text-destructive">{t('host.folderInvalid')}</div>}
        <ErrorAlert error={error} />
        {isos.data && <div className="text-xs text-muted-foreground">{t('host.isoFiles', { n: isos.data.files.length })}</div>}
      </Section>
      {manage && (
        <Section title={t('host.dangerZone')} bodyClassName="flex flex-wrap gap-2 p-3">
          {ops.hostActions(host, { page: true }).filter((a) => a.danger || a.key === 'token' || a.key === 'edit').map((a) => (
            <Button key={a.key} size="sm" variant={a.danger ? 'destructive' : 'outline'} disabled={a.disabled} onClick={a.onSelect}>{a.icon && <a.icon />}{a.label}</Button>
          ))}
        </Section>
      )}
      {!manage && <Notice kind="info">{t('cfg.readOnly')}</Notice>}
      <div className="text-xs text-muted-foreground"><Server className="mr-1 inline size-3" />{host.id}</div>
    </div>
  );
}
