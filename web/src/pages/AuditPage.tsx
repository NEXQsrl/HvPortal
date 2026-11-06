import { Bot, ChevronDown, ChevronRight, Download, ScrollText, Server, ShieldCheck, UserRound } from 'lucide-react';
import { Fragment, useMemo, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { get, post } from '@/api/client';
import type { AuditEntry } from '@/api/types';
import { Empty, ErrorAlert, Loading, Notice, PageHeader, StatusBadge } from '@/components/common';
import { Pager } from '@/components/OtpInput';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { NativeSelect } from '@/components/ui/native-select';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { dictionaryKeys, useI18n, type TKey } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLoad } from '@/lib/hooks';

type T = ReturnType<typeof useI18n>['t'];
const KNOWN = new Set(dictionaryKeys.en);
/** Text of a key built at run time, or the fallback when the dictionary has no such entry. */
const optional = (t: T, key: string, fallback: string) => (KNOWN.has(key) ? t(key as TKey) : fallback);

const CATEGORIES = ['auth', 'users', 'role', 'groups', 'host', 'vm', 'template', 'settings', 'agent'] as const;
const CATEGORY_LABEL: Record<(typeof CATEGORIES)[number], TKey> = {
  auth: 'audit.cat.auth', users: 'audit.cat.users', role: 'audit.cat.role', groups: 'audit.cat.groups', host: 'audit.cat.host',
  vm: 'audit.cat.vm', template: 'audit.cat.templates', settings: 'audit.cat.settings', agent: 'audit.cat.agent',
};
/** Action prefixes of each area (the API takes them separated by commas). */
const CATEGORY_PREFIXES: Record<(typeof CATEGORIES)[number], string> = {
  auth: 'auth.,mfa.,setup.,admin_cli.', users: 'users.', role: 'roles.,role_assignments.', groups: 'groups.', host: 'hosts.,host.',
  vm: 'vm.,vms.,console.', template: 'template.,templates.', settings: 'settings.,audit.,downloads.,jobs.', agent: 'agent.,management_agents.',
};
const OUTCOMES = ['Success', 'Failure', 'Denied'] as const;

/** What an entry is about: the action in words and whether it is the request or the execution of a queued task. */
function describe(e: AuditEntry, t: T): { title: string; phase: 'request' | 'job' | null } {
  const request = e.action.endsWith('.request');
  const code = request ? e.action.slice(0, -'.request'.length) : e.action;
  return { title: optional(t, 'audit.a.' + code, code), phase: request ? 'request' : e.authMethod === 'job' ? 'job' : null };
}

/** "local:pwd+totp" -> "local account · password + MFA code". */
function authText(method: string, t: T): string {
  const [kind = method, factors] = method.split(':', 2);
  const word = (w: string) => optional(t, 'audit.auth.' + w, w);
  return factors ? `${word(kind)} · ${factors.split('+').map(word).join(' + ')}` : word(kind);
}

function actor(e: AuditEntry, t: T): { name: string; icon: ReactNode } {
  if (e.actorName && e.actorName !== 'system') return { name: e.actorName, icon: <UserRound className="size-3.5" /> };
  if (e.authMethod === 'system' || e.actorName === 'system') return { name: t('audit.actor.system'), icon: <Bot className="size-3.5" /> };
  if (e.authMethod === 'agent-mtls' || e.authMethod === 'enrollment-token') return { name: t('audit.actor.agent'), icon: <Server className="size-3.5" /> };
  return { name: t('audit.actor.anonymous'), icon: <UserRound className="size-3.5" /> };
}

interface Pair { key: string; label: string; value: string; from?: string }

const ISO_DATE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/;
const humanize = (key: string) => key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/[_.]/g, ' ').toLowerCase();

