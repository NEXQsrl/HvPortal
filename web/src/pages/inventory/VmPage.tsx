import { ChevronDown, Clock, Cpu, Info, MemoryStick, Monitor, Play, Power, RotateCcw, Server } from 'lucide-react';
import { useCallback } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { get } from '@/api/client';
import type { InventoryEvent, Job, Vm } from '@/api/types';
import { ActionsButton } from '@/components/actions';
import { AgentUpdateNotice } from '@/components/AgentUpdateNotice';
import { CheckpointIcon, CopyButton, ErrorAlert, GroupIcon, KeyValue, Loading, Notice, PageHeader, PowerBadge, Section, StatCard, StatusBadge, VmIcon } from '@/components/common';
import { VmConsole } from '@/components/console/VmConsole';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useOps } from '@/components/ops/OperationsProvider';
import { EventsTable, JobTable } from '@/components/tables';
import { Button } from '@/components/ui/button';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { VmCheckpoints } from '@/components/vm/VmCheckpoints';
import { VmHardware } from '@/components/vm/VmHardware';
import { VmSettings } from '@/components/vm/VmSettings';
import { vmMemory } from '@/components/vm/VmTable';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, formatDuration } from '@/lib/format';
import { useLive, useLoad } from '@/lib/hooks';

const TABS = ['summary', 'console', 'settings', 'hardware', 'checkpoints', 'events'] as const;

/** Msvm_SummaryInformation.ReplicationHealth: 0 Not applicable, 1 Ok, 2 Warning, 3 Critical. */
function ReplicationHealth({ value }: { value: number | null | undefined }) {
  const { t } = useI18n();
  if (value == null) return <>—</>;
  const tone = value === 1 ? 'ok' : value === 2 ? 'warn' : value === 3 ? 'danger' : 'muted';
  return <StatusBadge tone={tone}>{t(('repl.health.' + Math.min(value, 4)) as TKey)}</StatusBadge>;
}
type Tab = (typeof TABS)[number];

