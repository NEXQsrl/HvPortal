import { Plus, Search, X } from 'lucide-react';
import { useMemo, useState, type ReactNode } from 'react';
import { post, put } from '@/api/client';
import { GroupIcon, Notice, VmIcon } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { NativeSelect } from '@/components/ui/native-select';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { useI18n, type TKey } from '@/i18n';
import { toast } from '@/lib/toast';
import { cn } from '@/lib/utils';
import { CHECKPOINT_TYPES, NOTIFY_VALUES, type BackupJobDef, type CheckpointType, type NotifyOn, type Repository, type ScheduleKind } from './backup-api';
import { WEEK_ORDER, weekdayNames } from './BackupCommon';
import { BACKUP_NAME, MAX_RETENTION_POINTS, MAX_TARGETS, MAX_TIMES, TIME_OF_DAY, browserTimeZone, knownTimeZones } from './backup-rules';

interface Draft {
  name: string; description: string; enabled: boolean; repositoryId: string;
  scheduleKind: ScheduleKind; daysMask: number; times: string[]; intervalHours: number; timeZoneId: string;
  retentionPoints: number; retentionDaysOn: boolean; retentionDays: number;
  checkpointType: CheckpointType; verifyAfterCopy: boolean; restoreTestDays: number; notifyOn: NotifyOn;
  vmIds: string[]; groupIds: string[];
}

function draftOf(job: BackupJobDef | null, repos: Repository[]): Draft {
  return job ? {
    name: job.name, description: job.description ?? '', enabled: job.enabled, repositoryId: job.repositoryId,
    scheduleKind: job.scheduleKind, daysMask: job.daysMask, times: job.times.length ? job.times : ['22:00'], intervalHours: job.intervalHours, timeZoneId: job.timeZoneId,
    retentionPoints: job.retentionPoints, retentionDaysOn: job.retentionDays != null, retentionDays: job.retentionDays ?? 30,
    checkpointType: job.checkpointType, verifyAfterCopy: job.verifyAfterCopy, restoreTestDays: job.restoreTestDays, notifyOn: job.notifyOn,
    vmIds: job.vmIds, groupIds: job.groupIds,
  } : {
    name: '', description: '', enabled: true, repositoryId: repos[0]?.id ?? '',
    scheduleKind: 'daily', daysMask: 0x7f, times: ['22:00'], intervalHours: 6, timeZoneId: browserTimeZone(),
    retentionPoints: 7, retentionDaysOn: false, retentionDays: 30,
    checkpointType: 'Production', verifyAfterCopy: true, restoreTestDays: 0, notifyOn: 'warning',
    vmIds: [], groupIds: [],
  };
}

const intIn = (v: number, min: number, max: number) => Number.isInteger(v) && v >= min && v <= max;

function SectionTitle({ children }: { children: ReactNode }) {
  return <h3 className="border-b pb-1 text-xs font-semibold tracking-wide text-muted-foreground uppercase">{children}</h3>;
}

/**
 * Create or change a backup job (POST /backup/jobs, PUT /backup/jobs/{id}): what (VMs and groups, groups resolved at
 * each run), where (repository), when (daily / every N hours / manual, in a time zone), how long (retention) and how
 * (checkpoint type, verification, periodic restore tests, notifications).
 */
