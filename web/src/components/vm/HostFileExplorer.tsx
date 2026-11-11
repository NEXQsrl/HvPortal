import { ArrowUp, ChevronRight, Download, File as FileIcon, Folder, FolderOpen, FolderPlus, HardDrive, Pencil, RefreshCw, Trash2, Upload, X } from 'lucide-react';
import { useEffect, useRef, useState, type DragEvent } from 'react';
import { ApiError, get, post, uploadFile } from '@/api/client';
import type { FolderEntry, FolderListing } from '@/api/ops-types';
import type { Host } from '@/api/types';
import { KebabMenu, WithContextMenu, type Action } from '@/components/actions';
import { ErrorAlert, Notice } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Progress } from '@/components/ui/progress';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLocalStorage } from '@/lib/hooks';
import { toast } from '@/lib/toast';
import { cn } from '@/lib/utils';

interface UploadItem { id: number; file: File; folder: string; loaded: number; status: 'running' | 'done' | 'error' | 'cancelled'; error?: string; abort?: () => void }

let uploadSeq = 0;

/**
 * Explorer of a host's local fixed drives (agent 0.3.3+): open folders with one click, download files, upload files
 * (button or drag and drop) with progress. System, hidden and linked folders are never listed nor accepted; files
 * open for writing (running VM disks) cannot be downloaded. Everything is audited by the gateway.
 */
/**
 * With <paramref name="groupId"/> the explorer works in the scope of a VM group: it starts from the storage folders of
 * the group on the host and cannot leave them (the server enforces it).
 */