export function VmPage() {
  const { id } = useParams();
  const { t, fmtDate } = useI18n();
  const inv = useInventory();
  const ops = useOps();
  const [params, setParams] = useSearchParams();
  const data = useLoad(() => get<{ vm: Vm; recentJobs: Job[] }>(`/api/v1/vms/${id}`), [id]);
  const events = useLoad(() => get<InventoryEvent[]>(`/api/v1/vms/${id}/events`), [id]);
  const reload = useCallback(() => { data.reload(); events.reload(); }, [data.reload, events.reload]);
  useLive(reload, ['inventoryChanged', 'groupsChanged'], 500);
  useLive(data.reload, ['jobChanged'], 800);

  const vm = data.data?.vm ?? (id ? inv.vmById.get(id) : undefined);
  if (!vm) return data.loading ? <Loading /> : <div className="p-4"><ErrorAlert error={data.error} /></div>;

  const tab: Tab = (TABS as readonly string[]).includes(params.get('tab') ?? '') ? (params.get('tab') as Tab) : 'summary';
  const setTab = (v: string) => setParams(v === 'summary' ? {} : { tab: v }, { replace: true });
  const host = inv.hostById.get(vm.hostId);
  const groups = inv.groupsOfVm(vm.id);
  const quick = (op: 'Start' | 'Shutdown' | 'Restart', icon: React.ReactNode) =>
    vm.allowedOperations.includes(op) && (
      <Tooltip key={op}>
        <TooltipTrigger asChild><Button size="sm" variant="outline" onClick={() => ops.openPower(vm, op)} aria-label={t(('op.' + op) as TKey)}>{icon}<span className="hidden 2xl:inline">{t(('op.' + op) as TKey)}</span></Button></TooltipTrigger>
        <TooltipContent>{t(('op.' + op) as TKey)}</TooltipContent>
      </Tooltip>
    );

  return (
    <Tabs value={tab} onValueChange={setTab} className="flex h-full min-h-0 flex-col gap-0">
      <PageHeader icon={<VmIcon state={vm.powerState} critical={vm.isCritical} className="scale-125" />} title={vm.name}
        badges={<>
          <PowerBadge state={vm.powerState} critical={vm.isCritical} />
          {vm.stale && <StatusBadge tone="warn" title={t('stale.banner')}>stale</StatusBadge>}
          {vm.isMissing && <StatusBadge tone="danger">{t('vm.missing')}</StatusBadge>}
        </>}
        subtitle={<span className="flex flex-wrap items-center gap-x-3">
          <Link className="inline-flex items-center gap-1 hover:underline" to={`/hosts/${vm.hostId}`}><Server className="size-3" />{vm.hostName}</Link>
          {groups.map((g) => <Link key={g.id} className="inline-flex items-center gap-1 hover:underline" to={`/groups/${g.id}`}><GroupIcon className="size-3" />{inv.groupPath(g.id).map((x) => x.name).join(' / ')}</Link>)}
          {vm.guestOperatingSystem && <span>{vm.guestOperatingSystem}</span>}
        </span>}
        actions={<>
          {quick('Start', <Play />)}
          {quick('Shutdown', <Power />)}
          {quick('Restart', <RotateCcw />)}
          {vm.canConsole && <Button size="sm" variant="outline" onClick={() => setTab('console')}><Monitor />{t('vm.tab.console')}</Button>}
          <ActionsButton items={ops.vmActions(vm, { page: true })} label={vm.name}>{t('common.actions')}<ChevronDown /></ActionsButton>
        </>}>
        <TabsList variant="line" className="-mb-px h-9 w-full justify-start overflow-x-auto rounded-none bg-transparent p-0">
          {TABS.filter((x) => (x !== 'console' || vm.canConsole)).map((x) => (
            <TabsTrigger key={x} value={x} className="flex-none px-3">{t(('vm.tab.' + x) as TKey)}</TabsTrigger>
          ))}
        </TabsList>
      </PageHeader>
      <AgentUpdateNotice host={host} className="mx-4 mt-3" />
      {vm.stale && <div className="px-4 pt-3"><Notice kind="warn">{t('stale.banner')}</Notice></div>}
      {vm.isMissing && <div className="px-4 pt-3"><Notice kind="danger">{t('vm.missing')} ({fmtDate(vm.missingSince)})</Notice></div>}

      <TabsContent value="summary" className="min-h-0 overflow-auto p-4">
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <StatCard icon={<Cpu className="size-3.5" />} label="CPU" value={`${vm.processorCount ?? '—'} vCPU`} meter={vm.powerState === 'Running' ? vm.processorLoadPercent : null}
            sub={vm.processorLoadPercent != null && vm.powerState === 'Running' ? t('vm.load', { p: vm.processorLoadPercent }) : '—'} />
          <StatCard icon={<MemoryStick className="size-3.5" />} label={vm.powerState === 'Running' ? t('vm.memoryAssigned') : t('vm.memory')} value={vmMemory(vm)}
            meter={vm.memoryDemandMb != null && vm.memoryAssignedMb ? Math.min(100, Math.round((vm.memoryDemandMb / vm.memoryAssignedMb) * 100)) : null}
            sub={<>
              {vm.memoryDemandMb != null && <span className="block">{t('vm.memoryDemand')}: {formatBytes(vm.memoryDemandMb * 1048576)}{vm.memoryBufferPercent != null ? ` · ${t('vm.memoryBuffer')}: ${vm.memoryBufferPercent}%` : ''}</span>}
              {t('vm.startup')}: {vm.memoryStartupMb != null ? formatBytes(vm.memoryStartupMb * 1048576) : '—'} · {t('vm.dynamicMemory')}: {vm.dynamicMemoryEnabled == null ? '—' : vm.dynamicMemoryEnabled ? t('common.yes') : t('common.no')}
            </>} />
          <StatCard icon={<Clock className="size-3.5" />} label={t('vm.uptime')} value={vm.powerState === 'Running' ? formatDuration(vm.uptimeMs) : '—'}
            sub={<>{t('vm.shutdownService')}: {vm.shutdownServiceAvailable == null ? '—' : vm.shutdownServiceAvailable ? 'OK' : t('common.no')}</>} />
          <StatCard icon={<Info className="size-3.5" />} label={t('vm.configuration')} value={`Gen ${vm.generation ?? '—'}`}
            sub={<>{t('vm.version')}: {vm.configurationVersion ?? '—'} · {t('vm.checkpoints')}: <CheckpointIcon count={vm.checkpointCount} /></>} />
        </div>
        <div className="mt-3 grid gap-3 lg:grid-cols-2">
          <Section title={t('vm.guest')} bodyClassName="p-3">
            <KeyValue items={[
              [t('vm.guestOs'), vm.guestOperatingSystem ?? '—'],
              [t('vm.state'), <PowerBadge key="p" state={vm.powerState} critical={vm.isCritical} />],
              [t('vm.shutdownService'), vm.shutdownServiceAvailable == null ? '—' : vm.shutdownServiceAvailable ? 'OK' : t('common.no')],
              ['Heartbeat', vm.heartbeat ?? '—'],
              [t('job.completed'), fmtDate(vm.stateChangedAt)],
            ]} />
          </Section>
          <Section title={t('vm.related')} bodyClassName="p-3">
            <KeyValue items={[
              [t('vm.host'), host ? <Link className="text-primary hover:underline" to={`/hosts/${host.id}`}>{host.displayName}</Link> : vm.hostName],
              [t('vm.groups'), groups.length === 0 ? <span className="text-muted-foreground">{t('vm.noGroups')}</span>
                : <div className="flex flex-wrap gap-1">{groups.map((g) => <Link key={g.id} to={`/groups/${g.id}`}><StatusBadge tone="muted"><GroupIcon className="size-3" />{g.name}</StatusBadge></Link>)}</div>],
              ['Hyper-V ID', <span key="i" className="font-mono text-xs">{vm.hyperVId}</span>],
            ]} />
          </Section>
        </div>
        <div className="mt-3 grid gap-3 lg:grid-cols-2">
          <Section title={t('vm.general')} bodyClassName="p-3">
            <KeyValue items={[
              [t('vm.createdAt'), vm.hyperVCreatedAt ? fmtDate(vm.hyperVCreatedAt) : '—'],
              [t('vm.path'), vm.configurationPath
                ? <span key="p" className="flex items-center gap-1"><span className="font-mono text-xs [overflow-wrap:anywhere]">{vm.configurationPath}</span><CopyButton text={vm.configurationPath} /></span>
                : '—'],
              [t('vm.notes'), vm.notes ? <span key="n" className="text-sm whitespace-pre-wrap [overflow-wrap:anywhere]">{vm.notes}</span> : <span className="text-muted-foreground">{vm.notes === '' ? t('vm.noNotes') : '—'}</span>],
            ]} />
            {vm.hyperVCreatedAt === undefined || (vm.hyperVCreatedAt === null && vm.configurationPath == null)
              ? <p className="mt-2 text-xs text-muted-foreground">{t('vm.detailsAgent')}</p> : null}
          </Section>
          <Section title={t('vm.replication')} bodyClassName="p-3">
            <KeyValue items={[
              [t('vm.replicationHealth'), <ReplicationHealth key="h" value={vm.replicationHealth} />],
              [t('vm.replicationMode'), vm.replicationMode != null ? t(('repl.mode.' + vm.replicationMode) as TKey) : '—'],
              [t('vm.replicationState'), vm.replicationState != null ? t(('repl.state.' + Math.min(vm.replicationState, 22)) as TKey) : '—'],
            ]} />
          </Section>
        </div>
        <Section title={t('vm.recentJobs')} className="mt-3">
          <JobTable rows={(data.data?.recentJobs ?? []).map((job) => ({ job, requestedBy: null }))} compact className="max-h-80" />
        </Section>
      </TabsContent>

      <TabsContent value="console" className="min-h-0">
        {tab === 'console' && vm.canConsole && <VmConsole vm={vm} className="h-full" onStart={() => ops.openPower(vm, 'Start')} />}
      </TabsContent>
      <TabsContent value="settings" className="min-h-0">{tab === 'settings' && <VmSettings vm={vm} />}</TabsContent>
      <TabsContent value="hardware" className="min-h-0">{tab === 'hardware' && <VmHardware vm={vm} />}</TabsContent>
      <TabsContent value="checkpoints" className="min-h-0">{tab === 'checkpoints' && <VmCheckpoints vm={vm} />}</TabsContent>
      <TabsContent value="events" className="min-h-0 overflow-auto p-4">
        <Section title={t('vm.history')}><EventsTable events={events.data ?? []} /></Section>
      </TabsContent>
    </Tabs>
  );
}
