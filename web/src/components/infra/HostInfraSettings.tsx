import { Archive, Download, FolderLock, Plus, RefreshCw, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { post, put } from '@/api/client';
import type { Accepted } from '@/api/ops-types';
import type { Host } from '@/api/types';
import { ErrorAlert, Notice, Section, StatusBadge } from '@/components/common';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes, shortVersion } from '@/lib/format';
import { driveOf, insideStorageFolders, stagingFolderOk, suggestStagingPath, volumeOf } from '@/lib/staging';
import { toast } from '@/lib/toast';
import { useSession } from '@/session';
import { useAgentUpdates } from './AgentUpdates';

const folderOk = stagingFolderOk;

/**
 * Folders of the host the portal may see. For a host with its own agent the list is decided on the host when the agent
 * is installed (Install-Agent -StorageFolders) and only shown here; for a remote host it is kept by the portal.
 */
export function StorageFoldersSection({ host, onChanged }: { host: Host; onChanged: () => void }) {
  const { t } = useI18n();
  const { can } = useSession();
  const editable = !!host.storageFoldersEditable && can('hosts.manage');
  const current = host.storageFolders ?? [];
  const [folders, setFolders] = useState<string[]>(current);
  const [adding, setAdding] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const dirty = folders.join('|') !== current.join('|');
  const add = adding.trim().replace(/\\+$/, '');
  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await put(`/api/v1/hosts/${host.id}/storage-folders`, { folders });
      toast.success(t('settings.saved'));
      onChanged();
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return (
    <Section title={<span className="flex items-center gap-2"><FolderLock className="size-4 text-muted-foreground" />{t('sfold.title')}</span>} bodyClassName="space-y-3 p-3">
      <p className="text-xs text-muted-foreground">{t('sfold.help')}</p>
      {/* null: the agent is older than 0.7.0 and does not report (nor enforce) a list. */}
      {host.storageFolders == null && !editable
        ? <Notice kind="info">{t('sfold.unknown')}</Notice>
        : (editable ? folders : current).length === 0
          ? <Notice kind="info">{t('sfold.none')}</Notice>
          : (
            <div className="space-y-1">
              {(editable ? folders : current).map((p) => (
                <div key={p} className="flex items-center gap-2 rounded-md border px-2 py-1">
                  <FolderLock className="size-4 text-muted-foreground" /><span className="flex-1 font-mono text-xs">{p}</span>
                  {editable && <Button size="icon-xs" variant="ghost" aria-label={t('common.remove')} onClick={() => setFolders(folders.filter((x) => x !== p))}><Trash2 /></Button>}
                </div>
              ))}
            </div>
          )}
      {editable ? (
        <>
          <form className="flex flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); if (folderOk(add) && !folders.some((f) => f.toLowerCase() === add.toLowerCase())) { setFolders([...folders, add]); setAdding(''); } }}>
            <Input value={adding} onChange={(e) => setAdding(e.target.value)} placeholder="D:\VMs" className="w-72 font-mono" aria-label={t('sfold.add')} aria-invalid={!!adding && !folderOk(add)} />
            <Button type="submit" size="sm" variant="outline" disabled={!folderOk(add) || folders.length >= 16}><Plus />{t('sfold.add')}</Button>
            <span className="flex-1" />
            <Button type="button" size="sm" disabled={!dirty || busy} onClick={() => void save()}>{t('common.save')}</Button>
          </form>
          {adding && !folderOk(add) && <div className="text-xs text-destructive">{t('host.folderInvalid')}</div>}
          <p className="text-xs text-muted-foreground">{t('sfold.remoteHelp')}</p>
        </>
      ) : host.connectionMode !== 'Remote' && (
        <div className="space-y-1 text-xs text-muted-foreground">
          <p>{t('sfold.agentHelp')}</p>
          <pre className="command">{`.\\Install-Agent.ps1 -Source ..\\agent -StorageFolders 'D:\\VMs','E:\\Templates'`}</pre>
        </div>
      )}
      <ErrorAlert error={error} />
    </Section>
  );
}

