import { ArrowRight, BookOpen, CircleCheck, KeyRound, LifeBuoy, PlugZap, RefreshCw, Server, Settings, Unplug, UserRound, Users, Wrench, type LucideIcon } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link, Outlet, useLocation, useSearchParams } from 'react-router-dom';
import { get } from '@/api/client';
import type { GatewayConnection } from '@/api/types';
import { AgentSetupGuide } from '@/components/AgentSetupGuide';
import { ErrorAlert, Loading, Notice, PageHeader, Section, StatusBadge } from '@/components/common';
import { Bullets, Command, Step, Steps, TROUBLE_CONNECT, TROUBLE_CONSOLE, TROUBLE_OPS, TROUBLE_UPDATE, TroubleshootingTable, useAgentPackage } from '@/components/guide/GuideKit';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { Button } from '@/components/ui/button';
import { useI18n, type TKey } from '@/i18n';
import { compareVersions, semver } from '@/lib/format';
import { useLoad } from '@/lib/hooks';
import { useSession } from '@/session';
import { TOPIC_GUIDES, topicPath } from './TopicGuides';

/**
 * "user": the topic guides (TopicGuides.tsx), open to every signed-in user. "admin": installation and maintenance
 * procedures, only for holders of hosts.manage (the data they show is authorized by the server anyway).
 */
export type GuideSection = 'home' | 'user' | 'admin';

export interface GuidePage { to: string; title: TKey; desc?: TKey; icon: LucideIcon; end?: boolean; section: GuideSection }

export const GUIDE_PAGES: GuidePage[] = [
  { to: '/guide', title: 'guide.home.title', desc: 'topic.home.desc', icon: BookOpen, end: true, section: 'home' },
  ...TOPIC_GUIDES.map((g): GuidePage => ({ to: topicPath(g.slug), title: g.title, desc: g.desc, icon: g.icon, section: 'user' })),
  { to: '/guide/connect', title: 'guide.page.connect.title', desc: 'guide.page.connect.desc', icon: PlugZap, section: 'admin' },
  { to: '/guide/update', title: 'guide.page.update.title', desc: 'guide.page.update.desc', icon: RefreshCw, section: 'admin' },
  { to: '/guide/remove', title: 'guide.page.remove.title', desc: 'guide.page.remove.desc', icon: Unplug, section: 'admin' },
  { to: '/guide/entra', title: 'guide.page.entra.title', desc: 'guide.page.entra.desc', icon: KeyRound, section: 'admin' },
  { to: '/guide/troubleshooting', title: 'guide.trouble.title', desc: 'guide.page.trouble.desc', icon: LifeBuoy, section: 'admin' },
];

export const GUIDE_SECTIONS: { key: Exclude<GuideSection, 'home'>; title: TKey; icon: LucideIcon }[] = [
  { key: 'user', title: 'topic.section', icon: Users },
  { key: 'admin', title: 'topic.sectionAdmin', icon: Wrench },
];

/** Pages of the Guide the user may open: home and topic guides for everyone, administration with hosts.manage. */
export function visibleGuidePages(can: (permission: string) => boolean): GuidePage[] {
  return GUIDE_PAGES.filter((p) => p.section !== 'admin' || can('hosts.manage'));
}

