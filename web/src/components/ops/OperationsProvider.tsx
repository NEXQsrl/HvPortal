import {
  Camera, FolderInput, FolderMinus, FolderPlus, KeyRound, Monitor, Pause, Pencil, Play, Plus, Power, PowerOff, RefreshCw, RotateCcw, Save,
  Settings2, ShieldOff, SquareArrowOutUpRight, Trash2, Undo2,
} from 'lucide-react';
import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { del, newIdempotencyKey, patch, post } from '@/api/client';
import type { Accepted, Group } from '@/api/ops-types';
import { realtime } from '@/api/realtime';
import type { EnrollmentInfo, Host, PowerOperation, Vm } from '@/api/types';
import type { Action } from '@/components/actions';
import { errorText } from '@/components/common';
import { ActionDialog, ConfirmTypedDialog, EditDialog } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useI18n, type TKey } from '@/i18n';
import { watchJob } from '@/lib/jobs';
import { toast } from '@/lib/toast';
import { useSession } from '@/session';
import { CreateVmWizard } from './CreateVmWizard';
import { AddToGroupDialog, AddVmsDialog, MoveGroupDialog, NewGroupDialog } from './GroupDialogs';
import { AddHostDialog, EnrollmentDialog, RevokeHostDialog } from './HostDialogs';
import { CreateCheckpointDialog, PowerDialog } from './VmDialogs';

const POWER_ICONS: Record<PowerOperation, typeof Play> = {
  Start: Play, Shutdown: Power, Restart: RotateCcw, TurnOff: PowerOff, Reset: Undo2, Pause, Resume: Play, Save,
};

type DialogState =
  | { kind: 'power'; vm: Vm; op: PowerOperation }
  | { kind: 'deleteVm'; vm: Vm }
  | { kind: 'editVm'; vm: Vm }
  | { kind: 'addToGroup'; vms: Vm[] }
  | { kind: 'checkpoint'; vm: Vm }
  | { kind: 'createVm'; hostId?: string; groupId?: string }
  | { kind: 'addHost' }
  | { kind: 'enroll'; info: EnrollmentInfo; hostId: string }
  | { kind: 'editHost'; host: Host }
  | { kind: 'deleteHost'; host: Host }
  | { kind: 'revokeHost'; host: Host }
  | { kind: 'newGroup'; parentId: string | null }
  | { kind: 'editGroup'; group: Group }
  | { kind: 'moveGroup'; group: Group }
  | { kind: 'deleteGroup'; group: Group }
  | { kind: 'addVms'; group: Group };

interface Ops {
  vmActions: (vm: Vm, opts?: { groupId?: string; page?: boolean }) => Action[];
  powerActions: (vm: Vm) => Action[];
  hostActions: (host: Host, opts?: { page?: boolean }) => Action[];
  groupActions: (group: Group | null) => Action[];
  openCreateVm: (opts?: { hostId?: string; groupId?: string }) => void;
  openAddHost: () => void;
  openNewGroup: (parentId: string | null) => void;
  openPower: (vm: Vm, op: PowerOperation) => void;
  openConsole: (vm: Vm, popout?: boolean) => void;
  /** Drag and drop: add VMs to a group, optionally removing them from the group they were dragged from. */
  placeVms: (vmIds: string[], groupId: string, fromGroupId?: string) => Promise<void>;
  moveGroup: (groupId: string, parentId: string | null) => Promise<void>;
  queued: (r: Accepted, label: string) => void;
}

const Ctx = createContext<Ops | null>(null);

/**
 * Every action on VMs, hosts and groups and their dialogs live here, so the tree, the tables and the object pages
 * show exactly the same actions. The server authorizes each request; flags from the API only hide what would fail.
 */