/** Local folder of the host where backups to a NAS share are first written, then copied (and verified) to the share. */
export function BackupStagingSection({ host, onChanged }: { host: Host; onChanged: () => void }) {
  const { t } = useI18n();
  const { can } = useSession();
  const manage = can('hosts.manage') || can('backup.manage');
  const [path, setPath] = useState(host.backupStagingPath ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const value = path.trim();
  const valid = value === '' || folderOk(value);
  const dirty = value !== (host.backupStagingPath ?? '');
  // A folder that can work on this host (its drives and allowed folders), instead of a fixed example.
  const suggestion = suggestStagingPath(host);
  const drive = valid && value ? driveOf(value) : null;
  const volume = volumeOf(host, drive);
  const unknownDrive = !!drive && host.volumes.length > 0 && !volume;
  const outside = !!value && valid && !insideStorageFolders(value, host.storageFolders);
  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await put(`/api/v1/hosts/${host.id}/backup-staging`, { path: value || null });
      toast.success(t('settings.saved'));
      onChanged();
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return (
    <Section title={<span className="flex items-center gap-2"><Archive className="size-4 text-muted-foreground" />{t('staging.title')}</span>} bodyClassName="space-y-3 p-3">
      <p className="text-xs text-muted-foreground">{t('staging.help')}</p>
      {host.clusterId && (
        <p className="text-xs"><Link className="text-primary hover:underline" to={`/clusters/${host.clusterId}?tab=nodes`}>{t('staging.clusterHint')}</Link></p>
      )}
      {manage ? (
        <form className="flex flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); if (valid && dirty) void save(); }}>
          <Input value={path} onChange={(e) => setPath(e.target.value)} placeholder={suggestion ?? 'C:\\HvPortal-staging'} className="w-80 font-mono" aria-label={t('staging.title')} aria-invalid={!valid} />
          <Button type="submit" size="sm" disabled={!valid || !dirty || busy || unknownDrive || outside}>{t('common.save')}</Button>
          {suggestion && value.toLowerCase() !== suggestion.toLowerCase() && (
            <Button type="button" size="sm" variant="outline" onClick={() => setPath(suggestion)}>{t('staging.useSuggestion', { path: suggestion })}</Button>
          )}
        </form>
      ) : <div className="font-mono text-xs">{host.backupStagingPath ?? t('common.none')}</div>}
      {!valid && <div className="text-xs text-destructive">{t('staging.invalid')}</div>}
      {unknownDrive && <div className="text-xs text-destructive">{t('staging.noDrive', { drive: `${drive}:`, drives: host.volumes.map((v) => v.name).join(', ') })}</div>}
      {outside && <div className="text-xs text-destructive">{t('staging.outside')}</div>}
      {volume && !outside && <div className="text-xs text-muted-foreground">{t('staging.free', { drive: volume.name, free: formatBytes(volume.freeBytes) })}</div>}
      {host.storageFolders && host.storageFolders.length > 0 && <p className="text-xs text-muted-foreground">{t('move.allowedFolders', { list: host.storageFolders.join(', ') })}</p>}
      <ErrorAlert error={error} />
    </Section>
  );
}

/** Agent of the host: version, updater task, last update and "Update from the portal". */
export function AgentUpdateSection({ host }: { host: Host }) {
  const { t } = useI18n();
  const { can } = useSession();
  const ops = useOps();
  const admin = can('hosts.manage');
  const data = useAgentUpdates(admin && host.connectionMode !== 'Remote');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  if (!admin || host.connectionMode === 'Remote') return null;
  const state = data.data?.agents.find((a) => a.kind === 'host' && a.id === host.id);
  const update = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await post<Accepted>(`/api/v1/hosts/${host.id}/agent/update`);
      ops.queued(r, `${t('jobop.agent.update.Host')} — ${host.displayName}`);
      data.reload();
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return (
    <Section title={<span className="flex items-center gap-2"><RefreshCw className="size-4 text-muted-foreground" />{t('upd.hostTitle')}</span>} bodyClassName="space-y-2 p-3 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <span>{t('guide.update.installed')}: <span className="font-mono text-xs">{shortVersion(host.agentVersion)}</span></span>
        {data.data?.package?.version && <span className="text-muted-foreground">· {t('guide.update.package', { v: data.data.package.version })}</span>}
        {state && (state.outdated ? <StatusBadge tone="warn">{t('guide.update.outdated')}</StatusBadge> : <StatusBadge tone="ok">{t('guide.update.upToDate')}</StatusBadge>)}
        {host.updaterInstalled === true && <StatusBadge tone="muted">{t('upd.updaterOn')}</StatusBadge>}
        {host.updaterInstalled === false && <StatusBadge tone="muted">{t('upd.updaterOff')}</StatusBadge>}
      </div>
      {host.lastUpdateResult && <div className="text-xs text-muted-foreground [overflow-wrap:anywhere]">{t('upd.last')}: {host.lastUpdateResult}</div>}
      {state?.outdated && !state.canUpdate && <Notice kind="info">{t(('upd.reason.' + (state.reason ?? 'other')) as TKey)}</Notice>}
      <div className="flex flex-wrap gap-2">
        {state?.runningJobId
          ? <Button size="sm" variant="outline" asChild><Link to={`/jobs/${state.runningJobId}`}>{t('upd.viewJob')}</Link></Button>
          : state?.canUpdate && <Button size="sm" disabled={busy} onClick={() => void update()}><Download />{t('upd.updateTo', { v: data.data?.package?.version ?? '' })}</Button>}
        <Button size="sm" variant="ghost" asChild><Link to="/guide/update">{t('upd.open')}</Link></Button>
      </div>
      <ErrorAlert error={data.error ?? error} />
    </Section>
  );
}
