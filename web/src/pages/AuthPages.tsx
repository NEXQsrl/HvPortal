import { KeyRound, LifeBuoy, LockKeyhole, LogIn, LogOut, ShieldCheck, UserRound } from 'lucide-react';
import { useEffect, useState, type FormEvent } from 'react';
import { ApiError, post, resetCsrf } from '@/api/client';
import { IDLE_SIGNED_OUT_KEY } from '@/components/IdleGuard';
import { AuthShell as AuthFrame, IconInput, SubmitButton } from '@/components/auth/AuthShell';
import { CopyButton, ErrorAlert, Notice } from '@/components/common';
import { Field } from '@/components/dialogs';
import { MicrosoftMark } from '@/components/MicrosoftMark';
import { OtpInput } from '@/components/OtpInput';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useI18n, type TKey } from '@/i18n';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';

export { TopControls } from '@/components/auth/AuthShell';

function PasswordFields({ value, onChange, confirm, onConfirm, minLength, label }: {
  value: string; onChange: (v: string) => void; confirm: string; onConfirm: (v: string) => void; minLength: number; label: string;
}) {
  const { t } = useI18n();
  return (
    <>
      <Field label={label} htmlFor="pw-new" hint={t('pw.rules', { n: minLength })} aside={value.length > 0 ? `${[...value].length}` : undefined}>
        <Input id="pw-new" type="password" autoComplete="new-password" value={value} maxLength={256} onChange={(e) => onChange(e.target.value)} />
      </Field>
      <Field label={t('setup.confirm')} htmlFor="pw-confirm" error={confirm && value !== confirm ? t('pw.mismatch') : undefined}>
        <Input id="pw-confirm" type="password" autoComplete="new-password" value={confirm} maxLength={256} onChange={(e) => onConfirm(e.target.value)} />
      </Field>
    </>
  );
}

export function SetupPage() {
  const { t } = useI18n();
  const { refresh } = useSession();
  const [token, setToken] = useState('');
  const [userName, setUserName] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await post('/api/v1/setup', { setupToken: token.trim(), userName, displayName: displayName || undefined, password });
      await refresh();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };

  return (
    <AuthFrame title={t('setup.title')} icon={<KeyRound />}>
      <p className="mb-4 text-sm text-muted-foreground">{t('setup.help')}</p>
      <ErrorAlert error={error} className="signin-alert" />
      <form onSubmit={submit} className="signin-stagger space-y-3">
        <Field label={t('setup.token')} htmlFor="setup-token"><Input id="setup-token" autoComplete="off" spellCheck={false} value={token} onChange={(e) => setToken(e.target.value)} className="font-mono" /></Field>
        <Field label={t('login.user')} htmlFor="setup-user"><Input id="setup-user" autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} /></Field>
        <Field label={t('setup.displayName')} htmlFor="setup-dn"><Input id="setup-dn" value={displayName} onChange={(e) => setDisplayName(e.target.value)} /></Field>
        <PasswordFields value={password} onChange={setPassword} confirm={confirm} onConfirm={setConfirm} minLength={15} label={t('login.password')} />
        <SubmitButton className="mt-1" busy={busy} disabled={busy || !token || !userName || password.length < 15 || password !== confirm}>{t('setup.submit')}</SubmitButton>
      </form>
    </AuthFrame>
  );
}

const ENTRA_ERRORS = ['not_configured', 'not_provisioned', 'disabled', 'denied', 'state', 'invalid_token', 'wrong_tenant', 'unreachable', 'token_redemption_failed', 'session_expired'];

