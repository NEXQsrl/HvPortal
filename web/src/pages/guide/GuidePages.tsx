import { ArrowRight, BookOpen, CircleCheck, KeyRound, LifeBuoy, PlugZap, RefreshCw, Server, Settings, Unplug, UserRound } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link, NavLink, Outlet, useLocation } from 'react-router-dom';
import { get } from '@/api/client';
import type { GatewayConnection, Host } from '@/api/types';
import { AgentSetupGuide } from '@/components/AgentSetupGuide';
import { Empty, ErrorAlert, HostIcon, Loading, Notice, PageHeader, Section, StatusBadge } from '@/components/common';
import { AGENT_ZIP, Bullets, Command, PackageDownload, Step, Steps, TROUBLE_CONNECT, TROUBLE_CONSOLE, TROUBLE_OPS, TROUBLE_UPDATE, TroubleshootingTable, useAgentPackage } from '@/components/guide/GuideKit';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { useI18n, type TKey } from '@/i18n';
import { compareVersions, semver, shortVersion } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';

interface GuidePage { to: string; title: TKey; desc?: TKey; icon: typeof BookOpen; end?: boolean }

export const GUIDE_PAGES: GuidePage[] = [
  { to: '/guide', title: 'guide.home.title', icon: BookOpen, end: true },
  { to: '/guide/connect', title: 'guide.page.connect.title', desc: 'guide.page.connect.desc', icon: PlugZap },
  { to: '/guide/update', title: 'guide.page.update.title', desc: 'guide.page.update.desc', icon: RefreshCw },
  { to: '/guide/remove', title: 'guide.page.remove.title', desc: 'guide.page.remove.desc', icon: Unplug },
  { to: '/guide/entra', title: 'guide.page.entra.title', desc: 'guide.page.entra.desc', icon: KeyRound },
  { to: '/guide/troubleshooting', title: 'guide.trouble.title', desc: 'guide.page.trouble.desc', icon: LifeBuoy },
];

/** Hosts whose agent is older than the package published by this gateway. */
function useOutdated() {
  const inv = useInventory();
  const pkg = useAgentPackage();
  const target = semver(pkg.data?.agent?.version);
  const outdated = target ? inv.hosts.filter((h) => { const v = semver(h.agentVersion); return v !== null && compareVersions(v, target) < 0; }) : [];
  return { target, outdated, pkg, hosts: inv.hosts };
}

