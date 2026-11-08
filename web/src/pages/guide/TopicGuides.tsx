import {
  Archive, BookOpen, Camera, FolderTree, HardDrive, Layers, LayoutDashboard, LayoutTemplate, ListChecks, Monitor, MonitorPlay, Network, Plus, Rocket,
  ScrollText, Settings, SlidersHorizontal, SquarePlus, UserRound, Users, type LucideIcon,
} from 'lucide-react';
import type { ComponentType, ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import type { JobStatus } from '@/api/types';
import { JobBadge, Notice, Section, StatusBadge } from '@/components/common';
import { Bullets, Step, Steps } from '@/components/guide/GuideKit';
import { useInventory } from '@/components/inventory/InventoryProvider';
import { useOps } from '@/components/ops/OperationsProvider';
import { Button } from '@/components/ui/button';
import { useI18n, type TKey } from '@/i18n';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';

/**
 * Guides by topic for every signed-in user: how to use each area of the portal. To add a topic, write its page
 * component below and append an entry to TOPIC_GUIDES (all its texts go in i18n-topics.ts, keys "topic.<slug>.*"):
 * the route, the Guide home, the navigator tree and the command palette pick it up from the array.
 */
export interface TopicGuide {
  /** Last segment of the address: /guide/topics/<slug>. */
  slug: string;
  title: TKey;
  desc: TKey;
  icon: LucideIcon;
  /** Body of the page, rendered inside the Guide layout (header and width come from there). */
  Page: ComponentType;
  /** Other topic guides suggested at the bottom of the page. */
  related?: string[];
}

export const TOPIC_BASE = '/guide/topics';
export const topicPath = (slug: string) => `${TOPIC_BASE}/${slug}`;

// ---------------------------------------------------------------- building blocks

/** Opening paragraph of a topic, with the portal pages it is about. */
function Lead({ children, links }: { children: ReactNode; links?: ReactNode }) {
  return (
    <div className="flex flex-wrap items-start gap-3">
      <p className="min-w-0 flex-1 basis-80 text-sm text-muted-foreground">{children}</p>
      {links && <div className="flex flex-wrap gap-2">{links}</div>}
    </div>
  );
}

function Go({ to, icon: Icon, children }: { to: string; icon: LucideIcon; children: ReactNode }) {
  return <Button size="sm" variant="outline" asChild><Link to={to}><Icon />{children}</Link></Button>;
}

function Text({ children }: { children: ReactNode }) {
  return <p className="text-muted-foreground">{children}</p>;
}

function Code({ children }: { children: ReactNode }) {
  return <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[11px] text-foreground">{children}</code>;
}

/** Permissions an action needs (names as in Users & roles → Roles). */
function Needs({ perms }: { perms: string[] }) {
  const { t } = useI18n();
  return (
    <p className="flex flex-wrap items-center gap-1 text-xs text-muted-foreground">
      {t('topic.needs')}:{perms.map((p) => <Code key={p}>{p}</Code>)}
    </p>
  );
}

/** Bullet with a name in evidence (usually the label of a portal element) and its explanation. */
function Item({ label, children }: { label: ReactNode; children: ReactNode }) {
  return <><span className="font-medium text-foreground">{label}</span> — {children}</>;
}

/** Small reference table: the first column names the thing, the others explain it. */
function Terms({ head, rows }: { head: string[]; rows: ReactNode[][] }) {
  return (
    <div className="overflow-auto">
      <table className="w-full text-sm">
        <thead><tr className="text-left text-xs text-muted-foreground">{head.map((h) => <th key={h} className="px-3 py-2 font-medium">{h}</th>)}</tr></thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i} className="border-t align-top">
              {r.map((c, j) => <td key={j} className={cn('px-3 py-2', j === 0 ? 'min-w-32 font-medium' : 'text-muted-foreground')}>{c}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Block({ title, children }: { title: ReactNode; children: ReactNode }) {
  return <Section title={title} bodyClassName="space-y-3 p-5 text-sm">{children}</Section>;
}

// ---------------------------------------------------------------- getting started

function StartGuide() {
  const { t } = useI18n();
  return (
    <>
      <Lead links={<>
        <Go to="/" icon={LayoutDashboard}>{t('nav.dashboard')}</Go>
        <Go to="/inventory" icon={Layers}>{t('nav.inventory')}</Go>
        <Go to="/account" icon={UserRound}>{t('nav.account')}</Go>
      </>}>{t('topic.start.intro')}</Lead>
      <Block title={t('topic.start.signin')}>
        <Steps>
          <Step n={1} id="start-1" title={t('topic.start.s1.title')}><Text>{t('topic.start.s1.help')}</Text></Step>
          <Step n={2} id="start-2" title={t('topic.start.s2.title')}><Text>{t('topic.start.s2.help')}</Text></Step>
          <Step n={3} id="start-3" title={t('topic.start.s3.title')}><Text>{t('topic.start.s3.help')}</Text></Step>
          <Step n={4} id="start-4" title={t('topic.start.s4.title')}>
            <Bullets items={[t('topic.start.s4.b1'), t('topic.start.s4.b2'), t('topic.start.s4.b3')]} />
          </Step>
        </Steps>
      </Block>
      <Block title={t('topic.start.layout')}>
        <Bullets items={[
          <Item key="nav" label={t('topic.start.l.nav')}>{t('topic.start.l.navText')}</Item>,
          <Item key="search" label={t('topic.start.l.search')}>{t('topic.start.l.searchText')}</Item>,
          <Item key="menu" label={t('topic.start.l.menu')}>{t('topic.start.l.menuText')}</Item>,
          <Item key="tasks" label={t('tasks.recent')}>{t('topic.start.l.tasksText')}</Item>,
          <Item key="live" label={t('topic.start.l.live')}>{t('topic.start.l.liveText')}</Item>,
          <Item key="account" label={t('topic.start.l.account')}>{t('topic.start.l.accountText')}</Item>,
        ]} />
      </Block>
      <Block title={t('topic.start.views')}>
        <Bullets items={[
          <Item key="dash" label={t('nav.dashboard')}>{t('topic.start.v.dashText')}</Item>,
          <Item key="inv" label={t('nav.inventory')}>{t('topic.start.v.invText')}</Item>,
          <Item key="stale" label={t('topic.start.v.stale')}>{t('topic.start.v.staleText')}</Item>,
        ]} />
      </Block>
      <Block title={t('nav.account')}>
        <Bullets items={[t('topic.start.a.mfa'), t('topic.start.a.pw'), t('topic.start.a.sessions'), t('topic.start.a.entra'), t('topic.start.a.expiry'), t('topic.start.a.reauth')]} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- virtual machines

const POWER: { op: TKey; impact: TKey; perm: string }[] = [
  { op: 'op.Start', impact: 'impact.Start', perm: 'vm.power' },
  { op: 'op.Shutdown', impact: 'impact.Shutdown', perm: 'vm.power' },
  { op: 'op.Restart', impact: 'impact.Restart', perm: 'vm.power' },
  { op: 'op.Save', impact: 'impact.Save', perm: 'vm.power' },
  { op: 'op.Pause', impact: 'impact.Pause', perm: 'vm.power' },
  { op: 'op.Resume', impact: 'impact.Resume', perm: 'vm.power' },
  { op: 'op.TurnOff', impact: 'impact.TurnOff', perm: 'vm.power.force' },
  { op: 'op.Reset', impact: 'impact.Reset', perm: 'vm.power.force' },
];

function VmsGuide() {
  const { t } = useI18n();
  return (
    <>
      <Lead links={<Go to="/inventory?view=vms" icon={Monitor}>{t('inv.view.vms')}</Go>}>{t('topic.vms.intro')}</Lead>
      <Block title={t('topic.vms.power')}>
        <Text>{t('topic.vms.powerHelp')}</Text>
        <Terms head={[t('job.operation'), t('topic.col.effect'), t('topic.col.permission')]}
          rows={POWER.map((p) => [t(p.op), t(p.impact), <Code key="p">{p.perm}</Code>])} />
        <Bullets items={[t('topic.vms.p.guest'), t('topic.vms.p.force'), t('topic.vms.p.quick')]} />
      </Block>
      <Block title={t('topic.vms.tabs')}>
        <Bullets items={[
          <Item key="summary" label={t('vm.tab.summary')}>{t('topic.vms.t.summary')}</Item>,
          <Item key="console" label={t('vm.tab.console')}>{t('topic.vms.t.console')}</Item>,
          <Item key="settings" label={t('vm.tab.settings')}>{t('topic.vms.t.settings')}</Item>,
          <Item key="hardware" label={t('vm.tab.hardware')}>{t('topic.vms.t.hardware')}</Item>,
          <Item key="checkpoints" label={t('vm.tab.checkpoints')}>{t('topic.vms.t.checkpoints')}</Item>,
          <Item key="events" label={t('vm.tab.events')}>{t('topic.vms.t.events')}</Item>,
        ]} />
      </Block>
      <Block title={t('topic.vms.notes')}>
        <Bullets items={[t('topic.vms.n.where'), t('topic.vms.n.edit')]} />
        <Needs perms={['inventory.annotate']} />
      </Block>
      <Block title={t('topic.vms.delete')}>
        <Steps>
          <Step n={1} id="vmdel-1" title={t('topic.vms.d1.title')}><Text>{t('topic.vms.d1.help')}</Text></Step>
          <Step n={2} id="vmdel-2" title={t('topic.vms.d2.title')}>
            <Text>{t('topic.vms.d2.help')}</Text>
            <Notice kind="warn">{t('vm.deleteDisksKept')}</Notice>
            <Needs perms={['vm.delete']} />
          </Step>
          <Step n={3} id="vmdel-3" title={t('topic.vms.d3.title')}>
            <Text>{t('topic.vms.d3.help')}</Text>
            <Needs perms={['vm.delete']} />
          </Step>
        </Steps>
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- console

function ConsoleGuide() {
  const { t } = useI18n();
  return (
    <>
      <Lead links={<Go to="/inventory?view=vms" icon={Monitor}>{t('inv.view.vms')}</Go>}>{t('topic.console.intro')}</Lead>
      <Block title={t('topic.console.use')}>
        <Steps>
          <Step n={1} id="con-1" title={t('topic.console.s1.title')}><Text>{t('topic.console.s1.help')}</Text><Needs perms={['vm.console']} /></Step>
          <Step n={2} id="con-2" title={t('topic.console.s2.title')}><Text>{t('topic.console.s2.help')}</Text></Step>
          <Step n={3} id="con-3" title={t('topic.console.s3.title')}><Text>{t('topic.console.s3.help')}</Text></Step>
          <Step n={4} id="con-4" title={t('topic.console.s4.title')}><Text>{t('topic.console.s4.help')}</Text></Step>
          <Step n={5} id="con-5" title={t('topic.console.s5.title')}><Text>{t('topic.console.s5.help')}</Text></Step>
        </Steps>
      </Block>
      <Block title={t('topic.console.tools')}>
        <Bullets items={[
          <Item key="cad" label={t('con.cad')}>{t('topic.console.t.cad')}</Item>,
          <Item key="refresh" label={t('con.refresh')}>{t('topic.console.t.refresh')}</Item>,
          <Item key="fit" label={`${t('con.fit')} / ${t('con.actual')}`}>{t('topic.console.t.fit')}</Item>,
          <Item key="full" label={t('con.fullscreen')}>{t('topic.console.t.full')}</Item>,
          <Item key="popout" label={t('con.popout')}>{t('topic.console.t.popout')}</Item>,
          <Item key="reconnect" label={t('con.reconnect')}>{t('topic.console.t.reconnect')}</Item>,
        ]} />
      </Block>
      <Block title={t('topic.h.notes')}>
        <Notice kind="info">{t('con.limits')}</Notice>
        <Bullets items={[t('con.idle'), t('topic.console.n.offline')]} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- VM settings

function SettingsGuide() {
  const { t } = useI18n();
  return (
    <>
      <Lead links={<Go to="/inventory?view=vms" icon={Monitor}>{t('inv.view.vms')}</Go>}>{t('topic.settings.intro')}</Lead>
      <Block title={t('topic.settings.how')}>
        <Steps>
          <Step n={1} id="cfg-1" title={t('topic.settings.s1.title')}><Text>{t('topic.settings.s1.help')}</Text></Step>
          <Step n={2} id="cfg-2" title={t('topic.settings.s2.title')}><Text>{t('topic.settings.s2.help')}</Text></Step>
          <Step n={3} id="cfg-3" title={t('topic.settings.s3.title')}><Text>{t('topic.settings.s3.help')}</Text></Step>
        </Steps>
      </Block>
      <Block title={t('topic.settings.perms')}>
        <Terms head={[t('topic.col.what'), t('topic.col.permission')]} rows={[
          [[t('cfg.general'), t('cfg.processor'), t('cfg.memory'), t('cfg.firmware')].join(', '), <Code key="c">vm.config.compute</Code>],
          [[t('cfg.dvd'), t('cfg.disks')].join(', '), <Code key="s">vm.config.storage</Code>],
          [t('cfg.network'), <Code key="n">vm.config.network</Code>],
          [t('cfg.notes'), <Code key="a">inventory.annotate</Code>],
        ]} />
      </Block>
      <Block title={t('topic.settings.running')}>
        <Text>{t('topic.settings.runningHelp')}</Text>
        <div className="grid gap-4 md:grid-cols-2">
          <div className="space-y-2 rounded-lg border p-3">
            <StatusBadge tone="warn">{t('topic.settings.offOnly')}</StatusBadge>
            <Bullets items={[t('topic.settings.o1'), t('topic.settings.o2'), t('topic.settings.o3'), t('topic.settings.o4'), t('topic.settings.o5'), t('topic.settings.o6'), t('topic.settings.o7')]} />
          </div>
          <div className="space-y-2 rounded-lg border p-3">
            <StatusBadge tone="ok">{t('topic.settings.liveOk')}</StatusBadge>
            <Bullets items={[t('topic.settings.r1'), t('topic.settings.r2'), t('topic.settings.r3'), t('topic.settings.r4'), t('topic.settings.r5'), t('topic.settings.r6'), t('topic.settings.r7')]} />
          </div>
        </div>
      </Block>
      <Block title={t('topic.h.notes')}>
        <Bullets items={[
          t('topic.settings.n.memory'), t('topic.settings.n.disk'), t('topic.settings.n.iso'), t('cfg.tpmReadOnly'), t('cfg.gen1NoFirmware'),
          t('wiz.genFixed'), t('topic.settings.n.group'),
        ]} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- create a VM

function CreateGuide() {
  const { t } = useI18n();
  const inv = useInventory();
  const ops = useOps();
  const canCreate = inv.hosts.some((h) => h.canCreateVm);
  const steps: [TKey, TKey][] = [
    ['wiz.step.name', 'topic.create.s1'], ['wiz.step.compute', 'topic.create.s2'], ['wiz.step.storage', 'topic.create.s3'],
    ['wiz.step.network', 'topic.create.s4'], ['wiz.step.media', 'topic.create.s5'], ['wiz.step.options', 'topic.create.s6'],
    ['wiz.step.summary', 'topic.create.s7'],
  ];
  return (
    <>
      <Lead links={<>
        {canCreate && <Button size="sm" onClick={() => ops.openCreateVm()}><Plus />{t('wiz.title')}</Button>}
        <Go to="/inventory" icon={Layers}>{t('nav.inventory')}</Go>
      </>}>{t('topic.create.intro')}</Lead>
      <Block title={t('topic.create.steps')}>
        <Needs perms={['vm.create']} />
        <Steps>
          {steps.map(([title, help], i) => (
            <Step key={title} n={i + 1} id={`wiz-${i + 1}`} title={t(title)}><Text>{t(help)}</Text></Step>
          ))}
        </Steps>
      </Block>
      <Block title={t('topic.h.notes')}>
        <Bullets items={[t('topic.create.n.fixed'), t('topic.create.n.iso'), t('topic.create.n.group'), t('topic.create.n.template'), t('topic.create.n.after')]} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- checkpoints

function CheckpointsGuide() {
  const { t } = useI18n();
  return (
    <>
      <Lead links={<Go to="/inventory?view=vms" icon={Monitor}>{t('inv.view.vms')}</Go>}>{t('topic.cp.intro')}</Lead>
      <Notice kind="warn">{t('cp.notBackup')}</Notice>
      <Block title={t('topic.cp.create')}>
        <Text>{t('topic.cp.c1')}</Text>
        <Text>{t('topic.cp.c2')}</Text>
        <Bullets items={[
          <Item key="p" label={t('opt.cp.Production')}>{t('topic.cp.type.Production')}</Item>,
          <Item key="po" label={t('opt.cp.ProductionOnly')}>{t('topic.cp.type.ProductionOnly')}</Item>,
          <Item key="s" label={t('opt.cp.Standard')}>{t('topic.cp.type.Standard')}</Item>,
          <Item key="d" label={t('opt.cp.Disabled')}>{t('topic.cp.type.Disabled')}</Item>,
        ]} />
        <Needs perms={['vm.checkpoint.create']} />
      </Block>
      <Block title={t('topic.cp.apply')}>
        <Steps>
          <Step n={1} id="cpa-1" title={t('topic.cp.a1.title')}><Text>{t('topic.cp.a1.help')}</Text></Step>
          <Step n={2} id="cpa-2" title={t('topic.cp.a2.title')}><Text>{t('topic.cp.a2.help')}</Text></Step>
          <Step n={3} id="cpa-3" title={t('topic.cp.a3.title')}><Text>{t('topic.cp.a3.help')}</Text></Step>
        </Steps>
        <Needs perms={['vm.checkpoint.apply']} />
      </Block>
      <Block title={t('topic.cp.delete')}>
        <Bullets items={[t('topic.cp.d1'), t('topic.cp.d2'), t('topic.cp.d3')]} />
        <Needs perms={['vm.checkpoint.apply']} />
      </Block>
      <Block title={t('topic.h.notes')}>
        <Bullets items={[t('topic.cp.n.short'), t('topic.cp.n.expand'), t('topic.cp.n.deleteVm')]} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- templates

const TEMPLATE_STATES: { label: TKey; tone: 'ok' | 'warn' | 'danger' | 'info'; text: TKey }[] = [
  { label: 'tpl.status.Creating', tone: 'info', text: 'topic.tpl.st.Creating' },
  { label: 'tpl.status.Ready', tone: 'ok', text: 'topic.tpl.st.Ready' },
  { label: 'tpl.status.Failed', tone: 'danger', text: 'tpl.statusHelp.Failed' },
  { label: 'tpl.status.Unverified', tone: 'warn', text: 'tpl.statusHelp.Unverified' },
  { label: 'tpl.status.Deleting', tone: 'info', text: 'topic.tpl.st.Deleting' },
];

function TemplatesGuide() {
  const { t } = useI18n();
  return (
    <>
      <Lead links={<Go to="/templates" icon={LayoutTemplate}>{t('nav.templates')}</Go>}>{t('topic.tpl.intro')}</Lead>
      <Block title={t('topic.tpl.prepare')}>
        <Steps>
          <Step n={1} id="tplp-1" title={t('topic.tpl.p1.title')}><Text>{t('topic.tpl.p1.help')}</Text></Step>
          <Step n={2} id="tplp-2" title={t('topic.tpl.p2.title')}><Text>{t('topic.tpl.p2.help')}</Text></Step>
          <Step n={3} id="tplp-3" title={t('topic.tpl.p3.title')}><Text>{t('topic.tpl.p3.help')}</Text></Step>
        </Steps>
      </Block>
      <Block title={t('topic.tpl.create')}>
        <Steps>
          <Step n={1} id="tplc-1" title={t('tpl.capture')}><Text>{t('topic.tpl.c1.help')}</Text><Needs perms={['templates.manage']} /></Step>
          <Step n={2} id="tplc-2" title={t('tpl.library')}><Text>{t('topic.tpl.c2.help')}</Text></Step>
          <Step n={3} id="tplc-3" title={t('topic.tpl.c3.title')}><Text>{t('topic.tpl.c3.help')}</Text></Step>
        </Steps>
      </Block>
      <Block title={t('topic.tpl.use')}>
        <Steps>
          <Step n={1} id="tplu-1" title={t('topic.tpl.u1.title')}><Text>{t('topic.tpl.u1.help')}</Text><Needs perms={['vm.create']} /></Step>
          <Step n={2} id="tplu-2" title={t('topic.tpl.u2.title')}><Text>{t('topic.tpl.u2.help')}</Text></Step>
          <Step n={3} id="tplu-3" title={t('topic.tpl.u3.title')}><Text>{t('topic.tpl.u3.help')}</Text></Step>
        </Steps>
      </Block>
      <Block title={t('topic.tpl.states')}>
        <Terms head={[t('tpl.status'), t('topic.col.meaning')]}
          rows={TEMPLATE_STATES.map((s) => [<StatusBadge key="b" tone={s.tone}>{t(s.label)}</StatusBadge>, t(s.text)])} />
      </Block>
      <Block title={t('topic.h.notes')}>
        <Bullets items={[t('topic.tpl.n.secrets'), t('topic.tpl.n.tpm'), t('topic.tpl.n.host'), t('topic.tpl.n.manage'), t('topic.tpl.n.serial'), t('topic.tpl.n.list')]} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- groups, permissions and limits

function GroupsGuide() {
  const { t } = useI18n();
  const { can } = useSession();
  return (
    <>
      <Lead links={<>
        <Go to="/inventory?view=groups" icon={FolderTree}>{t('inv.view.groups')}</Go>
        {(can('users.manage') || can('roles.manage')) && <Go to="/users" icon={Users}>{t('nav.users')}</Go>}
      </>}>{t('topic.groups.intro')}</Lead>
      <Block title={t('topic.groups.organize')}>
        <Needs perms={['groups.manage']} />
        <Steps>
          <Step n={1} id="grp-1" title={t('topic.groups.o1.title')}><Text>{t('topic.groups.o1.help')}</Text></Step>
          <Step n={2} id="grp-2" title={t('topic.groups.o2.title')}><Text>{t('topic.groups.o2.help')}</Text></Step>
          <Step n={3} id="grp-3" title={t('topic.groups.o3.title')}><Text>{t('topic.groups.o3.help')}</Text></Step>
          <Step n={4} id="grp-4" title={t('topic.groups.o4.title')}><Text>{t('topic.groups.o4.help')}</Text></Step>
        </Steps>
      </Block>
      <Block title={t('topic.groups.perms')}>
        <Bullets items={[t('topic.groups.r1'), t('topic.groups.r2'), t('group.inheritHelp'), t('roles.denyHelp'), t('topic.groups.r5'), t('topic.groups.r6')]} />
      </Block>
      <Block title={t('topic.groups.limits')}>
        <Text>{t('topic.groups.limitsHelp')}</Text>
        <Bullets items={[t('topic.groups.l1'), t('topic.groups.l2'), t('topic.groups.l3'), t('topic.groups.l4'), t('topic.groups.l5')]} />
        <Needs perms={['groups.manage', 'hosts.manage']} />
      </Block>
      <Block title={t('host.tab.vmgroups')}>
        <Bullets items={[t('topic.groups.v1'), t('topic.groups.v2'), t('topic.groups.v3')]} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- networks

function NetworksGuide() {
  const { t } = useI18n();
  return (
    <>
      <Lead links={<Go to="/networks" icon={Network}>{t('nav.networks')}</Go>}>{t('topic.net.intro')}</Lead>
      <Block title={t('topic.net.types')}>
        <Terms head={[t('host.switchType'), t('topic.col.meaning')]} rows={[
          [t('net.type.External'), t('topic.net.type.External')],
          [t('net.type.Internal'), t('topic.net.type.Internal')],
          [t('net.type.Private'), t('topic.net.type.Private')],
        ]} />
      </Block>
      <Block title={t('topic.net.create')}>
        <Steps>
          <Step n={1} id="net-1" title={t('net.new')}><Text>{t('topic.net.c1.help')}</Text><Needs perms={['network.manage']} /></Step>
          <Step n={2} id="net-2" title={t('topic.net.c2.title')}><Text>{t('topic.net.c2.help')}</Text></Step>
          <Step n={3} id="net-3" title={t('topic.net.c3.title')}><Text>{t('topic.net.c3.help')}</Text></Step>
          <Step n={4} id="net-4" title={t('topic.net.c4.title')}><Text>{t('topic.net.c4.help')}</Text></Step>
        </Steps>
        <Notice kind="danger">{t('topic.net.warn')}</Notice>
      </Block>
      <Block title={t('net.delete')}>
        <Bullets items={[t('topic.net.d1'), t('topic.net.d2'), t('topic.net.d3')]} />
      </Block>
      <Block title={t('lim.networksOwned')}>
        <Bullets items={[t('topic.net.p1'), t('topic.net.p2'), t('topic.net.p3')]} />
      </Block>
      <Block title={t('topic.h.notes')}>
        <Bullets items={[t('topic.net.n1'), t('topic.net.n2')]} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- storage and files

function StorageGuide() {
  const { t } = useI18n();
  return (
    <>
      <Lead links={<>
        <Go to="/storage" icon={HardDrive}>{t('nav.storage')}</Go>
        <Go to="/storage?tab=files" icon={HardDrive}>{t('sto.tab.files')}</Go>
      </>}>{t('topic.sto.intro')}</Lead>
      <Block title={t('sto.tab.overview')}>
        <Bullets items={[
          <Item key="v" label={t('sto.volumes')}>{t('topic.sto.volumes')}</Item>,
          <Item key="d" label={t('sto.disks')}>{t('topic.sto.disks')}</Item>,
        ]} />
      </Block>
      <Block title={t('host.isoLibrary')}>
        <Bullets items={[t('topic.sto.i1'), t('topic.sto.i2'), t('topic.sto.i3')]} />
      </Block>
      <Block title={t('topic.sto.files')}>
        <Steps>
          <Step n={1} id="files-1" title={t('topic.sto.f1.title')}><Text>{t('topic.sto.f1.help')}</Text><Needs perms={['host.files.read']} /></Step>
          <Step n={2} id="files-2" title={t('files.download')}><Text>{t('topic.sto.f2.help')}</Text></Step>
          <Step n={3} id="files-3" title={t('files.upload')}><Text>{t('topic.sto.f3.help')}</Text><Needs perms={['host.files.write']} /></Step>
        </Steps>
      </Block>
      <Block title={t('topic.h.notes')}>
        <Bullets items={[t('topic.sto.n1'), t('topic.sto.n2'), t('topic.sto.n3'), t('topic.sto.n4')]} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- tasks and audit

const JOB_STATES: { status: JobStatus; text: TKey }[] = [
  { status: 'Queued', text: 'topic.jobs.st.Queued' },
  { status: 'Running', text: 'topic.jobs.st.Running' },
  { status: 'Reconciling', text: 'topic.jobs.st.Reconciling' },
  { status: 'Succeeded', text: 'topic.jobs.st.Succeeded' },
  { status: 'Unverified', text: 'topic.jobs.st.Unverified' },
  { status: 'Failed', text: 'topic.jobs.st.Failed' },
  { status: 'Cancelled', text: 'topic.jobs.st.Cancelled' },
  { status: 'Unknown', text: 'topic.jobs.st.Unknown' },
];

function JobsGuide() {
  const { t } = useI18n();
  const { can } = useSession();
  return (
    <>
      <Lead links={<>
        <Go to="/jobs" icon={ListChecks}>{t('nav.jobs')}</Go>
        {can('audit.read') && <Go to="/audit" icon={ScrollText}>{t('nav.audit')}</Go>}
      </>}>{t('topic.jobs.intro')}</Lead>
      <Block title={t('topic.jobs.status')}>
        <Terms head={[t('job.status'), t('topic.col.meaning')]} rows={JOB_STATES.map((s) => [<JobBadge key="b" status={s.status} />, t(s.text)])} />
        <Notice kind="info">{t('topic.jobs.honest')}</Notice>
      </Block>
      <Block title={t('topic.jobs.follow')}>
        <Steps>
          <Step n={1} id="job-1" title={t('topic.jobs.f1.title')}><Text>{t('topic.jobs.f1.help')}</Text></Step>
          <Step n={2} id="job-2" title={t('topic.jobs.f2.title')}><Text>{t('topic.jobs.f2.help')}</Text></Step>
          <Step n={3} id="job-3" title={t('job.cancel')}><Text>{t('topic.jobs.f3.help')}</Text></Step>
        </Steps>
        <Bullets items={[t('topic.jobs.n1'), t('topic.jobs.n2'), t('topic.jobs.n3')]} />
      </Block>
      <Block title={t('nav.audit')}>
        <Text>{t('topic.jobs.auditHelp')}</Text>
        <Bullets items={[t('topic.jobs.a1'), t('topic.jobs.a2'), t('topic.jobs.a3'), t('topic.jobs.a4')]} />
        <Needs perms={['audit.read']} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- users and roles

const BUILT_IN_ROLES: [TKey, TKey][] = [
  ['topic.users.role.platform', 'topic.users.role.platformText'],
  ['topic.users.role.infra', 'topic.users.role.infraText'],
  ['topic.users.role.operator', 'topic.users.role.operatorText'],
  ['topic.users.role.folder', 'topic.users.role.folderText'],
  ['topic.users.role.auditor', 'topic.users.role.auditorText'],
  ['topic.users.role.readOnly', 'topic.users.role.readOnlyText'],
];

const SCOPES: [TKey, TKey][] = [
  ['topic.users.sc.global', 'topic.users.sc.globalText'],
  ['topic.users.sc.host', 'topic.users.sc.hostText'],
  ['topic.users.sc.group', 'topic.users.sc.groupText'],
  ['topic.users.sc.vm', 'topic.users.sc.vmText'],
];

function UsersGuide() {
  const { t } = useI18n();
  const { can } = useSession();
  return (
    <>
      <Lead links={<>
        {(can('users.manage') || can('roles.manage')) && <Go to="/users" icon={Users}>{t('nav.users')}</Go>}
        {can('settings.manage') && <Go to="/settings" icon={Settings}>{t('nav.settings')}</Go>}
        {can('hosts.manage') && <Go to="/guide/entra" icon={BookOpen}>{t('guide.page.entra.title')}</Go>}
      </>}>{t('topic.users.intro')}</Lead>
      <Block title={t('users.add')}>
        <Needs perms={['users.manage']} />
        <Steps>
          <Step n={1} id="usr-1" title={t('users.type')}><Text>{t('topic.users.s1.help')}</Text></Step>
          <Step n={2} id="usr-2" title={t('topic.users.s2.title')}><Text>{t('topic.users.s2.help')}</Text></Step>
          <Step n={3} id="usr-3" title={t('topic.users.s3.title')}><Text>{t('topic.users.s3.help')}</Text></Step>
          <Step n={4} id="usr-4" title={t('roles.role')}><Text>{t('topic.users.s4.help')}</Text></Step>
        </Steps>
      </Block>
      <Block title={t('topic.users.manage')}>
        <Bullets items={[t('topic.users.m1'), t('topic.users.m2'), t('topic.users.m3'), t('topic.users.m4')]} />
      </Block>
      <Block title={t('topic.users.roles')}>
        <Text>{t('topic.users.rolesHelp')}</Text>
        <Terms head={[t('roles.role'), t('topic.col.meaning')]} rows={BUILT_IN_ROLES.map(([name, text]) => [t(name), t(text)])} />
      </Block>
      <Block title={t('topic.users.scopes')}>
        <Terms head={[t('roles.scope'), t('topic.col.meaning')]} rows={SCOPES.map(([name, text]) => [t(name), t(text)])} />
        <Bullets items={[t('roles.denyHelp'), t('topic.users.sc.assign')]} />
        <Needs perms={['roles.manage']} />
      </Block>
      <Block title={t('topic.users.security')}>
        <Text>{t('topic.users.securityHelp')}</Text>
        <Bullets items={[t('topic.users.x1'), t('topic.users.x2'), t('topic.users.x3')]} />
        <Notice kind="warn">{t('topic.users.breakGlass')}</Notice>
        <Needs perms={['settings.manage']} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- VM backups

const BACKUP_POINT_STATES: { label: TKey; tone: 'ok' | 'warn' | 'danger' | 'info'; text: TKey }[] = [
  { label: 'bk.pointStatus.Creating', tone: 'info', text: 'topic.bk.st.Creating' },
  { label: 'bk.pointStatus.Ok', tone: 'ok', text: 'topic.bk.st.Ok' },
  { label: 'bk.pointStatus.Unverified', tone: 'warn', text: 'topic.bk.st.Unverified' },
  { label: 'bk.pointStatus.Failed', tone: 'danger', text: 'topic.bk.st.Failed' },
];

const BACKUP_RESTORES: [TKey, TKey, string][] = [
  ['bk.restore.mode.NewVm', 'topic.bk.r.newVm', 'backup.restore, vm.create'],
  ['bk.restore.mode.Replace', 'topic.bk.r.replace', 'backup.restore'],
  ['bk.restore.mode.Test', 'topic.bk.r.test', 'backup.restore, vm.create'],
  ['bk.point.restoreDisk', 'topic.bk.r.disk', 'backup.restore, vm.config.storage'],
  ['bk.tab.files', 'topic.bk.r.files', 'backup.restore'],
];

function BackupGuide() {
  const { t } = useI18n();
  return (
    <>
      <Lead links={<Go to="/backup" icon={Archive}>{t('nav.backup')}</Go>}>{t('topic.bk.intro')}</Lead>
      <Block title={t('topic.bk.how')}>
        <Text>{t('topic.bk.howHelp')}</Text>
        <Steps>
          <Step n={1} id="bkh-1" title={t('topic.bk.h1.title')}><Text>{t('topic.bk.h1.help')}</Text></Step>
          <Step n={2} id="bkh-2" title={t('topic.bk.h2.title')}><Text>{t('topic.bk.h2.help')}</Text></Step>
          <Step n={3} id="bkh-3" title={t('topic.bk.h3.title')}><Text>{t('topic.bk.h3.help')}</Text></Step>
          <Step n={4} id="bkh-4" title={t('topic.bk.h4.title')}><Text>{t('topic.bk.h4.help')}</Text></Step>
          <Step n={5} id="bkh-5" title={t('topic.bk.h5.title')}><Text>{t('topic.bk.h5.help')}</Text></Step>
        </Steps>
        <Notice kind="info">{t('topic.bk.notWsb')}</Notice>
      </Block>
      <Block title={t('topic.bk.prepare')}>
        <Steps>
          <Step n={1} id="bkp-1" title={t('topic.bk.p1.title')}><Text>{t('topic.bk.p1.help')}</Text></Step>
          <Step n={2} id="bkp-2" title={t('topic.bk.p2.title')}><Text>{t('topic.bk.p2.help')}</Text><Needs perms={['backup.manage']} /></Step>
          <Step n={3} id="bkp-3" title={t('staging.title')}><Text>{t('topic.bk.p3.help')}</Text><Needs perms={['hosts.manage', 'backup.manage']} /></Step>
          <Step n={4} id="bkp-4" title={t('bk.repo.check')}><Text>{t('topic.bk.p4.help')}</Text></Step>
        </Steps>
      </Block>
      <Block title={t('topic.bk.run')}>
        <Bullets items={[
          <Item key="j" label={t('bk.job.new')}>{t('topic.bk.j.job')}</Item>,
          <Item key="r" label={t('bk.job.retention')}>{t('topic.bk.j.retention')}</Item>,
          <Item key="n" label={t('bk.backupNow')}>{t('topic.bk.j.now')}</Item>,
          <Item key="o" label={t('bk.job.notify')}>{t('topic.bk.j.notify')}</Item>,
        ]} />
        <Needs perms={['backup.manage', 'backup.run']} />
      </Block>
      <Block title={t('topic.bk.states')}>
        <Terms head={[t('topic.col.what'), t('topic.col.meaning')]}
          rows={BACKUP_POINT_STATES.map((s) => [<StatusBadge key="b" tone={s.tone}>{t(s.label)}</StatusBadge>, t(s.text)])} />
        <Text>{t('topic.bk.verify')}</Text>
      </Block>
      <Block title={t('topic.bk.restore')}>
        <Terms head={[t('topic.col.what'), t('topic.col.effect'), t('topic.col.permission')]}
          rows={BACKUP_RESTORES.map(([label, text, perms]) => [t(label), t(text), <Code key="p">{perms}</Code>])} />
        <Notice kind="warn">{t('topic.bk.replaceWarn')}</Notice>
      </Block>
      <Block title={t('topic.h.notes')}>
        <Bullets items={[t('topic.bk.n.full'), t('topic.bk.n.consistency'), t('topic.bk.n.security'), t('topic.bk.n.portal'), t('topic.bk.n.docs')]} />
      </Block>
    </>
  );
}

// ---------------------------------------------------------------- catalog

export const TOPIC_GUIDES: TopicGuide[] = [
  { slug: 'start', title: 'topic.start.title', desc: 'topic.start.desc', icon: Rocket, Page: StartGuide, related: ['vms', 'jobs'] },
  { slug: 'vms', title: 'topic.vms.title', desc: 'topic.vms.desc', icon: Monitor, Page: VmsGuide, related: ['console', 'settings', 'checkpoints'] },
  { slug: 'console', title: 'topic.console.title', desc: 'topic.console.desc', icon: MonitorPlay, Page: ConsoleGuide, related: ['vms'] },
  { slug: 'settings', title: 'topic.settings.title', desc: 'topic.settings.desc', icon: SlidersHorizontal, Page: SettingsGuide, related: ['vms', 'storage', 'networks'] },
  { slug: 'create', title: 'topic.create.title', desc: 'topic.create.desc', icon: SquarePlus, Page: CreateGuide, related: ['templates', 'settings', 'groups'] },
  { slug: 'checkpoints', title: 'topic.cp.title', desc: 'topic.cp.desc', icon: Camera, Page: CheckpointsGuide, related: ['vms', 'templates'] },
  { slug: 'templates', title: 'topic.tpl.title', desc: 'topic.tpl.desc', icon: LayoutTemplate, Page: TemplatesGuide, related: ['create', 'checkpoints'] },
  { slug: 'groups', title: 'topic.groups.title', desc: 'topic.groups.desc', icon: FolderTree, Page: GroupsGuide, related: ['users', 'networks'] },
  { slug: 'networks', title: 'topic.net.title', desc: 'topic.net.desc', icon: Network, Page: NetworksGuide, related: ['settings', 'groups'] },
  { slug: 'storage', title: 'topic.sto.title', desc: 'topic.sto.desc', icon: HardDrive, Page: StorageGuide, related: ['create', 'settings'] },
  { slug: 'backup', title: 'topic.bk.title', desc: 'topic.bk.desc', icon: Archive, Page: BackupGuide, related: ['checkpoints', 'storage', 'templates'] },
  { slug: 'jobs', title: 'topic.jobs.title', desc: 'topic.jobs.desc', icon: ListChecks, Page: JobsGuide, related: ['start', 'users'] },
  { slug: 'users', title: 'topic.users.title', desc: 'topic.users.desc', icon: Users, Page: UsersGuide, related: ['groups', 'jobs'] },
];

/** Route element of /guide/topics/:topic. */
export function TopicGuidePage() {
  const { topic } = useParams();
  const { t } = useI18n();
  const guide = TOPIC_GUIDES.find((g) => g.slug === topic);
  if (!guide) {
    return (
      <Notice kind="warn">
        {t('topic.notFound')} <Link to="/guide" className="font-medium text-primary hover:underline">{t('topic.allGuides')}</Link>
      </Notice>
    );
  }
  const related = (guide.related ?? []).map((s) => TOPIC_GUIDES.find((g) => g.slug === s)).filter((g): g is TopicGuide => !!g);
  return (
    <>
      <guide.Page />
      <nav className="flex flex-wrap items-center gap-2 border-t pt-3" aria-label={t('topic.related')}>
        <span className="text-xs text-muted-foreground">{t('topic.related')}</span>
        {related.map((g) => (
          <Button key={g.slug} size="sm" variant="ghost" asChild><Link to={topicPath(g.slug)}><g.icon />{t(g.title)}</Link></Button>
        ))}
        <span className="flex-1" />
        <Button size="sm" variant="ghost" asChild><Link to="/guide"><BookOpen />{t('topic.allGuides')}</Link></Button>
      </nav>
    </>
  );
}
