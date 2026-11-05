import { useState, type FormEvent, type ReactNode } from 'react';
import { ApiError, post, resetCsrf } from '../api/client';
import { TopControls } from '../components/Layout';
import { CopyButton, ErrorAlert } from '../components/ui';
import { OtpInput } from '../components/widgets';
import { useI18n } from '../i18n';
import { useSession } from '../session';

function AuthFrame({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="auth-page">
      <div className="card auth-card">
        <div className="row" style={{ justifyContent: 'flex-end', marginBottom: 8 }}><TopControls /></div>
        <h1><img src="/favicon.svg" alt="" width={28} height={28} />{title}</h1>
        {children}
      </div>
    </div>
  );
}

function PasswordFields({ value, onChange, confirm, onConfirm, minLength, label }: {
  value: string; onChange: (v: string) => void; confirm: string; onConfirm: (v: string) => void; minLength: number; label: string;
}) {
  const { t } = useI18n();
  return (
    <>
      <div className="field">
        <label htmlFor="pw-new">{label}</label>
        <input id="pw-new" type="password" autoComplete="new-password" value={value} maxLength={256} onChange={(e) => onChange(e.target.value)} />
        <div className="hint">{t('pw.rules', { n: minLength })} {value.length > 0 && `(${[...value].length})`}</div>
      </div>
      <div className="field">
        <label htmlFor="pw-confirm">{t('setup.confirm')}</label>
        <input id="pw-confirm" type="password" autoComplete="new-password" value={confirm} maxLength={256} onChange={(e) => onConfirm(e.target.value)} />
        {confirm && value !== confirm && <div className="hint" role="alert">{t('pw.mismatch')}</div>}
      </div>
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
    <AuthFrame title={t('setup.title')}>
      <p className="muted">{t('setup.help')}</p>
      <ErrorAlert error={error} />
      <form onSubmit={submit}>
        <div className="field">
          <label htmlFor="setup-token">{t('setup.token')}</label>
          <input id="setup-token" autoComplete="off" spellCheck={false} value={token} onChange={(e) => setToken(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="setup-user">{t('login.user')}</label>
          <input id="setup-user" autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="setup-dn">{t('setup.displayName')}</label>
          <input id="setup-dn" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        </div>
        <PasswordFields value={password} onChange={setPassword} confirm={confirm} onConfirm={setConfirm} minLength={15} label={t('login.password')} />
        <button className="primary" type="submit" disabled={busy || !token || !userName || password.length < 15 || password !== confirm}>{t('setup.submit')}</button>
      </form>
    </AuthFrame>
  );
}

export function LoginPage() {
  const { t } = useI18n();
  const { refresh } = useSession();
  const [mode, setMode] = useState<'login' | 'recover'>('login');
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
      if (r.status === 'mfa_required' && r.challenge) {
        setChallenge(r.challenge);
      } else {
        resetCsrf();
        await refresh();
      }
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
    <AuthFrame title={challenge ? t('login.mfaTitle') : t('login.title')}>
      {!challenge && (
        <div className="tabs" role="tablist">
          <button type="button" role="tab" aria-selected className="active">{t('login.local')}</button>
          <button type="button" role="tab" aria-selected={false} disabled title={t('login.entraLater')}>{t('login.entra')}</button>
        </div>
      )}
      {error && <div className="alert danger" role="alert">{error}</div>}
      {!challenge ? (
        <form onSubmit={submitPassword}>
          <div className="field">
            <label htmlFor="login-user">{t('login.user')}</label>
            <input id="login-user" autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} autoFocus />
          </div>
          <div className="field">
            <label htmlFor="login-pw">{t('login.password')}</label>
            <input id="login-pw" type="password" autoComplete="current-password" value={password} maxLength={1024} onChange={(e) => setPassword(e.target.value)} />
          </div>
          <div className="row">
            <button className="primary" type="submit" disabled={busy || !userName || !password}>{t('login.submit')}</button>
            <span className="spacer" />
            <button type="button" className="link" onClick={() => setMode('recover')}>{t('login.recover')}</button>
          </div>
        </form>
      ) : (
        <form onSubmit={submitMfa}>
          {useRecovery ? (
            <div className="field">
              <label htmlFor="login-code">{t('login.recoveryCode')}</label>
              <input id="login-code" autoComplete="off" maxLength={20} value={code} onChange={(e) => setCode(e.target.value)} autoFocus />
            </div>
          ) : (
            <div className="otp-center">
              <p className="muted" style={{ textAlign: 'center' }}>{t('login.codeHelp')}</p>
              <OtpInput label={t('login.code')} value={code} onChange={setCode} autoFocus idPrefix="login-otp"
                onComplete={(v) => void submitMfa(undefined, v)} />
            </div>
          )}
          <div className="row">
            <button className="primary" type="submit" disabled={busy || !code}>{t('login.verify')}</button>
            <span className="spacer" />
            <button type="button" className="link" onClick={() => { setUseRecovery(!useRecovery); setCode(''); }}>
              {useRecovery ? t('login.useCode') : t('login.useRecovery')}
            </button>
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
    <AuthFrame title={t('recover.title')}>
      <p className="muted small">{t('recover.help')}</p>
      <ErrorAlert error={error} />
      {done ? (
        <>
          <div className="alert ok">{t('recover.done')}</div>
          <button className="primary" type="button" onClick={onDone}>{t('login.title')}</button>
        </>
      ) : (
        <form onSubmit={submit}>
          <div className="field">
            <label htmlFor="rec-token">{t('recover.token')}</label>
            <input id="rec-token" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} />
          </div>
          <div className="field">
            <label htmlFor="rec-user">{t('login.user')}</label>
            <input id="rec-user" autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} />
          </div>
          <PasswordFields value={password} onChange={setPassword} confirm={confirm} onConfirm={setConfirm} minLength={15} label={t('recover.newPassword')} />
          <div className="row">
            <button className="primary" type="submit" disabled={!token || !userName || password !== confirm || password.length < 15}>{t('recover.submit')}</button>
            <button type="button" onClick={onDone}>{t('common.cancel')}</button>
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
    <form onSubmit={submit}>
      {required && <div className="alert warn">{t('pw.changeRequired')}</div>}
      {ok && <div className="alert ok">{t('pw.changed')}</div>}
      <ErrorAlert error={error} />
      <div className="field">
        <label htmlFor="pw-current">{t('pw.current')}</label>
        <input id="pw-current" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />
      </div>
      <PasswordFields value={password} onChange={setPassword} confirm={confirm} onConfirm={setConfirm} minLength={min} label={t('pw.new')} />
      <button className="primary" type="submit" disabled={!current || password !== confirm || [...password].length < min}>{t('pw.changeTitle')}</button>
    </form>
  );
}

export function ForcedPasswordChangePage() {
  const { t } = useI18n();
  const { logout } = useSession();
  return (
    <AuthFrame title={t('pw.changeTitle')}>
      <ChangePasswordForm required />
      <p><button type="button" className="link" onClick={() => void logout()}>{t('nav.logout')}</button></p>
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
      <div>
        <h2>{t('mfa.recoveryTitle')}</h2>
        <p className="muted">{t('mfa.recoveryHelp')}</p>
        <div className="codes">{codes.map((c) => <span key={c}>{c}</span>)}</div>
        <div className="row" style={{ marginTop: 12 }}>
          <CopyButton text={codes.join('\n')} />
          <button className="primary" type="button" onClick={async () => { setCodes(null); await refresh(); onDone?.(); }}>{t('mfa.saved')}</button>
        </div>
      </div>
    );
  }

  return (
    <div>
      {required && <div className="alert warn">{t('mfa.enrollRequired')}</div>}
      <ErrorAlert error={error} />
      {!enroll ? (
        <button className="primary" type="button" onClick={() => void start()}>{t('mfa.start')}</button>
      ) : (
        <form onSubmit={confirmCode}>
          <p className="muted">{t('mfa.scan')}</p>
          <img className="qr" src={enroll.qrSvgDataUri} alt="TOTP QR code" />
          <div className="field">
            <label>{t('mfa.key')}</label>
            <div className="secret">{enroll.secret.replace(/(.{4})/g, '$1 ').trim()}</div>
          </div>
          <div className="field">
            <label>{t('mfa.firstCode')}</label>
            <OtpInput label={t('mfa.firstCode')} value={code} onChange={setCode} idPrefix="mfa-otp" />
          </div>
          <button className="primary" type="submit" disabled={code.length !== 6}>{t('mfa.confirm')}</button>
        </form>
      )}
    </div>
  );
}

export function ForcedMfaPage() {
  const { t } = useI18n();
  const { logout } = useSession();
  return (
    <AuthFrame title={t('mfa.enrollTitle')}>
      <MfaEnrollment required />
      <p><button type="button" className="link" onClick={() => void logout()}>{t('nav.logout')}</button></p>
    </AuthFrame>
  );
}
