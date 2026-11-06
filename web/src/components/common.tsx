import { Check, CircleAlert, Copy, Folder, FolderOpen, History, LoaderCircle, Monitor, Server } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { ApiError } from '@/api/client';
import type { JobStatus, PowerState } from '@/api/types';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { useI18n, type TKey } from '@/i18n';
import { cn } from '@/lib/utils';

// ---------------------------------------------------------------- feedback

export function errorText(error: unknown): string {
  if (!error) return '';
  if (error instanceof ApiError) return `${error.message} (${error.code})`;
  return String((error as Error).message ?? error);
}

export function ErrorAlert({ error, className }: { error: unknown; className?: string }) {
  const { t } = useI18n();
  if (!error) return null;
  return (
    <Alert variant="destructive" className={cn('my-2', className)} role="alert">
      <CircleAlert />
      <AlertTitle>{t('common.error')}</AlertTitle>
      <AlertDescription className="break-words">{errorText(error)}</AlertDescription>
    </Alert>
  );
}

export function Notice({ kind = 'info', title, children, className }: { kind?: 'info' | 'warn' | 'danger' | 'ok'; title?: ReactNode; children?: ReactNode; className?: string }) {
  const tone = {
    info: 'border-info/30 bg-info/5 text-foreground [&>svg]:text-info',
    warn: 'border-warning/40 bg-warning/10 text-foreground [&>svg]:text-warning',
    danger: 'border-destructive/40 bg-destructive/5 text-foreground [&>svg]:text-destructive',
    ok: 'border-success/40 bg-success/10 text-foreground [&>svg]:text-success',
  }[kind];
  return (
    <div role={kind === 'danger' ? 'alert' : 'status'} className={cn('alert rounded-lg border px-3 py-2 text-sm', `alert-${kind}`, tone, className)}>
      {title && <div className="font-medium">{title}</div>}
      {children && <div className={cn(title && 'mt-0.5 text-muted-foreground')}>{children}</div>}
    </div>
  );
}

export function Loading({ className }: { className?: string }) {
  const { t } = useI18n();
  return (
    <div className={cn('flex items-center gap-2 p-4 text-sm text-muted-foreground', className)} aria-live="polite">
      <LoaderCircle className="size-4 animate-spin" /> {t('common.loading')}
    </div>
  );
}

export function Empty({ children, icon, className }: { children: ReactNode; icon?: ReactNode; className?: string }) {
  return (
    <div className={cn('flex flex-col items-center justify-center gap-2 p-8 text-center text-sm text-muted-foreground', className)}>
      {icon}
      {children}
    </div>
  );
}

// ---------------------------------------------------------------- brand

/**
 * HvPortal logo: color artwork on the light theme, white artwork on the dark theme (switched by the `.dark` class,
 * no script). `mark` shows only the symbol. Size it with a height class on `className` (e.g. "h-7").
 */
export function BrandLogo({ className, mark = false }: { className?: string; mark?: boolean }) {
  const light = mark ? '/brand/mark-color-180.png' : '/brand/logo-color.png';
  const dark = mark ? '/brand/mark-white-180.png' : '/brand/logo-white.png';
  return (
    <span className={cn('brand-logo inline-flex shrink-0 items-center', className)}>
      <img src={light} alt="HvPortal" className="block h-full w-auto dark:hidden" draggable={false} />
      <img src={dark} alt="HvPortal" className="hidden h-full w-auto dark:block" draggable={false} />
    </span>
  );
}

// ---------------------------------------------------------------- layout helpers

export function PageHeader({ icon, title, subtitle, badges, actions, children }: {
  icon?: ReactNode; title: ReactNode; subtitle?: ReactNode; badges?: ReactNode; actions?: ReactNode; children?: ReactNode;
}) {
  return (
    <div className="border-b bg-panel px-4 pt-3 pb-0">
      <div className="flex flex-wrap items-start gap-3 pb-3">
        {icon && <div className="mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-lg border bg-card text-muted-foreground">{icon}</div>}
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="truncate text-lg leading-7 font-semibold">{title}</h1>
            {badges}
          </div>
          {subtitle && <div className="truncate text-xs text-muted-foreground">{subtitle}</div>}
        </div>
        {actions && <div className="flex flex-wrap items-center gap-1.5">{actions}</div>}
      </div>
      {children}
    </div>
  );
}

