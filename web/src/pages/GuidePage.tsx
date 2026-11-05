import { BookOpen, Server } from 'lucide-react';
import { Link } from 'react-router-dom';
import { get } from '@/api/client';
import type { GatewayConnection } from '@/api/types';
import { AgentSetupGuide } from '@/components/AgentSetupGuide';
import { ErrorAlert, Loading, Notice, PageHeader, Section } from '@/components/common';
import { Button } from '@/components/ui/button';
import { useI18n } from '@/i18n';
import { useLoad } from '@/lib/hooks';
import { useSession } from '@/session';

export function GuidePage() {
  const { t } = useI18n();
  const { can } = useSession();
  const allowed = can('hosts.manage');
  const info = useLoad(() => (allowed ? get<GatewayConnection>('/api/v1/hosts/connection-info') : Promise.resolve(null)), [allowed]);
  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<BookOpen className="size-5" />} title={t('guide.title')}
        actions={allowed && <Button size="sm" variant="outline" asChild><Link to="/hosts"><Server />{t('guide.goHosts')}</Link></Button>} />
      <div className="min-h-0 flex-1 space-y-3 overflow-auto p-4">
        <p className="max-w-4xl text-sm text-muted-foreground">{t('guide.intro')}</p>
        {!allowed ? <Notice kind="info">{t('planned.title')}</Notice>
          : info.loading && !info.data ? <Loading />
          : info.data ? <Section bodyClassName="p-5"><div className="max-w-4xl"><AgentSetupGuide connection={info.data} /></div></Section>
          : <ErrorAlert error={info.error} />}
      </div>
    </div>
  );
}
