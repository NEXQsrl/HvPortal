import { useCallback, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react';
import type { PowerState } from '../api/types';
import { useI18n, type TKey } from '../i18n';

// ---------------------------------------------------------------- context / kebab menu

export interface MenuItem {
  label: string;
  onSelect: () => void;
  danger?: boolean;
  disabled?: boolean;
  separatorBefore?: boolean;
}

interface MenuState { x: number; y: number; items: MenuItem[] }

/** One menu per page: opened by right click on a row or by the "⋯" button. Closes on Escape, outside click, scroll. */
export function useContextMenu() {
  const [state, setState] = useState<MenuState | null>(null);
  const close = useCallback(() => setState(null), []);

  const onContextMenu = useCallback((e: MouseEvent, items: MenuItem[]) => {
    e.preventDefault();
    e.stopPropagation();
    setState({ x: e.clientX, y: e.clientY, items });
  }, []);

  const onButton = useCallback((e: MouseEvent<HTMLButtonElement>, items: MenuItem[]) => {
    e.stopPropagation();
    const r = e.currentTarget.getBoundingClientRect();
    setState({ x: r.right, y: r.bottom + 4, items });
  }, []);

  const element = state ? <FloatingMenu state={state} onClose={close} /> : null;
  return { onContextMenu, onButton, element, close };
}

function FloatingMenu({ state, onClose }: { state: MenuState; onClose: () => void }) {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState({ left: state.x, top: state.y });

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const w = el.offsetWidth;
    const h = el.offsetHeight;
    setPos({
      left: Math.max(8, Math.min(state.x - (state.x > window.innerWidth / 2 ? w : 0), window.innerWidth - w - 8)),
      top: Math.max(8, Math.min(state.y, window.innerHeight - h - 8)),
    });
    el.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();
  }, [state]);

  useEffect(() => {
    const down = (e: globalThis.MouseEvent) => { if (!ref.current?.contains(e.target as Node)) onClose(); };
    const key = (e: globalThis.KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('mousedown', down);
    window.addEventListener('keydown', key);
    window.addEventListener('resize', onClose);
    window.addEventListener('scroll', onClose, true);
    return () => {
      window.removeEventListener('mousedown', down);
      window.removeEventListener('keydown', key);
      window.removeEventListener('resize', onClose);
      window.removeEventListener('scroll', onClose, true);
    };
  }, [onClose]);

  const onKey = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
    e.preventDefault();
    const buttons = [...(ref.current?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)') ?? [])];
    const i = buttons.indexOf(document.activeElement as HTMLButtonElement);
    const next = e.key === 'ArrowDown' ? (i + 1) % buttons.length : (i - 1 + buttons.length) % buttons.length;
    buttons[next]?.focus();
  };

  return (
    <div ref={ref} className="menu" role="menu" style={{ left: pos.left, top: pos.top }} onKeyDown={onKey} onContextMenu={(e) => e.preventDefault()}>
      {state.items.map((it, i) => (
        <div key={i}>
          {it.separatorBefore && <div className="menu-sep" role="separator" />}
          <button type="button" role="menuitem" className={it.danger ? 'danger-text' : undefined} disabled={it.disabled}
            onClick={() => { onClose(); it.onSelect(); }}>{it.label}</button>
        </div>
      ))}
    </div>
  );
}

export function KebabButton({ onClick, label }: { onClick: (e: MouseEvent<HTMLButtonElement>) => void; label: string }) {
  return <button type="button" className="kebab" aria-label={label} title={label} aria-haspopup="menu" onClick={onClick}>⋯</button>;
}

/** Props that make a table row behave as a link: click / Enter opens, right click opens the menu. */
export function rowProps(open: () => void, onContext: (e: MouseEvent) => void) {
  return {
    className: 'clickable',
    tabIndex: 0,
    onClick: (e: MouseEvent) => { if (!(e.target as HTMLElement).closest('button, a, input, select')) open(); },
    onKeyDown: (e: KeyboardEvent) => { if (e.key === 'Enter' && e.target === e.currentTarget) open(); },
    onContextMenu: onContext,
  };
}

// ---------------------------------------------------------------- icons

export function StateIcon({ state, critical }: { state: PowerState; critical?: boolean }) {
  const { t } = useI18n();
  const label = t(('state.' + state) as TKey) + (critical ? ' (critical)' : '');
  const kind = critical ? 'critical'
    : state === 'Running' ? 'running'
    : state === 'Off' ? 'off'
    : state === 'Paused' ? 'paused'
    : state === 'Saved' ? 'saved'
    : state === 'Unknown' || state === 'Other' ? 'unknown' : 'busy';
  return (
    <span className={'state-icon ' + kind} role="img" aria-label={label} title={label}>
      <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">
        {kind === 'running' && <><circle cx="8" cy="8" r="7.5" /><path d="M6.3 4.8v6.4L11.2 8z" className="fg" /></>}
        {kind === 'off' && <><circle cx="8" cy="8" r="7.5" /><rect x="5.2" y="5.2" width="5.6" height="5.6" rx="1" className="fg" /></>}
        {kind === 'paused' && <><circle cx="8" cy="8" r="7.5" /><rect x="5.2" y="4.8" width="2" height="6.4" className="fg" /><rect x="8.8" y="4.8" width="2" height="6.4" className="fg" /></>}
        {kind === 'saved' && <><circle cx="8" cy="8" r="7.5" /><path d="M5 4.5h4.8L11 5.7v5.8H5z" className="fg" /></>}
        {kind === 'busy' && <><circle cx="8" cy="8" r="7.5" /><path d="M8 3.5a4.5 4.5 0 1 1-4.5 4.5" className="fg-stroke" /></>}
        {kind === 'critical' && <><circle cx="8" cy="8" r="7.5" /><path d="M7.1 3.8h1.8l-.3 5.4H7.4zM7.1 10.6h1.8v1.8H7.1z" className="fg" /></>}
        {kind === 'unknown' && <><circle cx="8" cy="8" r="7.5" /><path d="M6 6.2a2 2 0 1 1 2.8 1.8c-.5.3-.8.6-.8 1.3v.4M8 11.3v.2" className="fg-stroke" /></>}
      </svg>
    </span>
  );
}