export function OperationsProvider({ children }: { children: ReactNode }) {
  const { t } = useI18n();
  const { can } = useSession();
  const inv = useInventory();
  const navigate = useNavigate();
  const [dialog, setDialog] = useState<DialogState | null>(null);
  const close = useCallback(() => setDialog(null), []);
  const manageHosts = can('hosts.manage');
  const manageGroups = inv.canManageGroups;

  const queued = useCallback((r: Accepted, label: string) => {
    watchJob(r.jobId, label);
    toast.info(t('job.queuedToast'), <span>{label} · <Link className="text-primary underline-offset-2 hover:underline" to={`/jobs/${r.jobId}`}>{t('job.openTask')}</Link></span>);
    realtime.emit('jobChanged', { jobId: r.jobId });
  }, [t]);

  const fail = useCallback((e: unknown) => toast.error(t('common.error'), errorText(e)), [t]);

  const openConsole = useCallback((vm: Vm, popout?: boolean) => {
    if (popout) window.open(`/console/${vm.id}`, `hvp-console-${vm.id}`, 'popup,width=1280,height=900');
    else navigate(`/vms/${vm.id}?tab=console`);
  }, [navigate]);

  const placeVms = useCallback(async (vmIds: string[], groupId: string, fromGroupId?: string) => {
    const target = inv.groupById.get(groupId);
    try {
      await post(`/api/v1/groups/${groupId}/members`, { vmIds });
      if (fromGroupId && fromGroupId !== groupId) await post(`/api/v1/groups/${fromGroupId}/members/remove`, { vmIds });
      toast.success(t(fromGroupId ? 'group.moved' : 'group.added', { group: target?.name ?? '' }));
      inv.reload();
    } catch (e) { fail(e); }
  }, [inv, t, fail]);

  const moveGroup = useCallback(async (groupId: string, parentId: string | null) => {
    try {
      await patch(`/api/v1/groups/${groupId}`, parentId ? { parentId } : { moveToRoot: true });
      toast.success(t('group.moved', { group: parentId ? inv.groupById.get(parentId)?.name ?? '' : t('group.root') }));
      inv.reload();
    } catch (e) { fail(e); }
  }, [inv, t, fail]);

  const powerActions = useCallback((vm: Vm): Action[] => vm.allowedOperations.map((op) => ({
    key: 'power-' + op, label: t(('op.' + op) as TKey), icon: POWER_ICONS[op], danger: vm.destructiveOperations.includes(op),
    onSelect: () => setDialog({ kind: 'power', vm, op }),
  })), [t]);

  const vmActions = useCallback((vm: Vm, opts: { groupId?: string; page?: boolean } = {}): Action[] => {
    const list: Action[] = [];
    if (!opts.page) list.push({ key: 'open', label: t('menu.open'), icon: SquareArrowOutUpRight, onSelect: () => navigate(`/vms/${vm.id}`) });
    if (vm.canConsole) {
      list.push({ key: 'console', label: t('vm.openConsole'), icon: Monitor, onSelect: () => openConsole(vm) });
      list.push({ key: 'console-popout', label: t('con.popout'), icon: SquareArrowOutUpRight, onSelect: () => openConsole(vm, true) });
    }
    powerActions(vm).forEach((a, i) => list.push({ ...a, separatorBefore: i === 0 }));
    if (vm.canConfigure) list.push({ key: 'settings', label: t('vm.editSettings'), icon: Settings2, separatorBefore: true, onSelect: () => navigate(`/vms/${vm.id}?tab=settings`) });
    if (vm.canCheckpoint) list.push({ key: 'checkpoint', label: t('vm.createCheckpoint'), icon: Camera, separatorBefore: !vm.canConfigure, onSelect: () => setDialog({ kind: 'checkpoint', vm }) });
    if (vm.canAnnotate) list.push({ key: 'describe', label: t('menu.editDescription'), icon: Pencil, onSelect: () => setDialog({ kind: 'editVm', vm }) });
    if (manageGroups) {
      list.push({ key: 'add-group', label: t('vm.addToGroup'), icon: FolderPlus, separatorBefore: true, onSelect: () => setDialog({ kind: 'addToGroup', vms: [vm] }) });
      const g = opts.groupId ? inv.groupById.get(opts.groupId) : undefined;
      if (g) {
        list.push({
          key: 'remove-group', label: t('vm.removeFromGroup', { group: g.name }), icon: FolderMinus,
          onSelect: async () => {
            try { await post(`/api/v1/groups/${g.id}/members/remove`, { vmIds: [vm.id] }); inv.reload(); } catch (e) { fail(e); }
          },
        });
      }
    }
    if (vm.canDelete) list.push({ key: 'delete', label: t('vm.delete'), icon: Trash2, danger: true, separatorBefore: true, onSelect: () => setDialog({ kind: 'deleteVm', vm }) });
    if (vm.canForget) {
      list.push({
        key: 'forget', label: t('vm.forget'), icon: Trash2, danger: true, separatorBefore: true,
        onSelect: async () => {
          try { await post(`/api/v1/vms/${vm.id}/forget`); inv.reload(); navigate('/vms'); } catch (e) { fail(e); }
        },
      });
    }
    return list;
  }, [t, navigate, openConsole, powerActions, manageGroups, inv, fail]);

  const hostActions = useCallback((h: Host, opts: { page?: boolean } = {}): Action[] => {
    const list: Action[] = [];
    if (!opts.page) list.push({ key: 'open', label: t('menu.open'), icon: SquareArrowOutUpRight, onSelect: () => navigate(`/hosts/${h.id}`) });
    if (h.canCreateVm) list.push({ key: 'new-vm', label: t('host.newVm'), icon: Plus, onSelect: () => setDialog({ kind: 'createVm', hostId: h.id }) });
    list.push({
      key: 'refresh', label: t('common.refresh'), icon: RefreshCw, disabled: !h.connected,
      onSelect: async () => { try { await post(`/api/v1/hosts/${h.id}/refresh`); inv.reload(); } catch (e) { fail(e); } },
    });
    if (manageHosts) {
      list.push({ key: 'edit', label: t('menu.editDescription'), icon: Pencil, separatorBefore: true, onSelect: () => setDialog({ kind: 'editHost', host: h }) });
      list.push({
        key: 'token', label: t('host.newToken'), icon: KeyRound,
        onSelect: async () => {
          try { setDialog({ kind: 'enroll', info: await post<EnrollmentInfo>(`/api/v1/hosts/${h.id}/enrollment-token`), hostId: h.id }); } catch (e) { fail(e); }
        },
      });
      list.push({ key: 'revoke', label: t('host.revoke'), icon: ShieldOff, danger: true, separatorBefore: true, disabled: h.status === 'Revoked', onSelect: () => setDialog({ kind: 'revokeHost', host: h }) });
      list.push({ key: 'delete', label: t('host.delete'), icon: Trash2, danger: true, onSelect: () => setDialog({ kind: 'deleteHost', host: h }) });
    }
    return list;
  }, [t, navigate, manageHosts, inv, fail]);

  const groupActions = useCallback((g: Group | null): Action[] => {
    if (!manageGroups) return g ? [{ key: 'open', label: t('menu.open'), icon: SquareArrowOutUpRight, onSelect: () => navigate(`/groups/${g.id}`) }] : [];
    if (!g) return [{ key: 'new', label: t('group.new'), icon: FolderPlus, onSelect: () => setDialog({ kind: 'newGroup', parentId: null }) }];
    return [
      { key: 'open', label: t('menu.open'), icon: SquareArrowOutUpRight, onSelect: () => navigate(`/groups/${g.id}`) },
      { key: 'new-sub', label: t('group.newSub'), icon: FolderPlus, separatorBefore: true, onSelect: () => setDialog({ kind: 'newGroup', parentId: g.id }) },
      { key: 'add-vms', label: t('group.addVms'), icon: Plus, onSelect: () => setDialog({ kind: 'addVms', group: g }) },
      ...(inv.hosts.some((h) => h.canCreateVm) ? [{ key: 'new-vm', label: t('host.newVm'), icon: Monitor, onSelect: () => setDialog({ kind: 'createVm', groupId: g.id }) }] : []),
      { key: 'edit', label: t('group.edit'), icon: Pencil, separatorBefore: true, onSelect: () => setDialog({ kind: 'editGroup', group: g }) },
      { key: 'move', label: t('group.move'), icon: FolderInput, onSelect: () => setDialog({ kind: 'moveGroup', group: g }) },
      { key: 'delete', label: t('group.delete'), icon: Trash2, danger: true, separatorBefore: true, onSelect: () => setDialog({ kind: 'deleteGroup', group: g }) },
    ];
  }, [manageGroups, t, navigate, inv.hosts]);

  const value = useMemo<Ops>(() => ({
    vmActions, powerActions, hostActions, groupActions, queued, placeVms, moveGroup, openConsole,
    openCreateVm: (o) => setDialog({ kind: 'createVm', hostId: o?.hostId, groupId: o?.groupId }),
    openAddHost: () => setDialog({ kind: 'addHost' }),
    openNewGroup: (parentId) => setDialog({ kind: 'newGroup', parentId }),
    openPower: (vm, op) => setDialog({ kind: 'power', vm, op }),
  }), [vmActions, powerActions, hostActions, groupActions, queued, placeVms, moveGroup, openConsole]);

  const d = dialog;
  return (
    <Ctx.Provider value={value}>
      {children}
      {d?.kind === 'power' && <PowerDialog vm={d.vm} op={d.op} onQueued={queued} onClose={close} />}
      {d?.kind === 'checkpoint' && <CreateCheckpointDialog vm={d.vm} onQueued={queued} onClose={close} />}
      {d?.kind === 'editVm' && (
        <EditDialog title={t('vm.editTitle', { name: d.vm.name })} initialDescription={d.vm.description} onClose={close}
          onSave={async (_, description) => { await patch(`/api/v1/vms/${d.vm.id}`, { description }); inv.reload(); }} />
      )}
      {d?.kind === 'deleteVm' && (
        <ConfirmTypedDialog title={t('vm.deleteTitle', { name: d.vm.name })} name={d.vm.name} confirmLabel={t('vm.delete')} onClose={close}
          impact={<>{t('vm.deleteImpact')}<br /><strong>{t('vm.deleteDisksKept')}</strong></>}
          onConfirm={async (typed) => {
            const r = await post<Accepted>(`/api/v1/vms/${d.vm.id}/delete`, { confirmName: typed, idempotencyKey: newIdempotencyKey() });
            queued(r, `${t('op.Delete')} — ${d.vm.name}`);
          }} />
      )}
      {d?.kind === 'addToGroup' && (
        <AddToGroupDialog vmIds={d.vms.map((v) => v.id)} title={`${t('vm.addToGroup')} ${d.vms.map((v) => v.name).join(', ')}`} onClose={close}
          onAdd={async (groupId) => { await post(`/api/v1/groups/${groupId}/members`, { vmIds: d.vms.map((v) => v.id) }); inv.reload(); }} />
      )}
      {d?.kind === 'createVm' && <CreateVmWizard hostId={d.hostId} groupId={d.groupId} onQueued={queued} onClose={close} />}
      {d?.kind === 'addHost' && (
        // The enrollment guide replaces this dialog: closing must not clear it.
        <AddHostDialog onClose={() => setDialog((cur) => (cur?.kind === 'addHost' ? null : cur))}
          onCreated={(info, hostId) => { inv.reload(); setDialog({ kind: 'enroll', info, hostId }); }} />
      )}
      {d?.kind === 'enroll' && <EnrollmentDialog info={d.info} hostId={d.hostId} onClose={() => { close(); inv.reload(); }} />}
      {d?.kind === 'editHost' && (
        <EditDialog title={t('host.editTitle', { name: d.host.displayName })} withName initialName={d.host.displayName} initialDescription={d.host.description} onClose={close}
          onSave={async (name, description) => { await patch(`/api/v1/hosts/${d.host.id}`, { displayName: name, description }); inv.reload(); }} />
      )}
      {d?.kind === 'deleteHost' && (
        <ConfirmTypedDialog title={t('host.deleteTitle', { name: d.host.displayName })} name={d.host.displayName} confirmLabel={t('host.delete')} onClose={close}
          impact={t('host.deleteImpact', { n: d.host.vmCount })}
          onConfirm={async (typed) => { await post(`/api/v1/hosts/${d.host.id}/delete`, { confirmName: typed }); inv.reload(); navigate('/hosts'); }} />
      )}
      {d?.kind === 'revokeHost' && <RevokeHostDialog host={d.host} onClose={() => { close(); inv.reload(); }} />}
      {d?.kind === 'newGroup' && <NewGroupDialog parentId={d.parentId} onClose={close} onCreated={(id) => { inv.reload(); navigate(`/groups/${id}`); }} />}
      {d?.kind === 'editGroup' && (
        <EditDialog title={`${t('group.edit').replace('…', '')} — ${d.group.name}`} withName nameLabel={t('group.name')} initialName={d.group.name} initialDescription={d.group.description}
          onClose={close} onSave={async (name, description) => { await patch(`/api/v1/groups/${d.group.id}`, { name, description }); inv.reload(); }} />
      )}
      {d?.kind === 'moveGroup' && (
        <MoveGroupDialog group={d.group} onClose={close}
          onMove={async (parentId) => { await patch(`/api/v1/groups/${d.group.id}`, parentId ? { parentId } : { moveToRoot: true }); inv.reload(); }} />
      )}
      {d?.kind === 'deleteGroup' && (
        <ActionDialog title={t('group.delete').replace('…', '')} confirmLabel={t('common.delete')} danger onClose={close}
          onConfirm={async () => { await del(`/api/v1/groups/${d.group.id}`); inv.reload(); navigate('/inventory'); }}>
          <p className="text-sm">{t('group.deleteConfirm', { name: d.group.name })}</p>
        </ActionDialog>
      )}
      {d?.kind === 'addVms' && <AddVmsDialog group={d.group} onClose={() => { close(); inv.reload(); }} />}
    </Ctx.Provider>
  );
}

export function useOps(): Ops {
  const v = useContext(Ctx);
  if (!v) throw new Error('OperationsProvider missing');
  return v;
}
