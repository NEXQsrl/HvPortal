import { ArrowUpCircle, Check, CircleAlert, CircleCheck, Loader2 } from 'lucide-react';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { realtime } from '@/api/realtime';
import { Button } from '@/components/ui/button';
import { useI18n, type TKey } from '@/i18n';
import { formatDuration } from '@/lib/format';
import { advance, isFinal, isSlow, loadRun, nextDelay, saveRun, type Observation, type UpdateRun } from '@/lib/portal-update';
import { cn } from '@/lib/utils';

interface PortalUpdateApi {
  /**
   * Shows the waiting screen and runs <paramref name="request"/> (the install request: the gateway downloads and
   * verifies the setup before it answers). Accepted: the screen follows the update. Refused: the screen closes and
   * the error is thrown to the caller, which shows it as before.
   */
  begin: (target: string, from: string, request: () => Promise<unknown>) => Promise<void>;
}

const Ctx = createContext<PortalUpdateApi>({ begin: async (_t, _f, request) => { await request(); } });
export const usePortalUpdate = () => useContext(Ctx);

const POLL_TIMEOUT_MS = 6000;

async function fetchJson(path: string): Promise<{ ok: boolean; status: number; body: unknown }> {
  const abort = new AbortController();
  const timer = window.setTimeout(() => abort.abort(), POLL_TIMEOUT_MS);
  try {
    // Plain fetch, not the API client: a 401 here must not sign the user out or open dialogs.
    const r = await fetch(path, { credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'application/json' }, signal: abort.signal });
    let body: unknown = null;
    try { body = await r.json(); } catch { /* not JSON: the gateway is not serving the API (yet) */ }
    return { ok: r.ok && body !== null, status: r.status, body };
  } finally {
    window.clearTimeout(timer);
  }
}

/** One look at the gateway: is it there, which version runs, and (when signed in) what the updater wrote. */
async function observe(): Promise<Observation> {
  let state: { ok: boolean; body: unknown };
  try {
    state = await fetchJson('/api/v1/auth/state');
  } catch {
    return { reachable: false };
  }
  const s = state.body as { version?: unknown; authenticated?: unknown } | null;
  if (!state.ok || typeof s?.version !== 'string') return { reachable: false };
  const obs: Observation = { reachable: true, version: s.version, authenticated: s.authenticated === true };
  if (obs.authenticated) {
    try {
      const u = await fetchJson('/api/v1/system/updates');
      const install = (u.body as { install?: { outcome?: unknown; message?: unknown } } | null)?.install;
      if (u.ok && typeof install?.outcome === 'string') {
        obs.outcome = install.outcome;
        obs.outcomeMessage = typeof install.message === 'string' ? install.message : null;
      }
    } catch { /* the gateway went away between the two requests: the next poll sees it */ }
  }
  return obs;
}

/**
 * Update of the portal from Settings > System (ADR-042): a full-screen page while the gateway replaces itself. It
 * survives the restart of the gateway, a reload and a new sign-in (the run is in localStorage), reconnects by itself
 * with backoff and reports success only when the gateway that answers runs the requested version.
 */
