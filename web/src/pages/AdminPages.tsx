import { useState, type FormEvent } from 'react';
import { del, get, post, put } from '../api/client';
import type { Assignment, AuditEntry, Host, Role, SecuritySettings, UserRow, Vm } from '../api/types';
import { CopyButton, Dialog, ErrorAlert, Loading, useLoad } from '../components/ui';
import { useI18n, type TKey } from '../i18n';
import { useSession } from '../session';
import { ChangePasswordForm, MfaEnrollment } from './AuthPages';

export function UsersPage() {
  const { t, fmtDate } = useI18n();
  const { me, can } = useSession();
  const users = useLoad(() => (can('users.manage') ? get<UserRow[]>('/api/v1/users') : Promise.resolve([] as UserRow[])));
  const roles = useLoad(() => (can('roles.manage') ? get<Role[]>('/api/v1/roles') : Promise.resolve([] as Role[])));
  const assignments = useLoad(() => (can('roles.manage') ? get<Assignment[]>('/api/v1/role-assignments') : Promise.resolve([] as Assignment[])));
  const [error, setError] = useState<unknown>(null);
  const [temp, setTemp] = useState<{ name: string; password: string } | null>(null);
  const [adding, setAdding] = useState(false);
  const [newName, setNewName] = useState('');
  const [assigning, setAssigning] = useState(false);

  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try { await fn(); users.reload(); assignments.reload(); } catch (e) { setError(e); }
  };

  const createUser = (e: FormEvent) => {
    e.preventDefault();
    void act(async () => {
      const r = await post<{ userName: string; temporaryPassword: string }>('/api/v1/users', { userName: newName });
      setTemp({ name: r.userName, password: r.temporaryPassword });
      setAdding(false);
      setNewName('');
    });
  };

  return (
    <>
      <h1>{t('nav.users')}</h1>
      <ErrorAlert error={error ?? users.error} />
      {temp && (
        <div className="alert warn">{t('users.temp', { name: temp.name })} <span className="secret">{temp.password}</span> <CopyButton text={temp.password} />
          <button type="button" className="link" onClick={() => setTemp(null)}> {t('common.close')}</button></div>
      )}
      {can('users.manage') && (
        <>
          <div className="row"><h2>{t('users.title')}</h2><span className="spacer" />
            <button type="button" className="primary" onClick={() => setAdding(true)}>{t('users.add')}</button></div>
          <p className="muted small">{t('users.noRole')}</p>
          {users.loading && !users.data ? <Loading /> : (
            <div className="card table-wrap"><table>
              <thead><tr><th>{t('login.user')}</th><th>{t('users.enabled')}</th><th>{t('users.mfa')}</th><th>{t('users.lastLogin')}</th><th /></tr></thead>
              <tbody>
                {(users.data ?? []).map((u) => (
                  <tr key={u.id}>
                    <td>{u.userName}{u.displayName && <span className="muted"> · {u.displayName}</span>}{u.mustChangePassword && <span className="badge warn"> pw</span>}{u.isBreakGlass && <span className="badge danger"> break-glass</span>}</td>
                    <td>{u.isEnabled ? t('common.yes') : <span className="badge danger">{t('common.no')}</span>}</td>
                    <td>{u.mfaEnabled ? <span className="badge ok">TOTP</span> : '—'}</td>
                    <td className="small">{u.lastLoginAt ? fmtDate(u.lastLoginAt) : t('common.never')}</td>
                    <td>{u.id !== me?.id && (
                      <div className="row">
                        <button type="button" onClick={() => void act(() => post(`/api/v1/users/${u.id}/${u.isEnabled ? 'disable' : 'enable'}`))}>{u.isEnabled ? t('users.disable') : t('users.enable')}</button>
                        <button type="button" onClick={() => void act(async () => {
                          const r = await post<{ temporaryPassword: string }>(`/api/v1/users/${u.id}/reset-password`);
                          setTemp({ name: u.userName, password: r.temporaryPassword });
                        })}>{t('users.resetPassword')}</button>
                        {u.mfaEnabled && <button type="button" onClick={() => void act(() => post(`/api/v1/users/${u.id}/reset-mfa`))}>{t('users.resetMfa')}</button>}
                      </div>
                    )}</td>
                  </tr>
                ))}
              </tbody>
            </table></div>
          )}
        </>
      )}

      {can('roles.manage') && (
        <>
          <div className="row"><h2>{t('roles.assignments')}</h2><span className="spacer" />
            <button type="button" className="primary" onClick={() => setAssigning(true)}>{t('roles.assign')}</button></div>
          <p className="muted small">{t('roles.denyHelp')}</p>
          <div className="card table-wrap"><table>
            <thead><tr><th>{t('login.user')}</th><th>{t('roles.role')}</th><th>{t('roles.scope')}</th><th>{t('roles.effect')}</th><th /></tr></thead>
            <tbody>
              {(assignments.data ?? []).map((a) => (
                <tr key={a.id}>
                  <td>{a.userName}</td><td>{a.roleName}</td>
                  <td>{a.scopeType}{a.scopeType !== 'Global' && `: ${a.scopeName ?? a.scopeId}`}</td>
                  <td><span className={'badge ' + (a.effect === 'Deny' ? 'danger' : 'ok')}>{a.effect}</span></td>
                  <td>{a.userId !== me?.id && <button type="button" onClick={() => void act(() => del(`/api/v1/role-assignments/${a.id}`))}>{t('roles.remove')}</button>}</td>
                </tr>
              ))}
            </tbody>
          </table></div>
          <h2>{t('roles.catalog')}</h2>
          <div className="card table-wrap"><table><tbody>
            {(roles.data ?? []).map((r) => <tr key={r.id}><td><strong>{r.name}</strong><div className="muted small">{r.description}</div></td><td className="mono small">{r.permissions.join(', ')}</td></tr>)}
          </tbody></table></div>
          <AssignDialog open={assigning} onClose={() => { setAssigning(false); assignments.reload(); }} users={users.data ?? []} roles={roles.data ?? []} />
        </>
      )}

      <Dialog open={adding} title={t('users.add')} onClose={() => setAdding(false)}
        actions={<><button type="button" onClick={() => setAdding(false)}>{t('common.cancel')}</button>
          <button type="submit" form="add-user" className="primary" disabled={!newName}>{t('common.save')}</button></>}>
        <form id="add-user" onSubmit={createUser}>
          <div className="field"><label htmlFor="new-user">{t('login.user')}</label><input id="new-user" value={newName} onChange={(e) => setNewName(e.target.value)} /></div>
        </form>
      </Dialog>
    </>
  );
}

