import { useState } from 'react';
import { get, newIdempotencyKey, post } from '@/api/client';
import type { Accepted } from '@/api/ops-types';
import type { Vm } from '@/api/types';
import { ConfirmTypedDialog } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Checkbox } from '@/components/ui/checkbox';
import type { DetailsResponse } from '@/components/vm/VmHardware';
import { useI18n } from '@/i18n';
import { useLoad } from '@/lib/hooks';

/**
 * Delete a VM (typed name). By default only its configuration is deleted and the disk files stay on the host; with
 * "also delete the disk files" (agent 0.7.0+) the agent deletes the disks attached to it too, except files another VM
 * uses (attached or as parent of a differencing disk) and files outside the allowed storage folders.
 */
export function DeleteVmDialog({ vm, onQueued, onClose }: { vm: Vm; onQueued: (r: Accepted, label: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const host = useInventory().hostById.get(vm.hostId);
  const canDeleteDisks = !!host?.agent7;
  const [deleteDisks, setDeleteDisks] = useState(false);
  const details = useLoad(() => get<DetailsResponse>(`/api/v1/vms/${vm.id}/details`), [vm.id]);
  const disks = (details.data?.details?.disks ?? []).filter((d) => d.kind === 'VirtualHardDisk' && d.path).map((d) => d.path!);
  return (
    <ConfirmTypedDialog title={t('vm.deleteTitle', { name: vm.name })} name={vm.name} confirmLabel={deleteDisks ? t('vmdel.confirmWithDisks') : t('vm.delete')} onClose={onClose}
      impact={<>{t('vm.deleteImpact')}<br /><strong>{deleteDisks ? t('vmdel.disksDeleted') : t('vm.deleteDisksKept')}</strong></>}
      onConfirm={async (typed) => {
        const r = await post<Accepted>(`/api/v1/vms/${vm.id}/delete`, { confirmName: typed, deleteDisks, idempotencyKey: newIdempotencyKey() });
        onQueued(r, `${t(deleteDisks ? 'jobop.vm.delete.DeleteWithDisks' : 'op.Delete')} — ${vm.name}`);
      }}>
      <label className="flex items-start gap-2 rounded-md border p-2 text-sm has-[:checked]:border-destructive/50 has-[:checked]:bg-destructive/5">
        <Checkbox checked={deleteDisks} disabled={!canDeleteDisks} onCheckedChange={(v) => setDeleteDisks(!!v)} className="mt-0.5" />
        <span>
          <span className="font-medium">{t('vmdel.option')}</span>
          <span className="block text-xs text-muted-foreground">{canDeleteDisks ? t('vmdel.optionHelp') : t('vmdel.oldAgent')}</span>
        </span>
      </label>
      {deleteDisks && (
        <div className="space-y-1 text-xs">
          <div className="font-medium">{t('vmdel.files')}</div>
          {disks.length === 0
            ? <div className="text-muted-foreground">{details.loading ? t('common.loading') : t('vmdel.filesUnknown')}</div>
            : <ul className="list-disc space-y-0.5 pl-4 font-mono [overflow-wrap:anywhere]">{disks.map((p) => <li key={p}>{p}</li>)}</ul>}
          <p className="text-muted-foreground">{t('vmdel.rules')}</p>
        </div>
      )}
    </ConfirmTypedDialog>
  );
}
