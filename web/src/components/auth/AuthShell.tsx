import { Activity, ArrowRight, HardDrive, Languages, LoaderCircle, Monitor, Moon, Network, Server, ShieldCheck, Sun, Waypoints } from 'lucide-react';
import type { ComponentProps, ReactNode } from 'react';
import { BrandLogo } from '@/components/common';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useI18n } from '@/i18n';
import { cn } from '@/lib/utils';
import { useSession } from '@/session';
import { useTheme } from '@/theme';

/*
 * Visual shell of the pages shown before the portal (sign-in, initial setup, account recovery, forced password change
 * and MFA enrollment). Styles and animations are the "sign-in page" section of index.css: CSS only (no inline styles,
 * the CSP allows style elements with a nonce only), transform/opacity animations, off with prefers-reduced-motion.
 */

/** Language and theme switches for the sign-in pages (no account menu there). */
export function TopControls() {
  const { t, lang, setLang } = useI18n();
  const [, setTheme, dark] = useTheme();
  return (
    <div className="signin-controls flex items-center gap-0.5 rounded-full border p-0.5">
      <Button type="button" variant="ghost" size="sm" className="rounded-full" onClick={() => setLang(lang === 'it' ? 'en' : 'it')}><Languages />{t('lang.toggle')}</Button>
      <Button type="button" variant="ghost" size="icon-sm" className="rounded-full" onClick={() => setTheme(dark ? 'light' : 'dark')} aria-label={t('theme.toggle')} title={t('theme.toggle')}>
        <span key={dark ? 'sun' : 'moon'} className="signin-swap inline-flex">{dark ? <Sun /> : <Moon />}</span>
      </Button>
    </div>
  );
}

/** Text input with a leading icon; every input attribute goes to the input itself. */
export function IconInput({ icon, className, ...props }: ComponentProps<typeof Input> & { icon: ReactNode }) {
  return (
    <div className="signin-input relative">
      <span className="signin-input-icon pointer-events-none absolute inset-y-0 left-3 flex items-center" aria-hidden="true">{icon}</span>
      <Input className={cn('pl-9', className)} {...props} />
    </div>
  );
}

/** Full-width submit button of the sign-in pages: spinner while `busy`, optional arrow. */
export function SubmitButton({ busy = false, arrow = false, className, children, ...props }: Omit<ComponentProps<typeof Button>, 'type'> & { busy?: boolean; arrow?: boolean }) {
  return (
    <Button type="submit" aria-busy={busy || undefined} className={cn('h-10 w-full', className)} {...props}>
      {busy && <LoaderCircle className="animate-spin" />}
      {children}
      {arrow && !busy && <ArrowRight className="signin-arrow" />}
    </Button>
  );
}

/** Slowly drifting colour fields and a faint grid behind the whole page (decorative). */
function Backdrop() {
  return (
    <div className="signin-backdrop" aria-hidden="true">
      <span className="signin-blob signin-blob-1" />
      <span className="signin-blob signin-blob-2" />
      <span className="signin-blob signin-blob-3" />
      <span className="signin-grid" />
    </div>
  );
}

/** Abstract picture of hosts, VMs, networks and disks around the portal (decorative: no data). */
function Orbit() {
  return (
    <div className="signin-orbit" aria-hidden="true">
      <span className="signin-ring signin-ring-1">
        <span className="signin-node signin-node-n"><span className="signin-chip"><Server /></span></span>
        <span className="signin-node signin-node-s"><span className="signin-chip"><HardDrive /></span></span>
        <span className="signin-packet signin-node-e" />
      </span>
      <span className="signin-ring signin-ring-2">
        <span className="signin-node signin-node-e"><span className="signin-chip"><Monitor /></span></span>
        <span className="signin-node signin-node-w"><span className="signin-chip"><Network /></span></span>
        <span className="signin-packet signin-node-n" />
      </span>
      <span className="signin-ring signin-ring-3"><span className="signin-packet signin-node-w" /></span>
      <span className="signin-ping" />
      <span className="signin-core"><img src="/brand/mark-white-180.png" alt="" draggable={false} /></span>
    </div>
  );
}