export function HostFileExplorer({ host, groupId, readOnly }: { host: Pick<Host, 'id'>; groupId?: string; readOnly?: boolean }) {
  const { t, fmtDate } = useI18n();
  const scope = groupId ? `&groupId=${groupId}` : '';
  const [path, setPath] = useLocalStorage<string | null>(`hvp.files.${host.id}${groupId ? '.' + groupId : ''}`, null);
  const [data, setData] = useState<FolderListing | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(false);
  const [tick, setTick] = useState(0);
  const [uploads, setUploads] = useState<UploadItem[]>([]);
  const [conflict, setConflict] = useState<{ file: File; folder: string } | null>(null);
  const [dragOver, setDragOver] = useState(false);
  const [creating, setCreating] = useState(false);
  const [renaming, setRenaming] = useState<ExplorerEntry | null>(null);
  const [deleting, setDeleting] = useState<ExplorerEntry | null>(null);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    get<FolderListing>(`/api/v1/hosts/${host.id}/files?${path ? `path=${encodeURIComponent(path)}` : ''}${scope}`)
      .then((r) => { if (!cancelled) setData(r); })
      .catch((e) => {
        if (cancelled) return;
        if (path && e instanceof ApiError && (e.code === 'not_found' || e.code === 'location_not_allowed' || e.code === 'storage_not_allowed')) { setPath(null); return; }
        setError(e);
        setData(null);
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [host.id, path, tick, groupId]);

  const update = (id: number, p: Partial<UploadItem>) => setUploads((list) => list.map((u) => (u.id === id ? { ...u, ...p } : u)));

  const start = (file: File, folder: string, overwrite = false) => {
    const id = ++uploadSeq;
    const q = new URLSearchParams({ folder, name: file.name, overwrite: String(overwrite) });
    const job = uploadFile<{ path: string; sha256: string }>(`/api/v1/hosts/${host.id}/files/upload?${q}${scope}`, file, (loaded) => update(id, { loaded }));
    setUploads((list) => [...list, { id, file, folder, loaded: 0, status: 'running', abort: job.abort }]);
    job.promise.then((r) => {
      update(id, { status: 'done', loaded: file.size });
      toast.success(t('files.uploaded', { name: file.name }), r.sha256 ? `SHA-256 ${r.sha256.slice(0, 16)}…` : undefined);
      setTick((x) => x + 1);
    }).catch((e: unknown) => {
      if (e instanceof ApiError && e.code === 'aborted') { update(id, { status: 'cancelled' }); return; }
      if (e instanceof ApiError && e.code === 'file_exists') { update(id, { status: 'cancelled' }); setConflict({ file, folder }); return; }
      update(id, { status: 'error', error: e instanceof Error ? e.message : String(e) });
    });
  };

  const pick = (files: FileList | null) => {
    if (!files || !data?.path) return;
    for (const f of Array.from(files)) start(f, data.path);
  };

  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setDragOver(false);
    pick(e.dataTransfer.files);
  };

  // Rename / delete of a row (agent 0.9.4, permission host.files.manage: the server decides and says why it refuses).
  // Virtual disks, checkpoints and VM configuration files are never offered: they belong to Hyper-V.
  const entryActions = (e: ExplorerEntry): Action[] => {
    const locked = isProtectedName(e.name);
    return [
      { key: 'rename', label: t('files.rename'), icon: Pencil, disabled: locked, hint: locked ? t('files.protected') : undefined, onSelect: () => setRenaming(e) },
      { key: 'delete', label: t('files.delete'), icon: Trash2, danger: true, disabled: locked, hint: locked ? t('files.protected') : undefined, onSelect: () => setDeleting(e) },
    ];
  };

  const crumbs = path ? path.replace(/\\$/, '').split('\\').map((seg, i, all) => ({ name: i === 0 ? seg + '\\' : seg, path: i === 0 ? seg + '\\' : all.slice(0, i + 1).join('\\') })) : [];
  const canUpload = !!data?.path && !readOnly;

  return (
    <div className="flex min-h-0 flex-col rounded-lg border bg-card">
      <div className="flex flex-wrap items-center gap-1 border-b bg-muted/40 px-2 py-1.5">
        <Button size="icon-sm" variant="ghost" aria-label={t('picker.up')} disabled={!path} onClick={() => setPath(data?.parent ?? null)}><ArrowUp /></Button>
        <nav aria-label={t('picker.location')} className="flex min-w-0 flex-1 items-center gap-0.5 overflow-x-auto text-sm">
          <button type="button" className="flex shrink-0 items-center gap-1 rounded px-1.5 py-0.5 hover:bg-accent" onClick={() => setPath(null)}><HardDrive className="size-3.5" />{t('picker.drives')}</button>
          {crumbs.map((c) => (
            <span key={c.path} className="flex shrink-0 items-center gap-0.5">
              <ChevronRight className="size-3.5 text-muted-foreground" />
              <button type="button" className="rounded px-1.5 py-0.5 hover:bg-accent" onClick={() => setPath(c.path)}>{c.name}</button>
            </span>
          ))}
        </nav>
        {data?.freeBytes != null && path && <span className="hidden text-xs text-muted-foreground sm:inline">{t('picker.freeShort', { free: formatBytes(data.freeBytes) })}</span>}
        <Button size="icon-sm" variant="ghost" aria-label={t('common.refresh')} onClick={() => setTick((x) => x + 1)}><RefreshCw className={cn(loading && 'animate-spin')} /></Button>
        <Button size="sm" variant="outline" disabled={!canUpload} onClick={() => setCreating(true)}><FolderPlus />{t('nfold.action')}</Button>
        <Button size="sm" variant="outline" disabled={!canUpload} onClick={() => input.current?.click()}><Upload />{t('files.upload')}</Button>
        <input ref={input} type="file" multiple className="hidden" onChange={(e) => { pick(e.target.files); e.target.value = ''; }} />
      </div>

      <div className={cn('relative min-h-72 flex-1 overflow-auto', dragOver && 'ring-2 ring-primary/60 ring-inset')}
        onDragOver={(e) => { if (canUpload && e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragOver(true); } }}
        onDragLeave={() => setDragOver(false)} onDrop={onDrop}>
        <div className="p-2"><ErrorAlert error={error} /></div>
        {data && !path && (
          <div className="grid gap-2 p-2 sm:grid-cols-2 xl:grid-cols-3">
            {/* Group scope: the starting points are the storage folders of the group. */}
            {groupId && data.folders.map((f) => (
              <button key={f.path} type="button" onClick={() => setPath(f.path)} className="flex items-center gap-3 rounded-lg border bg-background p-3 text-left hover:border-primary/50 hover:bg-accent">
                <Folder className="size-5 text-muted-foreground" />
                <span className="min-w-0 flex-1 truncate font-mono text-xs">{f.path}</span>
              </button>
            ))}
            {data.drives.map((d) => (
              <button key={d.path} type="button" onClick={() => setPath(d.path)} className="flex items-start gap-3 rounded-lg border bg-background p-3 text-left hover:border-primary/50 hover:bg-accent">
                <HardDrive className="mt-0.5 size-5 text-muted-foreground" />
                <span className="min-w-0 flex-1 space-y-1">
                  <span className="block text-sm font-medium">{d.label ? `${d.label} (${d.path.replace(/\\$/, '')})` : d.path}</span>
                  <Progress value={d.totalBytes > 0 ? 100 - (d.freeBytes / d.totalBytes) * 100 : 0} className="h-1.5" />
                  <span className="block text-xs text-muted-foreground">{t('picker.free', { free: formatBytes(d.freeBytes), total: formatBytes(d.totalBytes) })}</span>
                </span>
              </button>
            ))}
          </div>
        )}
        {data && path && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('files.name')}</TableHead>
                <TableHead className="w-28 text-right">{t('host.size')}</TableHead>
                <TableHead className="hidden w-44 md:table-cell">{t('files.modified')}</TableHead>
                <TableHead className="w-24"><span className="sr-only">{t('common.actions')}</span></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.folders.map((f) => (
                <WithContextMenu key={f.path} label={f.name} items={canUpload ? entryActions({ ...f, folder: true }) : []}>
                <TableRow className="group cursor-pointer" aria-label={t('picker.open', { name: f.name })} tabIndex={0}
                  onClick={(e) => { if (!(e.target as HTMLElement).closest('button, a, [role=menuitem]')) setPath(f.path); }}
                  onKeyDown={(e) => { if (e.key === 'Enter' && e.target === e.currentTarget) setPath(f.path); }}>
                  <TableCell><span className="flex items-center gap-2"><Folder className="size-4 text-amber-500 group-hover:hidden" /><FolderOpen className="hidden size-4 text-amber-500 group-hover:block" />{f.name}</span></TableCell>
                  <TableCell /><TableCell className="hidden md:table-cell" />
                  <TableCell className="text-right">
                    <span className="flex items-center justify-end gap-1">
                      {canUpload && <KebabMenu label={t('files.actionsOf', { name: f.name })} items={entryActions({ ...f, folder: true })} />}
                      <ChevronRight className="size-4 text-muted-foreground" />
                    </span>
                  </TableCell>
                </TableRow>
                </WithContextMenu>
              ))}
              {data.files.map((f) => (
                <WithContextMenu key={f.path} label={f.name} items={canUpload ? entryActions({ ...f, folder: false }) : []}>
                <TableRow>
                  <TableCell><span className="flex min-w-0 items-center gap-2"><FileIcon className="size-4 shrink-0 text-muted-foreground" /><span className="truncate">{f.name}</span></span></TableCell>
                  <TableCell className="text-right text-xs whitespace-nowrap tabular">{formatBytes(f.sizeBytes)}</TableCell>
                  <TableCell className="hidden text-xs whitespace-nowrap tabular md:table-cell">{fmtDate(f.modifiedUtc)}</TableCell>
                  <TableCell className="text-right">
                    <span className="flex items-center justify-end gap-1">
                      {canUpload && <KebabMenu label={t('files.actionsOf', { name: f.name })} items={entryActions({ ...f, folder: false })} />}
                      <Button asChild size="icon-sm" variant="ghost" aria-label={t('files.downloadName', { name: f.name })} title={t('files.download')}>
                        <a href={`/api/v1/hosts/${host.id}/files/download?path=${encodeURIComponent(f.path)}${scope}`} download={f.name}><Download /></a>
                      </Button>
                    </span>
                  </TableCell>
                </TableRow>
                </WithContextMenu>
              ))}
              {data.folders.length + data.files.length === 0 && (
                <TableRow><TableCell colSpan={4} className="py-8 text-center text-sm text-muted-foreground">{t('files.empty')}</TableCell></TableRow>
              )}
            </TableBody>
          </Table>
        )}
        {data?.truncated && <p className="px-3 py-2 text-xs text-muted-foreground">{t('files.truncated')}</p>}
        {dragOver && <div className="pointer-events-none absolute inset-0 flex items-center justify-center bg-primary/5 text-sm font-medium text-primary">{t('files.drop', { path: data?.path ?? '' })}</div>}
      </div>

      {uploads.length > 0 && (
        <div className="space-y-2 border-t p-3">
          {uploads.map((u) => (
            <div key={u.id} className="flex items-center gap-3 text-xs">
              <Upload className="size-3.5 shrink-0 text-muted-foreground" />
              <span className="w-56 truncate" title={`${u.folder}\\${u.file.name}`}>{u.file.name}</span>
              <Progress value={u.file.size > 0 ? (u.loaded / u.file.size) * 100 : 100} className="h-1.5 flex-1" />
              <span className="w-40 text-right tabular text-muted-foreground">
                {u.status === 'running' ? `${formatBytes(u.loaded)} / ${formatBytes(u.file.size)}` : u.status === 'done' ? t('files.done') : u.status === 'cancelled' ? t('files.cancelled') : <span className="text-destructive" title={u.error}>{t('files.failed')}</span>}
              </span>
              {u.status === 'running'
                ? <Button size="icon-xs" variant="ghost" aria-label={t('common.cancel')} onClick={() => u.abort?.()}><X /></Button>
                : <Button size="icon-xs" variant="ghost" aria-label={t('common.close')} onClick={() => setUploads((l) => l.filter((x) => x.id !== u.id))}><X /></Button>}
            </div>
          ))}
          {uploads.some((u) => u.status === 'error') && <Notice kind="danger">{uploads.filter((u) => u.status === 'error').map((u) => `${u.file.name}: ${u.error}`).join(' · ')}</Notice>}
        </div>
      )}

      {conflict && (
        <Dialog open onOpenChange={(o) => { if (!o) setConflict(null); }}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>{t('files.existsTitle')}</DialogTitle>
              <DialogDescription>{t('files.existsText', { name: conflict.file.name, folder: conflict.folder })}</DialogDescription>
            </DialogHeader>
            <DialogFooter>
              <Button variant="outline" onClick={() => setConflict(null)}>{t('common.cancel')}</Button>
              <Button variant="destructive" onClick={() => { start(conflict.file, conflict.folder, true); setConflict(null); }}>{t('files.replace')}</Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
      {renaming && (
        <RenameEntryDialog hostId={host.id} entry={renaming} groupId={groupId} onClose={() => setRenaming(null)}
          onDone={(name) => { toast.success(t('files.renamed', { name: renaming.name, to: name })); setTick((x) => x + 1); }} />
      )}
      {deleting && (
        <DeleteEntryDialog hostId={host.id} entry={deleting} groupId={groupId} onClose={() => setDeleting(null)}
          onDone={() => { toast.success(t('files.deleted', { name: deleting.name })); setTick((x) => x + 1); }} />
      )}
      {creating && data?.path && (
        <NewFolderDialog hostId={host.id} parent={data.path} groupId={groupId} onClose={() => setCreating(false)}
          onCreated={(f) => { setCreating(false); toast.success(t('nfold.created', { name: f.name })); setPath(f.path); }} />
      )}
    </div>
  );
}

