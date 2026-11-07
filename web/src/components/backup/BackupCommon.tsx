import { ChevronDown, ChevronRight, CircleAlert, CircleCheck, LoaderCircle, TriangleAlert } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import type { Host } from '@/api/types';
import { CopyButton, Notice, StatusBadge } from '@/components/common';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';
import type { BackupCheck, BackupJobDef, BackupPoint, CheckStatus, PointStatus, Repository, RunStatus } from './backup-api';

type T = ReturnType<typeof useI18n>['t'];
type Tone = 'ok' | 'warn' | 'danger' | 'info' | 'muted';

/** What the user may do in the backup area (global permissions; the server authorizes every request again). */
export function useBackupAccess() {
  const { can } = useSession();
  const manage = can('backup.manage');
  const run = can('backup.run');
  const restore = can('backup.restore');
  return { manage, run, restore, see: manage || run || restore };
}

export const pointTone = (s: PointStatus): Tone =>
  s === 'Ok' ? 'ok' : s === 'Unverified' ? 'warn' : s === 'Failed' ? 'danger' : s === 'Deleted' ? 'muted' : 'info';

export function PointStatusBadge({ status }: { status: PointStatus }) {
  const { t } = useI18n();
  return (
    <StatusBadge tone={pointTone(status)} title={t(('bk.pointStatusHelp.' + status) as TKey)}>
      {(status === 'Creating' || status === 'Deleting') && <LoaderCircle className="animate-spin" />}
      {t(('bk.pointStatus.' + status) as TKey)}
    </StatusBadge>
  );
}

export const runTone = (s: RunStatus): Tone => (s === 'Success' ? 'ok' : s === 'Warning' ? 'warn' : s === 'Failed' ? 'danger' : 'info');

export function RunStatusBadge({ status }: { status: RunStatus }) {
  const { t } = useI18n();
  return (
    <StatusBadge tone={runTone(status)}>
      {status === 'Running' && <LoaderCircle className="animate-spin" />}
      {t(('bk.runStatus.' + status) as TKey)}
    </StatusBadge>
  );
}

const checkTone = (s: string): Tone => (s === 'Ok' ? 'ok' : s === 'Warning' ? 'warn' : s === 'Critical' ? 'danger' : 'muted');

/** Worst status of a list of checks (Critical > Warning > Ok); null when there are none. */
export function worstCheck(checks: BackupCheck[]): CheckStatus | null {
  if (checks.length === 0) return null;
  if (checks.some((c) => c.status === 'Critical')) return 'Critical';
  if (checks.some((c) => c.status !== 'Ok')) return 'Warning';
  return 'Ok';
}

export function CheckStatusBadge({ status }: { status: string }) {
  const { t } = useI18n();
  const known = status === 'Ok' || status === 'Warning' || status === 'Critical';
  return <StatusBadge tone={checkTone(status)}>{known ? t(('bk.check.status.' + status) as TKey) : status}</StatusBadge>;
}

/** Area of a readiness check from its key ("vm-checkpoints:<id>" -> "vm-checkpoints", "cmdlet-Import-VM" -> "cmdlet"). */
export function checkArea(key: string): string {
  const base = key.split(':')[0] ?? key;
  return base.startsWith('cmdlet-') ? 'cmdlet' : base;
}

const KNOWN_AREAS = new Set(['hyperv-module', 'cmdlet', 'vm-checkpoints', 'vm-vss', 'vm-guest', 'repository', 'repository-space', 'staging', 'staging-folder']);

/** Checks of other VMs are left out when the list is about one VM (keys "vm-...:<Hyper-V id>"). */
export function checksForVm(checks: BackupCheck[], hyperVId: string): BackupCheck[] {
  return checks.filter((c) => {
    const [base, id] = c.key.split(':');
    return !(base?.startsWith('vm-') && id && id.toLowerCase() !== hyperVId.toLowerCase());
  });
}

// A fix that is a PowerShell command (Verb-Noun ...) is shown as code with a copy button.
const COMMAND = /^[A-Z][a-z]+-[A-Za-z]+(?:\s|$)/;

/**
 * Readiness of a host for backups (GetBackupReadiness): problems first, each with what it means and how to enable what
 * is missing (the command given by the host when there is one). Passed checks stay folded.
 */