/** Brand panel of large screens: logo, animated picture, what the portal does. */
function Hero() {
  const { t } = useI18n();
  const features: [ReactNode, string][] = [
    [<ShieldCheck key="a" />, t('login2.feature.access')],
    [<Waypoints key="b" />, t('login2.feature.agent')],
    [<Activity key="c" />, t('login2.feature.audit')],
  ];
  return (
    <aside className="signin-hero relative m-3 hidden flex-col overflow-hidden rounded-[1.75rem] p-10 text-white lg:sticky lg:top-3 lg:order-first lg:flex lg:h-[calc(100dvh-1.5rem)] lg:self-start [@media(max-height:45rem)]:p-8"
      aria-label={t('login2.heroLabel')}>
      <span className="signin-hero-grid" aria-hidden="true" />
      <span className="signin-hero-glow signin-hero-glow-1" aria-hidden="true" />
      <span className="signin-hero-glow signin-hero-glow-2" aria-hidden="true" />
      <img src="/brand/logo-white.png" alt="HvPortal" className="relative h-8 w-fit shrink-0" draggable={false} />
      <div className="signin-hero-visual relative my-6 flex items-center justify-center"><Orbit /></div>
      <div className="signin-stagger relative max-w-lg shrink-0">
        <p className="signin-hero-title text-[2rem] leading-[1.15] font-semibold tracking-tight text-balance [@media(max-height:45rem)]:text-[1.65rem]">{t('login2.tagline')}</p>
        <p className="mt-3 text-[0.95rem] leading-relaxed text-pretty text-white/80">{t('login2.intro')}</p>
        <ul className="signin-stagger signin-hero-features mt-7 space-y-3 [@media(max-height:45rem)]:mt-5">
          {features.map(([icon, text]) => (
            <li key={text} className="flex items-center gap-3 text-sm text-white/90">
              <span className="signin-feature-icon flex size-9 shrink-0 items-center justify-center rounded-xl" aria-hidden="true">{icon}</span>
              {text}
            </li>
          ))}
        </ul>
      </div>
    </aside>
  );
}

/**
 * Page frame: animated backdrop, brand panel (large screens), language/theme switches, the glass card with the title
 * and the form, footer with the product version. The children are revealed one after the other (`signin-stagger`);
 * a child that is itself `signin-stagger` reveals its own children instead.
 */
export function AuthShell({ title, subtitle, icon, children }: { title: string; subtitle?: string; icon?: ReactNode; children: ReactNode }) {
  const { t } = useI18n();
  const { state } = useSession();
  return (
    <div className="signin auth-page relative h-dvh overflow-x-hidden overflow-y-auto bg-background">
      <Backdrop />
      <div className="relative grid min-h-full lg:grid-cols-2">
        <main className="flex min-w-0 flex-col">
          <div className="flex items-center justify-end px-4 py-3 sm:px-6"><TopControls /></div>
          <div className="flex flex-1 flex-col items-center px-4 pb-4">
            <div className="my-auto w-full max-w-[27rem] py-4">
              <div className="signin-enter mb-6 flex flex-col items-center gap-2 text-center lg:hidden">
                <BrandLogo className="h-9" />
                <p className="text-sm text-muted-foreground">{t('login2.tagline')}</p>
              </div>
              <div className="signin-card auth-card relative rounded-2xl border p-5 sm:p-8">
                <span className="signin-card-edge" aria-hidden="true" />
                <div className="mb-5 flex items-center gap-3.5">
                  {icon && <span className="signin-icon" aria-hidden="true">{icon}</span>}
                  <div className="min-w-0">
                    <h1 className="text-xl leading-7 font-semibold tracking-tight">{title}</h1>
                    {subtitle && <p className="text-sm text-muted-foreground">{subtitle}</p>}
                  </div>
                </div>
                <div className="signin-stagger">{children}</div>
              </div>
            </div>
          </div>
          <footer className="px-4 pb-4 text-center text-[11px] text-muted-foreground">
            © NEXQ S.r.l. · <a className="hover:underline" href="https://www.nexq.it" target="_blank" rel="noopener noreferrer">www.nexq.it</a>
            {state?.version && <> · <span className="tabular">{t('login2.version', { v: state.version })}</span></>}
          </footer>
        </main>
        <Hero />
      </div>
    </div>
  );
}
