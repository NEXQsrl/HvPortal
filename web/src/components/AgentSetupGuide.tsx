import { CircleCheck, LifeBuoy, LoaderCircle, TriangleAlert } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import type { GatewayConnection, Host } from '@/api/types';
import { useI18n } from '@/i18n';
import { useNow } from '@/lib/hooks';
import { Notice } from './common';
import { AGENT_ZIP, Bullets, Command, PackageDownload, Step, Steps, TROUBLE_CONNECT, TroubleshootingTable } from './guide/GuideKit';
import { NativeSelect } from './ui/native-select';

/**
 * Step-by-step agent installation guide. With a token it is "live": ready-to-paste commands, countdown, host status.
 * In the enrollment dialog troubleshooting is shown inline; on the guide pages it links to its own page.
 */
export function AgentSetupGuide({ connection, token, expiresAt, host, troubleshooting = 'inline' }: {
  connection: GatewayConnection;
  token?: string;
  expiresAt?: string;
  host?: Host | null;
  troubleshooting?: 'inline' | 'link';
}) {
  const { t, fmtDate } = useI18n();
  const [name, setName] = useState(connection.preferredName);
  const now = useNow();
  const remaining = expiresAt ? Math.max(0, Math.floor((new Date(expiresAt).getTime() - now) / 1000)) : 0;
  const expired = expiresAt !== undefined && remaining <= 0;
  const url = `https://${name}:${connection.agentPort}`;
  const folder = 'C:\\HvPortal\\agent-package';
  const tokenValue = token ?? '<token>';

  return (
    <div>
      {!connection.listensOnNetwork && <Notice kind="danger" className="mb-4">{t('guide.loopbackOnly', { bind: connection.bindAddress })}</Notice>}
      <Steps>
        <Step n={1} title={t('guide.s1.title')}>
          <Bullets items={[
            t('guide.s1.admin'),
            t('guide.s1.network', { port: connection.agentPort }),
            t('guide.s1.hyperv'),
            t('guide.s1.module'),
            ...(token ? [t('guide.s1.token')] : []),
          ]} />
        </Step>
        <Step n={2} title={t('guide.s2.title')}>
          <PackageDownload help={t('guide.s2.help')} />
        </Step>
        <Step n={3} title={t('guide.s3.title')}>
          <p className="text-muted-foreground">{t('guide.s3.help')}</p>
          <Command text={`Get-FileHash .\\${AGENT_ZIP} -Algorithm SHA256`} />
          <p className="text-muted-foreground">{t('guide.s3.compare')}</p>
          <Command text={`Unblock-File .\\${AGENT_ZIP}\nExpand-Archive .\\${AGENT_ZIP} -DestinationPath ${folder} -Force`} />
          <p className="text-xs text-muted-foreground">{t('guide.s3.unblock')}</p>
        </Step>
        <Step n={4} title={t('guide.s4.title')}>
          <p className="text-muted-foreground">{t('guide.s4.help')}</p>
          <div className="grid max-w-md gap-1.5">
            <label htmlFor="gw-name" className="text-xs font-medium text-muted-foreground">{t('guide.s4.name')}</label>
            <NativeSelect id="gw-name" value={name} onChange={(e) => setName(e.target.value)}>
              {connection.gatewayNames.map((n) => <option key={n} value={n}>{n}</option>)}
            </NativeSelect>
            <div className="text-xs text-muted-foreground">{t('guide.s4.nameHelp')}</div>
          </div>
          <Command text={`Test-NetConnection ${name} -Port ${connection.agentPort}`} />
          <p className="text-muted-foreground">{t('guide.s4.expect')}</p>
        </Step>
        <Step n={5} title={t('guide.s5.title')}>
          {token ? (
            expired
              ? <Notice kind="danger">{t('guide.s5.expired')}</Notice>
              : <Notice kind={remaining < 180 ? 'warn' : 'info'}>{t('guide.s5.countdown', { time: formatMmSs(remaining), at: fmtDate(expiresAt) })}</Notice>
          ) : <Notice kind="info">{t('guide.s5.noToken')}</Notice>}
          <Command text={`cd ${folder}\\installer\n$env:HVP_ENROLLMENT_TOKEN = '${tokenValue}'\n.\\Install-Agent.ps1 -Source ..\\agent -Gateway ${url} -CaSha256 ${connection.caFingerprintSha256}`} />
          <p className="text-muted-foreground">{t('guide.s5.what')}</p>
          <div className="text-xs text-muted-foreground">{t('host.fingerprint')}</div>
          <code className="secret block rounded-md border bg-muted px-2 py-1 font-mono text-xs break-all">{connection.caFingerprintSha256}</code>
          <p className="text-xs text-muted-foreground">{t('guide.s5.fingerprint')}</p>
        </Step>
        <Step n={6} title={t('guide.s6.title')}>
          {host !== undefined && <HostStatus host={host} />}
          <p className="text-muted-foreground">{t('guide.s6.help')}</p>
          <Command text={`& 'C:\\Program Files\\HvPortal\\Agent\\HvPortal.Agent.exe' inventory`} />
          <p className="text-muted-foreground">{t('guide.s6.expect')}</p>
        </Step>
      </Steps>

      {troubleshooting === 'inline' ? (
        <details className="guide-trouble mt-4 rounded-lg border bg-card">
          <summary className="cursor-pointer px-3 py-2 text-sm font-semibold">{t('guide.trouble.title')}</summary>
          <div className="border-t"><TroubleshootingTable keys={TROUBLE_CONNECT} port={connection.agentPort} /></div>
        </details>
      ) : (
        <Link to="/guide/troubleshooting" className="mt-4 flex items-center gap-2 rounded-lg border bg-card px-3 py-2 text-sm hover:bg-accent/50">
          <LifeBuoy className="size-4 text-muted-foreground" /><span className="font-medium">{t('guide.trouble.title')}</span>
          <span className="text-muted-foreground">— {t('guide.page.trouble.desc')}</span>
        </Link>
      )}
    </div>
  );
}

function HostStatus({ host }: { host: Host | null }) {
  const { t } = useI18n();
  if (!host || !host.connected) return <div className="flex items-center gap-2 rounded-lg border border-info/30 bg-info/5 px-3 py-2" role="status"><LoaderCircle className="size-4 animate-spin text-info" />{t('guide.s6.waiting')}</div>;
  if (host.access === 'Ok') return <div className="flex items-center gap-2 rounded-lg border border-success/40 bg-success/10 px-3 py-2" role="status"><CircleCheck className="size-4 text-success" />{t('guide.s6.ok', { n: host.vmCount })}</div>;
  if (!host.access) return <div className="flex items-center gap-2 rounded-lg border border-info/30 bg-info/5 px-3 py-2" role="status"><LoaderCircle className="size-4 animate-spin text-info" />{t('guide.s6.connected')}</div>;
  return <div className="flex items-center gap-2 rounded-lg border border-warning/40 bg-warning/10 px-3 py-2" role="status"><TriangleAlert className="size-4 text-warning" />{t('guide.s6.connectedNoAccess', { access: host.access })} {host.accessMessage}</div>;
}

function formatMmSs(seconds: number) {
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  return `${m}:${String(s).padStart(2, '0')}`;
}