export function ReadinessList({ checks, className, hostId, onNavigate }: { checks: BackupCheck[]; className?: string; hostId?: string; onNavigate?: () => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const [showOk, setShowOk] = useState(false);
  // Where the portal itself can fix a problem: the staging folder in the host settings, checkpoints in the VM settings.
  const fixLink = (c: BackupCheck): { to: string; label: string } | null => {
    const area = checkArea(c.key);
    if ((area === 'staging' || area === 'staging-folder') && hostId) return { to: `/hosts/${hostId}?tab=settings`, label: t('bk.check.openHostSettings') };
    if (area === 'vm-checkpoints') {
      const id = c.key.split(':')[1]?.toLowerCase();
      const vm = id ? inv.vms.find((v) => v.hyperVId.toLowerCase() === id) : undefined;
      if (vm?.canConfigure) return { to: `/vms/${vm.id}?tab=settings`, label: t('bk.check.openVmSettings', { name: vm.name }) };
    }
    return null;
  };
  const order = (c: BackupCheck) => (c.status === 'Critical' ? 0 : c.status === 'Ok' ? 2 : 1);
  const problems = checks.filter((c) => c.status !== 'Ok').sort((a, b) => order(a) - order(b));
  const passed = checks.filter((c) => c.status === 'Ok');
  const title = (c: BackupCheck) => {
    const area = checkArea(c.key);
    return KNOWN_AREAS.has(area) ? t(('bk.check.' + area) as TKey) : c.key;
  };
  const how = (c: BackupCheck) => {
    const area = checkArea(c.key);
    return KNOWN_AREAS.has(area) ? t(('bk.check.' + area + '.how') as TKey) : null;
  };
  return (
    <div className={cn('space-y-2', className)}>
      {problems.length === 0 && passed.length > 0 && <Notice kind="ok">{t('bk.check.allOk', { n: passed.length })}</Notice>}
      {problems.map((c, i) => (
        <div key={c.key + i} role={c.status === 'Critical' ? 'alert' : 'status'}
          className={cn('readiness-check rounded-lg border px-3 py-2 text-sm', c.status === 'Critical' ? 'border-destructive/40 bg-destructive/5' : 'border-warning/40 bg-warning/10')}>
          <div className="flex flex-wrap items-center gap-2">
            {c.status === 'Critical' ? <CircleAlert className="size-4 text-destructive" /> : <TriangleAlert className="size-4 text-warning" />}
            <span className="font-medium">{title(c)}</span>
            <CheckStatusBadge status={c.status} />
          </div>
          <p className="mt-1 break-words">{c.message}</p>
          {how(c) && <p className="mt-1 text-xs text-muted-foreground"><span className="font-medium text-foreground/80">{t('bk.check.howTo')}</span> {how(c)}</p>}
          {c.fix && (COMMAND.test(c.fix) ? (
            <div className="mt-1.5 flex items-start gap-1.5">
              <pre className="command min-w-0 flex-1 overflow-x-auto rounded-md border bg-muted px-2 py-1 font-mono text-[11px] whitespace-pre-wrap break-all">{c.fix}</pre>
              <CopyButton text={c.fix} />
            </div>
          ) : <p className="mt-1 text-xs"><span className="font-medium">{t('bk.check.fix')}</span> {c.fix}</p>)}
          {(() => {
            const link = fixLink(c);
            return link && <Link className="mt-1 inline-block text-xs font-medium text-primary hover:underline" to={link.to} onClick={onNavigate}>{link.label}</Link>;
          })()}
        </div>
      ))}
      {passed.length > 0 && problems.length > 0 && (
        <button type="button" className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground" onClick={() => setShowOk(!showOk)} aria-expanded={showOk}>
          {showOk ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}{t('bk.check.passed', { n: passed.length })}
        </button>
      )}
      {(showOk || problems.length === 0) && passed.length > 0 && (
        <ul className="space-y-1">
          {passed.map((c, i) => (
            <li key={c.key + i} className="flex items-start gap-2 text-xs">
              <CircleCheck className="mt-px size-3.5 shrink-0 text-success" />
              <span><span className="font-medium">{title(c)}</span> — <span className="text-muted-foreground">{c.message}</span></span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Requested checkpoint type, in words (never "application-consistent": see BackupCheckpointTypes). */
export const checkpointRequested = (t: T, type: string | null | undefined) =>
  type === 'Production' || type === 'ProductionOnly' || type === 'Standard' ? t(('bk.cp.' + type) as TKey) : type ?? '—';

/** What Hyper-V actually produced: a standard checkpoint makes a crash-consistent copy. */
export function CheckpointUsed({ point }: { point: Pick<BackupPoint, 'checkpointType' | 'checkpointTypeUsed'> }) {
  const { t } = useI18n();
  const used = point.checkpointTypeUsed;
  if (!used) return <span className="text-muted-foreground" title={t('bk.cpUsed.unknownHelp')}>{t('bk.cpUsed.unknown')}</span>;
  const fellBack = point.checkpointType === 'Production' && used === 'Standard';
  return (
    <span title={t(used === 'Standard' ? 'bk.cpUsed.StandardHelp' : 'bk.cpUsed.ProductionHelp')} className={cn(fellBack && 'text-[color-mix(in_oklch,var(--warning)_55%,var(--foreground))]')}>
      {used === 'Production' || used === 'Standard' ? t(('bk.cpUsed.' + used) as TKey) : used}
      {fellBack && ' ⚠'}
    </span>
  );
}

/** Verification (SHA-256 of every file) and restore test state of a point. */
export function VerifyState({ point }: { point: BackupPoint }) {
  const { t, fmtDate } = useI18n();
  return (
    <div className="space-y-0.5 text-xs">
      <div title={point.verifyDetail ?? undefined}>
        {point.verifyOk === true ? <span className="text-success">✓ {t('bk.verify.ok')}</span>
          : point.verifyOk === false ? <span className="text-destructive">✗ {t('bk.verify.failed')}</span>
          : <span className="text-muted-foreground">{t('bk.verify.never')}</span>}
        {point.verifiedAt && <span className="text-muted-foreground"> · {fmtDate(point.verifiedAt)}</span>}
      </div>
      {point.testPassed != null && (
        <div title={point.testDetail ?? undefined}>
          {point.testPassed ? <span className="text-success">✓ {t('bk.test.ok')}</span> : <span className="text-destructive">✗ {t('bk.test.failed')}</span>}
          {point.testedAt && <span className="text-muted-foreground"> · {fmtDate(point.testedAt)}</span>}
        </div>
      )}
    </div>
  );
}

/** "schedule" | "manual" | "adhoc" in words. */
export const triggerText = (t: T, trigger: string) =>
  trigger === 'schedule' || trigger === 'manual' || trigger === 'adhoc' ? t(('bk.trigger.' + trigger) as TKey) : trigger;

/** Short names of the days of the week in the UI language, index 0 = Sunday (bit 0 of DaysMask). */
export function weekdayNames(lang: string): string[] {
  const f = new Intl.DateTimeFormat(lang === 'it' ? 'it-IT' : 'en-GB', { weekday: 'short', timeZone: 'UTC' });
  // 2023-01-01 was a Sunday.
  return Array.from({ length: 7 }, (_, i) => f.format(new Date(Date.UTC(2023, 0, 1 + i))));
}

/** Monday first, as in European calendars: bit indexes of the days. */
export const WEEK_ORDER = [1, 2, 3, 4, 5, 6, 0];

export function scheduleText(t: T, lang: string, j: Pick<BackupJobDef, 'scheduleKind' | 'daysMask' | 'times' | 'intervalHours' | 'timeZoneId'>): string {
  if (j.scheduleKind === 'manual') return t('bk.sched.manualShort');
  if (j.scheduleKind === 'hourly') return t('bk.sched.hourlyText', { n: j.intervalHours });
  const names = weekdayNames(lang);
  const mask = j.daysMask & 0x7f;
  const days = mask === 0x7f ? t('bk.sched.everyDay') : mask === 0x3e ? t('bk.sched.weekdays') : WEEK_ORDER.filter((d) => mask & (1 << d)).map((d) => names[d]).join(', ');
  return `${days} ${j.times.join(', ')} (${j.timeZoneId})`;
}

/** Free space of the drive of a local repository, from the live inventory of its host; null for shares or unknown. */
export function repositorySpace(repo: Pick<Repository, 'isShare' | 'hostId' | 'path'>, hostById: Map<string, Host>): { free: number; total: number } | null {
  if (repo.isShare || !repo.hostId) return null;
  const host = hostById.get(repo.hostId);
  const drive = repo.path.slice(0, 2).toUpperCase();
  const v = host?.volumes.find((x) => x.name.slice(0, 2).toUpperCase() === drive);
  return v && v.freeBytes != null && v.sizeBytes ? { free: v.freeBytes, total: v.sizeBytes } : null;
}

export function SpaceText({ space }: { space: { free: number; total: number } | null }) {
  const { t } = useI18n();
  if (!space) return <span className="text-muted-foreground">{t('bk.repo.spaceUnknown')}</span>;
  return <span>{t('picker.free', { free: formatBytes(space.free), total: formatBytes(space.total) })}</span>;
}
