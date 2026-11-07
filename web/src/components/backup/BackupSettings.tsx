import { KeySquare, Plus, Save, Send, ShieldOff, Trash2, TriangleAlert } from 'lucide-react';
import { useState } from 'react';
import { get, post, put } from '@/api/client';
import { CopyButton, Empty, ErrorAlert, Loading, Notice, Section, StatusBadge } from '@/components/common';
import { ActionDialog, Field, HelpTip } from '@/components/dialogs';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { NativeSelect } from '@/components/ui/native-select';
import { Switch } from '@/components/ui/switch';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Textarea } from '@/components/ui/textarea';
import { useI18n, type TKey } from '@/i18n';
import { useLoad } from '@/lib/hooks';
import { toast } from '@/lib/toast';
import type { ApiTokenRow, CreatedToken, NotificationSettings } from './backup-api';
import { BACKUP_NAME, isValidComputerName, isValidMail, isValidWebhookUrl } from './backup-rules';

const WEBHOOK_FORMATS = ['teams', 'slack', 'generic'] as const;

interface HookRow { key: number; name: string; format: string; url: string; masked: string | null; storedName: string | null }

interface EmailDraft { enabled: boolean; host: string; port: number; security: string; userName: string; password: string; removePassword: boolean; hasPassword: boolean; from: string; to: string }

interface Draft { email: EmailDraft; hooks: HookRow[]; restoreTestFailed: boolean; agentsOutdated: boolean }

let hookSeq = 0;

function draftOf(s: NotificationSettings): Draft {
  return {
    email: {
      enabled: s.email.enabled, host: s.email.host ?? '', port: s.email.port || 587, security: s.email.security || 'starttls', userName: s.email.userName ?? '',
      password: '', removePassword: false, hasPassword: s.email.hasPassword, from: s.email.from ?? '', to: s.email.to.join('\n'),
    },
    hooks: s.webhooks.map((w) => ({ key: ++hookSeq, name: w.name, format: w.format, url: '', masked: w.url, storedName: w.name })),
    restoreTestFailed: s.restoreTestFailed,
    agentsOutdated: s.agentsOutdated,
  };
}

const recipients = (s: string) => s.split(/[\s,;]+/).map((x) => x.trim()).filter(Boolean);

/**
 * Settings > Notifications (GET/PUT /api/v1/settings/notifications, POST .../test): e-mail through an SMTP server with
 * STARTTLS and HTTPS webhooks (Microsoft Teams, Slack, generic JSON). Nothing is sent until a channel is configured.
 * Secrets are write-only: the SMTP password is never returned and webhook addresses come back masked.
 */