/** Details of an entry as label/value pairs: nulls dropped, "request" unwrapped, before/after shown as changes. */
function pairsOf(details: unknown, t: T, fmtDate: (iso: string) => string): Pair[] {
  const label = (key: string) => optional(t, 'audit.k.' + key.toLowerCase(), humanize(key));
  const text = (key: string, v: unknown): string => {
    if (v === null || v === undefined || v === '') return '—';
    if (typeof v === 'boolean') return t(v ? 'audit.yes' : 'audit.no');
    if (typeof v === 'number') return /bytes$/i.test(key) ? formatBytes(v) : String(v);
    if (typeof v === 'string') return ISO_DATE.test(v) ? fmtDate(v) : v;
    if (Array.isArray(v)) return v.length === 0 ? '—' : v.map((x) => (typeof x === 'object' && x !== null ? compact(x as Record<string, unknown>) : text(key, x))).join(', ');
    return compact(v as Record<string, unknown>);
  };
  const compact = (o: Record<string, unknown>): string =>
    Object.entries(o).filter(([, v]) => v !== null && v !== undefined && v !== '').map(([k, v]) => `${label(k)}: ${text(k, v)}`).join(' · ') || '—';
  const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
  const empty = (v: unknown) => v === null || v === undefined || v === '' || (Array.isArray(v) && v.length === 0);

  const out: Pair[] = [];
  const walk = (o: Record<string, unknown>, prefix: string) => {
    const keys = Object.keys(o);
    const beforeKey = keys.find((k) => k.toLowerCase() === 'before');
    const afterKey = keys.find((k) => k.toLowerCase() === 'after');
    if (beforeKey !== undefined && afterKey !== undefined) {
      const b = o[beforeKey];
      const a = o[afterKey];
      if (isObject(a) || isObject(b)) {
        const bo = isObject(b) ? b : {};
        const ao = isObject(a) ? a : {};
        for (const k of new Set([...Object.keys(bo), ...Object.keys(ao)])) {
          if (JSON.stringify(bo[k] ?? null) !== JSON.stringify(ao[k] ?? null)) out.push({ key: k, label: prefix + label(k), value: text(k, ao[k]), from: text(k, bo[k]) });
        }
      } else if (JSON.stringify(a ?? null) !== JSON.stringify(b ?? null)) {
        out.push({ key: 'change', label: prefix + t('audit.change'), value: text('', a), from: text('', b) });
      }
    }
    for (const [k, v] of Object.entries(o)) {
      if (k === beforeKey && afterKey !== undefined) continue;
      if (k === afterKey && beforeKey !== undefined) continue;
      if (empty(v)) continue;
      if (isObject(v)) walk(v, k.toLowerCase() === 'request' ? prefix : `${prefix}${label(k)} › `);
      else out.push({ key: k, label: prefix + label(k), value: text(k, v) });
    }
  };
  if (isObject(details)) walk(details, '');
  else if (!empty(details)) out.push({ key: 'details', label: t('audit.detail.details'), value: text('', details) });
  return out;
}

function parse(json: string | null): unknown {
  if (!json) return null;
  try { return JSON.parse(json) as unknown; } catch { return json; }
}

/** Fields that only make sense in the expanded view (identifiers, hashes). */
const NOISE = /^(jobid|sha256|consolesession|checkpointid|serial|parentid|groupid|hypervcode)$/i;

const RESOURCE_PATH: Record<string, string> = { vm: '/vms/', host: '/hosts/', group: '/groups/' };

function ResourceRef({ e }: { e: AuditEntry }) {
  const { t } = useI18n();
  if (!e.resourceType) return null;
  const type = optional(t, 'audit.rt.' + e.resourceType, e.resourceType);
  const name = e.resourceName ?? e.resourceId;
  if (!name) return <span className="text-muted-foreground">{type}</span>;
  const path = RESOURCE_PATH[e.resourceType];
  return (
    <span className="inline-flex flex-wrap items-baseline gap-x-1">
      <span className="text-muted-foreground">{type}</span>
      {e.resourceExists && path && e.resourceId
        ? <Link className="font-medium text-primary hover:underline" to={path + e.resourceId} onClick={(ev) => ev.stopPropagation()}>{name}</Link>
        : <span className={e.resourceName ? 'font-medium' : 'font-mono text-xs'}>{name}</span>}
      {e.resourceName && !e.resourceExists && e.resourceId && <span className="text-xs text-muted-foreground">({t('audit.removed')})</span>}
    </span>
  );
}

