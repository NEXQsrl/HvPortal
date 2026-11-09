import { CheckCircle2, FlaskConical, KeyRound, Save, UserRound } from 'lucide-react';
import { useState } from 'react';
import { get, post, put } from '@/api/client';
import type { Role } from '@/api/types';
import { CopyButton, ErrorAlert, Loading, Notice, Section } from '@/components/common';
import { Field, HelpTip } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { MicrosoftMark } from '@/components/MicrosoftMark';
import { GroupPickerList } from '@/components/ops/GroupDialogs';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { Switch } from '@/components/ui/switch';
import { useI18n } from '@/i18n';
import { useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';

export interface CreatedUser { userName: string; authSource: 'local' | 'entra'; temporaryPassword: string | null; mfaAtFirstSignIn: boolean }

/** Scope selector shared by "new user" and "assign role". */
export function ScopeFields({ scopeType, scopeId, onChange, idPrefix }: { scopeType: string; scopeId: string; onChange: (type: string, id: string) => void; idPrefix: string }) {
  const { t } = useI18n();
  const inv = useInventory();
  return (
    <>
      <Field label={t('roles.scope')} htmlFor={`${idPrefix}-scope`}>
        <NativeSelect id={`${idPrefix}-scope`} value={scopeType} onChange={(e) => onChange(e.target.value, '')}>
          <option value="Global">{t('users.scopeGlobal')}</option>{inv.clusters.length > 0 && <option value="Cluster">{t('scope.Cluster')}</option>}<option value="Host">Host</option><option value="Group">{t('group.title')}</option><option value="Vm">VM</option>
        </NativeSelect>
      </Field>
      {scopeType === 'Cluster' && <NativeSelect aria-label={t('scope.Cluster')} value={scopeId} onChange={(e) => onChange('Cluster', e.target.value)}><option value="" />{inv.clusters.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</NativeSelect>}
      {scopeType === 'Host' && <NativeSelect aria-label="Host" value={scopeId} onChange={(e) => onChange('Host', e.target.value)}><option value="" />{inv.hosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}</NativeSelect>}
      {scopeType === 'Vm' && <NativeSelect aria-label="VM" value={scopeId} onChange={(e) => onChange('Vm', e.target.value)}><option value="" />{inv.vms.map((v) => <option key={v.id} value={v.id}>{v.name} ({v.hostName})</option>)}</NativeSelect>}
      {scopeType === 'Group' && <GroupPickerList value={scopeId || null} onChange={(g) => onChange('Group', g ?? '')} />}
    </>
  );
}

/**
 * New user: type (local password or Microsoft Entra ID account), role and scope (optional, same anti-escalation
 * rules as role assignments), and for local users "set up MFA at first sign-in" (default on).
 */
export function AddUserDialog({ roles, onCreated, onClose }: { roles: Role[]; onCreated: (u: CreatedUser) => void; onClose: () => void }) {
  const { t } = useI18n();
  const { state, can } = useSession();
  const entraAvailable = !!state?.providers.includes('entra');
  const [source, setSource] = useState<'local' | 'entra'>('local');
  const [userName, setUserName] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [requireMfa, setRequireMfa] = useState(true);
  const [roleId, setRoleId] = useState('');
  const [scopeType, setScopeType] = useState('Global');
  const [scopeId, setScopeId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const canAssign = can('roles.manage');
  const nameOk = source === 'entra' ? /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(userName.trim()) : userName.trim().length >= 3;
  const valid = nameOk && (!roleId || scopeType === 'Global' || !!scopeId);

  const create = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await post<{ userName: string; temporaryPassword: string | null }>('/api/v1/users', {
        userName: userName.trim(),
        displayName: displayName.trim() || undefined,
        authSource: source,
        requireMfa: source === 'local' ? requireMfa : undefined,
        roleId: roleId || undefined,
        scopeType: roleId ? scopeType : undefined,
        scopeId: roleId && scopeType !== 'Global' ? scopeId : undefined,
      });
      onCreated({ userName: r.userName, authSource: source, temporaryPassword: r.temporaryPassword, mfaAtFirstSignIn: source === 'local' && requireMfa });
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open onOpenChange={(o) => { if (!o && !busy) onClose(); }}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{t('users.add')}</DialogTitle>
          <DialogDescription>{t('users.addHelp')}</DialogDescription>
        </DialogHeader>
        <form id="add-user" className="space-y-4" onSubmit={(e) => { e.preventDefault(); if (valid && !busy) void create(); }}>
          <ErrorAlert error={error} />
          <Field label={t('users.type')}>
            <RadioGroup value={source} onValueChange={(v) => setSource(v as 'local' | 'entra')} className="grid gap-2 sm:grid-cols-2">
              {(['local', 'entra'] as const).map((s) => (
                <label key={s} className={cn('flex cursor-pointer items-start gap-2 rounded-lg border p-3 text-sm has-[[data-state=checked]]:border-primary/50 has-[[data-state=checked]]:bg-primary/5',
                  s === 'entra' && !entraAvailable && 'cursor-not-allowed opacity-60')}>
                  <RadioGroupItem value={s} disabled={s === 'entra' && !entraAvailable} className="mt-0.5" />
                  <span className="space-y-0.5">
                    <span className="flex items-center gap-1.5 font-medium">{s === 'entra' ? <MicrosoftMark className="size-3.5" /> : <UserRound className="size-3.5" />}{t(s === 'entra' ? 'users.typeEntra' : 'users.typeLocal')}</span>
                    <span className="block text-xs text-muted-foreground">{t(s === 'entra' ? (entraAvailable ? 'users.typeEntraHelp' : 'users.typeEntraOff') : 'users.typeLocalHelp')}</span>
                  </span>
                </label>
              ))}
            </RadioGroup>
          </Field>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label={t(source === 'entra' ? 'users.upn' : 'login.user')} htmlFor="nu-name" hint={source === 'entra' ? t('users.upnHint') : undefined}>
              <Input id="nu-name" value={userName} onChange={(e) => setUserName(e.target.value)} autoFocus autoComplete="off" placeholder={source === 'entra' ? 'nome@contoso.com' : 'mario.rossi'} />
            </Field>
            <Field label={t('users.displayName')} htmlFor="nu-display"><Input id="nu-display" value={displayName} onChange={(e) => setDisplayName(e.target.value)} maxLength={64} /></Field>
          </div>
          {source === 'local' ? (
            <label className="flex items-start gap-2 text-sm">
              <Checkbox checked={requireMfa} onCheckedChange={(c) => setRequireMfa(!!c)} className="mt-0.5" />
              <span className="flex items-center gap-1.5">{t('users.requireMfa')}<HelpTip>{t('users.requireMfaHelp')}</HelpTip></span>
            </label>
          ) : <Notice kind="info">{t('users.entraMfa')}</Notice>}
          {canAssign ? (
            <div className="space-y-3 rounded-lg border p-3">
              <Field label={t('roles.role')} htmlFor="nu-role" hint={!roleId ? t('users.noRoleHint') : undefined}>
                <NativeSelect id="nu-role" value={roleId} onChange={(e) => setRoleId(e.target.value)}>
                  <option value="">{t('users.noRoleOption')}</option>
                  {roles.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
                </NativeSelect>
              </Field>
              {roleId && <ScopeFields scopeType={scopeType} scopeId={scopeId} onChange={(ty, id) => { setScopeType(ty); setScopeId(id); }} idPrefix="nu" />}
            </div>
          ) : <p className="text-xs text-muted-foreground">{t('users.noRole')}</p>}
        </form>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{t('common.cancel')}</Button>
          <Button type="submit" form="add-user" disabled={!valid || busy}>{t('users.create')}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Result of a creation: temporary password (local) or how a Microsoft user signs in. */
export function CreatedUserNotice({ user, onClose }: { user: CreatedUser; onClose: () => void }) {
  const { t } = useI18n();
  return (
    <Notice kind={user.temporaryPassword ? 'warn' : 'ok'} title={user.temporaryPassword ? t('users.temp', { name: user.userName }) : t('users.entraCreated', { name: user.userName })}>
      {user.temporaryPassword ? (
        <>
          <div className="mt-1 flex flex-wrap items-center gap-2"><code className="secret rounded border bg-muted px-2 py-1 font-mono text-foreground">{user.temporaryPassword}</code>
            <CopyButton text={user.temporaryPassword} /><Button size="sm" variant="ghost" onClick={onClose}>{t('common.close')}</Button></div>
          <p className="mt-1 text-xs">{t(user.mfaAtFirstSignIn ? 'users.firstSignInMfa' : 'users.firstSignIn')}</p>
        </>
      ) : (
        <div className="mt-1 flex items-center gap-2 text-xs">{t('users.entraCreatedHelp')}<Button size="sm" variant="ghost" onClick={onClose}>{t('common.close')}</Button></div>
      )}
    </Notice>
  );
}

interface EntraSettingsView {
  enabled: boolean; tenantId: string | null; clientId: string | null; authority: string; hasClientSecret: boolean; complete: boolean;
  updatedAt: string | null; redirectUri: string; authorities: string[];
}

/** Settings > Microsoft Entra ID: app registration values, write-only client secret, metadata test. */
export function EntraSettingsSection() {
  const { t, fmtDate } = useI18n();
  const { refresh } = useSession();
  const data = useLoad(() => get<EntraSettingsView>('/api/v1/settings/entra'));
  const [draft, setDraft] = useState<{ enabled: boolean; tenantId: string; clientId: string; authority: string } | null>(null);
  const [secret, setSecret] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (data.loading && !data.data) return <Section title={t('entra.title')}><Loading /></Section>;
  const v = data.data;
  if (!v) return <Section title={t('entra.title')} bodyClassName="p-3"><ErrorAlert error={data.error} /></Section>;
  const d = draft ?? { enabled: v.enabled, tenantId: v.tenantId ?? '', clientId: v.clientId ?? '', authority: v.authority };
  const set = (p: Partial<typeof d>) => { setDraft({ ...d, ...p }); setNotice(null); };
  const dirty = !!draft || secret.length > 0;

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await put('/api/v1/settings/entra', { ...d, tenantId: d.tenantId.trim() || null, clientId: d.clientId.trim() || null, clientSecret: secret || null });
      setDraft(null);
      setSecret('');
      setNotice(t('settings.saved'));
      data.reload();
      await refresh();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  const test = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await post<{ issuer: string; keys: number }>('/api/v1/settings/entra/test');
      setNotice(t('entra.testOk', { issuer: r.issuer, keys: r.keys }));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Section title={<span className="flex items-center gap-2"><MicrosoftMark />{t('entra.title')}<HelpTip>{t('entra.help')}</HelpTip></span>} bodyClassName="space-y-4 p-4">
      <h3 className="text-sm font-medium">{t('entra.setupTitle')}</h3>
      <ol className="list-decimal space-y-1 pl-4 text-xs text-muted-foreground">
        <li>{t('entra.step1')}</li>
        <li>{t('entra.step2')}
          <div className="mt-1 flex items-center gap-2"><code className="min-w-0 flex-1 truncate rounded border bg-muted px-2 py-1 font-mono text-[11px] text-foreground">{v.redirectUri}</code><CopyButton text={v.redirectUri} /></div>
        </li>
        <li>{t('entra.step3')}</li>
        <li>{t('entra.step4')}</li>
      </ol>
      <ErrorAlert error={error} />
      {notice && <Notice kind="ok">{notice}</Notice>}
      <label className="flex items-center gap-2 border-t pt-4 text-sm"><Switch checked={d.enabled} onCheckedChange={(c) => set({ enabled: c })} />{t('entra.enabled')}<HelpTip>{t('entra.mfaNote')}</HelpTip></label>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('entra.tenant')} htmlFor="en-tenant"><Input id="en-tenant" value={d.tenantId} onChange={(e) => set({ tenantId: e.target.value })} placeholder="00000000-0000-0000-0000-000000000000" className="font-mono text-xs" /></Field>
        <Field label={t('entra.client')} htmlFor="en-client"><Input id="en-client" value={d.clientId} onChange={(e) => set({ clientId: e.target.value })} placeholder="00000000-0000-0000-0000-000000000000" className="font-mono text-xs" /></Field>
        <Field label={t('entra.secret')} htmlFor="en-secret" hint={t('entra.secretHelp')} aside={v.hasClientSecret ? t('entra.secretStored') : t('entra.secretNone')}>
          <Input id="en-secret" type="password" autoComplete="new-password" value={secret} onChange={(e) => { setSecret(e.target.value); setNotice(null); }} placeholder={v.hasClientSecret ? '••••••••' : ''} />
        </Field>
        <Field label={t('entra.cloud')} htmlFor="en-cloud">
          <NativeSelect id="en-cloud" value={d.authority} onChange={(e) => set({ authority: e.target.value })}>
            {v.authorities.map((a) => <option key={a} value={a}>{a.replace('https://', '')}</option>)}
          </NativeSelect>
        </Field>
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <Button disabled={!dirty || busy} onClick={() => void save()}><Save />{t('common.save')}</Button>
        <Button variant="outline" disabled={!v.tenantId || busy || dirty} onClick={() => void test()}><FlaskConical />{t('entra.test')}</Button>
        {v.complete && v.enabled && <span className="flex items-center gap-1 text-xs text-success"><CheckCircle2 className="size-3.5" />{t('entra.active')}</span>}
        {v.updatedAt && <span className="ml-auto text-xs text-muted-foreground"><KeyRound className="mr-1 inline size-3" />{fmtDate(v.updatedAt)}</span>}
      </div>
    </Section>
  );
}