export function PortalUpdateProvider({ children }: { children: ReactNode }) {
  const [run, setRun] = useState<UpdateRun | null>(() => loadRun(Date.now()));
  const current = useRef(run);
  current.current = run;

  const update = useCallback((next: UpdateRun | null) => {
    current.current = next;
    saveRun(next);
    setRun(next);
  }, []);

  const begin = useCallback(async (target: string, from: string, request: () => Promise<unknown>) => {
    update({ target, from, startedAt: Date.now(), phase: 'preparing', sawDown: false, backOnOtherVersion: 0 });
    try {
      await request();
    } catch (e) {
      update(null);
      throw e;
    }
    update({ target, from, startedAt: Date.now(), phase: 'installing', sawDown: false, backOnOtherVersion: 0 });
  }, [update]);

  // Poll the gateway while an update is going on. One loop per run; it stops at a final phase.
  const polling = run !== null && run.phase !== 'preparing' && !isFinal(run.phase);
  useEffect(() => {
    if (!polling) return;
    let stopped = false;
    let timer = 0;
    let failures = 0;
    const tick = async () => {
      const obs = await observe();
      if (stopped || !current.current) return;
      failures = obs.reachable ? 0 : failures + 1;
      const next = advance(current.current, obs);
      update(next);
      if (!isFinal(next.phase)) timer = window.setTimeout(() => void tick(), nextDelay(next, failures));
    };
    timer = window.setTimeout(() => void tick(), 1500);
    return () => { stopped = true; window.clearTimeout(timer); };
  }, [polling, update]);

  // Another tab of the same browser started or closed the update.
  useEffect(() => {
    const onStorage = (e: StorageEvent) => { if (e.key === 'hvp.portalUpdate') setRun(loadRun(Date.now())); };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, []);

  // Started by another administrator: the gateway tells every open page, which then follows the same update.
  useEffect(() => realtime.on('portalUpdating', (p) => {
    if (!p.version || !p.from || current.current) return;
    update({ target: p.version, from: p.from, startedAt: Date.now(), phase: 'installing', sawDown: false, backOnOtherVersion: 0 });
  }), [update]);

  const api = useMemo(() => ({ begin }), [begin]);
  return (
    <Ctx.Provider value={api}>
      {children}
      {run && <Overlay run={run} onClose={() => update(null)} />}
    </Ctx.Provider>
  );
}

const STEPS = ['verify', 'install', 'restart', 'check'] as const;

function stepIndex(run: UpdateRun): number {
  if (run.phase === 'preparing') return 0;
  if (run.phase === 'restarting') return 2;
  if (run.phase === 'success') return 4;
  // Installing: before the restart the updater is working; after it, the new gateway is being checked.
  return run.sawDown ? 3 : 1;
}

function Overlay({ run, onClose }: { run: UpdateRun; onClose: () => void }) {
  const { t } = useI18n();
  const [now, setNow] = useState(() => Date.now());
  const done = isFinal(run.phase);
  useEffect(() => {
    if (done) return;
    const h = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(h);
  }, [done]);
  // Keep keyboard focus inside the page (what is behind cannot be used while the gateway restarts).
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => { box.current?.focus(); }, [run.phase]);

  const at = stepIndex(run);
  const failed = run.phase === 'failed';
  const success = run.phase === 'success';
  // After a successful update the page is loaded again from the new gateway (new scripts); the run is cleared first,
  // so the reload cannot come back here. When the session ended, the reload shows the normal sign-in page.
  const reload = () => { onClose(); window.location.reload(); };

  return (
    // While the request is still open the page stays below the dialogs: the re-authentication dialog must be usable.
    <div className={cn('fixed inset-0 flex items-center justify-center overflow-auto bg-background/95 p-4 backdrop-blur-sm', run.phase === 'preparing' ? 'z-40' : 'z-[200]')}
      role="alertdialog" aria-modal="true" aria-labelledby="pupd-ov-title" aria-describedby="pupd-ov-text">
      <div ref={box} tabIndex={-1} className="w-full max-w-lg rounded-xl border bg-card p-6 shadow-xl outline-none sm:p-8">
        <div className="flex flex-col items-center text-center">
          <div className="relative mb-5 flex size-24 items-center justify-center" aria-hidden="true">
            {!done && <>
              <span className="absolute inset-0 rounded-full border-2 border-primary/15" />
              <span className="absolute inset-0 animate-spin rounded-full border-2 border-transparent border-t-primary [animation-duration:1.6s] motion-reduce:animate-none" />
              <span className="absolute inset-3 animate-spin rounded-full border-2 border-transparent border-b-primary/50 [animation-direction:reverse] [animation-duration:2.6s] motion-reduce:animate-none" />
              <span className="absolute inset-6 animate-pulse rounded-full bg-primary/10 motion-reduce:animate-none" />
            </>}
            {success ? <CircleCheck className="size-16 text-success" /> : failed ? <CircleAlert className="size-16 text-destructive" /> : <ArrowUpCircle className="relative size-9 text-primary" />}
          </div>
          <h1 id="pupd-ov-title" className="text-lg font-semibold">
            {success ? t('pupd.ov.successTitle', { version: run.target }) : failed ? t('pupd.ov.failedTitle') : t('pupd.ov.title')}
          </h1>
          <p id="pupd-ov-text" className="mt-1.5 text-sm text-muted-foreground" aria-live="polite">
            {success ? t(run.signedOut ? 'pupd.ov.successSignedOut' : 'pupd.ov.success', { version: run.target, from: run.from })
              : failed ? t(run.failure === 'rolled_back' ? 'pupd.ov.rolledBack' : 'pupd.ov.failed', { version: run.target, from: run.from })
              : t(('pupd.ov.phase.' + run.phase) as TKey, { version: run.target })}
          </p>
        </div>

        {!failed && (
          <ol className="mt-6 space-y-2 text-sm">
            {STEPS.map((s, i) => {
              const state = i < at ? 'done' : i === at ? 'now' : 'todo';
              return (
                <li key={s} className={cn('flex items-center gap-2.5', state === 'todo' && 'text-muted-foreground')} aria-current={state === 'now' ? 'step' : undefined}>
                  <span className={cn('flex size-5 shrink-0 items-center justify-center rounded-full border text-[10px]',
                    state === 'done' && 'border-success bg-success/15 text-success', state === 'now' && 'border-primary text-primary')}>
                    {state === 'done' ? <Check className="size-3" /> : state === 'now' ? <Loader2 className="size-3 animate-spin motion-reduce:animate-none" /> : i + 1}
                  </span>
                  <span className={cn(state === 'now' && 'font-medium')}>{t(('pupd.ov.step.' + s) as TKey)}</span>
                </li>
              );
            })}
          </ol>
        )}

        {failed && run.message && (
          <div className="mt-5 rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-left text-xs [overflow-wrap:anywhere]" role="alert">{run.message}</div>
        )}

        {!done && (
          <div className="mt-6 space-y-2 border-t pt-4 text-xs text-muted-foreground">
            <p>{t('pupd.ov.connection')}</p>
            <p className="tabular">{t('pupd.ov.elapsed', { time: formatDuration(Math.max(0, now - run.startedAt)) })}</p>
            {isSlow(run, now) && (
              <div className="rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-foreground">
                <p>{t('pupd.ov.slow')}</p>
                <Button size="sm" variant="outline" className="mt-2" onClick={onClose}>{t('pupd.ov.stopWaiting')}</Button>
              </div>
            )}
          </div>
        )}

        {done && (
          <div className="mt-6 flex justify-center gap-2">
            {success
              ? <Button onClick={reload}>{t(run.signedOut ? 'pupd.ov.signIn' : 'pupd.ov.reload')}</Button>
              : <Button variant="outline" onClick={run.signedOut ? reload : onClose}>{t(run.signedOut ? 'pupd.ov.signIn' : 'common.close')}</Button>}
          </div>
        )}
      </div>
    </div>
  );
}
