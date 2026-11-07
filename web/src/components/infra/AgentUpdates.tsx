import { CircleCheck, Download, RefreshCw, ShieldAlert, ShieldCheck } from 'lucide-react';
import { useCallback, useState } from 'react';
import { Link } from 'react-router-dom';
import { get, post } from '@/api/client';
import type { AgentUpdateState, AgentUpdatesResponse } from '@/api/infra-types';
import type { Accepted } from '@/api/ops-types';
import { ErrorAlert, HostIcon, Loading, Notice, Section, StatusBadge } from '@/components/common';
import { ActionDialog } from '@/components/dialogs';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n, type TKey } from '@/i18n';
import { shortVersion } from '@/lib/format';
import { useLive, useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';

// One request shared by the components of a page (indicator in the top bar, dashboard, guide).
let shared: { at: number; promise: Promise<AgentUpdatesResponse> } | null = null;

function loadUpdates(force = false) {
  if (force || !shared || Date.now() - shared.at > 15_000) {
    const promise = get<AgentUpdatesResponse>('/api/v1/agents/updates');
    shared = { at: Date.now(), promise };
    promise.catch(() => { shared = null; });
  }
  return shared.promise;
}

/**
 * Agents older than the package of this gateway and whether each can be updated from the portal. Only for holders of
 * hosts.manage (the endpoint needs it; calling it without would only record a denial).
 */
export function useAgentUpdates(enabled = true) {
  const [force, setForce] = useState(0);
  const data = useLoad(() => (enabled ? loadUpdates(force > 0) : Promise.resolve(null)), [enabled, force]);
  const reload = useCallback(() => setForce((x) => x + 1), []);
  useLive(reload, ['inventoryChanged', 'jobChanged'], 3000);
  return { ...data, reload };
}

/** Last outcome written by the updater on the host: "<time> <status> <version>: <message>". */
function LastResult({ text }: { text: string | null }) {
  const { t, fmtDate } = useI18n();
  if (!text) return <span className="text-muted-foreground">—</span>;
  const m = /^(\S+) (\S+) (\S*): ?(.*)$/s.exec(text);
  if (!m) return <span className="text-xs [overflow-wrap:anywhere]">{text}</span>;
  const [, time, status, version, message] = m;
  // Update-HvPortalAgent.ps1 writes "ok" or "failed".
  const ok = status === 'ok';
  return (
    <span className="text-xs">
      <StatusBadge tone={ok ? 'ok' : 'danger'}>{ok ? t('upd.lastOk', { v: version ?? '' }) : t('upd.lastFailed')}</StatusBadge>
      <span className="ml-1 text-muted-foreground">{fmtDate(time ?? null)}</span>
      {!ok && message && <span className="block [overflow-wrap:anywhere] text-muted-foreground">{message}</span>}
    </span>
  );
}

function StateBadge({ a }: { a: AgentUpdateState }) {
  const { t } = useI18n();
  if (a.runningJobId) return <StatusBadge tone="info"><RefreshCw className="animate-spin" />{t('upd.running')}</StatusBadge>;
  if (!a.version) return <StatusBadge tone="muted">{t('guide.update.unknown')}</StatusBadge>;
  if (!a.outdated) return <StatusBadge tone="ok"><CircleCheck />{t('guide.update.upToDate')}</StatusBadge>;
  return <StatusBadge tone="warn">{t('guide.update.outdated')}</StatusBadge>;
}

/**
 * Table of every agent (hosts with their own agent and management agents of remote hosts) with the version, whether
 * it can be updated from the portal (and why not), the last update outcome and an Update button; "Update all" queues
 * one job per agent that can be updated.
 */
export function AgentUpdatesPanel() {
  const { t } = useI18n();
  const ops = useOps();
  const data = useAgentUpdates();
  const [confirmAll, setConfirmAll] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const r = data.data;
  const updatable = (r?.agents ?? []).filter((a) => a.canUpdate && !a.runningJobId);

  const updateOne = async (a: AgentUpdateState) => {
    setBusy(a.id);
    setError(null);
    try {
      const res = await post<Accepted>(a.kind === 'host' ? `/api/v1/hosts/${a.id}/agent/update` : `/api/v1/management-agents/${a.id}/update`);
      ops.queued(res, `${t(a.kind === 'host' ? 'jobop.agent.update.Host' : 'jobop.agent.update.ManagementAgent')} — ${a.name}`);
      data.reload();
    } catch (e) { setError(e); } finally { setBusy(null); }
  };

  return (
    <Section title={t('upd.title')} actions={
      <span className="flex flex-wrap items-center gap-2">
        {r && <span className="text-xs text-muted-foreground">{t('guide.update.gateway', { v: r.gatewayVersion })}{r.package?.version ? ` · ${t('guide.update.package', { v: r.package.version })}` : ''}</span>}
        <Button size="xs" variant="ghost" onClick={data.reload} disabled={data.loading} aria-label={t('common.refresh')}><RefreshCw className={cn(data.loading && 'animate-spin')} /></Button>
        <Button size="xs" disabled={updatable.length === 0} onClick={() => setConfirmAll(true)}><Download />{t('upd.updateAll', { n: updatable.length })}</Button>
      </span>
    }>
      <ErrorAlert error={data.error ?? error} className="mx-3 mt-2" />
      {r && !r.package && <div className="p-3"><Notice kind="warn">{t('guide.s2.missing')}</Notice></div>}
      {r?.package && (
        <div className="flex flex-wrap items-center gap-2 border-b px-3 py-2 text-xs">
          {r.package.catalogSigned
            ? <StatusBadge tone="ok" title={r.package.signer ?? undefined}><ShieldCheck />{t('upd.catalogSigned')}</StatusBadge>
            : <StatusBadge tone="warn"><ShieldAlert />{t('upd.catalogMissing')}</StatusBadge>}
          <span className="text-muted-foreground">{r.package.catalogSigned ? t('upd.catalogSignedHelp') : t('upd.catalogMissingHelp')}</span>
        </div>
      )}
      {data.loading && !r ? <Loading /> : r && (
        <Table>
          <TableHeader><TableRow>
            <TableHead>{t('host.name')}</TableHead><TableHead>{t('guide.update.installed')}</TableHead><TableHead>{t('common.status')}</TableHead>
            <TableHead>{t('upd.fromPortal')}</TableHead><TableHead>{t('upd.last')}</TableHead><TableHead className="w-28"><span className="sr-only">{t('vm.actions')}</span></TableHead>
          </TableRow></TableHeader>
          <TableBody>
            {r.agents.length === 0 ? <TableRow><TableCell colSpan={6} className="text-sm text-muted-foreground">{t('guide.update.noHosts')}</TableCell></TableRow> : r.agents.map((a) => (
              <TableRow key={a.kind + a.id}>
                <TableCell>
                  {a.kind === 'host'
                    ? <Link to={`/hosts/${a.id}`} className="flex items-center gap-2 font-medium hover:underline"><HostIcon connected={a.connected} />{a.name}</Link>
                    : <span className="flex items-center gap-2 font-medium"><HostIcon connected={a.connected} />{a.name}</span>}
                  <span className="text-xs text-muted-foreground">{t(a.kind === 'host' ? 'upd.kindHost' : 'upd.kindManagement')}</span>
                </TableCell>
                <TableCell className="font-mono text-xs">{shortVersion(a.version)}</TableCell>
                <TableCell><StateBadge a={a} /></TableCell>
                <TableCell className="max-w-72 text-xs">
                  {!a.outdated ? <span className="text-muted-foreground">—</span>
                    : a.canUpdate ? <span className="text-success">{t('upd.canUpdate')}</span>
                      : <span className="text-muted-foreground">{t(('upd.reason.' + (a.reason ?? 'other')) as TKey)}</span>}
                </TableCell>
                <TableCell className="max-w-72"><LastResult text={a.lastUpdateResult} /></TableCell>
                <TableCell className="text-right">
                  {a.runningJobId ? <Button size="xs" variant="outline" asChild><Link to={`/jobs/${a.runningJobId}`}>{t('upd.viewJob')}</Link></Button>
                    : a.canUpdate && <Button size="xs" variant="outline" disabled={busy === a.id} onClick={() => void updateOne(a)}><Download />{t('upd.update')}</Button>}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {confirmAll && (
        <ActionDialog title={t('upd.confirmTitle', { n: updatable.length })} confirmLabel={t('upd.update')} onClose={() => setConfirmAll(false)}
          onConfirm={async () => {
            const res = await post<{ queued: { id: string; name: string; jobId: string; created: boolean }[]; skipped: { id: string; name: string; reason: string }[] }>('/api/v1/agents/update-outdated');
            for (const q of res.queued) ops.queued({ jobId: q.jobId, created: q.created }, `${t('upd.jobLabel')} — ${q.name}`);
            setConfirmAll(false);
            data.reload();
          }}>
          <p className="text-sm">{updatable.map((a) => a.name).join(', ')}</p>
          <p className="text-sm text-muted-foreground">{t('upd.confirmHelp')}</p>
        </ActionDialog>
      )}
    </Section>
  );
}

/** Top-bar notice for administrators: how many agents are older than the package of this gateway. */
export function AgentUpdatesIndicator() {
  const { t } = useI18n();
  const { can } = useSession();
  const admin = can('hosts.manage');
  const data = useAgentUpdates(admin);
  const n = data.data?.outdated ?? 0;
  if (!admin || n === 0) return null;
  return (
    <Link to="/guide/update" className="hidden items-center gap-1.5 rounded-md border border-warning/50 bg-warning/10 px-2 py-1 text-xs font-medium text-foreground hover:bg-warning/20 sm:inline-flex"
      title={t('upd.indicatorHelp', { n })}>
      <RefreshCw className="size-3.5" /><span className="hidden md:inline">{t('upd.indicator', { n })}</span><span className="md:hidden">{n}</span>
    </Link>
  );
}

/** Dashboard notice with the outdated agents and a link to the update page. */
export function OutdatedAgentsNotice() {
  const { t } = useI18n();
  const { can } = useSession();
  const admin = can('hosts.manage');
  const data = useAgentUpdates(admin);
  const r = data.data;
  if (!admin || !r || r.outdated === 0) return null;
  const outdated = r.agents.filter((a) => a.outdated);
  const portal = outdated.filter((a) => a.canUpdate).length;
  return (
    <Notice kind="warn" title={t('upd.noticeTitle', { n: r.outdated, v: r.package?.version ?? '' })}>
      <span>{outdated.map((a) => `${a.name} (${shortVersion(a.version)})`).join(', ')}. </span>
      <span>{portal > 0 ? t('upd.noticePortal', { n: portal }) : t('upd.noticeManual')} </span>
      <Link to="/guide/update" className="font-medium text-primary hover:underline">{t('upd.open')}</Link>
    </Notice>
  );
}
