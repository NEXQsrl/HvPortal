import { ArrowRightLeft, Boxes, ChevronDown, Cpu, Database, HardDrive, MemoryStick, Plus, RefreshCw, Scale, Server, ShieldCheck, Wrench } from 'lucide-react';
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { get, newIdempotencyKey, post } from '@/api/client';
import type { ClusterDetail, ClusterNodeView, ClusterSharedVolumeStatus, ClusterStatusReport } from '@/api/cluster-types';
import type { Accepted } from '@/api/ops-types';
import { ActionsButton } from '@/components/actions';
import { CopyButton, ErrorAlert, KeyValue, Loading, Meter, Notice, PageHeader, Section, StatCard, StatusBadge } from '@/components/common';
import { HealthBadge } from '@/components/infra/HostHealth';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { IssueList } from '@/components/ops/ClusterDialogs';
import { ClusterBackupStagingSection, nodesWithoutStaging } from '@/components/backup/ClusterBackupStaging';
import { ActionDialog, Field } from '@/components/dialogs';
import { NativeSelect } from '@/components/ui/native-select';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { VmTable } from '@/components/vm/VmTable';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, formatDuration, percent } from '@/lib/format';
import { useLive } from '@/lib/hooks';
import { cn } from '@/lib/utils';

const TABS = ['summary', 'nodes', 'storage', 'quorum', 'network', 'roles', 'vms'] as const;
type Tab = (typeof TABS)[number];

const RESERVE = 2 * 1024 * 1024 * 1024;
const CPU_LIMIT = 4;

const stateTone = (s: string | null | undefined): 'ok' | 'warn' | 'danger' | 'muted' =>
  s === 'Up' || s === 'Online' || s === 'Healthy' ? 'ok' : s === 'Down' || s === 'Failed' || s === 'Unhealthy' || s === 'Offline' ? 'danger' : s == null ? 'muted' : 'warn';

function Empty({ cols, children }: { cols: number; children: ReactNode }) {
  return <TableRow><TableCell colSpan={cols} className="text-sm text-muted-foreground">{children}</TableCell></TableRow>;
}

/**
 * Failover cluster (ADR-041): status and problems with what to do, capacity with one node lost, nodes and their
 * maintenance, storage (shared volumes, Storage Spaces Direct), quorum and witness, networks, roles, VMs. The page
 * shows the last stored read at once and reads the cluster again through any node agent.
 */
export function ClusterPage() {
  const { id } = useParams();
  const { t, fmtDate } = useI18n();
  const inv = useInventory();
  const ops = useOps();
  const [params, setParams] = useSearchParams();
  const [data, setData] = useState<ClusterDetail | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [reading, setReading] = useState(false);
  const seq = useRef(0);

  const load = useCallback(async (live: boolean) => {
    const n = ++seq.current;
    if (live) setReading(true);
    try {
      const r = await get<ClusterDetail>(`/api/v1/clusters/${id}${live ? '/status' : ''}`);
      if (n === seq.current || live) setData(r);
      setError(null);
    } catch (e) {
      setError(e);
    } finally {
      if (live) setReading(false);
    }
  }, [id]);

  // Stored status first (immediate), then a live read through the node agents.
  useEffect(() => {
    setData(null);
    void load(false).then(() => load(true));
  }, [load]);
  const reloadStored = useCallback(() => { void load(false); }, [load]);
  useLive(reloadStored, ['inventoryChanged'], 2000);

  if (!data) return error ? <div className="p-4"><ErrorAlert error={error} /></div> : <Loading />;
  const tab: Tab = (TABS as readonly string[]).includes(params.get('tab') ?? '') ? (params.get('tab') as Tab) : 'summary';
  const status = data.status;
  const report = status.report;
  const nodeHosts = data.nodes.map((n) => n.hostId).filter((x): x is string => !!x);
  const vms = inv.vms.filter((v) => nodeHosts.includes(v.hostId));
  const up = data.nodes.filter((n) => n.state === 'Up').length;
  const agents = data.nodes.filter((n) => n.agentConnected).length;
  const summary = { id: data.id, name: data.name, description: data.description, can: { manage: data.can.manage } };

  return (
    <Tabs value={tab} onValueChange={(v) => setParams(v === 'summary' ? {} : { tab: v }, { replace: true })} className="flex h-full min-h-0 flex-col gap-0">
      <PageHeader icon={<Boxes className="size-5" />} title={data.name}
        badges={<>
          {status.level ? <HealthBadge status={status.level} /> : null}
          {data.windowsName && <StatusBadge tone="muted">{data.windowsName}</StatusBadge>}
        </>}
        subtitle={[t('cl.nodesCount', { up, total: data.nodes.length, agents }), data.description].filter(Boolean).join(' · ')}
        actions={<>
          <Button size="sm" variant="outline" onClick={() => void load(true)} disabled={reading}><RefreshCw className={cn(reading && 'animate-spin')} />{t('cl.refresh')}</Button>
          {data.can.createVm && <Button size="sm" onClick={() => ops.openCreateVm({ hostId: suggestedNode(data, inv.hosts.filter((h) => h.canCreateVm).map((h) => h.id)) })}><Plus />{t('cl.newVm')}</Button>}
          <ActionsButton items={ops.clusterActions(summary, { page: true, nodes: nodeHosts.length, createVm: false })} label={data.name}>{t('common.actions')}<ChevronDown /></ActionsButton>
        </>}>
        <TabsList variant="line" className="page-tabs -mb-px h-9 w-full justify-start rounded-none bg-transparent p-0">
          {TABS.map((x) => <TabsTrigger key={x} value={x} className="flex-none px-3">{t(('cl.tab.' + x) as TKey)}{x === 'vms' && <span className="ml-1 text-xs text-muted-foreground">{vms.length}</span>}</TabsTrigger>)}
        </TabsList>
      </PageHeader>
      <div className="space-y-2 px-4 pt-3">
        <ErrorAlert error={error} />
        <div className="text-xs text-muted-foreground">
          {reading ? t('cl.reading') : report ? t('cl.readBy', { at: fmtDate(report.collectedUtc), node: report.readByNode ?? '?' })
            : status.storedAt ? t('cl.storedAt', { at: fmtDate(status.storedAt) }) : t('cl.neverRead')}
        </div>
        {!report && status.error && !reading && (
          <Notice kind="danger" title={t('cl.unreadable')}>
            <p className="font-mono text-xs [overflow-wrap:anywhere]">{status.error}</p>
            {status.attempts.length > 0 && <ul className="mt-1 list-disc pl-4 text-xs">{status.attempts.map((a) => <li key={a} className="[overflow-wrap:anywhere]">{a}</li>)}</ul>}
          </Notice>
        )}
      </div>

      <TabsContent value="summary" className="min-h-0 overflow-auto p-4"><ClusterSummaryTab data={data} /></TabsContent>
      <TabsContent value="nodes" className="min-h-0 overflow-auto p-4">{tab === 'nodes' && <NodesTab data={data} onChanged={reloadStored} />}</TabsContent>
      <TabsContent value="storage" className="min-h-0 overflow-auto p-4">{tab === 'storage' && <StorageTab report={report} data={data} onQueued={() => void load(true)} />}</TabsContent>
      <TabsContent value="quorum" className="min-h-0 overflow-auto p-4">{tab === 'quorum' && <QuorumTab report={report} />}</TabsContent>
      <TabsContent value="network" className="min-h-0 overflow-auto p-4">{tab === 'network' && <NetworkTab report={report} />}</TabsContent>
      <TabsContent value="roles" className="min-h-0 overflow-auto p-4">{tab === 'roles' && <RolesTab report={report} />}</TabsContent>
      <TabsContent value="vms" className="flex min-h-0 flex-col">{tab === 'vms' && <VmTable vms={vms} className="flex-1" />}</TabsContent>
    </Tabs>
  );
}