export function LoginPage() {
  const { t } = useI18n();
  const { refresh, state } = useSession();
  const entra = !!state?.providers.includes('entra');
  const localAllowed = state?.localSignIn !== 'break-glass-only';
  const [tab, setTab] = useState<'local' | 'entra'>(entra && !localAllowed ? 'entra' : 'local');
  const [mode, setMode] = useState<'login' | 'recover'>('login');
  // Set by IdleGuard just before it signs the user out: shown once, then forgotten.
  const [idleSignedOut] = useState(() => {
    try { return window.sessionStorage.getItem(IDLE_SIGNED_OUT_KEY) === '1'; } catch { return false; }
  });
  useEffect(() => {
    try { window.sessionStorage.removeItem(IDLE_SIGNED_OUT_KEY); } catch { /* nothing to forget */ }
  }, []);
  const entraError = (() => {
    const e = new URLSearchParams(window.location.search).get('error');
    if (!e?.startsWith('entra_')) return null;
    const code = e.slice(6);
    return t(ENTRA_ERRORS.includes(code) ? (`login.entraError.${code}` as TKey) : 'login.entraError.denied');
  })();
  const signInWithMicrosoft = () => {
    const back = window.location.pathname.startsWith('/login') ? '/' : window.location.pathname + window.location.search;
    window.location.href = '/api/v1/auth/entra/start?returnUrl=' + encodeURIComponent(back);
  };
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [challenge, setChallenge] = useState<string | null>(null);
  const [code, setCode] = useState('');
  const [useRecovery, setUseRecovery] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const explain = (e: unknown) => {
    if (e instanceof ApiError) {
      if (e.code === 'throttled') return t('login.throttled', { s: e.retryAfter ?? 30 });
      if (e.code === 'invalid_credentials') return t('login.invalid');
      return `${e.message} (${e.code})`;
    }
    return String(e);
  };

  const submitPassword = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const r = await post<{ status: string; challenge?: string }>('/api/v1/auth/login', { userName, password });
      if (r.status === 'mfa_required' && r.challenge) setChallenge(r.challenge);
      else { resetCsrf(); await refresh(); }
    } catch (err) {
      setError(explain(err));
    } finally {
      setBusy(false);
      setPassword('');
    }
  };

  const submitMfa = async (e?: FormEvent, value = code) => {
    e?.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await post('/api/v1/auth/login/mfa', useRecovery ? { challenge, recoveryCode: value } : { challenge, code: value });
      resetCsrf();
      await refresh();
    } catch (err) {
      if (err instanceof ApiError && err.code === 'mfa_challenge_invalid') setChallenge(null);
      setError(explain(err));
    } finally {
      setBusy(false);
      setCode('');
    }
  };
  if (mode === 'recover') return <RecoverPage onDone={() => setMode('login')} />;

  return (
    <AuthFrame title={challenge ? t('login.mfaTitle') : t('login.title')} subtitle={challenge ? undefined : t('login2.welcome')} icon={challenge ? <ShieldCheck /> : <LogIn />}>
      {!challenge && (
        <div className="signin-tabs relative mb-5 grid grid-cols-2 rounded-xl p-1 text-sm" role="tablist" data-active={tab}>
          <span className="signin-tabs-thumb" aria-hidden="true" />
          <button type="button" role="tab" aria-selected={tab === 'local'} onClick={() => setTab('local')}
            className={cn('relative rounded-lg py-1.5 transition-colors', tab === 'local' ? 'font-medium text-foreground' : 'text-muted-foreground hover:text-foreground')}>{t('login.local')}</button>
          <button type="button" role="tab" aria-selected={tab === 'entra'} disabled={!entra} title={entra ? undefined : t('login.entraOff')} onClick={() => setTab('entra')}
            className={cn('relative rounded-lg py-1.5 transition-colors', tab === 'entra' ? 'font-medium text-foreground' : 'text-muted-foreground enabled:hover:text-foreground', !entra && 'opacity-60')}>{t('login.entra')}</button>
        </div>
      )}
      {(error ?? entraError) && <Notice kind="danger" className="signin-alert mb-4">{error ?? entraError}</Notice>}
      {idleSignedOut && !error && !entraError && <Notice kind="info" className="signin-alert mb-4">{t('idle.signedOut')}</Notice>}
      {!challenge && tab === 'entra' ? (
        <div className="space-y-3">
          <Button type="button" variant="outline" className="signin-ms h-10 w-full" onClick={signInWithMicrosoft}><MicrosoftMark />{t('login.withMicrosoft')}</Button>
          <p className="text-center text-xs text-muted-foreground">{t('login.entraHelp')}</p>
        </div>
      ) : !challenge ? (
        <form onSubmit={submitPassword} className="signin-stagger space-y-3.5">
          <Field label={t('login.user')} htmlFor="login-user"><IconInput icon={<UserRound />} id="login-user" autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} autoFocus /></Field>
          <Field label={t('login.password')} htmlFor="login-pw"><IconInput icon={<LockKeyhole />} id="login-pw" type="password" autoComplete="current-password" value={password} maxLength={1024} onChange={(e) => setPassword(e.target.value)} /></Field>
          <SubmitButton className="mt-1" busy={busy} arrow disabled={busy || !userName || !password}>{t('login.submit')}</SubmitButton>
          <div className="text-center"><Button type="button" variant="link" size="sm" onClick={() => setMode('recover')}>{t('login.recover')}</Button></div>
        </form>
      ) : (
        <form onSubmit={submitMfa} className="signin-stagger space-y-4">
          {useRecovery ? (
            <Field label={t('login.recoveryCode')} htmlFor="login-code"><IconInput icon={<KeyRound />} id="login-code" autoComplete="off" maxLength={20} value={code} onChange={(e) => setCode(e.target.value)} autoFocus className="font-mono" /></Field>
          ) : (
            <div className="otp-center flex flex-col items-center gap-3">
              <p className="text-center text-sm text-muted-foreground">{t('login.codeHelp')}</p>
              <OtpInput label={t('login.code')} value={code} onChange={setCode} autoFocus idPrefix="login-otp" onComplete={(v) => void submitMfa(undefined, v)} />
            </div>
          )}
          <SubmitButton busy={busy} disabled={busy || !code}>{t('login.verify')}</SubmitButton>
          <div className="text-center">
            <Button type="button" variant="link" size="sm" onClick={() => { setUseRecovery(!useRecovery); setCode(''); }}>{useRecovery ? t('login.useCode') : t('login.useRecovery')}</Button>
          </div>
        </form>
      )}
    </AuthFrame>
  );
}

