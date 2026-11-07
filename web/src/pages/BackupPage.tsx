import { Archive, CirclePlay, Database, FolderSearch, HardDrive, History, ListChecks, Pencil, Plus, RefreshCw, ScanSearch, ShieldCheck, Trash2 } from 'lucide-react';
import { useCallback, useMemo, useState, type ReactNode } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { del, get, post } from '@/api/client';
import type { Vm } from '@/api/types';
import { rowLink } from '@/components/actions';
import { BackupNowDialog } from '@/components/backup/BackupNowDialog';
import { CheckStatusBadge, RunStatusBadge, SpaceText, repositorySpace, scheduleText, triggerText, useBackupAccess, worstCheck, PointStatusBadge } from '@/components/backup/BackupCommon';
import { MountBrowseDialog, MountsList } from '@/components/backup/FileRestore';
import { JobDialog } from '@/components/backup/JobDialog';
import { PointsTable, usePointActions } from '@/components/backup/PointActions';
import { CheckRepositoryDialog, DeleteRepositoryDialog, RepositoryDialog, RescanRepositoryDialog } from '@/components/backup/RepositoryDialogs';
import { MOUNT_IDLE_HOURS, POINT_STATUSES, RUN_STATUSES, type BackupJobDef, type BackupPoint, type BackupRun, type MountInfo, type Overview, type Repository } from '@/components/backup/backup-api';
import { Empty, ErrorAlert, KeyValue, Loading, Mono, Notice, PageHeader, Section, StatCard, StatusBadge, errorText } from '@/components/common';
import { ActionDialog, HelpTip } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { NativeSelect } from '@/components/ui/native-select';
import { Progress } from '@/components/ui/progress';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, formatDuration, percent } from '@/lib/format';
import { useLive, useLoad } from '@/lib/hooks';
import { toast } from '@/lib/toast';
import { cn } from '@/lib/utils';

const BACKUP_TABS = ['overview', 'jobs', 'repositories', 'runs', 'points', 'files'] as const;
type BackupTab = (typeof BACKUP_TABS)[number];

/** GET with live refresh: backups change when their tasks end. */
function useBackupLoad<T>(path: string | null, deps: unknown[] = []) {
  const data = useLoad(() => (path ? get<T>(path) : Promise.resolve(null as T | null)), [path, ...deps]);
  const reload = useCallback(() => data.reload(), [data.reload]);
  useLive(reload, ['jobChanged'], 1000);
  return data;
}

/**
 * Backup area (/backup, ADR-031): overview, jobs, repositories, runs, restore points and open file restore sessions.
 * The tab and the filters are in the URL (?tab=, ?status=, ?job=, ?repository=, ?vm=; ?run= opens a run), so the tree
 * of the navigator and the links of notifications open them directly. Needs a backup permission at Global scope.
 */
export function BackupPage() {
  const { t } = useI18n();
  const access = useBackupAccess();
  const [params, setParams] = useSearchParams();
  const tab: BackupTab = (BACKUP_TABS as readonly string[]).includes(params.get('tab') ?? '') ? (params.get('tab') as BackupTab) : 'overview';
  const runId = params.get('run');
  const setParam = useCallback((changes: Record<string, string | null>) => {
    setParams((prev) => {
      const next = new URLSearchParams(prev);
      for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k); }
      return next;
    }, { replace: true });
  }, [setParams]);

  if (!access.see) {
    return (
      <div className="flex h-full min-h-0 flex-col">
        <PageHeader icon={<Archive className="size-5" />} title={t('bk.title')} />
        <div className="p-4"><Notice kind="info" title={t('bk.noAccessTitle')}>{t('bk.noAccess')}</Notice></div>
      </div>
    );
  }

  return (
    <Tabs value={tab} onValueChange={(v) => setParams(v === 'overview' ? {} : { tab: v }, { replace: true })} className="flex h-full min-h-0 flex-col gap-0">
      <PageHeader icon={<Archive className="size-5" />} title={<span className="flex items-center gap-2">{t('bk.title')}<HelpTip>{t('bk.help')}</HelpTip></span>} subtitle={t('bk.subtitle')}>
        <TabsList variant="line" className="page-tabs -mb-px h-9 w-full justify-start overflow-x-auto rounded-none bg-transparent p-0">
          {BACKUP_TABS.map((x) => <TabsTrigger key={x} value={x} className="flex-none px-3">{t(('bk.tab.' + x) as TKey)}</TabsTrigger>)}
        </TabsList>
      </PageHeader>
      <TabsContent value="overview" className="min-h-0 overflow-auto">{tab === 'overview' && <OverviewTab />}</TabsContent>
      <TabsContent value="jobs" className="min-h-0 overflow-auto">{tab === 'jobs' && <JobsTab />}</TabsContent>
      <TabsContent value="repositories" className="min-h-0 overflow-auto">{tab === 'repositories' && <RepositoriesTab />}</TabsContent>
      <TabsContent value="runs" className="min-h-0 overflow-auto">{tab === 'runs' && <RunsTab params={params} setParam={setParam} />}</TabsContent>
      <TabsContent value="points" className="min-h-0 overflow-auto">{tab === 'points' && <PointsTab params={params} setParam={setParam} />}</TabsContent>
      <TabsContent value="files" className="min-h-0 overflow-auto">{tab === 'files' && <FilesTab />}</TabsContent>
      {runId && <RunDetailDialog runId={runId} onClose={() => setParam({ run: null })} />}
    </Tabs>
  );
}

