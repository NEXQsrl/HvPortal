import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { ApiError, get, onUnauthenticated, post, resetCsrf } from './api/client';
import type { AuthState, Me } from './api/types';
import { realtime } from './api/realtime';

interface Session {
  state: AuthState | null;
  me: Me | null;
  loading: boolean;
  refresh: () => Promise<void>;
  logout: () => Promise<void>;
  can: (permission: string) => boolean;
}

const Ctx = createContext<Session | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState | null>(null);
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    resetCsrf();
    try {
      const s = await get<AuthState>('/api/v1/auth/state');
      setState(s);
      if (s.authenticated) {
        try {
          const m = await get<Me>('/api/v1/auth/me');
          setMe(m);
          if (!m.restriction) void realtime.start();
        } catch (e) {
          if (e instanceof ApiError && e.status === 401) setMe(null);
          else throw e;
        }
      } else {
        setMe(null);
        void realtime.stop();
      }
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
    onUnauthenticated(() => {
      setMe(null);
      void realtime.stop();
    });
  }, [refresh]);

  const logout = useCallback(async () => {
    try { await post('/api/v1/auth/logout'); } catch { /* already signed out */ }
    await realtime.stop();
    await refresh();
  }, [refresh]);

  // UI hint only: the server authorizes every request.
  const can = useCallback((p: string) => !!me?.globalPermissions.includes(p), [me]);

  return <Ctx.Provider value={{ state, me, loading, refresh, logout, can }}>{children}</Ctx.Provider>;
}

export function useSession(): Session {
  const v = useContext(Ctx);
  if (!v) throw new Error('SessionProvider missing');
  return v;
}
