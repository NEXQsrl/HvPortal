import { CircleCheck, LayoutDashboard, ListChecks, Monitor, Plus, Server, TriangleAlert } from 'lucide-react';
import { useCallback } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { get } from '@/api/client';
import type { Job } from '@/api/types';
import { rowLink } from '@/components/actions';
import { Empty, ErrorAlert, HostIcon, Loading, Meter, PageHeader, Section, StatCard, VmIcon } from '@/components/common';
import { OutdatedAgentsNotice } from '@/components/infra/AgentUpdates';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useOps } from '@/components/ops/OperationsProvider';
import { JobTable } from '@/components/tables';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { formatBytes, percent } from '@/lib/format';
import { isActive } from '@/lib/jobs';
import { useLive, useLoad } from '@/lib/hooks';
import { useSession } from '@/session';

export function DashboardPage() {
  const { t } = useI18n();
  const { can } = useSession();
  const inv = useInventory();
  const ops = useOps();
  const navigate = useNavigate();
  const jobs = useLoad(() => get<{ job: Job; requestedBy: string | null }[]>('/api/v1/jobs?take=10'));
  const reloadJobs = useCallback(() => jobs.reload(), [jobs.reload]);
  useLive(reloadJobs, ['jobChanged'], 500);

  if (inv.loading) return <Loading />;
  const h = inv.hosts;
  const v = inv.vms;
  const active = (jobs.data ?? []).filter((j) => isActive(j.job.status)).length;
  const attention = [
    ...h.filter((x) => x.status === 'Active' && !x.connected).map((x) => ({ key: 'h' + x.id, to: `/hosts/${x.id}`, icon: <HostIcon connected={false} />, name: x.displayName, why: t('dash.offlineHost') })),
    ...h.filter((x) => x.connected && x.access && x.access !== 'Ok').map((x) => ({ key: 'a' + x.id, to: `/hosts/${x.id}`, icon: <HostIcon connected access={x.access} />, name: x.displayName, why: t('dash.accessIssue', { a: x.access ?? '' }) })),
    ...h.filter((x) => x.connected && x.stale).map((x) => ({ key: 's' + x.id, to: `/hosts/${x.id}`, icon: <HostIcon connected access={x.access} />, name: x.displayName, why: t('dash.staleHost') })),
    ...v.filter((x) => x.isCritical).map((x) => ({ key: 'c' + x.id, to: `/vms/${x.id}`, icon: <VmIcon state={x.powerState} critical />, name: x.name, why: t('dash.criticalVm') })),
    ...v.filter((x) => x.isMissing).map((x) => ({ key: 'm' + x.id, to: `/vms/${x.id}`, icon: <VmIcon state={x.powerState} />, name: x.name, why: t('dash.missingVm') })),
  ];

  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<LayoutDashboard className="size-5" />} title={t('nav.dashboard')}
        actions={<>
          {can('hosts.manage') && <Button size="sm" variant="outline" onClick={() => ops.openAddHost()}><Server />{t('host.add')}</Button>}
          {h.some((x) => x.canCreateVm) && <Button size="sm" onClick={() => ops.openCreateVm()}><Plus />{t('wiz.title')}</Button>}
        </>} />
      <div className="min-h-0 flex-1 space-y-3 overflow-auto p-4">
        <ErrorAlert error={inv.error} />
        <OutdatedAgentsNotice />
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <StatCard icon={<Server className="size-3.5" />} label={`${t('dash.hosts')} ${t('dash.connected')}`} value={`${h.filter((x) => x.connected).length}/${h.length}`} />
          <StatCard icon={<Monitor className="size-3.5" />} label={`${t('dash.vms')} ${t('dash.running')}`} value={`${v.filter((x) => x.powerState === 'Running').length}/${v.length}`} />
          <StatCard icon={<ListChecks className="size-3.5" />} label={t('dash.jobs')} value={active} />
          <StatCard icon={<TriangleAlert className="size-3.5" />} label={t('dash.attention')} value={attention.length} className={attention.length > 0 ? 'border-warning/50' : undefined} />
        </div>
        {h.length === 0 && (
          <Section bodyClassName="p-4">
            <Empty icon={<Server className="size-6" />}>
              <p>{t('dash.noHosts')}</p>
              {can('hosts.manage') && <Button size="sm" onClick={() => ops.openAddHost()}><Plus />{t('dash.addHost')}</Button>}
            </Empty>
          </Section>
        )}
        <div className="grid gap-3 xl:grid-cols-[2fr_1fr]">
          {h.length > 0 && (
            <Section title={t('dash.hostHealth')}>
              <Table>
                <TableHeader><TableRow><TableHead>{t('host.name')}</TableHead><TableHead className="w-40">CPU</TableHead><TableHead className="w-48">{t('vm.memory')}</TableHead><TableHead className="text-right">VM</TableHead></TableRow></TableHeader>
                <TableBody>
                  {h.map((x) => {
                    const hw = x.hardware;
                    const used = hw?.totalPhysicalMemoryBytes != null && hw.freePhysicalMemoryBytes != null ? hw.totalPhysicalMemoryBytes - hw.freePhysicalMemoryBytes : null;
                    return (
                      <TableRow key={x.id} {...rowLink(() => navigate(`/hosts/${x.id}`))}>
                        <TableCell><div className="flex items-center gap-2"><HostIcon connected={x.connected} access={x.access} /><span className="font-medium">{x.displayName}</span></div></TableCell>
                        <TableCell><div className="flex items-center gap-2"><Meter value={hw?.cpuLoadPercent} className="w-20" /><span className="text-xs tabular">{hw?.cpuLoadPercent != null ? `${hw.cpuLoadPercent}%` : '—'}</span></div></TableCell>
                        <TableCell><div className="flex items-center gap-2"><Meter value={percent(used, hw?.totalPhysicalMemoryBytes)} className="w-20" /><span className="text-xs whitespace-nowrap tabular">{formatBytes(used)} / {formatBytes(hw?.totalPhysicalMemoryBytes)}</span></div></TableCell>
                        <TableCell className="text-right tabular">{v.filter((vm) => vm.hostId === x.id && vm.powerState === 'Running').length}/{x.vmCount}</TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
            </Section>
          )}
          <Section title={t('dash.attentionList')}>
            {attention.length === 0 ? <Empty icon={<CircleCheck className="size-6 text-success" />}>{t('dash.allGood')}</Empty> : (
              <ul className="divide-y">
                {attention.slice(0, 12).map((a) => (
                  <li key={a.key}><Link to={a.to} className="flex items-center gap-2 px-3 py-2 text-sm hover:bg-accent/50">{a.icon}<span className="font-medium">{a.name}</span><span className="ml-auto text-xs text-muted-foreground">{a.why}</span></Link></li>
                ))}
              </ul>
            )}
          </Section>
        </div>
        <Section title={t('dash.recentJobs')} actions={<Button variant="link" size="xs" asChild><Link to="/jobs">{t('tasks.viewAll')}</Link></Button>}>
          <JobTable rows={jobs.data ?? []} compact className="max-h-96" />
        </Section>
      </div>
    </div>
  );
}