function AssignDialog({ open, onClose, users, roles }: { open: boolean; onClose: () => void; users: UserRow[]; roles: Role[] }) {
  const { t } = useI18n();
  const hosts = useLoad(() => (open ? get<Host[]>('/api/v1/hosts') : Promise.resolve([] as Host[])), [open]);
  const vms = useLoad(() => (open ? get<Vm[]>('/api/v1/vms') : Promise.resolve([] as Vm[])), [open]);
  const [userId, setUserId] = useState('');
  const [roleId, setRoleId] = useState('');
  const [scopeType, setScopeType] = useState('Global');
  const [scopeId, setScopeId] = useState('');
  const [effect, setEffect] = useState('Allow');
  const [error, setError] = useState<unknown>(null);

  const submit = async () => {
    setError(null);
    try {
      await post('/api/v1/role-assignments', { userId, roleId, scopeType, scopeId: scopeType === 'Global' ? '' : scopeId, effect });
      onClose();
    } catch (e) { setError(e); }
  };

  return (
    <Dialog open={open} title={t('roles.assign')} onClose={onClose}
      actions={<><button type="button" onClick={onClose}>{t('common.cancel')}</button>
        <button type="button" className="primary" disabled={!userId || !roleId || (scopeType !== 'Global' && !scopeId)} onClick={() => void submit()}>{t('common.save')}</button></>}>
      <ErrorAlert error={error} />
      <div className="field"><label htmlFor="as-user">{t('login.user')}</label>
        <select id="as-user" value={userId} onChange={(e) => setUserId(e.target.value)}><option value="" />{users.map((u) => <option key={u.id} value={u.id}>{u.userName}</option>)}</select></div>
      <div className="field"><label htmlFor="as-role">{t('roles.role')}</label>
        <select id="as-role" value={roleId} onChange={(e) => setRoleId(e.target.value)}><option value="" />{roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}</select></div>
      <div className="field"><label htmlFor="as-scope">{t('roles.scope')}</label>
        <select id="as-scope" value={scopeType} onChange={(e) => { setScopeType(e.target.value); setScopeId(''); }}>
          <option value="Global">Global</option><option value="Host">Host</option><option value="Vm">VM</option>
        </select></div>
      {scopeType === 'Host' && <div className="field"><select aria-label="Host" value={scopeId} onChange={(e) => setScopeId(e.target.value)}><option value="" />{(hosts.data ?? []).map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}</select></div>}
      {scopeType === 'Vm' && <div className="field"><select aria-label="VM" value={scopeId} onChange={(e) => setScopeId(e.target.value)}><option value="" />{(vms.data ?? []).map((v) => <option key={v.id} value={v.id}>{v.name} ({v.hostName})</option>)}</select></div>}
      <div className="field"><label htmlFor="as-effect">{t('roles.effect')}</label>
        <select id="as-effect" value={effect} onChange={(e) => setEffect(e.target.value)}><option>Allow</option><option>Deny</option></select></div>
    </Dialog>
  );
}