export function NotificationSettingsSection() {
  const { t } = useI18n();
  const settings = useLoad(() => get<NotificationSettings>('/api/v1/settings/notifications'));
  const [draft, setDraft] = useState<Draft | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [test, setTest] = useState<{ sent: boolean; errors: string[] } | null>(null);
  const base = settings.data;
  if (!base) return settings.error ? <ErrorAlert error={settings.error} /> : <Loading />;
  const d = draft ?? draftOf(base);
  const dirty = draft !== null;
  const set = (p: Partial<Draft>) => { setDraft({ ...d, ...p }); setTest(null); };
  const setEmail = (p: Partial<EmailDraft>) => set({ email: { ...d.email, ...p } });
  const setHook = (key: number, p: Partial<HookRow>) => set({ hooks: d.hooks.map((h) => (h.key === key ? { ...h, ...p } : h)) });

  const e = d.email;
  const to = recipients(e.to);
  const emailErrors: Record<string, TKey | null> = e.enabled ? {
    host: !isValidComputerName(e.host.trim()) ? 'bk.notif.hostInvalid' : null,
    port: !Number.isInteger(e.port) || e.port < 1 || e.port > 65535 ? 'bk.notif.portInvalid' : null,
    from: !isValidMail(e.from.trim()) ? 'bk.notif.mailInvalid' : null,
    to: to.length === 0 || to.length > 20 || to.some((x) => !isValidMail(x)) ? 'bk.notif.toInvalid' : null,
    user: e.userName.length > 256 ? 'bk.notif.userInvalid' : null,
  } : {};
  const hookError = (h: HookRow): TKey | null => {
    if (!h.name.trim() || h.name.trim().length > 64) return 'bk.notif.hookNameInvalid';
    if (h.url.trim()) return isValidWebhookUrl(h.url.trim()) ? null : 'bk.notif.hookUrlInvalid';
    if (!h.masked) return 'bk.notif.hookUrlRequired';
    // A stored address is kept by the server only for the same name: a renamed webhook needs its address again.
    return h.name.trim() !== h.storedName ? 'bk.notif.hookUrlAgain' : null;
  };
  const names = d.hooks.map((h) => h.name.trim());
  const duplicate = names.some((n, i) => n && names.indexOf(n) !== i);
  const valid = Object.values(emailErrors).every((x) => !x) && d.hooks.every((h) => !hookError(h)) && !duplicate && d.hooks.length <= 10;

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      const body = {
        email: {
          enabled: e.enabled, host: e.host.trim() || null, port: e.port, security: e.security, userName: e.userName.trim() || null,
          // null = keep the stored password, "" = remove it, a value = replace it.
          password: e.password ? e.password : e.removePassword ? '' : null,
          from: e.from.trim() || null, to,
        },
        webhooks: d.hooks.map((h) => ({ name: h.name.trim(), url: h.url.trim() || h.masked || '', format: h.format })),
        restoreTestFailed: d.restoreTestFailed,
        agentsOutdated: d.agentsOutdated,
      };
      settings.setData(await put<NotificationSettings>('/api/v1/settings/notifications', body));
      setDraft(null);
      toast.success(t('settings.saved'));
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  };
  const sendTest = async () => {
    setBusy(true);
    setError(null);
    try { setTest(await post<{ sent: boolean; errors: string[] }>('/api/v1/settings/notifications/test')); } catch (err) { setError(err); } finally { setBusy(false); }
  };
  const nothing = !base.email.enabled && base.webhooks.length === 0;

  return (
    <div className="space-y-4">
      <Notice kind="info">{t('bk.notif.intro')}</Notice>
      <ErrorAlert error={error} />
      <Section title={<span className="flex items-center gap-2"><Switch id="nt-email" checked={e.enabled} onCheckedChange={(v) => setEmail({ enabled: v })} /><Label htmlFor="nt-email" className="cursor-pointer">{t('bk.notif.email')}</Label></span>}
        bodyClassName="space-y-3 p-4">
        <div className="grid gap-3 sm:grid-cols-[1fr_8rem_10rem]">
          <Field label={t('bk.notif.smtpHost')} htmlFor="nt-host" error={e.host && emailErrors.host ? t(emailErrors.host) : undefined}>
            <Input id="nt-host" value={e.host} placeholder="smtp.contoso.local" disabled={!e.enabled} onChange={(x) => setEmail({ host: x.target.value })} />
          </Field>
          <Field label={t('bk.notif.port')} htmlFor="nt-port" error={emailErrors.port ? t(emailErrors.port) : undefined}>
            <Input id="nt-port" type="number" min={1} max={65535} value={e.port} disabled={!e.enabled} onChange={(x) => setEmail({ port: Number(x.target.value) })} />
          </Field>
          <Field label={t('bk.notif.security')} htmlFor="nt-sec" hint={t('bk.notif.securityHint')}>
            <NativeSelect id="nt-sec" value={e.security} disabled={!e.enabled} onChange={(x) => setEmail({ security: x.target.value })}>
              <option value="starttls">STARTTLS</option>
              <option value="none">{t('bk.notif.securityNone')}</option>
            </NativeSelect>
          </Field>
        </div>
        {e.enabled && e.security === 'none' && <Notice kind="warn">{t('bk.notif.noneWarn')}</Notice>}
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('bk.notif.user')} htmlFor="nt-user" hint={t('bk.notif.userHint')} error={emailErrors.user ? t(emailErrors.user) : undefined}>
            <Input id="nt-user" autoComplete="off" value={e.userName} disabled={!e.enabled} onChange={(x) => setEmail({ userName: x.target.value })} />
          </Field>
          <Field label={t('bk.notif.password')} htmlFor="nt-pw" aside={e.hasPassword && !e.removePassword ? t('bk.repo.passwordStored') : undefined}>
            <Input id="nt-pw" type="password" autoComplete="new-password" value={e.password} disabled={!e.enabled || !e.userName.trim()}
              placeholder={e.hasPassword && !e.removePassword ? t('bk.repo.passwordKeep') : ''} onChange={(x) => setEmail({ password: x.target.value, removePassword: false })} />
          </Field>
        </div>
        {e.hasPassword && (
          <label className="flex items-center gap-2 text-xs">
            <Checkbox checked={e.removePassword} disabled={!e.enabled} onCheckedChange={(v) => setEmail({ removePassword: !!v, password: '' })} />{t('bk.notif.removePassword')}
          </label>
        )}
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('bk.notif.from')} htmlFor="nt-from" error={e.from && emailErrors.from ? t(emailErrors.from) : undefined}>
            <Input id="nt-from" type="email" value={e.from} placeholder="hvportal@contoso.local" disabled={!e.enabled} onChange={(x) => setEmail({ from: x.target.value })} />
          </Field>
          <Field label={t('bk.notif.to')} htmlFor="nt-to" hint={t('bk.notif.toHint')} aside={`${to.length}/20`} error={e.to && emailErrors.to ? t(emailErrors.to) : undefined}>
            <Textarea id="nt-to" rows={3} value={e.to} placeholder="ops@contoso.local" disabled={!e.enabled} onChange={(x) => setEmail({ to: x.target.value })} />
          </Field>
        </div>
      </Section>

      <Section title={<span className="flex items-center gap-1.5">{t('bk.notif.webhooks')}<HelpTip>{t('bk.notif.webhooksHelp')}</HelpTip></span>}
        actions={<Button size="sm" variant="outline" disabled={d.hooks.length >= 10} onClick={() => set({ hooks: [...d.hooks, { key: ++hookSeq, name: '', format: 'teams', url: '', masked: null, storedName: null }] })}><Plus />{t('bk.notif.addWebhook')}</Button>}
        bodyClassName="space-y-3 p-4">
        {d.hooks.length === 0 && <p className="text-sm text-muted-foreground">{t('bk.notif.noWebhooks')}</p>}
        {d.hooks.map((h) => {
          const err = hookError(h);
          return (
            <div key={h.key} className="grid gap-2 rounded-lg border p-3 sm:grid-cols-[12rem_9rem_1fr_auto] sm:items-end">
              <Field label={t('common.name')} htmlFor={`wh-n-${h.key}`}>
                <Input id={`wh-n-${h.key}`} value={h.name} maxLength={64} onChange={(x) => setHook(h.key, { name: x.target.value })} />
              </Field>
              <Field label={t('bk.notif.format')} htmlFor={`wh-f-${h.key}`}>
                <NativeSelect id={`wh-f-${h.key}`} value={h.format} onChange={(x) => setHook(h.key, { format: x.target.value })}>
                  {WEBHOOK_FORMATS.map((f) => <option key={f} value={f}>{t(('bk.notif.format.' + f) as TKey)}</option>)}
                </NativeSelect>
              </Field>
              <Field label={t('bk.notif.url')} htmlFor={`wh-u-${h.key}`} aside={h.masked && !h.url ? t('bk.notif.urlStored') : undefined}>
                <Input id={`wh-u-${h.key}`} className="font-mono text-xs" value={h.url} autoComplete="off" placeholder={h.masked ?? 'https://…'} onChange={(x) => setHook(h.key, { url: x.target.value })} />
              </Field>
              <Button type="button" size="icon-sm" variant="ghost" className="text-destructive" aria-label={t('common.remove')} onClick={() => set({ hooks: d.hooks.filter((x) => x.key !== h.key) })}><Trash2 /></Button>
              {err && (h.name || h.url || h.masked) && <p className="text-xs text-destructive sm:col-span-4" role="alert">{t(err)}</p>}
            </div>
          );
        })}
        {duplicate && <p className="text-xs text-destructive" role="alert">{t('bk.notif.hookDuplicate')}</p>}
        <p className="text-xs text-muted-foreground">{t('bk.notif.secretNote')}</p>
      </Section>

      <Section title={t('bk.notif.events')} bodyClassName="space-y-3 p-4">
        <p className="text-sm text-muted-foreground">{t('bk.notif.eventsJobs')}</p>
        <label className="flex items-start gap-2 text-sm">
          <Switch checked={d.restoreTestFailed} onCheckedChange={(v) => set({ restoreTestFailed: v })} className="mt-0.5" />
          <span>{t('bk.notif.restoreTestFailed')}</span>
        </label>
        <label className="flex items-start gap-2 text-sm">
          <Switch checked={d.agentsOutdated} onCheckedChange={(v) => set({ agentsOutdated: v })} className="mt-0.5" />
          <span>{t('bk.notif.agentsOutdated')}</span>
        </label>
      </Section>

      {test && (test.sent ? <Notice kind="ok">{t('bk.notif.testSent')}</Notice>
        : <Notice kind="danger" title={t('bk.notif.testFailed')}>{test.errors.map((x, i) => <div key={i} className="break-words">{x}</div>)}</Notice>)}
      <div className="flex flex-wrap items-center justify-end gap-2 border-t pt-3">
        {dirty && <span className="mr-auto text-xs text-muted-foreground">{t('bk.notif.unsaved')}</span>}
        {dirty && <Button variant="ghost" onClick={() => { setDraft(null); setError(null); }} disabled={busy}>{t('common.cancel')}</Button>}
        <Button variant="outline" onClick={() => void sendTest()} disabled={busy || dirty || nothing} title={dirty ? t('bk.notif.saveFirst') : nothing ? t('bk.notif.nothing') : undefined}><Send />{t('bk.notif.test')}</Button>
        <Button onClick={() => void save()} disabled={busy || !dirty || !valid}><Save />{t('common.save')}</Button>
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ API tokens

