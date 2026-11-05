import { CircleCheck, Download, LoaderCircle, TriangleAlert } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { get } from '@/api/client';
import type { GatewayConnection, Host, PackageInfo } from '@/api/types';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLoad, useNow } from '@/lib/hooks';
import { CopyButton, Notice, StatusBadge } from './common';
import { Button } from './ui/button';
import { NativeSelect } from './ui/native-select';

/** Step-by-step agent installation guide. With a token it is "live": ready-to-paste commands, countdown, host status. */
export function AgentSetupGuide({ connection, token, expiresAt, host }: {
  connection: GatewayConnection;
  token?: string;
  expiresAt?: string;
  host?: Host | null;
}) {
  const { t, fmtDate } = useI18n();
  const pkg = useLoad(() => get<{ agent: PackageInfo | null }>('/api/v1/downloads'));
  const [name, setName] = useState(connection.preferredName);
  const now = useNow();
  const remaining = expiresAt ? Math.max(0, Math.floor((new Date(expiresAt).getTime() - now) / 1000)) : 0;
  const expired = expiresAt !== undefined && remaining <= 0;
  const url = `https://${name}:${connection.agentPort}`;
  const zip = 'HvPortal.Agent-win-x64.zip';
  const folder = 'C:\\HvPortal\\agent-package';
  const tokenValue = token ?? '<token>';

  const step = (n: number, titleKey: TKey, body: ReactNode) => (
    <section className="guide-step relative border-l pb-5 pl-6 last:pb-0" aria-labelledby={`step-${n}`}>
      <span className="absolute -left-3 top-0 flex size-6 items-center justify-center rounded-full border bg-card text-xs font-semibold text-primary">{n}</span>
      <h3 id={`step-${n}`} className="mb-2 text-sm leading-6 font-semibold">{t(titleKey)}</h3>
      <div className="space-y-2 text-sm">{body}</div>
    </section>
  );

  return (
    <div className="guide ml-3 space-y-0">
      {!connection.listensOnNetwork && <Notice kind="danger" className="mb-4 -ml-3">{t('guide.loopbackOnly', { bind: connection.bindAddress })}</Notice>}

      {step(1, 'guide.s1.title', (
        <ul className="list-disc space-y-1 pl-4 text-muted-foreground">
          <li>{t('guide.s1.admin')}</li>
          <li>{t('guide.s1.network', { port: connection.agentPort })}</li>
          <li>{t('guide.s1.hyperv')}</li>
          {token && <li>{t('guide.s1.token')}</li>}
        </ul>
      ))}

      {step(2, 'guide.s2.title', pkg.data?.agent ? (
        <>
          <p className="text-muted-foreground">{t('guide.s2.help')}</p>
          <div className="flex flex-wrap items-center gap-2">
            <Button asChild size="sm"><a href="/api/v1/downloads/agent" download={zip}><Download />{t('guide.s2.download')}</a></Button>
            <span className="text-xs text-muted-foreground">{pkg.data.agent.version ?? ''} · {formatBytes(pkg.data.agent.sizeBytes)}</span>
            {pkg.data.agent.signed
              ? <StatusBadge tone="ok">{t('guide.s2.signed')}</StatusBadge>
              : <StatusBadge tone="warn">{t('guide.s2.unsigned')}</StatusBadge>}
          </div>
          {!pkg.data.agent.signed && <p className="text-xs text-muted-foreground">{t('guide.s2.unsignedHelp')}</p>}
          <div className="text-xs text-muted-foreground">SHA-256</div>
          <div className="flex items-center gap-2"><code className="secret pkg-hash flex-1 rounded-md border bg-muted px-2 py-1 font-mono text-xs break-all">{pkg.data.agent.sha256}</code><CopyButton text={pkg.data.agent.sha256} /></div>
        </>
      ) : pkg.loading ? <p className="text-muted-foreground">{t('common.loading')}</p>
        : <Notice kind="warn">{t('guide.s2.missing')}</Notice>)}

      {step(3, 'guide.s3.title', (
        <>
          <p className="text-muted-foreground">{t('guide.s3.help')}</p>
          <Command text={`Get-FileHash .\\${zip} -Algorithm SHA256`} />
          <p className="text-muted-foreground">{t('guide.s3.compare')}</p>
          <Command text={`Unblock-File .\\${zip}\nExpand-Archive .\\${zip} -DestinationPath ${folder} -Force`} />
          <p className="text-xs text-muted-foreground">{t('guide.s3.unblock')}</p>
        </>
      ))}

      {step(4, 'guide.s4.title', (
        <>
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
        </>
      ))}

      {step(5, 'guide.s5.title', (
        <>
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
        </>
      ))}

      {step(6, 'guide.s6.title', (
        <>
          {host !== undefined && <HostStatus host={host} />}
          <p className="text-muted-foreground">{t('guide.s6.help')}</p>
          <Command text={`& 'C:\\Program Files\\HvPortal\\Agent\\HvPortal.Agent.exe' inventory`} />
          <p className="text-muted-foreground">{t('guide.s6.expect')}</p>
        </>
      ))}

      <details className="guide-trouble mt-4 -ml-3 rounded-lg border bg-card">
        <summary className="cursor-pointer px-3 py-2 text-sm font-semibold">{t('guide.trouble.title')}</summary>
        <div className="overflow-auto border-t">
          <table className="w-full text-xs">
            <thead><tr className="text-left text-muted-foreground"><th className="px-3 py-2 font-medium">{t('guide.trouble.symptom')}</th><th className="px-3 py-2 font-medium">{t('guide.trouble.fix')}</th></tr></thead>
            <tbody>
              {TROUBLE.map((k) => (
                <tr key={k} className="border-t align-top">
                  <td className="w-2/5 px-3 py-2 font-mono">{t(`guide.trouble.${k}.s` as TKey)}</td>
                  <td className="px-3 py-2">{t(`guide.trouble.${k}.f` as TKey, { port: connection.agentPort })}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </details>
    </div>
  );
}

const TROUBLE = ['tcp', 'denied', 'scripts', 'rejected', 'pin', 'name', 'access', 'offline'] as const;

function HostStatus({ host }: { host: Host | null }) {
  const { t } = useI18n();
  if (!host || !host.connected) return <div className="flex items-center gap-2 rounded-lg border border-info/30 bg-info/5 px-3 py-2" role="status"><LoaderCircle className="size-4 animate-spin text-info" />{t('guide.s6.waiting')}</div>;
  if (host.access === 'Ok') return <div className="flex items-center gap-2 rounded-lg border border-success/40 bg-success/10 px-3 py-2" role="status"><CircleCheck className="size-4 text-success" />{t('guide.s6.ok', { n: host.vmCount })}</div>;
  if (!host.access) return <div className="flex items-center gap-2 rounded-lg border border-info/30 bg-info/5 px-3 py-2" role="status"><LoaderCircle className="size-4 animate-spin text-info" />{t('guide.s6.connected')}</div>;
  return <div className="flex items-center gap-2 rounded-lg border border-warning/40 bg-warning/10 px-3 py-2" role="status"><TriangleAlert className="size-4 text-warning" />{t('guide.s6.connectedNoAccess', { access: host.access })} {host.accessMessage}</div>;
}

function Command({ text }: { text: string }) {
  return (
    <div className="command-block flex items-start gap-2">
      <pre className="command min-w-0 flex-1">{text}</pre>
      <CopyButton text={text} />
    </div>
  );
}

function formatMmSs(seconds: number) {
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  return `${m}:${String(s).padStart(2, '0')}`;
}