export function GuideLayout() {
  const { t } = useI18n();
  const { can } = useSession();
  const location = useLocation();
  const current = GUIDE_PAGES.find((p) => (p.end ? location.pathname === p.to : location.pathname.startsWith(p.to))) ?? GUIDE_PAGES[0]!;
  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<current.icon className="size-5" />} title={t(current.title)} subtitle={t(current.desc ?? 'guide.home.intro')} />
      <div className="flex min-h-0 flex-1 flex-col lg:flex-row">
        <nav aria-label={t('guide.pages')} className="shrink-0 border-b bg-panel lg:w-64 lg:border-r lg:border-b-0">
          <ul className="flex gap-1 overflow-x-auto p-2 lg:flex-col">
            {GUIDE_PAGES.map((p) => (
              <li key={p.to} className="shrink-0">
                <NavLink to={p.to} end={p.end}
                  className={({ isActive }) => cn('flex h-8 items-center gap-2 rounded-md px-2.5 text-sm whitespace-nowrap hover:bg-accent',
                    isActive ? 'bg-accent font-medium text-accent-foreground' : 'text-muted-foreground')}>
                  <p.icon className="size-4 shrink-0" />{t(p.title)}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
        <div className="min-h-0 flex-1 overflow-auto p-4">
          <div className="max-w-5xl space-y-4">
            {can('hosts.manage') ? <Outlet /> : <Notice kind="info">{t('guide.restricted')}</Notice>}
          </div>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- overview

export function GuideHome() {
  const { t } = useI18n();
  const { outdated, target } = useOutdated();
  return (
    <>
      <div className="grid gap-3 md:grid-cols-2">
        {GUIDE_PAGES.filter((p) => !p.end).map((p) => (
          <Link key={p.to} to={p.to} className="group flex gap-3 rounded-lg border bg-card p-4 transition-colors hover:border-primary/40 hover:bg-accent/30">
            <span className="flex size-9 shrink-0 items-center justify-center rounded-lg border bg-background text-primary"><p.icon className="size-4" /></span>
            <span className="min-w-0 flex-1">
              <span className="flex items-center gap-2 text-sm font-semibold">{t(p.title)}<ArrowRight className="size-3.5 opacity-0 transition-opacity group-hover:opacity-100" /></span>
              <span className="mt-0.5 block text-sm text-muted-foreground">{p.desc && t(p.desc)}</span>
              {p.to === '/guide/update' && target && (
                <span className="mt-2 block">
                  {outdated.length > 0
                    ? <StatusBadge tone="warn">{t('guide.home.outdated', { n: outdated.length })}</StatusBadge>
                    : <StatusBadge tone="ok"><CircleCheck />{t('guide.home.allUpdated')}</StatusBadge>}
                </span>
              )}
            </span>
          </Link>
        ))}
      </div>
    </>
  );
}

// ---------------------------------------------------------------- connect

function useConnection() {
  return useLoad(() => get<GatewayConnection>('/api/v1/hosts/connection-info'));
}

export function GuideConnect() {
  const { t } = useI18n();
  const info = useConnection();
  return (
    <>
      <div className="flex flex-wrap items-start gap-3">
        <p className="min-w-0 flex-1 text-sm text-muted-foreground">{t('guide.intro')}</p>
        <Button size="sm" variant="outline" asChild><Link to="/hosts"><Server />{t('guide.goHosts')}</Link></Button>
      </div>
      {info.loading && !info.data ? <Loading /> : info.data
        ? <Section bodyClassName="p-5"><AgentSetupGuide connection={info.data} troubleshooting="link" /></Section>
        : <ErrorAlert error={info.error} />}
    </>
  );
}

// ---------------------------------------------------------------- update

function VersionStatus({ host, target }: { host: Host; target: [number, number, number] | null }) {
  const { t } = useI18n();
  const v = semver(host.agentVersion);
  if (!host.connected) return <StatusBadge tone="muted">{t('guide.update.notConnected')}</StatusBadge>;
  if (!v || !target) return <StatusBadge tone="muted">{t('guide.update.unknown')}</StatusBadge>;
  const c = compareVersions(v, target);
  return c < 0 ? <StatusBadge tone="warn">{t('guide.update.outdated')}</StatusBadge>
    : c > 0 ? <StatusBadge tone="info">{t('guide.update.newer')}</StatusBadge>
    : <StatusBadge tone="ok"><CircleCheck />{t('guide.update.upToDate')}</StatusBadge>;
}

export function GuideUpdate() {
  const { t } = useI18n();
  const { target, pkg, hosts } = useOutdated();
  const info = useLoad(() => get<{ version: string; runningAsService?: boolean }>('/api/v1/system/info'));
  const conn = useConnection();
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
  const prevComment = t('guide.update.a6.folder');

  return (
    <>
      <p className="text-sm text-muted-foreground">{t('guide.update.intro')}</p>
      <Notice kind="info">{t('guide.update.order')}</Notice>

      <Section title={t('guide.update.status')} actions={
        <span className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
          {info.data && <span>{t('guide.update.gateway', { v: info.data.version })}</span>}
          {target && <span>· {t('guide.update.package', { v: pkgVersion })}</span>}
        </span>
      }>
        {hosts.length === 0 ? <Empty>{t('guide.update.noHosts')}</Empty> : (
          <Table>
            <TableHeader><TableRow><TableHead>{t('host.name')}</TableHead><TableHead>{t('guide.update.installed')}</TableHead><TableHead>{t('common.status')}</TableHead></TableRow></TableHeader>
            <TableBody>
              {hosts.map((h) => (
                <TableRow key={h.id}>
                  <TableCell><Link to={`/hosts/${h.id}`} className="flex items-center gap-2 font-medium hover:underline"><HostIcon connected={h.connected} access={h.access} />{h.displayName}</Link></TableCell>
                  <TableCell className="font-mono text-xs">{shortVersion(h.agentVersion)}</TableCell>
                  <TableCell><VersionStatus host={h} target={target} /></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        {!pkg.loading && !pkg.data?.agent && <div className="p-3"><Notice kind="warn">{t('guide.s2.missing')}</Notice></div>}
      </Section>

      <Section title={t('guide.update.agent.title')} bodyClassName="p-5">
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
          </Step>
          <Step n={5} id="agent-5" title={t('guide.update.a5.title')}>
            <Command text={`Get-Service HvPortalAgent\n& 'C:\\Program Files\\HvPortal\\Agent\\HvPortal.Agent.exe' status`} />
            <p className="text-muted-foreground">{t('guide.update.a5.help')}</p>
          </Step>
          <Step n={6} id="agent-6" title={t('guide.update.a6.title')}>
            <p className="text-muted-foreground">{t('guide.update.a6.help')}</p>
            <Command text={`cd C:\\HvPortal\\agent-package\\installer   # ${prevComment}\n.\\Install-Agent.ps1 -Source ..\\agent`} />
          </Step>
        </Steps>
      </Section>

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
            <Command text={`cd <release>\\installer\n.\\Install-Gateway.ps1 ${installArgs}`} />
            <p className="text-muted-foreground">{t('guide.update.g3.what')}</p>
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

// ---------------------------------------------------------------- remove

export function GuideRemove() {
  const { t } = useI18n();
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('guide.remove.intro')}</p>
      <Section bodyClassName="p-5">
        <Steps>
          <Step n={1} id="rm-1" title={t('guide.remove.r1.title')}>
            <p className="text-muted-foreground">{t('guide.remove.r1.help')}</p>
            <p className="text-muted-foreground">{t('guide.remove.r1.revoke')}</p>
            <Button size="sm" variant="outline" asChild><Link to="/hosts"><Server />{t('guide.goHosts')}</Link></Button>
          </Step>
          <Step n={2} id="rm-2" title={t('guide.remove.r2.title')}>
            <p className="text-muted-foreground">{t('guide.remove.r2.help')}</p>
            <Command text={`cd C:\\HvPortal\\agent-package\\installer\n.\\Uninstall-HvPortal.ps1 -Component Agent`} />
            <p className="text-muted-foreground">{t('guide.remove.r2.what')}</p>
          </Step>
          <Step n={3} id="rm-3" title={t('guide.remove.r3.title')}>
            <p className="text-muted-foreground">{t('guide.remove.r3.help')}</p>
            <Button size="sm" variant="outline" asChild><Link to="/guide/connect"><PlugZap />{t('guide.page.connect.title')}</Link></Button>
          </Step>
        </Steps>
      </Section>
    </>
  );
}

// ---------------------------------------------------------------- Microsoft Entra ID

const TROUBLE_ENTRA = ['enRedirect', 'enSecret', 'enAssign', 'enConsent', 'enProvision', 'enTenant', 'enReach'] as const;

export function GuideEntra() {
  const { t } = useI18n();
  const { can } = useSession();
  // Same formula as the gateway (https://<host as typed in the browser>/api/v1/auth/entra/callback).
  const redirect = `https://${window.location.host}/api/v1/auth/entra/callback`;
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('guide.entra.intro')}</p>
      <Section bodyClassName="p-5">
        <Steps>
          <Step n={1} id="en-1" title={t('guide.entra.e1.title')}>
            <Bullets items={[t('guide.entra.e1.b1'), t('guide.entra.e1.b2'), t('guide.entra.e1.b3')]} />
          </Step>
          <Step n={2} id="en-2" title={t('guide.entra.e2.title')}>
            <p className="text-muted-foreground">{t('guide.entra.e2.help')}</p>
            <Bullets items={[t('guide.entra.e2.b1'), t('guide.entra.e2.b2'), t('guide.entra.e2.b3')]} />
            <Command text={redirect} />
            <p className="text-muted-foreground">{t('guide.entra.e2.redirectNote')}</p>
          </Step>
          <Step n={3} id="en-3" title={t('guide.entra.e3.title')}>
            <p className="text-muted-foreground">{t('guide.entra.e3.help')}</p>
            <Notice kind="warn">{t('guide.entra.e3.expiry')}</Notice>
          </Step>
          <Step n={4} id="en-4" title={t('guide.entra.e4.title')}>
            <p className="text-muted-foreground">{t('guide.entra.e4.help')}</p>
          </Step>
          <Step n={5} id="en-5" title={t('guide.entra.e5.title')}>
            <Bullets items={[t('guide.entra.e5.b1'), t('guide.entra.e5.b2'), t('guide.entra.e5.b3')]} />
          </Step>
          <Step n={6} id="en-6" title={t('guide.entra.e6.title')}>
            <p className="text-muted-foreground">{t('guide.entra.e6.help')}</p>
            {can('settings.manage') && <Button size="sm" variant="outline" asChild><Link to="/settings?tab=entra"><Settings />{t('guide.entra.e6.go')}</Link></Button>}
          </Step>
          <Step n={7} id="en-7" title={t('guide.entra.e7.title')}>
            <p className="text-muted-foreground">{t('guide.entra.e7.help')}</p>
            {can('users.manage') && <Button size="sm" variant="outline" asChild><Link to="/users"><UserRound />{t('guide.entra.e7.go')}</Link></Button>}
          </Step>
          <Step n={8} id="en-8" title={t('guide.entra.e8.title')}>
            <p className="text-muted-foreground">{t('guide.entra.e8.help')}</p>
            <Notice kind="info">{t('guide.entra.e8.breakGlass')}</Notice>
          </Step>
        </Steps>
      </Section>
      <Section title={t('guide.trouble.entra')}><TroubleshootingTable keys={TROUBLE_ENTRA} port="" /></Section>
    </>
  );
}

// ---------------------------------------------------------------- troubleshooting

export function GuideTroubleshooting() {
  const { t } = useI18n();
  const conn = useConnection();
  const port = conn.data?.agentPort ?? 8444;
  const block = (title: TKey, keys: readonly string[]): ReactNode => (
    <Section title={t(title)}><TroubleshootingTable keys={keys} port={port} /></Section>
  );
  return (
    <>
      <Notice kind="info">{t('guide.trouble.logs')}</Notice>
      {block('guide.trouble.connect', TROUBLE_CONNECT)}
      {block('guide.trouble.update', TROUBLE_UPDATE)}
      {block('guide.trouble.ops', TROUBLE_OPS)}
      {block('guide.trouble.console', TROUBLE_CONSOLE)}
    </>
  );
}