interface ExplorerEntry { name: string; path: string; folder: boolean }

// Same list as ProtectedHostFiles (Contracts): the server refuses these whatever the page shows.
const PROTECTED = ['.vhd', '.vhdx', '.avhd', '.avhdx', '.vhds', '.vhdpmem', '.vmcx', '.vmrs', '.vmgs', '.vsv', '.vmtx', '.hrl', '.rct', '.mrt'];
const isProtectedName = (name: string) => PROTECTED.some((x) => name.toLowerCase().endsWith(x));

/** Rename a file or folder in place (POST /hosts/{id}/files/rename). The server explains every refusal. */
function RenameEntryDialog({ hostId, entry, groupId, onClose, onDone }: { hostId: string; entry: ExplorerEntry; groupId?: string; onClose: () => void; onDone: (name: string) => void }) {
  const { t } = useI18n();
  const [name, setName] = useState(entry.name);
  const n = name.trim();
  const ok = validFolderName(n) && n !== entry.name && !isProtectedName(n);
  return (
    <ActionDialog title={t(entry.folder ? 'files.renameFolderTitle' : 'files.renameFileTitle', { name: entry.name })} confirmLabel={t('files.rename')} disabled={!ok} onClose={onClose}
      onConfirm={async () => { await post(`/api/v1/hosts/${hostId}/files/rename`, { path: entry.path, newName: n, groupId }); onDone(n); }}>
      <Field label={t('files.newName')} htmlFor="frename-name" hint={entry.path}
        error={n && n !== entry.name && !ok ? t(isProtectedName(n) ? 'files.protected' : 'nfold.invalid') : undefined}>
        <Input id="frename-name" value={name} maxLength={200} autoFocus onChange={(e) => setName(e.target.value)} aria-invalid={!!n && n !== entry.name && !ok} />
      </Field>
      {entry.folder && <p className="text-xs text-muted-foreground">{t('files.renameFolderHelp')}</p>}
    </ActionDialog>
  );
}

