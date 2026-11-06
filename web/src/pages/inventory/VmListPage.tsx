import { Search } from 'lucide-react';
import { useMemo, useState } from 'react';
import { ErrorAlert, Loading } from '@/components/common';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { VmTable } from '@/components/vm/VmTable';
import { useI18n, type TKey } from '@/i18n';

/** All VMs with filters (Inventory > All VMs). */
export function VmListView() {
  const { t } = useI18n();
  const inv = useInventory();
  const [search, setSearch] = useState('');
  const [state, setState] = useState('');
  const [host, setHost] = useState('');
  const rows = useMemo(() => inv.vms.filter((v) =>
    (!search || [v.name, v.hostName ?? '', v.notes ?? '', v.guestOperatingSystem ?? ''].some((s) => s.toLowerCase().includes(search.toLowerCase())))
    && (!state || v.powerState === state) && (!host || v.hostId === host)), [inv.vms, search, state, host]);

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-center gap-2 border-b bg-panel px-4 py-2">
        <div className="relative w-full max-w-xs">
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
          <Input aria-label={t('common.search')} placeholder={t('common.search')} value={search} onChange={(e) => setSearch(e.target.value)} className="pl-8" />
        </div>
        <NativeSelect aria-label={t('vm.state')} value={state} onChange={(e) => setState(e.target.value)} className="w-40">
          <option value="">{t('vm.filterState')}</option>
          {['Running', 'Off', 'Saved', 'Paused'].map((s) => <option key={s} value={s}>{t(('state.' + s) as TKey)}</option>)}
        </NativeSelect>
        {inv.hosts.length > 1 && (
          <NativeSelect aria-label={t('vm.host')} value={host} onChange={(e) => setHost(e.target.value)} className="w-44">
            <option value="">{t('common.all')} — {t('nav.hosts')}</option>
            {inv.hosts.map((h) => <option key={h.id} value={h.id}>{h.displayName}</option>)}
          </NativeSelect>
        )}
        <span className="ml-auto text-xs text-muted-foreground tabular">{rows.length} / {inv.vms.length}</span>
      </div>
      <ErrorAlert error={inv.error} className="mx-4" />
      {inv.loading ? <Loading /> : <VmTable vms={rows} className="flex-1" />}
    </div>
  );
}
