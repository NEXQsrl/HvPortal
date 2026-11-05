import { DatabaseBackup, Download, RotateCcw, Upload, X } from 'lucide-react';
import { useRef, useState } from 'react';
import { del, get, post, rawRequest } from '@/api/client';
import { ErrorAlert, KeyValue, Notice, Section } from '@/components/common';
import { Field, HelpTip } from '@/components/dialogs';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useI18n } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { toast } from '@/lib/toast';

interface Manifest {
  format: string; formatVersion: number; productVersion: string; createdAt: string; createdBy: string | null; machine: string;
  lastMigration: string | null; users: number; hosts: number; vms: number; groups: number; databaseBytes: number;
}
interface PendingState { pending: { manifest: Manifest; stagedAt: string; stagedBy: string } | null; lastApplied: { appliedAt: string; previousStateKeptIn: string } | null; runningAsService: boolean }

const MIN = 20;

/**
 * Settings > Backup and restore of the portal: sealed file (database snapshot + key ring + internal CA) protected by a
 * passphrase; restore is previewed, confirmed with a typed word and applied at the next service start.
 */
export function BackupSection() {
  const { t, fmtDate } = useI18n();
  const state = useLoad(() => get<PendingState>('/api/v1/settings/backup/restore'));
  const [pass, setPass] = useState('');
  const [pass2, setPass2] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [rpass, setRpass] = useState('');
  const [preview, setPreview] = useState<Manifest | null>(null);
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const input = useRef<HTMLInputElement>(null);

  const run = async (key: string, fn: () => Promise<void>) => {
    setBusy(key);
    setError(null);
    try { await fn(); } catch (e) { setError(e); } finally { setBusy(null); }
  };

  const exportBackup = () => run('export', async () => {
    const r = await rawRequest('POST', '/api/v1/settings/backup/export', JSON.stringify({ passphrase: pass }), { 'Content-Type': 'application/json' });
    const name = /filename="?([^";]+)"?/.exec(r.headers.get('Content-Disposition') ?? '')?.[1] ?? 'hvportal-backup.hvpbak';
    const url = URL.createObjectURL(await r.blob());
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
    setPass('');
    setPass2('');
    toast.success(t('backup.exported'), name);
  });

  const upload = (apply: boolean) => run(apply ? 'apply' : 'preview', async () => {
    if (!file) return;
    const headers: Record<string, string> = { 'Content-Type': 'application/octet-stream', 'X-HvPortal-Passphrase': rpass };
    if (apply) headers['X-HvPortal-Confirm'] = confirm.trim();
    const r = await rawRequest('POST', `/api/v1/settings/backup/restore${apply ? '?apply=true' : ''}`, file, headers);
    const body = await r.json() as { staged: boolean; manifest: Manifest };
    setPreview(body.manifest);
    if (body.staged) {
      setFile(null);
      setRpass('');
      setConfirm('');
      setPreview(null);
      state.reload();
    }
  });

  const pending = state.data?.pending;
  return (
    <Section title={<span className="flex items-center gap-2"><DatabaseBackup className="size-4 text-muted-foreground" />{t('backup.title')}<HelpTip>{t('backup.help')}</HelpTip></span>} bodyClassName="space-y-4 p-4">
      <ErrorAlert error={error ?? state.error} />

      {pending && (
        <Notice kind="warn" title={t('backup.pendingTitle')}>
          <p className="text-xs">{t('backup.pendingText', { date: fmtDate(pending.manifest.createdAt), version: pending.manifest.productVersion, machine: pending.manifest.machine })}</p>
          <div className="mt-2 flex flex-wrap gap-2">
            {state.data?.runningAsService
              ? <Button size="sm" variant="destructive" disabled={!!busy} onClick={() => void run('restart', async () => { await post('/api/v1/settings/backup/restart'); toast.info(t('backup.restarting')); })}><RotateCcw />{t('backup.restartNow')}</Button>
              : <span className="text-xs">{t('backup.restartManual')}</span>}
            <Button size="sm" variant="outline" disabled={!!busy} onClick={() => void run('cancel', async () => { await del('/api/v1/settings/backup/restore'); state.reload(); })}><X />{t('backup.cancelRestore')}</Button>
          </div>
        </Notice>
      )}
      {state.data?.lastApplied && !pending && (
        <Notice kind="info">{t('backup.lastApplied', { date: fmtDate(state.data.lastApplied.appliedAt), path: state.data.lastApplied.previousStateKeptIn })}</Notice>
      )}

      <div className="space-y-3 rounded-lg border p-4">
        <h3 className="flex items-center gap-1.5 text-sm font-medium">{t('backup.create')}<HelpTip>{t('backup.storeSafely')}</HelpTip></h3>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('backup.passphrase')} htmlFor="bk-pass" hint={t('backup.passHint', { n: MIN })}><Input id="bk-pass" type="password" autoComplete="new-password" value={pass} onChange={(e) => setPass(e.target.value)} /></Field>
          <Field label={t('backup.passphrase2')} htmlFor="bk-pass2" error={pass2 && pass2 !== pass ? t('backup.mismatch') : null}><Input id="bk-pass2" type="password" autoComplete="new-password" value={pass2} onChange={(e) => setPass2(e.target.value)} /></Field>
        </div>
        <Button size="sm" disabled={pass.length < MIN || pass !== pass2 || !!busy} onClick={() => void exportBackup()}><Download />{t('backup.download')}</Button>
      </div>

      <div className="space-y-3 rounded-lg border p-4">
        <h3 className="text-sm font-medium">{t('backup.restore')}</h3>
        <div className="flex flex-wrap items-center gap-2">
          <Button size="sm" variant="outline" onClick={() => input.current?.click()}><Upload />{t('backup.chooseFile')}</Button>
          <span className="text-xs text-muted-foreground">{file ? `${file.name} (${formatBytes(file.size)})` : t('backup.noFile')}</span>
          <input ref={input} type="file" accept=".hvpbak" className="hidden" onChange={(e) => { setFile(e.target.files?.[0] ?? null); setPreview(null); e.target.value = ''; }} />
        </div>
        <Field label={t('backup.passphrase')} htmlFor="bk-rpass"><Input id="bk-rpass" type="password" autoComplete="off" value={rpass} onChange={(e) => { setRpass(e.target.value); setPreview(null); }} /></Field>
        <Button size="sm" variant="outline" disabled={!file || !rpass || !!busy} onClick={() => void upload(false)}>{t('backup.verify')}</Button>
        {preview && (
          <div className="space-y-2 rounded-md bg-muted/50 p-3">
            <KeyValue items={[
              [t('backup.createdAt'), fmtDate(preview.createdAt)],
              [t('backup.version'), preview.productVersion],
              [t('backup.machine'), preview.machine],
              [t('backup.createdBy'), preview.createdBy ?? '—'],
              [t('backup.contents'), t('backup.counts', { users: preview.users, hosts: preview.hosts, vms: preview.vms, groups: preview.groups })],
              [t('backup.size'), formatBytes(preview.databaseBytes)],
            ]} />
            <Notice kind="danger">{t('backup.restoreWarning')}</Notice>
            <Field label={t('backup.typeConfirm')} htmlFor="bk-confirm"><Input id="bk-confirm" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="off" placeholder="RIPRISTINA" /></Field>
            <Button size="sm" variant="destructive" disabled={!['RIPRISTINA', 'RESTORE'].includes(confirm.trim()) || !!busy} onClick={() => void upload(true)}><RotateCcw />{t('backup.stage')}</Button>
          </div>
        )}
      </div>
    </Section>
  );
}
