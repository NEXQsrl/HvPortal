import { Download, KeyRound, Plus, ScrollText, Settings, ShieldCheck, UserRound, Users } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { del, get, post, put } from '@/api/client';
import type { Assignment, AuditEntry, Role, SecuritySettings, UserRow } from '@/api/types';
import { CopyButton, ErrorAlert, KeyValue, Loading, Notice, PageHeader, Section, StatusBadge } from '@/components/common';
import { BackupSection } from '@/components/admin/BackupSection';
import { AddUserDialog, CreatedUserNotice, EntraSettingsSection, type CreatedUser } from '@/components/admin/UserAdmin';
import { ActionDialog, Field } from '@/components/dialogs';
import { MicrosoftMark } from '@/components/MicrosoftMark';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { GroupPickerList } from '@/components/ops/GroupDialogs';
import { Pager } from '@/components/OtpInput';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n, type TKey } from '@/i18n';
import { useLoad } from '@/lib/hooks';
import { useSession } from '@/session';
import { ChangePasswordForm, MfaEnrollment } from './AuthPages';

function Page({ icon, title, actions, children }: { icon: ReactNode; title: string; actions?: ReactNode; children: ReactNode }) {
  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={icon} title={title} actions={actions} />
      <div className="min-h-0 flex-1 space-y-3 overflow-auto p-4">{children}</div>
    </div>
  );
}

