import { ArrowUp, ChevronRight, Download, File as FileIcon, Folder, FolderOpen, HardDrive, LoaderCircle, Plug, RefreshCw, Unplug } from 'lucide-react';
import { useEffect, useState } from 'react';
import { ApiError, get, post } from '@/api/client';
import type { FolderListing } from '@/api/ops-types';
import { Empty, ErrorAlert, Notice } from '@/components/common';
import { Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { NativeSelect } from '@/components/ui/native-select';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { toast } from '@/lib/toast';
import { cn } from '@/lib/utils';
import { MOUNT_IDLE_HOURS, type BackupPoint, type MountInfo, type MountResponse, type Repository } from './backup-api';
import { HostSelect, hostChoices } from './RestoreDialogs';

/**
 * Read-only browser of a restore point disk attached to a host (GET /backup/mounts/{id}/files): volumes, folders,
 * files; each file is downloaded through the portal (audited). Only the user who attached the disk can browse it.
 */
export function MountBrowser({ mountId, className }: { mountId: string; className?: string }) {
  const { t, fmtDate } = useI18n();
  const [path, setPath] = useState<string | null>(null);
  const [data, setData] = useState<FolderListing | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(false);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    get<FolderListing>(`/api/v1/backup/mounts/${mountId}/files${path ? `?path=${encodeURIComponent(path)}` : ''}`)
      .then((r) => { if (!cancelled) setData(r); })
      .catch((e) => { if (!cancelled) { setError(e); setData(null); } })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [mountId, path, tick]);

  const crumbs = path ? path.replace(/\\$/, '').split('\\').map((seg, i, all) => ({ name: i === 0 ? seg + '\\' : seg, path: i === 0 ? seg + '\\' : all.slice(0, i + 1).join('\\') })) : [];
  const gone = error instanceof ApiError && (error.status === 404 || error.code === 'not_found');
  const folders = data?.folders ?? [];
  const files = data?.files ?? [];

  return (
    <div className={cn('flex min-h-0 flex-col rounded-lg border bg-card', className)}>
      <div className="flex flex-wrap items-center gap-1 border-b bg-muted/40 px-2 py-1.5">
        <Button size="icon-sm" variant="ghost" aria-label={t('picker.up')} disabled={!path} onClick={() => setPath(data?.parent ?? null)}><ArrowUp /></Button>
        <nav aria-label={t('picker.location')} className="flex min-w-0 flex-1 items-center gap-0.5 overflow-x-auto text-sm">
          <button type="button" className="flex shrink-0 items-center gap-1 rounded px-1.5 py-0.5 hover:bg-accent" onClick={() => setPath(null)}><HardDrive className="size-3.5" />{t('bk.files.volumes')}</button>
          {crumbs.map((c) => (
            <span key={c.path} className="flex shrink-0 items-center gap-0.5">
              <ChevronRight className="size-3.5 text-muted-foreground" />
              <button type="button" className="rounded px-1.5 py-0.5 hover:bg-accent" onClick={() => setPath(c.path)}>{c.name}</button>
            </span>
          ))}
        </nav>
        <Button size="icon-sm" variant="ghost" aria-label={t('common.refresh')} onClick={() => setTick((x) => x + 1)}><RefreshCw className={cn(loading && 'animate-spin')} /></Button>
      </div>
      <div className="min-h-64 flex-1 overflow-auto">
        <div className="px-2 empty:hidden">{gone ? <Notice kind="warn" className="my-2">{t('bk.files.gone', { h: MOUNT_IDLE_HOURS })}</Notice> : <ErrorAlert error={error} />}</div>
        {loading && !data && <p className="p-3 text-sm text-muted-foreground">{t('common.loading')}</p>}
        {data && !path && (
          folders.length === 0 ? <Empty>{t('bk.files.noVolumes')}</Empty> : (
            <div className="grid gap-2 p-2 sm:grid-cols-2">
              {folders.map((v) => (
                <button key={v.path} type="button" onClick={() => setPath(v.path)} className="flex items-center gap-3 rounded-lg border bg-background p-3 text-left hover:border-primary/50 hover:bg-accent">
                  <HardDrive className="size-5 text-muted-foreground" />
                  <span className="min-w-0 flex-1 truncate text-sm font-medium">{v.name}</span>
                </button>
              ))}
            </div>
          )
        )}
        {data && path && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('files.name')}</TableHead>
                <TableHead className="w-28 text-right">{t('common.size')}</TableHead>
                <TableHead className="hidden w-44 md:table-cell">{t('files.modified')}</TableHead>
                <TableHead className="w-12"><span className="sr-only">{t('files.download')}</span></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {folders.map((f) => (
                <TableRow key={f.path} className="group cursor-pointer" onClick={() => setPath(f.path)} tabIndex={0}
                  onKeyDown={(e) => { if (e.key === 'Enter') setPath(f.path); }} aria-label={t('picker.open', { name: f.name })}>
                  <TableCell><span className="flex items-center gap-2"><Folder className="size-4 text-amber-500 group-hover:hidden" /><FolderOpen className="hidden size-4 text-amber-500 group-hover:block" />{f.name}</span></TableCell>
                  <TableCell /><TableCell className="hidden md:table-cell" />
                  <TableCell className="text-right"><ChevronRight className="ml-auto size-4 text-muted-foreground" /></TableCell>
                </TableRow>
              ))}
              {files.map((f) => (
                <TableRow key={f.path}>
                  <TableCell><span className="flex min-w-0 items-center gap-2"><FileIcon className="size-4 shrink-0 text-muted-foreground" /><span className="truncate">{f.name}</span></span></TableCell>
                  <TableCell className="text-right text-xs whitespace-nowrap tabular">{formatBytes(f.sizeBytes)}</TableCell>
                  <TableCell className="hidden text-xs whitespace-nowrap tabular md:table-cell">{fmtDate(f.modifiedUtc)}</TableCell>
                  <TableCell className="text-right">
                    <Button asChild size="icon-sm" variant="ghost" aria-label={t('files.downloadName', { name: f.name })} title={t('files.download')}>
                      <a href={`/api/v1/backup/mounts/${mountId}/download?path=${encodeURIComponent(f.path)}`} download={f.name}><Download /></a>
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
              {folders.length + files.length === 0 && (
                <TableRow><TableCell colSpan={4} className="py-8 text-center text-sm text-muted-foreground">{t('files.empty')}</TableCell></TableRow>
              )}
            </TableBody>
          </Table>
        )}
        {data?.truncated && <p className="px-3 py-2 text-xs text-muted-foreground">{t('files.truncated')}</p>}
      </div>
    </div>
  );
}

/** Detach a disk opened for file restore (POST /backup/mounts/{id}/dismount). */
export async function dismount(mountId: string, t: ReturnType<typeof useI18n>['t']): Promise<boolean> {
  try {
    await post(`/api/v1/backup/mounts/${mountId}/dismount`);
    toast.success(t('bk.files.dismounted'));
    return true;
  } catch (e) {
    toast.error(t('common.error'), e instanceof Error ? e.message : String(e));
    return false;
  }
}

/**
 * File-level restore of a restore point: choose a disk and the host that attaches it (read-only), then browse its
 * volumes and download files. The disk stays attached until "Detach" or MOUNT_IDLE_HOURS without use.
 */
export function FileRestoreDialog({ point, repo, onClose, onChanged }: { point: BackupPoint; repo?: Repository; onClose: () => void; onChanged?: () => void }) {
  const { t, fmtDate } = useI18n();
  const inv = useInventory();
  const choices = hostChoices(inv.hosts, repo, 'mount');
  const usable = choices.filter((c) => !c.reason);
  const [disk, setDisk] = useState(point.disks[0] ?? '');
  const [hostId, setHostId] = useState(usable.find((c) => c.host.id === point.hostId)?.host.id ?? usable[0]?.host.id ?? '');
  const [mount, setMount] = useState<{ mountId: string; hostId: string; staged: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const host = inv.hostById.get(mount?.hostId ?? hostId);
  const staged = !!repo?.hasCredentials;

  const open = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await post<MountResponse>(`/api/v1/backup/points/${point.id}/mount`, { disk, hostId });
      setMount({ mountId: r.mountId, hostId: r.hostId, staged: r.staged });
      onChanged?.();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  const detach = async () => {
    if (!mount) return;
    setBusy(true);
    const ok = await dismount(mount.mountId, t);
    setBusy(false);
    if (ok) { onChanged?.(); onClose(); }
  };

  return (
    <Dialog open onOpenChange={(o) => { if (!o && !busy) onClose(); }}>
      <DialogContent className="flex max-h-[calc(100dvh-2rem)] flex-col overflow-hidden sm:max-w-3xl">
        <DialogHeader className="shrink-0 pr-6">
          <DialogTitle>{t('bk.files.title', { name: point.vmName })}</DialogTitle>
          <DialogDescription>{t('bk.restore.from', { date: fmtDate(point.createdAt), repo: point.repositoryName ?? '—' })}</DialogDescription>
        </DialogHeader>
        <div className="-mx-4 min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-0.5">
          {!mount ? (
            <>
              <Notice kind="info" title={t('bk.files.howTitle')}>{t('bk.files.how', { h: MOUNT_IDLE_HOURS })}</Notice>
              {point.status === 'Unverified' && <Notice kind="warn">{t('bk.restore.unverified')}</Notice>}
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label={t('bk.disk.disk')} htmlFor="fr-disk">
                  <NativeSelect id="fr-disk" value={disk} onChange={(e) => setDisk(e.target.value)}>
                    {point.disks.map((d) => <option key={d} value={d}>{d}</option>)}
                  </NativeSelect>
                </Field>
                <Field label={t('bk.files.host')} htmlFor="fr-host" hint={t('bk.files.hostHint')}>
                  <HostSelect id="fr-host" value={hostId} onChange={setHostId} choices={choices} />
                </Field>
              </div>
              {usable.length === 0 && <Notice kind="warn">{t('bk.files.noHost')}</Notice>}
              {staged && <Notice kind="warn">{t('bk.files.stagedNote')}</Notice>}
              {busy && <p className="flex items-center gap-2 text-sm text-muted-foreground"><LoaderCircle className="size-4 animate-spin" />{t(staged ? 'bk.files.openingStaged' : 'bk.files.opening')}</p>}
            </>
          ) : (
            <>
              <Notice kind="ok">{t('bk.files.opened', { host: host?.displayName ?? '—', h: MOUNT_IDLE_HOURS })}</Notice>
              <MountBrowser mountId={mount.mountId} className="min-h-80" />
              <p className="text-xs text-muted-foreground">{t('bk.files.permissions')}</p>
            </>
          )}
          <ErrorAlert error={error} />
        </div>
        <DialogFooter className="shrink-0">
          {!mount ? (
            <>
              <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{t('common.cancel')}</Button>
              <Button type="button" onClick={() => void open()} disabled={busy || !disk || !hostId}><Plug />{t('bk.files.open')}</Button>
            </>
          ) : (
            <>
              <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{t('bk.files.keepOpen')}</Button>
              <Button type="button" variant="destructive" onClick={() => void detach()} disabled={busy}><Unplug />{t('bk.files.dismount')}</Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Browse a disk that is already open (from the list of file restore sessions). */
export function MountBrowseDialog({ mount, onClose, onChanged }: { mount: MountInfo; onClose: () => void; onChanged: () => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const [busy, setBusy] = useState(false);
  return (
    <Dialog open onOpenChange={(o) => { if (!o && !busy) onClose(); }}>
      <DialogContent className="flex max-h-[calc(100dvh-2rem)] flex-col overflow-hidden sm:max-w-3xl">
        <DialogHeader className="shrink-0 pr-6">
          <DialogTitle>{t('bk.files.title', { name: mount.vmName })}</DialogTitle>
          <DialogDescription><span className="font-mono">{mount.disk}</span> · {inv.hostById.get(mount.hostId)?.displayName ?? '—'}</DialogDescription>
        </DialogHeader>
        <div className="-mx-4 min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-0.5">
          <MountBrowser mountId={mount.mountId} className="min-h-80" />
          <p className="text-xs text-muted-foreground">{t('bk.files.permissions')} {t('bk.files.idle', { h: MOUNT_IDLE_HOURS })}</p>
        </div>
        <DialogFooter className="shrink-0">
          <Button type="button" variant="outline" onClick={onClose} disabled={busy}>{t('bk.files.keepOpen')}</Button>
          <Button type="button" variant="destructive" disabled={busy} onClick={async () => {
            setBusy(true);
            const ok = await dismount(mount.mountId, t);
            setBusy(false);
            if (ok) { onChanged(); onClose(); }
          }}><Unplug />{t('bk.files.dismount')}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Disks this user has open for file restore (GET /backup/mounts): browse again or detach. */
export function MountsList({ mounts, onChanged, onBrowse, className }: { mounts: MountInfo[]; onChanged: () => void; onBrowse: (m: MountInfo) => void; className?: string }) {
  const { t, fmtDate } = useI18n();
  const inv = useInventory();
  if (mounts.length === 0) return null;
  return (
    <div className={cn('rounded-lg border bg-card', className)}>
      <div className="flex items-center gap-2 border-b px-3 py-1.5 text-sm font-semibold"><Plug className="size-4 text-info" />{t('bk.files.sessions')}</div>
      <Table>
        <TableBody>
          {mounts.map((m) => (
            <TableRow key={m.mountId}>
              <TableCell className="font-medium">{m.vmName}</TableCell>
              <TableCell className="max-w-56 truncate font-mono text-xs" title={m.disk}>{m.disk}</TableCell>
              <TableCell className="text-xs">{inv.hostById.get(m.hostId)?.displayName ?? '—'}</TableCell>
              <TableCell className="text-xs text-muted-foreground">{m.volumes.map((v) => v.root).join(' ')}</TableCell>
              <TableCell className="text-xs whitespace-nowrap tabular">{fmtDate(m.createdUtc)}</TableCell>
              <TableCell className="text-right whitespace-nowrap">
                <Button size="xs" variant="outline" onClick={() => onBrowse(m)}><FolderOpen />{t('bk.files.browse')}</Button>{' '}
                <Button size="xs" variant="ghost" className="text-destructive" onClick={async () => { if (await dismount(m.mountId, t)) onChanged(); }}><Unplug />{t('bk.files.dismount')}</Button>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <p className="border-t px-3 py-1.5 text-xs text-muted-foreground">{t('bk.files.idle', { h: MOUNT_IDLE_HOURS })}</p>
    </div>
  );
}
