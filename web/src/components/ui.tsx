import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { ApiError } from '../api/client';
import type { JobStatus, PowerState } from '../api/types';
import { useI18n } from '../i18n';

export function Dialog({ open, title, children, actions, onClose }: {
  open: boolean; title: string; children: ReactNode; actions: ReactNode; onClose: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const d = ref.current;
    if (!d) return;
    if (open && !d.open) d.showModal();
    if (!open && d.open) d.close();
  }, [open]);
  return (
    <dialog ref={ref} onClose={onClose} aria-labelledby="dlg-title">
      <div className="dialog-body">
        <h2 id="dlg-title" style={{ marginTop: 0 }}>{title}</h2>
        {children}
      </div>
      <div className="dialog-actions">{actions}</div>
    </dialog>
  );
}

export function ErrorAlert({ error }: { error: unknown }) {
  const { t } = useI18n();
  if (!error) return null;
  const msg = error instanceof ApiError ? `${error.message} (${error.code})` : String((error as Error).message ?? error);
  return <div className="alert danger" role="alert">{t('common.error')}: {msg}</div>;
}

export function PowerBadge({ state, critical }: { state: PowerState; critical?: boolean }) {
  const { t } = useI18n();
  const cls = critical ? 'danger' : state === 'Running' ? 'ok' : state === 'Off' ? '' : state === 'Unknown' ? 'danger' : 'warn';
  return <span className={'badge ' + cls}>{t(('state.' + state) as never)}{critical ? ' ⚠' : ''}</span>;
}

export function JobBadge({ status }: { status: JobStatus }) {
  const cls = status === 'Succeeded' ? 'ok' : status === 'Failed' || status === 'Unknown' ? 'danger'
    : status === 'Unverified' || status === 'Reconciling' ? 'warn' : status === 'Cancelled' ? '' : 'info';
  return <span className={'badge ' + cls}>{status}</span>;
}

export function Progress({ value }: { value: number }) {
  return <div className="progress" role="progressbar" aria-valuenow={value} aria-valuemin={0} aria-valuemax={100}><span style={{ width: `${value}%` }} /></div>;
}

export function CopyButton({ text }: { text: string }) {
  const { t } = useI18n();
  const [done, setDone] = useState(false);
  return (
    <button type="button" onClick={async () => {
      try { await navigator.clipboard.writeText(text); setDone(true); setTimeout(() => setDone(false), 1500); } catch { /* clipboard blocked */ }
    }}>{done ? t('common.copied') : t('common.copy')}</button>
  );
}

export function Loading() {
  const { t } = useI18n();
  return <p className="muted" aria-live="polite">{t('common.loading')}</p>;
}

export function formatBytes(n: number | null | undefined): string {
  if (n == null) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  let v = n;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v >= 10 || i === 0 ? 0 : 1)} ${units[i]}`;
}

export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return '—';
  const s = Math.floor(ms / 1000);
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  return d > 0 ? `${d}d ${h}h` : h > 0 ? `${h}h ${m}m` : `${m}m ${s % 60}s`;
}

/** Simple polling-free data hook with manual reload. */
export function useLoad<T>(loader: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    loader().then((d) => { if (!cancelled) { setData(d); setError(null); } })
      .catch((e) => { if (!cancelled) setError(e); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);
  const reload = useCallback(() => setTick((x) => x + 1), []);
  return { data, error, loading, reload };
}
