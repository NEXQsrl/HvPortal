import { Link } from 'react-router-dom';
import { get } from '../api/client';
import type { GatewayConnection } from '../api/types';
import { AgentSetupGuide } from '../components/AgentSetupGuide';
import { ErrorAlert, Loading, useLoad } from '../components/ui';
import { useI18n } from '../i18n';
import { useSession } from '../session';

export function GuidePage() {
  const { t } = useI18n();
  const { can } = useSession();
  const allowed = can('hosts.manage');
  const info = useLoad(() => (allowed ? get<GatewayConnection>('/api/v1/hosts/connection-info') : Promise.resolve(null)), [allowed]);
  return (
    <>
      <div className="row"><h1>{t('guide.title')}</h1><span className="spacer" />
        {allowed && <Link className="button" to="/hosts">{t('guide.goHosts')}</Link>}</div>
      <p className="muted">{t('guide.intro')}</p>
      {!allowed ? <div className="alert info">{t('planned.title')}</div>
        : info.loading && !info.data ? <Loading />
        : info.data ? <div className="card"><AgentSetupGuide connection={info.data} /></div>
        : <ErrorAlert error={info.error} />}
    </>
  );
}
