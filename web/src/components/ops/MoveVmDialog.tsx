import { FolderOpen } from 'lucide-react';
import { useState } from 'react';
import { newIdempotencyKey, post } from '@/api/client';
import type { Accepted } from '@/api/ops-types';
import type { Vm } from '@/api/types';
import { Notice } from '@/components/common';
import { ActionDialog, Field } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { useI18n } from '@/i18n';
import { LIBRARY_PATH } from '@/lib/format';
import { HostFolderPicker } from './HostFolderPicker';

type Mode = 'host' | 'storage';

/**
 * Move a VM (vCenter "Migrate"): to another host of the portal (Move-VM: live migration of a running VM, with its files
 * unless they are on shared storage) or only its files to another folder of the same host (Move-VMStorage).
 * The server checks vm.migrate, vm.create on the destination, the folder limits and the allowed storage folders.
 */
export function MoveVmDialog({ vm, onQueued, onClose }: { vm: Vm; onQueued: (r: Accepted, label: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const [mode, setMode] = useState<Mode>('host');
  const destinations = inv.hosts.filter((h) => h.id !== vm.hostId && h.status === 'Active' && h.canCreateVm);
  const [hostId, setHostId] = useState(destinations[0]?.id ?? '');
  const [includeStorage, setIncludeStorage] = useState(true);
  const [path, setPath] = useState('');
  const [picking, setPicking] = useState(false);
  const [key] = useState(newIdempotencyKey);
  const folderHost = mode === 'storage' ? vm.hostId : hostId;
  const needsPath = mode === 'storage' || includeStorage;
  const pathOk = !needsPath || (LIBRARY_PATH.test(path.trim()) && !path.includes('..'));
  const valid = (mode === 'storage' || !!hostId) && pathOk && (!needsPath || path.trim().length > 3);
  const destination = inv.hostById.get(hostId);
  const allowed = inv.hostById.get(folderHost)?.storageFolders;
  return (
    <ActionDialog title={t('move.title', { name: vm.name })} confirmLabel={t('move.confirm')} disabled={!valid} wide onClose={onClose}
      onConfirm={async () => {
        const r = await post<Accepted>(`/api/v1/vms/${vm.id}/move`, {
          mode, destinationHostId: mode === 'host' ? hostId : undefined, includeStorage: mode === 'storage' || includeStorage,
          destinationPath: needsPath ? path.trim() : undefined, idempotencyKey: key,
        });
        onQueued(r, mode === 'host' ? `${t('move.jobHost')} — ${vm.name} → ${destination?.displayName ?? ''}` : `${t('move.jobStorage')} — ${vm.name}`);
      }}>
      <RadioGroup value={mode} onValueChange={(v) => { setMode(v as Mode); setPath(''); }} className="gap-2">
        {(['host', 'storage'] as const).map((m) => (
          <label key={m} className="flex cursor-pointer items-start gap-2 rounded-md border p-2 text-sm has-[:checked]:border-primary/50 has-[:checked]:bg-primary/5">
            <RadioGroupItem value={m} className="mt-0.5" />
            <span><span className="font-medium">{t(m === 'host' ? 'move.modeHost' : 'move.modeStorage')}</span>
              <span className="block text-xs text-muted-foreground">{t(m === 'host' ? 'move.modeHostHelp' : 'move.modeStorageHelp')}</span></span>
          </label>
        ))}
      </RadioGroup>
      {mode === 'host' && (
        destinations.length === 0 ? <Notice kind="warn">{t('move.noDestination')}</Notice> : (
          <>
            <Field label={t('move.destination')} htmlFor="move-host">
              <NativeSelect id="move-host" value={hostId} onChange={(e) => { setHostId(e.target.value); setPath(''); }}>
                {destinations.map((h) => <option key={h.id} value={h.id} disabled={!h.connected}>{h.displayName}{h.connected ? '' : ` (${t('host.offline')})`}</option>)}
              </NativeSelect>
            </Field>
            <label className="flex items-start gap-2 text-sm">
              <Checkbox checked={includeStorage} onCheckedChange={(v) => setIncludeStorage(!!v)} className="mt-0.5" />
              <span>{t('move.includeStorage')}<span className="block text-xs text-muted-foreground">{t('move.includeStorageHelp')}</span></span>
            </label>
          </>
        )
      )}
      {needsPath && (
        <Field label={t('move.folder')} htmlFor="move-path" hint={t('move.folderHint')}
          error={path && !pathOk ? t('host.folderInvalid') : undefined}>
          <div className="flex gap-2">
            <Input id="move-path" className="font-mono" value={path} placeholder="D:\VMs" onChange={(e) => setPath(e.target.value)} aria-invalid={!!path && !pathOk} />
            <Button type="button" variant="outline" disabled={!folderHost} onClick={() => setPicking(true)}><FolderOpen />{t('move.browse')}</Button>
          </div>
        </Field>
      )}
      {needsPath && allowed && allowed.length > 0 && <p className="text-xs text-muted-foreground">{t('move.allowedFolders', { list: allowed.join(', ') })}</p>}
      <Notice kind="info" title={t('move.requirementsTitle')}>{mode === 'host' ? t('move.requirementsHost') : t('move.requirementsStorage')}</Notice>
      {vm.powerState === 'Running' && <p className="text-xs text-muted-foreground">{t(mode === 'host' ? 'move.liveNote' : 'move.storageLiveNote')}</p>}
      {picking && folderHost && (
        <HostFolderPicker hostId={folderHost} mode="folder" initialPath={path || null} onClose={() => setPicking(false)}
          onPickFolder={(f) => { setPath(f.path); setPicking(false); }} />
      )}
    </ActionDialog>
  );
}