/** The node with the most free memory among those the user may create VMs on (Up, agent ready). */
export function suggestedNode(data: ClusterDetail, allowedHosts: string[]): string | undefined {
  return data.nodes.filter((n) => n.hostId && allowedHosts.includes(n.hostId) && n.state !== 'Paused' && n.state !== 'Down' && n.agentConnected)
    .sort((a, b) => (b.freeMemoryBytes ?? 0) - (a.freeMemoryBytes ?? 0))[0]?.hostId ?? undefined;
}

function ClusterSummaryTab({ data }: { data: ClusterDetail }) {
  const { t } = useI18n();
  const c = data.status.capacity;
  const report = data.status.report;
  const usedMem = c.totalMemoryBytes - c.freeMemoryBytes;
  const problems = data.status.issues;
  return (
    <div className="space-y-3">
      <Section title={t('cl.problems')} bodyClassName="p-3">
        {problems.length === 0 ? <div className="text-sm text-muted-foreground">{data.status.level ? t('cl.noProblems') : t('cl.neverRead')}</div> : <IssueList issues={problems} />}
      </Section>
      {data.backup.credentialRepositories > 0 && nodesWithoutStaging(data).length > 0 && (
        <Notice kind="warn" title={t('clst.missingTitle', { n: nodesWithoutStaging(data).length })}>
          {t('clst.missing', { nodes: nodesWithoutStaging(data).map((n) => n.hostName ?? n.name).join(', ') })}{' '}
          <Link className="text-primary hover:underline" to="?tab=nodes">{t('clst.open')}</Link>
        </Notice>
      )}
      <div className="resource-grid grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard icon={<MemoryStick className="size-3.5" />} label={t('cl.cap.memory')} value={`${formatBytes(usedMem)} / ${formatBytes(c.totalMemoryBytes)}`}
          meter={percent(usedMem, c.totalMemoryBytes)}
          sub={t('cl.cap.memoryValue', { used: formatBytes(c.vmMemoryBytes), free: formatBytes(c.freeMemoryBytes), total: formatBytes(c.totalMemoryBytes) })} />
        <StatCard icon={<Cpu className="size-3.5" />} label={t('cl.cap.cpu')} value={c.vcpuRatio != null ? `${c.vcpuRatio}:1` : '—'}
          sub={<>
            <div>{t('cl.cap.cpuValue', { vcpu: c.runningVcpus, lp: c.logicalProcessors, ratio: c.vcpuRatio ?? '—' })}</div>
            {c.vcpuRatioN1 != null && <div className={cn(c.vcpuRatioN1 > CPU_LIMIT && 'text-warning')}>{t('cl.cap.cpuN1', { ratio: c.vcpuRatioN1 })}</div>}
          </>} />
        <StatCard icon={<Scale className="size-3.5" />} label={t('cl.cap.n1')}
          value={c.toleratesNodeLoss == null ? '—' : <span className={c.toleratesNodeLoss ? 'text-success' : 'text-warning'}>{c.toleratesNodeLoss ? 'OK' : '!'}</span>}
          sub={c.nodesUp <= 1 && c.nodes > 1 ? t('cl.cap.n1Single') : c.toleratesNodeLoss == null ? t('cl.cap.n1Unknown') : c.toleratesNodeLoss ? t('cl.cap.n1Ok')
            : t('cl.cap.n1Fail', { node: c.worstNode ?? '?', needed: formatBytes(c.failoverNeededBytes), available: formatBytes(c.failoverAvailableBytes) })} />
        <StatCard icon={<HardDrive className="size-3.5" />} label={t('cl.cap.storage')}
          value={c.sharedVolumeBytes != null ? `${formatBytes(c.sharedVolumeFreeBytes)}` : '—'}
          meter={c.sharedVolumeBytes != null ? percent(c.sharedVolumeBytes - (c.sharedVolumeFreeBytes ?? 0), c.sharedVolumeBytes) : undefined}
          sub={<>
            {c.sharedVolumeBytes != null && <div>{t('cl.cap.storageValue', { free: formatBytes(c.sharedVolumeFreeBytes), size: formatBytes(c.sharedVolumeBytes) })}</div>}
            {c.poolFreeBytes != null && <div className={cn(c.poolReserveBytes != null && c.poolFreeBytes < c.poolReserveBytes && 'text-warning')}>
              {t('cl.cap.pool', { free: formatBytes(c.poolFreeBytes), reserve: formatBytes(c.poolReserveBytes) })}</div>}
          </>} />
      </div>
      <p className="text-xs text-muted-foreground">{t('cl.cap.help', { reserve: formatBytes(RESERVE), limit: CPU_LIMIT })}</p>
      <div className="grid gap-3 xl:grid-cols-2">
        <Section title={t('cl.core.title')} bodyClassName="p-3">
          {!report ? <div className="text-sm text-muted-foreground">{t('cl.neverRead')}</div> : report.coreGroupState === undefined ? (
            <div className="text-sm text-muted-foreground">{t('cl.core.oldAgent')}</div>
          ) : (
            <div className="space-y-2 text-sm">
              <div className="flex items-center gap-2">{t('cl.core.group')} <StatusBadge tone={stateTone(report.coreGroupState)}>{report.coreGroupState ?? '—'}</StatusBadge></div>
              <ul className="space-y-1">
                {(report.coreResources ?? []).map((r) => (
                  <li key={r.name} className="flex items-center justify-between gap-2">
                    <span className="min-w-0 truncate">{r.name} <span className="text-xs text-muted-foreground">{r.resourceType}</span></span>
                    <StatusBadge tone={stateTone(r.state)}>{r.state ?? '—'}</StatusBadge>
                  </li>
                ))}
              </ul>
              {report.storage?.s2dEnabled && (
                <div className="flex items-center gap-2 border-t pt-2">{t('cl.core.healthService')}
                  <StatusBadge tone={(report.healthFaults ?? []).length === 0 ? 'ok' : (report.healthFaults ?? []).some((f) => f.severity === 'Critical') ? 'danger' : 'warn'}>
                    {(report.healthFaults ?? []).length === 0 ? t('cl.core.noFaults') : t('cl.core.faults', { n: (report.healthFaults ?? []).length })}
                  </StatusBadge>
                </div>
              )}
            </div>
          )}
        </Section>
        <Section title={t('cl.info.title')} bodyClassName="p-3">
          <KeyValue items={[
            [t('cl.info.windowsName'), report?.fqdn || report?.name || data.windowsName || '—'],
            [t('cl.info.access'), report?.administrativeAccessPoint === 'Dns' ? t('cl.info.workgroup') : report?.administrativeAccessPoint || '—'],
            [t('cl.info.domain'), report?.domain || '—'],
            [t('cl.q.type'), report?.quorum ? `${report.quorum.quorumType ?? '—'} · ${t('cl.q.witness')}: ${report.quorum.witnessType && report.quorum.witnessType !== 'None' ? `${report.quorum.witnessType} (${report.quorum.witnessState ?? '?'})` : t('cl.q.witnessNone')}` : '—'],
            [t('cl.s2d.title'), report?.storage ? (report.storage.s2dEnabled ? `${t('cl.s2d.enabled')}${report.storage.s2dState ? ` · ${report.storage.s2dState}` : ''}` : t('cl.s2d.disabled')) : '—'],
          ]} />
        </Section>
        <Section title={t('cl.tab.nodes')}>
          <Table>
            <TableBody>
              {data.nodes.map((n) => (
                <TableRow key={n.name}>
                  <TableCell className="font-medium">{n.hostId ? <Link className="hover:underline" to={`/hosts/${n.hostId}`}>{n.name}</Link> : n.name}</TableCell>
                  <TableCell><NodeStateBadge node={n} /></TableCell>
                  <TableCell className="w-32"><Meter value={percent(n.totalMemoryBytes != null && n.freeMemoryBytes != null ? n.totalMemoryBytes - n.freeMemoryBytes : null, n.totalMemoryBytes)} /></TableCell>
                  <TableCell className="text-right text-xs text-muted-foreground tabular">{n.runningVms} VM</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Section>
      </div>
    </div>
  );
}

function NodeStateBadge({ node }: { node: ClusterNodeView }) {
  const { t } = useI18n();
  const state = node.state ?? 'Unknown';
  return <StatusBadge tone={state === 'Paused' ? 'warn' : stateTone(node.state)}>{t(('cl.state.' + (['Up', 'Down', 'Paused', 'Joining'].includes(state) ? state : 'Unknown')) as TKey)}</StatusBadge>;
}

function NodesTab({ data, onChanged }: { data: ClusterDetail; onChanged: () => void }) {
  const { t } = useI18n();
  // Problems of each node (subject = node or host name), shown under its name.
  const nodeIssues = (n: ClusterNodeView) => data.status.issues.filter((i) => i.level !== 'Info' && i.subject
    && [n.name, n.hostName].some((x) => x && x.toLowerCase() === i.subject!.toLowerCase()));
  const inv = useInventory();
  const ops = useOps();
  const accessDenied = data.nodes.some((n) => n.clusterError?.startsWith('access_denied'));
  return (
    <div className="space-y-3">
      {accessDenied && (
        <Notice kind="warn" title={t('cl.accessFix')}>
          <div className="mt-1 flex items-center gap-2">
            <code className="flex-1 rounded border bg-muted px-2 py-1 font-mono text-xs break-all">Grant-ClusterAccess -User 'NT SERVICE\HvPortalAgent' -Full</code>
            <CopyButton text={"Grant-ClusterAccess -User 'NT SERVICE\\HvPortalAgent' -Full"} />
          </div>
          <p className="mt-1 text-xs">{t('cl.accessFixNote')}</p>
        </Notice>
      )}
      <Section title={t('cl.tab.nodes')}>
        <Table>
          <TableHeader><TableRow>
            <TableHead>{t('cl.node.name')}</TableHead><TableHead>{t('cl.node.state')}</TableHead><TableHead>{t('cl.node.agent')}</TableHead>
            <TableHead className="w-40">{t('cl.node.memory')}</TableHead><TableHead className="text-right">CPU</TableHead><TableHead className="text-right">{t('cl.node.vms')}</TableHead>
            <TableHead className="text-right">{t('cl.node.votes')}</TableHead><TableHead />
          </TableRow></TableHeader>
          <TableBody>
            {data.nodes.length === 0 ? <Empty cols={8}>—</Empty> : data.nodes.map((n) => {
              const host = n.hostId ? inv.hostById.get(n.hostId) : undefined;
              const used = n.totalMemoryBytes != null && n.freeMemoryBytes != null ? n.totalMemoryBytes - n.freeMemoryBytes : null;
              const paused = n.state === 'Paused' || n.storageMaintenance === true;
              return (
                <TableRow key={n.name} className={cn(n.state === 'Down' && 'bg-destructive/5')}>
                  <TableCell>
                    <div className="flex items-center gap-1.5 font-medium"><Server className="size-3.5 text-muted-foreground" />
                      {n.hostId ? <Link className="hover:underline" to={`/hosts/${n.hostId}`}>{n.name}</Link> : n.name}</div>
                    <div className="text-xs text-muted-foreground">{n.hostName && n.hostName.toLowerCase() !== n.name.toLowerCase() ? n.hostName : n.hostId ? n.model : t('cl.node.noHost')}</div>
                    {n.clusterMismatch && <div className="text-xs text-warning">{t(('cl.mismatch.' + n.clusterMismatch) as TKey, { name: host?.clusterFacts?.name ?? '?' })}</div>}
                    {n.notClusteredRunningVms.length > 0 && <div className="text-xs text-warning">{t('cl.node.notHa', { vms: n.notClusteredRunningVms.join(', ') })}</div>}
                    {nodeIssues(n).length > 0 && <IssueList issues={nodeIssues(n)} className="mt-1 max-w-md [&_li]:text-xs" />}
                  </TableCell>
                  <TableCell>
                    <NodeStateBadge node={n} />
                    {n.drainStatus && n.drainStatus !== 'NotInitiated' && <div className="mt-0.5 text-xs text-muted-foreground">drain: {n.drainStatus}</div>}
                    {n.storageMaintenance && <div className="mt-0.5 text-xs text-warning">{t('cl.node.storageMaintenance')}</div>}
                  </TableCell>
                  <TableCell>
                    <StatusBadge tone={!n.hostId ? 'danger' : n.agentConnected ? (n.agentReady ? 'ok' : 'warn') : 'danger'}>
                      {!n.hostId ? t('cl.agent.none') : !n.agentConnected ? t('cl.agent.disconnected') : n.agentReady ? t('cl.agent.connected') : t('cl.agent.outdated')}
                    </StatusBadge>
                    {n.agentVersion && <div className="mt-0.5 text-xs text-muted-foreground">{n.agentVersion}</div>}
                  </TableCell>
                  <TableCell>
                    <Meter value={percent(used, n.totalMemoryBytes)} />
                    <div className="mt-0.5 text-xs text-muted-foreground tabular">{formatBytes(n.freeMemoryBytes)} / {formatBytes(n.totalMemoryBytes)}</div>
                  </TableCell>
                  <TableCell className="text-right tabular">{n.cpuLoadPercent != null ? `${n.cpuLoadPercent}%` : '—'}<div className="text-xs text-muted-foreground">{n.logicalProcessors ?? '?'} LP</div></TableCell>
                  <TableCell className="text-right tabular">{n.runningVms}<div className="text-xs text-muted-foreground">{n.runningVcpus} vCPU · {formatBytes(n.runningVmMemoryBytes)}</div></TableCell>
                  <TableCell className="text-right tabular">{n.dynamicWeight ?? '—'}{n.nodeWeight != null && n.dynamicWeight !== n.nodeWeight && <span className="text-xs text-muted-foreground"> / {n.nodeWeight}</span>}</TableCell>
                  <TableCell className="text-right whitespace-nowrap">
                    {host && n.canMaintain && (
                      <Button size="sm" variant={paused ? 'default' : 'outline'} onClick={() => ops.openMaintenance(host, paused ? 'Exit' : 'Enter')} disabled={n.state === 'Down'}>
                        <Wrench />{t(paused ? 'cl.maint.exit' : 'cl.maint.enter')}
                      </Button>
                    )}
                    {host && data.can.manage && (
                      <Button size="sm" variant="ghost" className="ml-1" onClick={() => ops.openRemoveClusterNode(data.id, { id: host.id, displayName: host.displayName })}>{t('cl.node.remove')}</Button>
                    )}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </Section>
      {data.suggestedHosts.length > 0 && (
        <Notice kind="info" title={t('cl.suggested')}>
          <div className="flex flex-wrap gap-2">{data.suggestedHosts.map((h) => <Link key={h.id} className="text-primary hover:underline" to={`/hosts/${h.id}`}>{h.displayName}</Link>)}</div>
        </Notice>
      )}
      <ClusterBackupStagingSection data={data} onChanged={onChanged} />
    </div>
  );
}

function NotRead() {
  const { t } = useI18n();
  return <Notice kind="info">{t('cl.neverRead')}</Notice>;
}

function StorageTab({ report, data, onQueued }: { report: ClusterStatusReport | null; data: ClusterDetail; onQueued: () => void }) {
  const { t } = useI18n();
  const [moving, setMoving] = useState<ClusterSharedVolumeStatus | null>(null);
  // Who may put nodes in maintenance may also move the owner of a volume (hosts.maintenance on the new owner, checked by the server).
  const canMove = data.nodes.some((n) => n.canMaintain && n.state === 'Up');
  if (!report) return <NotRead />;
  const s = report.storage;
  const badDisks = s?.physicalDisks.filter((d) => d.healthStatus !== 'Healthy' || d.usage === 'Retired') ?? [];
  return (
    <div className="space-y-3">
      <Section title={<span className="flex items-center gap-2"><HardDrive className="size-4 text-muted-foreground" />{t('cl.csv.title')}</span>}>
        <Table>
          <TableHeader><TableRow>
            <TableHead>{t('common.name')}</TableHead><TableHead>{t('cl.csv.owner')}</TableHead><TableHead>{t('cl.node.state')}</TableHead>
            <TableHead>{t('cl.csv.access')}</TableHead><TableHead className="w-40">{t('host.usage')}</TableHead><TableHead className="text-right">{t('host.freeSpace')}</TableHead>
            {canMove && <TableHead />}
          </TableRow></TableHeader>
          <TableBody>
            {report.sharedVolumes.length === 0 ? <Empty cols={6}>{t('cl.csv.none')}</Empty> : report.sharedVolumes.map((v) => (
              <TableRow key={v.name + (v.path ?? '')}>
                <TableCell><div className="font-mono text-xs">{v.path || '—'}</div><div className="text-xs text-muted-foreground">{v.name}</div></TableCell>
                <TableCell className="text-sm">{v.ownerNode ?? '—'}</TableCell>
                <TableCell><StatusBadge tone={stateTone(v.state)}>{v.state ?? '—'}</StatusBadge>{v.maintenanceMode && <div className="text-xs text-warning">maintenance</div>}</TableCell>
                <TableCell><StatusBadge tone={!v.access ? 'muted' : v.access === 'Direct' ? 'ok' : 'warn'}>{v.access ?? '—'}</StatusBadge></TableCell>
                <TableCell><Meter value={percent(v.sizeBytes != null && v.freeBytes != null ? v.sizeBytes - v.freeBytes : null, v.sizeBytes)} /></TableCell>
                <TableCell className="text-right whitespace-nowrap tabular">{formatBytes(v.freeBytes)} / {formatBytes(v.sizeBytes)}</TableCell>
                {canMove && (
                  <TableCell className="text-right">
                    <Button size="sm" variant="outline" disabled={v.state !== 'Online'} onClick={() => setMoving(v)}><ArrowRightLeft />{t('cl.csv.move')}</Button>
                  </TableCell>
                )}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Section>

      {(report.replication ?? []).length > 0 && (
        <Section title={t('cl.sr.title')}>
          <Table>
            <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('cl.sr.role')}</TableHead><TableHead>{t('cl.sr.partner')}</TableHead>
              <TableHead>{t('cl.node.state')}</TableHead><TableHead className="text-right">{t('cl.sr.remaining')}</TableHead></TableRow></TableHeader>
            <TableBody>
              {(report.replication ?? []).map((r) => (
                <TableRow key={r.name}>
                  <TableCell className="font-medium">{r.name}<div className="text-xs text-muted-foreground">{r.replicationMode}</div></TableCell>
                  <TableCell className="text-sm">{r.isPrimary ? t('cl.sr.source') : t('cl.sr.destination')}</TableCell>
                  <TableCell className="text-sm">{r.partner ?? '—'}</TableCell>
                  <TableCell><StatusBadge tone={r.status === 'ContinuouslyReplicating' ? 'ok' : 'warn'}>{r.status || '—'}</StatusBadge></TableCell>
                  <TableCell className="text-right tabular">{formatBytes(r.bytesRemaining)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Section>
      )}
      {moving && <MoveSharedVolumeDialog clusterId={data.id} volume={moving} nodes={data.nodes} onClose={() => setMoving(null)} onQueued={onQueued} />}
      <Section title={<span className="flex items-center gap-2"><Database className="size-4 text-muted-foreground" />{t('cl.s2d.title')}</span>}
        actions={s ? <StatusBadge tone={s.s2dEnabled ? 'ok' : 'muted'}>{s.s2dEnabled ? `${t('cl.s2d.enabled')}${s.s2dState ? ` · ${s.s2dState}` : ''}` : '—'}</StatusBadge> : null}
        bodyClassName={s?.s2dEnabled ? undefined : 'p-3'}>
        {!s ? <div className="text-sm text-muted-foreground">{t('health.notRead')}</div> : !s.s2dEnabled ? <div className="text-sm text-muted-foreground">{t('cl.s2d.disabled')}</div> : (
          <div className="divide-y">
            <div className="p-3">
              <div className="mb-1 text-sm font-semibold">{t('cl.s2d.redundancy')}</div>
              <p className="mb-2 text-xs text-muted-foreground">{t('cl.s2d.redundancyHelp')}</p>
              <ul className="space-y-1.5">
                {s.virtualDisks.length === 0 ? <li className="text-sm text-muted-foreground">—</li> : s.virtualDisks.map((v) => {
                  const r = redundancy(v.healthStatus, v.operationalStatus);
                  return (
                    <li key={v.name} className="flex flex-wrap items-center justify-between gap-2 text-sm">
                      <span className="font-medium">{v.name} <span className="text-xs font-normal text-muted-foreground">{v.resiliency ?? ''}{v.dataCopies ? ` · ${t('cl.s2d.copies', { n: v.dataCopies })}` : ''}</span></span>
                      <StatusBadge tone={r.tone}>{t(r.key)}</StatusBadge>
                    </li>
                  );
                })}
              </ul>
            </div>
            {(report.healthFaults ?? []).length > 0 && (
              <div className="p-3">
                <div className="mb-2 text-sm font-semibold">{t('cl.s2d.faults')}</div>
                <ul className="space-y-2">
                  {(report.healthFaults ?? []).map((f, i) => (
                    <li key={i} className="rounded-md border p-2 text-sm">
                      <div className="flex items-center gap-2"><StatusBadge tone={f.severity === 'Critical' ? 'danger' : 'warn'}>{f.severity ?? '—'}</StatusBadge><span className="font-medium">{f.faultingObject}</span></div>
                      <div className="mt-1">{f.reason}</div>
                      {f.recommendedAction && <div className="mt-1 text-xs text-muted-foreground">{t('cl.s2d.action')}: {f.recommendedAction}</div>}
                    </li>
                  ))}
                </ul>
              </div>
            )}
            <Table>
              <TableHeader><TableRow><TableHead>{t('cl.s2d.pools')}</TableHead><TableHead>{t('health.reported')}</TableHead><TableHead className="w-40">{t('cl.s2d.allocated')}</TableHead><TableHead className="text-right">{t('host.size')}</TableHead></TableRow></TableHeader>
              <TableBody>
                {s.pools.length === 0 ? <Empty cols={4}>—</Empty> : s.pools.map((p) => (
                  <TableRow key={p.name}>
                    <TableCell className="font-medium">{p.name}{s.cacheState && <div className="text-xs text-muted-foreground">{t('cl.s2d.cache')}: {s.cacheState}</div>}</TableCell>
                    <TableCell><StatusBadge tone={p.isReadOnly ? 'danger' : stateTone(p.healthStatus)}>{p.healthStatus ?? '—'}</StatusBadge><div className="text-xs text-muted-foreground">{p.operationalStatus}</div></TableCell>
                    <TableCell><Meter value={percent(p.allocatedBytes, p.sizeBytes)} /><div className="text-xs text-muted-foreground tabular">{formatBytes(p.allocatedBytes)}</div></TableCell>
                    <TableCell className="text-right tabular">{formatBytes(p.sizeBytes)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Table>
              <TableHeader><TableRow><TableHead>{t('cl.s2d.vdisks')}</TableHead><TableHead>{t('health.reported')}</TableHead><TableHead>{t('cl.s2d.resiliency')}</TableHead><TableHead className="text-right">{t('host.size')}</TableHead><TableHead className="text-right">{t('cl.s2d.footprint')}</TableHead></TableRow></TableHeader>
              <TableBody>
                {s.virtualDisks.length === 0 ? <Empty cols={5}>—</Empty> : s.virtualDisks.map((v) => (
                  <TableRow key={v.name} className={cn(v.healthStatus === 'Unhealthy' && 'bg-destructive/5')}>
                    <TableCell className="font-medium">{v.name}</TableCell>
                    <TableCell><StatusBadge tone={stateTone(v.healthStatus)}>{v.healthStatus ?? '—'}</StatusBadge><div className="text-xs text-muted-foreground">{v.operationalStatus}</div></TableCell>
                    <TableCell className="text-sm">{v.resiliency ?? '—'}{v.dataCopies != null && <span className="text-xs text-muted-foreground"> · {v.dataCopies}×</span>}</TableCell>
                    <TableCell className="text-right tabular">{formatBytes(v.sizeBytes)}</TableCell>
                    <TableCell className="text-right tabular">{formatBytes(v.footprintBytes)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <div className="p-3">
              <div className="mb-1 text-sm font-semibold">{t('cl.s2d.jobs')}</div>
              <p className="mb-2 text-xs text-muted-foreground">{t('cl.s2d.jobsHelp')}</p>
              {s.jobs.length === 0 ? <div className="flex items-center gap-2 text-sm text-success"><ShieldCheck className="size-4" />{t('cl.s2d.noJobs')}</div> : (
                <ul className="space-y-3">
                  {s.jobs.map((j) => {
                    const eta = j.elapsedSeconds && j.percentComplete && j.percentComplete > 0 && j.percentComplete < 100
                      ? Math.round(j.elapsedSeconds * (100 - j.percentComplete) / j.percentComplete) : null;
                    return (
                      <li key={j.name} className="text-sm">
                        <div className="flex justify-between"><span>{j.name} <span className="text-xs text-muted-foreground">{j.state}</span></span><span className="tabular">{j.percentComplete ?? '?'}%</span></div>
                        <Meter value={j.percentComplete} className="mt-1" label={t('cl.s2d.progress')} />
                        <div className="mt-1 flex flex-wrap gap-x-3 text-xs text-muted-foreground tabular">
                          {j.bytesTotal ? <span>{t('cl.s2d.copied', { done: formatBytes(j.bytesProcessed), total: formatBytes(j.bytesTotal) })}</span> : null}
                          {j.elapsedSeconds != null && <span>{t('cl.s2d.elapsed', { time: formatDuration(j.elapsedSeconds * 1000) })}</span>}
                          {eta != null && <span>{t('cl.s2d.eta', { time: formatDuration(eta * 1000) })}</span>}
                        </div>
                      </li>
                    );
                  })}
                </ul>
              )}
            </div>
            <div>
              <div className="flex items-center justify-between px-3 py-2 text-sm font-semibold">{t('cl.s2d.pdisks')}
                <span className="text-xs font-normal text-muted-foreground">{t('cl.s2d.pdiskSummary', { total: s.physicalDisks.length, bad: badDisks.length })}</span></div>
              <Table containerClassName="max-h-80">
                <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('health.type')}</TableHead><TableHead>{t('cl.s2d.usage')}</TableHead><TableHead>{t('health.reported')}</TableHead><TableHead className="text-right">{t('host.size')}</TableHead></TableRow></TableHeader>
                <TableBody>
                  {[...badDisks, ...s.physicalDisks.filter((d) => !badDisks.includes(d))].map((d) => (
                    <TableRow key={d.name + (d.serialNumber ?? '')} className={cn(badDisks.includes(d) && 'bg-warning/5')}>
                      <TableCell><div className="font-medium">{d.name}</div>{d.serialNumber && <div className="font-mono text-xs text-muted-foreground">{d.serialNumber}</div>}</TableCell>
                      <TableCell className="text-xs">{d.mediaType ?? '—'}</TableCell>
                      <TableCell className="text-xs">{d.usage ?? '—'}</TableCell>
                      <TableCell><StatusBadge tone={stateTone(d.healthStatus)}>{d.healthStatus ?? '—'}</StatusBadge><div className="text-xs text-muted-foreground">{d.operationalStatus}</div></TableCell>
                      <TableCell className="text-right tabular">{formatBytes(d.sizeBytes)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          </div>
        )}
      </Section>
    </div>
  );
}

function QuorumTab({ report }: { report: ClusterStatusReport | null }) {
  const { t } = useI18n();
  if (!report) return <NotRead />;
  const q = report.quorum;
  const witnessVote = q?.witnessType && q.witnessType !== 'None' ? 1 : 0;
  const total = report.nodes.reduce((s, n) => s + (n.nodeWeight ?? 1), 0) + witnessVote;
  const present = report.nodes.filter((n) => n.state === 'Up' || n.state === 'Paused').reduce((s, n) => s + (n.nodeWeight ?? 1), 0)
    + (witnessVote && q?.witnessState === 'Online' ? 1 : 0);
  return (
    <div className="grid gap-3 xl:grid-cols-2">
      <Section title={t('cl.q.title')} bodyClassName="p-3">
        {!q ? <div className="text-sm text-muted-foreground">{t('health.notRead')}</div> : (
          <KeyValue items={[
            [t('cl.q.type'), q.quorumType ?? '—'],
            [t('cl.q.witness'), q.witnessType && q.witnessType !== 'None'
              ? <span key="w">{q.witnessType} · {q.witnessName} <StatusBadge tone={stateTone(q.witnessState)}>{q.witnessState ?? '—'}</StatusBadge></span> : t('cl.q.witnessNone')],
            [t('cl.q.target'), q.witnessTarget ? <span key="tg" className="font-mono text-xs [overflow-wrap:anywhere]">{q.witnessTarget}</span> : '—'],
            [t('cl.q.dynamic'), q.dynamicQuorum == null ? '—' : q.dynamicQuorum ? t('common.yes') : t('common.no')],
            [t('cl.q.votes'), t('cl.q.votesValue', { up: present, total })],
          ]} />
        )}
        <p className="mt-3 text-xs text-muted-foreground">{t('cl.q.help')}</p>
      </Section>
      <Section title={t('cl.tab.nodes')}>
        <Table>
          <TableHeader><TableRow><TableHead>{t('cl.node.name')}</TableHead><TableHead>{t('cl.node.state')}</TableHead><TableHead className="text-right">{t('cl.node.votes')}</TableHead></TableRow></TableHeader>
          <TableBody>
            {report.nodes.map((n) => (
              <TableRow key={n.name}>
                <TableCell className="font-medium">{n.name}</TableCell>
                <TableCell><StatusBadge tone={n.state === 'Paused' ? 'warn' : stateTone(n.state)}>{n.state ?? '—'}</StatusBadge></TableCell>
                <TableCell className="text-right tabular">{n.dynamicWeight ?? '—'} / {n.nodeWeight ?? '—'}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Section>
    </div>
  );
}

function NetworkTab({ report }: { report: ClusterStatusReport | null }) {
  const { t } = useI18n();
  if (!report) return <NotRead />;
  return (
    <Section title={t('cl.tab.network')}>
      <Table>
        <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('cl.node.state')}</TableHead><TableHead>{t('cl.net.role')}</TableHead><TableHead>{t('cl.net.address')}</TableHead></TableRow></TableHeader>
        <TableBody>
          {report.networks.length === 0 ? <Empty cols={4}>{t('cl.net.none')}</Empty> : report.networks.map((n) => (
            <TableRow key={n.name}>
              <TableCell className="font-medium">{n.name}</TableCell>
              <TableCell><StatusBadge tone={stateTone(n.state)}>{n.state ?? '—'}</StatusBadge></TableCell>
              <TableCell className="text-sm">{n.role ?? '—'}</TableCell>
              <TableCell className="font-mono text-xs">{n.address || '—'}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Section>
  );
}

function RolesTab({ report }: { report: ClusterStatusReport | null }) {
  const { t } = useI18n();
  const inv = useInventory();
  if (!report) return <NotRead />;
  const vmByHyperV = new Map(inv.vms.map((v) => [v.hyperVId.toLowerCase(), v]));
  return (
    <div className="space-y-3">
      {report.failedResources.length > 0 && (
        <Section title={t('cl.roles.failed')}>
          <Table>
            <TableBody>
              {report.failedResources.map((r) => (
                <TableRow key={r.name} className="bg-destructive/5">
                  <TableCell className="font-medium">{r.name}</TableCell><TableCell className="text-xs">{r.resourceType}</TableCell>
                  <TableCell className="text-xs">{r.ownerGroup}</TableCell><TableCell className="text-xs">{r.ownerNode}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Section>
      )}
      <Section title={t('cl.tab.roles')}>
        <Table>
          <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('cl.roles.type')}</TableHead><TableHead>{t('cl.node.state')}</TableHead><TableHead>{t('cl.roles.owner')}</TableHead></TableRow></TableHeader>
          <TableBody>
            {report.roles.length === 0 ? <Empty cols={4}>{t('cl.roles.none')}</Empty> : report.roles.map((r) => {
              const vm = r.vmId ? vmByHyperV.get(r.vmId.toLowerCase()) : undefined;
              return (
                <TableRow key={r.name} className={cn(r.state === 'Failed' && 'bg-destructive/5')}>
                  <TableCell className="font-medium">{vm ? <Link className="hover:underline" to={`/vms/${vm.id}`}>{r.name}</Link> : r.name}</TableCell>
                  <TableCell className="text-xs">{r.groupType ?? '—'}</TableCell>
                  <TableCell><StatusBadge tone={r.state === 'Offline' ? 'muted' : stateTone(r.state)}>{r.state ?? '—'}</StatusBadge></TableCell>
                  <TableCell className="text-sm">{r.ownerNode ?? '—'}</TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </Section>
    </div>
  );
}

/**
 * Another node becomes the owner (coordinator) of a cluster shared volume (Move-ClusterSharedVolume, agent 0.9.1). The
 * data does not move and the VMs keep running; the server checks the live cluster and the permission on that node.
 */
function MoveSharedVolumeDialog({ clusterId, volume, nodes, onClose, onQueued }: {
  clusterId: string; volume: ClusterSharedVolumeStatus; nodes: ClusterNodeView[]; onClose: () => void; onQueued: () => void;
}) {
  const { t } = useI18n();
  const ops = useOps();
  const targets = nodes.filter((n) => n.hostId && n.canMaintain && n.state === 'Up' && n.name.toLowerCase() !== (volume.ownerNode ?? '').toLowerCase());
  const [hostId, setHostId] = useState(targets[0]?.hostId ?? '');
  const [key] = useState(newIdempotencyKey);
  return (
    <ActionDialog title={t('cl.csv.moveTitle', { name: volume.path || volume.name })} confirmLabel={t('cl.csv.moveConfirm')} disabled={!hostId} onClose={onClose}
      onConfirm={async () => {
        const r = await post<Accepted>(`/api/v1/clusters/${clusterId}/shared-volumes/move`, { volumeName: volume.name, destinationHostId: hostId, idempotencyKey: key });
        ops.queued(r, `${t('jobop.cluster.storage.MoveCsv')} — ${volume.path || volume.name}`);
        onQueued();
      }}>
      <p className="text-sm text-muted-foreground">{t('cl.csv.moveHelp')}</p>
      <div className="rounded-md bg-muted/50 px-3 py-2 text-sm">{t('cl.csv.owner')}: <span className="font-medium">{volume.ownerNode ?? '—'}</span></div>
      {targets.length === 0 ? <Notice kind="warn">{t('cl.csv.noTarget')}</Notice> : (
        <Field label={t('cl.csv.moveTo')} htmlFor="csv-node">
          <NativeSelect id="csv-node" value={hostId} onChange={(e) => setHostId(e.target.value)}>
            {targets.map((n) => <option key={n.hostId} value={n.hostId!}>{n.name}</option>)}
          </NativeSelect>
        </Field>
      )}
    </ActionDialog>
  );
}

/** Redundancy of the data of a volume (the copies of its "objects") from what Storage Spaces reports. */
function redundancy(health: string | null, status: string | null): { key: TKey; tone: 'ok' | 'warn' | 'danger' | 'muted' } {
  const st = (status ?? '').toLowerCase();
  if (health === 'Healthy' && (st === 'ok' || st === '')) return { key: 'cl.s2d.inSync', tone: 'ok' };
  if (st.includes('in service')) return { key: 'cl.s2d.repairing', tone: 'warn' };
  if (st.includes('degraded') || st.includes('incomplete')) return { key: 'cl.s2d.reduced', tone: 'warn' };
  if (health === 'Unhealthy' || st.includes('detached') || st.includes('lost') || st.includes('no redundancy')) return { key: 'cl.s2d.unavailable', tone: 'danger' };
  return { key: 'cl.s2d.unknownRedundancy', tone: 'muted' };
}
