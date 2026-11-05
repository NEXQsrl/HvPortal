import { useEffect, useRef, useState } from 'react';
import { onReauthRequired, post } from '../api/client';
import { useI18n } from '../i18n';
import { useSession } from '../session';
import { Dialog, ErrorAlert } from './ui';

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
    <Dialog open={open} title={t('reauth.title')} onClose={() => finish(false)}
      actions={<>
        <button type="button" onClick={() => finish(false)}>{t('common.cancel')}</button>
        <button type="button" className="primary" onClick={() => void submit()} disabled={!password}>{t('reauth.submit')}</button>
      </>}>
      <p className="muted">{t('reauth.help')}</p>
      <ErrorAlert error={error} />
      <div className="field">
        <label htmlFor="reauth-pw">{t('login.password')}</label>
        <input id="reauth-pw" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
      </div>
      {me?.mfaEnabled && (
        <div className="field">
          <label htmlFor="reauth-code">{t('login.code')}</label>
          <input id="reauth-code" inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={code} onChange={(e) => setCode(e.target.value)} />
        </div>
      )}
    </Dialog>
  );
}
