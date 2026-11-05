import { FolderPlus, Monitor, Server, UserRound } from 'lucide-react';
import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { GroupIcon, HostIcon, VmIcon } from '@/components/common';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useOps } from '@/components/ops/OperationsProvider';
import { Command, CommandDialog, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList, CommandSeparator, CommandShortcut } from '@/components/ui/command';
import { useI18n, type TKey } from '@/i18n';
import { useSession } from '@/session';
import { GUIDE_PAGES } from '@/pages/guide/GuidePages';
import { NAV_ITEMS } from './nav';

/** Ctrl+K: jump to any VM, host, group or page, or start a common action. */
export function CommandPalette({ open, onOpenChange }: { open: boolean; onOpenChange: (o: boolean) => void }) {
  const { t } = useI18n();
  const { can } = useSession();
  const inv = useInventory();
  const ops = useOps();
  const navigate = useNavigate();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        onOpenChange(!open);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onOpenChange]);

  const go = (path: string) => { onOpenChange(false); navigate(path); };
  const run = (fn: () => void) => { onOpenChange(false); fn(); };

  return (
    <CommandDialog open={open} onOpenChange={onOpenChange} title={t('cmd.title')} description={t('cmd.placeholder')}>
      <Command>
      <CommandInput placeholder={t('cmd.placeholder')} />
      <CommandList className="max-h-[60vh]">
        <CommandEmpty>{t('cmd.empty')}</CommandEmpty>
        {inv.vms.length > 0 && (
          <CommandGroup heading={t('cmd.vms')}>
            {inv.vms.map((v) => (
              <CommandItem key={v.id} value={`vm ${v.name} ${v.hostName ?? ''} ${v.id}`} onSelect={() => go(`/vms/${v.id}`)}>
                <VmIcon state={v.powerState} critical={v.isCritical} /><span className="truncate">{v.name}</span>
                <span className="ml-auto truncate text-xs text-muted-foreground">{v.hostName}</span>
              </CommandItem>
            ))}
          </CommandGroup>
        )}
        {inv.hosts.length > 0 && (
          <CommandGroup heading={t('cmd.hosts')}>
            {inv.hosts.map((h) => (
              <CommandItem key={h.id} value={`host ${h.displayName} ${h.machineName ?? ''}`} onSelect={() => go(`/hosts/${h.id}`)}>
                <HostIcon connected={h.connected} access={h.access} />{h.displayName}
                <span className="ml-auto text-xs text-muted-foreground">{h.vmCount} VM</span>
              </CommandItem>
            ))}
          </CommandGroup>
        )}
        {inv.groups.length > 0 && (
          <CommandGroup heading={t('cmd.groups')}>
            {inv.groups.map((g) => (
              <CommandItem key={g.id} value={`group ${inv.groupPath(g.id).map((x) => x.name).join(' ')}`} onSelect={() => go(`/groups/${g.id}`)}>
                <GroupIcon />{inv.groupPath(g.id).map((x) => x.name).join(' / ')}
              </CommandItem>
            ))}
          </CommandGroup>
        )}
        <CommandSeparator />
        <CommandGroup heading={t('cmd.actions')}>
          {inv.hosts.some((h) => h.canCreateVm) && <CommandItem value="action new vm" onSelect={() => run(() => ops.openCreateVm())}><Monitor />{t('wiz.title')}</CommandItem>}
          {can('hosts.manage') && <CommandItem value="action add host" onSelect={() => run(() => ops.openAddHost())}><Server />{t('host.add')}</CommandItem>}
          {inv.canManageGroups && <CommandItem value="action new group" onSelect={() => run(() => ops.openNewGroup(null))}><FolderPlus />{t('group.new')}</CommandItem>}
        </CommandGroup>
        <CommandGroup heading={t('cmd.pages')}>
          {NAV_ITEMS.filter((n) => !n.permission || n.permission.some(can)).map((n) => (
            <CommandItem key={n.to} value={`page ${t(n.label as TKey)}`} onSelect={() => go(n.to)}>
              <n.icon />{t(n.label as TKey)}{n.planned && <CommandShortcut>{t('shell.soon')}</CommandShortcut>}
            </CommandItem>
          ))}
          <CommandItem value={`page ${t('inv.view.hosts')}`} onSelect={() => go('/inventory?view=hosts')}><Server />{t('nav.inventory')} › {t('inv.view.hosts')}</CommandItem>
          <CommandItem value={`page ${t('inv.view.vms')}`} onSelect={() => go('/inventory?view=vms')}><Monitor />{t('nav.inventory')} › {t('inv.view.vms')}</CommandItem>
          <CommandItem value="page account" onSelect={() => go('/account')}><UserRound />{t('nav.account')}</CommandItem>
        </CommandGroup>
        {can('hosts.manage') && (
          <CommandGroup heading={t('guide.home.title')}>
            {GUIDE_PAGES.filter((p) => !p.end).map((p) => (
              <CommandItem key={p.to} value={`guide ${t(p.title)} ${p.desc ? t(p.desc) : ''}`} onSelect={() => go(p.to)}>
                <p.icon />{t(p.title)}
              </CommandItem>
            ))}
          </CommandGroup>
        )}
      </CommandList>
      </Command>
    </CommandDialog>
  );
}