export function JobDialog({ job, repositories, onSaved, onClose }: { job: BackupJobDef | null; repositories: Repository[]; onSaved: () => void; onClose: () => void }) {
  const { t, lang, fmtDate } = useI18n();
  const inv = useInventory();
  const [d, setD] = useState<Draft>(() => draftOf(job, repositories));
  const set = (p: Partial<Draft>) => setD((x) => ({ ...x, ...p }));
  const [newTime, setNewTime] = useState('');
  const zones = useMemo(knownTimeZones, []);
  const days = weekdayNames(lang);
  const repo = repositories.find((r) => r.id === d.repositoryId);

  // VMs covered by the selection: chosen directly, or members of a chosen group or of one of its sub-groups.
  const covered = useMemo(() => {
    const groups = new Set(d.groupIds);
    let changed = true;
    while (changed) {
      changed = false;
      for (const g of inv.groups) if (g.parentId && groups.has(g.parentId) && !groups.has(g.id)) { groups.add(g.id); changed = true; }
    }
    const viaGroup = new Set<string>();
    for (const g of inv.groups) if (groups.has(g.id)) g.vmIds.forEach((v) => viaGroup.add(v));
    return { viaGroup, all: new Set([...d.vmIds, ...viaGroup]) };
  }, [d.groupIds, d.vmIds, inv.groups]);
  const coveredVms = [...covered.all].map((id) => inv.vmById.get(id)).filter((v) => !!v && !v.isMissing);
  const otherHost = repo?.hostId ? coveredVms.filter((v) => v!.hostId !== repo.hostId).length : 0;
  const noStaging = repo?.hasCredentials ? coveredVms.filter((v) => !inv.hostById.get(v!.hostId)?.backupStagingPath).length : 0;
  // VMs of a cluster move between nodes: every node of their clusters needs the staging folder too.
  const clusterIds = new Set(coveredVms.map((v) => inv.hostById.get(v!.hostId)?.clusterId).filter((c): c is string => !!c));
  const nodesNoStaging = repo?.hasCredentials ? inv.hosts.filter((h) => h.clusterId && clusterIds.has(h.clusterId) && !h.backupStagingPath).map((h) => h.displayName) : [];
  const hiddenVms = d.vmIds.filter((id) => !inv.vmById.has(id)).length;
  const hiddenGroups = d.groupIds.filter((id) => !inv.groupById.has(id)).length;

  const errors = {
    name: !BACKUP_NAME.test(d.name.trim()) ? 'bk.name.invalid' : null,
    repo: !d.repositoryId ? 'bk.job.repoRequired' : null,
    targets: d.vmIds.length + d.groupIds.length === 0 ? 'bk.job.targetsRequired' : d.vmIds.length + d.groupIds.length > MAX_TARGETS ? 'bk.job.targetsTooMany' : null,
    schedule: d.scheduleKind === 'daily' ? ((d.daysMask & 0x7f) === 0 ? 'bk.job.daysRequired' : d.times.length === 0 || d.times.length > MAX_TIMES || d.times.some((x) => !TIME_OF_DAY.test(x)) ? 'bk.job.timesRequired' : null)
      : d.scheduleKind === 'hourly' && !intIn(d.intervalHours, 1, 24) ? 'bk.job.intervalInvalid' : null,
    zone: !d.timeZoneId.trim() || d.timeZoneId.trim().length > 64 ? 'bk.job.zoneInvalid' : null,
    retention: !intIn(d.retentionPoints, 1, MAX_RETENTION_POINTS) || (d.retentionDaysOn && !intIn(d.retentionDays, 1, 3650)) ? 'bk.job.retentionInvalid' : null,
    test: !intIn(d.restoreTestDays, 0, 365) ? 'bk.job.testInvalid' : null,
    description: d.description.length > 500 ? 'bk.descriptionTooLong' : null,
  } satisfies Record<string, TKey | null>;
  const valid = Object.values(errors).every((e) => e === null);

  const addTime = () => {
    const v = newTime.trim();
    if (!TIME_OF_DAY.test(v) || d.times.includes(v) || d.times.length >= MAX_TIMES) return;
    set({ times: [...d.times, v].sort() });
    setNewTime('');
  };

  const save = async () => {
    const body = {
      name: d.name.trim(), description: d.description.trim() || undefined, repositoryId: d.repositoryId, enabled: d.enabled,
      scheduleKind: d.scheduleKind, daysMask: d.daysMask & 0x7f, times: d.times, intervalHours: d.intervalHours, timeZoneId: d.timeZoneId.trim(),
      retentionPoints: d.retentionPoints, retentionDays: d.retentionDaysOn ? d.retentionDays : null,
      checkpointType: d.checkpointType, verifyAfterCopy: d.verifyAfterCopy, restoreTestDays: d.restoreTestDays, notifyOn: d.notifyOn,
      vmIds: d.vmIds, groupIds: d.groupIds,
    };
    const r = job ? await put<{ id: string; nextRunAt: string | null }>(`/api/v1/backup/jobs/${job.id}`, body) : await post<{ id: string; nextRunAt: string | null }>('/api/v1/backup/jobs', body);
    toast.success(t(job ? 'bk.job.saved' : 'bk.job.created', { name: body.name }), r.nextRunAt ? t('bk.job.nextRunAt', { at: fmtDate(r.nextRunAt) }) : t(d.enabled ? 'bk.job.noNextRun' : 'bk.job.disabledNote'));
    onSaved();
  };

  return (
    <ActionDialog title={job ? t('bk.job.editTitle', { name: job.name }) : t('bk.job.newTitle')} confirmLabel={t('common.save')} disabled={!valid} wide onClose={onClose} onConfirm={save}>
      <SectionTitle>{t('bk.job.general')}</SectionTitle>
      <div className="grid gap-3 sm:grid-cols-[1fr_auto]">
        <Field label={t('common.name')} htmlFor="bj-name" error={d.name && errors.name ? t(errors.name) : undefined}>
          <Input id="bj-name" value={d.name} maxLength={64} onChange={(e) => set({ name: e.target.value })} autoFocus />
        </Field>
        <div className="flex items-end gap-2 pb-1.5">
          <Switch id="bj-enabled" checked={d.enabled} onCheckedChange={(v) => set({ enabled: v })} />
          <Label htmlFor="bj-enabled" className="cursor-pointer text-sm">{t('bk.job.enabled')}</Label>
        </div>
      </div>
      <Field label={t('common.description')} htmlFor="bj-desc" aside={`${d.description.length}/500`} error={errors.description ? t(errors.description) : undefined}>
        <Textarea id="bj-desc" rows={2} maxLength={500} value={d.description} onChange={(e) => set({ description: e.target.value })} />
      </Field>

      <SectionTitle>{t('bk.job.what')}</SectionTitle>
      <TargetPicker vmIds={d.vmIds} groupIds={d.groupIds} viaGroup={covered.viaGroup} onChange={(vmIds, groupIds) => set({ vmIds, groupIds })} />
      <p className="text-xs text-muted-foreground">
        {t('bk.job.coverage', { n: covered.all.size })}{(hiddenVms + hiddenGroups) > 0 && ` ${t('bk.job.hiddenTargets', { n: hiddenVms + hiddenGroups })}`}
      </p>
      {errors.targets && (d.vmIds.length + d.groupIds.length > 0) && <p className="text-xs text-destructive">{t(errors.targets)}</p>}
      <p className="text-xs text-muted-foreground">{t('bk.job.runsAsNote')}</p>

      <SectionTitle>{t('bk.job.where')}</SectionTitle>
      {repositories.length === 0 ? <Notice kind="warn">{t('bk.job.noRepos')}</Notice> : (
        <Field label={t('bk.point.repository')} htmlFor="bj-repo">
          <NativeSelect id="bj-repo" value={d.repositoryId} onChange={(e) => set({ repositoryId: e.target.value })}>
            {!d.repositoryId && <option value="" />}
            {repositories.map((r) => <option key={r.id} value={r.id}>{r.name} — {r.path}{r.hostName ? ` (${r.hostName})` : ''}</option>)}
          </NativeSelect>
        </Field>
      )}
      {otherHost > 0 && repo && <Notice kind="warn">{t('bk.job.otherHostWarn', { n: otherHost, host: repo.hostName ?? '—' })}</Notice>}
      {noStaging > 0 && <Notice kind="warn">{t('bk.job.noStagingWarn', { n: noStaging })}</Notice>}
      {nodesNoStaging.length > 0 && <Notice kind="warn">{t('bk.job.noStagingNodes', { nodes: nodesNoStaging.join(', ') })}</Notice>}

      <SectionTitle>{t('bk.job.when')}</SectionTitle>
      <div className="flex flex-wrap gap-1.5" role="radiogroup" aria-label={t('bk.job.when')}>
        {(['daily', 'hourly', 'manual'] as const).map((k) => (
          <Button key={k} type="button" size="sm" variant={d.scheduleKind === k ? 'default' : 'outline'} role="radio" aria-checked={d.scheduleKind === k} onClick={() => set({ scheduleKind: k })}>
            {t(('bk.sched.' + k) as TKey)}
          </Button>
        ))}
      </div>
      {d.scheduleKind === 'daily' && (
        <>
          <Field label={t('bk.job.days')} error={errors.schedule === 'bk.job.daysRequired' ? t(errors.schedule) : undefined}>
            <div className="flex flex-wrap gap-1">
              {WEEK_ORDER.map((i) => {
                const on = (d.daysMask & (1 << i)) !== 0;
                return <Button key={i} type="button" size="sm" variant={on ? 'default' : 'outline'} aria-pressed={on} className="w-12" onClick={() => set({ daysMask: d.daysMask ^ (1 << i) })}>{days[i]}</Button>;
              })}
              <Button type="button" size="sm" variant="ghost" onClick={() => set({ daysMask: 0x7f })}>{t('bk.sched.everyDay')}</Button>
              <Button type="button" size="sm" variant="ghost" onClick={() => set({ daysMask: 0x3e })}>{t('bk.sched.weekdays')}</Button>
            </div>
          </Field>
          <Field label={t('bk.job.times')} hint={t('bk.job.timesHint')} error={errors.schedule === 'bk.job.timesRequired' ? t(errors.schedule) : undefined}>
            <div className="flex flex-wrap items-center gap-1.5">
              {d.times.map((x) => (
                <span key={x} className="inline-flex items-center gap-1 rounded-md border bg-muted px-2 py-0.5 font-mono text-sm tabular">
                  {x}
                  <button type="button" aria-label={t('bk.job.removeTime', { time: x })} className="text-muted-foreground hover:text-destructive" onClick={() => set({ times: d.times.filter((y) => y !== x) })}><X className="size-3.5" /></button>
                </span>
              ))}
              <Input type="time" aria-label={t('bk.job.addTime')} className="w-28" value={newTime} onChange={(e) => setNewTime(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addTime(); } }} />
              <Button type="button" size="sm" variant="outline" onClick={addTime} disabled={!TIME_OF_DAY.test(newTime) || d.times.includes(newTime) || d.times.length >= MAX_TIMES}><Plus />{t('common.add')}</Button>
            </div>
          </Field>
        </>
      )}
      {d.scheduleKind === 'hourly' && (
        <Field label={t('bk.job.interval')} htmlFor="bj-interval" aside="1–24" error={errors.schedule ? t(errors.schedule) : undefined}>
          <Input id="bj-interval" type="number" min={1} max={24} className="w-32" value={d.intervalHours} onChange={(e) => set({ intervalHours: Number(e.target.value) })} />
        </Field>
      )}
      {d.scheduleKind === 'manual' && <p className="text-xs text-muted-foreground">{t('bk.sched.manualHelp')}</p>}
      {d.scheduleKind !== 'manual' && (
        <Field label={t('bk.job.zone')} htmlFor="bj-zone" hint={t('bk.job.zoneHint')} error={errors.zone ? t(errors.zone) : undefined}>
          <Input id="bj-zone" list="bj-zones" value={d.timeZoneId} maxLength={64} onChange={(e) => set({ timeZoneId: e.target.value })} className="max-w-xs" />
          <datalist id="bj-zones">{zones.map((z) => <option key={z} value={z} />)}</datalist>
        </Field>
      )}
      {d.scheduleKind === 'hourly' && <p className="text-xs text-muted-foreground">{t('bk.sched.hourlyHelp')}</p>}

      <SectionTitle>{t('bk.job.retention')}</SectionTitle>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('bk.job.retentionPoints')} htmlFor="bj-ret" aside={`1–${MAX_RETENTION_POINTS}`} hint={t('bk.job.retentionPointsHint')}>
          <Input id="bj-ret" type="number" min={1} max={MAX_RETENTION_POINTS} value={d.retentionPoints} onChange={(e) => set({ retentionPoints: Number(e.target.value) })} />
        </Field>
        <div className="field flex min-w-0 flex-col gap-1.5">
          <div className="flex flex-1 items-end gap-1.5">
            <label className="flex cursor-pointer items-center gap-2 text-xs leading-4 font-medium text-muted-foreground">
              <Checkbox checked={d.retentionDaysOn} onCheckedChange={(v) => set({ retentionDaysOn: !!v })} />{t('bk.job.retentionDays')}
            </label>
            <span className="ml-auto shrink-0 text-xs leading-4 text-muted-foreground tabular">1–3650</span>
          </div>
          <Input id="bj-days" type="number" min={1} max={3650} disabled={!d.retentionDaysOn} aria-label={t('bk.job.retentionDays')} value={d.retentionDays}
            onChange={(e) => set({ retentionDays: Number(e.target.value) })} />
        </div>
      </div>
      {errors.retention && <p className="text-xs text-destructive">{t(errors.retention)}</p>}
      <p className="text-xs text-muted-foreground">{t('bk.job.retentionHelp')}</p>

      <SectionTitle>{t('bk.job.how')}</SectionTitle>
      <Field label={t('bk.job.checkpoint')} htmlFor="bj-cp" hint={t('bk.job.checkpointHint')}>
        <NativeSelect id="bj-cp" value={d.checkpointType} onChange={(e) => set({ checkpointType: e.target.value as CheckpointType })}>
          {CHECKPOINT_TYPES.map((c) => <option key={c} value={c}>{t(('bk.cp.' + c) as TKey)}</option>)}
        </NativeSelect>
      </Field>
      <p className="text-xs text-muted-foreground">{t(('bk.cp.' + d.checkpointType + 'Help') as TKey)}</p>
      <label className="flex items-start gap-2 text-sm">
        <Checkbox checked={d.verifyAfterCopy} onCheckedChange={(v) => set({ verifyAfterCopy: !!v })} className="mt-0.5" />
        <span>{t('bk.job.verifyAfterCopy')}<span className="block text-xs text-muted-foreground">{t('bk.job.verifyAfterCopyHelp')}</span></span>
      </label>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('bk.job.restoreTest')} htmlFor="bj-test" aside="0–365" hint={t('bk.job.restoreTestHint')} error={errors.test ? t(errors.test) : undefined}>
          <Input id="bj-test" type="number" min={0} max={365} value={d.restoreTestDays} onChange={(e) => set({ restoreTestDays: Number(e.target.value) })} />
        </Field>
        <Field label={t('bk.job.notify')} htmlFor="bj-notify" hint={t('bk.job.notifyHint')}>
          <NativeSelect id="bj-notify" value={d.notifyOn} onChange={(e) => set({ notifyOn: e.target.value as NotifyOn })}>
            {NOTIFY_VALUES.map((n) => <option key={n} value={n}>{t(('bk.notify.' + n) as TKey)}</option>)}
          </NativeSelect>
        </Field>
      </div>
      <p className="text-xs text-muted-foreground">{d.restoreTestDays > 0 ? t('bk.job.restoreTestOn', { n: d.restoreTestDays }) : t('bk.job.restoreTestOff')}</p>
    </ActionDialog>
  );
}