function Details({ e }: { e: AuditEntry }) {
  const { t, fmtDate } = useI18n();
  const pairs = useMemo(() => pairsOf(parse(e.detailsJson), t, fmtDate), [e.detailsJson, t, fmtDate]);
  const [raw, setRaw] = useState(false);
  const facts: [string, ReactNode][] = [
    [t('audit.detail.code'), <code key="c" className="font-mono">{e.action}</code>],
    [t('audit.detail.resource'), e.resourceType ? <span key="r">{e.resourceType}{e.resourceId && <> · <span className="font-mono">{e.resourceId}</span></>}</span> : '—'],
    [t('audit.detail.auth'), <span key="a">{authText(e.authMethod, t)} <span className="font-mono text-muted-foreground">({e.authMethod})</span></span>],
    [t('audit.detail.utc'), <span key="u" className="font-mono">{e.timestampUtc}</span>],
    [t('audit.source'), e.sourceIp ?? '—'],
    [t('audit.detail.correlation'), <Link key="k" className="font-mono text-primary hover:underline" to={`/audit?q=${e.correlationId}`}>{e.correlationId}</Link>],
    [t('audit.detail.entry'), <span key="i" className="font-mono">#{e.id}</span>],
  ];
  return (
    <div className="audit-details grid gap-4 px-2 py-2 text-xs lg:grid-cols-2">
      <div>
        <div className="mb-1 font-medium text-foreground">{t('audit.detail.details')}</div>
        {pairs.length === 0 ? <p className="text-muted-foreground">{t('audit.detail.noDetails')}</p> : (
          <dl className="grid grid-cols-[minmax(7rem,max-content)_1fr] gap-x-3 gap-y-1">
            {pairs.map((p, i) => (
              <Fragment key={i}>
                <dt className="text-muted-foreground">{p.label}</dt>
                <dd className="min-w-0 break-words">{p.from !== undefined && <><span className="text-muted-foreground line-through">{p.from}</span> → </>}{p.value}</dd>
              </Fragment>
            ))}
          </dl>
        )}
        {e.detailsJson && (
          <div className="mt-2">
            <button type="button" className="text-primary hover:underline" onClick={() => setRaw(!raw)}>{t('audit.detail.raw')}</button>
            {raw && <pre className="mt-1 max-h-64 overflow-auto rounded-md border bg-muted p-2 font-mono text-[11px] whitespace-pre-wrap break-all">{JSON.stringify(parse(e.detailsJson), null, 2)}</pre>}
          </div>
        )}
      </div>
      <dl className="grid h-fit grid-cols-[max-content_1fr] gap-x-3 gap-y-1">
        {facts.map(([k, v]) => <Fragment key={k}><dt className="text-muted-foreground">{k}</dt><dd className="min-w-0 break-all">{v}</dd></Fragment>)}
      </dl>
    </div>
  );
}

/** Audit log: one line per action in plain words, the name of the resource, the outcome; click a line for the details. */
export function AuditPage() {
  const { t, fmtDate } = useI18n();
  const [params, setParams] = useSearchParams();
  const param = (k: string) => params.get(k) ?? '';
  const outcome = (OUTCOMES as readonly string[]).includes(param('outcome')) ? param('outcome') : '';
  const category = (CATEGORIES as readonly string[]).includes(param('area')) ? param('area') : '';
  const days = ['1', '7', '30'].includes(param('days')) ? param('days') : '';
  const q = param('q');
  const update = (changes: Record<string, string>) => {
    setSkip(0);
    // From the latest parameters, not the ones of this render: two filters changed in a row must both stay.
    setParams((prev) => {
      const next = new URLSearchParams(prev);
      for (const [k, v] of Object.entries(changes)) { if (v) next.set(k, v); else next.delete(k); }
      return next;
    }, { replace: true });
  };
  const [text, setText] = useState(q);
  const [typedFor, setTypedFor] = useState(q);
  if (typedFor !== q) { setTypedFor(q); setText(q); }
  const [skip, setSkip] = useState(0);
  const [take, setTake] = useState(50);
  const [open, setOpen] = useState<number | null>(null);
  // The start of the period is fixed when the period is chosen, so paging does not shift it.
  const from = useMemo(() => (days ? new Date(Date.now() - Number(days) * 86_400_000).toISOString() : ''), [days]);
  const qs = `skip=${skip}&take=${take}${outcome ? '&outcome=' + outcome : ''}${category ? '&action=' + encodeURIComponent(CATEGORY_PREFIXES[category as (typeof CATEGORIES)[number]]) : ''}${q ? '&q=' + encodeURIComponent(q) : ''}${from ? '&from=' + encodeURIComponent(from) : ''}`;
  const data = useLoad(() => get<{ total: number; items: AuditEntry[] }>(`/api/v1/audit?${qs}`), [qs]);
  const [verify, setVerify] = useState<{ valid: boolean; entriesChecked: number; firstInvalidId: number | null; problem: string | null } | null>(null);
  const [error, setError] = useState<unknown>(null);
  const items = data.data?.items ?? [];

  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader icon={<ScrollText className="size-5" />} title={t('nav.audit')}
        actions={<>
          <Button size="sm" variant="outline" onClick={async () => { setError(null); try { setVerify(await post('/api/v1/audit/verify')); } catch (e) { setError(e); } }}><ShieldCheck />{t('audit.verify')}</Button>
          <Button size="sm" variant="outline" asChild><a href="/api/v1/audit/export"><Download />{t('audit.export')}</a></Button>
        </>} />
      <div className="space-y-2 px-4 empty:hidden">
        <ErrorAlert error={error ?? data.error} />
        {verify && (verify.valid
          ? <Notice kind="ok" className="mt-3">{t('audit.valid', { n: verify.entriesChecked })}</Notice>
          : <Notice kind="danger" className="mt-3">{t('audit.invalid', { id: verify.firstInvalidId ?? '?', problem: verify.problem ?? '' })}</Notice>)}
      </div>
      <Pager total={data.data?.total ?? 0} skip={skip} take={take} onChange={(s, tk) => { setSkip(s); setTake(tk); }}>
        <form className="flex flex-wrap items-center gap-2" onSubmit={(e) => { e.preventDefault(); update({ q: text.trim() }); }}>
          <Input aria-label={t('common.search')} placeholder={t('audit.search')} value={text} onChange={(e) => setText(e.target.value)} className="w-52" />
          <NativeSelect aria-label={t('audit.category')} value={category} onChange={(e) => update({ area: e.target.value })} className="w-40">
            <option value="">{t('audit.cat.all')}</option>
            {CATEGORIES.map((c) => <option key={c} value={c}>{t(CATEGORY_LABEL[c])}</option>)}
          </NativeSelect>
          <NativeSelect aria-label={t('audit.outcome')} value={outcome} onChange={(e) => update({ outcome: e.target.value })} className="w-32">
            <option value="">{t('audit.allOutcomes')}</option>
            {OUTCOMES.map((o) => <option key={o} value={o}>{t(('audit.outcome.' + o) as TKey)}</option>)}
          </NativeSelect>
          <NativeSelect aria-label={t('audit.period')} value={days} onChange={(e) => update({ days: e.target.value })} className="w-36">
            <option value="">{t('audit.period.all')}</option>
            {(['1', '7', '30'] as const).map((d) => <option key={d} value={d}>{t(('audit.period.' + d) as TKey)}</option>)}
          </NativeSelect>
          <Button type="submit" size="sm" variant="outline">{t('common.search')}</Button>
        </form>
      </Pager>
      {data.loading && !data.data ? <Loading /> : items.length === 0 ? <Empty>{t('audit.none')}</Empty> : (
        <Table containerClassName="min-h-0 flex-1">
          <TableHeader><TableRow>
            <TableHead className="w-8" />
            <TableHead>{t('audit.when')}</TableHead><TableHead>{t('audit.who')}</TableHead><TableHead>{t('audit.what')}</TableHead>
            <TableHead>{t('audit.outcome')}</TableHead><TableHead>{t('audit.source')}</TableHead>
          </TableRow></TableHeader>
          <TableBody>
            {items.map((e) => {
              const d = describe(e, t);
              const who = actor(e, t);
              const expanded = open === e.id;
              const summary = pairsOf(parse(e.detailsJson), t, fmtDate).filter((p) => !NOISE.test(p.key) && p.value.length <= 80 && p.value !== e.resourceName).slice(0, 4);
              return (
                <Fragment key={e.id}>
                  <TableRow className="audit-row cursor-pointer align-top" aria-expanded={expanded} onClick={() => setOpen(expanded ? null : e.id)}>
                    <TableCell className="pr-0">
                      <button type="button" className="text-muted-foreground" aria-label={t(expanded ? 'audit.collapse' : 'audit.expand')}
                        onClick={(ev) => { ev.stopPropagation(); setOpen(expanded ? null : e.id); }}>
                        {expanded ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
                      </button>
                    </TableCell>
                    <TableCell className="text-xs whitespace-nowrap tabular">{fmtDate(e.timestampUtc)}</TableCell>
                    <TableCell>
                      <div className="flex items-center gap-1.5 whitespace-nowrap">{who.icon}{who.name}</div>
                      <div className="text-xs text-muted-foreground">{authText(e.authMethod, t)}</div>
                    </TableCell>
                    <TableCell className="max-w-xl whitespace-normal">
                      <div className="flex flex-wrap items-baseline gap-x-2">
                        <span className="audit-title font-medium">{d.title}</span>
                        {d.phase && <StatusBadge tone="muted" title={t(d.phase === 'request' ? 'audit.phase.requestHelp' : 'audit.phase.jobHelp')}>{t(d.phase === 'request' ? 'audit.phase.request' : 'audit.phase.job')}</StatusBadge>}
                      </div>
                      <div className="text-sm"><ResourceRef e={e} /></div>
                      {summary.length > 0 && (
                        <div className="mt-0.5 flex flex-wrap gap-x-3 gap-y-0.5 text-xs text-muted-foreground">
                          {summary.map((p, i) => <span key={i}>{p.label}: <span className="text-foreground/80">{p.from !== undefined ? `${p.from} → ${p.value}` : p.value}</span></span>)}
                        </div>
                      )}
                    </TableCell>
                    <TableCell><StatusBadge tone={e.outcome === 'Success' ? 'ok' : e.outcome === 'Denied' ? 'warn' : 'danger'}>{t(('audit.outcome.' + e.outcome) as TKey)}</StatusBadge></TableCell>
                    <TableCell className="text-xs whitespace-nowrap">{e.sourceIp ?? '—'}</TableCell>
                  </TableRow>
                  {expanded && <TableRow className="bg-muted/30 hover:bg-muted/30"><TableCell /><TableCell colSpan={5} className="whitespace-normal"><Details e={e} /></TableCell></TableRow>}
                </Fragment>
              );
            })}
          </TableBody>
        </Table>
      )}
    </div>
  );
}
