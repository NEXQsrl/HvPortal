import { useEffect, useState, type ReactNode } from 'react';
import { NavLink } from 'react-router-dom';
import { realtime } from '../api/realtime';
import { useI18n, type TKey } from '../i18n';
import { useSession } from '../session';

function useTheme() {
  const [theme, setTheme] = useState<string | null>(() => {
    try { return localStorage.getItem('hvp.theme'); } catch { return null; }
  });
  useEffect(() => {
    if (theme) document.documentElement.dataset.theme = theme;
    else delete document.documentElement.dataset.theme;
  }, [theme]);
  const toggle = () => {
    const dark = theme ? theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
    const next = dark ? 'light' : 'dark';
    setTheme(next);
    try { localStorage.setItem('hvp.theme', next); } catch { /* ignore */ }
  };
  return toggle;
}

function LiveIndicator() {
  const { t } = useI18n();
  const [, force] = useState(0);
  useEffect(() => realtime.onState(() => force((x) => x + 1)), []);
  const s = realtime.state;
  const cls = s === 'connected' ? 'ok' : s === 'connecting' ? 'warn' : '';
  return <span className={'badge ' + cls} role="status">{t(('live.' + s) as TKey)}</span>;
}

export function TopControls() {
  const { t, lang, setLang } = useI18n();
  const toggleTheme = useTheme();
  return (
    <>
      <button type="button" onClick={() => setLang(lang === 'it' ? 'en' : 'it')}>{t('lang.toggle')}</button>
      <button type="button" onClick={toggleTheme} aria-label={t('theme.toggle')} title={t('theme.toggle')}>◐</button>
    </>
  );
}

export function Layout({ children }: { children: ReactNode }) {
  const { t } = useI18n();
  const { me, logout, can } = useSession();
  const [open, setOpen] = useState(false);
  const link = (to: string, key: TKey, show = true, soon = false) =>
    show ? <NavLink to={to} className={soon ? 'soon' : undefined} onClick={() => setOpen(false)}>{t(key)}</NavLink> : null;

  return (
    <div className="shell">
      <nav className={'sidebar' + (open ? ' open' : '')} aria-label="Main">
        <div className="brand"><img src="/favicon.svg" alt="" />{t('app.name')}</div>
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
          {link('/account', 'nav.account')}
        </div>
      </nav>
      <div className="main">
        <header className="topbar">
          <button type="button" className="menu-toggle" onClick={() => setOpen(!open)} aria-expanded={open}>☰</button>
          <LiveIndicator />
          <span className="muted small">{me?.displayName || me?.userName}</span>
          <TopControls />
          <button type="button" onClick={() => void logout()}>{t('nav.logout')}</button>
        </header>
        <main className="content">{children}</main>
      </div>
    </div>
  );
}