const EXPIRY_CHOICES = [30, 90, 365, 0] as const;

/**
 * Settings > API tokens (GET/POST /api/v1/api-tokens, POST /{id}/revoke): read-only tokens for monitoring systems
 * (scope backup.read), valid only on /api/v1/public/*. The token is shown once, when it is created.
 */
export function ApiTokensSection() {
  const { t, fmtDate } = useI18n();
  const tokens = useLoad(() => get<ApiTokenRow[]>('/api/v1/api-tokens'));
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<CreatedToken | null>(null);
  const [revoking, setRevoking] = useState<ApiTokenRow | null>(null);
  const now = Date.now();
  const state = (r: ApiTokenRow): { tone: 'ok' | 'muted' | 'danger'; key: TKey } =>
    r.revokedAt ? { tone: 'danger', key: 'bk.tok.revoked' } : r.expiresAt && new Date(r.expiresAt).getTime() <= now ? { tone: 'muted', key: 'bk.tok.expired' } : { tone: 'ok', key: 'bk.tok.active' };
  return (
    <div className="space-y-4">
      <Notice kind="info" title={t('bk.tok.introTitle')}>{t('bk.tok.intro')}</Notice>
      <Section title={t('settings.tab.apiTokens')} actions={<Button size="sm" onClick={() => setCreating(true)}><Plus />{t('bk.tok.new')}</Button>}>
        <ErrorAlert error={tokens.error} className="mx-3" />
        {tokens.loading && !tokens.data ? <Loading /> : (tokens.data ?? []).length === 0 ? <Empty icon={<KeySquare className="size-6" />}>{t('bk.tok.none')}</Empty> : (
          <Table>
            <TableHeader><TableRow>
              <TableHead>{t('common.name')}</TableHead><TableHead>{t('common.status')}</TableHead><TableHead className="hidden md:table-cell">{t('bk.tok.scopes')}</TableHead>
              <TableHead className="hidden md:table-cell">{t('bk.tok.created')}</TableHead><TableHead>{t('bk.tok.expires')}</TableHead><TableHead>{t('bk.tok.lastUsed')}</TableHead><TableHead />
            </TableRow></TableHeader>
            <TableBody>
              {(tokens.data ?? []).map((r) => {
                const s = state(r);
                return (
                  <TableRow key={r.id} className="align-top">
                    <TableCell><div className="font-medium">{r.name}</div><code className="font-mono text-xs text-muted-foreground">{r.prefix}…</code></TableCell>
                    <TableCell><StatusBadge tone={s.tone}>{t(s.key)}</StatusBadge>{r.revokedAt && <div className="mt-0.5 text-xs text-muted-foreground tabular">{fmtDate(r.revokedAt)}</div>}</TableCell>
                    <TableCell className="hidden md:table-cell">{r.scopes.map((x) => <code key={x} className="rounded bg-muted px-1.5 py-0.5 font-mono text-[11px]">{x}</code>)}</TableCell>
                    <TableCell className="hidden text-xs md:table-cell"><div className="tabular">{fmtDate(r.createdAt)}</div>{r.createdBy && <div className="text-muted-foreground">{r.createdBy}</div>}</TableCell>
                    <TableCell className="text-xs tabular">{r.expiresAt ? fmtDate(r.expiresAt) : t('bk.tok.noExpiry')}</TableCell>
                    <TableCell className="text-xs"><div className="tabular">{r.lastUsedAt ? fmtDate(r.lastUsedAt) : t('common.never')}</div>{r.lastUsedIp && <div className="text-muted-foreground">{r.lastUsedIp}</div>}</TableCell>
                    <TableCell className="text-right">{!r.revokedAt && <Button size="xs" variant="ghost" className="text-destructive" onClick={() => setRevoking(r)}><ShieldOff />{t('bk.tok.revoke')}</Button>}</TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        )}
      </Section>
      <PublicApiHelp />
      {creating && <NewTokenDialog onClose={() => setCreating(false)} onCreated={(c) => { setCreated(c); tokens.reload(); }} />}
      {created && <TokenCreatedDialog token={created} onClose={() => setCreated(null)} />}
      {revoking && (
        <ActionDialog title={t('bk.tok.revokeTitle', { name: revoking.name })} confirmLabel={t('bk.tok.revoke')} danger onClose={() => setRevoking(null)}
          onConfirm={async () => { await post(`/api/v1/api-tokens/${revoking.id}/revoke`); tokens.reload(); }}>
          <p className="text-sm">{t('bk.tok.revokeHelp')}</p>
        </ActionDialog>
      )}
    </div>
  );
}

function NewTokenDialog({ onCreated, onClose }: { onCreated: (t: CreatedToken) => void; onClose: () => void }) {
  const { t } = useI18n();
  const [name, setName] = useState('');
  const [days, setDays] = useState<number>(365);
  const valid = BACKUP_NAME.test(name.trim());
  return (
    <ActionDialog title={t('bk.tok.new')} confirmLabel={t('common.create')} disabled={!valid} onClose={onClose}
      onConfirm={async () => {
        // Creation needs a recent re-authentication: the client opens the dialog for it and retries.
        onCreated(await post<CreatedToken>('/api/v1/api-tokens', { name: name.trim(), expiresInDays: days > 0 ? days : undefined, scopes: ['backup.read'] }));
      }}>
      <Field label={t('common.name')} htmlFor="tk-name" hint={t('bk.tok.nameHint')} error={name && !valid ? t('bk.name.invalid') : undefined}>
        <Input id="tk-name" value={name} maxLength={64} placeholder="PRTG" onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>
      <Field label={t('bk.tok.expires')} htmlFor="tk-exp">
        <NativeSelect id="tk-exp" value={days} onChange={(e) => setDays(Number(e.target.value))}>
          {EXPIRY_CHOICES.map((n) => <option key={n} value={n}>{n === 0 ? t('bk.tok.noExpiry') : t('bk.tok.days', { n })}</option>)}
        </NativeSelect>
      </Field>
      {days === 0 && <Notice kind="warn">{t('bk.tok.noExpiryWarn')}</Notice>}
      <Field label={t('bk.tok.scopes')}>
        <div className="flex items-center gap-2 text-sm"><code className="rounded bg-muted px-1.5 py-0.5 font-mono text-xs">backup.read</code><span className="text-xs text-muted-foreground">{t('bk.tok.scopeHelp')}</span></div>
      </Field>
      <p className="text-xs text-muted-foreground">{t('bk.tok.reauthNote')}</p>
    </ActionDialog>
  );
}

function curlExamples(token: string) {
  const origin = window.location.origin;
  // Example: the runs of the last 24 hours, as an ISO 8601 UTC time without milliseconds.
  const since = new Date(Date.now() - 86_400_000).toISOString().replace(/\.\d{3}Z$/, 'Z');
  return {
    curl: `curl -H "Authorization: Bearer ${token}" ${origin}/api/v1/public/backup/status`,
    runs: `curl -H "Authorization: Bearer ${token}" "${origin}/api/v1/public/backup/runs?since=${since}"`,
    ps: `Invoke-RestMethod -Uri '${origin}/api/v1/public/backup/status' -Headers @{ Authorization = 'Bearer ${token}' }`,
  };
}

function CodeLine({ text }: { text: string }) {
  return (
    <div className="flex items-start gap-1.5">
      <pre className="command min-w-0 flex-1 overflow-x-auto rounded-md border bg-muted px-2 py-1.5 font-mono text-[11px] whitespace-pre-wrap break-all">{text}</pre>
      <CopyButton text={text} />
    </div>
  );
}

/** The new token, shown once: copy it now; it cannot be shown again (only its SHA-256 is stored). */
function TokenCreatedDialog({ token, onClose }: { token: CreatedToken; onClose: () => void }) {
  const { t, fmtDate } = useI18n();
  const [stored, setStored] = useState(false);
  const ex = curlExamples(token.token);
  return (
    <Dialog open onOpenChange={() => { /* closed only with the button, after the token was copied */ }}>
      <DialogContent className="flex max-h-[calc(100dvh-2rem)] flex-col overflow-hidden sm:max-w-2xl" showCloseButton={false}>
        <DialogHeader className="shrink-0">
          <DialogTitle>{t('bk.tok.createdTitle', { name: token.name })}</DialogTitle>
          <DialogDescription>{token.expiresAt ? t('bk.tok.createdExpires', { at: fmtDate(token.expiresAt) }) : t('bk.tok.noExpiry')}</DialogDescription>
        </DialogHeader>
        <div className="-mx-4 min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-0.5">
          <Notice kind="warn" title={<span className="flex items-center gap-1.5"><TriangleAlert className="size-4" />{t('bk.tok.onceTitle')}</span>}>{t('bk.tok.once')}</Notice>
          <div className="flex items-center gap-2">
            <code className="secret min-w-0 flex-1 rounded border bg-muted px-2 py-1.5 font-mono text-sm break-all text-foreground">{token.token}</code>
            <CopyButton text={token.token} />
          </div>
          <div className="space-y-1.5">
            <div className="text-xs font-medium text-muted-foreground">{t('bk.tok.exampleStatus')}</div>
            <CodeLine text={ex.curl} />
            <div className="text-xs font-medium text-muted-foreground">{t('bk.tok.exampleRuns')}</div>
            <CodeLine text={ex.runs} />
            <div className="text-xs font-medium text-muted-foreground">PowerShell</div>
            <CodeLine text={ex.ps} />
          </div>
          <p className="text-xs text-muted-foreground">{t('bk.tok.tlsNote')}</p>
          <label className="flex items-center gap-2 text-sm">
            <Checkbox checked={stored} onCheckedChange={(v) => setStored(!!v)} />{t('bk.tok.stored')}
          </label>
        </div>
        <DialogFooter className="shrink-0"><Button type="button" disabled={!stored} onClick={onClose}>{t('common.close')}</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** How monitoring systems read the backup status (GET /api/v1/public/backup/status and /runs). */
function PublicApiHelp() {
  const { t } = useI18n();
  const ex = curlExamples('hvp_…');
  return (
    <Section title={t('bk.tok.apiTitle')} bodyClassName="space-y-2 p-4 text-sm">
      <p>{t('bk.tok.apiStatus')}</p>
      <CodeLine text={ex.curl} />
      <p>{t('bk.tok.apiRuns')}</p>
      <CodeLine text={ex.runs} />
      <p className="text-xs text-muted-foreground">{t('bk.tok.apiNotes')}</p>
      <p className="text-xs text-muted-foreground">{t('bk.tok.tlsNote')}</p>
    </Section>
  );
}

