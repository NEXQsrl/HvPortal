import { LogOut, TimerReset } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { get } from '@/api/client';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Progress } from '@/components/ui/progress';
import { useI18n } from '@/i18n';
import { useSession } from '@/session';

/** Inactivity before the warning, and length of the countdown (ADR-038). */
export const IDLE_WARN_MINUTES = 15;
export const IDLE_COUNTDOWN_SECONDS = 180;
const STORAGE_KEY = 'hvp.lastActivity';
/** Read by the sign-in page to say why the user is there. */
export const IDLE_SIGNED_OUT_KEY = 'hvp.idleSignedOut';
// User input only: "scroll" is left out because layout changes and programmatic scrolling fire it too (scrolling by the
// user comes with wheel, touch, keyboard or pointer events).
const ACTIVITY_EVENTS = ['pointerdown', 'pointermove', 'keydown', 'wheel', 'touchstart'] as const;

function readShared(): number {
  try {
    const v = Number(window.localStorage.getItem(STORAGE_KEY));
    return Number.isFinite(v) ? v : 0;
  } catch {
    return 0;
  }
}

function writeShared(at: number) {
  try { window.localStorage.setItem(STORAGE_KEY, String(at)); } catch { /* storage blocked: this tab only */ }
}

/**
 * Signs the user out after a period without any activity in the portal: after 15 minutes (or 3 minutes before the
 * server idle timeout, when that is shorter) a dialog counts down 3 minutes; only "Stay signed in" keeps the session.
 * Activity in any tab of the portal counts (shared through localStorage). The server keeps its own idle timeout and
 * authorizes every request: this is the user-facing part, so that an unattended browser does not stay signed in while
 * background refreshes keep the session alive.
 */
export function IdleGuard() {
  const { t } = useI18n();
  const { me, logout } = useSession();
  const serverIdle = me?.idleTimeoutMinutes ?? 30;
  const warnAfterMs = Math.max(1, Math.min(IDLE_WARN_MINUTES, serverIdle - 3)) * 60_000;
  const countdownMs = IDLE_COUNTDOWN_SECONDS * 1000;
  const last = useRef(Date.now());
  const warning = useRef(false);
  const lastWrite = useRef(0);
  const signingOut = useRef(false);
  const [remaining, setRemaining] = useState<number | null>(null);

  const markActive = useCallback((force = false) => {
    const now = Date.now();
    last.current = now;
    if (force || now - lastWrite.current > 5000) {
      lastWrite.current = now;
      writeShared(now);
    }
  }, []);

  useEffect(() => {
    markActive(true);
    // While the warning is open, moving the mouse does not count: only the button keeps the session.
    const onActivity = () => { if (!warning.current) markActive(); };
    for (const e of ACTIVITY_EVENTS) window.addEventListener(e, onActivity, { passive: true, capture: true });
    return () => { for (const e of ACTIVITY_EVENTS) window.removeEventListener(e, onActivity, { capture: true }); };
  }, [markActive]);

  useEffect(() => {
    const tick = () => {
      const lastAt = Math.max(last.current, readShared());
      const idle = Date.now() - lastAt;
      if (idle >= warnAfterMs + countdownMs) {
        if (signingOut.current) return;
        signingOut.current = true;
        try { window.sessionStorage.setItem(IDLE_SIGNED_OUT_KEY, '1'); } catch { /* message only */ }
        void logout('idle');
        return;
      }
      if (idle >= warnAfterMs) {
        warning.current = true;
        setRemaining(Math.ceil((warnAfterMs + countdownMs - idle) / 1000));
      } else if (warning.current) {
        // Activity in another tab, or "Stay signed in".
        warning.current = false;
        setRemaining(null);
      }
    };
    const id = window.setInterval(tick, 1000);
    tick();
    return () => window.clearInterval(id);
  }, [warnAfterMs, countdownMs, logout]);

  const stay = () => {
    warning.current = false;
    setRemaining(null);
    markActive(true);
    // Also tells the server that the user is still there (and checks that the session is still valid).
    void get('/api/v1/auth/me').catch(() => undefined);
  };

  const open = remaining !== null;
  const mm = Math.floor((remaining ?? 0) / 60);
  const ss = String((remaining ?? 0) % 60).padStart(2, '0');
  return (
    <Dialog open={open} onOpenChange={() => { /* closed only by its buttons */ }}>
      <DialogContent className="sm:max-w-md" showCloseButton={false} onEscapeKeyDown={(e) => e.preventDefault()} onPointerDownOutside={(e) => e.preventDefault()}
        onInteractOutside={(e) => e.preventDefault()}>
        <DialogHeader>
          <DialogTitle>{t('idle.title')}</DialogTitle>
          <DialogDescription>{t('idle.help', { minutes: Math.round(warnAfterMs / 60_000) })}</DialogDescription>
        </DialogHeader>
        <div className="space-y-2 text-center">
          <div className="text-xs text-muted-foreground">{t('idle.countdown')}</div>
          <div role="timer" aria-live="off" className="font-mono text-4xl font-semibold tabular-nums">{mm}:{ss}</div>
          <Progress value={((remaining ?? 0) * 100) / IDLE_COUNTDOWN_SECONDS} aria-label={t('idle.countdown')} />
        </div>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => { signingOut.current = true; void logout(); }}>
            <LogOut />{t('idle.signOut')}
          </Button>
          <Button type="button" onClick={stay} autoFocus><TimerReset />{t('idle.stay')}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