function RecoverPage({ onDone }: { onDone: () => void }) {
  const { t } = useI18n();
  const [token, setToken] = useState('');
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await post('/api/v1/auth/recover', { token: token.trim(), userName, newPassword: password });
      setDone(true);
    } catch (err) {
      setError(err);
    }
  };

  return (
    <AuthFrame title={t('recover.title')} icon={<LifeBuoy />}>
      <p className="mb-4 text-xs text-muted-foreground">{t('recover.help')}</p>
      <ErrorAlert error={error} className="signin-alert" />
      {done ? (
        <div className="space-y-3"><Notice kind="ok">{t('recover.done')}</Notice><Button type="button" className="h-10 w-full" onClick={onDone}>{t('login.title')}</Button></div>
      ) : (
        <form onSubmit={submit} className="signin-stagger space-y-3">
          <Field label={t('recover.token')} htmlFor="rec-token"><Input id="rec-token" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} className="font-mono" /></Field>
          <Field label={t('login.user')} htmlFor="rec-user"><Input id="rec-user" autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} /></Field>
          <PasswordFields value={password} onChange={setPassword} confirm={confirm} onConfirm={setConfirm} minLength={15} label={t('recover.newPassword')} />
          <div className="flex gap-2 pt-1">
            <Button type="submit" className="h-10 flex-1" disabled={!token || !userName || password !== confirm || password.length < 15}>{t('recover.submit')}</Button>
            <Button type="button" variant="outline" className="h-10" onClick={onDone}>{t('common.cancel')}</Button>
          </div>
        </form>
      )}
    </AuthFrame>
  );
}

export function ChangePasswordForm({ required, onChanged }: { required?: boolean; onChanged?: () => void }) {
  const { t } = useI18n();
  const { me, refresh } = useSession();
  const [current, setCurrent] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [ok, setOk] = useState(false);
  const min = me?.passwordMinLength ?? 15;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await post('/api/v1/auth/password', { currentPassword: current, newPassword: password });
      setOk(true);
      setCurrent(''); setPassword(''); setConfirm('');
      resetCsrf();
      await refresh();
      onChanged?.();
    } catch (err) {
      setError(err);
    }
  };

  return (
    <form onSubmit={submit} className="space-y-3">
      {required && <Notice kind="warn">{t('pw.changeRequired')}</Notice>}
      {ok && <Notice kind="ok">{t('pw.changed')}</Notice>}
      <ErrorAlert error={error} />
      <Field label={t('pw.current')} htmlFor="pw-current"><Input id="pw-current" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} /></Field>
      <PasswordFields value={password} onChange={setPassword} confirm={confirm} onConfirm={setConfirm} minLength={min} label={t('pw.new')} />
      <Button type="submit" disabled={!current || password !== confirm || [...password].length < min}>{t('pw.changeTitle')}</Button>
    </form>
  );
}

