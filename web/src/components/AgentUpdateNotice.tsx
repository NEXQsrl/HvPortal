import { RefreshCw } from 'lucide-react';
import { Link } from 'react-router-dom';
import type { Host } from '@/api/types';
import { useI18n } from '@/i18n';
import { isAgentOlder, shortVersion } from '@/lib/format';
import { useSession } from '@/session';
import { Notice } from './common';

/** Shown when a host's agent is older than the gateway: new functions are unavailable there until it is updated. */
export function AgentUpdateNotice({ host, className }: { host: Host | undefined; className?: string }) {
  const { t } = useI18n();
  const { state, can } = useSession();
  if (!host || !isAgentOlder(host.agentVersion, state?.version)) return null;
  return (
    <Notice kind="warn" className={className}>
      <span>{t('guide.hint.outdated', { v: shortVersion(host.agentVersion), g: shortVersion(state?.version) })} </span>
      {can('hosts.manage')
        ? <Link to="/guide/update" className="inline-flex items-center gap-1 font-medium text-primary hover:underline"><RefreshCw className="size-3.5" />{t('guide.page.update.title')}</Link>
        : <span>{t('guide.hint.askAdmin')}</span>}
    </Notice>
  );
}
