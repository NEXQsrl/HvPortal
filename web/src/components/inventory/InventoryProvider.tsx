import { createContext, useCallback, useContext, useMemo, type ReactNode } from 'react';
import { get } from '@/api/client';
import type { Group, GroupsResponse } from '@/api/ops-types';
import type { Host, Vm } from '@/api/types';
import { useLive, useLoad } from '@/lib/hooks';

interface Inventory {
  hosts: Host[];
  vms: Vm[];
  groups: Group[];
  canManageGroups: boolean;
  loading: boolean;
  error: unknown;
  reload: () => void;
  vmById: Map<string, Vm>;
  hostById: Map<string, Host>;
  groupById: Map<string, Group>;
  groupsOfVm: (vmId: string) => Group[];
  /** Ancestors of a group from the root down to the group itself. */
  groupPath: (groupId: string) => Group[];
  childrenOf: (parentId: string | null) => Group[];
}

const Ctx = createContext<Inventory | null>(null);

/**
 * Shared inventory snapshot for the tree, lists, command palette and dialogs. Every item is already filtered by the
 * server to what the user may see; reloaded on server events (inventory, groups).
 */
export function InventoryProvider({ children }: { children: ReactNode }) {
  const hosts = useLoad(() => get<Host[]>('/api/v1/hosts'));
  const vms = useLoad(() => get<Vm[]>('/api/v1/vms'));
  const groups = useLoad(() => get<GroupsResponse>('/api/v1/groups'));
  const reloadInventory = useCallback(() => { hosts.reload(); vms.reload(); }, [hosts.reload, vms.reload]);
  const reloadGroups = useCallback(() => { groups.reload(); vms.reload(); }, [groups.reload, vms.reload]);
  useLive(reloadInventory, ['inventoryChanged']);
  useLive(reloadGroups, ['groupsChanged']);

  const value = useMemo<Inventory>(() => {
    const h = hosts.data ?? [];
    const v = vms.data ?? [];
    const g = groups.data?.groups ?? [];
    const vmById = new Map(v.map((x) => [x.id, x]));
    const hostById = new Map(h.map((x) => [x.id, x]));
    const groupById = new Map(g.map((x) => [x.id, x]));
    const byVm = new Map<string, Group[]>();
    for (const grp of g) for (const id of grp.vmIds) byVm.set(id, [...(byVm.get(id) ?? []), grp]);
    return {
      hosts: h,
      vms: v,
      groups: g,
      canManageGroups: groups.data?.canManage ?? false,
      loading: (hosts.loading && !hosts.data) || (vms.loading && !vms.data),
      error: hosts.error ?? vms.error ?? groups.error,
      reload: () => { hosts.reload(); vms.reload(); groups.reload(); },
      vmById,
      hostById,
      groupById,
      groupsOfVm: (id) => byVm.get(id) ?? [],
      groupPath: (id) => {
        const path: Group[] = [];
        let cur = groupById.get(id);
        while (cur && path.length < 20 && !path.includes(cur)) {
          path.unshift(cur);
          cur = cur.parentId ? groupById.get(cur.parentId) : undefined;
        }
        return path;
      },
      childrenOf: (parentId) => g.filter((x) => x.parentId === parentId).sort((a, b) => a.name.localeCompare(b.name)),
    };
  }, [hosts.data, vms.data, groups.data, hosts.loading, vms.loading, hosts.error, vms.error, groups.error, hosts.reload, vms.reload, groups.reload]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useInventory(): Inventory {
  const v = useContext(Ctx);
  if (!v) throw new Error('InventoryProvider missing');
  return v;
}