export function ForcedPasswordChangePage() {
  const { t } = useI18n();
  const { logout } = useSession();
  return (
    <AuthFrame title={t('pw.changeTitle')} icon={<LockKeyhole />}>
      <ChangePasswordForm required />
      <div className="mt-4 text-center"><Button type="button" variant="link" size="sm" onClick={() => void logout()}><LogOut />{t('nav.logout')}</Button></div>
    </AuthFrame>
  );
}

export function MfaEnrollment({ required, onDone }: { required?: boolean; onDone?: () => void }) {
  const { t } = useI18n();
  const { refresh } = useSession();
  const [enroll, setEnroll] = useState<{ secret: string; qrSvgDataUri: string } | null>(null);
  const [code, setCode] = useState('');
  const [codes, setCodes] = useState<string[] | null>(null);
  const [error, setError] = useState<unknown>(null);

  const start = async () => {
    setError(null);
    try { setEnroll(await post('/api/v1/mfa/totp/enroll')); } catch (e) { setError(e); }
  };
  const confirmCode = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      const r = await post<{ recoveryCodes: string[] }>('/api/v1/mfa/totp/confirm', { code });
      setEnroll(null); // the secret is not kept in memory after activation
      setCodes(r.recoveryCodes);
      resetCsrf();
    } catch (err) { setError(err); }
  };

  if (codes) {
    return (
      <div className="space-y-3">
        <h2 className="text-base font-semibold">{t('mfa.recoveryTitle')}</h2>
        <p className="text-sm text-muted-foreground">{t('mfa.recoveryHelp')}</p>
        <div className="codes grid grid-cols-2 gap-2 rounded-lg border bg-muted p-3 font-mono text-sm">{codes.map((c) => <span key={c}>{c}</span>)}</div>
        <div className="flex gap-2">
          <CopyButton text={codes.join('\n')} />
          <Button type="button" onClick={async () => { setCodes(null); await refresh(); onDone?.(); }}>{t('mfa.saved')}</Button>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {required && <Notice kind="warn">{t('mfa.enrollRequired')}</Notice>}
      <ErrorAlert error={error} />
      {!enroll ? (
        <Button type="button" onClick={() => void start()}><ShieldCheck />{t('mfa.start')}</Button>
      ) : (
        <form onSubmit={confirmCode} className="space-y-3">
          <p className="text-sm text-muted-foreground">{t('mfa.scan')}</p>
          <img className="qr mx-auto size-48 rounded-lg border bg-white p-2" src={enroll.qrSvgDataUri} alt="TOTP QR code" />
          <Field label={t('mfa.key')}>
            <code className="secret block rounded-md border bg-muted px-2 py-1.5 font-mono text-sm break-all">{enroll.secret.replace(/(.{4})/g, '$1 ').trim()}</code>
          </Field>
          <Field label={t('mfa.firstCode')}>
            <OtpInput label={t('mfa.firstCode')} value={code} onChange={setCode} idPrefix="mfa-otp" />
          </Field>
          <Button type="submit" disabled={code.length !== 6}>{t('mfa.confirm')}</Button>
        </form>
      )}
    </div>
  );
}

export function ForcedMfaPage() {
  const { t } = useI18n();
  const { logout } = useSession();
  return (
    <AuthFrame title={t('mfa.enrollTitle')} icon={<ShieldCheck />}>
      <MfaEnrollment required />
      <div className="mt-4 text-center"><Button type="button" variant="link" size="sm" onClick={() => void logout()}><LogOut />{t('nav.logout')}</Button></div>
    </AuthFrame>
  );
}
