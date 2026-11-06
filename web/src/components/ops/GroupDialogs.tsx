import { Search } from 'lucide-react';
import { useMemo, useState } from 'react';
import { post, put } from '@/api/client';
import type { Group } from '@/api/ops-types';
import { GroupIcon, Notice, VmIcon } from '@/components/common';
import { ActionDialog, Field, HelpTip } from '@/components/dialogs';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { useI18n } from '@/i18n';
import { GROUP_NAME } from '@/lib/format';
import { toast } from '@/lib/toast';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';
import { draftFromPolicy, LimitsEditor, limitsEmpty, limitsValid, policyFromDraft, type LimitsDraft } from './GroupLimits';

/** Indented, selectable list of the group tree (single choice). */
export function GroupPickerList({ value, onChange, exclude, allowRoot }: {
  value: string | null; onChange: (id: string | null) => void; exclude?: Set<string>; allowRoot?: boolean;
}) {
  const { t } = useI18n();
  const inv = useInventory();
  const rows: { group: Group | null; depth: number }[] = [];
  if (allowRoot) rows.push({ group: null, depth: 0 });
  const walk = (parent: string | null, depth: number) => {
    for (const g of inv.childrenOf(parent)) {
      if (exclude?.has(g.id)) continue;
      rows.push({ group: g, depth });
      walk(g.id, depth + 1);
    }
  };
  walk(null, allowRoot ? 1 : 0);
  return (
    <div role="listbox" aria-label={t('group.pick')} className="max-h-72 overflow-auto rounded-md border p-1">
      {rows.length === 0 && <div className="p-3 text-sm text-muted-foreground">{t('tree.empty')}</div>}
      {rows.map(({ group, depth }) => {
        const id = group?.id ?? null;
        const selected = value === id;
        return (
          <button key={id ?? 'root'} type="button" role="option" aria-selected={selected}
            className={cn('flex w-full items-center gap-2 rounded px-2 py-1 text-left text-sm hover:bg-accent', selected && 'bg-primary/10 text-primary ring-1 ring-primary/30')}
            style={{ paddingLeft: 8 + depth * 16 }} onClick={() => onChange(id)}>
            <GroupIcon open={selected} />
            <span className="truncate">{group?.name ?? t('group.root')}</span>
          </button>
        );
      })}
    </div>
  );
}

export function NewGroupDialog({ parentId, onCreated, onClose }: { parentId: string | null; onCreated: (id: string) => void; onClose: () => void }) {
  const { t } = useI18n();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [parent, setParent] = useState<string | null>(parentId);
  // Advanced mode: limits of the group (quotas, hosts, networks, storage), set right after the group is created.
  const { can } = useSession();
  const canLimit = can('hosts.manage');
  const [advanced, setAdvanced] = useState(false);
  const [limits, setLimits] = useState<LimitsDraft>(() => draftFromPolicy(null));
  const valid = GROUP_NAME.test(name.trim()) && (!advanced || limitsValid(limits));
  return (
    <ActionDialog title={t('group.new')} confirmLabel={t('common.create')} disabled={!valid} onClose={onClose} wide={advanced}
      onConfirm={async () => {
        const r = await post<{ id: string }>('/api/v1/groups', { name: name.trim(), description: description || undefined, parentId: parent });
        if (advanced && !limitsEmpty(limits)) {
          // The group exists even if its limits are refused: say so and open it (the limits can be set from its page).
          try {
            await put(`/api/v1/groups/${r.id}/policy`, policyFromDraft(limits));
          } catch (e) {
            toast.error(t('lim.notSaved'), e instanceof Error ? e.message : String(e));
          }
        }
        onCreated(r.id);
      }}>
      <Field label={t('group.name')} htmlFor="group-name" error={name && !valid ? t('wiz.nameInvalid') : undefined}>
        <Input id="group-name" value={name} maxLength={64} onChange={(e) => setName(e.target.value)} autoFocus />
      </Field>
      <Field label={t('group.parent')}>
        <GroupPickerList value={parent} onChange={setParent} allowRoot />
      </Field>
      <Field label={t('common.description')} htmlFor="group-desc">
        <Textarea id="group-desc" rows={2} maxLength={1000} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      <p className="text-xs text-muted-foreground">{t('group.inheritHelp')}</p>
      {canLimit && (
        <div className="rounded-lg border">
          <div className="flex items-center gap-2 px-3 py-2">
            <Switch id="group-advanced" checked={advanced} onCheckedChange={setAdvanced} />
            <Label htmlFor="group-advanced" className="cursor-pointer text-sm font-medium">{t('lim.advanced')}</Label>
            <HelpTip>{t('lim.advancedHelp')}</HelpTip>
          </div>
          {advanced && <div className="border-t p-3"><LimitsEditor value={limits} onChange={setLimits} /></div>}
        </div>
      )}
    </ActionDialog>
  );
}

