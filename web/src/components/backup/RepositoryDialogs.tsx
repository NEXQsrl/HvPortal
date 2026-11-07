import { FolderOpen, LoaderCircle, Play } from 'lucide-react';
import { useState } from 'react';
import { del, post, put } from '@/api/client';
import { ErrorAlert, Mono, Notice } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { HostFolderPicker } from '@/components/ops/HostFolderPicker';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { Textarea } from '@/components/ui/textarea';
import { useI18n, type TKey } from '@/i18n';
import { normalizeFolder } from '@/lib/hostPaths';
import { toast } from '@/lib/toast';
import type { CheckResponse, Repository, RescanResponse } from './backup-api';
import { ReadinessList } from './BackupCommon';
import { BACKUP_NAME, isInsideStorageFolders, isValidAccountName, isValidLocalRepositoryPath, isValidSharePath, normalizeShare } from './backup-rules';

/**
 * Create or change a repository (POST /backup/repositories, PUT /backup/repositories/{id}): a local folder of one host
 * or an SMB share usable by every host, optionally with a user of the NAS (outside the domain). The path and the host
 * never change once created (the restore points live there); the password is write-only.
 */
export function RepositoryDialog({ repo, onSaved, onClose }: { repo: Repository | null; onSaved: () => void; onClose: () => void }) {
  const { t, fmtDate } = useI18n();
  const inv = useInventory();
  const hosts = inv.hosts.filter((h) => h.status === 'Active');
  const [name, setName] = useState(repo?.name ?? '');
  const [description, setDescription] = useState(repo?.description ?? '');
  const [kind, setKind] = useState<'local' | 'share'>(repo ? (repo.isShare ? 'share' : 'local') : 'share');
  const [hostId, setHostId] = useState(repo?.hostId ?? hosts[0]?.id ?? '');
  const [path, setPath] = useState(repo?.path ?? '');
  const [useCreds, setUseCreds] = useState(repo ? repo.hasCredentials : false);
  const [userName, setUserName] = useState(repo?.userName ?? '');
  const [password, setPassword] = useState('');
  const [picking, setPicking] = useState(false);
  const host = inv.hostById.get(hostId);
  const editing = !!repo;

  const pathValue = kind === 'share' ? normalizeShare(path) : normalizeFolder(path);
  const pathError: TKey | null = editing ? null : !path.trim() ? 'bk.repo.pathRequired'
    : kind === 'share' ? (!isValidSharePath(pathValue) ? 'bk.repo.shareInvalid' : null)
    : !isValidLocalRepositoryPath(pathValue) ? 'bk.repo.localInvalid' : !isInsideStorageFolders(pathValue, host?.storageFolders) ? 'bk.folder.notAllowed' : null;
  const credsNeeded = kind === 'share' && useCreds;
  const userError: TKey | null = credsNeeded && !isValidAccountName(userName.trim()) ? 'bk.repo.userInvalid' : null;
  // A new or changed user, or a repository without a stored password, needs the password now.
  const passwordRequired = credsNeeded && (!repo?.hasCredentials || userName.trim() !== (repo.userName ?? ''));
  const passwordError: TKey | null = passwordRequired && !password ? 'bk.repo.passwordRequired' : password.length > 256 ? 'bk.repo.passwordInvalid' : null;
  const nameError: TKey | null = !BACKUP_NAME.test(name.trim()) ? 'bk.name.invalid' : null;
  const valid = !nameError && !pathError && (kind === 'share' || !!hostId) && !userError && !passwordError && description.length <= 500;
  const removingCreds = editing && repo.hasCredentials && !useCreds;

  return (
    <ActionDialog title={editing ? t('bk.repo.editTitle', { name: repo.name }) : t('bk.repo.newTitle')} confirmLabel={t('common.save')} disabled={!valid} wide onClose={onClose}
      onConfirm={async () => {
        const creds = credsNeeded ? { userName: userName.trim(), password: password || undefined } : { userName: '', password: '' };
        if (editing) {
          await put(`/api/v1/backup/repositories/${repo.id}`, { name: name.trim(), description: description.trim() || undefined, ...creds });
        } else {
          await post('/api/v1/backup/repositories', {
            name: name.trim(), description: description.trim() || undefined, path: pathValue, hostId: kind === 'local' ? hostId : undefined,
            ...(credsNeeded ? { userName: userName.trim(), password } : {}),
          });
        }
        toast.success(t('bk.repo.saved', { name: name.trim() }), t('bk.repo.checkSuggestion'));
        onSaved();
      }}>
      <Field label={t('common.name')} htmlFor="br-name" error={name && nameError ? t(nameError) : undefined}>
        <Input id="br-name" value={name} maxLength={64} onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>
      <Field label={t('common.description')} htmlFor="br-desc" aside={`${description.length}/500`}>
        <Textarea id="br-desc" rows={2} maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      {editing ? (
        <Notice kind="info" title={t(repo.isShare ? 'bk.repo.kind.share' : 'bk.repo.kind.local')}>
          <Mono className="break-all">{repo.path}</Mono>{repo.hostName && <> · {repo.hostName}</>}
          <span className="mt-1 block text-xs">{t('bk.repo.pathFixed')}</span>
        </Notice>
      ) : (
        <>
          <RadioGroup value={kind} onValueChange={(v) => { setKind(v as 'local' | 'share'); setPath(''); }} className="gap-2 sm:grid-cols-2">
            {(['share', 'local'] as const).map((k) => (
              <label key={k} className="flex cursor-pointer items-start gap-2 rounded-md border p-2 text-sm has-[:checked]:border-primary/50 has-[:checked]:bg-primary/5">
                <RadioGroupItem value={k} className="mt-0.5" />
                <span><span className="font-medium">{t(('bk.repo.kind.' + k) as TKey)}</span><span className="block text-xs text-muted-foreground">{t(('bk.repo.kind.' + k + 'Help') as TKey)}</span></span>
              </label>
            ))}
          </RadioGroup>
          {kind === 'local' && (
            <Field label={t('bk.repo.host')} htmlFor="br-host">
              <NativeSelect id="br-host" value={hostId} onChange={(e) => { setHostId(e.target.value); setPath(''); }}>
                {hosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}{h.connected ? '' : ` (${t('host.offline')})`}</option>)}
              </NativeSelect>
            </Field>
          )}
          <Field label={t('bk.repo.path')} htmlFor="br-path" hint={t(kind === 'share' ? 'bk.repo.shareHint' : 'bk.repo.localHint')} error={path && pathError ? t(pathError) : undefined}>
            <div className="flex gap-2">
              <Input id="br-path" className="font-mono" value={path} placeholder={kind === 'share' ? '\\\\nas01\\backups\\hyperv' : 'D:\\Backups'} onChange={(e) => setPath(e.target.value)} aria-invalid={!!path && !!pathError} />
              {kind === 'local' && <Button type="button" variant="outline" disabled={!host?.canBrowseFolders} onClick={() => setPicking(true)}><FolderOpen />{t('picker.browse')}</Button>}
            </div>
          </Field>
          {kind === 'local' && host?.storageFolders && host.storageFolders.length > 0 && <p className="text-xs text-muted-foreground">{t('move.allowedFolders', { list: host.storageFolders.join(', ') })}</p>}
        </>
      )}
      {kind === 'share' && (
        <section className="space-y-3 rounded-lg border p-3">
          <label className="flex items-start gap-2 text-sm">
            <Checkbox checked={useCreds} onCheckedChange={(v) => setUseCreds(!!v)} className="mt-0.5" />
            <span><span className="font-medium">{t('bk.repo.useCreds')}</span><span className="block text-xs text-muted-foreground">{t('bk.repo.useCredsHelp')}</span></span>
          </label>
          {useCreds && (
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label={t('bk.repo.user')} htmlFor="br-user" hint={t('bk.repo.userHint')} error={userName && userError ? t(userError) : undefined}>
                <Input id="br-user" autoComplete="off" value={userName} placeholder="NAS01\backup" onChange={(e) => setUserName(e.target.value)} />
              </Field>
              <Field label={t('bk.repo.password')} htmlFor="br-pw" error={passwordError && (password || !passwordRequired) ? t(passwordError) : undefined}
                aside={repo?.hasCredentials && !passwordRequired ? t('bk.repo.passwordStored') : undefined}>
                <Input id="br-pw" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)}
                  placeholder={repo?.hasCredentials && !passwordRequired ? t('bk.repo.passwordKeep') : ''} />
              </Field>
            </div>
          )}
          {useCreds && <Notice kind="warn">{t('bk.repo.stagingNote')}</Notice>}
          {!useCreds && <p className="text-xs text-muted-foreground">{t('bk.repo.computerAccountNote')}</p>}
          {removingCreds && <Notice kind="warn">{t('bk.repo.removeCreds')}</Notice>}
          {repo?.credentialUpdatedAt && repo.hasCredentials && useCreds && <p className="text-xs text-muted-foreground">{t('bk.repo.credsUpdated', { at: fmtDate(repo.credentialUpdatedAt) })}</p>}
        </section>
      )}
      {picking && host && (
        <HostFolderPicker hostId={host.id} mode="folder" initialPath={path || null} onClose={() => setPicking(false)} onPickFolder={(f) => { setPath(f.path); setPicking(false); }} />
      )}
    </ActionDialog>
  );
}

