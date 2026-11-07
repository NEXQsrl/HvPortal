import { ArrowDown, MonitorDown, RefreshCw, Terminal, type LucideIcon } from 'lucide-react';
import type { ReactNode } from 'react';
import { get } from '@/api/client';
import type { GatewayConnection } from '@/api/types';
import { Notice, Section } from '@/components/common';
import { AGENT_ZIP, Bullets, Command, PackageDownload, SetupDownload, Step, Steps, useAgentPackage } from '@/components/guide/GuideKit';
import { AgentUpdatesPanel } from '@/components/infra/AgentUpdates';
import { useI18n, type TKey } from '@/i18n';
import { semver } from '@/lib/format';
import { useLoad } from '@/lib/hooks';

const METHODS: { id: string; icon: LucideIcon; title: TKey; when: TKey; badge?: TKey }[] = [
  { id: 'update-portal', icon: RefreshCw, title: 'upd.m.portal', when: 'upd.m.portalWhen', badge: 'upd.m.recommended' },
  { id: 'update-setup', icon: MonitorDown, title: 'upd.m.setup', when: 'upd.m.setupWhen' },
  { id: 'update-zip', icon: Terminal, title: 'upd.m.zip', when: 'upd.m.zipWhen' },
];

function jump(id: string) {
  document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

function Method({ id, title, children }: { id: string; title: ReactNode; children: ReactNode }) {
  return <section id={id} className="scroll-mt-4"><Section title={title} bodyClassName="space-y-4 p-5">{children}</Section></section>;
}

/**
 * Guide > Update: the state of every agent with "Update" / "Update all" from the portal, then the three ways to
 * update an agent (from the portal, graphical setup, zip + PowerShell) and the gateway procedure.
 */
export function GuideUpdate() {
  const { t } = useI18n();
  const pkg = useAgentPackage();
  const target = semver(pkg.data?.agent?.version);
  const info = useLoad(() => get<{ version: string; runningAsService?: boolean }>('/api/v1/system/info'));
  const conn = useLoad(() => get<GatewayConnection>('/api/v1/hosts/connection-info'));
  const pkgVersion = target?.join('.') ?? '<version>';
  const folder = `C:\\HvPortal\\agent-package-${pkgVersion}`;
  const uiPort = window.location.port || '443';
  const names = conn.data?.gatewayNames.join(',') ?? '<names>';
  // Only non-default values are spelled out (defaults: bind 0.0.0.0, UI 8443, agents 8444).
  const installArgs = [
    '-Source ..\\gateway', `-PublicNames ${names}`,
    conn.data && conn.data.bindAddress !== '0.0.0.0' ? `-BindAddress ${conn.data.bindAddress}` : '',
    window.location.port && window.location.port !== '8443' ? `-UiPort ${window.location.port}` : '',
    conn.data && conn.data.agentPort !== 8444 ? `-AgentPort ${conn.data.agentPort}` : '',
  ].filter(Boolean).join(' ');

  return (
    <>
      <p className="text-sm text-muted-foreground">{t('guide.update.intro')}</p>
      <Notice kind="info">{t('guide.update.order')}</Notice>

      <AgentUpdatesPanel />

      <div>
        <h2 className="mb-2 text-sm font-semibold">{t('upd.m.title')}</h2>
        <div className="grid gap-3 md:grid-cols-3">
          {METHODS.map((m) => (
            <button key={m.id} type="button" onClick={() => jump(m.id)}
              className="group flex flex-col gap-2 rounded-lg border bg-card p-4 text-left transition-colors hover:border-primary/40 hover:bg-accent/30">
              <span className="flex items-center gap-2">
                <span className="flex size-8 items-center justify-center rounded-lg border bg-background text-primary"><m.icon className="size-4" /></span>
                <span className="text-sm font-semibold">{t(m.title)}</span>
                {m.badge && <span className="ml-auto rounded-full bg-primary/10 px-2 py-0.5 text-[11px] font-medium text-primary">{t(m.badge)}</span>}
              </span>
              <span className="text-sm text-muted-foreground">{t(m.when)}</span>
              <span className="mt-auto flex items-center gap-1 text-xs font-medium text-primary">{t('upd.m.go')}<ArrowDown className="size-3.5 transition-transform group-hover:translate-y-0.5" /></span>
            </button>
          ))}
        </div>
      </div>

      <Method id="update-portal" title={t('upd.m.portal')}>
        <p className="text-sm text-muted-foreground">{t('upd.p.intro')}</p>
        <Steps>
          <Step n={1} id="up-1" title={t('upd.p.s1')}><p className="text-muted-foreground">{t('upd.p.s1h')}</p></Step>
          <Step n={2} id="up-2" title={t('upd.p.s2')}><p className="text-muted-foreground">{t('upd.p.s2h')}</p></Step>
          <Step n={3} id="up-3" title={t('upd.p.s3')}><p className="text-muted-foreground">{t('upd.p.s3h')}</p></Step>
          <Step n={4} id="up-4" title={t('upd.p.s4')}><p className="text-muted-foreground">{t('upd.p.s4h')}</p></Step>
        </Steps>
        <Notice kind="info" title={t('upd.p.reqTitle')}>
          <ul className="list-disc space-y-1 pl-4">
            <li>{t('upd.p.req1')}</li>
            <li>{t('upd.p.req2')}</li>
            <li>{t('upd.p.req3')}</li>
          </ul>
        </Notice>
        <p className="text-xs text-muted-foreground">{t('upd.p.signNote')}</p>
        <Command text={`.\\installer\\Build-Release.ps1 -SigningCertificateThumbprint <thumbprint>`} />
      </Method>

      <Method id="update-setup" title={t('upd.m.setup')}>
        <Steps>
          <Step n={1} id="us-1" title={t('upd.s.s1')}><SetupDownload /></Step>
          <Step n={2} id="us-2" title={t('upd.s.s2')}><p className="text-muted-foreground">{t('upd.s.s2h')}</p></Step>
          <Step n={3} id="us-3" title={t('upd.s.s3')}>
            <Bullets items={[t('upd.s.s3a'), t('upd.s.s3b'), t('upd.s.s3c')]} />
          </Step>
          <Step n={4} id="us-4" title={t('guide.update.a5.title')}>
            <p className="text-muted-foreground">{t('upd.s.s4h')}</p>
          </Step>
        </Steps>
      </Method>

      <Method id="update-zip" title={t('upd.m.zip')}>
        <Steps>
          <Step n={1} id="agent-1" title={t('guide.update.a1.title')}>
            <Bullets items={[t('guide.update.a1.tasks'), t('guide.update.a1.vms'), t('guide.update.a1.identity'), t('guide.update.a1.module')]} />
          </Step>
          <Step n={2} id="agent-2" title={t('guide.update.a2.title')}>
            <PackageDownload help={t('guide.update.a2.help')} />
          </Step>
          <Step n={3} id="agent-3" title={t('guide.update.a3.title')}>
            <p className="text-muted-foreground">{t('guide.update.a3.help')}</p>
            <Command text={`Get-FileHash .\\${AGENT_ZIP} -Algorithm SHA256\nUnblock-File .\\${AGENT_ZIP}\nExpand-Archive .\\${AGENT_ZIP} -DestinationPath ${folder} -Force`} />
          </Step>
          <Step n={4} id="agent-4" title={t('guide.update.a4.title')}>
            <Command text={`cd ${folder}\\installer\n.\\Install-Agent.ps1 -Source ..\\agent`} />
            <p className="text-muted-foreground">{t('guide.update.a4.noEnroll')}</p>
            <p className="text-muted-foreground">{t('guide.update.a4.what')}</p>
            <p className="text-muted-foreground">{t('upd.z.options')}</p>
          </Step>
          <Step n={5} id="agent-5" title={t('guide.update.a5.title')}>
            <Command text={`Get-Service HvPortalAgent\n& 'C:\\Program Files\\HvPortal\\Agent\\HvPortal.Agent.exe' status`} />
            <p className="text-muted-foreground">{t('guide.update.a5.help')}</p>
          </Step>
          <Step n={6} id="agent-6" title={t('guide.update.a6.title')}>
            <p className="text-muted-foreground">{t('guide.update.a6.help')}</p>
            <Command text={`cd C:\\HvPortal\\agent-package\\installer   # ${t('guide.update.a6.folder')}\n.\\Install-Agent.ps1 -Source ..\\agent`} />
          </Step>
        </Steps>
      </Method>

      <Section title={t('guide.update.gw.title')} bodyClassName="space-y-4 p-5">
        {info.data && info.data.runningAsService === false && <Notice kind="warn">{t('guide.update.gw.dev')}</Notice>}
        {info.data?.runningAsService && <Notice kind="info">{t('guide.update.gw.service')}</Notice>}
        <Steps>
          <Step n={1} id="gw-1" title={t('guide.update.g1.title')}>
            <Bullets items={[t('guide.update.g1.downtime'), t('guide.update.g1.keys')]} />
            <Command text={`& 'C:\\Program Files\\HvPortal\\Gateway\\HvPortal.Gateway.exe' admin export-keys --out D:\\secure\\hvportal-keys.hvpx`} />
            <p className="text-muted-foreground">{t('guide.update.g1.params')}</p>
            <Command text={`(Get-ItemProperty HKLM:\\SYSTEM\\CurrentControlSet\\Services\\HvPortalGateway).Environment`} />
          </Step>
          <Step n={2} id="gw-2" title={t('guide.update.g2.title')}>
            <p className="text-muted-foreground">{t('guide.update.g2.help')}</p>
            <Command text={`.\\installer\\Build-Release.ps1`} />
          </Step>
          <Step n={3} id="gw-3" title={t('guide.update.g3.title')}>
            <p className="text-muted-foreground">{t('upd.g.setup')}</p>
            <Command text={`cd <release>\\installer\n.\\Install-Gateway.ps1 ${installArgs}`} />
            <p className="text-muted-foreground">{t('guide.update.g3.what')}</p>
            <p className="text-muted-foreground">{t('upd.g.locked')}</p>
          </Step>
          <Step n={4} id="gw-4" title={t('guide.update.g4.title')}>
            <Command text={`Get-Service HvPortalGateway\nInvoke-WebRequest https://localhost:${uiPort}/healthz -UseBasicParsing`} />
            <p className="text-muted-foreground">{t('guide.update.g4.help')}</p>
          </Step>
          <Step n={5} id="gw-5" title={t('guide.update.g5.title')}>
            <p className="text-muted-foreground">{t('guide.update.g5.help')}</p>
            <Command text={[
              'Stop-Service HvPortalGateway',
              'cd C:\\ProgramData\\HvPortal\\Gateway\\db',
              'Remove-Item .\\hvportal.db-wal, .\\hvportal.db-shm -ErrorAction SilentlyContinue',
              'Copy-Item .\\pre-upgrade-<date>\\* . -Force',
              'cd <previous release>\\installer',
              `.\\Install-Gateway.ps1 ${installArgs}`,
            ].join('\n')} />
          </Step>
        </Steps>
      </Section>
    </>
  );
}