export function CheckpointIcon({ count }: { count: number | null }) {
  const { t } = useI18n();
  if (count == null) return <span className="muted" title={t('vm.checkpointsUnknown')}>?</span>;
  if (count === 0) return <span className="muted" aria-label={t('vm.checkpointsNone')} title={t('vm.checkpointsNone')}>—</span>;
  const label = t('vm.checkpointsCount', { n: count });
  return (
    <span className="checkpoint-icon" role="img" aria-label={label} title={label}>
      <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">
        <circle cx="8" cy="8" r="6.2" fill="none" strokeWidth="1.6" />
        <path d="M8 4.6V8l2.4 1.6" fill="none" strokeWidth="1.6" strokeLinecap="round" />
      </svg>
      <span className="checkpoint-count">{count}</span>
    </span>
  );
}

// ---------------------------------------------------------------- OTP input (6 boxes)

export function OtpInput({ value, onChange, onComplete, autoFocus, idPrefix = 'otp', label }: {
  value: string; onChange: (v: string) => void; onComplete?: (v: string) => void; autoFocus?: boolean; idPrefix?: string; label: string;
}) {
  const refs = useRef<(HTMLInputElement | null)[]>([]);
  const digits = Array.from({ length: 6 }, (_, i) => value[i] ?? '');

  useEffect(() => { if (autoFocus) refs.current[0]?.focus(); }, [autoFocus]);

  const set = (next: string) => {
    const clean = next.replace(/\D/g, '').slice(0, 6);
    onChange(clean);
    if (clean.length === 6) onComplete?.(clean);
    return clean;
  };

  const onInput = (i: number, raw: string) => {
    const typed = raw.replace(/\D/g, '');
    if (!typed) return;
    if (typed.length > 1) { // paste or autofill of the whole code
      const clean = set(typed);
      refs.current[Math.min(clean.length, 5)]?.focus();
      return;
    }
    const arr = [...digits];
    arr[i] = typed;
    const joined = arr.join('').slice(0, 6);
    set(joined);
    refs.current[Math.min(i + 1, 5)]?.focus();
  };

  const onKey = (i: number, e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Backspace') {
      e.preventDefault();
      const arr = [...digits];
      if (arr[i]) { arr[i] = ''; } else if (i > 0) { arr[i - 1] = ''; refs.current[i - 1]?.focus(); }
      onChange(arr.join(''));
    } else if (e.key === 'ArrowLeft' && i > 0) {
      refs.current[i - 1]?.focus();
    } else if (e.key === 'ArrowRight' && i < 5) {
      refs.current[i + 1]?.focus();
    }
  };

  return (
    <div className="otp" role="group" aria-label={label}>
      {digits.map((d, i) => (
        <input key={i} id={`${idPrefix}-${i}`} ref={(el) => { refs.current[i] = el; }}
          className="otp-box" inputMode="numeric" pattern="[0-9]*" maxLength={i === 0 ? 6 : 1}
          autoComplete={i === 0 ? 'one-time-code' : 'off'} aria-label={`${label} ${i + 1}/6`}
          value={d} onChange={(e) => onInput(i, e.target.value)} onKeyDown={(e) => onKey(i, e)}
          onFocus={(e) => e.target.select()}
          onPaste={(e) => { e.preventDefault(); const c = set(e.clipboardData.getData('text')); refs.current[Math.min(c.length, 5)]?.focus(); }} />
      ))}
    </div>
  );
}

// ---------------------------------------------------------------- pager

export function Pager({ total, skip, take, onChange, children }: {
  total: number; skip: number; take: number; onChange: (skip: number, take: number) => void; children?: ReactNode;
}) {
  const { t } = useI18n();
  const from = total === 0 ? 0 : skip + 1;
  const to = Math.min(skip + take, total);
  return (
    <div className="pager">
      {children}
      <span className="spacer" />
      <label className="small" htmlFor="page-size">{t('pager.size')}</label>
      <select id="page-size" value={take} onChange={(e) => onChange(0, Number(e.target.value))} style={{ width: 'auto' }}>
        {[25, 50, 100, 200].map((n) => <option key={n} value={n}>{n}</option>)}
      </select>
      <span className="small">{t('pager.range', { from, to, total })}</span>
      <button type="button" onClick={() => onChange(0, take)} disabled={skip === 0} aria-label={t('pager.first')}>«</button>
      <button type="button" onClick={() => onChange(Math.max(0, skip - take), take)} disabled={skip === 0} aria-label={t('pager.prev')}>‹</button>
      <button type="button" onClick={() => onChange(skip + take, take)} disabled={to >= total} aria-label={t('pager.next')}>›</button>
      <button type="button" onClick={() => onChange(Math.max(0, Math.floor((total - 1) / take) * take), take)} disabled={to >= total} aria-label={t('pager.last')}>»</button>
    </div>
  );
}