export function MoveGroupDialog({ group, onMove, onClose }: { group: Group; onMove: (parentId: string | null) => Promise<void>; onClose: () => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const [parent, setParent] = useState<string | null>(group.parentId);
  // A group cannot be moved into itself or below itself.
  const exclude = useMemo(() => {
    const s = new Set<string>([group.id]);
    let changed = true;
    while (changed) {
      changed = false;
      for (const g of inv.groups) if (g.parentId && s.has(g.parentId) && !s.has(g.id)) { s.add(g.id); changed = true; }
    }
    return s;
  }, [inv.groups, group.id]);
  return (
    <ActionDialog title={`${t('group.move')} ${group.name}`} confirmLabel={t('group.move').replace('…', '')} disabled={parent === group.parentId}
      onClose={onClose} onConfirm={() => onMove(parent)}>
      <GroupPickerList value={parent} onChange={setParent} exclude={exclude} allowRoot />
      <p className="text-xs text-muted-foreground">{t('group.accessChange')}</p>
    </ActionDialog>
  );
}

export function AddToGroupDialog({ vmIds, title, onAdd, onClose }: { vmIds: string[]; title: string; onAdd: (groupId: string) => Promise<void>; onClose: () => void }) {
  const { t } = useI18n();
  const [group, setGroup] = useState<string | null>(null);
  return (
    <ActionDialog title={title} confirmLabel={t('common.add')} disabled={!group || vmIds.length === 0} onClose={onClose} onConfirm={() => onAdd(group!)}>
      <GroupPickerList value={group} onChange={setGroup} />
      <p className="text-xs text-muted-foreground">{t('group.inheritHelp')}</p>
    </ActionDialog>
  );
}

export function AddVmsDialog({ group, onClose }: { group: Group; onClose: () => void }) {
  const { t } = useI18n();
  const inv = useInventory();
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [filter, setFilter] = useState('');
  const candidates = inv.vms.filter((v) => !group.vmIds.includes(v.id)
    && (!filter || v.name.toLowerCase().includes(filter.toLowerCase()) || (v.hostName ?? '').toLowerCase().includes(filter.toLowerCase())));
  const toggle = (id: string) => setSelected((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  return (
    <ActionDialog title={`${t('group.addVms')} ${group.name}`} confirmLabel={`${t('common.add')} (${selected.size})`} disabled={selected.size === 0}
      onClose={onClose} onConfirm={async () => { await post(`/api/v1/groups/${group.id}/members`, { vmIds: [...selected] }); }}>
      <div className="relative">
        <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
        <Input className="pl-8" placeholder={t('tree.filter')} value={filter} onChange={(e) => setFilter(e.target.value)} aria-label={t('tree.filter')} />
      </div>
      <div className="max-h-80 overflow-auto rounded-md border">
        {candidates.length === 0 && <div className="p-3 text-sm text-muted-foreground">{t('group.noVms')}</div>}
        {candidates.map((v) => (
          <label key={v.id} className="flex cursor-pointer items-center gap-2 border-b px-3 py-1.5 text-sm last:border-0 hover:bg-accent/50">
            <Checkbox checked={selected.has(v.id)} onCheckedChange={() => toggle(v.id)} aria-label={v.name} />
            <VmIcon state={v.powerState} critical={v.isCritical} />
            <span className="flex-1 truncate">{v.name}</span>
            <span className="truncate text-xs text-muted-foreground">{v.hostName}</span>
          </label>
        ))}
      </div>
      <Notice kind="info">{t('group.accessChange')}</Notice>
    </ActionDialog>
  );
}
