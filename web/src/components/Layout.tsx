import { useEffect, useRef, useState, type ReactNode } from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
import { realtime } from '../api/realtime';
import { useI18n, type TKey } from '../i18n';
import { useSession } from '../session';

type ThemeChoice = 'system' | 'light' | 'dark';

function readTheme(): ThemeChoice {
  try {
    const v = localStorage.getItem('hvp.theme');
    return v === 'light' || v === 'dark' ? v : 'system';
  } catch {
    return 'system';
  }
}

export function useTheme(): [ThemeChoice, (t: ThemeChoice) => void] {
  const [theme, setTheme] = useState<ThemeChoice>(readTheme);
  useEffect(() => {
    if (theme === 'system') delete document.documentElement.dataset.theme;
    else document.documentElement.dataset.theme = theme;
    try {
      if (theme === 'system') localStorage.removeItem('hvp.theme');
      else localStorage.setItem('hvp.theme', theme);
    } catch { /* storage unavailable */ }
  }, [theme]);
  return [theme, setTheme];
}

function LiveIndicator() {
  const { t } = useI18n();
  const [, force] = useState(0);
  useEffect(() => realtime.onState(() => force((x) => x + 1)), []);
  const s = realtime.state;
  const cls = s === 'connected' ? 'ok' : s === 'connecting' ? 'warn' : '';
  return <span className={'badge live ' + cls} role="status" title={t(('live.' + s) as TKey)}><span className="dot" />{t(('live.' + s) as TKey)}</span>;
}

/** Language and theme switches for the sign-in pages (no account menu there). */
export function TopControls() {
  const { t, lang, setLang } = useI18n();
  const [theme, setTheme] = useTheme();
  const dark = theme === 'dark' || (theme === 'system' && matchMedia('(prefers-color-scheme: dark)').matches);
  return (
    <>
      <button type="button" onClick={() => setLang(lang === 'it' ? 'en' : 'it')}>{t('lang.toggle')}</button>
      <button type="button" onClick={() => setTheme(dark ? 'light' : 'dark')} aria-label={t('theme.toggle')} title={t('theme.toggle')}>◐</button>
    </>
  );
}

function AccountMenu() {
  const { t, lang, setLang } = useI18n();
  const { me, logout } = useSession();
  const [theme, setTheme] = useTheme();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();
  const name = me?.displayName || me?.userName || '';
  const initials = name.split(/[\s._@-]+/).filter(Boolean).slice(0, 2).map((p) => p[0]!.toUpperCase()).join('') || '?';

  useEffect(() => {
    if (!open) return;
    const down = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    window.addEventListener('mousedown', down);
    window.addEventListener('keydown', key);
    return () => { window.removeEventListener('mousedown', down); window.removeEventListener('keydown', key); };
  }, [open]);

  return (
    <div className="account" ref={ref}>
      <button type="button" className="account-button" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(!open)}>
        <span className="avatar" aria-hidden="true">{initials}</span>
        <span className="account-name">{name}</span>
        <span aria-hidden="true">▾</span>
      </button>
      {open && (
        <div className="account-menu" role="menu">
          <div className="account-head">
            <strong>{name}</strong>
            <span className="muted small">{me?.userName}{me?.mfaEnabled ? ' · MFA' : ''}</span>
          </div>
          <button type="button" role="menuitem" onClick={() => { setOpen(false); navigate('/account'); }}>{t('nav.account')}</button>
          <div className="menu-sep" role="separator" />
          <div className="account-group" role="group" aria-label={t('account.language')}>
            <span className="muted small">{t('account.language')}</span>
            <div className="segmented">
              <button type="button" aria-pressed={lang === 'it'} onClick={() => setLang('it')}>Italiano</button>
              <button type="button" aria-pressed={lang === 'en'} onClick={() => setLang('en')}>English</button>
            </div>
          </div>
          <div className="account-group" role="group" aria-label={t('account.theme')}>
            <span className="muted small">{t('account.theme')}</span>
            <div className="segmented">
              {(['system', 'light', 'dark'] as const).map((v) => (
                <button key={v} type="button" aria-pressed={theme === v} onClick={() => setTheme(v)}>{t(('account.theme.' + v) as TKey)}</button>
              ))}
            </div>
          </div>
          <div className="menu-sep" role="separator" />
          <button type="button" role="menuitem" className="danger-text" onClick={() => { setOpen(false); void logout(); }}>{t('nav.logout')}</button>
        </div>
      )}
    </div>
  );
}

export function Layout({ children }: { children: ReactNode }) {
  const { t } = useI18n();
  const { can, state } = useSession();
  const [open, setOpen] = useState(false);
  const link = (to: string, key: TKey, show = true, soon = false) =>
    show ? <NavLink to={to} end={to === '/'} className={soon ? 'soon' : undefined} onClick={() => setOpen(false)}>{t(key)}</NavLink> : null;

  return (
    <div className="app">
      <header className="topbar">
        <button type="button" className="menu-toggle" onClick={() => setOpen(!open)} aria-expanded={open} aria-label="Menu">☰</button>
        <div className="brand"><img src="/favicon.svg" alt="" />{t('app.name')}</div>
        <span className="spacer" />
        <LiveIndicator />
        <AccountMenu />
      </header>
      <div className="shell">
        <nav className={'sidebar' + (open ? ' open' : '')} aria-label="Main">
          <div className="nav">
            {link('/', 'nav.dashboard')}
            {link('/vms', 'nav.vms')}
            {link('/hosts', 'nav.hosts')}
            {link('/jobs', 'nav.jobs')}
            {link('/guide', 'nav.guide', can('hosts.manage'))}
            <div className="nav-sep" />
            {link('/networks', 'nav.networks', true, true)}
            {link('/storage', 'nav.storage', true, true)}
            {link('/backup', 'nav.backup', true, true)}
            {link('/templates', 'nav.templates', true, true)}
            <div className="nav-sep" />
            {link('/users', 'nav.users', can('users.manage') || can('roles.manage'))}
            {link('/audit', 'nav.audit', can('audit.read'))}
            {link('/settings', 'nav.settings', can('settings.manage'))}
          </div>
          <footer className="sidebar-footer">
            <div>{t('app.name')} {state?.version ? `v${state.version}` : ''}</div>
            <div>© NEXQ S.r.l.</div>
            <a href="https://www.nexq.it" target="_blank" rel="noopener noreferrer">www.nexq.it</a>
          </footer>
        </nav>
        <main className="content">{children}</main>
      </div>
    </div>
  );
}
