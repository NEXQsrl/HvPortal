import { ArrowUp, ChevronRight, Download, File as FileIcon, Folder, FolderOpen, HardDrive, RefreshCw, Upload, X } from 'lucide-react';
import { useEffect, useRef, useState, type DragEvent } from 'react';
import { ApiError, get, uploadFile } from '@/api/client';
import type { FolderListing } from '@/api/ops-types';
import type { Host } from '@/api/types';
import { ErrorAlert, Notice } from '@/components/common';
import { Button } from '@/components/ui/button';
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
export function HostFileExplorer({ host }: { host: Host }) {
  const { t, fmtDate } = useI18n();
  const [path, setPath] = useLocalStorage<string | null>(`hvp.files.${host.id}`, null);
  const [data, setData] = useState<FolderListing | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(false);
  const [tick, setTick] = useState(0);
  const [uploads, setUploads] = useState<UploadItem[]>([]);
  const [conflict, setConflict] = useState<{ file: File; folder: string } | null>(null);
  const [dragOver, setDragOver] = useState(false);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    get<FolderListing>(`/api/v1/hosts/${host.id}/files${path ? `?path=${encodeURIComponent(path)}` : ''}`)
      .then((r) => { if (!cancelled) setData(r); })
      .catch((e) => {
        if (cancelled) return;
        if (path && e instanceof ApiError && (e.code === 'not_found' || e.code === 'location_not_allowed')) { setPath(null); return; }
        setError(e);
        setData(null);
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [host.id, path, tick]);

  const update = (id: number, p: Partial<UploadItem>) => setUploads((list) => list.map((u) => (u.id === id ? { ...u, ...p } : u)));

  const start = (file: File, folder: string, overwrite = false) => {
    const id = ++uploadSeq;
    const q = new URLSearchParams({ folder, name: file.name, overwrite: String(overwrite) });
    const job = uploadFile<{ path: string; sha256: string }>(`/api/v1/hosts/${host.id}/files/upload?${q}`, file, (loaded) => update(id, { loaded }));
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

  const crumbs = path ? path.replace(/\\$/, '').split('\\').map((seg, i, all) => ({ name: i === 0 ? seg + '\\' : seg, path: i === 0 ? seg + '\\' : all.slice(0, i + 1).join('\\') })) : [];
  const canUpload = !!data?.path;

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
        <Button size="sm" variant="outline" disabled={!canUpload} onClick={() => input.current?.click()}><Upload />{t('files.upload')}</Button>
        <input ref={input} type="file" multiple className="hidden" onChange={(e) => { pick(e.target.files); e.target.value = ''; }} />
      </div>

      <div className={cn('relative min-h-72 flex-1 overflow-auto', dragOver && 'ring-2 ring-primary/60 ring-inset')}
        onDragOver={(e) => { if (canUpload && e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragOver(true); } }}
        onDragLeave={() => setDragOver(false)} onDrop={onDrop}>
        <div className="p-2"><ErrorAlert error={error} /></div>
        {data && !path && (
          <div className="grid gap-2 p-2 sm:grid-cols-2 xl:grid-cols-3">
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
                <TableHead className="w-12"><span className="sr-only">{t('files.download')}</span></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.folders.map((f) => (
                <TableRow key={f.path} className="group cursor-pointer" onClick={() => setPath(f.path)}
                  tabIndex={0} onKeyDown={(e) => { if (e.key === 'Enter') setPath(f.path); }} aria-label={t('picker.open', { name: f.name })}>
                  <TableCell><span className="flex items-center gap-2"><Folder className="size-4 text-amber-500 group-hover:hidden" /><FolderOpen className="hidden size-4 text-amber-500 group-hover:block" />{f.name}</span></TableCell>
                  <TableCell /><TableCell className="hidden md:table-cell" />
                  <TableCell className="text-right"><ChevronRight className="ml-auto size-4 text-muted-foreground" /></TableCell>
                </TableRow>
              ))}
              {data.files.map((f) => (
                <TableRow key={f.path}>
                  <TableCell><span className="flex min-w-0 items-center gap-2"><FileIcon className="size-4 shrink-0 text-muted-foreground" /><span className="truncate">{f.name}</span></span></TableCell>
                  <TableCell className="text-right text-xs whitespace-nowrap tabular">{formatBytes(f.sizeBytes)}</TableCell>
                  <TableCell className="hidden text-xs whitespace-nowrap tabular md:table-cell">{fmtDate(f.modifiedUtc)}</TableCell>
                  <TableCell className="text-right">
                    <Button asChild size="icon-sm" variant="ghost" aria-label={t('files.downloadName', { name: f.name })} title={t('files.download')}>
                      <a href={`/api/v1/hosts/${host.id}/files/download?path=${encodeURIComponent(f.path)}`} download={f.name}><Download /></a>
                    </Button>
                  </TableCell>
                </TableRow>
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
    </div>
  );
}