export function AuditPage() {
  const { t, fmtDate } = useI18n();
  const [outcome, setOutcome] = useState('');
  const data = useLoad(() => get<{ total: number; items: AuditEntry[] }>(`/api/v1/audit?take=200${outcome ? '&outcome=' + outcome : ''}`), [outcome]);
  const [verify, setVerify] = useState<{ valid: boolean; entriesChecked: number; firstInvalidId: number | null; problem: string | null } | null>(null);
  const [error, setError] = useState<unknown>(null);
  return (
    <>
      <div className="row"><h1>{t('nav.audit')}</h1><span className="spacer" />
        <select aria-label={t('audit.outcome')} value={outcome} onChange={(e) => setOutcome(e.target.value)} style={{ maxWidth: 160 }}>
          <option value="">*</option><option>Success</option><option>Failure</option><option>Denied</option></select>
        <button type="button" onClick={async () => { setError(null); try { setVerify(await post('/api/v1/audit/verify')); } catch (e) { setError(e); } }}>{t('audit.verify')}</button>
        <a className="button" href="/api/v1/audit/export">{t('audit.export')}</a>
      </div>
      <ErrorAlert error={error ?? data.error} />
      {verify && (verify.valid
        ? <div className="alert ok">{t('audit.valid', { n: verify.entriesChecked })}</div>
        : <div className="alert danger">{t('audit.invalid', { id: verify.firstInvalidId ?? '?', problem: verify.problem ?? '' })}</div>)}
      {data.loading && !data.data ? <Loading /> : (
        <div className="card table-wrap"><table>
          <thead><tr><th>{t('audit.time')}</th><th>{t('audit.actor')}</th><th>{t('audit.action')}</th><th>{t('audit.resource')}</th><th>{t('audit.outcome')}</th><th>{t('audit.source')}</th></tr></thead>
          <tbody>
            {(data.data?.items ?? []).map((e) => (
              <tr key={e.id}>
                <td className="small" style={{ whiteSpace: 'nowrap' }}>{fmtDate(e.timestampUtc)}</td>
                <td>{e.actorName ?? '—'}<div className="muted small">{e.authMethod}</div></td>
                <td className="mono small">{e.action}{e.detailsJson && <div className="muted">{e.detailsJson}</div>}</td>
                <td className="small">{e.resourceType}{e.resourceId && <div className="mono muted">{e.resourceId}</div>}</td>
                <td><span className={'badge ' + (e.outcome === 'Success' ? 'ok' : 'danger')}>{e.outcome}</span></td>
                <td className="small">{e.sourceIp}</td>
              </tr>
            ))}
          </tbody>
        </table></div>
      )}
    </>
  );
}

export function SettingsPage() {
  const { t } = useI18n();
  const settings = useLoad(() => get<SecuritySettings>('/api/v1/settings/security'));
  const info = useLoad(() => get<{ version: string; milestone: string; database: string; telemetry: string; dataDirectory: string | null; internetRequired: string }>('/api/v1/system/info'));
  const [draft, setDraft] = useState<SecuritySettings | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const s = draft ?? settings.data;
  if (!s) return settings.error ? <ErrorAlert error={settings.error} /> : <Loading />;
  const set = (patch: Partial<SecuritySettings>) => { setDraft({ ...s, ...patch }); setSaved(false); };
  const save = async () => {
    setError(null);
    try { await put('/api/v1/settings/security', s); setSaved(true); setDraft(null); settings.reload(); } catch (e) { setError(e); }
  };
  return (
    <>
      <h1>{t('nav.settings')}</h1>
      <ErrorAlert error={error} />
      {saved && <div className="alert ok">{t('settings.saved')}</div>}
      <div className="grid cols-2">
        <div className="card">
          <h2 style={{ marginTop: 0 }}>{t('settings.security')}</h2>
          <div className="field"><label htmlFor="s-mfa">{t('settings.mfaFor')}</label>
            <select id="s-mfa" value={s.mfaRequiredFor} onChange={(e) => set({ mfaRequiredFor: e.target.value as SecuritySettings['mfaRequiredFor'] })}>
              {(['None', 'Privileged', 'All'] as const).map((v) => <option key={v} value={v}>{t(('settings.mfa.' + v) as TKey)}</option>)}
            </select></div>
          <div className="field"><label htmlFor="s-idle">{t('settings.idle')}</label><input id="s-idle" type="number" min={5} max={240} value={s.sessionIdleMinutes} onChange={(e) => set({ sessionIdleMinutes: Number(e.target.value) })} /></div>
          <div className="field"><label htmlFor="s-abs">{t('settings.absolute')}</label><input id="s-abs" type="number" min={1} max={72} value={s.sessionAbsoluteHours} onChange={(e) => set({ sessionAbsoluteHours: Number(e.target.value) })} /></div>
          <div className="field"><label htmlFor="s-pw">{t('settings.pwmin')}</label><input id="s-pw" type="number" min={15} max={128} value={s.passwordMinLength} onChange={(e) => set({ passwordMinLength: Number(e.target.value) })} /></div>
          <div className="field"><label htmlFor="s-local">{t('settings.localSignIn')}</label>
            <select id="s-local" value={s.localSignIn} onChange={(e) => set({ localSignIn: e.target.value as SecuritySettings['localSignIn'] })}>
              <option value="allowed">allowed</option><option value="break-glass-only">break-glass-only</option></select></div>
          <button type="button" className="primary" disabled={!draft} onClick={() => void save()}>{t('common.save')}</button>
        </div>
        <div className="card">
          <h2 style={{ marginTop: 0 }}>{t('settings.system')}</h2>
          {info.data && (
            <dl className="kv">
              <dt>Version</dt><dd>{info.data.version} ({info.data.milestone})</dd>
              <dt>Database</dt><dd>{info.data.database}</dd>
              <dt>{t('settings.telemetry')}</dt><dd>{info.data.telemetry}</dd>
              <dt>{t('settings.offline')}</dt><dd>{info.data.internetRequired}</dd>
              {info.data.dataDirectory && <><dt>Data</dt><dd className="mono small">{info.data.dataDirectory}</dd></>}
            </dl>
          )}
        </div>
      </div>
    </>
  );
}