export function Section({ title, actions, children, className, bodyClassName }: {
  title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string; bodyClassName?: string;
}) {
  return (
    <section className={cn('flex min-h-0 flex-col rounded-lg border bg-card', className)}>
      {(title || actions) && (
        <div className="flex min-h-10 items-center gap-2 border-b px-3 py-1.5">
          {title && <h2 className="text-sm font-semibold">{title}</h2>}
          <span className="flex-1" />
          {actions}
        </div>
      )}
      <div className={cn('min-h-0 flex-1', bodyClassName)}>{children}</div>
    </section>
  );
}

export function KeyValue({ items, className }: { items: [ReactNode, ReactNode][]; className?: string }) {
  return (
    <dl className={cn('grid grid-cols-[minmax(7rem,auto)_1fr] gap-x-4 gap-y-1.5 text-sm', className)}>
      {items.map(([k, v], i) => (
        <div key={i} className="contents">
          <dt className="text-muted-foreground">{k}</dt>
          <dd className="min-w-0 break-words">{v ?? '—'}</dd>
        </div>
      ))}
    </dl>
  );
}

export function Meter({ value, className, label }: { value: number | null | undefined; className?: string; label?: string }) {
  if (value == null) return <div className={cn('h-1.5 rounded-full bg-muted', className)} />;
  const v = Math.max(0, Math.min(100, value));
  const tone = v >= 90 ? 'bg-destructive' : v >= 75 ? 'bg-warning' : 'bg-primary';
  return (
    <div className={cn('h-1.5 overflow-hidden rounded-full bg-muted', className)} role="meter" aria-valuenow={v} aria-valuemin={0} aria-valuemax={100} aria-label={label}>
      <div className={cn('h-full rounded-full transition-[width]', tone)} style={{ width: `${v}%` }} />
    </div>
  );
}

export function StatCard({ icon, label, value, sub, meter, className }: {
  icon?: ReactNode; label: ReactNode; value: ReactNode; sub?: ReactNode; meter?: number | null; className?: string;
}) {
  return (
    <div className={cn('rounded-lg border bg-card p-3', className)}>
      <div className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">{icon}{label}</div>
      <div className="mt-1 text-xl font-semibold tabular">{value}</div>
      {meter !== undefined && <Meter value={meter} className="mt-2" />}
      {sub && <div className="mt-1.5 space-y-0.5 text-xs text-muted-foreground">{sub}</div>}
    </div>
  );
}

export function CopyButton({ text, className }: { text: string; className?: string }) {
  const { t } = useI18n();
  const [done, setDone] = useState(false);
  const label = done ? t('common.copied') : t('common.copy');
  // Icon only; the label stays available as tooltip and accessible name.
  return (
    <Button type="button" variant="outline" size="icon-sm" className={className} aria-label={label} title={label} onClick={async () => {
      try { await navigator.clipboard.writeText(text); setDone(true); window.setTimeout(() => setDone(false), 1500); } catch { /* clipboard blocked */ }
    }}>
      {done ? <Check /> : <Copy />}
    </Button>
  );
}

export function Mono({ children, className }: { children: ReactNode; className?: string }) {
  return <span className={cn('font-mono text-xs', className)}>{children}</span>;
}

// ---------------------------------------------------------------- status

type Tone = 'ok' | 'warn' | 'danger' | 'info' | 'muted';
const toneClass: Record<Tone, string> = {
  ok: 'bg-success/12 text-success border-success/25',
  warn: 'bg-warning/15 border-warning/35 text-[color-mix(in_oklch,var(--warning)_55%,var(--foreground))]',
  danger: 'bg-destructive/10 text-destructive border-destructive/25',
  info: 'bg-info/10 text-info border-info/25',
  muted: 'bg-muted text-muted-foreground border-border',
};

export function StatusBadge({ tone, children, title, className }: { tone: Tone; children: ReactNode; title?: string; className?: string }) {
  return <Badge variant="outline" title={title} className={cn('badge gap-1 rounded-md font-medium', `badge-${tone}`, toneClass[tone], className)}>{children}</Badge>;
}

