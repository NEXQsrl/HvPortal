import { useEffect, useState } from 'react';
import { get } from '../api/client';
import type { GatewayConnection, Host, PackageInfo } from '../api/types';
import { useI18n, type TKey } from '../i18n';
import { CopyButton, formatBytes, useLoad } from './ui';

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
  const remaining = useCountdown(expiresAt);
  const expired = expiresAt !== undefined && remaining <= 0;
  const url = `https://${name}:${connection.agentPort}`;
  const zip = 'HvPortal.Agent-win-x64.zip';
  const folder = 'C:\\HvPortal\\agent-package';
  const tokenValue = token ?? '<token>';

  const step = (n: number, titleKey: TKey, body: React.ReactNode) => (
    <section className="guide-step" aria-labelledby={`step-${n}`}>
      <h3 id={`step-${n}`}><span className="step-no">{n}</span>{t(titleKey)}</h3>
      {body}
    </section>
  );

  return (
    <div className="guide">
      {!connection.listensOnNetwork && <div className="alert danger">{t('guide.loopbackOnly', { bind: connection.bindAddress })}</div>}

      {step(1, 'guide.s1.title', (
        <>
          <ul className="small">
            <li>{t('guide.s1.admin')}</li>
            <li>{t('guide.s1.network', { port: connection.agentPort })}</li>
            <li>{t('guide.s1.hyperv')}</li>
            {token && <li>{t('guide.s1.token')}</li>}
          </ul>
        </>
      ))}

      {step(2, 'guide.s2.title', pkg.data?.agent ? (
        <>
          <p className="small">{t('guide.s2.help')}</p>
          <div className="row">
            <a className="button primary" href="/api/v1/downloads/agent" download={zip}>⬇ {t('guide.s2.download')}</a>
            <span className="muted small">{pkg.data.agent.version ?? ''} · {formatBytes(pkg.data.agent.sizeBytes)}</span>
            {pkg.data.agent.signed
              ? <span className="badge ok">{t('guide.s2.signed')}</span>
              : <span className="badge warn">{t('guide.s2.unsigned')}</span>}
          </div>
          {!pkg.data.agent.signed && <p className="small muted">{t('guide.s2.unsignedHelp')}</p>}
          <label className="small" style={{ marginTop: 8 }}>SHA-256</label>
          <div className="row"><div className="secret pkg-hash" style={{ flex: 1 }}>{pkg.data.agent.sha256}</div><CopyButton text={pkg.data.agent.sha256} /></div>
        </>
      ) : pkg.loading ? <p className="muted small">{t('common.loading')}</p>
        : <div className="alert warn small">{t('guide.s2.missing')}</div>)}

      {step(3, 'guide.s3.title', (
        <>
          <p className="small">{t('guide.s3.help')}</p>
          <Command text={`Get-FileHash .\\${zip} -Algorithm SHA256`} />
          <p className="small">{t('guide.s3.compare')}</p>
          <Command text={`Unblock-File .\\${zip}\nExpand-Archive .\\${zip} -DestinationPath ${folder} -Force`} />
          <p className="small muted">{t('guide.s3.unblock')}</p>
        </>
      ))}

      {step(4, 'guide.s4.title', (
        <>
          <p className="small">{t('guide.s4.help')}</p>
          <div className="field">
            <label htmlFor="gw-name">{t('guide.s4.name')}</label>
            <select id="gw-name" value={name} onChange={(e) => setName(e.target.value)}>
              {connection.gatewayNames.map((n) => <option key={n} value={n}>{n}</option>)}
            </select>
            <div className="hint">{t('guide.s4.nameHelp')}</div>
          </div>
          <Command text={`Test-NetConnection ${name} -Port ${connection.agentPort}`} />
          <p className="small">{t('guide.s4.expect')}</p>
        </>
      ))}

      {step(5, 'guide.s5.title', (
        <>
          {token ? (
            expired
              ? <div className="alert danger">{t('guide.s5.expired')}</div>
              : <div className={'alert ' + (remaining < 180 ? 'warn' : 'info')}>{t('guide.s5.countdown', { time: formatMmSs(remaining), at: fmtDate(expiresAt) })}</div>
          ) : <div className="alert info small">{t('guide.s5.noToken')}</div>}
          <Command text={`cd ${folder}\\installer\n$env:HVP_ENROLLMENT_TOKEN = '${tokenValue}'\n.\\Install-Agent.ps1 -Source ..\\agent -Gateway ${url} -CaSha256 ${connection.caFingerprintSha256}`} />
          <p className="small">{t('guide.s5.what')}</p>
          <label className="small">{t('host.fingerprint')}</label>
          <div className="secret small">{connection.caFingerprintSha256}</div>
          <p className="small muted">{t('guide.s5.fingerprint')}</p>
        </>
      ))}

      {step(6, 'guide.s6.title', (
        <>
          {host !== undefined && <HostStatus host={host} />}
          <p className="small">{t('guide.s6.help')}</p>
          <Command text={`& 'C:\\Program Files\\HvPortal\\Agent\\HvPortal.Agent.exe' inventory`} />
          <p className="small">{t('guide.s6.expect')}</p>
        </>
      ))}

      <details className="guide-trouble">
        <summary><strong>{t('guide.trouble.title')}</strong></summary>
        <table className="small">
          <thead><tr><th>{t('guide.trouble.symptom')}</th><th>{t('guide.trouble.fix')}</th></tr></thead>
          <tbody>
            {TROUBLE.map((k) => (
              <tr key={k}><td className="mono">{t(`guide.trouble.${k}.s` as TKey)}</td><td>{t(`guide.trouble.${k}.f` as TKey, { port: connection.agentPort })}</td></tr>
            ))}
          </tbody>
        </table>
      </details>
    </div>
  );
}

const TROUBLE = ['tcp', 'denied', 'scripts', 'rejected', 'pin', 'name', 'access', 'offline'] as const;

function HostStatus({ host }: { host: Host | null }) {
  const { t } = useI18n();
  if (!host || !host.connected) return <div className="alert info" role="status">⏳ {t('guide.s6.waiting')}</div>;
  if (host.access === 'Ok') return <div className="alert ok" role="status">✓ {t('guide.s6.ok', { n: host.vmCount })}</div>;
  if (!host.access) return <div className="alert info" role="status">⏳ {t('guide.s6.connected')}</div>;
  return <div className="alert warn" role="status">{t('guide.s6.connectedNoAccess', { access: host.access })} {host.accessMessage}</div>;
}

function Command({ text }: { text: string }) {
  return (
    <div className="command-block">
      <pre className="command">{text}</pre>
      <CopyButton text={text} />
    </div>
  );
}

function useCountdown(iso?: string) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!iso) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [iso]);
  return iso ? Math.max(0, Math.floor((new Date(iso).getTime() - now) / 1000)) : 0;
}

function formatMmSs(seconds: number) {
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  return `${m}:${String(s).padStart(2, '0')}`;
}
