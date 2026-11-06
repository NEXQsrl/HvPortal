import { Download } from 'lucide-react';
import type { ReactNode } from 'react';
import { get } from '@/api/client';
import type { PackageInfo } from '@/api/types';
import { CopyButton, Notice, StatusBadge } from '@/components/common';
import { Button } from '@/components/ui/button';
import { useI18n, type TKey } from '@/i18n';
import { formatBytes } from '@/lib/format';
import { useLoad } from '@/lib/hooks';

export const AGENT_ZIP = 'HvPortal.Agent-win-x64.zip';

/** Vertical numbered procedure. */
export function Steps({ children }: { children: ReactNode }) {
  return <div className="guide ml-3">{children}</div>;
}

export function Step({ n, title, children, id }: { n: number | string; title: ReactNode; children: ReactNode; id?: string }) {
  const hid = id ?? `step-${n}`;
  return (
    <section className="guide-step relative border-l pb-6 pl-6 last:border-l-transparent last:pb-0" aria-labelledby={hid}>
      <span className="absolute -left-3 top-0 flex size-6 items-center justify-center rounded-full border bg-card text-xs font-semibold text-primary">{n}</span>
      <h3 id={hid} className="mb-2 text-sm leading-6 font-semibold">{title}</h3>
      <div className="space-y-2 text-sm">{children}</div>
    </section>
  );
}

/** PowerShell command block with a copy button. */
export function Command({ text }: { text: string }) {
  return (
    <div className="command-block flex items-start gap-2">
      <pre className="command min-w-0 flex-1">{text}</pre>
      <CopyButton text={text} />
    </div>
  );
}

export function Bullets({ items }: { items: ReactNode[] }) {
  return <ul className="list-disc space-y-1 pl-4 text-muted-foreground">{items.map((x, i) => <li key={i}>{x}</li>)}</ul>;
}

// One request shared by every component of a page (the gateway hashes the 40+ MB zip on the first call).
let packageRequest: { at: number; promise: Promise<{ agent: PackageInfo | null }> } | null = null;

function loadAgentPackage() {
  if (!packageRequest || Date.now() - packageRequest.at > 30_000) {
    const promise = get<{ agent: PackageInfo | null }>('/api/v1/downloads');
    packageRequest = { at: Date.now(), promise };
    promise.catch(() => { packageRequest = null; });
  }
  return packageRequest.promise;
}

/** Agent package published by this gateway (version, size, signature, SHA-256). */
export function useAgentPackage() {
  return useLoad(loadAgentPackage);
}

export function PackageDownload({ help }: { help?: ReactNode }) {
  const { t } = useI18n();
  const pkg = useAgentPackage();
  if (pkg.loading && !pkg.data) return <p className="text-muted-foreground">{t('common.loading')}</p>;
  const a = pkg.data?.agent;
  if (!a) return <Notice kind="warn">{t('guide.s2.missing')}</Notice>;
  return (
    <>
      {help && <p className="text-muted-foreground">{help}</p>}
      <div className="flex flex-wrap items-center gap-2">
        <Button asChild size="sm"><a href="/api/v1/downloads/agent" download={AGENT_ZIP}><Download />{t('guide.s2.download')}</a></Button>
        <span className="text-xs text-muted-foreground">{a.version ?? ''} · {formatBytes(a.sizeBytes)}</span>
        {!a.signed ? <StatusBadge tone="warn">{t('guide.s2.unsigned')}</StatusBadge>
          : a.selfSigned ? <StatusBadge tone="warn" title={a.signer ?? undefined}>{t('guide.s2.selfSigned')}</StatusBadge>
            : <StatusBadge tone="ok" title={a.signer ?? undefined}>{t('guide.s2.signed')}</StatusBadge>}
      </div>
      {!a.signed && <p className="text-xs text-muted-foreground">{t('guide.s2.unsignedHelp')}</p>}
      {a.signed && a.selfSigned && <p className="text-xs text-muted-foreground">{t('guide.s2.selfSignedHelp', { signer: a.signer ?? '' })}</p>}
      <div className="text-xs text-muted-foreground">SHA-256</div>
      <div className="flex items-center gap-2">
        <code className="secret pkg-hash flex-1 rounded-md border bg-muted px-2 py-1 font-mono text-xs break-all">{a.sha256}</code>
        <CopyButton text={a.sha256} />
      </div>
    </>
  );
}

export const TROUBLE_CONNECT = ['tcp', 'denied', 'scripts', 'rejected', 'pin', 'name', 'access', 'offline'] as const;
export const TROUBLE_UPDATE = ['upstart', 'upaccess', 'upold'] as const;
export const TROUBLE_OPS = ['module', 'signed', 'clm', 'outdated', 'off', 'iso', 'groups'] as const;
export const TROUBLE_CONSOLE = ['conoff', 'mouse', 'conclosed'] as const;

/** Symptom → cause/solution table; keys map to guide.trouble.<key>.s / .f strings. */
export function TroubleshootingTable({ keys, port }: { keys: readonly string[]; port: number | string }) {
  const { t } = useI18n();
  return (
    <div className="overflow-auto">
      <table className="w-full text-xs">
        <thead><tr className="text-left text-muted-foreground"><th className="px-3 py-2 font-medium">{t('guide.trouble.symptom')}</th><th className="px-3 py-2 font-medium">{t('guide.trouble.fix')}</th></tr></thead>
        <tbody>
          {keys.map((k) => (
            <tr key={k} className="border-t align-top">
              <td className="w-2/5 px-3 py-2 font-mono [overflow-wrap:anywhere]">{t(`guide.trouble.${k}.s` as TKey)}</td>
              <td className="px-3 py-2">{t(`guide.trouble.${k}.f` as TKey, { port })}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
