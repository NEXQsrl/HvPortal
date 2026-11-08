import { useEffect, useRef, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { ApiError, onReauthRequired, post } from '@/api/client';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { useI18n } from '@/i18n';
import { useSession } from '@/session';
import { ErrorAlert } from './common';
import { Field } from './dialogs';
import { OtpInput } from './OtpInput';

/** Global step-up dialog: opened by the API client when the server answers "reauthentication_required". */
export function ReauthDialog() {
  const { t } = useI18n();
  const { me } = useSession();
  const [open, setOpen] = useState(false);
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [error, setError] = useState<unknown>(null);
  const resolver = useRef<((ok: boolean) => void) | null>(null);

  useEffect(() => {
    onReauthRequired(() => new Promise<boolean>((resolve) => {
      resolver.current = resolve;
      setPassword('');
      setCode('');
      setError(null);
      setOpen(true);
    }));
  }, []);

  const finish = (ok: boolean) => {
    setOpen(false);
    setPassword('');
    setCode('');
    resolver.current?.(ok);
    resolver.current = null;
  };

  // Microsoft (Entra ID) accounts: the portal shows a random code and the user types it again (ADR-038). It confirms
  // the intent of the person at this session; the identity was proven by the Microsoft sign-in of the session.
  const entra = me?.authSource === 'entra';
  const [shown, setShown] = useState<{ code: string; expiresAt: string } | null>(null);
  const newCode = async () => {
    setError(null);
    setCode('');
    try {
      setShown(await post<{ code: string; expiresAt: string }>('/api/v1/auth/reauth/code'));
    } catch (e) {
      setShown(null);
      setError(e);
    }
  };
  useEffect(() => {
    if (open && entra) void newCode();
    if (!open) setShown(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, entra]);

  const submit = async () => {
    try {
      await post('/api/v1/auth/reauth', entra ? { confirmationCode: code } : { password, code: me?.mfaEnabled ? code : undefined });
      finish(true);
    } catch (e) {
      const c = e instanceof ApiError ? e.code : '';
      if (c === 'invalid_confirmation_code') setError(new Error(t('reauth.codeWrong')));
      else if (c === 'confirmation_code_expired' || c === 'confirmation_code_locked') { setError(new Error(t('reauth.codeExpired'))); await newCode(); }
      else setError(e);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) finish(false); }}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{t('reauth.title')}</DialogTitle>
          <DialogDescription>
            {t(me?.authSource === 'entra' ? 'reauth.helpEntra' : me?.mfaEnabled ? 'reauth.helpMfa' : 'reauth.helpPassword', { user: me?.displayName ?? me?.userName ?? '' })}
          </DialogDescription>
        </DialogHeader>
        {entra ? (
          <form id="reauth-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); if (code.trim().length >= 6) void submit(); }}>
            <ErrorAlert error={error} />
            <div className="rounded-lg border bg-muted/40 p-3 text-center">
              <div className="text-xs text-muted-foreground">{t('reauth.codeShown')}</div>
              {/* Shown as text to read, not to copy: the point is typing it again. */}
              <div data-testid="reauth-code" aria-live="polite" className="mt-1 select-none font-mono text-3xl font-semibold tracking-[0.4em]">
                {shown?.code ?? '······'}
              </div>
              <Button type="button" variant="link" size="sm" onClick={() => void newCode()}><RefreshCw />{t('reauth.codeNew')}</Button>
            </div>
            <Field label={t('reauth.codeType')} htmlFor="reauth-confirm">
              <Input id="reauth-confirm" autoComplete="off" autoCapitalize="characters" spellCheck={false} maxLength={12} className="font-mono uppercase tracking-widest"
                value={code} onChange={(e) => setCode(e.target.value)} onPaste={(e) => e.preventDefault()} autoFocus />
            </Field>
            <p className="text-xs text-muted-foreground">{t('reauth.codeNote')}</p>
          </form>
        ) : (
        <form id="reauth-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); if (password) void submit(); }}>
          <ErrorAlert error={error} />
          <Field label={t('reauth.yourPassword', { user: me?.userName ?? '' })} htmlFor="reauth-pw">
            <Input id="reauth-pw" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} autoFocus />
          </Field>
          {me?.mfaEnabled && (
            <Field label={t('reauth.yourCode')}>
              <OtpInput label={t('reauth.yourCode')} value={code} onChange={setCode} idPrefix="reauth-otp" />
            </Field>
          )}
        </form>
        )}
        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => finish(false)}>{t('common.cancel')}</Button>
          <Button type="submit" form="reauth-form" disabled={entra ? code.trim().length < 6 || !shown : !password}>{t('reauth.submit')}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