/** VMs and groups protected by a job; a chosen group covers its VMs and the VMs of its sub-groups at each run. */
function TargetPicker({ vmIds, groupIds, viaGroup, onChange }: { vmIds: string[]; groupIds: string[]; viaGroup: Set<string>; onChange: (vmIds: string[], groupIds: string[]) => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const [filter, setFilter] = useState('');
  const [hostId, setHostId] = useState('');
  const vmSet = new Set(vmIds);
  const groupSet = new Set(groupIds);
  const groupRows: { id: string; name: string; depth: number; count: number }[] = [];
  const walk = (parent: string | null, depth: number) => {
    for (const g of inv.childrenOf(parent)) {
      groupRows.push({ id: g.id, name: g.name, depth, count: g.vmIds.length });
      walk(g.id, depth + 1);
    }
  };
  walk(null, 0);
  const f = filter.trim().toLowerCase();
  const vms = inv.vms.filter((v) => !v.isMissing && (!hostId || v.hostId === hostId) && (!f || v.name.toLowerCase().includes(f) || (v.hostName ?? '').toLowerCase().includes(f)))
    .sort((a, b) => a.name.localeCompare(b.name));
  const toggleVm = (id: string) => onChange(vmSet.has(id) ? vmIds.filter((x) => x !== id) : [...vmIds, id], groupIds);
  const toggleGroup = (id: string) => onChange(vmIds, groupSet.has(id) ? groupIds.filter((x) => x !== id) : [...groupIds, id]);
  return (
    <div className="grid gap-3 md:grid-cols-2">
      <div className="min-w-0 space-y-1.5">
        <div className="text-xs font-medium text-muted-foreground">{t('bk.job.groups', { n: groupIds.length })}</div>
        <div className="h-64 overflow-auto rounded-md border p-1">
          {groupRows.length === 0 && <div className="p-3 text-sm text-muted-foreground">{t('bk.job.noGroups')}</div>}
          {groupRows.map((g) => (
            <label key={g.id} className={cn('flex cursor-pointer items-center gap-2 rounded px-2 py-1 text-sm hover:bg-accent/50', groupSet.has(g.id) && 'bg-primary/5')} style={{ paddingLeft: 8 + g.depth * 16 }}>
              <Checkbox checked={groupSet.has(g.id)} onCheckedChange={() => toggleGroup(g.id)} aria-label={g.name} />
              <GroupIcon open={groupSet.has(g.id)} />
              <span className="min-w-0 flex-1 truncate">{g.name}</span>
              <span className="text-[10px] text-muted-foreground tabular">{g.count}</span>
            </label>
          ))}
        </div>
        <p className="text-xs text-muted-foreground">{t('bk.job.groupsHelp')}</p>
      </div>
      <div className="min-w-0 space-y-1.5">
        <div className="text-xs font-medium text-muted-foreground">{t('bk.job.vms', { n: vmIds.length })}</div>
        <div className="flex gap-1.5">
          <div className="relative min-w-0 flex-1">
            <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
            <Input className="pl-8" placeholder={t('tree.filter')} value={filter} onChange={(e) => setFilter(e.target.value)} aria-label={t('tree.filter')} />
          </div>
          <NativeSelect aria-label={t('bk.job.hostFilter')} value={hostId} onChange={(e) => setHostId(e.target.value)} className="w-36">
            <option value="">{t('bk.job.allHosts')}</option>
            {inv.hosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
          </NativeSelect>
        </div>
        <div className="h-56 overflow-auto rounded-md border">
          {vms.length === 0 && <div className="p-3 text-sm text-muted-foreground">{t('group.noVms')}</div>}
          {vms.map((v) => {
            const via = viaGroup.has(v.id) && !vmSet.has(v.id);
            return (
              <label key={v.id} className="flex cursor-pointer items-center gap-2 border-b px-3 py-1 text-sm last:border-0 hover:bg-accent/50">
                <Checkbox checked={vmSet.has(v.id) || via} disabled={via} onCheckedChange={() => toggleVm(v.id)} aria-label={v.name} />
                <VmIcon state={v.powerState} critical={v.isCritical} />
                <span className="min-w-0 flex-1 truncate">{v.name}</span>
                {via ? <span className="text-[10px] text-muted-foreground">{t('bk.job.viaGroup')}</span> : <span className="truncate text-xs text-muted-foreground">{v.hostName}</span>}
              </label>
            );
          })}
        </div>
        <div className="flex gap-1.5">
          <Button type="button" size="xs" variant="ghost" disabled={vms.length === 0}
            onClick={() => onChange([...new Set([...vmIds, ...vms.filter((v) => !viaGroup.has(v.id)).map((v) => v.id)])], groupIds)}>{t('bk.job.selectShown')}</Button>
          {/* VMs the user cannot see stay in the job: only visible ones are cleared. */}
          <Button type="button" size="xs" variant="ghost" disabled={!vmIds.some((id) => inv.vmById.has(id))} onClick={() => onChange(vmIds.filter((id) => !inv.vmById.has(id)), groupIds)}>{t('bk.job.clearVms')}</Button>
        </div>
      </div>
    </div>
  );
}
