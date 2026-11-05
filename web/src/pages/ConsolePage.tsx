import { useEffect } from 'react';
import { useParams } from 'react-router-dom';
import { get, newIdempotencyKey, post } from '@/api/client';
import type { Vm } from '@/api/types';
import { ErrorAlert, Loading } from '@/components/common';
import { VmConsole } from '@/components/console/VmConsole';
import { useI18n } from '@/i18n';
import { useLoad } from '@/lib/hooks';
import { toast } from '@/lib/toast';

/** Stand-alone console window (opened with "Open in a new window"). */
export function ConsolePage() {
  const { id } = useParams();
  const { t } = useI18n();
  const data = useLoad(() => get<{ vm: Vm }>(`/api/v1/vms/${id}`), [id]);
  const vm = data.data?.vm;
  useEffect(() => { if (vm) document.title = t('con.title', { name: vm.name }); }, [vm, t]);
  if (!vm) return data.loading ? <Loading /> : <div className="p-4"><ErrorAlert error={data.error} /></div>;
  return (
    <div className="flex h-dvh flex-col bg-background">
      <VmConsole vm={vm} popout={false} className="h-full"
        onStart={async () => {
          try {
            await post(`/api/v1/vms/${vm.id}/power`, { operation: 'Start', idempotencyKey: newIdempotencyKey() });
            toast.info(t('job.queuedToast'), `${t('op.Start')} — ${vm.name}`);
            window.setTimeout(data.reload, 3000);
          } catch (e) { toast.error(t('common.error'), String((e as Error).message)); }
        }} />
    </div>
  );
}