/** Nodes of the navigator tree of the Guide: the home, then every section the user may open with its pages. */
export function guideTree(can: (permission: string) => boolean): { key: string; title: TKey; icon: LucideIcon; to: string; depth: number }[] {
  const pages = visibleGuidePages(can);
  const nodes = pages.filter((p) => p.section === 'home').map((p) => ({ key: p.to, title: p.title, icon: p.icon, to: p.to, depth: 0 }));
  for (const s of GUIDE_SECTIONS) {
    const list = pages.filter((p) => p.section === s.key);
    if (list.length === 0) continue;
    nodes.push({ key: `section:${s.key}`, title: s.title, icon: s.icon, to: `/guide?section=${s.key}`, depth: 0 });
    for (const p of list) nodes.push({ key: p.to, title: p.title, icon: p.icon, to: p.to, depth: 1 });
  }
  return nodes;
}

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
  const path = location.pathname;
  const current = GUIDE_PAGES.find((p) => (p.end ? path === p.to : path === p.to || path.startsWith(p.to + '/'))) ?? GUIDE_PAGES[0]!;
  // Topic guides are for everyone; the administration pages stay with hosts.manage, as their data on the server.
  const allowed = current.section !== 'admin' || can('hosts.manage');
  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<current.icon className="size-5" />} title={t(current.title)} subtitle={t(current.desc ?? 'topic.home.desc')} />
      <div className="flex min-h-0 flex-1 flex-col lg:flex-row">
        <div className="min-h-0 flex-1 overflow-auto p-4">
          <div className="max-w-5xl space-y-4">
            {allowed ? <Outlet /> : (
              <Notice kind="info">
                {t('topic.adminRestricted')} <Link to="/guide" className="font-medium text-primary hover:underline">{t('topic.allGuides')}</Link>
              </Notice>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- overview

function GuideCard({ page, children }: { page: GuidePage; children?: ReactNode }) {
  const { t } = useI18n();
  return (
    <Link to={page.to} className="group flex gap-3 rounded-lg border bg-card p-4 transition-colors hover:border-primary/40 hover:bg-accent/30">
      <span className="flex size-9 shrink-0 items-center justify-center rounded-lg border bg-background text-primary"><page.icon className="size-4" /></span>
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-2 text-sm font-semibold">{t(page.title)}<ArrowRight className="size-3.5 opacity-0 transition-opacity group-hover:opacity-100" /></span>
        <span className="mt-0.5 block text-sm text-muted-foreground">{page.desc && t(page.desc)}</span>
        {children}
      </span>
    </Link>
  );
}

/**
 * Cards of the administration pages, with the agent update status. Rendered only for hosts.manage: the package
 * status comes from an endpoint that needs it (calling it without would only record a denial).
 */
function AdminGuideCards() {
  const { t } = useI18n();
  const { outdated, target } = useOutdated();
  return (
    <div className="grid gap-3 md:grid-cols-2">
      {GUIDE_PAGES.filter((p) => p.section === 'admin').map((p) => (
        <GuideCard key={p.to} page={p}>
          {p.to === '/guide/update' && target && (
            <span className="mt-2 block">
              {outdated.length > 0
                ? <StatusBadge tone="warn">{t('guide.home.outdated', { n: outdated.length })}</StatusBadge>
                : <StatusBadge tone="ok"><CircleCheck />{t('guide.home.allUpdated')}</StatusBadge>}
            </span>
          )}
        </GuideCard>
      ))}
    </div>
  );
}

/** Home of the Guide: topic guides for everyone, then the administration pages. ?section=user|admin shows one only. */
export function GuideHome() {
  const { t } = useI18n();
  const { can } = useSession();
  const [params] = useSearchParams();
  const only = params.get('section');
  const admin = can('hosts.manage');
  return (
    <>
      {only !== 'admin' && (
        <section className="space-y-3" aria-labelledby="guide-user">
          <div>
            <h2 id="guide-user" className="text-sm font-semibold">{t('topic.section')}</h2>
            <p className="mt-0.5 text-sm text-muted-foreground">{t('topic.home.user')}</p>
          </div>
          <div className="grid gap-3 md:grid-cols-2">
            {GUIDE_PAGES.filter((p) => p.section === 'user').map((p) => <GuideCard key={p.to} page={p} />)}
          </div>
        </section>
      )}
      {only !== 'user' && admin && (
        <section className="space-y-3 pt-2" aria-labelledby="guide-admin">
          <div>
            <h2 id="guide-admin" className="text-sm font-semibold">{t('topic.sectionAdmin')}</h2>
            <p className="mt-0.5 text-sm text-muted-foreground">{t('guide.home.intro')}</p>
          </div>
          <AdminGuideCards />
        </section>
      )}
      {only === 'admin' && !admin && <Notice kind="info">{t('topic.adminRestricted')}</Notice>}
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

export { GuideUpdate } from './GuideUpdate';

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
