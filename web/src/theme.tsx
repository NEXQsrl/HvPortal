import { useEffect, useState } from 'react';

export type ThemeChoice = 'system' | 'light' | 'dark';

export function readTheme(): ThemeChoice {
  try {
    const v = localStorage.getItem('hvp.theme');
    return v === 'light' || v === 'dark' ? v : 'system';
  } catch {
    return 'system';
  }
}

const systemDark = () => window.matchMedia('(prefers-color-scheme: dark)').matches;

export function applyTheme(choice: ThemeChoice) {
  const dark = choice === 'dark' || (choice === 'system' && systemDark());
  document.documentElement.classList.toggle('dark', dark);
  document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
}

const listeners = new Set<(t: ThemeChoice) => void>();
let current: ThemeChoice = readTheme();

export function setTheme(t: ThemeChoice) {
  current = t;
  try {
    if (t === 'system') localStorage.removeItem('hvp.theme');
    else localStorage.setItem('hvp.theme', t);
  } catch { /* storage unavailable */ }
  applyTheme(t);
  listeners.forEach((l) => l(t));
}

/** Shared theme state (account menu, sign-in pages). Follows the OS setting when "system". */
export function useTheme(): [ThemeChoice, (t: ThemeChoice) => void, boolean] {
  const [theme, setLocal] = useState<ThemeChoice>(current);
  const [dark, setDark] = useState(() => document.documentElement.classList.contains('dark'));
  useEffect(() => {
    const l = (t: ThemeChoice) => { setLocal(t); setDark(document.documentElement.classList.contains('dark')); };
    listeners.add(l);
    const m = window.matchMedia('(prefers-color-scheme: dark)');
    const onSystem = () => { if (current === 'system') { applyTheme('system'); setDark(m.matches); } };
    m.addEventListener('change', onSystem);
    return () => { listeners.delete(l); m.removeEventListener('change', onSystem); };
  }, []);
  return [theme, setTheme, dark];
}
