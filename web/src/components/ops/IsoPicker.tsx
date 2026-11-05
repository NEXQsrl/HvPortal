import { FolderSearch } from 'lucide-react';
import { useState } from 'react';
import { get, put } from '@/api/client';
import type { IsoFile, IsosResponse } from '@/api/ops-types';
import type { Host } from '@/api/types';
import { ErrorAlert } from '@/components/common';
import { Button } from '@/components/ui/button';
import { NativeSelect } from '@/components/ui/native-select';
import { useI18n } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { normalizeFolder, parentFolder } from '@/lib/hostPaths';
import { toast } from '@/lib/toast';
import { useSession } from '@/session';
import { HostFolderPicker } from './HostFolderPicker';

function inside(path: string, roots: string[]) {
  const p = path.toLowerCase();
  return roots.some((r) => p.startsWith((r.endsWith('\\') ? r : r + '\\').toLowerCase()));
}

/**
 * ISO selection for a host: files of the host's ISO library, plus (administrators, agent 0.3.1+) "Browse..." on the
 * host's drives. Picking an ISO outside the library adds its folder to the library (audited, hosts.manage), because the
 * agent mounts only ISOs inside library folders.
 */
export function IsoPicker({ id, host, value, onChange, emptyLabel, disabled }: {
  id: string; host: Host; value: string; onChange: (path: string) => void; emptyLabel: string; disabled?: boolean;
}) {
  const { t } = useI18n();
  const { can } = useSession();
  const [browsing, setBrowsing] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const isos = useLoad(() => get<IsosResponse>(`/api/v1/hosts/${host.id}/isos`), [host.id]);
  const roots = isos.data?.roots ?? host.isoLibrary;
  const files = isos.data?.files ?? [];
  const canBrowse = can('hosts.manage') && !!host.canBrowseFolders && host.connected;
  const options = [...new Map([...(value ? [[value, null] as const] : []), ...files.map((f) => [f.path, f] as const)]).entries()];

  const pickIso = async (iso: IsoFile) => {
    setBrowsing(false);
    setError(null);
    if (!inside(iso.path, roots)) {
      const folder = normalizeFolder(parentFolder(iso.path));
      try {
        await put(`/api/v1/hosts/${host.id}/iso-library`, { paths: [...roots, folder] });
        toast.success(t('iso.libraryAdded', { path: folder }));
      } catch (e) {
        setError(e);
        return;
      }
    }
    isos.reload();
    onChange(iso.path);
  };

  const hint = isos.loading ? undefined
    : roots.length === 0 ? (canBrowse ? t('iso.browseHint') : can('hosts.manage') ? t('cfg.noLibrary') : t('iso.askAdmin'))
    : files.length === 0 ? t('iso.emptyLibrary') : undefined;

  return (
    <div className="grid gap-1.5">
      <div className="flex gap-2">
        <NativeSelect id={id} value={value} onChange={(e) => onChange(e.target.value)} disabled={disabled || (options.length === 0 && !value)} className="min-w-0 flex-1">
          <option value="">{emptyLabel}</option>
          {options.map(([p, f]) => <option key={p} value={p}>{p}{f ? ` (${formatBytes(f.sizeBytes)})` : ''}</option>)}
        </NativeSelect>
        {canBrowse && <Button type="button" variant="outline" onClick={() => setBrowsing(true)} disabled={disabled}><FolderSearch />{t('picker.browse')}</Button>}
      </div>
      {hint && <div className="text-xs text-muted-foreground">{hint}</div>}
      <ErrorAlert error={error} />
      {browsing && (
        <HostFolderPicker hostId={host.id} mode="iso" initialPath={value ? parentFolder(value) : roots[0] ?? null}
          onPickIso={(f) => void pickIso(f)} onClose={() => setBrowsing(false)} />
      )}
    </div>
  );
}
