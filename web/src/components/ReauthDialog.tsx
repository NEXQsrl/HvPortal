import { useEffect, useRef, useState } from 'react';
import { onReauthRequired, post } from '@/api/client';
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

  const submit = async () => {
    try {
      await post('/api/v1/auth/reauth', { password, code: me?.mfaEnabled ? code : undefined });
      finish(true);
    } catch (e) {
      setError(e);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) finish(false); }}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{t('reauth.title')}</DialogTitle>
          <DialogDescription>{t('reauth.help')}</DialogDescription>
        </DialogHeader>
        <form id="reauth-form" className="space-y-3" onSubmit={(e) => { e.preventDefault(); if (password) void submit(); }}>
          <ErrorAlert error={error} />
          <Field label={t('login.password')} htmlFor="reauth-pw">
            <Input id="reauth-pw" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} autoFocus />
          </Field>
          {me?.mfaEnabled && (
            <Field label={t('login.code')}>
              <OtpInput label={t('login.code')} value={code} onChange={setCode} idPrefix="reauth-otp" />
            </Field>
          )}
        </form>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => finish(false)}>{t('common.cancel')}</Button>
          <Button type="submit" form="reauth-form" disabled={!password}>{t('reauth.submit')}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
