import { CircleAlert, CircleCheck, Info, X } from 'lucide-react';
import { useEffect, useState, type ReactNode } from 'react';
import { cn } from '@/lib/utils';

/** Minimal toast stack (no runtime style injection, CSP-safe). */
type Kind = 'success' | 'error' | 'info';
interface ToastItem { id: number; kind: Kind; title: ReactNode; description?: ReactNode }

let seq = 0;
const listeners = new Set<(items: ToastItem[]) => void>();
let items: ToastItem[] = [];

function emit() { listeners.forEach((l) => l(items)); }

function push(kind: Kind, title: ReactNode, description?: ReactNode, ttl = kind === 'error' ? 9000 : 5000) {
  const id = ++seq;
  items = [...items.slice(-4), { id, kind, title, description }];
  emit();
  window.setTimeout(() => dismiss(id), ttl);
  return id;
}

export function dismiss(id: number) {
  items = items.filter((t) => t.id !== id);
  emit();
}

export const toast = {
  success: (title: ReactNode, description?: ReactNode) => push('success', title, description),
  error: (title: ReactNode, description?: ReactNode) => push('error', title, description),
  info: (title: ReactNode, description?: ReactNode) => push('info', title, description),
};

export function Toaster() {
  const [list, setList] = useState<ToastItem[]>(items);
  useEffect(() => {
    listeners.add(setList);
    return () => { listeners.delete(setList); };
  }, []);
  return (
    <div className="pointer-events-none fixed right-4 bottom-4 z-[100] flex w-[min(380px,calc(100vw-2rem))] flex-col gap-2" aria-live="polite">
      {list.map((t) => {
        const Icon = t.kind === 'success' ? CircleCheck : t.kind === 'error' ? CircleAlert : Info;
        return (
          <div key={t.id} role={t.kind === 'error' ? 'alert' : 'status'}
            className={cn('pointer-events-auto flex gap-3 rounded-lg border bg-popover p-3 text-popover-foreground shadow-lg animate-in fade-in slide-in-from-bottom-2',
              t.kind === 'error' && 'border-destructive/40')}>
            <Icon className={cn('mt-0.5 size-4 shrink-0', t.kind === 'success' ? 'text-success' : t.kind === 'error' ? 'text-destructive' : 'text-info')} />
            <div className="min-w-0 flex-1 text-sm">
              <div className="font-medium">{t.title}</div>
              {t.description && <div className="mt-0.5 break-words text-muted-foreground">{t.description}</div>}
            </div>
            <button type="button" className="text-muted-foreground hover:text-foreground" aria-label="Close" onClick={() => dismiss(t.id)}><X className="size-4" /></button>
          </div>
        );
      })}
    </div>
  );
}
