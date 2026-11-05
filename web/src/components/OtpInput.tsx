import { ChevronFirst, ChevronLast, ChevronLeft, ChevronRight } from 'lucide-react';
import { useEffect, useRef, type KeyboardEvent, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { NativeSelect } from '@/components/ui/native-select';
import { useI18n } from '@/i18n';
import { cn } from '@/lib/utils';

/** Six separate digit boxes; paste / autofill of the whole code is supported. */
export function OtpInput({ value, onChange, onComplete, autoFocus, idPrefix = 'otp', label, className }: {
  value: string; onChange: (v: string) => void; onComplete?: (v: string) => void; autoFocus?: boolean; idPrefix?: string; label: string; className?: string;
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
    if (typed.length > 1) {
      const clean = set(typed);
      refs.current[Math.min(clean.length, 5)]?.focus();
      return;
    }
    const arr = [...digits];
    arr[i] = typed;
    set(arr.join('').slice(0, 6));
    refs.current[Math.min(i + 1, 5)]?.focus();
  };

  const onKey = (i: number, e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Backspace') {
      e.preventDefault();
      const arr = [...digits];
      if (arr[i]) arr[i] = '';
      else if (i > 0) { arr[i - 1] = ''; refs.current[i - 1]?.focus(); }
      onChange(arr.join(''));
    } else if (e.key === 'ArrowLeft' && i > 0) refs.current[i - 1]?.focus();
    else if (e.key === 'ArrowRight' && i < 5) refs.current[i + 1]?.focus();
  };

  return (
    <div className={cn('otp flex gap-2', className)} role="group" aria-label={label}>
      {digits.map((d, i) => (
        <input key={i} id={`${idPrefix}-${i}`} ref={(el) => { refs.current[i] = el; }}
          className={cn('otp-box size-11 rounded-lg border border-input bg-background text-center font-mono text-lg font-semibold outline-none transition-colors',
            'focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 dark:bg-input/30', i === 2 && 'mr-2')}
          inputMode="numeric" pattern="[0-9]*" maxLength={i === 0 ? 6 : 1} autoComplete={i === 0 ? 'one-time-code' : 'off'} aria-label={`${label} ${i + 1}/6`}
          value={d} onChange={(e) => onInput(i, e.target.value)} onKeyDown={(e) => onKey(i, e)} onFocus={(e) => e.target.select()}
          onPaste={(e) => { e.preventDefault(); const c = set(e.clipboardData.getData('text')); refs.current[Math.min(c.length, 5)]?.focus(); }} />
      ))}
    </div>
  );
}

export function Pager({ total, skip, take, onChange, children }: {
  total: number; skip: number; take: number; onChange: (skip: number, take: number) => void; children?: ReactNode;
}) {
  const { t } = useI18n();
  const from = total === 0 ? 0 : skip + 1;
  const to = Math.min(skip + take, total);
  return (
    <div className="pager flex flex-wrap items-center gap-2 border-b bg-panel px-4 py-2">
      {children}
      <span className="flex-1" />
      <label className="text-xs text-muted-foreground" htmlFor="page-size">{t('pager.size')}</label>
      <NativeSelect id="page-size" value={take} onChange={(e) => onChange(0, Number(e.target.value))} className="w-20">
        {[25, 50, 100, 200].map((n) => <option key={n} value={n}>{n}</option>)}
      </NativeSelect>
      <span className="text-xs text-muted-foreground tabular">{t('pager.range', { from, to, total })}</span>
      <Button variant="outline" size="icon-sm" onClick={() => onChange(0, take)} disabled={skip === 0} aria-label={t('pager.first')}><ChevronFirst /></Button>
      <Button variant="outline" size="icon-sm" onClick={() => onChange(Math.max(0, skip - take), take)} disabled={skip === 0} aria-label={t('pager.prev')}><ChevronLeft /></Button>
      <Button variant="outline" size="icon-sm" onClick={() => onChange(skip + take, take)} disabled={to >= total} aria-label={t('pager.next')}><ChevronRight /></Button>
      <Button variant="outline" size="icon-sm" onClick={() => onChange(Math.max(0, Math.floor((total - 1) / take) * take), take)} disabled={to >= total} aria-label={t('pager.last')}><ChevronLast /></Button>
    </div>
  );
}