/** Delete a file or an empty folder (POST /hosts/{id}/files/delete): explicit confirmation, nothing recursive. */
function DeleteEntryDialog({ hostId, entry, groupId, onClose, onDone }: { hostId: string; entry: ExplorerEntry; groupId?: string; onClose: () => void; onDone: () => void }) {
  const { t } = useI18n();
  const [confirmed, setConfirmed] = useState(false);
  return (
    <ActionDialog title={t(entry.folder ? 'files.deleteFolderTitle' : 'files.deleteFileTitle', { name: entry.name })} confirmLabel={t('files.delete')} danger disabled={!confirmed} onClose={onClose}
      onConfirm={async () => { await post(`/api/v1/hosts/${hostId}/files/delete`, { path: entry.path, confirmName: entry.name, groupId }); onDone(); }}>
      <Notice kind="danger" title={t('files.deleteWarning')}>
        <span className="font-mono text-xs [overflow-wrap:anywhere]">{entry.path}</span>
      </Notice>
      <p className="text-sm text-muted-foreground">{t(entry.folder ? 'files.deleteFolderHelp' : 'files.deleteFileHelp')}</p>
      <label className="flex items-start gap-2 text-sm">
        <input type="checkbox" className="mt-0.5" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
        <span>{t('files.deleteConfirm', { name: entry.name })}</span>
      </label>
    </ActionDialog>
  );
}