export function UsersPage() {
  const { t, fmtDate } = useI18n();
  const { me, can } = useSession();
  const users = useLoad(() => (can('users.manage') ? get<UserRow[]>('/api/v1/users') : Promise.resolve([] as UserRow[])));
  const roles = useLoad(() => (can('roles.manage') ? get<Role[]>('/api/v1/roles') : Promise.resolve([] as Role[])));
  const assignments = useLoad(() => (can('roles.manage') ? get<Assignment[]>('/api/v1/role-assignments') : Promise.resolve([] as Assignment[])));
  const [error, setError] = useState<unknown>(null);
  const [temp, setTemp] = useState<{ name: string; password: string } | null>(null);
  const [created, setCreated] = useState<CreatedUser | null>(null);
  const [adding, setAdding] = useState(false);
  const [assigning, setAssigning] = useState(false);

  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try { await fn(); users.reload(); assignments.reload(); } catch (e) { setError(e); }
  };

  return (
    <Page icon={<Users className="size-5" />} title={t('nav.users')}>
      <ErrorAlert error={error ?? users.error} />
      {temp && (
        <Notice kind="warn" title={t('users.temp', { name: temp.name })}>
          <div className="mt-1 flex flex-wrap items-center gap-2"><code className="secret rounded border bg-muted px-2 py-1 font-mono text-foreground">{temp.password}</code>
            <CopyButton text={temp.password} /><Button size="sm" variant="ghost" onClick={() => setTemp(null)}>{t('common.close')}</Button></div>
        </Notice>
      )}
      {created && <CreatedUserNotice user={created} onClose={() => setCreated(null)} />}
      {can('users.manage') && (
        <Section title={t('users.title')} actions={<Button size="sm" onClick={() => setAdding(true)}><Plus />{t('users.add')}</Button>}>
          <p className="px-3 pt-2 text-xs text-muted-foreground">{t('users.noRole')}</p>
          {users.loading && !users.data ? <Loading /> : (
            <Table containerClassName="max-h-[45vh]">
              <TableHeader><TableRow><TableHead>{t('login.user')}</TableHead><TableHead>{t('users.enabled')}</TableHead><TableHead>{t('users.mfa')}</TableHead><TableHead>{t('users.lastLogin')}</TableHead><TableHead /></TableRow></TableHeader>
              <TableBody>
                {(users.data ?? []).map((u) => (
                  <TableRow key={u.id}>
                    <TableCell>
                      <div className="flex flex-wrap items-center gap-1"><span className="font-medium">{u.userName}</span>{u.displayName && <span className="text-muted-foreground">· {u.displayName}</span>}
                        {u.authSource === 'entra'
                          ? <StatusBadge tone="info" title={u.entraBound ? undefined : t('users.entraNotBound')}><MicrosoftMark className="size-3" />Microsoft{!u.entraBound && ' *'}</StatusBadge>
                          : <StatusBadge tone="muted">{t('users.typeLocal')}</StatusBadge>}
                        {u.mustChangePassword && <StatusBadge tone="warn">pw</StatusBadge>}{u.isBreakGlass && <StatusBadge tone="danger">break-glass</StatusBadge>}</div>
                    </TableCell>
                    <TableCell>{u.isEnabled ? t('common.yes') : <StatusBadge tone="danger">{t('common.no')}</StatusBadge>}</TableCell>
                    <TableCell>{u.authSource === 'entra' ? <span className="text-xs text-muted-foreground">Entra ID</span>
                      : u.mfaEnabled ? <StatusBadge tone="ok">TOTP</StatusBadge> : u.mfaEnrollmentRequired ? <StatusBadge tone="warn">{t('users.mfaPending')}</StatusBadge> : '—'}</TableCell>
                    <TableCell className="text-xs whitespace-nowrap tabular">{u.lastLoginAt ? fmtDate(u.lastLoginAt) : t('common.never')}</TableCell>
                    <TableCell className="text-right whitespace-nowrap">{u.id !== me?.id && (
                      <div className="flex justify-end gap-1">
                        <Button size="xs" variant="outline" onClick={() => void act(() => post(`/api/v1/users/${u.id}/${u.isEnabled ? 'disable' : 'enable'}`))}>{u.isEnabled ? t('users.disable') : t('users.enable')}</Button>
                        {u.authSource !== 'entra' && <Button size="xs" variant="outline" onClick={() => void act(async () => {
                          const r = await post<{ temporaryPassword: string }>(`/api/v1/users/${u.id}/reset-password`);
                          setTemp({ name: u.userName, password: r.temporaryPassword });
                        })}>{t('users.resetPassword')}</Button>}
                        {u.mfaEnabled && <Button size="xs" variant="outline" onClick={() => void act(() => post(`/api/v1/users/${u.id}/reset-mfa`))}>{t('users.resetMfa')}</Button>}
                      </div>
                    )}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </Section>
      )}

      {can('roles.manage') && (
        <>
          <Section title={t('roles.assignments')} actions={<Button size="sm" onClick={() => setAssigning(true)}><Plus />{t('roles.assign')}</Button>}>
            <p className="px-3 pt-2 text-xs text-muted-foreground">{t('roles.denyHelp')} {t('group.inheritHelp')}</p>
            <Table containerClassName="max-h-[45vh]">
              <TableHeader><TableRow><TableHead>{t('login.user')}</TableHead><TableHead>{t('roles.role')}</TableHead><TableHead>{t('roles.scope')}</TableHead><TableHead>{t('roles.effect')}</TableHead><TableHead /></TableRow></TableHeader>
              <TableBody>
                {(assignments.data ?? []).map((a) => (
                  <TableRow key={a.id}>
                    <TableCell className="font-medium">{a.userName}</TableCell>
                    <TableCell>{a.roleName}</TableCell>
                    <TableCell>{a.scopeType}{a.scopeType !== 'Global' && `: ${a.scopeName ?? a.scopeId}`}</TableCell>
                    <TableCell><StatusBadge tone={a.effect === 'Deny' ? 'danger' : 'ok'}>{a.effect}</StatusBadge></TableCell>
                    <TableCell className="text-right">{a.userId !== me?.id && <Button size="xs" variant="ghost" className="text-destructive" onClick={() => void act(() => del(`/api/v1/role-assignments/${a.id}`))}>{t('roles.remove')}</Button>}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Section>
          <Section title={t('roles.catalog')}>
            <Table containerClassName="max-h-[45vh]">
              <TableBody>
                {(roles.data ?? []).map((r) => (
                  <TableRow key={r.id}>
                    <TableCell className="w-64 align-top"><div className="font-medium">{r.name}</div><div className="text-xs text-muted-foreground">{r.description}</div></TableCell>
                    <TableCell><div className="flex flex-wrap gap-1">{r.permissions.map((p) => <code key={p} className="rounded bg-muted px-1.5 py-0.5 font-mono text-[11px]">{p}</code>)}</div></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Section>
          {assigning && <AssignDialog onClose={() => { setAssigning(false); assignments.reload(); }} users={users.data ?? []} roles={roles.data ?? []} />}
        </>
      )}
      {adding && (
        <AddUserDialog roles={roles.data ?? []} onClose={() => setAdding(false)} onCreated={(u) => { setCreated(u); users.reload(); assignments.reload(); }} />
      )}
    </Page>
  );
}

function AssignDialog({ onClose, users, roles }: { onClose: () => void; users: UserRow[]; roles: Role[] }) {
  const { t } = useI18n();
  const inv = useInventory();
  const [userId, setUserId] = useState('');
  const [roleId, setRoleId] = useState('');
  const [scopeType, setScopeType] = useState('Global');
  const [scopeId, setScopeId] = useState('');
  const [effect, setEffect] = useState('Allow');
  return (
    <ActionDialog title={t('roles.assign')} confirmLabel={t('common.save')} onClose={onClose}
      disabled={!userId || !roleId || (scopeType !== 'Global' && !scopeId)}
      onConfirm={async () => { await post('/api/v1/role-assignments', { userId, roleId, scopeType, scopeId: scopeType === 'Global' ? '' : scopeId, effect }); }}>
      <Field label={t('login.user')} htmlFor="as-user">
        <NativeSelect id="as-user" value={userId} onChange={(e) => setUserId(e.target.value)}><option value="" />{users.map((u) => <option key={u.id} value={u.id}>{u.userName}</option>)}</NativeSelect>
      </Field>
      <Field label={t('roles.role')} htmlFor="as-role">
        <NativeSelect id="as-role" value={roleId} onChange={(e) => setRoleId(e.target.value)}><option value="" />{roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}</NativeSelect>
      </Field>
      <Field label={t('roles.scope')} htmlFor="as-scope">
        <NativeSelect id="as-scope" value={scopeType} onChange={(e) => { setScopeType(e.target.value); setScopeId(''); }}>
          <option value="Global">Global</option><option value="Host">Host</option><option value="Group">{t('group.title')}</option><option value="Vm">VM</option>
        </NativeSelect>
      </Field>
      {scopeType === 'Host' && <NativeSelect aria-label="Host" value={scopeId} onChange={(e) => setScopeId(e.target.value)}><option value="" />{inv.hosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}</NativeSelect>}
      {scopeType === 'Vm' && <NativeSelect aria-label="VM" value={scopeId} onChange={(e) => setScopeId(e.target.value)}><option value="" />{inv.vms.map((v) => <option key={v.id} value={v.id}>{v.name} ({v.hostName})</option>)}</NativeSelect>}
      {scopeType === 'Group' && <GroupPickerList value={scopeId || null} onChange={(g) => setScopeId(g ?? '')} />}
      <Field label={t('roles.effect')} htmlFor="as-effect">
        <NativeSelect id="as-effect" value={effect} onChange={(e) => setEffect(e.target.value)}><option>Allow</option><option>Deny</option></NativeSelect>
      </Field>
    </ActionDialog>
  );
}

export function AuditPage() {
  const { t, fmtDate } = useI18n();
  const [outcome, setOutcome] = useState('');
  const [action, setAction] = useState('');
  const [query, setQuery] = useState('');
  const [skip, setSkip] = useState(0);
  const [take, setTake] = useState(50);
  const qs = `skip=${skip}&take=${take}${outcome ? '&outcome=' + outcome : ''}${query ? '&action=' + encodeURIComponent(query) : ''}`;
  const data = useLoad(() => get<{ total: number; items: AuditEntry[] }>(`/api/v1/audit?${qs}`), [qs]);
  const [verify, setVerify] = useState<{ valid: boolean; entriesChecked: number; firstInvalidId: number | null; problem: string | null } | null>(null);
  const [error, setError] = useState<unknown>(null);
  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<ScrollText className="size-5" />} title={t('nav.audit')}
        actions={<>
          <Button size="sm" variant="outline" onClick={async () => { setError(null); try { setVerify(await post('/api/v1/audit/verify')); } catch (e) { setError(e); } }}><ShieldCheck />{t('audit.verify')}</Button>
          <Button size="sm" variant="outline" asChild><a href="/api/v1/audit/export"><Download />{t('audit.export')}</a></Button>
        </>} />
      <div className="space-y-2 px-4 empty:hidden">
        <ErrorAlert error={error ?? data.error} />
        {verify && (verify.valid
          ? <Notice kind="ok" className="mt-3">{t('audit.valid', { n: verify.entriesChecked })}</Notice>
          : <Notice kind="danger" className="mt-3">{t('audit.invalid', { id: verify.firstInvalidId ?? '?', problem: verify.problem ?? '' })}</Notice>)}
      </div>
      <Pager total={data.data?.total ?? 0} skip={skip} take={take} onChange={(s, tk) => { setSkip(s); setTake(tk); }}>
        <form className="flex flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); setSkip(0); setQuery(action.trim()); }}>
          <Input aria-label={t('audit.action')} placeholder={t('audit.filterAction')} value={action} onChange={(e) => setAction(e.target.value)} className="w-56" />
          <NativeSelect aria-label={t('audit.outcome')} value={outcome} onChange={(e) => { setSkip(0); setOutcome(e.target.value); }} className="w-40">
            <option value="">{t('audit.allOutcomes')}</option><option>Success</option><option>Failure</option><option>Denied</option>
          </NativeSelect>
          <Button type="submit" size="sm" variant="outline">{t('common.search')}</Button>
        </form>
      </Pager>
      {data.loading && !data.data ? <Loading /> : (
        <Table containerClassName="min-h-0 flex-1">
          <TableHeader><TableRow><TableHead>{t('audit.time')}</TableHead><TableHead>{t('audit.actor')}</TableHead><TableHead>{t('audit.action')}</TableHead><TableHead>{t('audit.resource')}</TableHead><TableHead>{t('audit.outcome')}</TableHead><TableHead>{t('audit.source')}</TableHead></TableRow></TableHeader>
          <TableBody>
            {(data.data?.items ?? []).map((e) => (
              <TableRow key={e.id}>
                <TableCell className="text-xs whitespace-nowrap tabular">{fmtDate(e.timestampUtc)}</TableCell>
                <TableCell>{e.actorName ?? '—'}<div className="text-xs text-muted-foreground">{e.authMethod}</div></TableCell>
                <TableCell className="max-w-md"><code className="font-mono text-xs">{e.action}</code>{e.detailsJson && <div className="font-mono text-[11px] break-all text-muted-foreground">{e.detailsJson}</div>}</TableCell>
                <TableCell className="text-xs">{e.resourceType}{e.resourceId && <div className="font-mono text-muted-foreground">{e.resourceId}</div>}</TableCell>
                <TableCell><StatusBadge tone={e.outcome === 'Success' ? 'ok' : 'danger'}>{e.outcome}</StatusBadge></TableCell>
                <TableCell className="text-xs">{e.sourceIp}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </div>
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
  if (!s) return settings.error ? <div className="p-4"><ErrorAlert error={settings.error} /></div> : <Loading />;
  const set = (p: Partial<SecuritySettings>) => { setDraft({ ...s, ...p }); setSaved(false); };
  const save = async () => {
    setError(null);
    try { await put('/api/v1/settings/security', s); setSaved(true); setDraft(null); settings.reload(); } catch (e) { setError(e); }
  };
  return (
    <Page icon={<Settings className="size-5" />} title={t('nav.settings')}>
      <ErrorAlert error={error} />
      {saved && <Notice kind="ok">{t('settings.saved')}</Notice>}
      <div className="grid gap-3 xl:grid-cols-2">
        <Section title={t('settings.security')} bodyClassName="space-y-3 p-3">
          <Field label={t('settings.mfaFor')} htmlFor="s-mfa">
            <NativeSelect id="s-mfa" value={s.mfaRequiredFor} onChange={(e) => set({ mfaRequiredFor: e.target.value as SecuritySettings['mfaRequiredFor'] })}>
              {(['None', 'Privileged', 'All'] as const).map((v) => <option key={v} value={v}>{t(('settings.mfa.' + v) as TKey)}</option>)}
            </NativeSelect>
          </Field>
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={t('settings.idle')} htmlFor="s-idle"><Input id="s-idle" type="number" min={5} max={240} value={s.sessionIdleMinutes} onChange={(e) => set({ sessionIdleMinutes: Number(e.target.value) })} /></Field>
            <Field label={t('settings.absolute')} htmlFor="s-abs"><Input id="s-abs" type="number" min={1} max={72} value={s.sessionAbsoluteHours} onChange={(e) => set({ sessionAbsoluteHours: Number(e.target.value) })} /></Field>
            <Field label={t('settings.pwmin')} htmlFor="s-pw"><Input id="s-pw" type="number" min={15} max={128} value={s.passwordMinLength} onChange={(e) => set({ passwordMinLength: Number(e.target.value) })} /></Field>
          </div>
          <Field label={t('settings.localSignIn')} htmlFor="s-local">
            <NativeSelect id="s-local" value={s.localSignIn} onChange={(e) => set({ localSignIn: e.target.value as SecuritySettings['localSignIn'] })}>
              <option value="allowed">{t('settings.localSignIn.allowed')}</option><option value="break-glass-only">{t('settings.localSignIn.breakGlass')}</option>
            </NativeSelect>
          </Field>
          <Button disabled={!draft} onClick={() => void save()}>{t('common.save')}</Button>
        </Section>
        <EntraSettingsSection />
        <BackupSection />
        <Section title={t('settings.system')} bodyClassName="p-3">
          {info.data && (
            <KeyValue items={[
              ['Version', `${info.data.version} (${info.data.milestone})`],
              ['Database', info.data.database],
              [t('settings.telemetry'), info.data.telemetry],
              [t('settings.offline'), info.data.internetRequired],
              ...(info.data.dataDirectory ? [['Data', <code key="d" className="font-mono text-xs">{info.data.dataDirectory}</code>] as [ReactNode, ReactNode]] : []),
            ]} />
          )}
        </Section>
      </div>
    </Page>
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
    <Page icon={<UserRound className="size-5" />} title={t('nav.account')}>
      <ErrorAlert error={error} />
      {me.authSource === 'entra' ? (
        <Notice kind="info" title={t('account.entraTitle')}>{t('account.entraHelp')}</Notice>
      ) : (
      <div className="grid gap-3 xl:grid-cols-2">
        <Section title={t('login.mfaTitle')} bodyClassName="space-y-3 p-3">
          {me.mfaEnabled ? (
            <>
              <Notice kind="ok">{t('mfa.enabled')} {t('mfa.remaining', { n: me.recoveryCodesRemaining })}</Notice>
              {codes && <><div className="codes grid grid-cols-2 gap-2 rounded-lg border bg-muted p-3 font-mono text-sm">{codes.map((c) => <span key={c}>{c}</span>)}</div><CopyButton text={codes.join('\n')} /></>}
              <div className="flex flex-wrap gap-2">
                <Button variant="outline" size="sm" onClick={async () => { setError(null); try { setCodes((await post<{ recoveryCodes: string[] }>('/api/v1/mfa/recovery-codes')).recoveryCodes); await refresh(); } catch (e) { setError(e); } }}><KeyRound />{t('mfa.regenerate')}</Button>
                {!me.mfaRequired && <Button variant="destructive" size="sm" onClick={async () => { setError(null); try { await post('/api/v1/mfa/totp/disable'); await refresh(); } catch (e) { setError(e); } }}>{t('mfa.disable')}</Button>}
              </div>
            </>
          ) : (
            <><p className="text-sm text-muted-foreground">{t('mfa.disabled')}</p><MfaEnrollment /></>
          )}
          <p className="text-xs text-muted-foreground">{t('mfa.webauthn')}</p>
        </Section>
        <Section title={t('pw.changeTitle')} bodyClassName="p-3"><ChangePasswordForm /></Section>
      </div>
      )}
      <Section title={t('account.sessions')}>
        <Table>
          <TableBody>
            {(sessions.data ?? []).map((s) => (
              <TableRow key={s.id}>
                <TableCell className="text-xs whitespace-nowrap tabular">{fmtDate(s.createdAt)} → {fmtDate(s.lastSeenAt)}</TableCell>
                <TableCell className="text-xs">{s.ipAddress}<div className="max-w-md truncate text-muted-foreground">{s.userAgent}</div></TableCell>
                <TableCell><StatusBadge tone="muted">{s.authMethods}</StatusBadge></TableCell>
                <TableCell className="text-right">{s.current ? <StatusBadge tone="info">{t('account.current')}</StatusBadge>
                  : <Button size="xs" variant="outline" onClick={async () => { await del(`/api/v1/auth/sessions/${s.id}`); sessions.reload(); }}>{t('account.revoke')}</Button>}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Section>
    </Page>
  );
}

export function PlannedPage({ titleKey, milestone }: { titleKey: TKey; milestone: string }) {
  const { t } = useI18n();
  return (
    <Page icon={<ScrollText className="size-5" />} title={t(titleKey)}>
      <Section title={t('planned.title')} bodyClassName="p-3"><p className="text-sm">{t('planned.body', { m: milestone })}</p></Section>
    </Page>
  );
}