/**
 * Check a repository from a host (POST /backup/repositories/{id}/check): the host must reach and write the folder, have
 * the Hyper-V cmdlets and, for credentials, a staging folder; the VMs of the host are checked too. The result is kept.
 */
export function CheckRepositoryDialog({ repo, onChecked, onClose }: { repo: Repository; onChecked: () => void; onClose: () => void }) {
  const { t, fmtDate } = useI18n();
  const inv = useInventory();
  const candidates = inv.hosts.filter((h) => h.status === 'Active' && h.connected && h.agent7 && (!repo.hostId || h.id === repo.hostId));
  const [hostId, setHostId] = useState(repo.hostId ?? (candidates.find((h) => h.id === repo.lastCheckHostId)?.id ?? candidates[0]?.id ?? ''));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [result, setResult] = useState<CheckResponse | null>(null);
  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      setResult(await post<CheckResponse>(`/api/v1/backup/repositories/${repo.id}/check`, { hostId: hostId || undefined }));
      onChecked();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  const shown = result ? { host: result.host, checks: result.checks, at: null } : repo.lastCheck ? { host: repo.lastCheck.host, checks: repo.lastCheck.checks, at: repo.lastCheckedAt } : null;
  return (
    <Dialog open onOpenChange={(o) => { if (!o && !busy) onClose(); }}>
      <DialogContent className="flex max-h-[calc(100dvh-2rem)] flex-col overflow-hidden sm:max-w-2xl">
        <DialogHeader className="shrink-0 pr-6">
          <DialogTitle>{t('bk.repo.checkTitle', { name: repo.name })}</DialogTitle>
          <DialogDescription>{t('bk.repo.checkHelp')}</DialogDescription>
        </DialogHeader>
        <div className="-mx-4 min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-0.5">
          <Field label={t('bk.repo.checkFrom')} htmlFor="rc-host" hint={repo.isShare ? t('bk.repo.checkFromHint') : undefined}>
            <NativeSelect id="rc-host" value={hostId} disabled={!!repo.hostId} onChange={(e) => setHostId(e.target.value)}>
              {candidates.length === 0 && <option value="">{t('bk.repo.noCheckHost')}</option>}
              {candidates.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
            </NativeSelect>
          </Field>
          {busy && <p className="flex items-center gap-2 text-sm text-muted-foreground"><LoaderCircle className="size-4 animate-spin" />{t('bk.now.checking')}</p>}
          <ErrorAlert error={error} />
          {shown && !busy && (
            <section className="space-y-2">
              <div className="text-xs text-muted-foreground">{shown.at ? t('bk.repo.lastCheck', { at: fmtDate(shown.at), host: shown.host }) : t('bk.repo.checkedFrom', { host: shown.host })}</div>
              <ReadinessList checks={shown.checks} hostId={result?.hostId ?? repo.lastCheckHostId ?? undefined} onNavigate={onClose} />
            </section>
          )}
        </div>
        <DialogFooter className="shrink-0">
          <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{t('common.close')}</Button>
          <Button type="button" onClick={() => void run()} disabled={busy || candidates.length === 0}><Play />{t('bk.repo.runCheck')}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Rescan (POST /backup/repositories/{id}/rescan): restore points found in the folder and unknown to the portal are added back, Unverified. */
export function RescanRepositoryDialog({ repo, onDone, onClose }: { repo: Repository; onDone: () => void; onClose: () => void }) {
  const { t } = useI18n();
  return (
    <ActionDialog title={t('bk.repo.rescanTitle', { name: repo.name })} confirmLabel={t('bk.repo.rescan')} onClose={onClose}
      onConfirm={async () => {
        const r = await post<RescanResponse>(`/api/v1/backup/repositories/${repo.id}/rescan`, {});
        toast.success(t('bk.repo.rescanDone', { found: r.found, added: r.added }), r.added > 0 ? t('bk.repo.rescanVerify') : undefined);
        onDone();
      }}>
      <p className="text-sm">{t('bk.repo.rescanHelp')}</p>
      <Notice kind="info">{t('bk.repo.rescanVerify')}</Notice>
    </ActionDialog>
  );
}

export function DeleteRepositoryDialog({ repo, onDone, onClose }: { repo: Repository; onDone: () => void; onClose: () => void }) {
  const { t } = useI18n();
  return (
    <ActionDialog title={t('bk.repo.deleteTitle', { name: repo.name })} confirmLabel={t('common.delete')} danger onClose={onClose}
      onConfirm={async () => { await del(`/api/v1/backup/repositories/${repo.id}`); onDone(); }}>
      <p className="text-sm">{t('bk.repo.deleteHelp')}</p>
      {repo.points > 0 && <Notice kind="warn">{t('bk.repo.deleteHasPoints', { n: repo.points })}</Notice>}
    </ActionDialog>
  );
}
