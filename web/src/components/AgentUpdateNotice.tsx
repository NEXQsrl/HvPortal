import { RefreshCw } from 'lucide-react';
import { Link } from 'react-router-dom';
import type { Host } from '@/api/types';
import { useI18n, type TKey } from '@/i18n';
import { compareVersions, semver, shortVersion } from '@/lib/format';
import { useSession } from '@/session';
import { Notice } from './common';

/** Agent version that introduced each function (agent protocol history, see AgentProtocol.Version). */
const AGENT_FEATURES: { since: string; key: TKey }[] = [
  { since: '0.3.0', key: 'agentfeat.ops' },
  { since: '0.3.1', key: 'agentfeat.location' },
  { since: '0.3.2', key: 'agentfeat.vmgroups' },
  { since: '0.3.3', key: 'agentfeat.files' },
];

/** Functions not available on a host because its agent is too old (empty = nothing missing). */
export function missingAgentFeatures(agentVersion: string | null | undefined): TKey[] {
  const v = semver(agentVersion);
  if (!v) return [];
  return AGENT_FEATURES.filter((f) => compareVersions(v, semver(f.since)!) < 0).map((f) => f.key);
}

/**
 * Shown when a host's agent lacks functions of this portal: lists exactly what is missing (a newer gateway alone,
 * with no new agent function, does not ask to update the agents).
 */
export function AgentUpdateNotice({ host, className }: { host: Host | undefined; className?: string }) {
  const { t } = useI18n();
  const { can } = useSession();
  const missing = missingAgentFeatures(host?.agentVersion);
  if (!host || missing.length === 0) return null;
  return (
    <Notice kind="warn" className={className}>
      <span>{t('guide.hint.missing', { v: shortVersion(host.agentVersion), list: missing.map((k) => t(k)).join(', ') })} </span>
      {can('hosts.manage')
        ? <Link to="/guide/update" className="inline-flex items-center gap-1 font-medium text-primary hover:underline"><RefreshCw className="size-3.5" />{t('guide.page.update.title')}</Link>
        : <span>{t('guide.hint.askAdmin')}</span>}
    </Notice>
  );
}