export function Dot({ tone, className }: { tone: Tone; className?: string }) {
  const c = { ok: 'bg-success', warn: 'bg-warning', danger: 'bg-destructive', info: 'bg-info', muted: 'bg-muted-foreground/50' }[tone];
  return <span className={cn('inline-block size-2 shrink-0 rounded-full', c, className)} aria-hidden="true" />;
}

export function powerTone(state: PowerState, critical?: boolean): Tone {
  if (critical) return 'danger';
  if (state === 'Running') return 'ok';
  if (state === 'Off') return 'muted';
  if (state === 'Unknown' || state === 'Other') return 'danger';
  return 'warn';
}

export function PowerBadge({ state, critical }: { state: PowerState; critical?: boolean }) {
  const { t } = useI18n();
  return <StatusBadge tone={powerTone(state, critical)}><Dot tone={powerTone(state, critical)} />{t(('state.' + state) as TKey)}{critical ? ' ⚠' : ''}</StatusBadge>;
}

export function jobTone(status: JobStatus): Tone {
  return status === 'Succeeded' ? 'ok'
    : status === 'Failed' || status === 'Unknown' ? 'danger'
    : status === 'Unverified' || status === 'Reconciling' ? 'warn'
    : status === 'Cancelled' ? 'muted' : 'info';
}

export function JobBadge({ status }: { status: JobStatus }) {
  const { t } = useI18n();
  return (
    <StatusBadge tone={jobTone(status)}>
      {(status === 'Running' || status === 'Queued' || status === 'Reconciling') && <LoaderCircle className="animate-spin" />}
      {t(('jobstatus.' + status) as TKey)}
    </StatusBadge>
  );
}

/** VM glyph with a state marker, as in hypervisor inventory trees. */
export function VmIcon({ state, critical, className }: { state: PowerState; critical?: boolean; className?: string }) {
  const { t } = useI18n();
  const label = t(('state.' + state) as TKey) + (critical ? ' (critical)' : '');
  const tone = powerTone(state, critical);
  return (
    <span className={cn('state-icon relative inline-flex size-4 shrink-0', `state-${state.toLowerCase()}`, className)} role="img" aria-label={label} title={label}>
      <Monitor className={cn('size-4', state === 'Running' ? 'text-foreground/80' : 'text-muted-foreground')} strokeWidth={1.75} />
      <span className={cn('absolute -right-0.5 -bottom-0.5 size-2 rounded-full ring-2 ring-[var(--tree-bg,var(--card))]',
        { ok: 'bg-success', warn: 'bg-warning', danger: 'bg-destructive', info: 'bg-info', muted: 'bg-muted-foreground/60' }[tone],
        (state === 'Starting' || state === 'Stopping' || state === 'Saving' || state === 'Resetting') && 'animate-pulse')} />
    </span>
  );
}

export function HostIcon({ connected, access, className }: { connected: boolean; access?: string | null; className?: string }) {
  const tone: Tone = !connected ? 'danger' : access && access !== 'Ok' ? 'warn' : 'ok';
  return (
    <span className={cn('relative inline-flex size-4 shrink-0', className)} aria-hidden="true">
      <Server className="size-4 text-muted-foreground" strokeWidth={1.75} />
      <span className={cn('absolute -right-0.5 -bottom-0.5 size-2 rounded-full ring-2 ring-[var(--tree-bg,var(--card))]', { ok: 'bg-success', warn: 'bg-warning', danger: 'bg-destructive', info: 'bg-info', muted: 'bg-muted' }[tone])} />
    </span>
  );
}

export function GroupIcon({ open, className }: { open?: boolean; className?: string }) {
  const I = open ? FolderOpen : Folder;
  return <I className={cn('size-4 shrink-0 text-amber-500/90 dark:text-amber-400/90', className)} strokeWidth={1.75} aria-hidden="true" />;
}

export function CheckpointIcon({ count }: { count: number | null }) {
  const { t } = useI18n();
  if (count == null) return <span className="text-muted-foreground" title={t('vm.checkpointsUnknown')}>?</span>;
  if (count === 0) return <span className="text-muted-foreground/60" aria-label={t('vm.checkpointsNone')} title={t('vm.checkpointsNone')}>—</span>;
  const label = t('vm.checkpointsCount', { n: count });
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="checkpoint-icon inline-flex items-center gap-0.5 text-info" role="img" aria-label={label}>
          <History className="size-3.5" /><span className="text-xs font-medium tabular">{count}</span>
        </span>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}