export function AccountPage() {
  const { t, fmtDate } = useI18n();
  const { me, refresh } = useSession();
  const sessions = useLoad(() => get<{ id: string; createdAt: string; lastSeenAt: string; ipAddress: string | null; userAgent: string | null; authMethods: string; current: boolean }[]>('/api/v1/auth/sessions'));
  const [codes, setCodes] = useState<string[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  if (!me) return <Loading />;
  return (
    <>
      <h1>{t('nav.account')}</h1>
      <ErrorAlert error={error} />
      <div className="grid cols-2">
        <div className="card">
          <h2 style={{ marginTop: 0 }}>{t('login.mfaTitle')}</h2>
          {me.mfaEnabled ? (
            <>
              <p className="alert ok">{t('mfa.enabled')} {t('mfa.remaining', { n: me.recoveryCodesRemaining })}</p>
              {codes && <><div className="codes">{codes.map((c) => <span key={c}>{c}</span>)}</div><CopyButton text={codes.join('\n')} /></>}
              <div className="row" style={{ marginTop: 8 }}>
                <button type="button" onClick={async () => { setError(null); try { setCodes((await post<{ recoveryCodes: string[] }>('/api/v1/mfa/recovery-codes')).recoveryCodes); await refresh(); } catch (e) { setError(e); } }}>{t('mfa.regenerate')}</button>
                {!me.mfaRequired && <button type="button" className="danger-outline" onClick={async () => { setError(null); try { await post('/api/v1/mfa/totp/disable'); await refresh(); } catch (e) { setError(e); } }}>{t('mfa.disable')}</button>}
              </div>
            </>
          ) : (
            <><p className="muted">{t('mfa.disabled')}</p><MfaEnrollment /></>
          )}
          <p className="muted small">{t('mfa.webauthn')}</p>
        </div>
        <div className="card">
          <h2 style={{ marginTop: 0 }}>{t('pw.changeTitle')}</h2>
          <ChangePasswordForm />
        </div>
      </div>
      <h2>{t('account.sessions')}</h2>
      <div className="card table-wrap"><table><tbody>
        {(sessions.data ?? []).map((s) => (
          <tr key={s.id}>
            <td className="small">{fmtDate(s.createdAt)} → {fmtDate(s.lastSeenAt)}</td>
            <td className="small">{s.ipAddress}<div className="muted">{s.userAgent}</div></td>
            <td><span className="badge">{s.authMethods}</span></td>
            <td>{s.current ? <span className="badge info">{t('account.current')}</span>
              : <button type="button" onClick={async () => { await del(`/api/v1/auth/sessions/${s.id}`); sessions.reload(); }}>{t('account.revoke')}</button>}</td>
          </tr>
        ))}
      </tbody></table></div>
    </>
  );
}

export function PlannedPage({ titleKey, milestone }: { titleKey: TKey; milestone: string }) {
  const { t } = useI18n();
  return (
    <>
      <h1>{t(titleKey)}</h1>
      <div className="card"><h2 style={{ marginTop: 0 }}>{t('planned.title')}</h2><p>{t('planned.body', { m: milestone })}</p></div>
    </>
  );
}