type SetParam = (changes: Record<string, string | null>) => void;

// ------------------------------------------------------------------ overview

function OverviewTab() {
  const { t, fmtDate, lang } = useI18n();
  const access = useBackupAccess();
  const inv = useInventory();
  const ops = useOps();
  const navigate = useNavigate();
  const overview = useBackupLoad<Overview>('/api/v1/backup/overview');
  const jobs = useBackupLoad<BackupJobDef[]>('/api/v1/backup/jobs');
  const repos = useBackupLoad<Repository[]>('/api/v1/backup/repositories');
  const [backup, setBackup] = useState<Vm | null>(null);
  const o = overview.data;
  if (overview.loading && !o) return <Loading />;
  if (!o) return <div className="p-4"><ErrorAlert error={overview.error} /></div>;
  const l = o.last24h;
  return (
    <div className="space-y-3 p-4">
      <ErrorAlert error={overview.error ?? jobs.error ?? repos.error} />
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard icon={<History className="size-3.5" />} label={t('bk.ov.last24h')} value={l.runs}
          sub={<>
            <div><span className="text-success">{t('bk.ov.success', { n: l.success })}</span> · <span className={cn(l.warning > 0 && 'text-[color-mix(in_oklch,var(--warning)_55%,var(--foreground))]')}>{t('bk.ov.warning', { n: l.warning })}</span></div>
            <div><span className={cn(l.failed > 0 && 'font-medium text-destructive')}>{t('bk.ov.failed', { n: l.failed })}</span>{l.running > 0 && <> · {t('bk.ov.running', { n: l.running })}</>}</div>
          </>} className={cn(l.failed > 0 && 'border-destructive/40')} />
        <StatCard icon={<ShieldCheck className="size-3.5" />} label={t('bk.ov.protected')} value={`${o.protectedVms} / ${o.vms}`} meter={percent(o.protectedVms, o.vms)}
          sub={o.unprotected.length > 0 ? <span className="text-[color-mix(in_oklch,var(--warning)_55%,var(--foreground))]">{t('bk.ov.unprotectedCount', { n: o.unprotected.length })}</span> : t('bk.ov.allProtected')} />
        <StatCard icon={<Database className="size-3.5" />} label={t('bk.ov.points')} value={o.points} sub={formatBytes(o.bytes)} />
        <StatCard icon={<ListChecks className="size-3.5" />} label={t('bk.ov.jobs')} value={`${o.enabledJobs} / ${o.jobs}`} sub={t('bk.ov.jobsSub', { n: o.repositories })} />
      </div>

      {o.repositories === 0 && (
        <Notice kind="info" title={t('bk.ov.startTitle')}>
          {t('bk.ov.startRepo')}{' '}{access.manage && <Link className="text-primary hover:underline" to="/backup?tab=repositories">{t('bk.repo.new')}</Link>}
        </Notice>
      )}
      {o.repositories > 0 && o.jobs === 0 && (
        <Notice kind="info" title={t('bk.ov.startTitle')}>
          {t('bk.ov.startJob')}{' '}{access.manage && <Link className="text-primary hover:underline" to="/backup?tab=jobs">{t('bk.job.new')}</Link>}
        </Notice>
      )}

      {o.problems.length > 0 && (
        <Section title={<span className="flex items-center gap-1.5">{t('bk.ov.problems')}<HelpTip>{t('bk.ov.problemsHelp')}</HelpTip></span>}>
          <Table>
            <TableBody>
              {o.problems.map((p) => (
                <TableRow key={p.id} className="align-top">
                  <TableCell className="font-medium">{inv.vmById.has(p.vmId) ? <Link className="text-primary hover:underline" to={`/vms/${p.vmId}?tab=backups`}>{p.vmName}</Link> : p.vmName}</TableCell>
                  <TableCell className="text-xs whitespace-nowrap tabular">{fmtDate(p.createdAt)}</TableCell>
                  <TableCell><PointStatusBadge status={p.status} /></TableCell>
                  <TableCell className="max-w-xl text-xs whitespace-normal text-muted-foreground">
                    {p.verifyOk === false ? `${t('bk.verify.failed')}: ${p.verifyDetail ?? ''}` : p.testPassed === false ? `${t('bk.test.failed')}: ${p.testDetail ?? ''}` : p.error ?? t(('bk.pointStatusHelp.' + p.status) as TKey)}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">{p.jobName ?? t('bk.point.adhoc')}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Section>
      )}

      {o.unprotected.length > 0 && (
        <Section title={<span className="flex items-center gap-1.5">{t('bk.ov.unprotected')}<HelpTip>{t('bk.ov.unprotectedHelp')}</HelpTip></span>}>
          <Table containerClassName="max-h-72">
            <TableBody>
              {o.unprotected.map((u) => {
                const vm = inv.vmById.get(u.id);
                return (
                  <TableRow key={u.id}>
                    <TableCell className="font-medium"><Link className="text-primary hover:underline" to={`/vms/${u.id}?tab=backups`}>{u.name}</Link></TableCell>
                    <TableCell className="text-xs text-muted-foreground">{inv.hostById.get(u.hostId)?.displayName ?? '—'}</TableCell>
                    <TableCell className="text-right">{vm?.canBackup && <Button size="xs" variant="outline" onClick={() => setBackup(vm)}><Archive />{t('bk.backupNow')}</Button>}</TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </Section>
      )}

      <div className="grid gap-3 xl:grid-cols-2">
        <Section title={t('bk.ov.jobsLast')} actions={<Button size="xs" variant="ghost" asChild><Link to="/backup?tab=jobs">{t('bk.ov.all')}</Link></Button>}>
          {!jobs.data ? <Loading /> : jobs.data.length === 0 ? <Empty>{t('bk.job.none')}</Empty> : (
            <Table>
              <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('bk.job.lastRun')}</TableHead><TableHead>{t('bk.job.nextRun')}</TableHead></TableRow></TableHeader>
              <TableBody>
                {jobs.data.map((j) => (
                  <TableRow key={j.id}>
                    <TableCell><div className="font-medium">{j.name}</div><div className="text-xs text-muted-foreground">{scheduleText(t, lang, j)}</div></TableCell>
                    <TableCell>{j.lastRun ? (
                      <button type="button" className="flex flex-col items-start gap-0.5 text-left" onClick={() => navigate(`/backup?run=${j.lastRun!.id}`)}>
                        <RunStatusBadge status={j.lastRun.status} /><span className="text-xs text-muted-foreground tabular">{fmtDate(j.lastRun.startedAt)}</span>
                      </button>
                    ) : <span className="text-xs text-muted-foreground">{t('common.never')}</span>}</TableCell>
                    <TableCell className="text-xs tabular">{!j.enabled ? <StatusBadge tone="muted">{t('bk.job.disabled')}</StatusBadge> : j.nextRunAt ? fmtDate(j.nextRunAt) : '—'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Section>
        <Section title={t('bk.tab.repositories')} actions={<Button size="xs" variant="ghost" asChild><Link to="/backup?tab=repositories">{t('bk.ov.all')}</Link></Button>}>
          {!repos.data ? <Loading /> : repos.data.length === 0 ? <Empty>{t('bk.repo.none')}</Empty> : (
            <Table>
              <TableHeader><TableRow><TableHead>{t('common.name')}</TableHead><TableHead>{t('bk.repo.space')}</TableHead><TableHead>{t('bk.repo.lastCheckCol')}</TableHead></TableRow></TableHeader>
              <TableBody>
                {repos.data.map((r) => {
                  const worst = r.lastCheck ? worstCheck(r.lastCheck.checks) : null;
                  return (
                    <TableRow key={r.id}>
                      <TableCell><div className="font-medium">{r.name}</div><Mono className="text-muted-foreground">{r.path}</Mono></TableCell>
                      <TableCell className="text-xs"><SpaceText space={repositorySpace(r, inv.hostById)} /><div className="text-muted-foreground">{t('bk.repo.used', { n: r.points, size: formatBytes(r.bytes) })}</div></TableCell>
                      <TableCell className="text-xs">{worst ? <CheckStatusBadge status={worst} /> : <StatusBadge tone="muted">{t('bk.repo.notChecked')}</StatusBadge>}</TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          )}
        </Section>
      </div>

      <Section title={t('bk.ov.recentRuns')} actions={<Button size="xs" variant="ghost" asChild><Link to="/backup?tab=runs">{t('bk.ov.all')}</Link></Button>}>
        <RunsTable runs={o.recentRuns} />
      </Section>
      {backup && <BackupNowDialog vm={backup} onClose={() => setBackup(null)} onQueued={(r, label) => { ops.queued(r, label); overview.reload(); }} />}
    </div>
  );
}

// ------------------------------------------------------------------ jobs

function JobsTab() {
  const { t, fmtDate, lang } = useI18n();
  const access = useBackupAccess();
  const navigate = useNavigate();
  const jobs = useBackupLoad<BackupJobDef[]>('/api/v1/backup/jobs');
  const repos = useBackupLoad<Repository[]>('/api/v1/backup/repositories');
  const [editing, setEditing] = useState<BackupJobDef | 'new' | null>(null);
  const [deleting, setDeleting] = useState<BackupJobDef | null>(null);
  const repoName = (id: string) => repos.data?.find((r) => r.id === id)?.name ?? '—';
  const canRun = access.manage || access.run;
  const runNow = async (j: BackupJobDef) => {
    try {
      const r = await post<{ runId: string; total: number }>(`/api/v1/backup/jobs/${j.id}/run`);
      toast.info(t('bk.job.started', { name: j.name, n: r.total }), <Link className="text-primary underline-offset-2 hover:underline" to={`/backup?tab=runs&run=${r.runId}`}>{t('bk.run.open')}</Link>);
      jobs.reload();
    } catch (e) { toast.error(t('common.error'), errorText(e)); }
  };
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b px-4 py-2">
        {access.manage && <Button size="sm" onClick={() => setEditing('new')} disabled={!repos.data || repos.data.length === 0}><Plus />{t('bk.job.new')}</Button>}
        <Button size="sm" variant="outline" onClick={jobs.reload}><RefreshCw className={cn(jobs.loading && 'animate-spin')} />{t('common.refresh')}</Button>
        {access.manage && repos.data && repos.data.length === 0 && <span className="text-xs text-muted-foreground">{t('bk.job.noRepos')}</span>}
      </div>
      <div className="px-4 empty:hidden"><ErrorAlert error={jobs.error} /></div>
      {jobs.loading && !jobs.data ? <Loading /> : (jobs.data ?? []).length === 0 ? <Empty icon={<ListChecks className="size-6" />}>{t('bk.job.none')}</Empty> : (
        <Table containerClassName="min-h-0 flex-1">
          <TableHeader><TableRow>
            <TableHead>{t('common.name')}</TableHead><TableHead>{t('bk.job.schedule')}</TableHead><TableHead className="hidden md:table-cell">{t('bk.point.repository')}</TableHead>
            <TableHead className="hidden lg:table-cell">{t('bk.job.protects')}</TableHead><TableHead className="hidden lg:table-cell">{t('bk.job.retention')}</TableHead>
            <TableHead>{t('bk.job.lastRun')}</TableHead><TableHead className="hidden md:table-cell">{t('bk.job.nextRun')}</TableHead><TableHead />
          </TableRow></TableHeader>
          <TableBody>
            {(jobs.data ?? []).map((j) => (
              <TableRow key={j.id} className="align-top">
                <TableCell className="max-w-xs whitespace-normal">
                  <div className="flex flex-wrap items-center gap-1.5"><span className="font-medium">{j.name}</span>{!j.enabled && <StatusBadge tone="muted">{t('bk.job.disabled')}</StatusBadge>}</div>
                  {j.description && <div className="text-xs text-muted-foreground">{j.description}</div>}
                  {j.runsAs && <div className="text-xs text-muted-foreground">{t('bk.job.runsAs', { user: j.runsAs })}</div>}
                </TableCell>
                <TableCell className="max-w-56 text-xs whitespace-normal">{scheduleText(t, lang, j)}</TableCell>
                <TableCell className="hidden text-xs md:table-cell">{repoName(j.repositoryId)}</TableCell>
                <TableCell className="hidden text-xs lg:table-cell">{t('bk.job.protectsText', { vms: j.vmIds.length, groups: j.groupIds.length })}</TableCell>
                <TableCell className="hidden text-xs lg:table-cell">
                  {t('bk.job.retentionText', { n: j.retentionPoints })}{j.retentionDays != null && <div className="text-muted-foreground">{t('bk.job.retentionDaysText', { n: j.retentionDays })}</div>}
                  {j.restoreTestDays > 0 && <div className="text-muted-foreground">{t('bk.job.restoreTestOn', { n: j.restoreTestDays })}</div>}
                </TableCell>
                <TableCell>{j.lastRun ? (
                  <button type="button" className="flex flex-col items-start gap-0.5 text-left" onClick={() => navigate(`/backup?tab=jobs&run=${j.lastRun!.id}`)}>
                    <RunStatusBadge status={j.lastRun.status} /><span className="text-xs text-muted-foreground tabular">{fmtDate(j.lastRun.startedAt)}</span>
                  </button>
                ) : <span className="text-xs text-muted-foreground">{t('common.never')}</span>}</TableCell>
                <TableCell className="hidden text-xs tabular md:table-cell">{j.enabled && j.nextRunAt ? fmtDate(j.nextRunAt) : '—'}</TableCell>
                <TableCell>
                  <div className="flex flex-wrap justify-end gap-1">
                    {canRun && <Button size="xs" onClick={() => void runNow(j)} disabled={j.lastRun?.status === 'Running'} title={j.lastRun?.status === 'Running' ? t('bk.job.alreadyRunning') : undefined}><CirclePlay />{t('bk.job.runNow')}</Button>}
                    <Button size="xs" variant="outline" asChild><Link to={`/backup?tab=runs&job=${j.id}`}><History />{t('bk.tab.runs')}</Link></Button>
                    <Button size="xs" variant="outline" asChild><Link to={`/backup?tab=points&job=${j.id}`}><Database />{t('bk.tab.points')}</Link></Button>
                    {access.manage && <Button size="xs" variant="outline" onClick={() => setEditing(j)}><Pencil />{t('common.edit')}</Button>}
                    {access.manage && <Button size="xs" variant="ghost" className="text-destructive" onClick={() => setDeleting(j)}><Trash2 />{t('common.delete')}</Button>}
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {editing && <JobDialog job={editing === 'new' ? null : editing} repositories={repos.data ?? []} onSaved={jobs.reload} onClose={() => setEditing(null)} />}
      {deleting && (
        <ActionDialog title={t('bk.job.deleteTitle', { name: deleting.name })} confirmLabel={t('common.delete')} danger onClose={() => setDeleting(null)}
          onConfirm={async () => { await del(`/api/v1/backup/jobs/${deleting.id}`); jobs.reload(); }}>
          <p className="text-sm">{t('bk.job.deleteHelp')}</p>
        </ActionDialog>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ repositories

function RepositoriesTab() {
  const { t, fmtDate } = useI18n();
  const access = useBackupAccess();
  const inv = useInventory();
  const repos = useBackupLoad<Repository[]>('/api/v1/backup/repositories');
  const [editing, setEditing] = useState<Repository | 'new' | null>(null);
  const [checking, setChecking] = useState<Repository | null>(null);
  const [rescanning, setRescanning] = useState<Repository | null>(null);
  const [deleting, setDeleting] = useState<Repository | null>(null);
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b px-4 py-2">
        {access.manage && <Button size="sm" onClick={() => setEditing('new')}><Plus />{t('bk.repo.new')}</Button>}
        <Button size="sm" variant="outline" onClick={repos.reload}><RefreshCw className={cn(repos.loading && 'animate-spin')} />{t('common.refresh')}</Button>
      </div>
      <div className="px-4 empty:hidden"><ErrorAlert error={repos.error} /></div>
      {repos.loading && !repos.data ? <Loading /> : (repos.data ?? []).length === 0 ? (
        <Empty icon={<HardDrive className="size-6" />}>{t('bk.repo.none')}<span className="max-w-xl text-xs">{t('bk.repo.noneHelp')}</span></Empty>
      ) : (
        <Table containerClassName="min-h-0 flex-1">
          <TableHeader><TableRow>
            <TableHead>{t('common.name')}</TableHead><TableHead>{t('bk.repo.path')}</TableHead><TableHead className="hidden md:table-cell">{t('bk.tab.points')}</TableHead>
            <TableHead className="hidden md:table-cell">{t('bk.repo.space')}</TableHead><TableHead>{t('bk.repo.lastCheckCol')}</TableHead><TableHead />
          </TableRow></TableHeader>
          <TableBody>
            {(repos.data ?? []).map((r) => {
              const worst = r.lastCheck ? worstCheck(r.lastCheck.checks) : null;
              return (
                <TableRow key={r.id} className="align-top">
                  <TableCell className="max-w-xs whitespace-normal"><div className="font-medium">{r.name}</div>{r.description && <div className="text-xs text-muted-foreground">{r.description}</div>}</TableCell>
                  <TableCell className="max-w-sm whitespace-normal">
                    <Mono className="break-all">{r.path}</Mono>
                    <div className="mt-0.5 flex flex-wrap gap-1">
                      <StatusBadge tone="muted">{r.isShare ? t('bk.repo.kind.share') : t('bk.repo.localOf', { host: r.hostName ?? '—' })}</StatusBadge>
                      {r.hasCredentials && <StatusBadge tone="info" title={r.userName ?? undefined}>{t('bk.repo.withCreds')}</StatusBadge>}
                    </div>
                  </TableCell>
                  <TableCell className="hidden text-xs md:table-cell">
                    <Link className="text-primary hover:underline" to={`/backup?tab=points&repository=${r.id}`}>{t('bk.repo.used', { n: r.points, size: formatBytes(r.bytes) })}</Link>
                  </TableCell>
                  <TableCell className="hidden text-xs md:table-cell"><SpaceText space={repositorySpace(r, inv.hostById)} /></TableCell>
                  <TableCell className="text-xs">
                    {worst ? <CheckStatusBadge status={worst} /> : <StatusBadge tone="muted">{t('bk.repo.notChecked')}</StatusBadge>}
                    {r.lastCheckedAt && <div className="mt-0.5 text-muted-foreground tabular">{fmtDate(r.lastCheckedAt)}{r.lastCheck?.host ? ` · ${r.lastCheck.host}` : ''}</div>}
                    {r.lastCheck && worst !== 'Ok' && (
                      <div className="mt-0.5 line-clamp-2 max-w-64 break-words text-muted-foreground" title={r.lastCheck.checks.filter((c) => c.status !== 'Ok').map((c) => c.message).join('\n')}>
                        {r.lastCheck.checks.find((c) => c.status === 'Critical')?.message ?? r.lastCheck.checks.find((c) => c.status !== 'Ok')?.message}
                      </div>
                    )}
                  </TableCell>
                  <TableCell>
                    {access.manage && (
                      <div className="flex flex-wrap justify-end gap-1">
                        <Button size="xs" variant="outline" onClick={() => setChecking(r)}><ShieldCheck />{t('bk.repo.check')}</Button>
                        <Button size="xs" variant="outline" onClick={() => setRescanning(r)}><ScanSearch />{t('bk.repo.rescan')}</Button>
                        <Button size="xs" variant="outline" onClick={() => setEditing(r)}><Pencil />{t('common.edit')}</Button>
                        <Button size="xs" variant="ghost" className="text-destructive" onClick={() => setDeleting(r)}><Trash2 />{t('common.delete')}</Button>
                      </div>
                    )}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      )}
      <div className="p-4"><Notice kind="info" title={t('bk.repo.aboutTitle')}>{t('bk.repo.about')}</Notice></div>
      {editing && <RepositoryDialog repo={editing === 'new' ? null : editing} onSaved={repos.reload} onClose={() => setEditing(null)} />}
      {checking && <CheckRepositoryDialog repo={checking} onChecked={repos.reload} onClose={() => setChecking(null)} />}
      {rescanning && <RescanRepositoryDialog repo={rescanning} onDone={repos.reload} onClose={() => setRescanning(null)} />}
      {deleting && <DeleteRepositoryDialog repo={deleting} onDone={repos.reload} onClose={() => setDeleting(null)} />}
    </div>
  );
}

// ------------------------------------------------------------------ runs

function RunsTable({ runs, onOpen }: { runs: BackupRun[]; onOpen?: (r: BackupRun) => void }) {
  const { t, fmtDate } = useI18n();
  const [, setParams] = useSearchParams();
  const open = onOpen ?? ((r: BackupRun) => setParams((prev) => { const n = new URLSearchParams(prev); n.set('run', r.id); return n; }, { replace: true }));
  if (runs.length === 0) return <Empty>{t('bk.run.none')}</Empty>;
  return (
    <Table>
      <TableHeader><TableRow>
        <TableHead>{t('bk.run.started')}</TableHead><TableHead>{t('bk.run.job')}</TableHead><TableHead className="hidden md:table-cell">{t('bk.run.trigger')}</TableHead>
        <TableHead>{t('common.status')}</TableHead><TableHead>{t('bk.run.vms')}</TableHead><TableHead className="hidden md:table-cell">{t('bk.run.duration')}</TableHead>
        <TableHead className="hidden lg:table-cell">{t('bk.run.message')}</TableHead>
      </TableRow></TableHeader>
      <TableBody>
        {runs.map((r) => (
          <TableRow key={r.id} {...rowLink(() => open(r))} className="cursor-pointer">
            <TableCell className="text-xs whitespace-nowrap tabular">{fmtDate(r.startedAt)}</TableCell>
            <TableCell className="max-w-48 truncate">{r.jobName ?? (r.backupJobId ? t('bk.point.jobGone') : t('bk.point.adhoc'))}</TableCell>
            <TableCell className="hidden text-xs md:table-cell">{triggerText(t, r.trigger)}</TableCell>
            <TableCell><RunStatusBadge status={r.status} /></TableCell>
            <TableCell className="text-xs whitespace-nowrap tabular">
              {r.status === 'Running' ? t('bk.run.vmsRunning', { n: r.total }) : t('bk.run.vmsText', { ok: r.succeeded, warn: r.warnings, failed: r.failed, total: r.total })}
            </TableCell>
            <TableCell className="hidden text-xs whitespace-nowrap tabular md:table-cell">{r.completedAt ? formatDuration(new Date(r.completedAt).getTime() - new Date(r.startedAt).getTime()) : '—'}</TableCell>
            <TableCell className="hidden max-w-md truncate text-xs text-muted-foreground lg:table-cell" title={r.message ?? ''}>{r.message}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

function RunsTab({ params, setParam }: { params: URLSearchParams; setParam: SetParam }) {
  const { t } = useI18n();
  const status = (RUN_STATUSES as string[]).includes(params.get('status') ?? '') ? params.get('status')! : '';
  const jobId = params.get('job') ?? '';
  const runs = useBackupLoad<BackupRun[]>(`/api/v1/backup/runs?limit=500${jobId ? `&jobId=${encodeURIComponent(jobId)}` : ''}`);
  const jobs = useBackupLoad<BackupJobDef[]>('/api/v1/backup/jobs');
  const rows = (runs.data ?? []).filter((r) => !status || r.status === status);
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b px-4 py-2">
        <NativeSelect aria-label={t('common.status')} value={status} onChange={(e) => setParam({ status: e.target.value })} className="w-40">
          <option value="">{t('bk.filter.allStatuses')}</option>
          {RUN_STATUSES.map((s) => <option key={s} value={s}>{t(('bk.runStatus.' + s) as TKey)}</option>)}
        </NativeSelect>
        <NativeSelect aria-label={t('bk.run.job')} value={jobId} onChange={(e) => setParam({ job: e.target.value })} className="w-52">
          <option value="">{t('bk.filter.allJobs')}</option>
          {(jobs.data ?? []).map((j) => <option key={j.id} value={j.id}>{j.name}</option>)}
        </NativeSelect>
        <Button size="sm" variant="outline" onClick={runs.reload}><RefreshCw className={cn(runs.loading && 'animate-spin')} />{t('common.refresh')}</Button>
        <span className="text-xs text-muted-foreground">{t('bk.filter.count', { n: rows.length })}</span>
      </div>
      <div className="px-4 empty:hidden"><ErrorAlert error={runs.error} /></div>
      {runs.loading && !runs.data ? <Loading /> : <div className="min-h-0 flex-1 overflow-auto"><RunsTable runs={rows} /></div>}
    </div>
  );
}

/** One run (GET /backup/runs/{id}): its counts and one line per VM with the restore point it produced. */
function RunDetailDialog({ runId, onClose }: { runId: string; onClose: () => void }) {
  const { t, fmtDate } = useI18n();
  const data = useBackupLoad<{ run: BackupRun; points: BackupPoint[] }>(`/api/v1/backup/runs/${runId}`);
  const run = data.data?.run;
  const points = data.data?.points ?? [];
  const done = points.filter((p) => p.status !== 'Creating').length;
  return (
    <Dialog open onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="flex max-h-[calc(100dvh-2rem)] flex-col overflow-hidden sm:max-w-4xl">
        <DialogHeader className="shrink-0 pr-6">
          <DialogTitle>{run ? `${run.jobName ?? t('bk.point.adhoc')} — ${fmtDate(run.startedAt)}` : t('bk.run.title')}</DialogTitle>
          <DialogDescription>{t('bk.run.subtitle')}</DialogDescription>
        </DialogHeader>
        <div className="-mx-4 min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-0.5">
          <ErrorAlert error={data.error} />
          {data.loading && !run ? <Loading /> : run && (
            <>
              <KeyValue items={[
                [t('common.status'), <RunStatusBadge key="s" status={run.status} />],
                [t('bk.run.trigger'), triggerText(t, run.trigger)],
                [t('bk.run.started'), fmtDate(run.startedAt)],
                [t('bk.run.completed'), fmtDate(run.completedAt)],
                [t('bk.run.duration'), run.completedAt ? formatDuration(new Date(run.completedAt).getTime() - new Date(run.startedAt).getTime()) : '—'],
                [t('bk.run.vms'), run.status === 'Running' ? t('bk.run.progress', { done, total: run.total }) : t('bk.run.vmsText', { ok: run.succeeded, warn: run.warnings, failed: run.failed, total: run.total })],
              ]} />
              {run.status === 'Running' && run.total > 0 && <Progress value={(done / run.total) * 100} className="h-2" />}
              {run.message && <Notice kind={run.status === 'Failed' ? 'danger' : run.status === 'Warning' ? 'warn' : 'info'}>{run.message}</Notice>}
              {points.length === 0 ? <Empty>{t('bk.run.noPoints')}</Empty> : (
                <Table containerClassName="rounded-md border">
                  <TableHeader><TableRow>
                    <TableHead>{t('bk.point.vm')}</TableHead><TableHead>{t('common.status')}</TableHead><TableHead className="text-right">{t('common.size')}</TableHead>
                    <TableHead>{t('bk.run.detail')}</TableHead><TableHead />
                  </TableRow></TableHeader>
                  <TableBody>
                    {points.map((p) => (
                      <TableRow key={p.id} className="align-top">
                        <TableCell className="font-medium">{p.vmExists ? <Link className="text-primary hover:underline" to={`/vms/${p.vmId}?tab=backups`} onClick={onClose}>{p.vmName}</Link> : p.vmName}</TableCell>
                        <TableCell><PointStatusBadge status={p.status} /></TableCell>
                        <TableCell className="text-right text-xs whitespace-nowrap tabular">{p.sizeBytes > 0 ? formatBytes(p.sizeBytes) : '—'}</TableCell>
                        <TableCell className="max-w-md text-xs break-words whitespace-normal text-muted-foreground">{p.error ?? (p.status === 'Ok' ? p.verifyDetail : t(('bk.pointStatusHelp.' + p.status) as TKey))}</TableCell>
                        <TableCell className="text-right">{p.jobId && <Link className="text-xs text-primary hover:underline" to={`/jobs/${p.jobId}`} onClick={onClose}>{t('bk.point.task')}</Link>}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </>
          )}
        </div>
        <DialogFooter className="shrink-0"><Button type="button" variant="outline" onClick={onClose}>{t('common.close')}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ------------------------------------------------------------------ restore points

function PointsTab({ params, setParam }: { params: URLSearchParams; setParam: SetParam }) {
  const { t } = useI18n();
  const access = useBackupAccess();
  const status = (POINT_STATUSES as string[]).includes(params.get('status') ?? '') ? params.get('status')! : '';
  const vmId = params.get('vm') ?? '';
  const jobId = params.get('job') ?? '';
  const repositoryId = params.get('repository') ?? '';
  const q = new URLSearchParams();
  if (vmId) q.set('vmId', vmId);
  if (jobId) q.set('jobId', jobId);
  if (repositoryId) q.set('repositoryId', repositoryId);
  const points = useBackupLoad<BackupPoint[]>(`/api/v1/backup/points${q.toString() ? '?' + q.toString() : ''}`);
  const jobs = useBackupLoad<BackupJobDef[]>('/api/v1/backup/jobs');
  const repos = useBackupLoad<Repository[]>('/api/v1/backup/repositories');
  const mounts = useLoad(() => get<MountInfo[]>('/api/v1/backup/mounts'), []);
  const [browsing, setBrowsing] = useState<MountInfo | null>(null);
  const reload = useCallback(() => { points.reload(); mounts.reload(); }, [points.reload, mounts.reload]);
  const pa = usePointActions({ can: access, repositories: repos.data ?? undefined, onChanged: reload });
  const rows = (points.data ?? []).filter((p) => !status || p.status === status);
  // VMs of the list (also deleted ones, kept by their restore points), for the VM filter.
  const vmOptions = useMemo(() => {
    const m = new Map<string, string>();
    for (const p of points.data ?? []) m.set(p.vmId, p.vmName);
    if (vmId && !m.has(vmId)) m.set(vmId, vmId);
    return [...m.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [points.data, vmId]);
  const total = rows.filter((p) => p.status === 'Ok' || p.status === 'Unverified').reduce((s, p) => s + p.sizeBytes, 0);
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b px-4 py-2">
        <NativeSelect aria-label={t('bk.point.vm')} value={vmId} onChange={(e) => setParam({ vm: e.target.value })} className="w-48">
          <option value="">{t('bk.filter.allVms')}</option>
          {vmOptions.map(([id, name]) => <option key={id} value={id}>{name}</option>)}
        </NativeSelect>
        <NativeSelect aria-label={t('bk.run.job')} value={jobId} onChange={(e) => setParam({ job: e.target.value })} className="w-44">
          <option value="">{t('bk.filter.allJobs')}</option>
          {(jobs.data ?? []).map((j) => <option key={j.id} value={j.id}>{j.name}</option>)}
        </NativeSelect>
        <NativeSelect aria-label={t('bk.point.repository')} value={repositoryId} onChange={(e) => setParam({ repository: e.target.value })} className="w-44">
          <option value="">{t('bk.filter.allRepos')}</option>
          {(repos.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
        </NativeSelect>
        <NativeSelect aria-label={t('common.status')} value={status} onChange={(e) => setParam({ status: e.target.value })} className="w-40">
          <option value="">{t('bk.filter.allStatuses')}</option>
          {POINT_STATUSES.map((s) => <option key={s} value={s}>{t(('bk.pointStatus.' + s) as TKey)}</option>)}
        </NativeSelect>
        <Button size="sm" variant="outline" onClick={reload}><RefreshCw className={cn(points.loading && 'animate-spin')} />{t('common.refresh')}</Button>
        <span className="text-xs text-muted-foreground">{t('bk.filter.pointsCount', { n: rows.length, size: formatBytes(total) })}</span>
      </div>
      <div className="space-y-2 px-4 empty:hidden">
        <ErrorAlert error={points.error} />
        {(mounts.data ?? []).length > 0 && <MountsList className="mt-2" mounts={mounts.data ?? []} onChanged={mounts.reload} onBrowse={setBrowsing} />}
      </div>
      {points.loading && !points.data ? <Loading /> : (
        <PointsTable points={rows} showVm actions={pa.actions} canRestore={access.restore} onRestore={(p) => pa.open('restore', p)} empty={t('bk.point.none')} className="min-h-0 flex-1" />
      )}
      {pa.dialogs}
      {browsing && <MountBrowseDialog mount={browsing} onClose={() => setBrowsing(null)} onChanged={mounts.reload} />}
    </div>
  );
}

// ------------------------------------------------------------------ file restore sessions

function FilesTab() {
  const { t } = useI18n();
  const mounts = useLoad(() => get<MountInfo[]>('/api/v1/backup/mounts'), []);
  const [browsing, setBrowsing] = useState<MountInfo | null>(null);
  const list = mounts.data ?? [];
  let body: ReactNode;
  if (mounts.loading && !mounts.data) body = <Loading />;
  else if (list.length === 0) body = <Empty icon={<FolderSearch className="size-6" />}>{t('bk.files.none')}<span className="max-w-xl text-xs">{t('bk.files.noneHelp')}</span></Empty>;
  else body = <MountsList mounts={list} onChanged={mounts.reload} onBrowse={setBrowsing} />;
  return (
    <div className="space-y-3 p-4">
      <div className="flex items-center gap-2">
        <Button size="sm" variant="outline" onClick={mounts.reload}><RefreshCw className={cn(mounts.loading && 'animate-spin')} />{t('common.refresh')}</Button>
        <Button size="sm" variant="outline" asChild><Link to="/backup?tab=points&status=Ok"><Database />{t('bk.files.choosePoint')}</Link></Button>
      </div>
      <ErrorAlert error={mounts.error} />
      {body}
      <Notice kind="info" title={t('bk.files.howTitle')}>{t('bk.files.how', { h: MOUNT_IDLE_HOURS })}</Notice>
      {browsing && <MountBrowseDialog mount={browsing} onClose={() => setBrowsing(null)} onChanged={mounts.reload} />}
    </div>
  );
}