// Windows folder name: no reserved characters, no leading dot/dollar, no trailing dot or space (the agent checks again).
const FOLDER_NAME = /^[^\\/:*?"<>|\x00-\x1f.$][^\\/:*?"<>|\x00-\x1f]{0,254}$/;
const validFolderName = (n: string) => FOLDER_NAME.test(n) && !/[. ]$/.test(n);

/** New sub-folder of the open folder (POST /hosts/{id}/files/folders; inside the allowed storage folders only). */
function NewFolderDialog({ hostId, parent, groupId, onClose, onCreated }: { hostId: string; parent: string; groupId?: string; onClose: () => void; onCreated: (f: FolderEntry) => void }) {
  const { t } = useI18n();
  const [name, setName] = useState('');
  const n = name.trim();
  const ok = validFolderName(n);
  return (
    <ActionDialog title={t('nfold.title')} confirmLabel={t('common.create')} disabled={!ok} onClose={onClose}
      onConfirm={async () => onCreated(await post<FolderEntry>(`/api/v1/hosts/${hostId}/files/folders`, { parent, name: n, groupId }))}>
      <Field label={t('nfold.name')} htmlFor="nfold-name" hint={t('nfold.in', { path: parent })} error={n && !ok ? t('nfold.invalid') : undefined}>
        <Input id="nfold-name" value={name} maxLength={255} autoFocus onChange={(e) => setName(e.target.value)} aria-invalid={!!n && !ok} />
      </Field>
    </ActionDialog>
  );
}
