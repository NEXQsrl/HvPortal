import { ArrowUp, ChevronRight, Disc3, Folder, FolderOpen, HardDrive, RefreshCw } from 'lucide-react';
import { useEffect, useState } from 'react';
import { get } from '@/api/client';
import type { FolderListing, IsoFile } from '@/api/ops-types';
import { ErrorAlert, Notice } from '@/components/common';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Progress } from '@/components/ui/progress';
import { useI18n } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { cn } from '@/lib/utils';

export type PickedFolder = { path: string; freeBytes: number | null };

/**
 * Explorer-like browser of a host's local fixed drives (agent 0.3.1+). The agent never lists hidden/system folders,
 * junctions or the top-level system folders (Windows, Users, Program Files, ProgramData, inetpub, PerfLogs, ...).
 * mode "folder": choose a folder (VM location). mode "iso": choose an .iso file (administrators).
 */
export function HostFolderPicker({ hostId, mode, initialPath, onPickFolder, onPickIso, onClose }: {
  hostId: string;
  mode: 'folder' | 'iso';
  initialPath?: string | null;
  onPickFolder?: (f: PickedFolder) => void;
  onPickIso?: (iso: IsoFile) => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const [path, setPath] = useState<string | null>(initialPath ?? null);
  const [data, setData] = useState<FolderListing | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setSelected(null);
    const q = new URLSearchParams();
    if (path) q.set('path', path);
    if (mode === 'iso') q.set('isos', 'true');
    get<FolderListing>(`/api/v1/hosts/${hostId}/folders?${q}`)
      .then((r) => { if (!cancelled) setData(r); })
      .catch((e) => {
        if (cancelled) return;
        // A remembered folder may have been removed: fall back to the drive list once.
        if (path && initialPath && path === initialPath) { setPath(null); return; }
        setError(e);
        setData(null);
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [hostId, path, mode, tick]);

  const crumbs = path ? path.replace(/\\$/, '').split('\\').map((seg, i, all) => ({ name: i === 0 ? seg + '\\' : seg, path: i === 0 ? seg + '\\' : all.slice(0, i + 1).join('\\') })) : [];
  const iso = mode === 'iso' && selected ? data?.isos.find((f) => f.path === selected) : undefined;
  const folderTarget = mode === 'folder' ? (selected ?? data?.path ?? null) : null;

  const confirm = () => {
    if (mode === 'iso' && iso) onPickIso?.(iso);
    else if (mode === 'folder' && folderTarget) onPickFolder?.({ path: folderTarget, freeBytes: data?.freeBytes ?? null });
  };

  return (
    <Dialog open onOpenChange={(o) => { if (!o) onClose(); }}>
      <DialogContent className="flex max-h-[85dvh] flex-col gap-0 overflow-hidden p-0 sm:max-w-2xl">
        <DialogHeader className="border-b px-4 py-3">
          <DialogTitle>{t(mode === 'iso' ? 'picker.isoTitle' : 'picker.folderTitle')}</DialogTitle>
          <DialogDescription>{t('picker.help')}</DialogDescription>
        </DialogHeader>
        <div className="flex items-center gap-1 border-b bg-muted/40 px-2 py-1.5">
          <Button size="icon-sm" variant="ghost" aria-label={t('picker.up')} disabled={!path} onClick={() => setPath(data?.parent ?? null)}><ArrowUp /></Button>
          <nav aria-label={t('picker.location')} className="flex min-w-0 flex-1 items-center gap-0.5 overflow-x-auto text-sm">
            <button type="button" className="flex shrink-0 items-center gap-1 rounded px-1.5 py-0.5 hover:bg-accent" onClick={() => setPath(null)}>
              <HardDrive className="size-3.5" />{t('picker.drives')}
            </button>
            {crumbs.map((c) => (
              <span key={c.path} className="flex shrink-0 items-center gap-0.5">
                <ChevronRight className="size-3.5 text-muted-foreground" />
                <button type="button" className="rounded px-1.5 py-0.5 hover:bg-accent" onClick={() => setPath(c.path)}>{c.name}</button>
              </span>
            ))}
          </nav>
          <Button size="icon-sm" variant="ghost" aria-label={t('common.refresh')} onClick={() => setTick((x) => x + 1)}><RefreshCw className={cn(loading && 'animate-spin')} /></Button>
        </div>
        <div className="min-h-64 flex-1 overflow-auto p-2" role="listbox" aria-label={t('picker.location')}>
          <ErrorAlert error={error} />
          {loading && !data && <p className="p-2 text-sm text-muted-foreground">{t('common.loading')}</p>}
          {data && !path && (
            <div className="grid gap-2 sm:grid-cols-2">
              {data.drives.map((d) => (
                <button key={d.path} type="button" onClick={() => setPath(d.path)}
                  className="flex items-start gap-3 rounded-lg border bg-card p-3 text-left hover:border-primary/50 hover:bg-accent">
                  <HardDrive className="mt-0.5 size-5 text-muted-foreground" />
                  <span className="min-w-0 flex-1 space-y-1">
                    <span className="block text-sm font-medium">{d.label ? `${d.label} (${d.path.replace(/\\$/, '')})` : d.path}</span>
                    <Progress value={d.totalBytes > 0 ? 100 - (d.freeBytes / d.totalBytes) * 100 : 0} className="h-1.5" />
                    <span className="block text-xs text-muted-foreground">{t('picker.free', { free: formatBytes(d.freeBytes), total: formatBytes(d.totalBytes) })}</span>
                  </span>
                </button>
              ))}
              {data.drives.length === 0 && <Notice kind="warn">{t('picker.noDrives')}</Notice>}
            </div>
          )}
          {data && path && (
            <ul className="space-y-0.5">
              {data.folders.map((f) => (
                <li key={f.path}>
                  <button type="button" role="option" aria-selected={selected === f.path}
                    onClick={() => setSelected(mode === 'folder' ? f.path : null)} onDoubleClick={() => setPath(f.path)}
                    onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); setPath(f.path); } }}
                    className={cn('group flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-accent', selected === f.path && 'bg-primary/10 ring-1 ring-primary/40')}>
                    {selected === f.path ? <FolderOpen className="size-4 text-primary" /> : <Folder className="size-4 text-amber-500" />}
                    <span className="min-w-0 flex-1 truncate">{f.name}</span>
                    <span role="button" tabIndex={-1} aria-label={t('picker.open', { name: f.name })} onClick={(e) => { e.stopPropagation(); setPath(f.path); }}
                      className="rounded p-0.5 text-muted-foreground opacity-60 hover:bg-background group-hover:opacity-100"><ChevronRight className="size-4" /></span>
                  </button>
                </li>
              ))}
              {mode === 'iso' && data.isos.map((f) => (
                <li key={f.path}>
                  <button type="button" role="option" aria-selected={selected === f.path} onClick={() => setSelected(f.path)} onDoubleClick={() => onPickIso?.(f)}
                    className={cn('flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-accent', selected === f.path && 'bg-primary/10 ring-1 ring-primary/40')}>
                    <Disc3 className="size-4 text-sky-500" />
                    <span className="min-w-0 flex-1 truncate">{f.path.split('\\').pop()}</span>
                    <span className="text-xs text-muted-foreground tabular-nums">{formatBytes(f.sizeBytes)}</span>
                  </button>
                </li>
              ))}
              {data.folders.length === 0 && (mode === 'folder' || data.isos.length === 0) && (
                <li className="px-2 py-6 text-center text-sm text-muted-foreground">{t(mode === 'iso' ? 'picker.emptyIso' : 'picker.empty')}</li>
              )}
            </ul>
          )}
          {data?.truncated && <p className="px-2 pt-2 text-xs text-muted-foreground">{t('picker.truncated')}</p>}
        </div>
        <DialogFooter className="m-0 items-center rounded-none border-t px-4 py-3">
          <span className="mr-auto min-w-0 truncate font-mono text-xs text-muted-foreground" title={iso?.path ?? folderTarget ?? ''}>
            {iso?.path ?? folderTarget ?? t('picker.chooseDrive')}
            {mode === 'folder' && data?.freeBytes != null && folderTarget && <span className="ml-2 font-sans">· {t('picker.freeShort', { free: formatBytes(data.freeBytes) })}</span>}
          </span>
          <Button type="button" variant="outline" onClick={onClose}>{t('common.cancel')}</Button>
          <Button type="button" onClick={confirm} disabled={mode === 'iso' ? !iso : !folderTarget}>{t(mode === 'iso' ? 'picker.useIso' : 'picker.useFolder')}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
